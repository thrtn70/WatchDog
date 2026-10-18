using System.Security.Cryptography;
using System.Text;
using WatchDog.CaptureIsolation;

namespace WatchDog.CaptureIsolation.Windows;

/// <summary>
/// Unregistered owner of exact, existing, fixed-local media-root handle chains. Each component
/// is opened without delete sharing and without following its final reparse point. This emits a
/// neutral namespace snapshot only: it does not authorize later path-based I/O because Windows
/// permits reparse mutation while directory handles are retained. A future admitted consumer must
/// operate handle-relative to the retained final directory and revalidate every component.
/// </summary>
public sealed class WindowsMediaRootBoundary : IHandleRelativeMediaRootBoundary
{
    private readonly object _sync = new();
    private readonly IWindowsMediaRootApi _api;
    private readonly IWindowsRelativeFileApi _relativeFileApi;
    private readonly Dictionary<Guid, MediaRootLease> _leases = [];
    private readonly List<SafeWindowsHandle> _pendingClose = [];
    private readonly List<SafeWindowsHandle> _pendingFileCleanup = [];
    private bool _disposed;

    public WindowsMediaRootBoundary()
        : this(WindowsMediaRootApi.Instance, WindowsRelativeFileApi.Instance)
    {
    }

    internal WindowsMediaRootBoundary(IWindowsMediaRootApi api)
        : this(api, WindowsRelativeFileApi.Instance)
    {
    }

    internal WindowsMediaRootBoundary(
        IWindowsMediaRootApi api,
        IWindowsRelativeFileApi relativeFileApi)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _relativeFileApi = relativeFileApi ?? throw new ArgumentNullException(nameof(relativeFileApi));
    }

    public ValueTask<LocalMediaRootObservation> AdmitAsync(
        string candidatePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryClosePending())
                return ValueTask.FromResult(Rejected(LocalMediaRootAdmissionStatus.ClosePending));

            if (!TryNormalizeCandidate(candidatePath, out var canonicalPath, out var rejection))
                return ValueTask.FromResult(rejection!);

            var rootPath = Path.GetPathRoot(canonicalPath!);
            if (string.IsNullOrEmpty(rootPath))
                return ValueTask.FromResult(Rejected(LocalMediaRootAdmissionStatus.InvalidPath));

            var driveType = _api.GetDriveType(rootPath);
            if (driveType == WindowsMediaRootDriveType.Remote)
                return ValueTask.FromResult(Rejected(LocalMediaRootAdmissionStatus.NetworkRoot));
            if (driveType != WindowsMediaRootDriveType.Fixed)
                return ValueTask.FromResult(Rejected(LocalMediaRootAdmissionStatus.NonFixedRoot));
            if (PathEquals(canonicalPath!, rootPath))
                return ValueTask.FromResult(Rejected(LocalMediaRootAdmissionStatus.VolumeRoot));

            var components = new List<MediaRootComponent>();
            try
            {
                foreach (var componentPath in EnumerateComponents(canonicalPath!, rootPath))
                {
                    var handle = _api.OpenDirectory(componentPath);
                    WindowsMediaRootHandleSnapshot snapshot;
                    try
                    {
                        snapshot = _api.Observe(handle);
                    }
                    catch
                    {
                        if (!handle.CloseOnce())
                            _pendingClose.Add(handle);
                        throw;
                    }

                    if ((snapshot.FileAttributes & WindowsMediaRootApi.FileAttributeDirectory) == 0)
                    {
                        components.Add(CreateComponent(handle, snapshot));
                        return ValueTask.FromResult(RejectAndClose(
                            components,
                            LocalMediaRootAdmissionStatus.NotDirectory));
                    }
                    if ((snapshot.FileAttributes & WindowsMediaRootApi.FileAttributeReparsePoint) != 0 ||
                        snapshot.ReparseTag != 0)
                    {
                        components.Add(CreateComponent(handle, snapshot));
                        return ValueTask.FromResult(RejectAndClose(
                            components,
                            LocalMediaRootAdmissionStatus.ReparsePoint));
                    }
                    if (!TryNormalizeFinalPath(snapshot.FinalPath, out var finalPath) ||
                        !PathEquals(componentPath, finalPath!))
                    {
                        components.Add(CreateComponent(handle, snapshot));
                        return ValueTask.FromResult(RejectAndClose(
                            components,
                            LocalMediaRootAdmissionStatus.FinalPathMismatch));
                    }

                    components.Add(new(
                        handle,
                        snapshot.VolumeSerialNumber,
                        snapshot.FileIdLow,
                        snapshot.FileIdHigh,
                        PathDigest(finalPath!)));
                }
            }
            catch (WindowsMediaRootNativeException exception)
            {
                var status = StatusForFailure(exception.FailureCode);
                return ValueTask.FromResult(RejectAndClose(components, status));
            }

            var identity = NewIdentity();
            var finalDigest = components[^1].FinalPathDigest;
            _leases.Add(identity.OpaqueHandleIdentity, new(identity, components, finalDigest));
            return ValueTask.FromResult(new LocalMediaRootObservation(
                LocalMediaRootAdmissionStatus.Confirmed,
                identity,
                finalDigest,
                null));
        }
    }

    public ValueTask<LocalMediaRootObservation> ObserveAsync(
        RetainedMediaRootIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            var lease = FindLease(identity, requireActive: true);
            var rejection = ObserveLease(lease);
            if (rejection is not null)
            {
                lease.State = MediaLeaseState.Closing;
                return ValueTask.FromResult(Rejected(rejection.Value));
            }

            return ValueTask.FromResult(new LocalMediaRootObservation(
                LocalMediaRootAdmissionStatus.Confirmed,
                lease.Identity,
                lease.FinalPathDigest,
                null));
        }
    }

    public ValueTask<HandleRelativeMediaFileObservation> CreateNewFileAsync(
        RetainedMediaRootIdentity rootIdentity,
        string singleComponentName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rootIdentity);
        if (!IsSafeSingleComponentName(singleComponentName))
        {
            return ValueTask.FromResult(RelativeRejected(
                HandleRelativeMediaFileStatus.InvalidName));
        }
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryClosePending())
            {
                return ValueTask.FromResult(RelativeRejected(
                    HandleRelativeMediaFileStatus.ClosePending));
            }

            var lease = FindLease(rootIdentity, requireActive: true);
            var rootRejection = ObserveLease(lease);
            if (rootRejection is not null)
            {
                lease.State = MediaLeaseState.Closing;
                return ValueTask.FromResult(RelativeRejected(MapRelativeStatus(rootRejection.Value)));
            }

            SafeWindowsHandle file;
            try
            {
                file = _relativeFileApi.CreateNew(
                    lease.Components[^1].Handle,
                    singleComponentName);
            }
            catch (WindowsRelativeFileNativeException exception)
            {
                var status = StatusForRelativeFailure(exception.FailureCode);
                if (status is HandleRelativeMediaFileStatus.RootStale or
                    HandleRelativeMediaFileStatus.ReparsePoint)
                {
                    lease.State = MediaLeaseState.Closing;
                }
                return ValueTask.FromResult(RelativeRejected(status));
            }

            try
            {
                var snapshot = _relativeFileApi.Observe(file);
                var rootComponent = lease.Components[^1];
                if ((snapshot.FileAttributes & WindowsMediaRootApi.FileAttributeDirectory) != 0 ||
                    (snapshot.FileAttributes & WindowsMediaRootApi.FileAttributeReparsePoint) != 0 ||
                    snapshot.ReparseTag != 0)
                {
                    return ValueTask.FromResult(CleanupCreatedFile(
                        file,
                        HandleRelativeMediaFileStatus.ReparsePoint));
                }
                if (snapshot.VolumeSerialNumber != rootComponent.VolumeSerialNumber)
                {
                    lease.State = MediaLeaseState.Closing;
                    return ValueTask.FromResult(CleanupCreatedFile(
                        file,
                        HandleRelativeMediaFileStatus.RootStale));
                }
                var postCreateRootRejection = ObserveLease(lease);
                if (postCreateRootRejection is not null)
                {
                    lease.State = MediaLeaseState.Closing;
                    return ValueTask.FromResult(CleanupCreatedFile(
                        file,
                        MapRelativeStatus(postCreateRootRejection.Value)));
                }
                if (cancellationToken.IsCancellationRequested)
                {
                    return ValueTask.FromResult(CleanupCreatedFile(
                        file,
                        HandleRelativeMediaFileStatus.Cancelled));
                }

                var identity = NewFileIdentity(rootIdentity, lease);
                lease.Files.Add(identity.OpaqueFileIdentity, new(
                    identity,
                    file,
                    snapshot.VolumeSerialNumber,
                    snapshot.FileIdLow,
                    snapshot.FileIdHigh));
                return ValueTask.FromResult(new HandleRelativeMediaFileObservation(
                    HandleRelativeMediaFileStatus.Created,
                    identity,
                    null));
            }
            catch (WindowsRelativeFileNativeException)
            {
                return ValueTask.FromResult(CleanupCreatedFile(
                    file,
                    HandleRelativeMediaFileStatus.Unavailable));
            }
        }
    }

    public ValueTask WriteFileAsync(
        RetainedMediaFileIdentity fileIdentity,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileIdentity);
        if (content.Length > IHandleRelativeMediaRootBoundary.MaximumTransferBytes)
            throw new ArgumentOutOfRangeException(nameof(content));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            var (root, file) = FindActiveFile(fileIdentity);
            RequireOperational(root, file);
            try
            {
                _relativeFileApi.Write(file.Handle, content.Span);
            }
            catch (WindowsRelativeFileNativeException exception)
            {
                throw new LocalMediaRootBoundaryException(exception.FailureCode);
            }
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask<byte[]> ReadFileAsync(
        RetainedMediaFileIdentity fileIdentity,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileIdentity);
        if (maximumBytes <= 0 ||
            maximumBytes > IHandleRelativeMediaRootBoundary.MaximumTransferBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            var (root, file) = FindActiveFile(fileIdentity);
            RequireOperational(root, file);
            try
            {
                return ValueTask.FromResult(_relativeFileApi.ReadAll(file.Handle, maximumBytes));
            }
            catch (WindowsRelativeFileNativeException exception)
            {
                throw new LocalMediaRootBoundaryException(exception.FailureCode);
            }
        }
    }

    public ValueTask FlushFileAsync(
        RetainedMediaFileIdentity fileIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileIdentity);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            var (root, file) = FindActiveFile(fileIdentity);
            RequireOperational(root, file);
            try
            {
                _relativeFileApi.Flush(file.Handle);
            }
            catch (WindowsRelativeFileNativeException exception)
            {
                throw new LocalMediaRootBoundaryException(exception.FailureCode);
            }
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask ReleaseFileAsync(
        RetainedMediaFileIdentity fileIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileIdentity);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            var root = FindLease(fileIdentity.RootIdentity, requireActive: false);
            var file = FindFileLease(root, fileIdentity);
            file.State = MediaLeaseState.Closing;
            if (!file.Handle.CloseOnce())
                throw new LocalMediaRootBoundaryException("media-file.close-failed");
            root.Files.Remove(fileIdentity.OpaqueFileIdentity);
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask ReleaseAsync(
        RetainedMediaRootIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryClosePending())
                throw new LocalMediaRootBoundaryException("media-root.close-pending");
            var lease = FindLease(identity, requireActive: false);
            lease.State = MediaLeaseState.Closing;
            if (lease.Files.Count != 0)
                throw new LocalMediaRootBoundaryException("media-root.files-open");
            if (!CloseComponents(lease.Components))
                throw new LocalMediaRootBoundaryException("media-root.close-failed");
            _leases.Remove(identity.OpaqueHandleIdentity);
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed)
                return ValueTask.CompletedTask;

            var closeFailed = !TryClosePending();
            foreach (var lease in _leases.Values.ToArray())
            {
                lease.State = MediaLeaseState.Closing;
                foreach (var file in lease.Files.Values.ToArray())
                {
                    file.State = MediaLeaseState.Closing;
                    if (file.Handle.CloseOnce())
                        lease.Files.Remove(file.Identity.OpaqueFileIdentity);
                    else
                        closeFailed = true;
                }
                if (lease.Files.Count == 0 && CloseComponents(lease.Components))
                    _leases.Remove(lease.Identity.OpaqueHandleIdentity);
                else
                    closeFailed = true;
            }
            if (!TryClosePending())
                closeFailed = true;
            if (closeFailed)
                throw new LocalMediaRootBoundaryException("media-root.close-failed");

            _disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private LocalMediaRootObservation RejectAndClose(
        List<MediaRootComponent> components,
        LocalMediaRootAdmissionStatus status)
    {
        if (!CloseComponents(components))
        {
            _pendingClose.AddRange(components.Select(static component => component.Handle));
            components.Clear();
        }
        if (!TryClosePending())
            return Rejected(LocalMediaRootAdmissionStatus.ClosePending);
        return Rejected(status);
    }

    private bool TryClosePending()
    {
        var allClosed = true;
        for (var index = _pendingFileCleanup.Count - 1; index >= 0; index--)
        {
            var handle = _pendingFileCleanup[index];
            if (_relativeFileApi.MarkDeleteOnClose(handle) && handle.CloseOnce())
                _pendingFileCleanup.RemoveAt(index);
            else
                allClosed = false;
        }
        for (var index = _pendingClose.Count - 1; index >= 0; index--)
        {
            if (_pendingClose[index].CloseOnce())
                _pendingClose.RemoveAt(index);
            else
                allClosed = false;
        }
        return allClosed;
    }

    private HandleRelativeMediaFileObservation CleanupCreatedFile(
        SafeWindowsHandle handle,
        HandleRelativeMediaFileStatus status)
    {
        if (!_relativeFileApi.MarkDeleteOnClose(handle) || !handle.CloseOnce())
        {
            _pendingFileCleanup.Add(handle);
            return RelativeRejected(HandleRelativeMediaFileStatus.ClosePending);
        }
        return RelativeRejected(status);
    }

    private bool CloseComponents(List<MediaRootComponent> components)
    {
        var allClosed = true;
        for (var index = components.Count - 1; index >= 0; index--)
        {
            if (components[index].Handle.CloseOnce())
                components.RemoveAt(index);
            else
                allClosed = false;
        }
        return allClosed;
    }

    private MediaRootLease FindLease(
        RetainedMediaRootIdentity identity,
        bool requireActive)
    {
        if (identity.OpaqueHandleIdentity == Guid.Empty ||
            !_leases.TryGetValue(identity.OpaqueHandleIdentity, out var lease) ||
            lease.Identity != identity)
        {
            throw new LocalMediaRootBoundaryException("media-root.identity-mismatch");
        }
        if (requireActive && lease.State != MediaLeaseState.Active)
            throw new LocalMediaRootBoundaryException("media-root.closing");
        return lease;
    }

    private MediaFileLease FindFileLease(
        MediaRootLease root,
        RetainedMediaFileIdentity identity)
    {
        if (identity.OpaqueFileIdentity == Guid.Empty ||
            identity.RootIdentity.OpaqueHandleIdentity == Guid.Empty ||
            identity.RootIdentity != root.Identity ||
            !root.Files.TryGetValue(identity.OpaqueFileIdentity, out var file) ||
            file.Identity != identity)
        {
            throw new LocalMediaRootBoundaryException("media-file.identity-mismatch");
        }
        return file;
    }

    private (MediaRootLease Root, MediaFileLease File) FindActiveFile(
        RetainedMediaFileIdentity identity)
    {
        var root = FindLease(identity.RootIdentity, requireActive: true);
        var file = FindFileLease(root, identity);
        if (file.State != MediaLeaseState.Active)
            throw new LocalMediaRootBoundaryException("media-file.closing");
        return (root, file);
    }

    private void RequireOperational(MediaRootLease root, MediaFileLease file)
    {
        if (!TryClosePending())
            throw new LocalMediaRootBoundaryException("media-root.close-pending");
        var rootRejection = ObserveLease(root);
        if (rootRejection is not null)
        {
            root.State = MediaLeaseState.Closing;
            throw new LocalMediaRootBoundaryException(FailureCode(rootRejection.Value));
        }
        WindowsRelativeFileSnapshot snapshot;
        try
        {
            snapshot = _relativeFileApi.Observe(file.Handle);
        }
        catch (WindowsRelativeFileNativeException exception)
        {
            throw new LocalMediaRootBoundaryException(exception.FailureCode);
        }
        if ((snapshot.FileAttributes & WindowsMediaRootApi.FileAttributeDirectory) != 0 ||
            (snapshot.FileAttributes & WindowsMediaRootApi.FileAttributeReparsePoint) != 0 ||
            snapshot.ReparseTag != 0)
        {
            throw new LocalMediaRootBoundaryException("media-file.reparse-point");
        }
        if (snapshot.VolumeSerialNumber != file.VolumeSerialNumber ||
            snapshot.FileIdLow != file.FileIdLow ||
            snapshot.FileIdHigh != file.FileIdHigh)
        {
            throw new LocalMediaRootBoundaryException("media-file.identity-mismatch");
        }
    }

    private LocalMediaRootAdmissionStatus? ObserveLease(MediaRootLease lease)
    {
        foreach (var component in lease.Components)
        {
            WindowsMediaRootHandleSnapshot snapshot;
            try
            {
                snapshot = _api.Observe(component.Handle);
            }
            catch (WindowsMediaRootNativeException exception)
            {
                return StatusForFailure(exception.FailureCode);
            }

            if ((snapshot.FileAttributes & WindowsMediaRootApi.FileAttributeDirectory) == 0)
                return LocalMediaRootAdmissionStatus.NotDirectory;
            if ((snapshot.FileAttributes & WindowsMediaRootApi.FileAttributeReparsePoint) != 0 ||
                snapshot.ReparseTag != 0)
            {
                return LocalMediaRootAdmissionStatus.ReparsePoint;
            }
            if (snapshot.VolumeSerialNumber != component.VolumeSerialNumber ||
                snapshot.FileIdLow != component.FileIdLow ||
                snapshot.FileIdHigh != component.FileIdHigh)
            {
                return LocalMediaRootAdmissionStatus.Stale;
            }
            if (!TryNormalizeFinalPath(snapshot.FinalPath, out var finalPath) ||
                !CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(PathDigest(finalPath!)),
                    Convert.FromHexString(component.FinalPathDigest)))
            {
                return LocalMediaRootAdmissionStatus.FinalPathMismatch;
            }
        }
        return null;
    }

    private RetainedMediaRootIdentity NewIdentity()
    {
        Guid value;
        do
        {
            value = Guid.NewGuid();
        }
        while (value == Guid.Empty || _leases.ContainsKey(value));
        return new(value);
    }

    private static RetainedMediaFileIdentity NewFileIdentity(
        RetainedMediaRootIdentity rootIdentity,
        MediaRootLease lease)
    {
        Guid value;
        do
        {
            value = Guid.NewGuid();
        }
        while (value == Guid.Empty || lease.Files.ContainsKey(value));
        return new(value, rootIdentity);
    }

    private static bool TryNormalizeCandidate(
        string? candidatePath,
        out string? canonicalPath,
        out LocalMediaRootObservation? rejection)
    {
        canonicalPath = null;
        rejection = null;
        if (string.IsNullOrWhiteSpace(candidatePath) || candidatePath.IndexOf('\0') >= 0)
        {
            rejection = Rejected(LocalMediaRootAdmissionStatus.InvalidPath);
            return false;
        }
        if (candidatePath.StartsWith(@"\\", StringComparison.Ordinal) ||
            candidatePath.StartsWith("//", StringComparison.Ordinal) ||
            candidatePath.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase))
        {
            rejection = Rejected(LocalMediaRootAdmissionStatus.UnsupportedNamespace);
            return false;
        }
        if (candidatePath.Length > 2 && candidatePath.AsSpan(2).Contains(':'))
        {
            rejection = Rejected(LocalMediaRootAdmissionStatus.AlternateDataStream);
            return false;
        }

        try
        {
            if (!Path.IsPathFullyQualified(candidatePath))
            {
                rejection = Rejected(LocalMediaRootAdmissionStatus.InvalidPath);
                return false;
            }
            canonicalPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
            var root = Path.GetPathRoot(canonicalPath);
            if (string.IsNullOrEmpty(root) || root.Length < 3 || root[1] != ':')
            {
                canonicalPath = null;
                rejection = Rejected(LocalMediaRootAdmissionStatus.UnsupportedNamespace);
                return false;
            }
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            canonicalPath = null;
            rejection = Rejected(LocalMediaRootAdmissionStatus.InvalidPath);
            return false;
        }
    }

    private static bool IsSafeSingleComponentName(string? value)
    {
        if (value is not { Length: > 0 and <= 128 } ||
            value is "." or ".." || value.EndsWith('.') || value.EndsWith(' ') ||
            !value.All(static character =>
                char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_'))
        {
            return false;
        }
        var stem = value.Split('.', 2)[0];
        return !ReservedDosNames.Contains(stem);
    }

    private static bool TryNormalizeFinalPath(string finalPath, out string? canonicalPath)
    {
        canonicalPath = null;
        if (string.IsNullOrWhiteSpace(finalPath))
            return false;
        var path = finalPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            ? @"\\" + finalPath[8..]
            : finalPath.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
                ? finalPath[4..]
                : finalPath;
        if (path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        try
        {
            canonicalPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return Path.IsPathFullyQualified(canonicalPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static IEnumerable<string> EnumerateComponents(string canonicalPath, string rootPath)
    {
        yield return rootPath;
        var current = rootPath;
        foreach (var part in canonicalPath[rootPath.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            yield return current;
        }
    }

    private static MediaRootComponent CreateComponent(
        SafeWindowsHandle handle,
        WindowsMediaRootHandleSnapshot snapshot) =>
        new(
            handle,
            snapshot.VolumeSerialNumber,
            snapshot.FileIdLow,
            snapshot.FileIdHigh,
            TryNormalizeFinalPath(snapshot.FinalPath, out var path) ? PathDigest(path!) : EmptyDigest);

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            StringComparison.OrdinalIgnoreCase);

    private static string PathDigest(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(path).ToUpperInvariant())));

    private static LocalMediaRootAdmissionStatus StatusForFailure(string failureCode) =>
        failureCode switch
        {
            "media-root.not-found" => LocalMediaRootAdmissionStatus.NotFound,
            "media-root.not-directory" => LocalMediaRootAdmissionStatus.NotDirectory,
            "media-root.access-denied" => LocalMediaRootAdmissionStatus.AccessDenied,
            "media-root.identity-unavailable" or "media-root.final-path-unavailable" =>
                LocalMediaRootAdmissionStatus.IdentityUnavailable,
            _ => LocalMediaRootAdmissionStatus.Unavailable,
        };

    private static HandleRelativeMediaFileStatus StatusForRelativeFailure(string failureCode) =>
        failureCode switch
        {
            "media-file.name-invalid" => HandleRelativeMediaFileStatus.InvalidName,
            "media-file.already-exists" => HandleRelativeMediaFileStatus.AlreadyExists,
            "media-file.reparse-point" => HandleRelativeMediaFileStatus.ReparsePoint,
            "media-file.access-denied" => HandleRelativeMediaFileStatus.AccessDenied,
            "media-file.root-stale" => HandleRelativeMediaFileStatus.RootStale,
            _ => HandleRelativeMediaFileStatus.Unavailable,
        };

    private static HandleRelativeMediaFileStatus MapRelativeStatus(
        LocalMediaRootAdmissionStatus status) =>
        status switch
        {
            LocalMediaRootAdmissionStatus.ReparsePoint =>
                HandleRelativeMediaFileStatus.ReparsePoint,
            LocalMediaRootAdmissionStatus.Stale or
                LocalMediaRootAdmissionStatus.FinalPathMismatch or
                LocalMediaRootAdmissionStatus.NotDirectory =>
                HandleRelativeMediaFileStatus.RootStale,
            LocalMediaRootAdmissionStatus.AccessDenied =>
                HandleRelativeMediaFileStatus.AccessDenied,
            LocalMediaRootAdmissionStatus.ClosePending =>
                HandleRelativeMediaFileStatus.ClosePending,
            _ => HandleRelativeMediaFileStatus.Unavailable,
        };

    private static LocalMediaRootObservation Rejected(LocalMediaRootAdmissionStatus status) =>
        new(status, null, null, FailureCode(status));

    private static HandleRelativeMediaFileObservation RelativeRejected(
        HandleRelativeMediaFileStatus status) =>
        new(status, null, RelativeFailureCode(status));

    private static string FailureCode(LocalMediaRootAdmissionStatus status) =>
        status switch
        {
            LocalMediaRootAdmissionStatus.InvalidPath => "media-root.invalid-path",
            LocalMediaRootAdmissionStatus.UnsupportedNamespace => "media-root.unsupported-namespace",
            LocalMediaRootAdmissionStatus.AlternateDataStream => "media-root.alternate-data-stream",
            LocalMediaRootAdmissionStatus.NetworkRoot => "media-root.network-root",
            LocalMediaRootAdmissionStatus.NonFixedRoot => "media-root.non-fixed-root",
            LocalMediaRootAdmissionStatus.VolumeRoot => "media-root.volume-root",
            LocalMediaRootAdmissionStatus.NotFound => "media-root.not-found",
            LocalMediaRootAdmissionStatus.NotDirectory => "media-root.not-directory",
            LocalMediaRootAdmissionStatus.AccessDenied => "media-root.access-denied",
            LocalMediaRootAdmissionStatus.ReparsePoint => "media-root.reparse-point",
            LocalMediaRootAdmissionStatus.IdentityUnavailable => "media-root.identity-unavailable",
            LocalMediaRootAdmissionStatus.FinalPathMismatch => "media-root.final-path-mismatch",
            LocalMediaRootAdmissionStatus.Stale => "media-root.stale",
            LocalMediaRootAdmissionStatus.ClosePending => "media-root.close-pending",
            _ => "media-root.unavailable",
        };

    private static string RelativeFailureCode(HandleRelativeMediaFileStatus status) =>
        status switch
        {
            HandleRelativeMediaFileStatus.InvalidName => "media-file.name-invalid",
            HandleRelativeMediaFileStatus.AlreadyExists => "media-file.already-exists",
            HandleRelativeMediaFileStatus.RootStale => "media-file.root-stale",
            HandleRelativeMediaFileStatus.ReparsePoint => "media-file.reparse-point",
            HandleRelativeMediaFileStatus.AccessDenied => "media-file.access-denied",
            HandleRelativeMediaFileStatus.Cancelled => "media-file.cancelled",
            HandleRelativeMediaFileStatus.ClosePending => "media-file.close-pending",
            _ => "media-file.unavailable",
        };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static readonly string EmptyDigest = new('0', 64);
    private static readonly HashSet<string> ReservedDosNames = new(
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        ],
        StringComparer.OrdinalIgnoreCase);

    private sealed record MediaRootComponent(
        SafeWindowsHandle Handle,
        ulong VolumeSerialNumber,
        ulong FileIdLow,
        ulong FileIdHigh,
        string FinalPathDigest);

    private sealed class MediaRootLease(
        RetainedMediaRootIdentity identity,
        List<MediaRootComponent> components,
        string finalPathDigest)
    {
        internal RetainedMediaRootIdentity Identity { get; } = identity;
        internal List<MediaRootComponent> Components { get; } = components;
        internal string FinalPathDigest { get; } = finalPathDigest;
        internal Dictionary<Guid, MediaFileLease> Files { get; } = [];
        internal MediaLeaseState State { get; set; } = MediaLeaseState.Active;
    }

    private sealed class MediaFileLease(
        RetainedMediaFileIdentity identity,
        SafeWindowsHandle handle,
        ulong volumeSerialNumber,
        ulong fileIdLow,
        ulong fileIdHigh)
    {
        internal RetainedMediaFileIdentity Identity { get; } = identity;
        internal SafeWindowsHandle Handle { get; } = handle;
        internal ulong VolumeSerialNumber { get; } = volumeSerialNumber;
        internal ulong FileIdLow { get; } = fileIdLow;
        internal ulong FileIdHigh { get; } = fileIdHigh;
        internal MediaLeaseState State { get; set; } = MediaLeaseState.Active;
    }

    private enum MediaLeaseState
    {
        Active,
        Closing,
    }
}
