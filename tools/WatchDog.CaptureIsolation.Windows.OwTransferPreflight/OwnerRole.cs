using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace WatchDog.CaptureIsolation.Windows.OwTransferPreflight;

internal static class OwnerRole
{
    private static readonly JsonSerializerOptions ReceiptJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    internal static async Task<int> RunAsync(ScenarioOptions scenario,
        string? witnessPipe = null, string? witnessNonce = null, int controllerPid = 0)
    {
        var state = new OwnerState(scenario);
        Exception? failure = null;
        string? expectedFault = null;
        try
        {
            state.InitializeFixture();
            await ExecuteAsync(state, witnessPipe, witnessNonce, controllerPid).ConfigureAwait(false);
            state.Succeeded = true;
        }
        catch (NativeLaunchFailure exception) when (scenario.Kind == "birth-failure" &&
            exception.InnerException is InjectedBirthQueryFailure)
        {
            expectedFault = "birth-failure";
            state.Succeeded = state.FailedBirthCustody is not null;
            state.RecordFailureNonThrowing("birth-failure-custody-retained", exception.Message);
        }
        catch (ExpectedScenarioFault exception)
        {
            expectedFault = exception.Code;
            state.Succeeded = state.ExpectedFaultSatisfied(exception.Code);
            state.RecordFailureNonThrowing("expected-fault-fail-closed", exception.Code);
        }
        catch (Exception exception)
        {
            failure = exception;
            state.RecordFailureNonThrowing("preflight-failed", exception.Message);
        }
        finally
        {
            await state.StopAndReapChildAsync().ConfigureAwait(false);
            state.DisposeReadHandlesForCleanup();
            state.CleanupExactArtifactsByHandle();
            state.DisposeRemainingHandles();
        }

        var receipt = new
        {
            schema = "watchdog.ow-transfer-preflight.v2",
            succeeded = state.Succeeded && state.CleanupSucceeded && failure is null,
            scenario = scenario.Name,
            expectedFault,
            successObligationsCaptured = state.SuccessObligationsCaptured,
            emergencyCustodyRetained = state.EmergencyCustodyRetained,
            injectedCustodyAppendFailureObserved = state.StoreFailureObserved,
            faultReadPostWriterDeathObserved = state.FaultReadPostDeathObserved,
            stopGateClosedBeforeNativeReturn = state.StopGateClosed,
            delayedCleanupOnlyDeliveryObserved = state.DelayedCleanupDeliveryObserved,
            ackAfterInjectedFault = state.CustodyEvents.Any(entry =>
                entry.Event == "ack-issued" && entry.Resource ==
                    (scenario.Resource ?? (scenario.Kind == "source-reuse" ? "frames" : null))),
            sameFileCoordinateReuseObserved = state.SameFileCoordinateReuseObserved,
            sourceGenerationDriftRejected = state.SourceGenerationDriftRejected,
            launch = state.LaunchEvidence,
            failedBirthCustody = state.FailedBirthCustody,
            runId = state.RunId,
            ownerPid = Environment.ProcessId,
            writerPid = state.WriterPid,
            writerExitCode = state.ChildExitCode,
            exactWriterReapedConfirmed = state.ChildReapedConfirmed,
            fixtureRoot = state.RunRoot,
            fixedNtfsObserved = state.FixedNtfsObserved,
            openedVolume = state.OpenedVolume,
            exactPipePeerPidsObserved = state.ExactPipePeerPidsObserved,
            postBirthParentTransferObserved = state.ParentTransferObserved,
            writerSourcesPinnedThroughAllAdoptions = state.WriterSourcesPinned,
            postWriterDeathReadHandlesObserved = state.PostDeathObserved,
            exactCleanupSucceeded = state.CleanupSucceeded,
            nativeAuthorityProven = false,
            nativeDurabilityProven = false,
            trustedBaseAdmissionProven = false,
            restrictiveAclPolicyAdmissionProven = false,
            completeHandleInventoryProven = false,
            zeroExtraInheritanceProven = false,
            b1WriteReleaseIssued = false,
            b1JournalWritesPerformed = false,
            cleanupFailure = state.CleanupFailure,
            failure = failure is null ? null : $"{failure.GetType().Name}: {failure.Message}",
            custodyEvents = state.CustodyEvents,
            omittedMeasurements = new[]
            {
                "The Documents base ACL was not independently admitted against a selected production policy.",
                "No complete O/W system handle inventory or extra-handle inheritance enumeration was performed.",
                "The named pipe uses CurrentUserOnly plus exact server/client PID binding; it is not a reviewed cryptographic transcript.",
                "DuplicateHandle is synchronous; injected stop after native return is cleanup-only delivery, not asynchronous cancellation-drain evidence.",
                "Source lease enforcement trusts this same-binary W; it is not confinement of a hostile writer.",
                "No security descriptor admission, B1 write release, journal write, flush, or durability measurement was performed.",
            },
        };
        Console.WriteLine(JsonSerializer.Serialize(receipt, ReceiptJson));
        return receipt.succeeded ? 0 : 1;
    }

    private static async Task ExecuteAsync(OwnerState state,
        string? witnessPipe, string? witnessNonce, int controllerPid)
    {
        var pipeName = $"watchdog-ow-preflight-{state.RunId:N}";
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        state.StartChild(CreateChildStartInfo(state, pipeName, nonce));
        state.TryRecord(
            "writer-born",
            sourcePid: Environment.ProcessId,
            targetPid: state.Child!.Id,
            detail: state.Child.CreationTimeUtc.ToString("O"));
        await using var earlyWitness = state.Scenario.IsEarlyOwnerDeath ?
            await OwnerDeathWitness.ConnectEarlyOwnerAsync(witnessPipe!, witnessNonce!, controllerPid,
                state.Scenario, state.Child.SafeHandle, state.WitnessBaseHandles(), state.CustodyEvents)
                .ConfigureAwait(false) : null;
        if (earlyWitness is not null)
            state.TryRecord("witness-prearmed-before-writer-resume", sourcePid: controllerPid,
                targetPid: Environment.ProcessId);
        state.LaunchEvidence = state.Child.Resume();
        state.TryRecord("writer-primary-thread-resumed", sourcePid: Environment.ProcessId,
            targetPid: state.Child.Id, detail: JsonSerializer.Serialize(state.Child.Evidence));

        using (var connectTimeout = new CancellationTokenSource(ProtocolConstants.ConnectTimeout))
            await pipe.WaitForConnectionAsync(connectTimeout.Token).ConfigureAwait(false);

        if (!NativeMethods.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pipeClientPid) ||
            pipeClientPid != checked((uint)state.Child.Id))
            throw new InvalidDataException("The named-pipe client is not the retained W process.");

        await using var channel = new MessageChannel(pipe);
        var hello = await ReceiveAsync(channel).ConfigureAwait(false);
        ProtocolChecks.Require(hello, "hello", nonce, state.Child.Id);
        state.ExactPipePeerPidsObserved = true;
        state.TryRecord(
            "pipe-peer-bound",
            sourcePid: Environment.ProcessId,
            targetPid: state.Child.Id);

        var targetProcess = state.Child.SafeHandle;
        state.TryRecord(
            "transfer-intent",
            "trusted-parent",
            Environment.ProcessId,
            state.Child.Id,
            access: NativeMethods.ParentTransferAccess);
        state.BeforeTransfer("trusted-parent");
        var remoteParent = NativeMethods.DuplicateToProcess(
            state.TrustedParentHandle!, targetProcess, NativeMethods.ParentTransferAccess);
        state.CaptureSuccess("trusted-parent", remoteParent);
        await state.InjectAfterSuccessAsync("trusted-parent").ConfigureAwait(false);
        state.TryRecord(
            "transfer-success-obligation",
            "trusted-parent",
            Environment.ProcessId,
            state.Child.Id,
            remoteParent,
            NativeMethods.ParentTransferAccess);

        await channel.SendAsync(
            new(
                "parent-offer",
                nonce,
                Environment.ProcessId,
                "trusted-parent",
                remoteParent,
                Path.GetFileName(state.TrustedParentPath)),
            CancellationToken.None).ConfigureAwait(false);

        var parentAdopted = await ReceiveAsync(channel).ConfigureAwait(false);
        ProtocolChecks.Require(parentAdopted, "parent-adopted", nonce, state.Child.Id);
        if (parentAdopted.Observation is null)
            throw new InvalidDataException("W did not return its adopted parent observation.");
        NativeMethods.RequireObservation(
            parentAdopted.Observation,
            NativeMethods.ParentTransferAccess,
            directory: true,
            state.TrustedParentPath);
        NativeMethods.RequireSameObject(state.TrustedParentObservation!, parentAdopted.Observation);
        state.TryRecord(
            "receiver-adopted",
            "trusted-parent",
            state.Child.Id,
            state.Child.Id,
            remoteParent,
            NativeMethods.ParentTransferAccess,
            parentAdopted.Observation);
        if (state.Scenario.IsEarlyOwnerDeath && state.Scenario.Resource == "trusted-parent")
            await ReachEarlyOwnerBarrierAsync(state, earlyWitness!, channel, nonce, "trusted-parent", remoteParent)
                .ConfigureAwait(false);
        state.TryRecord("ack-intent", "trusted-parent", Environment.ProcessId, state.Child.Id);
        await channel.SendAsync(
            new("parent-ack", nonce, Environment.ProcessId, "trusted-parent"),
            CancellationToken.None).ConfigureAwait(false);
        state.TryRecord("ack-issued", "trusted-parent", Environment.ProcessId, state.Child.Id);
        state.ParentTransferObserved = true;

        var offersMessage = await ReceiveAsync(channel).ConfigureAwait(false);
        ProtocolChecks.Require(offersMessage, "source-offers", nonce, state.Child.Id);
        var offers = offersMessage.Sources;
        if (offers is null || offers.Count != 3)
            throw new InvalidDataException("W did not offer exactly three retained sources.");

        var expected = new[]
        {
            new ExpectedSource("private-root", state.PrivateRootName, state.PrivateRootPath,
                NativeMethods.RootReadAccess, true),
            new ExpectedSource("frames", "frames.bin", state.FramesPath,
                NativeMethods.FileReadAccess, false),
            new ExpectedSource("anchor", "anchor.bin", state.AnchorPath,
                NativeMethods.FileReadAccess, false),
        };

        for (var index = 0; index < expected.Length; index++)
        {
            var item = expected[index];
            var offer = offers[index];
            if (offer.Resource != item.Resource || offer.Name != item.Name ||
                offer.LeaseId == Guid.Empty || offer.Generation != 1 || offer.Handle is 0 or -1)
                throw new InvalidDataException("Invalid initial source lease.");
            NativeMethods.RequireSourceObservation(offer.Observation, item.Directory, item.Path);
        }
        state.CleanupManifest = offers;
        if (earlyWitness is not null)
        {
            await earlyWitness.PrepareCleanupAsync(offers, state.CustodyEvents).ConfigureAwait(false);
            state.TryRecord("witness-cleanup-manifest-acked-before-read-transfers",
                sourcePid: controllerPid, targetPid: Environment.ProcessId,
                detail: "P cleanup custody is separate from O read adoption and W ACK origin.");
        }

        for (var index = 0; index < expected.Length; index++)
        {
            var item = expected[index];
            var offer = offers[index];
            if (!StringComparer.Ordinal.Equals(offer.Resource, item.Resource) ||
                !StringComparer.Ordinal.Equals(offer.Name, item.Name) || offer.Handle is 0 or -1)
                throw new InvalidDataException($"Unexpected W source offer at index {index}.");

            await channel.SendAsync(new("source-check", nonce, Environment.ProcessId,
                item.Resource, LeaseId: offer.LeaseId, Generation: offer.Generation),
                CancellationToken.None).ConfigureAwait(false);
            var sourceState = await ReceiveAsync(channel).ConfigureAwait(false);
            ProtocolChecks.Require(sourceState, "source-state", nonce, state.Child.Id);
            var current = sourceState.Sources is { Count: 1 } ? sourceState.Sources[0] :
                throw new InvalidDataException("Missing source continuity response.");
            if (current.Resource != offer.Resource || current.Name != offer.Name)
                throw new InvalidDataException("Unexpected source continuity descriptor.");
            NativeMethods.RequireSameObject(offer.Observation, current.Observation);
            if (current.LeaseId != offer.LeaseId || current.Generation != offer.Generation ||
                current.Handle != offer.Handle)
            {
                if (state.Scenario.Kind != "source-reuse" || item.Resource != "frames")
                    throw new InvalidDataException("Source continuity drift.");
                state.SameFileCoordinateReuseObserved = current.Handle == offer.Handle;
                state.SourceGenerationDriftRejected = true;
                state.TryRecord("source-generation-drift-rejected", item.Resource,
                    detail: $"Same numeric coordinate: {current.Handle == offer.Handle}; same object identity verified.");
                throw new ExpectedScenarioFault("source-generation-drift");
            }
            if (sourceState.Detail != "close-denied-while-pinned")
                throw new InvalidDataException("Missing close-barrier observation.");
            state.TryRecord("source-close-barrier-observed", item.Resource);

            state.TryRecord(
                "transfer-intent",
                item.Resource,
                state.Child.Id,
                Environment.ProcessId,
                offer.Handle,
                item.Access);
            state.BeforeTransfer(item.Resource);
            var adopted = NativeMethods.DuplicateFromProcess(
                targetProcess,
                offer.Handle,
                item.Access);
            state.Adopt(item.Resource, adopted);
            state.CaptureSuccess(item.Resource, adopted.DangerousGetHandle().ToInt64());
            await state.InjectAfterSuccessAsync(item.Resource).ConfigureAwait(false);
            var observed = NativeMethods.Observe(adopted, item.Resource);
            state.SetObservation(item.Resource, observed);
            NativeMethods.RequireObservation(observed, item.Access, item.Directory, item.Path);
            NativeMethods.RequireSameObject(offer.Observation, observed);
            state.TryRecord(
                "transfer-success-obligation",
                item.Resource,
                state.Child.Id,
                Environment.ProcessId,
                adopted.DangerousGetHandle().ToInt64(),
                item.Access,
                observed);
            state.TryRecord(
                "receiver-adopted",
                item.Resource,
                Environment.ProcessId,
                Environment.ProcessId,
                adopted.DangerousGetHandle().ToInt64(),
                item.Access,
                observed);
            if (state.Scenario.IsEarlyOwnerDeath && state.Scenario.Resource == item.Resource)
                await ReachEarlyOwnerBarrierAsync(state, earlyWitness!, channel, nonce, item.Resource,
                    offer.Handle, offer.LeaseId, offer.Generation).ConfigureAwait(false);
            state.TryRecord("ack-intent", item.Resource, Environment.ProcessId, state.Child.Id);
            await channel.SendAsync(
                new("source-ack", nonce, Environment.ProcessId, item.Resource,
                    LeaseId: offer.LeaseId, Generation: offer.Generation),
                CancellationToken.None).ConfigureAwait(false);
            state.TryRecord("ack-issued", item.Resource, Environment.ProcessId, state.Child.Id);
        }

        if (StringComparer.Ordinal.Equals(
                state.RootReadObservation!.FileId,
                state.FramesReadObservation!.FileId) ||
            StringComparer.Ordinal.Equals(
                state.RootReadObservation.FileId,
                state.AnchorReadObservation!.FileId) ||
            StringComparer.Ordinal.Equals(
                state.FramesReadObservation.FileId,
                state.AnchorReadObservation.FileId))
            throw new InvalidDataException("The private root, frames, and anchor objects alias.");

        state.WriterSourcesPinned = true;

        state.TryRecord(
            "all-read-custody-recorded-release-withheld",
            detail: "No B1 write release exists in this preflight.");
        if (state.Scenario.Kind is "job-close" or "owner-death")
        {
            await channel.SendAsync(new("noncooperative-wait", nonce, Environment.ProcessId),
                CancellationToken.None).ConfigureAwait(false);
            var ready = await ReceiveAsync(channel).ConfigureAwait(false);
            ProtocolChecks.Require(ready, "noncooperative-ready", nonce, state.Child.Id);
            if (state.Scenario.Kind == "owner-death")
            {
                await OwnerDeathWitness.NeverReturnOwnerBarrierAsync(witnessPipe!, witnessNonce!,
                    controllerPid, state.Child.SafeHandle, state.WitnessHandles(), state.CustodyEvents)
                    .ConfigureAwait(false);
                throw new InvalidOperationException("Owner death barrier returned.");
            }
            var result = await state.Child.CloseJobAndWaitAsync(ProtocolConstants.ExitTimeout).ConfigureAwait(false);
            state.JobClosedExperiment = result.JobClosed;
            if (!result.ProcessExited)
                throw new InvalidOperationException("Job close did not signal exact W.");
            state.TryRecord("job-close-exact-writer-exit-observed", targetPid: state.Child.Id);
            state.ObserveAfterWriterDeath();
            return;
        }
        await channel.SendAsync(
            new("shutdown", nonce, Environment.ProcessId),
            CancellationToken.None).ConfigureAwait(false);
        var shutdownReady = await ReceiveAsync(channel).ConfigureAwait(false);
        ProtocolChecks.Require(shutdownReady, "shutdown-ready", nonce, state.Child.Id);
        state.WriterSourcesPinned = true;
        state.TryRecord(
            "writer-source-close-barrier-released",
            sourcePid: state.Child.Id,
            targetPid: Environment.ProcessId);

        await state.Child.WaitForExitAsync(ProtocolConstants.ExitTimeout).ConfigureAwait(false);
        state.ChildExitCode = state.Child.ExitCode;
        if (state.ChildExitCode != 0)
            throw new InvalidOperationException($"W exited with code {state.ChildExitCode}.");

        state.ObserveAfterWriterDeath();
    }

    private static async Task ReachEarlyOwnerBarrierAsync(OwnerState state,
        OwnerDeathWitness.EarlyOwnerSession witness, MessageChannel channel, string nonce,
        string resource, long sourceCoordinate, Guid? leaseId = null, long? generation = null)
    {
        await channel.SendAsync(new("early-owner-barrier", nonce, Environment.ProcessId,
            resource, Handle: sourceCoordinate, Name: state.Scenario.Name, LeaseId: leaseId, Generation: generation),
            CancellationToken.None).ConfigureAwait(false);
        var ready = await ReceiveAsync(channel).ConfigureAwait(false);
        ProtocolChecks.Require(ready, "noncooperative-ready", nonce, state.Child!.Id);
        if (ready.Resource != resource || ready.Name != state.Scenario.Name || ready.Detail != state.Scenario.Name ||
            ready.Handle != sourceCoordinate ||
            ready.LeaseId != leaseId || ready.Generation != generation)
            throw new InvalidDataException("W noncooperative readiness does not bind the selected early edge.");
        state.TryRecord("early-noncooperative-ready-observed", resource,
            state.Child.Id, Environment.ProcessId,
            detail: $"scenario={state.Scenario.Name};lease={leaseId};generation={generation}");
        await witness.NeverReturnBarrierAsync(state.CustodyEvents, ready).ConfigureAwait(false);
        throw new InvalidOperationException("Early owner-death barrier returned.");
    }

    private static async Task<WireMessage> ReceiveAsync(MessageChannel channel)
    {
        using var timeout = new CancellationTokenSource(ProtocolConstants.MessageTimeout);
        return await channel.ReceiveAsync(timeout.Token).ConfigureAwait(false);
    }

    private static ProcessStartInfo CreateChildStartInfo(
        OwnerState state,
        string pipeName,
        string nonce)
    {
        var executable = Environment.ProcessPath ??
            throw new InvalidOperationException("The current executable path is unavailable.");
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        if (StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(executable), "dotnet.exe"))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--child");
        start.ArgumentList.Add("--pipe");
        start.ArgumentList.Add(pipeName);
        start.ArgumentList.Add("--nonce");
        start.ArgumentList.Add(nonce);
        start.ArgumentList.Add("--parent-pid");
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        start.ArgumentList.Add("--trusted-parent");
        start.ArgumentList.Add(state.TrustedParentPath);
        start.ArgumentList.Add("--private-root");
        start.ArgumentList.Add(state.PrivateRootName);
        start.ArgumentList.Add("--scenario");
        start.ArgumentList.Add(state.Scenario.Name);
        return start;
    }

    private sealed record ExpectedSource(
        string Resource,
        string Name,
        string Path,
        uint Access,
        bool Directory);

    private sealed class OwnerState(ScenarioOptions scenario)
    {
        private readonly List<CustodyEvent> _events = [];
        private CustodyJournal? _custody;
        private SafeFileHandle? _rootRead;
        private SafeFileHandle? _framesRead;
        private SafeFileHandle? _anchorRead;

        internal Guid RunId { get; } = Guid.NewGuid();
        internal string RunRoot { get; private set; } = string.Empty;
        internal string BasePath { get; private set; } = string.Empty;
        internal string TrustedParentPath { get; private set; } = string.Empty;
        internal string CustodyPath { get; private set; } = string.Empty;
        internal string PrivateRootName { get; } = $"writer-private-{Guid.NewGuid():N}";
        internal string PrivateRootPath => Path.Combine(TrustedParentPath, PrivateRootName);
        internal string FramesPath => Path.Combine(PrivateRootPath, "frames.bin");
        internal string AnchorPath => Path.Combine(PrivateRootPath, "anchor.bin");
        internal SafeFileHandle? TrustedParentHandle { get; private set; }
        internal SafeFileHandle? BaseHandle { get; private set; }
        internal SafeFileHandle? RunRootHandle { get; private set; }
        internal HandleObservation? BaseObservation { get; private set; }
        internal HandleObservation? RunRootObservation { get; private set; }
        internal HandleObservation? TrustedParentObservation { get; private set; }
        internal HandleObservation? RootReadObservation { get; private set; }
        internal HandleObservation? FramesReadObservation { get; private set; }
        internal HandleObservation? AnchorReadObservation { get; private set; }
        internal ScenarioOptions Scenario { get; } = scenario;
        internal NativeChildLaunch? Child { get; private set; }
        internal object? LaunchEvidence { get; set; }
        internal object? FailedBirthCustody { get; private set; }
        internal bool JobClosedExperiment { get; set; }
        internal int SuccessObligationsCaptured { get; private set; }
        internal bool EmergencyCustodyRetained { get; private set; }
        internal bool StoreFailureObserved { get; private set; }
        internal bool StopGateClosed { get; private set; }
        internal bool DelayedCleanupDeliveryObserved { get; private set; }
        internal bool FaultReadPostDeathObserved { get; private set; }
        internal bool SameFileCoordinateReuseObserved { get; set; }
        internal bool SourceGenerationDriftRejected { get; set; }
        internal IReadOnlyList<SourceOffer>? CleanupManifest { get; set; }
        private long _remoteParentObligation;
        internal int? WriterPid { get; private set; }
        internal int? ChildExitCode { get; set; }
        internal bool FixedNtfsObserved { get; private set; }
        internal VolumeObservation? OpenedVolume { get; private set; }
        internal bool ExactPipePeerPidsObserved { get; set; }
        internal bool ParentTransferObserved { get; set; }
        internal bool WriterSourcesPinned { get; set; }
        internal bool PostDeathObserved { get; private set; }
        internal bool Succeeded { get; set; }
        internal bool CleanupSucceeded { get; private set; } = true;
        internal bool ChildReapedConfirmed { get; private set; }
        internal string? CleanupFailure { get; private set; }
        internal IReadOnlyList<CustodyEvent> CustodyEvents => _events;

        internal void InitializeFixture()
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documents) || !Path.IsPathFullyQualified(documents))
                throw new InvalidOperationException("The Documents directory is unavailable.");
            BasePath = Path.Combine(
                documents,
                "WatchDog.CaptureIsolation.Windows.OwTransferPreflight");
            RunRoot = Path.Combine(BasePath, $"run-{RunId:N}");
            TrustedParentPath = Path.Combine(RunRoot, "trusted-parent");
            CustodyPath = Path.Combine(RunRoot, "owner-custody.jsonl");
            Directory.CreateDirectory(TrustedParentPath);

            BaseHandle = NativeMethods.OpenDirectoryForObservation(BasePath);
            BaseObservation = NativeMethods.Observe(BaseHandle, "fixture-base");
            NativeMethods.RequireObservation(
                BaseObservation,
                NativeMethods.ParentTransferAccess,
                directory: true,
                BasePath);
            RunRootHandle = NativeMethods.OpenDirectoryForObservation(RunRoot);
            RunRootObservation = NativeMethods.Observe(RunRootHandle, "run-root");
            NativeMethods.RequireObservation(
                RunRootObservation,
                NativeMethods.ParentTransferAccess,
                directory: true,
                RunRoot);
            TrustedParentHandle = NativeMethods.OpenTrustedParent(TrustedParentPath);
            TrustedParentObservation = NativeMethods.Observe(
                TrustedParentHandle,
                "trusted-parent-source");
            NativeMethods.RequireObservation(
                TrustedParentObservation,
                NativeMethods.ParentTransferAccess,
                directory: true,
                TrustedParentPath);
            var volume = NativeMethods.ObserveVolume(TrustedParentHandle);
            OpenedVolume = volume;
            FixedNtfsObserved = volume.IsFixed &&
                StringComparer.OrdinalIgnoreCase.Equals(volume.FileSystem, "NTFS");
            // GetVolumeInformationByHandleW exposes a 32-bit serial; FILE_ID_INFO uses 64 bits.
            // Both observations come from the same retained handle.
            if (!FixedNtfsObserved ||
                volume.VolumeSerialNumber !=
                    unchecked((uint)TrustedParentObservation.VolumeSerialNumber))
                throw new InvalidOperationException(
                    $"Opened volume mismatch: fixed={volume.IsFixed}, fs={volume.FileSystem}, " +
                    $"volumeSerial={volume.VolumeSerialNumber:X16}, " +
                    $"fileIdVolumeSerial={TrustedParentObservation.VolumeSerialNumber:X16}.");

            _custody = new CustodyJournal(CustodyPath, _events);
            TryRecord(
                "fixture-created-before-writer-birth",
                detail: "Native authority and durability flags are permanently false.");
            TryRecord(
                "trusted-parent-retained",
                "trusted-parent",
                Environment.ProcessId,
                Environment.ProcessId,
                TrustedParentHandle.DangerousGetHandle().ToInt64(),
                NativeMethods.ParentTransferAccess,
                TrustedParentObservation);
        }

        internal void StartChild(ProcessStartInfo startInfo)
        {
            try
            {
                Child = NativeChildLaunch.CreateSuspended(startInfo.FileName, startInfo.ArgumentList.ToArray(),
                    injectBirthQueryFailure: Scenario.Kind == "birth-failure");
            }
            catch (NativeLaunchFailure exception)
            {
                Child = exception.Launch;
                WriterPid = Child.Id;
                FailedBirthCustody = new
                {
                    nativePid = Child.Id,
                    nativePrimaryThreadId = Child.PrimaryThreadId,
                    originalSlotsRetained = true,
                    threadNeverResumed = true,
                    creationTimeNotMeasured = true,
                };
                throw;
            }
            WriterPid = Child.Id;
            LaunchEvidence = Child.Evidence;
        }

        internal void CaptureSuccess(string resource, long coordinate)
        {
            if (resource == "trusted-parent")
                _remoteParentObligation = coordinate;
            SuccessObligationsCaptured++;
        }

        internal void BeforeTransfer(string resource)
        {
            if (Scenario.Kind == "stop-after-success" && Scenario.Resource == resource)
            {
                StopGateClosed = true;
                TryRecord("stop-gate-closed-before-native-return", resource);
            }
        }

        internal bool ExpectedFaultSatisfied(string code) =>
            Scenario.Kind == "source-reuse" ? code == "source-generation-drift" &&
                SourceGenerationDriftRejected && SameFileCoordinateReuseObserved :
            code == Scenario.Name && EmergencyCustodyRetained &&
            !CustodyEvents.Any(entry => entry.Event == "ack-issued" && entry.Resource == Scenario.Resource) &&
            (Scenario.Kind != "store-failure" || StoreFailureObserved) &&
            (Scenario.Kind != "stop-after-success" || StopGateClosed && DelayedCleanupDeliveryObserved);

        internal async Task InjectAfterSuccessAsync(string resource)
        {
            if (Scenario.Resource != resource ||
                Scenario.Kind is not ("store-failure" or "stop-after-success" or "writer-death"))
                return;
            if (Scenario.Kind == "writer-death")
            {
                var result = await Child!.TerminateAndReapAsync(ProtocolConstants.ExitTimeout).ConfigureAwait(false);
                if (!result.ProcessExited || !result.JobEmpty)
                    throw new InvalidOperationException("Exact W death/job drain was not confirmed.");
                if (resource != "trusted-parent")
                {
                    var retained = resource switch
                    {
                        "private-root" => _rootRead!,
                        "frames" => _framesRead!,
                        "anchor" => _anchorRead!,
                        _ => throw new InvalidDataException("Unexpected fault read resource."),
                    };
                    var observed = NativeMethods.Observe(retained, $"{resource}-fault-post-w-death");
                    var offered = CleanupManifest!.Single(item => item.Resource == resource).Observation;
                    NativeMethods.RequireSameObject(offered, observed);
                    NativeMethods.RequireObservation(observed,
                        resource == "private-root" ? NativeMethods.RootReadAccess : NativeMethods.FileReadAccess,
                        resource == "private-root", offered.FinalPath);
                    FaultReadPostDeathObserved = true;
                    TryRecord("fault-read-post-writer-death-observed", resource, observation: observed);
                }
            }
            EmergencyCustodyRetained = resource == "trusted-parent" ?
                _remoteParentObligation is not (0 or -1) : resource switch
                {
                    "private-root" => _rootRead is { IsClosed: false, IsInvalid: false },
                    "frames" => _framesRead is { IsClosed: false, IsInvalid: false },
                    "anchor" => _anchorRead is { IsClosed: false, IsInvalid: false },
                    _ => false,
                };
            if (!EmergencyCustodyRetained)
                throw new InvalidOperationException("Native success custody was lost.");
            if (Scenario.Kind == "store-failure")
            {
                _custody!.FailNextAppend = true;
                return;
            }
            if (Scenario.Kind == "stop-after-success")
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
                DelayedCleanupDeliveryObserved = StopGateClosed && EmergencyCustodyRetained;
                TryRecord("stopped-result-cleanup-only-delivery", resource,
                    detail: "Synchronous native success retained across a 100 ms managed delivery delay; ACK withheld.");
            }
            throw new ExpectedScenarioFault($"{Scenario.Kind}-{resource}");
        }

        internal void Adopt(string resource, SafeFileHandle handle)
        {
            switch (resource)
            {
                case "private-root":
                    _rootRead = handle;
                    break;
                case "frames":
                    _framesRead = handle;
                    break;
                case "anchor":
                    _anchorRead = handle;
                    break;
                default:
                    handle.Dispose();
                    throw new InvalidDataException($"Unknown resource {resource}.");
            }
        }

        internal void SetObservation(string resource, HandleObservation observed)
        {
            switch (resource)
            {
                case "private-root": RootReadObservation = observed; break;
                case "frames": FramesReadObservation = observed; break;
                case "anchor": AnchorReadObservation = observed; break;
            }
        }

        internal void ObserveAfterWriterDeath()
        {
            var root = NativeMethods.Observe(_rootRead!, "private-root-post-w-death");
            var frames = NativeMethods.Observe(_framesRead!, "frames-post-w-death");
            var anchor = NativeMethods.Observe(_anchorRead!, "anchor-post-w-death");
            NativeMethods.RequireSameObject(RootReadObservation!, root);
            NativeMethods.RequireSameObject(FramesReadObservation!, frames);
            NativeMethods.RequireSameObject(AnchorReadObservation!, anchor);
            PostDeathObserved = true;
            TryRecord(
                "post-writer-death-read-handles-observed",
                detail: "Metadata only; no live or forensic byte read was performed.");
        }

        internal async Task StopAndReapChildAsync()
        {
            if (Child is null)
            {
                ChildReapedConfirmed = true;
                return;
            }
            try
            {
                if (!JobClosedExperiment)
                {
                    var result = await Child.TerminateAndReapAsync(ProtocolConstants.ExitTimeout).ConfigureAwait(false);
                    if (!result.ProcessExited || !result.JobEmpty)
                        throw new InvalidOperationException("W/job reap was not confirmed.");
                    ChildExitCode = Child.ExitCode;
                    ChildReapedConfirmed = true;
                    try
                    {
                        TryRecord("writer-forcibly-reaped", targetPid: WriterPid);
                    }
                    catch (Exception exception)
                    {
                        Succeeded = false;
                        CleanupSucceeded = false;
                        CleanupFailure ??= $"reap-custody-record: {exception.Message}";
                        RecordFailureNonThrowing(
                            "writer-reap-custody-record-failed", exception.Message);
                    }
                }
                ChildExitCode ??= Child.ExitCode;
                ChildReapedConfirmed = Child.HasExited;
                LaunchEvidence = Child.Evidence;
            }
            catch (Exception exception)
            {
                CleanupSucceeded = false;
                CleanupFailure ??= $"child-reap: {exception.Message}";
                ChildReapedConfirmed = false;
                RecordFailureNonThrowing("writer-reap-unconfirmed", exception.Message);
            }
        }

        internal IReadOnlyDictionary<string, SafeFileHandle> WitnessBaseHandles() =>
            new Dictionary<string, SafeFileHandle>(StringComparer.Ordinal)
            {
                ["fixture-base"] = BaseHandle!,
                ["run-root"] = RunRootHandle!,
                ["trusted-parent"] = TrustedParentHandle!,
            };

        internal IReadOnlyDictionary<string, SafeFileHandle> WitnessHandles() =>
            new Dictionary<string, SafeFileHandle>(StringComparer.Ordinal)
            {
                ["fixture-base"] = BaseHandle!,
                ["run-root"] = RunRootHandle!,
                ["trusted-parent"] = TrustedParentHandle!,
                ["private-root"] = _rootRead!,
                ["frames"] = _framesRead!,
                ["anchor"] = _anchorRead!,
            };

        internal void DisposeReadHandlesForCleanup()
        {
            if (!ChildReapedConfirmed)
                return;
            _anchorRead?.Dispose();
            _anchorRead = null;
            _framesRead?.Dispose();
            _framesRead = null;
            _rootRead?.Dispose();
            _rootRead = null;
        }

        internal void CleanupExactArtifactsByHandle()
        {
            if (!ChildReapedConfirmed)
            {
                CleanupSucceeded = false;
                CleanupFailure ??= "exact-cleanup: skipped because W exit was not confirmed";
                return;
            }
            try
            {
                TryRecord("exact-cleanup-intent");
            }
            catch (Exception exception)
            {
                CleanupSucceeded = false;
                CleanupFailure ??= $"cleanup-intent-record: {exception.Message}";
            }
            try
            {
                DeleteWriterObjectsByHandle();
                TrustedParentHandle!.Dispose();
                TrustedParentHandle = null;

                using (var trustedParentDelete = NativeMethods.OpenRelativeForDelete(
                           RunRootHandle!, "trusted-parent", directory: true))
                {
                    var observed = NativeMethods.Observe(
                        trustedParentDelete, "trusted-parent-cleanup");
                    NativeMethods.RequireSameObject(TrustedParentObservation!, observed);
                    NativeMethods.MarkDeleteOnClose(
                        trustedParentDelete, "trusted-parent-cleanup");
                }

                DisposeCustodyNonThrowing();
                RunRootHandle!.Dispose();
                RunRootHandle = null;
                using (var runRootDelete = NativeMethods.OpenRelativeForDelete(
                           BaseHandle!, Path.GetFileName(RunRoot), directory: true))
                {
                    var observed = NativeMethods.Observe(runRootDelete, "run-root-cleanup");
                    NativeMethods.RequireSameObject(RunRootObservation!, observed);
                    NativeMethods.MarkDeleteOnClose(runRootDelete, "run-root-cleanup");
                }
            }
            catch (Exception exception)
            {
                CleanupSucceeded = false;
                CleanupFailure ??= $"exact-cleanup: {exception.Message}";
                DisposeCustodyNonThrowing();
            }
        }

        private void DeleteWriterObjectsByHandle()
        {
            var rootIdentity = RootReadObservation ?? CleanupManifest?[0].Observation;
            var framesIdentity = FramesReadObservation ?? CleanupManifest?[1].Observation;
            var anchorIdentity = AnchorReadObservation ?? CleanupManifest?[2].Observation;
            if (rootIdentity is null)
                return;

            using var rootDelete = NativeMethods.OpenRelativeForDelete(
                TrustedParentHandle!, PrivateRootName, directory: true);
            var rootObserved = NativeMethods.Observe(rootDelete, "private-root-cleanup");
            NativeMethods.RequireSameObject(rootIdentity, rootObserved);

            if (framesIdentity is not null)
            {
                using (var framesDelete = NativeMethods.OpenRelativeForDelete(
                           rootDelete, "frames.bin", directory: false))
                {
                    var observed = NativeMethods.Observe(framesDelete, "frames-cleanup");
                    NativeMethods.RequireSameObject(framesIdentity, observed);
                    NativeMethods.MarkDeleteOnClose(framesDelete, "frames-cleanup");
                }
            }
            if (anchorIdentity is not null)
            {
                using (var anchorDelete = NativeMethods.OpenRelativeForDelete(
                           rootDelete, "anchor.bin", directory: false))
                {
                    var observed = NativeMethods.Observe(anchorDelete, "anchor-cleanup");
                    NativeMethods.RequireSameObject(anchorIdentity, observed);
                    NativeMethods.MarkDeleteOnClose(anchorDelete, "anchor-cleanup");
                }
            }
            NativeMethods.MarkDeleteOnClose(rootDelete, "private-root-cleanup");
        }

        internal void DisposeRemainingHandles()
        {
            if (!ChildReapedConfirmed)
                return;
            TrustedParentHandle?.Dispose();
            TrustedParentHandle = null;
            RunRootHandle?.Dispose();
            RunRootHandle = null;
            BaseHandle?.Dispose();
            BaseHandle = null;
            Child?.Dispose();
        }

        private void DisposeCustodyNonThrowing()
        {
            try
            {
                _custody?.Dispose();
            }
            catch (Exception exception)
            {
                CleanupSucceeded = false;
                CleanupFailure ??= $"custody-close: {exception.Message}";
            }
            finally
            {
                _custody = null;
            }
        }

        internal void RecordFailureNonThrowing(string @event, string detail)
        {
            var entry = new CustodyEvent(
                _events.Count + 1,
                DateTimeOffset.UtcNow,
                @event,
                null,
                Environment.ProcessId,
                WriterPid,
                null,
                null,
                null,
                detail);
            try
            {
                if (_custody is null)
                    _events.Add(entry);
                else
                    _custody.Append(entry);
            }
            catch
            {
                if (!_events.Contains(entry))
                    _events.Add(entry);
            }
        }

        internal void TryRecord(
            string @event,
            string? resource = null,
            int? sourcePid = null,
            int? targetPid = null,
            long? handle = null,
            uint? access = null,
            HandleObservation? observation = null,
            string? detail = null)
        {
            if (_custody is null)
                return;
            try
            {
                _custody.Append(new(
                _events.Count + 1,
                DateTimeOffset.UtcNow,
                @event,
                resource,
                sourcePid,
                targetPid,
                handle,
                access,
                observation,
                    detail));
            }
            catch (IOException) when (Scenario.Kind == "store-failure" &&
                resource == Scenario.Resource && @event == "transfer-success-obligation" &&
                _custody.InjectedFailureObserved)
            {
                StoreFailureObserved = true;
                throw new ExpectedScenarioFault(Scenario.Name);
            }
        }
    }
}

internal sealed record CustodyEvent(
    int Sequence,
    DateTimeOffset TimestampUtc,
    string Event,
    string? Resource,
    int? SourcePid,
    int? TargetPid,
    long? HandleCoordinate,
    uint? IntendedAccess,
    HandleObservation? Observation,
    string? Detail,
    bool Persisted = false);

internal sealed class CustodyJournal : IDisposable
{
    internal bool FailNextAppend { get; set; }
    internal bool InjectedFailureObserved { get; private set; }
    private readonly FileStream _stream;
    private readonly StreamWriter _writer;
    private readonly List<CustodyEvent> _events;

    internal CustodyJournal(string path, List<CustodyEvent> events)
    {
        _events = events;
        _stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read | FileShare.Delete,
            4096,
            FileOptions.WriteThrough | FileOptions.DeleteOnClose);
        _writer = new StreamWriter(_stream, leaveOpen: true);
    }

    internal void Append(CustodyEvent entry)
    {
        if (FailNextAppend)
        {
            FailNextAppend = false;
            InjectedFailureObserved = true;
            throw new IOException("Injected custody-store append failure after native success.");
        }
        var persisted = entry with { Persisted = true };
        var json = JsonSerializer.Serialize(persisted);
        _writer.WriteLine(json);
        _writer.Flush();
        _stream.Flush(flushToDisk: true);
        _events.Add(persisted);
    }

    public void Dispose()
    {
        try
        {
            _writer.Dispose();
        }
        finally
        {
            _stream.Dispose();
        }
    }
}
