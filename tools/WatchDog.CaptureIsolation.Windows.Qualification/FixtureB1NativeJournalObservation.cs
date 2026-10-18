namespace WatchDog.CaptureIsolation.Windows.Qualification;

[Flags]
internal enum FixtureB1NativeJournalHandleAccess : uint
{
    None = 0,
    ReadDataOrListDirectory = 0x00000001,
    WriteDataOrAddFile = 0x00000002,
    AppendDataOrAddSubdirectory = 0x00000004,
    ReadExtendedAttributes = 0x00000008,
    WriteExtendedAttributes = 0x00000010,
    ExecuteOrTraverse = 0x00000020,
    DeleteChild = 0x00000040,
    ReadAttributes = 0x00000080,
    WriteAttributes = 0x00000100,
    Delete = 0x00010000,
    ReadControl = 0x00020000,
    WriteDac = 0x00040000,
    WriteOwner = 0x00080000,
    Synchronize = 0x00100000,
}

internal enum FixtureB1NativeJournalObjectKind
{
    Directory,
    File,
}

internal enum FixtureB1NativeJournalAceKind
{
    Allow,
    Deny,
}

internal enum FixtureB1NativeJournalHandleRole
{
    RootChain,
    WriterOriginal,
    ReadObservation,
}

internal enum FixtureB1NativeJournalLeaseState
{
    Acquiring,
    WritableIdle,
    Poisoned,
    Closing,
    ClosePending,
    Closed,
}

internal enum FixtureB1NativeJournalCloseDisposition
{
    Closed,
    Uncertain,
}

internal enum FixtureB1NativeJournalFaultPoint
{
    None,
    BeforeOpenVolumeRoot,
    AfterOpenVolumeRoot,
    BeforeOpenAncestor,
    AfterOpenAncestor,
    BeforeCreatePrivateRoot,
    AfterCreatePrivateRoot,
    BeforeCreateFrames,
    AfterCreateFrames,
    BeforeCreateAnchor,
    AfterCreateAnchor,
    BeforeDuplicateFrames,
    AfterDuplicateFrames,
    BeforeDuplicateAnchor,
    AfterDuplicateAnchor,
    BeforeInitialObservation,
    BeforeInitialEnumeration,
    BeforeLeasePublication,
}

internal sealed record FixtureB1NativeJournalAceObservation(
    FixtureB1NativeJournalAceKind Kind,
    string Sid,
    uint AccessMask,
    byte Flags);

internal sealed record FixtureB1NativeJournalSecurityObservation(
    string OwnerSid,
    bool DaclProtected,
    IReadOnlyList<FixtureB1NativeJournalAceObservation> Dacl);

internal sealed record FixtureB1NativeJournalHandleObservation(
    FixtureB1NativeJournalObjectKind Kind,
    uint FileAttributes,
    uint ReparseTag,
    ulong VolumeSerialNumber,
    ulong FileIdLow,
    ulong FileIdHigh,
    uint NumberOfLinks,
    bool DeletePending,
    long EndOfFile,
    long AllocationSize,
    uint GrantedAccessMask,
    string FinalPath,
    FixtureB1NativeJournalSecurityObservation Security,
    IReadOnlyList<string> StreamNames);

internal sealed record FixtureB1NativeJournalSecurityPolicy(
    byte[] SelfRelativeSecurityDescriptor,
    string ExpectedOwnerSid,
    bool RequireProtectedDacl,
    IReadOnlyList<FixtureB1NativeJournalAceObservation> ExpectedDacl);

internal sealed record FixtureB1NativeJournalAccessPolicy(
    uint RequiredDirectoryAccess,
    uint RequiredWriterAccess,
    uint RequiredReadAccess,
    uint ForbiddenReadAccess);

internal sealed record FixtureB1NativeJournalAcquisitionOptions(
    string CanonicalDriveRoot,
    IReadOnlyList<string> AncestorComponents,
    string PrivateRootName,
    string FramesName,
    string AnchorName,
    long MaximumFramesBytes,
    long MaximumAnchorBytes,
    FixtureB1NativeJournalSecurityPolicy DirectorySecurity,
    FixtureB1NativeJournalSecurityPolicy FileSecurity,
    FixtureB1NativeJournalAccessPolicy Access);

internal interface IFixtureB1NativeJournalHandle
{
    Guid LocalHandleId { get; }
}

internal sealed record FixtureB1NativeJournalHandleResult(
    IFixtureB1NativeJournalHandle? Handle,
    string? FailureCode)
{
    internal bool Succeeded => Handle is not null && FailureCode is null;
}

internal sealed record FixtureB1NativeJournalObservationResult(
    FixtureB1NativeJournalHandleObservation? Observation,
    string? FailureCode)
{
    internal bool Succeeded => Observation is not null && FailureCode is null;
}

internal sealed record FixtureB1NativeJournalDirectoryEntriesResult(
    IReadOnlyList<string>? EntryNames,
    string? FailureCode)
{
    internal bool Succeeded => EntryNames is not null && FailureCode is null;
}

internal sealed record FixtureB1NativeJournalBytesResult(byte[]? Bytes, string? FailureCode)
{
    internal bool Succeeded => Bytes is not null && FailureCode is null;
}

internal sealed record FixtureB1NativeJournalCloseResult(
    FixtureB1NativeJournalCloseDisposition Disposition,
    string? FailureCode);

internal interface IFixtureB1NativeJournalObservationApi
{
    FixtureB1NativeJournalHandleResult OpenVolumeRoot(string canonicalDriveRoot);
    FixtureB1NativeJournalHandleResult OpenDirectoryRelative(
        IFixtureB1NativeJournalHandle parent,
        string singleComponentName);
    FixtureB1NativeJournalHandleResult CreatePrivateDirectoryRelative(
        IFixtureB1NativeJournalHandle trustedParent,
        string singleComponentName,
        byte[] selfRelativeSecurityDescriptor);
    FixtureB1NativeJournalHandleResult CreateFileRelative(
        IFixtureB1NativeJournalHandle root,
        string singleComponentName,
        byte[] selfRelativeSecurityDescriptor);
    FixtureB1NativeJournalHandleResult DuplicateReadOnly(
        IFixtureB1NativeJournalHandle source);
    FixtureB1NativeJournalObservationResult Observe(
        IFixtureB1NativeJournalHandle handle);
    FixtureB1NativeJournalDirectoryEntriesResult EnumerateDirectoryEntries(
        IFixtureB1NativeJournalHandle directory);
    FixtureB1NativeJournalBytesResult ReadExact(
        IFixtureB1NativeJournalHandle file, int expectedLength);
    FixtureB1NativeJournalCloseResult Close(IFixtureB1NativeJournalHandle handle);
}

internal interface IFixtureB1NativeJournalWriterAuthority
{
    // This is an injected deterministic model until the authenticated owner channel exists.
    bool AuthorizeLiveWriter(FixtureB1AuthoritySession session, Guid leaseId);
}

internal interface IFixtureB1NativeJournalFaultInjector
{
    bool ShouldFail(FixtureB1NativeJournalFaultPoint point);
}

internal sealed record FixtureB1NativeJournalFailure(
    string Code,
    FixtureB1NativeJournalFaultPoint FaultPoint = FixtureB1NativeJournalFaultPoint.None);

internal sealed record FixtureB1NativeJournalCleanupReceipt(
    bool Complete,
    IReadOnlyList<Guid> UncertainHandleIds,
    IReadOnlyList<string> FailureCodes);

internal sealed record FixtureB1NativeJournalReadObservation(
    bool Admitted,
    IReadOnlyList<FixtureB1NativeJournalHandleObservation> RootChain,
    FixtureB1NativeJournalHandleObservation? Frames,
    FixtureB1NativeJournalHandleObservation? Anchor,
    byte[]? FramesBytes,
    byte[]? AnchorBytes,
    FixtureB1NativeJournalFailure? Failure)
{
    internal bool NativeAuthorityProven => false;
    internal bool NativeDurabilityProven => false;
}

internal sealed record FixtureB1NativeJournalWriterAdmission(
    bool Admitted,
    FixtureB1NativeJournalFailure? Failure)
{
    internal bool NativeAuthorityProven => false;
    internal bool NativeDurabilityProven => false;
}

internal sealed record FixtureB1NativeJournalAcquisition(
    FixtureB1NativeJournalObservationLease? Lease,
    FixtureB1NativeJournalCleanupOwner CleanupOwner,
    FixtureB1NativeJournalFailure? Failure)
{
    internal bool Succeeded => Lease is not null && Failure is null;
    internal bool NativeAuthorityProven => false;
    internal bool NativeDurabilityProven => false;
}

/// <summary>
/// The only owner of handles accepted by a native journal acquisition. A failed Close call is
/// never retried because the raw value may already have been closed and reused by the process.
/// </summary>
internal sealed class FixtureB1NativeJournalCleanupOwner
{
    private readonly object _gate = new();
    private readonly IFixtureB1NativeJournalObservationApi _api;
    private readonly List<OwnedHandle> _handles = [];

    internal FixtureB1NativeJournalCleanupOwner(IFixtureB1NativeJournalObservationApi api) =>
        _api = api ?? throw new ArgumentNullException(nameof(api));

    internal void Adopt(
        IFixtureB1NativeJournalHandle handle,
        FixtureB1NativeJournalHandleRole role)
    {
        ArgumentNullException.ThrowIfNull(handle);
        lock (_gate)
        {
            if (_handles.Any(candidate => ReferenceEquals(candidate.Handle, handle)))
                throw new InvalidOperationException("A unique local handle identity is required.");
            var duplicateId = handle.LocalHandleId == Guid.Empty ||
                _handles.Any(candidate => candidate.Handle.LocalHandleId == handle.LocalHandleId);
            var cleanupId = handle.LocalHandleId;
            if (duplicateId)
            {
                do { cleanupId = Guid.NewGuid(); }
                while (_handles.Any(candidate => candidate.CleanupId == cleanupId));
            }
            // Retain the returned object before rejecting its identity. A failed native call
            // may still have yielded a live handle that this owner must close exactly once.
            _handles.Add(new(handle, role, cleanupId));
            if (duplicateId)
                throw new InvalidOperationException("A unique local handle identity is required.");
        }
    }

    internal FixtureB1NativeJournalCleanupReceipt CloseWriterOriginals() =>
        CloseWhere(static handle => handle.Role == FixtureB1NativeJournalHandleRole.WriterOriginal);

    internal FixtureB1NativeJournalCleanupReceipt CloseAll() => CloseWhere(static _ => true);

    internal IReadOnlyList<Guid> UncertainHandleIds
    {
        get
        {
            lock (_gate)
                return [.. _handles.Where(handle => handle.Uncertain)
                    .Select(handle => handle.CleanupId)];
        }
    }

    private FixtureB1NativeJournalCleanupReceipt CloseWhere(Func<OwnedHandle, bool> predicate)
    {
        lock (_gate)
        {
            var failures = new List<string>();
            for (var index = _handles.Count - 1; index >= 0; index--)
            {
                var owned = _handles[index];
                if (!predicate(owned) || owned.CloseAttempted)
                    continue;
                owned.CloseAttempted = true;
                FixtureB1NativeJournalCloseResult result;
                try
                {
                    result = _api.Close(owned.Handle);
                }
                catch
                {
                    result = new(FixtureB1NativeJournalCloseDisposition.Uncertain,
                        "native-journal.close-threw");
                }
                owned.Uncertain = result.Disposition != FixtureB1NativeJournalCloseDisposition.Closed;
                if (result.FailureCode is not null)
                    failures.Add(result.FailureCode);
            }

            var uncertain = _handles.Where(handle => handle.Uncertain)
                .Select(handle => handle.CleanupId).ToArray();
            return new(uncertain.Length == 0, uncertain, failures);
        }
    }

    private sealed class OwnedHandle(
        IFixtureB1NativeJournalHandle handle,
        FixtureB1NativeJournalHandleRole role,
        Guid cleanupId)
    {
        internal IFixtureB1NativeJournalHandle Handle { get; } = handle;
        internal FixtureB1NativeJournalHandleRole Role { get; } = role;
        internal Guid CleanupId { get; } = cleanupId;
        internal bool CloseAttempted { get; set; }
        internal bool Uncertain { get; set; }
    }
}

/// <summary>
/// Unregistered qualification-only acquisition. It establishes point-in-time retained-handle
/// observations. Writer authority remains injected and therefore never proves native authority.
/// </summary>
internal static class FixtureB1NativeJournalObservation
{
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;

    internal static FixtureB1NativeJournalAcquisition Acquire(
        IFixtureB1NativeJournalObservationApi api,
        IFixtureB1NativeJournalWriterAuthority writerAuthority,
        FixtureB1AuthoritySession session,
        FixtureB1NativeJournalAcquisitionOptions options,
        IFixtureB1NativeJournalFaultInjector? faults = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(writerAuthority);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        var cleanup = new FixtureB1NativeJournalCleanupOwner(api);
        try
        {
            if (session.RunNonce == Guid.Empty || session.LaunchId == Guid.Empty ||
                session.WrapperInstance == Guid.Empty || session.SessionEpoch == Guid.Empty ||
                session.ConnectionGeneration <= 0)
                throw new AcquisitionException("native-journal.session-invalid");
            ValidateOptions(options);
            var roots = new List<IFixtureB1NativeJournalHandle>();
            Fail(faults, FixtureB1NativeJournalFaultPoint.BeforeOpenVolumeRoot);
            var volume = RequireHandle(api.OpenVolumeRoot(options.CanonicalDriveRoot), cleanup,
                FixtureB1NativeJournalHandleRole.RootChain);
            roots.Add(volume);
            var parentObservation = RequireObservation(api.Observe(volume));
            ValidateDirectory(parentObservation, options.Access.RequiredDirectoryAccess);
            Fail(faults, FixtureB1NativeJournalFaultPoint.AfterOpenVolumeRoot);

            var parent = volume;
            foreach (var component in options.AncestorComponents)
            {
                Fail(faults, FixtureB1NativeJournalFaultPoint.BeforeOpenAncestor);
                parent = RequireHandle(api.OpenDirectoryRelative(parent, component), cleanup,
                    FixtureB1NativeJournalHandleRole.RootChain);
                roots.Add(parent);
                var observation = RequireObservation(api.Observe(parent));
                ValidateDirectory(observation, options.Access.RequiredDirectoryAccess);
                ValidateRelativePath(parentObservation.FinalPath, observation.FinalPath,
                    component);
                if (observation.VolumeSerialNumber != parentObservation.VolumeSerialNumber)
                    throw new AcquisitionException("native-journal.volume-drift");
                parentObservation = observation;
                Fail(faults, FixtureB1NativeJournalFaultPoint.AfterOpenAncestor);
            }

            Fail(faults, FixtureB1NativeJournalFaultPoint.BeforeCreatePrivateRoot);
            var privateRoot = RequireHandle(api.CreatePrivateDirectoryRelative(
                parent, options.PrivateRootName,
                (byte[])options.DirectorySecurity.SelfRelativeSecurityDescriptor.Clone()),
                cleanup, FixtureB1NativeJournalHandleRole.RootChain);
            roots.Add(privateRoot);
            var privateRootObservation = RequireObservation(api.Observe(privateRoot));
            ValidateDirectory(privateRootObservation, options.Access.RequiredDirectoryAccess);
            ValidateRelativePath(parentObservation.FinalPath,
                privateRootObservation.FinalPath, options.PrivateRootName);
            if (privateRootObservation.VolumeSerialNumber != parentObservation.VolumeSerialNumber)
                throw new AcquisitionException("native-journal.volume-drift");
            ValidateSecurity(privateRootObservation.Security, options.DirectorySecurity);
            Fail(faults, FixtureB1NativeJournalFaultPoint.AfterCreatePrivateRoot);

            Fail(faults, FixtureB1NativeJournalFaultPoint.BeforeCreateFrames);
            var framesWriter = RequireHandle(api.CreateFileRelative(
                privateRoot, options.FramesName,
                (byte[])options.FileSecurity.SelfRelativeSecurityDescriptor.Clone()),
                cleanup, FixtureB1NativeJournalHandleRole.WriterOriginal);
            Fail(faults, FixtureB1NativeJournalFaultPoint.AfterCreateFrames);

            Fail(faults, FixtureB1NativeJournalFaultPoint.BeforeCreateAnchor);
            var anchorWriter = RequireHandle(api.CreateFileRelative(
                privateRoot, options.AnchorName,
                (byte[])options.FileSecurity.SelfRelativeSecurityDescriptor.Clone()),
                cleanup, FixtureB1NativeJournalHandleRole.WriterOriginal);
            Fail(faults, FixtureB1NativeJournalFaultPoint.AfterCreateAnchor);

            Fail(faults, FixtureB1NativeJournalFaultPoint.BeforeDuplicateFrames);
            var framesRead = RequireHandle(api.DuplicateReadOnly(framesWriter), cleanup,
                FixtureB1NativeJournalHandleRole.ReadObservation);
            Fail(faults, FixtureB1NativeJournalFaultPoint.AfterDuplicateFrames);

            Fail(faults, FixtureB1NativeJournalFaultPoint.BeforeDuplicateAnchor);
            var anchorRead = RequireHandle(api.DuplicateReadOnly(anchorWriter), cleanup,
                FixtureB1NativeJournalHandleRole.ReadObservation);
            Fail(faults, FixtureB1NativeJournalFaultPoint.AfterDuplicateAnchor);

            if (framesWriter.LocalHandleId == framesRead.LocalHandleId ||
                anchorWriter.LocalHandleId == anchorRead.LocalHandleId ||
                framesWriter.LocalHandleId == anchorWriter.LocalHandleId)
                throw new AcquisitionException("native-journal.handle-identity-reused");

            Fail(faults, FixtureB1NativeJournalFaultPoint.BeforeInitialObservation);
            var rootSnapshots = roots.Select(handle => RequireObservation(api.Observe(handle)))
                .ToArray();
            var framesWriterSnapshot = RequireObservation(api.Observe(framesWriter));
            var anchorWriterSnapshot = RequireObservation(api.Observe(anchorWriter));
            var framesReadSnapshot = RequireObservation(api.Observe(framesRead));
            var anchorReadSnapshot = RequireObservation(api.Observe(anchorRead));
            Fail(faults, FixtureB1NativeJournalFaultPoint.BeforeInitialEnumeration);
            var entries = RequireEntries(api.EnumerateDirectoryEntries(privateRoot));

            ValidateInitialObservations(options, rootSnapshots, framesWriterSnapshot,
                anchorWriterSnapshot, framesReadSnapshot, anchorReadSnapshot, entries);
            Fail(faults, FixtureB1NativeJournalFaultPoint.BeforeLeasePublication);
            var lease = new FixtureB1NativeJournalObservationLease(
                api, writerAuthority, session, options, cleanup, Guid.NewGuid(), roots,
                privateRoot, framesWriter, anchorWriter, framesRead, anchorRead,
                rootSnapshots, framesWriterSnapshot, anchorWriterSnapshot,
                framesReadSnapshot, anchorReadSnapshot);
            return new(lease, cleanup, null);
        }
        catch (AcquisitionException exception)
        {
            var close = cleanup.CloseAll();
            var code = close.Complete ? exception.Code : "native-journal.acquire-close-pending";
            return new(null, cleanup, new(code, exception.Point));
        }
        catch
        {
            var close = cleanup.CloseAll();
            var code = close.Complete ? "native-journal.acquire-threw" :
                "native-journal.acquire-close-pending";
            return new(null, cleanup, new(code));
        }
    }

    private static IFixtureB1NativeJournalHandle RequireHandle(
        FixtureB1NativeJournalHandleResult result,
        FixtureB1NativeJournalCleanupOwner cleanup,
        FixtureB1NativeJournalHandleRole role)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Handle is not null)
            cleanup.Adopt(result.Handle, role);
        if (!result.Succeeded)
            throw new AcquisitionException(result.FailureCode ?? "native-journal.handle-unavailable");
        return result.Handle!;
    }

    internal static FixtureB1NativeJournalHandleObservation RequireObservation(
        FixtureB1NativeJournalObservationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Succeeded)
            throw new AcquisitionException(result.FailureCode ??
                "native-journal.observation-unavailable");
        return result.Observation!;
    }

    internal static IReadOnlyList<string> RequireEntries(
        FixtureB1NativeJournalDirectoryEntriesResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Succeeded)
            throw new AcquisitionException(result.FailureCode ??
                "native-journal.entries-unavailable");
        return result.EntryNames!;
    }

    private static void ValidateOptions(FixtureB1NativeJournalAcquisitionOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.CanonicalDriveRoot) ||
            options.AncestorComponents is null || options.DirectorySecurity is null ||
            options.FileSecurity is null || options.Access is null ||
            options.MaximumFramesBytes < 0 || options.MaximumAnchorBytes < 0 ||
            options.MaximumFramesBytes > int.MaxValue ||
            options.MaximumAnchorBytes > int.MaxValue)
            throw new AcquisitionException("native-journal.options-invalid");
        foreach (var name in options.AncestorComponents.Append(options.PrivateRootName)
                     .Append(options.FramesName).Append(options.AnchorName))
            ValidateComponent(name);
        if (StringComparer.OrdinalIgnoreCase.Equals(options.FramesName, options.AnchorName) ||
            options.DirectorySecurity.SelfRelativeSecurityDescriptor.Length == 0 ||
            options.FileSecurity.SelfRelativeSecurityDescriptor.Length == 0 ||
            string.IsNullOrWhiteSpace(options.DirectorySecurity.ExpectedOwnerSid) ||
            string.IsNullOrWhiteSpace(options.FileSecurity.ExpectedOwnerSid))
            throw new AcquisitionException("native-journal.options-invalid");
    }

    internal static void ValidateComponent(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 64 || name is "." or ".." ||
            !char.IsAsciiLetterOrDigit(name[0]) || name[^1] is '.' or ' ' ||
            name.Any(character => !(char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '_' or '-')))
            throw new AcquisitionException("native-journal.component-invalid");
    }

    internal static void ValidateInitialObservations(
        FixtureB1NativeJournalAcquisitionOptions options,
        IReadOnlyList<FixtureB1NativeJournalHandleObservation> roots,
        FixtureB1NativeJournalHandleObservation framesWriter,
        FixtureB1NativeJournalHandleObservation anchorWriter,
        FixtureB1NativeJournalHandleObservation framesRead,
        FixtureB1NativeJournalHandleObservation anchorRead,
        IReadOnlyList<string> entries)
    {
        if (roots.Count != options.AncestorComponents.Count + 2)
            throw new AcquisitionException("native-journal.root-chain-incomplete");
        for (var index = 0; index < roots.Count; index++)
        {
            ValidateDirectory(roots[index], options.Access.RequiredDirectoryAccess);
            if (index > 0)
                ValidateRelativePath(roots[index - 1].FinalPath, roots[index].FinalPath,
                    index == roots.Count - 1 ? options.PrivateRootName :
                    options.AncestorComponents[index - 1]);
            if (roots[index].VolumeSerialNumber != roots[0].VolumeSerialNumber)
                throw new AcquisitionException("native-journal.volume-drift");
        }
        ValidateSecurity(roots[^1].Security, options.DirectorySecurity);
        ValidateWriterFile(framesWriter, options.FileSecurity,
            options.Access.RequiredWriterAccess, options.MaximumFramesBytes);
        ValidateWriterFile(anchorWriter, options.FileSecurity,
            options.Access.RequiredWriterAccess, options.MaximumAnchorBytes);
        ValidateReadDuplicate(framesWriter, framesRead, options.FileSecurity, options.Access,
            options.MaximumFramesBytes);
        ValidateReadDuplicate(anchorWriter, anchorRead, options.FileSecurity, options.Access,
            options.MaximumAnchorBytes);
        ValidateRelativePath(roots[^1].FinalPath, framesWriter.FinalPath, options.FramesName);
        ValidateRelativePath(roots[^1].FinalPath, anchorWriter.FinalPath, options.AnchorName);
        if (SameObject(framesWriter, anchorWriter))
            throw new AcquisitionException("native-journal.files-alias");
        var expected = new HashSet<string>([options.FramesName, options.AnchorName],
            StringComparer.OrdinalIgnoreCase);
        if (entries.Count != expected.Count || entries.Any(entry => !expected.Remove(entry)) ||
            expected.Count != 0)
            throw new AcquisitionException("native-journal.root-entries-unexpected");
    }

    internal static void ValidateDirectory(
        FixtureB1NativeJournalHandleObservation observation,
        uint requiredAccess)
    {
        if (observation.Kind != FixtureB1NativeJournalObjectKind.Directory ||
            (observation.FileAttributes & FileAttributeDirectory) == 0 ||
            (observation.FileAttributes & FileAttributeReparsePoint) != 0 ||
            observation.ReparseTag != 0 || observation.DeletePending ||
            !HasAccess(observation.GrantedAccessMask, requiredAccess) ||
            observation.FileIdLow == 0 && observation.FileIdHigh == 0 ||
            string.IsNullOrWhiteSpace(observation.FinalPath))
            throw new AcquisitionException("native-journal.directory-rejected");
    }

    internal static void ValidateWriterFile(
        FixtureB1NativeJournalHandleObservation observation,
        FixtureB1NativeJournalSecurityPolicy security,
        uint requiredAccess,
        long maximumBytes)
    {
        if (observation.Kind != FixtureB1NativeJournalObjectKind.File ||
            (observation.FileAttributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0 ||
            observation.ReparseTag != 0 || observation.NumberOfLinks != 1 ||
            observation.DeletePending || observation.EndOfFile < 0 ||
            observation.EndOfFile > maximumBytes || observation.AllocationSize < 0 ||
            !HasAccess(observation.GrantedAccessMask, requiredAccess) ||
            observation.FileIdLow == 0 && observation.FileIdHigh == 0 ||
            observation.StreamNames.Count != 1 ||
            !StringComparer.OrdinalIgnoreCase.Equals(observation.StreamNames[0], "::$DATA"))
            throw new AcquisitionException("native-journal.file-rejected");
        ValidateSecurity(observation.Security, security);
    }

    internal static void ValidateReadDuplicate(
        FixtureB1NativeJournalHandleObservation writer,
        FixtureB1NativeJournalHandleObservation reader,
        FixtureB1NativeJournalSecurityPolicy security,
        FixtureB1NativeJournalAccessPolicy access,
        long maximumBytes)
    {
        ValidateWriterFile(reader, security, access.RequiredReadAccess, maximumBytes);
        if ((reader.GrantedAccessMask & access.ForbiddenReadAccess) != 0 ||
            !SameObject(writer, reader))
            throw new AcquisitionException("native-journal.read-duplicate-rejected");
    }

    internal static void ValidateSecurity(
        FixtureB1NativeJournalSecurityObservation observation,
        FixtureB1NativeJournalSecurityPolicy policy)
    {
        if (!StringComparer.Ordinal.Equals(observation.OwnerSid, policy.ExpectedOwnerSid) ||
            policy.RequireProtectedDacl && !observation.DaclProtected ||
            observation.Dacl.Count != policy.ExpectedDacl.Count)
            throw new AcquisitionException("native-journal.security-rejected");
        for (var index = 0; index < observation.Dacl.Count; index++)
        {
            if (observation.Dacl[index] != policy.ExpectedDacl[index])
                throw new AcquisitionException("native-journal.security-rejected");
        }
    }

    internal static bool SameObject(
        FixtureB1NativeJournalHandleObservation left,
        FixtureB1NativeJournalHandleObservation right) =>
        left.VolumeSerialNumber == right.VolumeSerialNumber &&
        left.FileIdLow == right.FileIdLow && left.FileIdHigh == right.FileIdHigh;

    private static bool HasAccess(uint actual, uint required) => (actual & required) == required;

    private static void ValidateRelativePath(string parent, string child, string component)
    {
        var expected = parent.TrimEnd('\\') + "\\" + component;
        if (!StringComparer.OrdinalIgnoreCase.Equals(expected, child.TrimEnd('\\')))
            throw new AcquisitionException("native-journal.final-path-mismatch");
    }

    private static void Fail(
        IFixtureB1NativeJournalFaultInjector? faults,
        FixtureB1NativeJournalFaultPoint point)
    {
        bool fail;
        try { fail = faults?.ShouldFail(point) == true; }
        catch { fail = true; }
        if (fail)
            throw new AcquisitionException("native-journal.injected-fault", point);
    }

    internal sealed class AcquisitionException(
        string code,
        FixtureB1NativeJournalFaultPoint point = FixtureB1NativeJournalFaultPoint.None)
        : Exception("A native journal observation was rejected.")
    {
        internal string Code { get; } = code;
        internal FixtureB1NativeJournalFaultPoint Point { get; } = point;
    }
}

internal sealed class FixtureB1NativeJournalObservationLease
{
    private readonly object _gate = new();
    private readonly IFixtureB1NativeJournalObservationApi _api;
    private readonly IFixtureB1NativeJournalWriterAuthority _writerAuthority;
    private readonly FixtureB1AuthoritySession _session;
    private readonly FixtureB1NativeJournalAcquisitionOptions _options;
    private readonly FixtureB1NativeJournalCleanupOwner _cleanup;
    private readonly IReadOnlyList<IFixtureB1NativeJournalHandle> _roots;
    private readonly IFixtureB1NativeJournalHandle _privateRoot;
    private readonly IFixtureB1NativeJournalHandle _framesWriter;
    private readonly IFixtureB1NativeJournalHandle _anchorWriter;
    private readonly IFixtureB1NativeJournalHandle _framesRead;
    private readonly IFixtureB1NativeJournalHandle _anchorRead;
    private readonly IReadOnlyList<FixtureB1NativeJournalHandleObservation> _initialRoots;
    private readonly FixtureB1NativeJournalHandleObservation _initialFramesWriter;
    private readonly FixtureB1NativeJournalHandleObservation _initialAnchorWriter;
    private readonly FixtureB1NativeJournalHandleObservation _initialFramesRead;
    private readonly FixtureB1NativeJournalHandleObservation _initialAnchorRead;
    private FixtureB1NativeJournalLeaseState _state = FixtureB1NativeJournalLeaseState.WritableIdle;
    private bool _writerCustodyClosed;

    internal FixtureB1NativeJournalObservationLease(
        IFixtureB1NativeJournalObservationApi api,
        IFixtureB1NativeJournalWriterAuthority writerAuthority,
        FixtureB1AuthoritySession session,
        FixtureB1NativeJournalAcquisitionOptions options,
        FixtureB1NativeJournalCleanupOwner cleanup,
        Guid leaseId,
        IReadOnlyList<IFixtureB1NativeJournalHandle> roots,
        IFixtureB1NativeJournalHandle privateRoot,
        IFixtureB1NativeJournalHandle framesWriter,
        IFixtureB1NativeJournalHandle anchorWriter,
        IFixtureB1NativeJournalHandle framesRead,
        IFixtureB1NativeJournalHandle anchorRead,
        IReadOnlyList<FixtureB1NativeJournalHandleObservation> initialRoots,
        FixtureB1NativeJournalHandleObservation initialFramesWriter,
        FixtureB1NativeJournalHandleObservation initialAnchorWriter,
        FixtureB1NativeJournalHandleObservation initialFramesRead,
        FixtureB1NativeJournalHandleObservation initialAnchorRead)
    {
        _api = api;
        _writerAuthority = writerAuthority;
        _session = session;
        _options = options;
        _cleanup = cleanup;
        LeaseId = leaseId;
        _roots = roots;
        _privateRoot = privateRoot;
        _framesWriter = framesWriter;
        _anchorWriter = anchorWriter;
        _framesRead = framesRead;
        _anchorRead = anchorRead;
        _initialRoots = initialRoots;
        _initialFramesWriter = initialFramesWriter;
        _initialAnchorWriter = initialAnchorWriter;
        _initialFramesRead = initialFramesRead;
        _initialAnchorRead = initialAnchorRead;
    }

    internal Guid LeaseId { get; }
    internal bool NativeAuthorityProven => false;
    internal bool NativeDurabilityProven => false;
    internal FixtureB1NativeJournalLeaseState State { get { lock (_gate) return _state; } }

    internal FixtureB1NativeJournalReadObservation ObserveRetainedStorageForRead()
    {
        lock (_gate)
        {
            if (_state is FixtureB1NativeJournalLeaseState.Closing or
                FixtureB1NativeJournalLeaseState.ClosePending or
                FixtureB1NativeJournalLeaseState.Closed)
                return ReadFailure("native-journal.lease-closing");
            if (!_writerCustodyClosed)
                return ReadFailure("native-journal.writer-still-open");
            try
            {
                var roots = _roots.Select(handle =>
                    FixtureB1NativeJournalObservation.RequireObservation(_api.Observe(handle)))
                    .ToArray();
                var frames = FixtureB1NativeJournalObservation.RequireObservation(
                    _api.Observe(_framesRead));
                var anchor = FixtureB1NativeJournalObservation.RequireObservation(
                    _api.Observe(_anchorRead));
                var entries = FixtureB1NativeJournalObservation.RequireEntries(
                    _api.EnumerateDirectoryEntries(_privateRoot));
                FixtureB1NativeJournalObservation.ValidateInitialObservations(
                    _options, roots, _writerCustodyClosed ? _initialFramesWriter :
                        FixtureB1NativeJournalObservation.RequireObservation(
                            _api.Observe(_framesWriter)),
                    _writerCustodyClosed ? _initialAnchorWriter :
                        FixtureB1NativeJournalObservation.RequireObservation(
                            _api.Observe(_anchorWriter)),
                    frames, anchor, entries);
                ValidateStableRootIdentities(roots);
                ValidateStableObject(_initialFramesRead, frames);
                ValidateStableObject(_initialAnchorRead, anchor);
                var framesBytes = RequireBytes(_api.ReadExact(_framesRead,
                    checked((int)frames.EndOfFile)), checked((int)frames.EndOfFile));
                var anchorBytes = RequireBytes(_api.ReadExact(_anchorRead,
                    checked((int)anchor.EndOfFile)), checked((int)anchor.EndOfFile));
                var framesAfter = FixtureB1NativeJournalObservation.RequireObservation(
                    _api.Observe(_framesRead));
                var anchorAfter = FixtureB1NativeJournalObservation.RequireObservation(
                    _api.Observe(_anchorRead));
                ValidateStableObject(frames, framesAfter);
                ValidateStableObject(anchor, anchorAfter);
                if (framesAfter.EndOfFile != frames.EndOfFile ||
                    anchorAfter.EndOfFile != anchor.EndOfFile)
                    throw new FixtureB1NativeJournalObservation.AcquisitionException(
                        "native-journal.read-size-drift");
                return new(true, roots, frames, anchor, framesBytes, anchorBytes, null);
            }
            catch (FixtureB1NativeJournalObservation.AcquisitionException exception)
            {
                _state = FixtureB1NativeJournalLeaseState.Poisoned;
                return ReadFailure(exception.Code);
            }
            catch
            {
                _state = FixtureB1NativeJournalLeaseState.Poisoned;
                return ReadFailure("native-journal.read-observation-threw");
            }
        }
    }

    internal FixtureB1NativeJournalWriterAdmission AuthorizeLiveWriter()
    {
        lock (_gate)
        {
            if (_state != FixtureB1NativeJournalLeaseState.WritableIdle ||
                _writerCustodyClosed)
                return WriterFailure("native-journal.writer-not-admitted");
            bool authority;
            try { authority = _writerAuthority.AuthorizeLiveWriter(_session, LeaseId); }
            catch { authority = false; }
            if (!authority)
            {
                _state = FixtureB1NativeJournalLeaseState.Poisoned;
                return WriterFailure("native-journal.writer-authority-lost");
            }
            try
            {
                var roots = _roots.Select(handle =>
                    FixtureB1NativeJournalObservation.RequireObservation(_api.Observe(handle)))
                    .ToArray();
                var framesWriter = FixtureB1NativeJournalObservation.RequireObservation(
                    _api.Observe(_framesWriter));
                var anchorWriter = FixtureB1NativeJournalObservation.RequireObservation(
                    _api.Observe(_anchorWriter));
                var framesRead = FixtureB1NativeJournalObservation.RequireObservation(
                    _api.Observe(_framesRead));
                var anchorRead = FixtureB1NativeJournalObservation.RequireObservation(
                    _api.Observe(_anchorRead));
                var entries = FixtureB1NativeJournalObservation.RequireEntries(
                    _api.EnumerateDirectoryEntries(_privateRoot));
                FixtureB1NativeJournalObservation.ValidateInitialObservations(
                    _options, roots, framesWriter, anchorWriter, framesRead, anchorRead, entries);
                ValidateStableRootIdentities(roots);
                ValidateStableObject(_initialFramesWriter, framesWriter);
                ValidateStableObject(_initialAnchorWriter, anchorWriter);
                ValidateStableObject(_initialFramesRead, framesRead);
                ValidateStableObject(_initialAnchorRead, anchorRead);
                return new(true, null);
            }
            catch (FixtureB1NativeJournalObservation.AcquisitionException exception)
            {
                _state = FixtureB1NativeJournalLeaseState.Poisoned;
                return WriterFailure(exception.Code);
            }
            catch
            {
                _state = FixtureB1NativeJournalLeaseState.Poisoned;
                return WriterFailure("native-journal.writer-observation-threw");
            }
        }
    }

    internal void Poison()
    {
        lock (_gate)
            if (_state is FixtureB1NativeJournalLeaseState.WritableIdle or
                FixtureB1NativeJournalLeaseState.Acquiring)
                _state = FixtureB1NativeJournalLeaseState.Poisoned;
    }

    internal FixtureB1NativeJournalCleanupReceipt CloseWriterCustody()
    {
        lock (_gate)
        {
            if (_writerCustodyClosed)
                return new(_cleanup.UncertainHandleIds.Count == 0,
                    _cleanup.UncertainHandleIds, []);
            _writerCustodyClosed = true;
            var receipt = _cleanup.CloseWriterOriginals();
            if (!receipt.Complete)
                _state = FixtureB1NativeJournalLeaseState.ClosePending;
            else if (_state == FixtureB1NativeJournalLeaseState.WritableIdle)
                _state = FixtureB1NativeJournalLeaseState.Poisoned;
            return receipt;
        }
    }

    internal FixtureB1NativeJournalCleanupReceipt Close()
    {
        lock (_gate)
        {
            if (_state == FixtureB1NativeJournalLeaseState.Closed)
                return new(true, [], []);
            _state = FixtureB1NativeJournalLeaseState.Closing;
            var receipt = _cleanup.CloseAll();
            _state = receipt.Complete ? FixtureB1NativeJournalLeaseState.Closed :
                FixtureB1NativeJournalLeaseState.ClosePending;
            return receipt;
        }
    }

    private void ValidateStableRootIdentities(
        IReadOnlyList<FixtureB1NativeJournalHandleObservation> roots)
    {
        if (roots.Count != _initialRoots.Count)
            throw new FixtureB1NativeJournalObservation.AcquisitionException(
                "native-journal.root-chain-drift");
        for (var index = 0; index < roots.Count; index++)
            ValidateStableObject(_initialRoots[index], roots[index]);
    }

    private static void ValidateStableObject(
        FixtureB1NativeJournalHandleObservation initial,
        FixtureB1NativeJournalHandleObservation current)
    {
        if (!FixtureB1NativeJournalObservation.SameObject(initial, current) ||
            initial.Kind != current.Kind || initial.FinalPath != current.FinalPath ||
            !SecurityEquals(initial.Security, current.Security) ||
            initial.NumberOfLinks != current.NumberOfLinks ||
            initial.DeletePending != current.DeletePending)
            throw new FixtureB1NativeJournalObservation.AcquisitionException(
                "native-journal.object-drift");
    }

    private static bool SecurityEquals(
        FixtureB1NativeJournalSecurityObservation left,
        FixtureB1NativeJournalSecurityObservation right)
    {
        if (!StringComparer.Ordinal.Equals(left.OwnerSid, right.OwnerSid) ||
            left.DaclProtected != right.DaclProtected || left.Dacl.Count != right.Dacl.Count)
            return false;
        for (var index = 0; index < left.Dacl.Count; index++)
            if (left.Dacl[index] != right.Dacl[index])
                return false;
        return true;
    }

    private static FixtureB1NativeJournalReadObservation ReadFailure(string code) =>
        new(false, [], null, null, null, null, new(code));

    private static byte[] RequireBytes(
        FixtureB1NativeJournalBytesResult result, int expectedLength)
    {
        if (!result.Succeeded || result.Bytes!.Length != expectedLength)
            throw new FixtureB1NativeJournalObservation.AcquisitionException(
                result.FailureCode ?? "native-journal.byte-read-incomplete");
        return result.Bytes;
    }

    private static FixtureB1NativeJournalWriterAdmission WriterFailure(string code) =>
        new(false, new(code));
}
