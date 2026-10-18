using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

namespace WatchDog.CaptureIsolation.Windows.OwTransferPreflight;

internal static partial class OwnerDeathWitness
{
    private const uint WriterProcessAccess = 0x00101001;
    private const uint AbruptOwnerExitCode = 137;
    private const uint WriterFallbackExitCode = 138;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private const uint WaitFailed = uint.MaxValue;
    private const int MaximumCustodyEvents = 4_096;
    private static readonly TimeSpan DeathTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);
    private static readonly JsonSerializerOptions ReceiptJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private static readonly string[] HandleOrder =
    [
        "fixture-base",
        "run-root",
        "trusted-parent",
        "private-root",
        "frames",
        "anchor",
    ];

    internal static async Task<int> RunControllerAsync()
    {
        var state = new ControllerState();
        Exception? failure = null;
        try
        {
            await ExecuteControllerAsync(state).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
            state.Failure = $"{exception.GetType().Name}: {exception.Message}";
        }
        finally
        {
            await TerminateAndReapNonThrowingAsync(state).ConfigureAwait(false);
            if (state.ManifestValidated && state.OwnerExitConfirmed && state.WriterExitConfirmed &&
                !state.CleanupAttempted)
            {
                TryCleanupExact(state);
            }
            state.DisposeRetainedHandles();
        }

        var succeeded = failure is null && state.Failure is null && state.BarrierArmed &&
            state.OwnerExitConfirmed && state.WriterExitConfirmed &&
            !state.WriterFallbackTerminationUsed && state.CleanupSucceeded;
        var receipt = new
        {
            schema = "watchdog.ow-transfer-owner-death.v1",
            scenario = "owner-death",
            succeeded,
            fixtureRoot = state.Manifest?.Handles.Single(item => item.Resource == "run-root").Observation.FinalPath,
            writerJobHandleHeldByWitness = false,
            controllerPid = Environment.ProcessId,
            ownerPid = state.OwnerPid,
            writerPid = state.WriterPid,
            ownerCreationFileTime = state.OwnerCreationFileTime,
            writerCreationFileTime = state.WriterCreationFileTime,
            ownerExitFileTime = state.OwnerExitFileTime,
            writerExitFileTime = state.WriterExitFileTime,
            ownerExitCode = state.OwnerExitCode,
            writerExitCode = state.WriterExitCode,
            manifestReceivedAtUtc = state.ManifestReceivedAtUtc,
            manifestAckedAtUtc = state.ManifestAckedAtUtc,
            barrierArmedAtUtc = state.BarrierArmedAtUtc,
            ownerTerminateIssuedAtUtc = state.OwnerTerminateIssuedAtUtc,
            ownerExitObservedAtUtc = state.OwnerExitObservedAtUtc,
            writerExitObservedAtUtc = state.WriterExitObservedAtUtc,
            ownerLiveImmediatelyBeforeDeath = state.OwnerLiveImmediatelyBeforeDeath,
            writerLiveImmediatelyBeforeOwnerDeath = state.WriterLiveImmediatelyBeforeOwnerDeath,
            ownerExitConfirmed = state.OwnerExitConfirmed,
            writerExitConfirmed = state.WriterExitConfirmed,
            writerFallbackTerminationUsed = state.WriterFallbackTerminationUsed,
            exactCleanupSucceeded = state.CleanupSucceeded,
            artifactsPreserved = !state.CleanupSucceeded,
            nativeAuthorityProven = false,
            nativeDurabilityProven = false,
            completeHandleInventoryProven = false,
            trustedBaseAdmissionProven = false,
            zeroExtraInheritanceProven = false,
            b1WriteReleaseIssued = false,
            b1JournalWritesPerformed = false,
            failure = state.Failure,
            custodyPrefix = state.CustodyPrefix,
        };
        Console.WriteLine(JsonSerializer.Serialize(receipt, ReceiptJson));
        return succeeded ? 0 : 1;
    }

    internal static async Task NeverReturnOwnerBarrierAsync(
        string pipeName,
        string nonce,
        int controllerPid,
        SafeHandle writerProcess,
        IReadOnlyDictionary<string, SafeFileHandle> handles,
        IReadOnlyList<CustodyEvent> custodyPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);
        if (controllerPid <= 0)
            throw new ArgumentOutOfRangeException(nameof(controllerPid));
        ArgumentNullException.ThrowIfNull(writerProcess);
        ArgumentNullException.ThrowIfNull(handles);
        ArgumentNullException.ThrowIfNull(custodyPrefix);
        if (writerProcess.IsInvalid || writerProcess.IsClosed)
            throw new InvalidOperationException("The retained W process handle is unavailable.");
        RequireExactHandleKeys(handles);
        ValidateCustodyPrefix(custodyPrefix);

        var writerIdentity = ObserveProcess(writerProcess);
        if (writerIdentity.Exited)
            throw new InvalidOperationException("W exited before the owner-death witness barrier.");

        var offers = new WitnessHandleOffer[HandleOrder.Length];
        for (var index = 0; index < HandleOrder.Length; index++)
        {
            var resource = HandleOrder[index];
            var handle = handles[resource];
            var observation = NativeMethods.Observe(handle, resource);
            var access = AccessFor(resource);
            NativeMethods.RequireObservation(
                observation,
                access,
                IsDirectoryResource(resource),
                observation.FinalPath);
            offers[index] = new(
                resource,
                handle.DangerousGetHandle().ToInt64(),
                access,
                observation);
        }
        ValidateObservationGraph(offers);

        var manifest = new WitnessManifest(
            "owner-witness-manifest",
            nonce,
            Guid.NewGuid(),
            Environment.ProcessId,
            controllerPid,
            writerProcess.DangerousGetHandle().ToInt64(),
            checked((int)writerIdentity.ProcessId),
            writerIdentity.CreationFileTime,
            offers,
            custodyPrefix.ToArray());

        using var connectTimeout = new CancellationTokenSource(ProtocolConstants.ConnectTimeout);
        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
        if (!NativeMethods.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid) ||
            serverPid != checked((uint)controllerPid))
        {
            throw new InvalidDataException("The owner-death pipe server is not exact P.");
        }

        await using var channel = new WitnessJsonChannel(pipe);
        await channel.SendAsync(manifest, CancellationToken.None).ConfigureAwait(false);
        var ack = await channel.ReceiveAsync<WitnessAck>(CancellationToken.None).ConfigureAwait(false);
        RequireAck(ack, "witness-manifest-ack", manifest, controllerPid);
        await channel.SendAsync(
            ack with { Type = "owner-barrier-armed", Pid = Environment.ProcessId },
            CancellationToken.None).ConfigureAwait(false);

        // Keep the exact W process, all six file objects, and the custody prefix rooted while O
        // waits to be terminated externally. No W job handle crosses this boundary.
        var retentionAnchor = new object[] { writerProcess, handles, custodyPrefix, manifest };
        await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        GC.KeepAlive(retentionAnchor);
        throw new InvalidOperationException("The abrupt-owner-death barrier unexpectedly resumed.");
    }

    private static async Task ExecuteControllerAsync(ControllerState state)
    {
        var pipeName = $"watchdog-owner-death-{Guid.NewGuid():N}";
        var nonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        var executable = Environment.ProcessPath ??
            throw new InvalidOperationException("The current executable path is unavailable.");
        var arguments = BuildOwnerArguments(executable, pipeName, nonce);
        try
        {
            state.Owner = NativeChildLaunch.CreateSuspended(executable, arguments);
        }
        catch (NativeLaunchFailure failure)
        {
            // Ownership already crossed CreateProcessW. Retain the exact launch so the outer
            // failure path can terminate and reap it instead of losing post-birth custody.
            state.Owner = failure.Launch;
            state.OwnerPid = failure.Launch.Id;
            state.OwnerCreationFileTime = failure.Launch.CreationFileTime;
            throw;
        }
        state.OwnerPid = state.Owner.Id;
        state.OwnerCreationFileTime = state.Owner.CreationFileTime;
        state.Owner.Resume();

        using (var connectTimeout = new CancellationTokenSource(ProtocolConstants.ConnectTimeout))
            await pipe.WaitForConnectionAsync(connectTimeout.Token).ConfigureAwait(false);
        if (!NativeMethods.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientPid) ||
            clientPid != checked((uint)state.OwnerPid.Value))
        {
            throw new InvalidDataException("The owner-death pipe client is not exact O.");
        }

        await using var channel = new WitnessJsonChannel(pipe);
        var manifest = await channel.ReceiveAsync<WitnessManifest>(CancellationToken.None)
            .ConfigureAwait(false);
        state.ManifestReceivedAtUtc = DateTimeOffset.UtcNow;
        AdoptAndValidateManifest(state, manifest, nonce);

        var ack = new WitnessAck(
            "witness-manifest-ack",
            nonce,
            manifest.ManifestId,
            Environment.ProcessId,
            manifest.OwnerPid,
            manifest.WriterPid,
            manifest.Handles.Count);
        await channel.SendAsync(ack, CancellationToken.None).ConfigureAwait(false);
        state.ManifestAckedAtUtc = DateTimeOffset.UtcNow;
        var armed = await channel.ReceiveAsync<WitnessAck>(CancellationToken.None).ConfigureAwait(false);
        RequireAck(armed, "owner-barrier-armed", manifest, Environment.ProcessId);
        state.BarrierArmed = true;
        state.BarrierArmedAtUtc = DateTimeOffset.UtcNow;

        state.OwnerLiveImmediatelyBeforeDeath = !state.Owner.HasExited;
        state.WriterLiveImmediatelyBeforeOwnerDeath = !ObserveProcess(state.WriterProcess!).Exited;
        if (!state.OwnerLiveImmediatelyBeforeDeath || !state.WriterLiveImmediatelyBeforeOwnerDeath)
            throw new InvalidOperationException("O or W exited before abrupt O termination.");

        state.OwnerTerminateIssuedAtUtc = DateTimeOffset.UtcNow;
        TerminateExact(state.Owner.SafeHandle, AbruptOwnerExitCode, "O");
        await state.Owner.WaitForExitAsync(DeathTimeout).ConfigureAwait(false);
        CaptureOwnerExit(state);

        if (!await WaitForProcessAsync(state.WriterProcess!, DeathTimeout).ConfigureAwait(false))
        {
            state.WriterFallbackTerminationUsed = true;
            TerminateExact(state.WriterProcess!, WriterFallbackExitCode, "W fallback");
            if (!await WaitForProcessAsync(state.WriterProcess!, DeathTimeout).ConfigureAwait(false))
                throw new TimeoutException("Exact W remained live after bounded fallback termination.");
            CaptureWriterExit(state);
            throw new InvalidOperationException("W required exact fallback termination after abrupt O death.");
        }
        CaptureWriterExit(state);

        CleanupExact(state);
    }

    private static void AdoptAndValidateManifest(
        ControllerState state,
        WitnessManifest manifest,
        string expectedNonce)
    {
        if (!StringComparer.Ordinal.Equals(manifest.Type, "owner-witness-manifest") ||
            !StringComparer.Ordinal.Equals(manifest.Nonce, expectedNonce) ||
            manifest.ManifestId == Guid.Empty || manifest.OwnerPid != state.OwnerPid ||
            manifest.ControllerPid != Environment.ProcessId || manifest.WriterPid <= 0 ||
            manifest.WriterProcessCoordinate is 0 or -1 || manifest.WriterCreationFileTime <= 0 ||
            manifest.Handles.Count != HandleOrder.Length)
        {
            throw new InvalidDataException("The O witness manifest header is invalid.");
        }
        ValidateCustodyPrefix(manifest.CustodyPrefix);

        var candidate = DuplicateWriterProcess(
            state.Owner!.SafeHandle,
            manifest.WriterProcessCoordinate);
        try
        {
            var capability = NativeMethods.ObserveKernelHandle(candidate);
            var writerIdentity = ObserveProcess(candidate);
            if (capability.GrantedAccess != WriterProcessAccess || capability.Inheritable ||
                writerIdentity.ProcessId != checked((uint)manifest.WriterPid) ||
                writerIdentity.CreationFileTime != manifest.WriterCreationFileTime ||
                writerIdentity.Exited)
            {
                throw new InvalidDataException("The retained W process identity or rights do not match O's manifest.");
            }
            state.WriterProcess = candidate;
        }
        catch
        {
            candidate.Dispose();
            throw;
        }
        state.WriterPid = manifest.WriterPid;
        state.WriterCreationFileTime = manifest.WriterCreationFileTime;

        var orderedOffers = manifest.Handles.ToArray();
        ValidateObservationGraph(orderedOffers);
        for (var index = 0; index < HandleOrder.Length; index++)
        {
            var resource = HandleOrder[index];
            var offer = orderedOffers[index];
            if (!StringComparer.Ordinal.Equals(offer.Resource, resource) ||
                offer.Coordinate is 0 or -1 || offer.DesiredAccess != AccessFor(resource))
            {
                throw new InvalidDataException($"The witness offer for {resource} is invalid.");
            }

            var adopted = NativeMethods.DuplicateFromProcess(
                state.Owner.SafeHandle,
                offer.Coordinate,
                offer.DesiredAccess);
            try
            {
                var observation = NativeMethods.Observe(adopted, resource);
                NativeMethods.RequireObservation(
                    observation,
                    offer.DesiredAccess,
                    IsDirectoryResource(resource),
                    offer.Observation.FinalPath);
                NativeMethods.RequireSameObject(offer.Observation, observation);
                state.FileHandles.Add(resource, adopted);
            }
            catch
            {
                adopted.Dispose();
                throw;
            }
        }

        state.Manifest = manifest;
        state.CustodyPrefix = manifest.CustodyPrefix.ToArray();
        state.ManifestValidated = true;
    }

    private static void ValidateObservationGraph(IReadOnlyList<WitnessHandleOffer> offers)
    {
        if (offers.Count != HandleOrder.Length)
            throw new InvalidDataException("The witness manifest does not contain six handles.");

        var byResource = new Dictionary<string, WitnessHandleOffer>(StringComparer.Ordinal);
        foreach (var offer in offers)
        {
            if (!byResource.TryAdd(offer.Resource, offer) ||
                offer.DesiredAccess != AccessFor(offer.Resource))
            {
                throw new InvalidDataException("The witness handle manifest is duplicated or unknown.");
            }
            NativeMethods.RequireObservation(
                offer.Observation,
                offer.DesiredAccess,
                IsDirectoryResource(offer.Resource),
                offer.Observation.FinalPath);
        }
        RequireExactHandleKeys(byResource);

        var fixtureBase = byResource["fixture-base"].Observation;
        var runRoot = byResource["run-root"].Observation;
        var trustedParent = byResource["trusted-parent"].Observation;
        var privateRoot = byResource["private-root"].Observation;
        var frames = byResource["frames"].Observation;
        var anchor = byResource["anchor"].Observation;
        var expectedBase = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "WatchDog.CaptureIsolation.Windows.OwTransferPreflight");
        if (!NativeMethods.PathsEqual(fixtureBase.FinalPath, expectedBase) ||
            !ParentPathEquals(runRoot.FinalPath, fixtureBase.FinalPath) ||
            !ParentPathEquals(trustedParent.FinalPath, runRoot.FinalPath) ||
            !ParentPathEquals(privateRoot.FinalPath, trustedParent.FinalPath) ||
            !ParentPathEquals(frames.FinalPath, privateRoot.FinalPath) ||
            !ParentPathEquals(anchor.FinalPath, privateRoot.FinalPath) ||
            !StringComparer.OrdinalIgnoreCase.Equals(LeafName(trustedParent.FinalPath), "trusted-parent") ||
            !StringComparer.OrdinalIgnoreCase.Equals(LeafName(frames.FinalPath), "frames.bin") ||
            !StringComparer.OrdinalIgnoreCase.Equals(LeafName(anchor.FinalPath), "anchor.bin") ||
            !LeafName(runRoot.FinalPath).StartsWith("run-", StringComparison.Ordinal) ||
            !LeafName(privateRoot.FinalPath).StartsWith("writer-private-", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The witness handle paths do not form the exact fixture chain.");
        }

        var identities = offers.Select(offer =>
            $"{offer.Observation.VolumeSerialNumber:X16}:{offer.Observation.FileId}").ToHashSet(
                StringComparer.Ordinal);
        if (identities.Count != HandleOrder.Length)
            throw new InvalidDataException("The witness handle manifest aliases retained objects.");
    }

    private static void ValidateCustodyPrefix(IReadOnlyList<CustodyEvent> custodyPrefix)
    {
        if (custodyPrefix.Count is 0 or > MaximumCustodyEvents)
            throw new InvalidDataException("The witness custody prefix is empty or unbounded.");
        DateTimeOffset previousTimestamp = default;
        for (var index = 0; index < custodyPrefix.Count; index++)
        {
            var entry = custodyPrefix[index];
            if (entry.Sequence != index + 1 || string.IsNullOrWhiteSpace(entry.Event) ||
                (index > 0 && entry.TimestampUtc < previousTimestamp))
            {
                throw new InvalidDataException("The witness custody prefix is not ordered and complete.");
            }
            previousTimestamp = entry.TimestampUtc;
        }
    }

    private static void RequireAck(
        WitnessAck ack,
        string expectedType,
        WitnessManifest manifest,
        int expectedControllerPid)
    {
        var expectedPid = expectedType == "witness-manifest-ack"
            ? expectedControllerPid
            : manifest.OwnerPid;
        if (!StringComparer.Ordinal.Equals(ack.Type, expectedType) ||
            !StringComparer.Ordinal.Equals(ack.Nonce, manifest.Nonce) ||
            ack.ManifestId != manifest.ManifestId || ack.Pid != expectedPid ||
            ack.OwnerPid != manifest.OwnerPid || ack.WriterPid != manifest.WriterPid ||
            ack.HandleCount != HandleOrder.Length)
        {
            throw new InvalidDataException($"The {expectedType} witness acknowledgment is invalid.");
        }
    }

    private static IReadOnlyList<string> BuildOwnerArguments(
        string executable,
        string pipeName,
        string nonce)
    {
        var arguments = new List<string>();
        if (StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(executable), "dotnet.exe"))
            arguments.Add(Assembly.GetExecutingAssembly().Location);
        arguments.Add("--owner-child");
        arguments.Add(pipeName);
        arguments.Add(nonce);
        arguments.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return arguments;
    }

    private static SafeProcessHandle DuplicateWriterProcess(
        SafeHandle ownerProcess,
        long writerCoordinate,
        uint desiredAccess = WriterProcessAccess)
    {
        var retained = new SafeProcessHandle(nint.Zero, ownsHandle: true);
        var ownerAdded = false;
        try
        {
            ownerProcess.DangerousAddRef(ref ownerAdded);
            var duplicated = DuplicateHandle(
                ownerProcess.DangerousGetHandle(),
                new nint(writerCoordinate),
                GetCurrentProcess(),
                out var rawWriter,
                desiredAccess,
                inheritHandle: false,
                options: 0);
            // This preallocated slot adoption must remain the first operation after
            // DuplicateHandle returns. Do not allocate, log, or query first.
            Marshal.InitHandle(retained, rawWriter);
            if (!duplicated || retained.IsInvalid)
            {
                retained.Dispose();
                throw NativeFailure("DuplicateHandle(O-to-P W process)");
            }
            return retained;
        }
        catch
        {
            retained.Dispose();
            throw;
        }
        finally
        {
            if (ownerAdded)
                ownerProcess.DangerousRelease();
        }
    }

    private static ProcessIdentity ObserveProcess(SafeHandle process)
    {
        var processId = GetProcessId(process);
        if (processId == 0 ||
            !GetProcessTimes(process, out var creation, out var exit, out _, out _))
        {
            throw NativeFailure("GetProcess identity");
        }
        var wait = WaitForSingleObject(process, 0);
        var exited = wait switch
        {
            WaitObject0 => true,
            WaitTimeout => false,
            WaitFailed => throw NativeFailure("WaitForSingleObject"),
            _ => throw new InvalidOperationException($"Unexpected process wait result {wait}."),
        };
        int? exitCode = null;
        if (exited)
        {
            if (!GetExitCodeProcess(process, out var nativeExitCode))
                throw NativeFailure("GetExitCodeProcess");
            exitCode = unchecked((int)nativeExitCode);
        }
        return new(processId, creation.ToLong(), exit.ToLong(), exited, exitCode);
    }

    private static async Task<bool> WaitForProcessAsync(SafeHandle process, TimeSpan timeout)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            if (ObserveProcess(process).Exited)
                return true;
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            if (elapsed >= timeout)
                return false;
            var remaining = timeout - elapsed;
            await Task.Delay(remaining < PollInterval ? remaining : PollInterval).ConfigureAwait(false);
        }
    }

    private static void TerminateExact(SafeHandle process, uint exitCode, string role)
    {
        if (ObserveProcess(process).Exited)
            return;
        if (!TerminateProcess(process, exitCode))
            throw NativeFailure($"TerminateProcess({role})");
    }

    private static void CaptureOwnerExit(ControllerState state)
    {
        var identity = ObserveProcess(state.Owner!.SafeHandle);
        if (!identity.Exited || identity.ProcessId != checked((uint)state.OwnerPid!.Value) ||
            (state.OwnerCreationFileTime is > 0 && identity.CreationFileTime != state.OwnerCreationFileTime))
        {
            throw new InvalidDataException("Exact O exit identity was not retained.");
        }
        state.OwnerExitConfirmed = true;
        state.OwnerExitCode = identity.ExitCode;
        state.OwnerExitFileTime = identity.ExitFileTime;
        state.OwnerExitObservedAtUtc = DateTimeOffset.UtcNow;
    }

    private static void CaptureWriterExit(ControllerState state)
    {
        var identity = ObserveProcess(state.WriterProcess!);
        if (!identity.Exited || identity.ProcessId != checked((uint)state.WriterPid!.Value) ||
            identity.CreationFileTime != state.WriterCreationFileTime)
        {
            throw new InvalidDataException("Exact W exit identity was not retained.");
        }
        state.WriterExitConfirmed = true;
        state.WriterExitCode = identity.ExitCode;
        state.WriterExitFileTime = identity.ExitFileTime;
        state.WriterExitObservedAtUtc = DateTimeOffset.UtcNow;
    }

    private static async Task TerminateAndReapNonThrowingAsync(ControllerState state)
    {
        if (state.Owner is not null)
        {
            try
            {
                if (!state.Owner.HasExited)
                {
                    state.OwnerTerminateIssuedAtUtc ??= DateTimeOffset.UtcNow;
                    TerminateExact(state.Owner.SafeHandle, AbruptOwnerExitCode, "O cleanup");
                }
                await state.Owner.WaitForExitAsync(DeathTimeout).ConfigureAwait(false);
                CaptureOwnerExit(state);
            }
            catch (Exception exception)
            {
                state.Failure ??= $"owner-reap: {exception.Message}";
            }
        }

        if (state.WriterProcess is not null)
        {
            try
            {
                if (!await WaitForProcessAsync(state.WriterProcess, DeathTimeout).ConfigureAwait(false))
                {
                    state.WriterFallbackTerminationUsed = true;
                    TerminateExact(state.WriterProcess, WriterFallbackExitCode, "W cleanup fallback");
                    if (!await WaitForProcessAsync(state.WriterProcess, DeathTimeout).ConfigureAwait(false))
                        throw new TimeoutException("Exact W cleanup fallback did not reap W.");
                }
                CaptureWriterExit(state);
            }
            catch (Exception exception)
            {
                state.Failure ??= $"writer-reap: {exception.Message}";
            }
        }
    }

    private static void TryCleanupExact(ControllerState state)
    {
        try
        {
            CleanupExact(state);
        }
        catch (Exception exception)
        {
            state.Failure ??= $"exact-cleanup: {exception.Message}";
        }
    }

    private static void CleanupExact(ControllerState state)
    {
        if (!state.ManifestValidated || !state.OwnerExitConfirmed || !state.WriterExitConfirmed)
            throw new InvalidOperationException("Exact cleanup requires validated dead O and W identities.");
        state.CleanupAttempted = true;
        var manifest = state.Manifest!;
        var observations = manifest.Handles.ToDictionary(
            offer => offer.Resource,
            offer => offer.Observation,
            StringComparer.Ordinal);

        if (manifest.Handles.Count is not (3 or 6) ||
            !new[] { "fixture-base", "run-root", "trusted-parent" }.All(observations.ContainsKey))
            throw new InvalidDataException("Cleanup requires the exact three- or six-object fixture schema.");

        if (manifest.Handles.Count == 6)
        {
            using (var rootDelete = NativeMethods.OpenRelativeForDelete(
                       state.FileHandles["trusted-parent"],
                       LeafName(observations["private-root"].FinalPath),
                       directory: true))
            {
                RequireRetainedCleanupObject(state, "private-root", observations["private-root"], rootDelete);
                using (var framesDelete = NativeMethods.OpenRelativeForDelete(
                           rootDelete,
                           LeafName(observations["frames"].FinalPath),
                           directory: false))
                {
                    RequireRetainedCleanupObject(state, "frames", observations["frames"], framesDelete);
                    NativeMethods.MarkDeleteOnClose(framesDelete, "frames-cleanup");
                    state.DisposeFileHandle("frames");
                }
                using (var anchorDelete = NativeMethods.OpenRelativeForDelete(
                           rootDelete,
                           LeafName(observations["anchor"].FinalPath),
                           directory: false))
                {
                    RequireRetainedCleanupObject(state, "anchor", observations["anchor"], anchorDelete);
                    NativeMethods.MarkDeleteOnClose(anchorDelete, "anchor-cleanup");
                    state.DisposeFileHandle("anchor");
                }
                NativeMethods.MarkDeleteOnClose(rootDelete, "private-root-cleanup");
                state.DisposeFileHandle("private-root");
            }
        }

        using (var trustedParentDelete = NativeMethods.OpenRelativeForDelete(
                   state.FileHandles["run-root"],
                   LeafName(observations["trusted-parent"].FinalPath),
                   directory: true))
        {
            RequireRetainedCleanupObject(state, "trusted-parent", observations["trusted-parent"], trustedParentDelete);
            NativeMethods.MarkDeleteOnClose(trustedParentDelete, "trusted-parent-cleanup");
            state.DisposeFileHandle("trusted-parent");
        }

        using (var runRootDelete = NativeMethods.OpenRelativeForDelete(
                   state.FileHandles["fixture-base"],
                   LeafName(observations["run-root"].FinalPath),
                   directory: true))
        {
            RequireRetainedCleanupObject(state, "run-root", observations["run-root"], runRootDelete);
            NativeMethods.MarkDeleteOnClose(runRootDelete, "run-root-cleanup");
            state.DisposeFileHandle("run-root");
        }
        state.CleanupSucceeded = true;
    }

    private static void RequireRetainedCleanupObject(
        ControllerState state, string resource, HandleObservation expected, SafeFileHandle deleteHandle)
    {
        var retained = NativeMethods.Observe(state.FileHandles[resource], resource);
        NativeMethods.RequireSameObject(expected, retained);
        NativeMethods.RequireSameObject(retained, NativeMethods.Observe(deleteHandle, resource + "-cleanup"));
    }

    private static void RequireExactHandleKeys<T>(IReadOnlyDictionary<string, T> handles)
    {
        if (handles.Count != HandleOrder.Length || HandleOrder.Any(key => !handles.ContainsKey(key)))
            throw new InvalidDataException("The owner-death witness requires exactly six retained objects.");
    }

    private static uint AccessFor(string resource) => resource switch
    {
        "fixture-base" or "run-root" or "trusted-parent" => NativeMethods.ParentTransferAccess,
        "private-root" => NativeMethods.RootReadAccess,
        "frames" or "anchor" => NativeMethods.FileReadAccess,
        _ => throw new InvalidDataException($"Unknown witness resource {resource}."),
    };

    private static bool IsDirectoryResource(string resource) =>
        resource is "fixture-base" or "run-root" or "trusted-parent" or "private-root";

    private static bool ParentPathEquals(string child, string expectedParent)
    {
        var parent = Path.GetDirectoryName(child.TrimEnd(Path.DirectorySeparatorChar));
        return parent is not null && NativeMethods.PathsEqual(parent, expectedParent);
    }

    private static string LeafName(string path) =>
        Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));

    private static InvalidOperationException NativeFailure(string operation)
    {
        var error = Marshal.GetLastPInvokeError();
        return new($"{operation} failed with Win32 error {error}.");
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateHandle(
        nint sourceProcess,
        nint sourceHandle,
        nint targetProcess,
        out nint targetHandle,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcessId", SetLastError = true)]
    private static partial uint GetProcessId(SafeHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(
        SafeHandle process,
        out FileTime creation,
        out FileTime exit,
        out FileTime kernel,
        out FileTime user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(SafeHandle process, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(SafeHandle process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(SafeHandle process, out uint exitCode);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        internal uint Low;
        internal uint High;

        internal readonly long ToLong() => unchecked((long)(((ulong)High << 32) | Low));
    }

    private sealed record WitnessHandleOffer(
        string Resource,
        long Coordinate,
        uint DesiredAccess,
        HandleObservation Observation);

    private sealed record WitnessManifest(
        string Type,
        string Nonce,
        Guid ManifestId,
        int OwnerPid,
        int ControllerPid,
        long WriterProcessCoordinate,
        int WriterPid,
        long WriterCreationFileTime,
        IReadOnlyList<WitnessHandleOffer> Handles,
        IReadOnlyList<CustodyEvent> CustodyPrefix);

    private sealed record WitnessAck(
        string Type,
        string Nonce,
        Guid ManifestId,
        int Pid,
        int OwnerPid,
        int WriterPid,
        int HandleCount);

    private readonly record struct ProcessIdentity(
        uint ProcessId,
        long CreationFileTime,
        long ExitFileTime,
        bool Exited,
        int? ExitCode);

    private sealed class ControllerState
    {
        internal NativeChildLaunch? Owner { get; set; }
        internal SafeProcessHandle? WriterProcess { get; set; }
        internal Dictionary<string, SafeFileHandle> FileHandles { get; } =
            new(StringComparer.Ordinal);
        internal WitnessManifest? Manifest { get; set; }
        internal IReadOnlyList<CustodyEvent> CustodyPrefix { get; set; } = [];
        internal int? OwnerPid { get; set; }
        internal int? WriterPid { get; set; }
        internal long? OwnerCreationFileTime { get; set; }
        internal long? WriterCreationFileTime { get; set; }
        internal long? OwnerExitFileTime { get; set; }
        internal long? WriterExitFileTime { get; set; }
        internal int? OwnerExitCode { get; set; }
        internal int? WriterExitCode { get; set; }
        internal DateTimeOffset? ManifestReceivedAtUtc { get; set; }
        internal DateTimeOffset? ManifestAckedAtUtc { get; set; }
        internal DateTimeOffset? BarrierArmedAtUtc { get; set; }
        internal DateTimeOffset? OwnerTerminateIssuedAtUtc { get; set; }
        internal DateTimeOffset? OwnerExitObservedAtUtc { get; set; }
        internal DateTimeOffset? WriterExitObservedAtUtc { get; set; }
        internal bool ManifestValidated { get; set; }
        internal bool BarrierArmed { get; set; }
        internal bool OwnerLiveImmediatelyBeforeDeath { get; set; }
        internal bool WriterLiveImmediatelyBeforeOwnerDeath { get; set; }
        internal bool OwnerExitConfirmed { get; set; }
        internal bool WriterExitConfirmed { get; set; }
        internal bool WriterFallbackTerminationUsed { get; set; }
        internal bool CleanupAttempted { get; set; }
        internal bool CleanupSucceeded { get; set; }
        internal string? Failure { get; set; }

        internal void DisposeFileHandle(string resource)
        {
            if (FileHandles.Remove(resource, out var handle))
                handle.Dispose();
        }

        internal void DisposeRetainedHandles()
        {
            foreach (var handle in FileHandles.Values)
                handle.Dispose();
            FileHandles.Clear();
            WriterProcess?.Dispose();
            WriterProcess = null;
            Owner?.Dispose();
            Owner = null;
        }
    }

    internal sealed class WitnessJsonChannel : IAsyncDisposable
    {
        private const int MaximumFrameBytes = 512 * 1024;
        private static readonly JsonSerializerOptions JsonOptions =
            new(JsonSerializerDefaults.Web);
        private readonly Stream _stream;

        internal WitnessJsonChannel(Stream stream) =>
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));

        internal async Task SendAsync<T>(T message, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProtocolConstants.MessageTimeout);
            var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
            if (payload.Length is 0 or > MaximumFrameBytes)
                throw new InvalidDataException("The owner-death witness frame is out of bounds.");
            var prefix = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
            await _stream.WriteAsync(prefix, timeout.Token).ConfigureAwait(false);
            await _stream.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
            await _stream.FlushAsync(timeout.Token).ConfigureAwait(false);
        }

        internal async Task<T> ReceiveAsync<T>(CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProtocolConstants.MessageTimeout);
            var prefix = new byte[sizeof(int)];
            await _stream.ReadExactlyAsync(prefix, timeout.Token).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
            if (length is <= 0 or > MaximumFrameBytes)
                throw new InvalidDataException("The owner-death witness peer supplied an invalid frame.");
            var payload = new byte[length];
            await _stream.ReadExactlyAsync(payload, timeout.Token).ConfigureAwait(false);
            return JsonSerializer.Deserialize<T>(payload, JsonOptions) ??
                throw new InvalidDataException("The owner-death witness returned an empty frame.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
