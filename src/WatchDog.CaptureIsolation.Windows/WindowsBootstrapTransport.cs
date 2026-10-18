using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using WatchDog.CaptureProtocol;

namespace WatchDog.CaptureIsolation.Windows;

internal sealed record WindowsBootstrapFixtureProfile(
    TimeSpan HandshakeBudget,
    TimeSpan DrainBudget,
    int MaxQueueDepth,
    CaptureProtocolLimits ProtocolLimits)
{
    internal void Validate()
    {
        if (HandshakeBudget <= TimeSpan.Zero || HandshakeBudget == Timeout.InfiniteTimeSpan ||
            DrainBudget <= TimeSpan.Zero || DrainBudget == Timeout.InfiniteTimeSpan ||
            MaxQueueDepth != 1)
        {
            throw new ArgumentException("Explicit finite fixture bootstrap limits are required.");
        }
        ArgumentNullException.ThrowIfNull(ProtocolLimits);
    }
}

internal sealed record WindowsBootstrapExpectation(
    HelperLaunchRequest Request,
    int SchemaVersion,
    int Architecture,
    string ParentPayloadDigest,
    string HelperPayloadDigest,
    CaptureIsolationLimitSnapshot EffectiveLimits);

/// <summary>
/// Unregistered parent-side Windows bootstrap owner. The endpoint and secret never leave the
/// inherited page-file section and never participate in public diagnostics.
/// </summary>
internal sealed partial class WindowsBootstrapTransportBoundary : IHelperTransportBoundary, IAsyncDisposable
{
    internal const int SecretBytes = 32;
    private const uint PageReadWrite = 0x04;
    private const uint FileMapRead = 0x0004;
    private const uint FileMapWrite = 0x0002;
    private const uint SectionMapRead = 0x0004;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const uint WaitTimeout = 258;
    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagFirstPipeInstance = 0x00080000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint PipeTypeMessage = 0x00000004;
    private const uint PipeReadModeMessage = 0x00000002;
    private const uint PipeRejectRemoteClients = 0x00000008;
    private const uint SddlRevision1 = 1;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly WindowsJobBoundary _jobs;
    private readonly WindowsBootstrapExpectation _expected;
    private readonly WindowsBootstrapFixtureProfile _profile;
    private readonly NamedPipeServerStream _pipe;
    private readonly ParentBootstrapProtocol _protocol;
    private SafeFileHandle? _section;
    private SafeFileHandle? _childSection;
    private SafeFileHandle? _childParentProcess;
    private nint _view;
    private readonly int _sectionBytes;
    private bool _authenticateStarted;
    private bool _disposed;

    private WindowsBootstrapTransportBoundary(
        WindowsJobBoundary jobs,
        WindowsBootstrapExpectation expected,
        WindowsBootstrapFixtureProfile profile,
        NamedPipeServerStream pipe,
        ParentBootstrapProtocol protocol,
        SafeFileHandle section,
        SafeFileHandle childSection,
        SafeFileHandle childParentProcess,
        nint view,
        int sectionBytes)
    {
        _jobs = jobs;
        _expected = expected;
        _profile = profile;
        _pipe = pipe;
        _protocol = protocol;
        _section = section;
        _childSection = childSection;
        _childParentProcess = childParentProcess;
        _view = view;
        _sectionBytes = sectionBytes;
    }

    internal static unsafe WindowsBootstrapTransportBoundary Create(
        WindowsJobBoundary jobs,
        WindowsBootstrapExpectation expected,
        WindowsBootstrapFixtureProfile profile)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(expected.Request);
        profile.Validate();

        var endpoint = $"wd-{Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()}";
        var pipe = CreateRestrictedPipe(endpoint);
        var secret = RandomNumberGenerator.GetBytes(SecretBytes);
        ParentBootstrapProtocol? protocol = null;
        SafeFileHandle? section = null;
        SafeFileHandle? childSection = null;
        SafeFileHandle? parentProcess = null;
        nint view = nint.Zero;
        byte[]? frame = null;
        try
        {
            parentProcess = OpenRestrictedCurrentProcessHandle();
            var parentId = GetProcessId(parentProcess);
            var parentCreation = GetCreationFileTime(parentProcess);
            var protocolLimits = profile.ProtocolLimits;
            var wireLimits = WireLimits(expected.EffectiveLimits);
            var descriptor = new BootstrapLaunchDescriptor(
                expected.Request.Correlation,
                AuthenticatedProtocolSession.ProtocolVersion,
                expected.SchemaVersion,
                expected.Architecture,
                expected.Request.PackageManifestDigest,
                expected.ParentPayloadDigest,
                expected.HelperPayloadDigest,
                wireLimits,
                endpoint,
                parentId,
                parentCreation);
            using (var descriptorSession = new AuthenticatedProtocolSession(
                secret,
                ProtocolChannel.Telemetry,
                ProtocolDirection.ParentToHelper,
                protocolLimits))
            {
                frame = descriptorSession.Encode(ProtocolMessageKind.Hello, descriptor);
            }
            protocol = new ParentBootstrapProtocol(secret, protocolLimits);
            var sectionBytes = checked(SecretBytes + frame.Length);
            section = CreateSection(sectionBytes);
            childSection = DuplicateReadOnlyInheritable(section);
            view = MapViewOfFile(section, FileMapWrite, 0, 0, (nuint)sectionBytes);
            if (view == nint.Zero)
                throw Failure("helper.windows.bootstrap.section-map-failed");
            Marshal.Copy(secret, 0, view, secret.Length);
            Marshal.Copy(frame, 0, view + SecretBytes, frame.Length);
            return new(
                jobs,
                expected,
                profile,
                pipe,
                protocol,
                section,
                childSection,
                parentProcess,
                view,
                sectionBytes);
        }
        catch
        {
            protocol?.Dispose();
            pipe.Dispose();
            if (view != nint.Zero)
                _ = UnmapViewOfFile(view);
            childSection?.Dispose();
            parentProcess?.Dispose();
            section?.Dispose();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            if (frame is not null)
                CryptographicOperations.ZeroMemory(frame);
        }
    }

    internal IReadOnlyList<SafeHandle> InheritedChildHandles =>
        [_childSection ?? throw new ObjectDisposedException(nameof(WindowsBootstrapTransportBoundary)),
         _childParentProcess ?? throw new ObjectDisposedException(nameof(WindowsBootstrapTransportBoundary))];

    internal nint ChildSectionHandle => _childSection?.DangerousGetHandle() ?? nint.Zero;
    internal nint ChildParentProcessHandle => _childParentProcess?.DangerousGetHandle() ?? nint.Zero;
    internal int SectionBytes => _sectionBytes;

    internal unsafe void CorruptBootstrapMaterialForFixture()
    {
        _gate.Wait();
        try
        {
            ThrowIfDisposed();
            if (_authenticateStarted || _view == nint.Zero)
                throw Failure("helper.windows.bootstrap.state-invalid");
            ((byte*)_view)[0] ^= 0x40;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<HelperBootstrapObservation> AuthenticateAsync(
        RetainedProcessIdentity process,
        HelperLaunchRequest request,
        CaptureIsolationLimitSnapshot effectiveLimits,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_authenticateStarted || process.HelperInstanceId != request.Correlation.HelperInstanceId ||
                request != _expected.Request || !SameLimits(effectiveLimits, _expected.EffectiveLimits))
            {
                throw Failure("helper.windows.bootstrap.state-invalid");
            }
            _authenticateStarted = true;
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(_profile.HandshakeBudget);
            await _pipe.WaitForConnectionAsync(budget.Token).ConfigureAwait(false);
            ZeroAndReleaseBootstrapMaterial();

            if (!GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out var clientProcessId))
                throw Failure("helper.windows.bootstrap.peer-query-failed");
            var retained = _jobs.GetOwnedProcessCoordinate(process, CancellationToken.None);
            if (clientProcessId != retained.ProcessId)
                throw Failure("helper.windows.bootstrap.peer-mismatch");
            using var connectedClient = OpenProcess(
                ProcessQueryLimitedInformation | Synchronize,
                false,
                clientProcessId);
            if (connectedClient.IsInvalid ||
                GetProcessId(connectedClient) != retained.ProcessId ||
                GetCreationFileTime(connectedClient) != retained.CreationFileTime ||
                WaitForSingleObject(connectedClient, 0) != WaitTimeout)
            {
                throw Failure("helper.windows.bootstrap.peer-mismatch");
            }

            var helperFrame = await ReadMessageAsync(_pipe, _profile.ProtocolLimits.MaxFrameBytes, budget.Token)
                .ConfigureAwait(false);
            HelperBootstrapHello helperHello;
            try
            {
                helperHello = _protocol.AcceptHelperHello(helperFrame, request.Correlation);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(helperFrame);
            }
            EnsureHelperHello(helperHello);
            var parentHello = new ParentBootstrapHello(
                request.Correlation,
                AuthenticatedProtocolSession.ProtocolVersion,
                _expected.SchemaVersion,
                _expected.Architecture,
                request.PackageManifestDigest,
                _expected.ParentPayloadDigest,
                _expected.HelperPayloadDigest,
                WireLimits(effectiveLimits));
            var parentFrame = _protocol.CreateParentHello(parentHello);
            try
            {
                await WriteMessageAsync(_pipe, parentFrame, budget.Token).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(parentFrame);
            }
            var ackFrame = await ReadMessageAsync(_pipe, _profile.ProtocolLimits.MaxFrameBytes, budget.Token)
                .ConfigureAwait(false);
            HelperBootstrapAck ack;
            try
            {
                ack = _protocol.AcceptHelperAck(ackFrame, request.Correlation);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(ackFrame);
            }
            EnsureAck(ack);
            return new(
                HelperAuthenticationOutcome.Authenticated,
                process,
                request.Correlation,
                request.Correlation.PlanDigest,
                effectiveLimits,
                [
                    new(
                        CaptureIsolationPayloadRole.Parent,
                        request.Correlation.ParentInstanceId,
                        request.Correlation.HelperInstanceId,
                        AuthenticatedProtocolSession.ProtocolVersion,
                        (CaptureIsolationSchemaVersion)_expected.SchemaVersion,
                        (CaptureIsolationArchitecture)_expected.Architecture,
                        request.PackageManifestDigest,
                        _expected.ParentPayloadDigest),
                    new(
                        CaptureIsolationPayloadRole.Helper,
                        request.Correlation.HelperInstanceId,
                        request.Correlation.ParentInstanceId,
                        AuthenticatedProtocolSession.ProtocolVersion,
                        (CaptureIsolationSchemaVersion)_expected.SchemaVersion,
                        (CaptureIsolationArchitecture)_expected.Architecture,
                        request.PackageManifestDigest,
                        _expected.HelperPayloadDigest),
                ]);
        }
        catch (OperationCanceledException)
        {
            await AbortTransportAsync().ConfigureAwait(false);
            throw;
        }
        catch (HelperBoundaryException)
        {
            await AbortTransportAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            await AbortTransportAsync().ConfigureAwait(false);
            throw Failure("helper.windows.bootstrap.failed");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask FenceAsync(
        RetainedProcessIdentity process,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;
            if (process.HelperInstanceId != _expected.Request.Correlation.HelperInstanceId)
                throw Failure("helper.windows.bootstrap.peer-mismatch");
            await AbortTransportAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<HelperStopOutcome> StopAsync(
        RetainedProcessIdentity process,
        TimeSpan gracefulBudget,
        CancellationToken cancellationToken)
    {
        if (gracefulBudget <= TimeSpan.Zero || gracefulBudget == Timeout.InfiniteTimeSpan)
            return HelperStopOutcome.Rejected;
        await FenceAsync(process, cancellationToken).ConfigureAwait(false);
        var drainBudget = gracefulBudget < _profile.DrainBudget
            ? gracefulBudget
            : _profile.DrainBudget;
        var primaryExited = await _jobs.WaitForOwnedProcessExitAsync(
            process,
            drainBudget,
            cancellationToken).ConfigureAwait(false);
        // EOF is not an authenticated stop acknowledgement and primary-process exit cannot prove
        // the job has no descendants. Force the supervisor down its independent terminate/reap path.
        return primaryExited ? HelperStopOutcome.BrokenTransport : HelperStopOutcome.TimedOut;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;
            await AbortTransportAsync().ConfigureAwait(false);
            _protocol.Dispose();
            _pipe.Dispose();
            _disposed = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureHelperHello(HelperBootstrapHello hello)
    {
        var expected = _expected;
        if (hello.Correlation != expected.Request.Correlation ||
            hello.ProtocolVersion != AuthenticatedProtocolSession.ProtocolVersion ||
            hello.SchemaVersion != expected.SchemaVersion || hello.Architecture != expected.Architecture ||
            !string.Equals(hello.PackageManifestDigest, expected.Request.PackageManifestDigest, StringComparison.Ordinal) ||
            !string.Equals(hello.HelperPayloadDigest, expected.HelperPayloadDigest, StringComparison.Ordinal) ||
            !SameLimits(hello.EffectiveLimits, expected.EffectiveLimits))
        {
            throw Failure("helper.windows.bootstrap.claim-mismatch");
        }
    }

    private void EnsureAck(HelperBootstrapAck ack)
    {
        var expected = _expected;
        if (ack.Correlation != expected.Request.Correlation ||
            ack.ProtocolVersion != AuthenticatedProtocolSession.ProtocolVersion ||
            ack.SchemaVersion != expected.SchemaVersion || ack.Architecture != expected.Architecture ||
            !string.Equals(ack.PackageManifestDigest, expected.Request.PackageManifestDigest, StringComparison.Ordinal) ||
            !string.Equals(ack.ParentPayloadDigest, expected.ParentPayloadDigest, StringComparison.Ordinal) ||
            !string.Equals(ack.HelperPayloadDigest, expected.HelperPayloadDigest, StringComparison.Ordinal) ||
            !SameLimits(ack.EffectiveLimits, expected.EffectiveLimits))
        {
            throw Failure("helper.windows.bootstrap.claim-mismatch");
        }
    }

    private ValueTask AbortTransportAsync()
    {
        ZeroAndReleaseBootstrapMaterial();
        if (_pipe.IsConnected)
        {
            try { _pipe.Disconnect(); } catch (IOException) { }
        }
        return ValueTask.CompletedTask;
    }

    private unsafe void ZeroAndReleaseBootstrapMaterial()
    {
        if (_view != nint.Zero)
        {
            new Span<byte>((void*)_view, _sectionBytes).Clear();
            if (!UnmapViewOfFile(_view))
                throw Failure("helper.windows.bootstrap.section-unmap-failed");
            _view = nint.Zero;
        }
        _childSection?.Dispose();
        _childSection = null;
        _childParentProcess?.Dispose();
        _childParentProcess = null;
        _section?.Dispose();
        _section = null;
    }

    private static NamedPipeServerStream CreateRestrictedPipe(string endpoint)
    {
        var userSid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(userSid))
            throw Failure("helper.windows.bootstrap.security-context-failed");
        // Protected DACL: only the current Windows user receives access. Exact retained-process
        // proof narrows that account-level grant to the one child after connection.
        var sddl = $"D:P(A;;GA;;;{userSid})";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                sddl,
                SddlRevision1,
                out var descriptor,
                out _))
        {
            throw Failure("helper.windows.bootstrap.security-descriptor-failed");
        }
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = checked((uint)Marshal.SizeOf<SecurityAttributes>()),
                SecurityDescriptor = descriptor,
                InheritHandle = 0,
            };
            var handle = CreateNamedPipe(
                $@"\\.\pipe\{endpoint}",
                PipeAccessDuplex | FileFlagFirstPipeInstance | FileFlagOverlapped,
                PipeTypeMessage | PipeReadModeMessage | PipeRejectRemoteClients,
                1,
                4096,
                4096,
                0,
                ref attributes);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw Failure("helper.windows.bootstrap.pipe-create-failed");
            }
            try
            {
                var stream = new NamedPipeServerStream(
                    PipeDirection.InOut,
                    isAsync: true,
                    isConnected: false,
                    handle);
                stream.ReadMode = PipeTransmissionMode.Message;
                return stream;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        finally
        {
            _ = LocalFree(descriptor);
        }
    }

    private static SafeFileHandle CreateSection(int bytes)
    {
        var handle = CreateFileMapping(new nint(-1), nint.Zero, PageReadWrite, 0, checked((uint)bytes), null);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw Failure("helper.windows.bootstrap.section-create-failed");
        }
        return handle;
    }

    private static SafeFileHandle DuplicateReadOnlyInheritable(SafeFileHandle source)
    {
        if (!DuplicateHandle(
                GetCurrentProcess(),
                source,
                GetCurrentProcess(),
                out var duplicate,
                SectionMapRead,
                false,
                0))
        {
            throw Failure("helper.windows.bootstrap.section-duplicate-failed");
        }
        return duplicate;
    }

    private static SafeFileHandle OpenRestrictedCurrentProcessHandle()
    {
        var handle = OpenProcess(
            ProcessQueryLimitedInformation | Synchronize,
            false,
            GetCurrentProcessId());
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw Failure("helper.windows.bootstrap.parent-handle-failed");
        }
        return handle;
    }

    private static BootstrapLimitSnapshot WireLimits(CaptureIsolationLimitSnapshot limits) =>
        new(limits.MaxControlFrameBytes, limits.MaxPayloadBytes, limits.MaxPendingCommands, limits.MaxTelemetrySamples);

    private static bool SameLimits(BootstrapLimitSnapshot actual, CaptureIsolationLimitSnapshot expected) =>
        actual.MaxControlFrameBytes == expected.MaxControlFrameBytes &&
        actual.MaxPayloadBytes == expected.MaxPayloadBytes &&
        actual.MaxPendingCommands == expected.MaxPendingCommands &&
        actual.MaxTelemetrySamples == expected.MaxTelemetrySamples;

    private static bool SameLimits(CaptureIsolationLimitSnapshot actual, CaptureIsolationLimitSnapshot expected) =>
        actual.MaxControlFrameBytes == expected.MaxControlFrameBytes &&
        actual.MaxPayloadBytes == expected.MaxPayloadBytes &&
        actual.MaxPendingCommands == expected.MaxPendingCommands &&
        actual.MaxTelemetrySamples == expected.MaxTelemetrySamples;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static HelperBoundaryException Failure(string code)
    {
        _ = Marshal.GetLastPInvokeError();
        return new(code);
    }

    internal static async Task<byte[]> ReadMessageAsync(PipeStream pipe, int maximumBytes, CancellationToken token)
    {
        var result = new byte[maximumBytes];
        var offset = 0;
        do
        {
            if (offset == result.Length)
                throw Failure("helper.windows.bootstrap.frame-too-large");
            var read = await pipe.ReadAsync(result.AsMemory(offset), token).ConfigureAwait(false);
            if (read == 0)
                throw Failure("helper.windows.bootstrap.transport-broken");
            offset += read;
        }
        while (!pipe.IsMessageComplete);
        return result.AsSpan(0, offset).ToArray();
    }

    internal static async Task WriteMessageAsync(PipeStream pipe, byte[] frame, CancellationToken token)
    {
        await pipe.WriteAsync(frame, token).ConfigureAwait(false);
        await pipe.FlushAsync(token).ConfigureAwait(false);
    }

    private static long GetCreationFileTime(SafeFileHandle process)
    {
        if (!GetProcessTimes(process, out var creation, out _, out _, out _))
            throw Failure("helper.windows.bootstrap.parent-identity-failed");
        return creation.ToLong();
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileMappingW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileMapping(nint file, nint security, uint protect, uint maximumHigh, uint maximumLow, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint MapViewOfFile(SafeFileHandle mapping, uint access, uint offsetHigh, uint offsetLow, nuint bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnmapViewOfFile(nint address);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateHandle(nint sourceProcess, SafeFileHandle source, nint targetProcess, out SafeFileHandle target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentProcessId();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeFileHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetProcessId(SafeFileHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(SafeFileHandle process, out FileTime creation, out FileTime exit, out FileTime kernel, out FileTime user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafePipeHandle CreateNamedPipe(
        string name,
        uint openMode,
        uint pipeMode,
        uint maximumInstances,
        uint outputBufferSize,
        uint inputBufferSize,
        uint defaultTimeout,
        ref SecurityAttributes securityAttributes);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor,
        uint stringSecurityDescriptorRevision,
        out nint securityDescriptor,
        out uint securityDescriptorSize);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal uint Length;
        internal nint SecurityDescriptor;
        internal int InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        internal uint Low;
        internal uint High;
        internal readonly long ToLong() => unchecked((long)(((ulong)High << 32) | Low));
    }
}

internal sealed partial class WindowsBootstrapHelperSession : IAsyncDisposable
{
    private const uint FileMapRead = 0x0004;
    private const uint HandleFlagInherit = 0x00000001;
    private const uint WaitTimeout = 258;
    private readonly NamedPipeClientStream _pipe;

    private WindowsBootstrapHelperSession(NamedPipeClientStream pipe) => _pipe = pipe;

    internal static string ProtocolRejectionCode(string stage, ProtocolErrorCode code) =>
        stage == "descriptor-decode" && code == ProtocolErrorCode.AuthenticationFailed
            ? "helper.windows.bootstrap.descriptor-rejected"
            : "helper.windows.bootstrap.protocol-rejected";

    internal static async Task<WindowsBootstrapHelperSession> ConnectAsync(
        nint sectionHandle,
        nint parentProcessHandle,
        int sectionBytes,
        WindowsBootstrapFixtureProfile profile,
        CancellationToken cancellationToken,
        bool abortAfterHelloForFixture = false)
    {
        profile.Validate();
        if (sectionHandle is 0 or -1 || parentProcessHandle is 0 or -1 ||
            sectionBytes <= WindowsBootstrapTransportBoundary.SecretBytes ||
            sectionBytes > profile.ProtocolLimits.MaxFrameBytes + WindowsBootstrapTransportBoundary.SecretBytes)
        {
            throw new HelperBoundaryException("helper.windows.bootstrap.coordinates-invalid");
        }
        using var section = new SafeFileHandle(sectionHandle, ownsHandle: true);
        using var parent = new SafeFileHandle(parentProcessHandle, ownsHandle: true);
        if (!SetHandleInformation(section, HandleFlagInherit, 0) ||
            !SetHandleInformation(parent, HandleFlagInherit, 0))
        {
            throw new HelperBoundaryException("helper.windows.bootstrap.inheritance-clear-failed");
        }
        var view = MapViewOfFile(section, FileMapRead, 0, 0, (nuint)sectionBytes);
        if (view == nint.Zero)
            throw new HelperBoundaryException("helper.windows.bootstrap.section-map-failed");
        var bytes = new byte[sectionBytes];
        byte[]? secret = null;
        byte[]? descriptorFrame = null;
        NamedPipeClientStream? pipe = null;
        var stage = "section-read";
        try
        {
            Marshal.Copy(view, bytes, 0, bytes.Length);
            secret = bytes.AsSpan(0, WindowsBootstrapTransportBoundary.SecretBytes).ToArray();
            descriptorFrame = bytes.AsSpan(WindowsBootstrapTransportBoundary.SecretBytes).ToArray();
            stage = "descriptor-decode";
            BootstrapLaunchDescriptor descriptor;
            using (var descriptorSession = new AuthenticatedProtocolSession(
                secret,
                ProtocolChannel.Telemetry,
                ProtocolDirection.ParentToHelper,
                profile.ProtocolLimits))
            {
                descriptor = descriptorSession.Decode<BootstrapLaunchDescriptor>(
                    descriptorFrame,
                    ProtocolMessageKind.Hello);
            }
            stage = "parent-proof";
            var parentId = GetProcessId(parent);
            if (parentId == 0 || parentId != descriptor.ParentProcessId ||
                GetCreationFileTime(parent) != descriptor.ParentCreationFileTime ||
                WaitForSingleObject(parent, 0) != WaitTimeout)
            {
                throw new HelperBoundaryException("helper.windows.bootstrap.parent-mismatch");
            }

            stage = "pipe-create";
            pipe = new NamedPipeClientStream(
                ".",
                descriptor.Endpoint,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(profile.HandshakeBudget);
            stage = "pipe-connect";
            await pipe.ConnectAsync(budget.Token).ConfigureAwait(false);
            pipe.ReadMode = PipeTransmissionMode.Message;
            stage = "server-proof";
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverId) || serverId != parentId)
                throw new HelperBoundaryException("helper.windows.bootstrap.parent-mismatch");

            stage = "handshake";
            using var protocol = new HelperBootstrapProtocol(secret, profile.ProtocolLimits);
            var helperHello = new HelperBootstrapHello(
                descriptor.Correlation,
                descriptor.ProtocolVersion,
                descriptor.SchemaVersion,
                descriptor.Architecture,
                descriptor.PackageManifestDigest,
                descriptor.HelperPayloadDigest,
                descriptor.EffectiveLimits);
            var helloFrame = protocol.CreateHelperHello(helperHello);
            try
            {
                await WindowsBootstrapTransportBoundary.WriteMessageAsync(pipe, helloFrame, budget.Token)
                    .ConfigureAwait(false);
            }
            finally { CryptographicOperations.ZeroMemory(helloFrame); }
            if (abortAfterHelloForFixture)
                throw new HelperBoundaryException("helper.windows.bootstrap.fixture-abort");
            var parentFrame = await WindowsBootstrapTransportBoundary.ReadMessageAsync(
                pipe,
                profile.ProtocolLimits.MaxFrameBytes,
                budget.Token).ConfigureAwait(false);
            ParentBootstrapHello parentHello;
            try
            {
                parentHello = protocol.AcceptParentHello(parentFrame, descriptor.Correlation);
            }
            finally { CryptographicOperations.ZeroMemory(parentFrame); }
            EnsureParentHello(descriptor, parentHello);
            var ack = new HelperBootstrapAck(
                descriptor.Correlation,
                descriptor.ProtocolVersion,
                descriptor.SchemaVersion,
                descriptor.Architecture,
                descriptor.PackageManifestDigest,
                descriptor.ParentPayloadDigest,
                descriptor.HelperPayloadDigest,
                descriptor.EffectiveLimits);
            var ackFrame = protocol.CreateHelperAck(ack);
            try
            {
                await WindowsBootstrapTransportBoundary.WriteMessageAsync(pipe, ackFrame, budget.Token)
                    .ConfigureAwait(false);
            }
            finally { CryptographicOperations.ZeroMemory(ackFrame); }
            var result = new WindowsBootstrapHelperSession(pipe);
            pipe = null;
            return result;
        }
        catch (HelperBoundaryException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (CaptureProtocolException exception)
        {
            throw new HelperBoundaryException(
                ProtocolRejectionCode(stage, exception.Diagnostic.Code));
        }
        catch (UnauthorizedAccessException)
        {
            throw new HelperBoundaryException("helper.windows.bootstrap.access-denied");
        }
        catch (ArgumentException)
        {
            throw new HelperBoundaryException("helper.windows.bootstrap.argument-invalid");
        }
        catch (IOException)
        {
            throw new HelperBoundaryException("helper.windows.bootstrap.transport-broken");
        }
        catch { throw new HelperBoundaryException($"helper.windows.bootstrap.{stage}-failed"); }
        finally
        {
            pipe?.Dispose();
            _ = UnmapViewOfFile(view);
            CryptographicOperations.ZeroMemory(bytes);
            if (secret is not null) CryptographicOperations.ZeroMemory(secret);
            if (descriptorFrame is not null) CryptographicOperations.ZeroMemory(descriptorFrame);
        }
    }

    internal async Task WaitForFenceAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        try
        {
            while (await _pipe.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) != 0) { }
        }
        catch (IOException) { }
    }

    public ValueTask DisposeAsync()
    {
        _pipe.Dispose();
        return ValueTask.CompletedTask;
    }

    internal static bool IsHandleUnavailable(nint handle) =>
        handle is 0 or -1 || !GetHandleInformation(handle, out _);

    private static void EnsureParentHello(BootstrapLaunchDescriptor descriptor, ParentBootstrapHello hello)
    {
        if (hello.Correlation != descriptor.Correlation || hello.ProtocolVersion != descriptor.ProtocolVersion ||
            hello.SchemaVersion != descriptor.SchemaVersion || hello.Architecture != descriptor.Architecture ||
            hello.PackageManifestDigest != descriptor.PackageManifestDigest ||
            hello.ParentPayloadDigest != descriptor.ParentPayloadDigest ||
            hello.HelperPayloadDigest != descriptor.HelperPayloadDigest ||
            hello.EffectiveLimits != descriptor.EffectiveLimits)
        {
            throw new HelperBoundaryException("helper.windows.bootstrap.claim-mismatch");
        }
    }

    private static long GetCreationFileTime(SafeFileHandle process)
    {
        if (!GetProcessTimes(process, out var creation, out _, out _, out _))
            throw new HelperBoundaryException("helper.windows.bootstrap.parent-identity-failed");
        return creation.ToLong();
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetHandleInformation(nint handle, out uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint MapViewOfFile(SafeFileHandle mapping, uint access, uint offsetHigh, uint offsetLow, nuint bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnmapViewOfFile(nint address);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetProcessId(SafeFileHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(SafeFileHandle process, out FileTime creation, out FileTime exit, out FileTime kernel, out FileTime user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        internal uint Low;
        internal uint High;
        internal readonly long ToLong() => unchecked((long)(((ulong)High << 32) | Low));
    }
}
