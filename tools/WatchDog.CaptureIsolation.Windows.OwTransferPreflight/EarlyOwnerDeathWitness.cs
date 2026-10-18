using Microsoft.Win32.SafeHandles;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

namespace WatchDog.CaptureIsolation.Windows.OwTransferPreflight;

internal static partial class OwnerDeathWitness
{
    private const uint EarlyWriterProcessAccess = 0x00101041;
    private static readonly string[] EarlyBaseResources =
        ["fixture-base", "run-root", "trusted-parent"];
    private static readonly string[] EarlyWriterResources =
        ["private-root", "frames", "anchor"];
    private static readonly string[] EarlyTransferResources =
        ["trusted-parent", "private-root", "frames", "anchor"];

    internal static async Task<int> RunEarlyControllerAsync(ScenarioOptions scenario)
    {
        ValidateEarlyScenario(scenario);
        var core = new ControllerState();
        var state = new EarlyControllerState(core, scenario);
        Exception? failure = null;
        try
        {
            await ExecuteEarlyControllerAsync(state).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
            core.Failure = $"{exception.GetType().Name}: {exception.Message}";
        }
        finally
        {
            await TerminateAndReapNonThrowingAsync(core).ConfigureAwait(false);
            if (core.ManifestValidated && core.OwnerExitConfirmed && core.WriterExitConfirmed &&
                !core.CleanupAttempted)
            {
                TryCleanupExact(core);
            }
            core.DisposeRetainedHandles();
        }

        var succeeded = failure is null && core.Failure is null && state.BarrierArmed &&
            core.OwnerExitConfirmed && core.WriterExitConfirmed &&
            !core.WriterFallbackTerminationUsed && core.CleanupSucceeded;
        var receipt = new
        {
            schema = "watchdog.ow-transfer-owner-death-early.v1",
            scenario = scenario.Name,
            scenarioKind = scenario.Kind,
            selectedResource = scenario.Resource,
            fixtureRoot = core.Manifest?.Handles
                .SingleOrDefault(item => item.Resource == "run-root")?.Observation.FinalPath,
            succeeded,
            stage = state.Stage.ToString(),
            controllerPid = Environment.ProcessId,
            ownerPid = core.OwnerPid,
            writerPid = core.WriterPid,
            ownerCreationFileTime = core.OwnerCreationFileTime,
            writerCreationFileTime = core.WriterCreationFileTime,
            writerProcessCapability = state.WriterProcessCapability,
            ownerExitFileTime = core.OwnerExitFileTime,
            writerExitFileTime = core.WriterExitFileTime,
            ownerExitCode = core.OwnerExitCode,
            writerExitCode = core.WriterExitCode,
            prearmAckedAtUtc = state.PrearmAckedAtUtc,
            cleanupManifestAckedAtUtc = state.CleanupManifestAckedAtUtc,
            barrierAckedAtUtc = state.BarrierAckedAtUtc,
            barrierArmedAtUtc = core.BarrierArmedAtUtc,
            ownerTerminateIssuedAtUtc = core.OwnerTerminateIssuedAtUtc,
            ownerExitObservedAtUtc = core.OwnerExitObservedAtUtc,
            writerExitObservedAtUtc = core.WriterExitObservedAtUtc,
            ownerLiveImmediatelyBeforeDeath = core.OwnerLiveImmediatelyBeforeDeath,
            writerLiveImmediatelyBeforeOwnerDeath = core.WriterLiveImmediatelyBeforeOwnerDeath,
            ownerExitConfirmed = core.OwnerExitConfirmed,
            writerExitConfirmed = core.WriterExitConfirmed,
            writerFallbackTerminationUsed = core.WriterFallbackTerminationUsed,
            writerJobHandleHeldByWitness = false,
            exactCleanupSucceeded = core.CleanupSucceeded,
            retainedCleanupObjectCount = core.Manifest?.Handles.Count ?? 0,
            cleanupManifestComplete = core.ManifestValidated &&
                core.Manifest?.Handles.Count ==
                    (IsParentScenario(scenario) ? EarlyBaseResources.Length :
                        EarlyBaseResources.Length + EarlyWriterResources.Length),
            artifactsPreserved = !core.CleanupSucceeded,
            expectedOwnerSuccessCount = state.ExpectedSuccessCount,
            expectedPrecedingAckCount = state.ExpectedPrecedingAckCount,
            observedOwnerSuccessCount = state.ObservedSuccessCount,
            observedPrecedingAckCount = state.ObservedAckCount,
            selectedNativeCoordinate = state.SelectedNativeCoordinate,
            selectedLeaseId = state.SelectedLeaseId,
            selectedGeneration = state.SelectedGeneration,
            boundReadiness = state.BoundReadiness,
            prearmHandles = state.PrearmHandles,
            pCleanupReads = state.PCleanupReads,
            nativeAuthorityProven = false,
            nativeDurabilityProven = false,
            completeHandleInventoryProven = false,
            trustedBaseAdmissionProven = false,
            zeroExtraInheritanceProven = false,
            b1WriteReleaseIssued = false,
            b1JournalWritesPerformed = false,
            failure = core.Failure,
            custodyPrefix = core.CustodyPrefix,
        };
        Console.WriteLine(JsonSerializer.Serialize(receipt, ReceiptJson));
        return succeeded ? 0 : 1;
    }

    internal static async Task<EarlyOwnerSession> ConnectEarlyOwnerAsync(
        string pipe,
        string nonce,
        int controllerPid,
        ScenarioOptions scenario,
        SafeHandle writerProcess,
        IReadOnlyDictionary<string, SafeFileHandle> baseHandles,
        IReadOnlyList<CustodyEvent> prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipe);
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);
        if (controllerPid <= 0)
            throw new ArgumentOutOfRangeException(nameof(controllerPid));
        ValidateEarlyScenario(scenario);
        ArgumentNullException.ThrowIfNull(writerProcess);
        ArgumentNullException.ThrowIfNull(baseHandles);
        ArgumentNullException.ThrowIfNull(prefix);
        if (writerProcess.IsInvalid || writerProcess.IsClosed)
            throw new InvalidOperationException("The early witness W process handle is unavailable.");
        RequireResourceKeys(baseHandles, EarlyBaseResources, "early prearm");
        RequirePersistedPrefixExtension([], prefix);
        RequirePrearmCustody(prefix);

        var writer = ObserveProcess(writerProcess);
        if (writer.Exited)
            throw new InvalidOperationException("W exited before early witness prearm.");
        var offers = new EarlyHandleOffer[EarlyBaseResources.Length];
        for (var index = 0; index < EarlyBaseResources.Length; index++)
        {
            var resource = EarlyBaseResources[index];
            var handle = baseHandles[resource];
            var observation = NativeMethods.Observe(handle, resource);
            NativeMethods.RequireObservation(
                observation,
                AccessFor(resource),
                directory: true,
                observation.FinalPath);
            offers[index] = new(
                resource,
                handle.DangerousGetHandle().ToInt64(),
                AccessFor(resource),
                observation);
        }
        ValidateEarlyBaseGraph(offers);

        var sessionId = Guid.NewGuid();
        var manifest = new EarlyPrearmManifest(
            "early-prearm",
            nonce,
            sessionId,
            BindScenario(scenario),
            Environment.ProcessId,
            controllerPid,
            writerProcess.DangerousGetHandle().ToInt64(),
            checked((int)writer.ProcessId),
            writer.CreationFileTime,
            offers,
            prefix.ToArray());

        var client = new NamedPipeClientStream(
            ".",
            pipe,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        try
        {
            using var connectTimeout = new CancellationTokenSource(ProtocolConstants.ConnectTimeout);
            await client.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
            if (!NativeMethods.GetNamedPipeServerProcessId(client.SafePipeHandle, out var serverPid) ||
                serverPid != checked((uint)controllerPid))
            {
                throw new InvalidDataException("The early witness pipe server is not exact P.");
            }

            var channel = new WitnessJsonChannel(client);
            await channel.SendAsync(manifest, CancellationToken.None).ConfigureAwait(false);
            var ack = await channel.ReceiveAsync<EarlyAck>(CancellationToken.None).ConfigureAwait(false);
            RequireEarlyAck(ack, "early-prearm-ack", EarlyStage.Prearmed, manifest.SessionId,
                manifest.Nonce, manifest.Scenario, controllerPid, manifest.OwnerPid,
                manifest.WriterPid, manifest.Prefix.Count, EarlyBaseResources.Length);
            return new EarlyOwnerSession(
                client,
                channel,
                manifest.SessionId,
                manifest.Nonce,
                scenario,
                controllerPid,
                manifest.OwnerPid,
                manifest.WriterPid,
                manifest.WriterCreationFileTime,
                manifest.Prefix);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static int RunEarlyContractChecks()
    {
        var positiveChecks = 0;
        var rejectedCases = new List<string>();

        void Positive(Action action)
        {
            action();
            positiveChecks++;
        }

        void Reject(string name, Action action)
        {
            RequireContractCheckFailure(action);
            rejectedCases.Add(name);
        }

        var observation = new HandleObservation(
            "trusted-parent",
            NativeMethods.ParentTransferAccess,
            false,
            true,
            false,
            1,
            "01",
            1,
            0,
            @"C:\fixture\run-1\trusted-parent");
        var first = new CustodyEvent(
            1, DateTimeOffset.UnixEpoch, "fixture-created", null, 1, 1,
            null, null, observation, null, Persisted: true);
        var second = new CustodyEvent(
            2, DateTimeOffset.UnixEpoch.AddTicks(1), "writer-born", null, 1, 2,
            null, null, null, null, Persisted: true);
        Positive(() => RequirePersistedPrefixExtension([first], [first, second]));
        Reject("prefix-truncation", () =>
            RequirePersistedPrefixExtension([first, second], [first]));
        Reject("prefix-mutation", () => RequirePersistedPrefixExtension(
            [first], [first with { Event = "mutated" }, second]));
        Reject("unpersisted-prefix", () => RequirePersistedPrefixExtension(
            [first], [first, second with { Persisted = false }]));
        Positive(() => RequireStage(EarlyStage.Prearmed, EarlyStage.Prearmed, "contract-check"));
        Reject("stage-replay", () =>
            RequireStage(EarlyStage.CleanupPrepared, EarlyStage.Prearmed, "contract-check"));

        Positive(() => RequirePrearmCustody([first, second]));
        var priorRead = new CustodyEvent(
            3, DateTimeOffset.UnixEpoch.AddTicks(2), "transfer-intent", "private-root",
            2, 1, 91, NativeMethods.RootReadAccess, null, null, Persisted: true);
        Reject("prior-read-before-prearm", () =>
            RequirePrearmCustody([first, second, priorRead]));

        var parentIntent = priorRead with
        {
            Event = "transfer-intent",
            Resource = "trusted-parent",
            SourcePid = 1,
            TargetPid = 2,
            HandleCoordinate = null,
            IntendedAccess = NativeMethods.ParentTransferAccess,
        };
        var parentSuccess = parentIntent with
        {
            Sequence = 4,
            Event = "transfer-success-obligation",
            HandleCoordinate = 42,
        };
        var parentAdoption = parentSuccess with
        {
            Sequence = 5,
            Event = "receiver-adopted",
            SourcePid = 2,
            TargetPid = 2,
            Observation = observation,
        };
        var parentAck = parentIntent with
        {
            Sequence = 6,
            Event = "ack-issued",
            HandleCoordinate = null,
        };
        CustodyEvent[] completedParent =
            [first, second, parentIntent, parentSuccess, parentAdoption, parentAck];
        Positive(() => RequireParentCompletedBeforeCleanup(
            completedParent, ownerPid: 1, writerPid: 2));
        Reject("prior-read-before-cleanup-manifest", () =>
            RequireParentCompletedBeforeCleanup([.. completedParent, priorRead], 1, 2));
        Reject("parent-ordering", () => RequireParentCompletedBeforeCleanup(
            [first, second, parentSuccess, parentIntent, parentAdoption, parentAck], 1, 2));
        Reject("null-coordinate", () => RequireParentCompletedBeforeCleanup(
            completedParent.Select(entry => entry == parentSuccess
                ? entry with { HandleCoordinate = null }
                : entry).ToArray(), 1, 2));
        Reject("coordinate-drift", () => RequireParentCompletedBeforeCleanup(
            completedParent.Select(entry => entry == parentAdoption
                ? entry with { HandleCoordinate = 43 }
                : entry).ToArray(), 1, 2));
        Reject("wrong-source", () => RequireParentCompletedBeforeCleanup(
            completedParent.Select(entry => entry == parentSuccess
                ? entry with { SourcePid = 99 }
                : entry).ToArray(), 1, 2));
        Reject("wrong-issuer", () => RequireParentCompletedBeforeCleanup(
            completedParent.Select(entry => entry == parentAck
                ? entry with { SourcePid = 99 }
                : entry).ToArray(), 1, 2));
        Reject("selected-ack", () => RequireNoSelectedOrLaterAcks(completedParent, 0));
        var laterAck = parentAck with { Sequence = 7, Resource = "frames" };
        Reject("later-ack", () => RequireNoSelectedOrLaterAcks(
            [.. completedParent, laterAck], 1));

        var prearmed = first with
        {
            Sequence = 3,
            Event = "witness-prearmed-before-writer-resume",
            SourcePid = 3,
            TargetPid = 1,
            Observation = null,
        };
        var resumed = prearmed with
        {
            Sequence = 4,
            Event = "writer-primary-thread-resumed",
            SourcePid = 1,
            TargetPid = 2,
            Detail = JsonSerializer.Serialize(new LaunchEvidence(2, 4, 5, 1, false)),
        };
        var orderedIntent = parentIntent with { Sequence = 5 };
        Positive(() => RequirePrearmResumeOrdering(
            [first, second, prearmed, resumed, orderedIntent], 3, 1, 2, 5));
        Reject("resume-ordering", () => RequirePrearmResumeOrdering(
            [first, second, resumed, prearmed, orderedIntent], 3, 1, 2, 5));

        var scenario = new ScenarioOptions(
            "owner-death-trusted-parent", "owner-death-edge", "trusted-parent");
        Reject("scenario-replay", () => RequireEarlyEnvelope(
            "early-prearm", "early-prearm", "nonce", "nonce", Guid.NewGuid(),
            new("replayed", "owner-death-edge", "trusted-parent"), scenario,
            1, 1, 2, 2));

        var privateIntent = parentIntent with
        {
            Sequence = 7,
            Resource = "private-root",
            SourcePid = 2,
            TargetPid = 1,
            HandleCoordinate = 91,
            IntendedAccess = NativeMethods.RootReadAccess,
        };
        var privateAdoption = privateIntent with
        {
            Sequence = 9,
            Event = "receiver-adopted",
            SourcePid = 1,
            TargetPid = 1,
        };
        var privateAck = parentAck with
        {
            Sequence = 10,
            Resource = "private-root",
        };
        var framesIntent = privateIntent with
        {
            Sequence = 11,
            Resource = "frames",
            HandleCoordinate = 92,
            IntendedAccess = NativeMethods.FileReadAccess,
        };
        CustodyEvent[] sequential =
            [.. completedParent, privateIntent, privateAdoption, privateAck, framesIntent];
        Positive(() => RequireSequentialCustodyOrdering(sequential, 3, 2));
        Reject("cross-resource-ack-ordering", () => RequireSequentialCustodyOrdering(
            [.. completedParent, privateIntent, privateAdoption,
                framesIntent with { Sequence = 10 }, privateAck with { Sequence = 11 }], 3, 2));

        var result = new
        {
            schema = "watchdog.ow-transfer-owner-death-early-contract.v1",
            succeeded = true,
            nativeEffects = false,
            positiveChecks,
            rejectionChecks = rejectedCases.Count,
            rejectedCases,
        };
        Console.WriteLine(JsonSerializer.Serialize(result, ReceiptJson));
        return 0;
    }

    private static async Task ExecuteEarlyControllerAsync(EarlyControllerState state)
    {
        var pipeName = $"watchdog-owner-death-early-{Guid.NewGuid():N}";
        var nonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        var executable = Environment.ProcessPath ??
            throw new InvalidOperationException("The current executable path is unavailable.");
        var arguments = BuildOwnerArguments(executable, pipeName, nonce)
            .Concat([state.Scenario.Name])
            .ToArray();
        try
        {
            state.Core.Owner = NativeChildLaunch.CreateSuspended(executable, arguments);
        }
        catch (NativeLaunchFailure launchFailure)
        {
            state.Core.Owner = launchFailure.Launch;
            state.Core.OwnerPid = launchFailure.Launch.Id;
            state.Core.OwnerCreationFileTime = launchFailure.Launch.CreationFileTime;
            throw;
        }
        state.Core.OwnerPid = state.Core.Owner.Id;
        state.Core.OwnerCreationFileTime = state.Core.Owner.CreationFileTime;
        state.Core.Owner.Resume();

        using (var connectTimeout = new CancellationTokenSource(ProtocolConstants.ConnectTimeout))
            await pipe.WaitForConnectionAsync(connectTimeout.Token).ConfigureAwait(false);
        if (!NativeMethods.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientPid) ||
            clientPid != checked((uint)state.Core.OwnerPid.Value))
        {
            throw new InvalidDataException("The early witness pipe client is not exact O.");
        }

        await using var channel = new WitnessJsonChannel(pipe);
        var prearm = await channel.ReceiveAsync<EarlyPrearmManifest>(CancellationToken.None)
            .ConfigureAwait(false);
        AdoptEarlyPrearm(state, prearm, nonce);
        await channel.SendAsync(
            NewEarlyAck("early-prearm-ack", EarlyStage.Prearmed, state, prearm.Prefix.Count,
                EarlyBaseResources.Length),
            CancellationToken.None).ConfigureAwait(false);
        state.PrearmAckedAtUtc = DateTimeOffset.UtcNow;
        state.Stage = EarlyStage.Prearmed;

        if (!IsParentScenario(state.Scenario))
        {
            var cleanup = await channel.ReceiveAsync<EarlyCleanupManifest>(CancellationToken.None)
                .ConfigureAwait(false);
            AdoptEarlyCleanup(state, cleanup);
            await channel.SendAsync(
                NewEarlyAck("early-cleanup-ack", EarlyStage.CleanupPrepared, state,
                    cleanup.Prefix.Count, EarlyWriterResources.Length),
                CancellationToken.None).ConfigureAwait(false);
            state.CleanupManifestAckedAtUtc = DateTimeOffset.UtcNow;
            state.Stage = EarlyStage.CleanupPrepared;
        }

        var barrier = await channel.ReceiveAsync<EarlyBarrierManifest>(CancellationToken.None)
            .ConfigureAwait(false);
        ValidateEarlyBarrier(state, barrier);
        await channel.SendAsync(
            NewEarlyAck("early-barrier-ack", EarlyStage.BarrierAcknowledged, state,
                barrier.Prefix.Count, state.Core.FileHandles.Count),
            CancellationToken.None).ConfigureAwait(false);
        state.BarrierAckedAtUtc = DateTimeOffset.UtcNow;
        state.Stage = EarlyStage.BarrierAcknowledged;

        var armed = await channel.ReceiveAsync<EarlyArmed>(CancellationToken.None)
            .ConfigureAwait(false);
        RequireEarlyArmed(state, armed);
        state.Stage = EarlyStage.Armed;
        state.BarrierArmed = true;
        state.Core.BarrierArmedAtUtc = DateTimeOffset.UtcNow;
        state.Core.CustodyPrefix = barrier.Prefix.ToArray();

        state.Core.OwnerLiveImmediatelyBeforeDeath = !state.Core.Owner.HasExited;
        state.Core.WriterLiveImmediatelyBeforeOwnerDeath =
            !ObserveProcess(state.Core.WriterProcess!).Exited;
        if (!state.Core.OwnerLiveImmediatelyBeforeDeath ||
            !state.Core.WriterLiveImmediatelyBeforeOwnerDeath)
        {
            throw new InvalidOperationException("O or W exited before the early abrupt O death.");
        }

        state.Core.OwnerTerminateIssuedAtUtc = DateTimeOffset.UtcNow;
        TerminateExact(state.Core.Owner.SafeHandle, AbruptOwnerExitCode, "early O");
        await state.Core.Owner.WaitForExitAsync(DeathTimeout).ConfigureAwait(false);
        CaptureOwnerExit(state.Core);
        if (!await WaitForProcessAsync(state.Core.WriterProcess!, DeathTimeout).ConfigureAwait(false))
        {
            state.Core.WriterFallbackTerminationUsed = true;
            TerminateExact(state.Core.WriterProcess!, WriterFallbackExitCode, "early W fallback");
            if (!await WaitForProcessAsync(state.Core.WriterProcess!, DeathTimeout).ConfigureAwait(false))
                throw new TimeoutException("Exact early W remained live after fallback termination.");
            CaptureWriterExit(state.Core);
            throw new InvalidOperationException("Early W required fallback termination after O death.");
        }
        CaptureWriterExit(state.Core);
        CleanupExact(state.Core);
    }

    private static void AdoptEarlyPrearm(
        EarlyControllerState state,
        EarlyPrearmManifest manifest,
        string nonce)
    {
        RequireEarlyEnvelope(
            manifest.Type,
            "early-prearm",
            manifest.Nonce,
            nonce,
            manifest.SessionId,
            manifest.Scenario,
            state.Scenario,
            manifest.OwnerPid,
            state.Core.OwnerPid,
            manifest.ControllerPid,
            Environment.ProcessId);
        if (manifest.WriterPid <= 0 || manifest.WriterProcessCoordinate is 0 or -1 ||
            manifest.WriterCreationFileTime <= 0 || manifest.BaseHandles.Count != EarlyBaseResources.Length)
        {
            throw new InvalidDataException("The early prearm header is invalid.");
        }
        RequirePersistedPrefixExtension([], manifest.Prefix);
        RequirePrearmCustody(manifest.Prefix);
        ValidateEarlyBaseGraph(manifest.BaseHandles);

        var candidate = DuplicateWriterProcess(
            state.Core.Owner!.SafeHandle,
            manifest.WriterProcessCoordinate,
            EarlyWriterProcessAccess);
        try
        {
            var processCapability = NativeMethods.ObserveKernelHandle(candidate);
            if (processCapability.GrantedAccess != EarlyWriterProcessAccess || processCapability.Inheritable)
                throw new InvalidDataException("The early retained W process capability has excess, missing, or inheritable rights.");
            var writer = ObserveProcess(candidate);
            if (writer.Exited || writer.ProcessId != checked((uint)manifest.WriterPid) ||
                writer.CreationFileTime != manifest.WriterCreationFileTime)
                throw new InvalidDataException("The early prearm retained the wrong W process.");
            state.WriterProcessCapability = processCapability;
            state.Core.WriterProcess = candidate;
        }
        catch
        {
            candidate.Dispose();
            throw;
        }
        state.Core.WriterPid = manifest.WriterPid;
        state.Core.WriterCreationFileTime = manifest.WriterCreationFileTime;

        var canonical = new List<WitnessHandleOffer>(EarlyBaseResources.Length);
        for (var index = 0; index < EarlyBaseResources.Length; index++)
        {
            var resource = EarlyBaseResources[index];
            var offer = manifest.BaseHandles[index];
            if (!StringComparer.Ordinal.Equals(offer.Resource, resource) ||
                offer.Coordinate is 0 or -1 || offer.DesiredAccess != AccessFor(resource))
            {
                throw new InvalidDataException($"The early prearm offer for {resource} is invalid.");
            }
            var adopted = NativeMethods.DuplicateFromProcess(
                state.Core.Owner.SafeHandle,
                offer.Coordinate,
                offer.DesiredAccess);
            state.Core.FileHandles.Add(resource, adopted);
            var observed = NativeMethods.Observe(adopted, resource);
            NativeMethods.RequireObservation(
                observed,
                offer.DesiredAccess,
                directory: true,
                offer.Observation.FinalPath);
            NativeMethods.RequireSameObject(offer.Observation, observed);
            var canonicalOffer = new WitnessHandleOffer(
                resource,
                adopted.DangerousGetHandle().ToInt64(),
                offer.DesiredAccess,
                observed);
            canonical.Add(canonicalOffer);
            state.PrearmHandles.Add(ToEarlyReceiptHandle(canonicalOffer));
        }

        state.SessionId = manifest.SessionId;
        state.Nonce = manifest.Nonce;
        state.PrearmPrefix = manifest.Prefix.ToArray();
        state.WriterProcessCoordinate = manifest.WriterProcessCoordinate;
        state.Core.CustodyPrefix = manifest.Prefix.ToArray();
        state.Core.Manifest = new WitnessManifest(
            "early-canonical-cleanup",
            manifest.Nonce,
            manifest.SessionId,
            manifest.OwnerPid,
            manifest.ControllerPid,
            manifest.WriterProcessCoordinate,
            manifest.WriterPid,
            manifest.WriterCreationFileTime,
            canonical,
            manifest.Prefix);
        state.Core.ManifestValidated = IsParentScenario(state.Scenario);
    }

    private static void AdoptEarlyCleanup(
        EarlyControllerState state,
        EarlyCleanupManifest cleanup)
    {
        RequireStage(state.Stage, EarlyStage.Prearmed, "early cleanup");
        RequireEarlySessionEnvelope(
            cleanup.Type,
            "early-cleanup",
            cleanup.Nonce,
            cleanup.SessionId,
            cleanup.Scenario,
            cleanup.OwnerPid,
            cleanup.ControllerPid,
            cleanup.WriterPid,
            state);
        RequirePersistedPrefixExtension(state.PrearmPrefix, cleanup.Prefix);
        if (cleanup.Stage != EarlyStage.CleanupPrepared ||
            cleanup.WriterCreationFileTime != state.Core.WriterCreationFileTime)
        {
            throw new InvalidDataException("The early cleanup stage or W creation identity drifted.");
        }
        RequireParentCompletedBeforeCleanup(
            cleanup.Prefix,
            state.Core.OwnerPid!.Value,
            state.Core.WriterPid!.Value);
        ValidateEarlySourceBindings(state, cleanup.Sources);

        var canonical = state.Core.Manifest!.Handles.ToList();
        foreach (var binding in cleanup.Sources)
        {
            var adopted = NativeMethods.DuplicateFromProcess(
                state.Core.WriterProcess!,
                binding.SourceCoordinate,
                binding.DesiredAccess);
            state.Core.FileHandles.Add(binding.Resource, adopted);
            var observed = NativeMethods.Observe(adopted, binding.Resource);
            NativeMethods.RequireObservation(
                observed,
                binding.DesiredAccess,
                binding.Resource == "private-root",
                binding.SourceObservation.FinalPath);
            NativeMethods.RequireSameObject(binding.SourceObservation, observed);
            var canonicalOffer = new WitnessHandleOffer(
                binding.Resource,
                adopted.DangerousGetHandle().ToInt64(),
                binding.DesiredAccess,
                observed);
            canonical.Add(canonicalOffer);
            state.PCleanupReads.Add(new EarlyCleanupReceipt(
                binding.CreatorPid,
                binding.SourceHolderPid,
                binding.InvokingPid,
                binding.TargetPid,
                binding.Resource,
                binding.Name,
                binding.SourceCoordinate,
                binding.LeaseId,
                binding.Generation,
                binding.DesiredAccess,
                binding.SourceObservation,
                canonicalOffer.Coordinate,
                observed));
        }

        state.CleanupSources = cleanup.Sources.ToArray();
        state.CleanupPrefix = cleanup.Prefix.ToArray();
        state.Core.CustodyPrefix = cleanup.Prefix.ToArray();
        state.Core.Manifest = state.Core.Manifest with
        {
            Handles = canonical,
            CustodyPrefix = cleanup.Prefix,
        };
        state.Core.ManifestValidated = true;
    }

    private static void ValidateEarlySourceBindings(
        EarlyControllerState state,
        IReadOnlyList<EarlySourceBinding> sources)
    {
        if (sources.Count != EarlyWriterResources.Length)
            throw new InvalidDataException("The early cleanup manifest must bind exactly three W originals.");
        var trustedParent = state.Core.Manifest!.Handles
            .Single(item => item.Resource == "trusted-parent").Observation;
        var identities = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < EarlyWriterResources.Length; index++)
        {
            var resource = EarlyWriterResources[index];
            var source = sources[index];
            var expectedAccess = AccessFor(resource);
            if (!StringComparer.Ordinal.Equals(source.Resource, resource) ||
                source.CreatorPid != state.Core.WriterPid ||
                source.SourceHolderPid != state.Core.WriterPid ||
                source.InvokingPid != Environment.ProcessId ||
                source.TargetPid != Environment.ProcessId ||
                source.SourceCoordinate is 0 or -1 || source.LeaseId == Guid.Empty ||
                source.Generation <= 0 || source.DesiredAccess != expectedAccess ||
                (source.SourceObservation.GrantedAccess & expectedAccess) != expectedAccess ||
                !StringComparer.Ordinal.Equals(source.Name,
                    resource == "private-root" ? LeafName(source.SourceObservation.FinalPath) :
                    resource + ".bin"))
            {
                throw new InvalidDataException($"The early source binding for {resource} is invalid.");
            }
            NativeMethods.RequireSourceObservation(
                source.SourceObservation,
                resource == "private-root",
                source.SourceObservation.FinalPath);
            if (resource == "private-root")
            {
                if (!ParentPathEquals(source.SourceObservation.FinalPath, trustedParent.FinalPath))
                    throw new InvalidDataException("The early private root is outside the trusted parent.");
            }
            else if (!ParentPathEquals(
                         source.SourceObservation.FinalPath,
                         sources[0].SourceObservation.FinalPath))
            {
                throw new InvalidDataException($"The early {resource} source is outside the private root.");
            }
            if (!identities.Add(
                    $"{source.SourceObservation.VolumeSerialNumber:X16}:{source.SourceObservation.FileId}"))
            {
                throw new InvalidDataException("The early W source bindings alias objects.");
            }
        }
        if (sources.Select(item => item.SourceCoordinate).Distinct().Count() != sources.Count ||
            sources.Select(item => item.LeaseId).Distinct().Count() != sources.Count)
        {
            throw new InvalidDataException("The early W source bindings reuse coordinates or leases.");
        }
    }

    private static void ValidateEarlyBarrier(
        EarlyControllerState state,
        EarlyBarrierManifest barrier)
    {
        var expectedStage = IsParentScenario(state.Scenario)
            ? EarlyStage.Prearmed
            : EarlyStage.CleanupPrepared;
        RequireStage(state.Stage, expectedStage, "early barrier");
        RequireEarlySessionEnvelope(
            barrier.Type,
            "early-barrier",
            barrier.Nonce,
            barrier.SessionId,
            barrier.Scenario,
            barrier.OwnerPid,
            barrier.ControllerPid,
            barrier.WriterPid,
            state);
        var previous = IsParentScenario(state.Scenario)
            ? state.PrearmPrefix
            : state.CleanupPrefix;
        RequirePersistedPrefixExtension(previous, barrier.Prefix);
        if (barrier.Stage != EarlyStage.BarrierAcknowledged)
            throw new InvalidDataException("The early barrier stage is invalid.");
        RequirePrearmResumeOrdering(
            barrier.Prefix,
            Environment.ProcessId,
            state.Core.OwnerPid!.Value,
            state.Core.WriterPid!.Value,
            state.Core.WriterCreationFileTime!.Value);
        ValidateEarlyPrefixSemantics(state, barrier.Prefix);
        ValidateBoundReadiness(state, barrier.Readiness);
        if (barrier.SelectedNativeCoordinate != state.SelectedNativeCoordinate ||
            barrier.SelectedLeaseId != state.SelectedLeaseId ||
            barrier.SelectedGeneration != state.SelectedGeneration ||
            barrier.ExpectedSuccessCount != state.ExpectedSuccessCount ||
            barrier.ExpectedPrecedingAckCount != state.ExpectedPrecedingAckCount)
        {
            throw new InvalidDataException("The early barrier selected a different native obligation.");
        }
        state.BoundReadiness = barrier.Readiness;
        state.Core.CustodyPrefix = barrier.Prefix.ToArray();
    }

    private static void ValidateEarlyPrefixSemantics(
        EarlyControllerState state,
        IReadOnlyList<CustodyEvent> prefix)
    {
        var selectedIndex = Array.IndexOf(EarlyTransferResources, state.Scenario.Resource!);
        state.ExpectedSuccessCount = selectedIndex + 1;
        state.ExpectedPrecedingAckCount = selectedIndex;
        var intents = prefix.Where(entry => entry.Event == "transfer-intent").ToArray();
        var successes = prefix.Where(entry => entry.Event == "transfer-success-obligation").ToArray();
        var adoptions = prefix.Where(entry => entry.Event == "receiver-adopted").ToArray();
        var acknowledgments = prefix.Where(entry => entry.Event == "ack-issued").ToArray();
        if (intents.Length != state.ExpectedSuccessCount ||
            successes.Length != state.ExpectedSuccessCount ||
            adoptions.Length != state.ExpectedSuccessCount ||
            acknowledgments.Length != state.ExpectedPrecedingAckCount)
        {
            throw new InvalidDataException("The early barrier custody counts do not match its edge.");
        }

        for (var index = 0; index < state.ExpectedSuccessCount; index++)
        {
            var resource = EarlyTransferResources[index];
            var intent = intents[index];
            var success = successes[index];
            var adoption = adoptions[index];
            var sourcePid = index == 0 ? state.Core.OwnerPid : state.Core.WriterPid;
            var targetPid = index == 0 ? state.Core.WriterPid : state.Core.OwnerPid;
            var adoptionPid = index == 0 ? state.Core.WriterPid : state.Core.OwnerPid;
            if (!StringComparer.Ordinal.Equals(intent.Resource, resource) ||
                intent.SourcePid != sourcePid || intent.TargetPid != targetPid ||
                intent.IntendedAccess != AccessFor(resource) ||
                !StringComparer.Ordinal.Equals(success.Resource, resource) ||
                success.SourcePid != sourcePid || success.TargetPid != targetPid ||
                success.HandleCoordinate is null or 0 or -1 ||
                success.IntendedAccess != AccessFor(resource) ||
                !StringComparer.Ordinal.Equals(adoption.Resource, resource) ||
                adoption.SourcePid != adoptionPid || adoption.TargetPid != adoptionPid ||
                adoption.HandleCoordinate is null or 0 or -1 ||
                adoption.HandleCoordinate != success.HandleCoordinate ||
                adoption.IntendedAccess != AccessFor(resource) || adoption.Observation is null)
            {
                throw new InvalidDataException($"The early custody obligation for {resource} is invalid.");
            }
            NativeMethods.RequireObservation(
                adoption.Observation,
                AccessFor(resource),
                resource != "frames" && resource != "anchor",
                adoption.Observation.FinalPath);
            if (index > 0)
            {
                var cleanupSource = state.CleanupSources.Single(item =>
                    StringComparer.Ordinal.Equals(item.Resource, resource));
                if (intent.HandleCoordinate != cleanupSource.SourceCoordinate)
                {
                    throw new InvalidDataException(
                        $"The early transfer intent for {resource} drifted from W's original coordinate.");
                }
                var successObservation = success.Observation ?? throw new InvalidDataException(
                        $"The early O read observation for {resource} is missing.");
                NativeMethods.RequireObservation(
                    successObservation,
                    AccessFor(resource),
                    resource == "private-root",
                    successObservation.FinalPath);
                NativeMethods.RequireSameObject(successObservation, adoption.Observation);
            }
            NativeMethods.RequireSameObject(adoption.Observation,
                index == 0
                    ? state.Core.Manifest!.Handles.Single(item => item.Resource == resource).Observation
                    : state.CleanupSources.Single(item => item.Resource == resource).SourceObservation);

            var intentIndex = IndexOfCustodyEvent(prefix, intent);
            var successIndex = IndexOfCustodyEvent(prefix, success);
            var adoptionIndex = IndexOfCustodyEvent(prefix, adoption);
            if (intentIndex >= successIndex || successIndex >= adoptionIndex)
                throw new InvalidDataException($"The early custody ordering for {resource} is invalid.");
            if (index < state.ExpectedPrecedingAckCount)
            {
                var ackIndex = IndexOfCustodyEvent(prefix, acknowledgments[index]);
                if (adoptionIndex >= ackIndex)
                    throw new InvalidDataException($"The early ACK ordering for {resource} is invalid.");
            }
        }

        for (var index = 0; index < acknowledgments.Length; index++)
        {
            var ack = acknowledgments[index];
            if (!StringComparer.Ordinal.Equals(ack.Resource, EarlyTransferResources[index]) ||
                ack.SourcePid != state.Core.OwnerPid || ack.TargetPid != state.Core.WriterPid)
            {
                throw new InvalidDataException("The early custody ACK sequence is invalid.");
            }
        }
        RequireSequentialCustodyOrdering(
            prefix,
            state.ExpectedSuccessCount,
            state.ExpectedPrecedingAckCount);
        if (prefix.Any(entry => entry.Event == "ack-issued" &&
                StringComparer.Ordinal.Equals(entry.Resource, state.Scenario.Resource)))
        {
            throw new InvalidDataException("The selected ordinary ACK was issued before early O death.");
        }
        RequireNoSelectedOrLaterAcks(prefix, selectedIndex);

        var selectedSuccess = successes[^1];
        state.SelectedNativeCoordinate = selectedSuccess.HandleCoordinate;
        state.ObservedSuccessCount = successes.Length;
        state.ObservedAckCount = acknowledgments.Length;
        if (selectedIndex > 0)
        {
            var source = state.CleanupSources[selectedIndex - 1];
            state.SelectedLeaseId = source.LeaseId;
            state.SelectedGeneration = source.Generation;
        }
    }

    private static void ValidateBoundReadiness(
        EarlyControllerState state,
        EarlyBoundReadiness readiness)
    {
        if (!StringComparer.Ordinal.Equals(readiness.Type, "noncooperative-ready") ||
            string.IsNullOrWhiteSpace(readiness.WireNonce) ||
            readiness.WriterPid != state.Core.WriterPid ||
            !StringComparer.Ordinal.Equals(readiness.Resource, state.Scenario.Resource) ||
            !StringComparer.Ordinal.Equals(readiness.Name, state.Scenario.Name) ||
            !StringComparer.Ordinal.Equals(readiness.Detail, state.Scenario.Name) ||
            readiness.Coordinate != ExpectedReadinessCoordinate(state))
        {
            throw new InvalidDataException("The early W readiness is not bound to the selected edge.");
        }
        if (IsParentScenario(state.Scenario))
        {
            if (readiness.LeaseId is not null || readiness.Generation is not null)
                throw new InvalidDataException("Parent readiness unexpectedly carried a W source lease.");
        }
        else if (readiness.LeaseId != state.SelectedLeaseId ||
                 readiness.Generation != state.SelectedGeneration)
        {
            throw new InvalidDataException("The early W readiness source lease drifted.");
        }
    }

    private static int IndexOfCustodyEvent(
        IReadOnlyList<CustodyEvent> prefix,
        CustodyEvent expected)
    {
        for (var index = 0; index < prefix.Count; index++)
        {
            if (prefix[index] == expected)
                return index;
        }
        throw new InvalidDataException("The expected custody entry is absent from its prefix.");
    }

    private static long? ExpectedReadinessCoordinate(EarlyControllerState state)
    {
        if (IsParentScenario(state.Scenario))
            return state.SelectedNativeCoordinate;
        return state.CleanupSources.Single(item =>
            StringComparer.Ordinal.Equals(item.Resource, state.Scenario.Resource)).SourceCoordinate;
    }

    private static void RequireEarlyArmed(EarlyControllerState state, EarlyArmed armed)
    {
        if (!StringComparer.Ordinal.Equals(armed.Type, "early-owner-armed") ||
            !StringComparer.Ordinal.Equals(armed.Nonce, state.Nonce) ||
            armed.SessionId != state.SessionId || armed.Scenario != BindScenario(state.Scenario) ||
            armed.OwnerPid != state.Core.OwnerPid || armed.WriterPid != state.Core.WriterPid ||
            armed.ControllerPid != Environment.ProcessId ||
            armed.Stage != EarlyStage.Armed)
        {
            throw new InvalidDataException("The early owner armed confirmation is invalid.");
        }
    }

    private static void ValidateEarlyBaseGraph(IReadOnlyList<EarlyHandleOffer> offers)
    {
        if (offers.Count != EarlyBaseResources.Length)
            throw new InvalidDataException("The early prearm requires exactly three base handles.");
        var map = offers.ToDictionary(item => item.Resource, StringComparer.Ordinal);
        RequireResourceKeys(map, EarlyBaseResources, "early base graph");
        foreach (var resource in EarlyBaseResources)
        {
            var offer = map[resource];
            if (offer.Coordinate is 0 or -1 || offer.DesiredAccess != AccessFor(resource))
                throw new InvalidDataException($"The early base offer for {resource} is invalid.");
            NativeMethods.RequireObservation(
                offer.Observation,
                offer.DesiredAccess,
                directory: true,
                offer.Observation.FinalPath);
        }
        var fixtureBase = map["fixture-base"].Observation;
        var runRoot = map["run-root"].Observation;
        var trustedParent = map["trusted-parent"].Observation;
        var expectedBase = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "WatchDog.CaptureIsolation.Windows.OwTransferPreflight");
        if (!NativeMethods.PathsEqual(fixtureBase.FinalPath, expectedBase) ||
            !ParentPathEquals(runRoot.FinalPath, fixtureBase.FinalPath) ||
            !ParentPathEquals(trustedParent.FinalPath, runRoot.FinalPath) ||
            !StringComparer.OrdinalIgnoreCase.Equals(LeafName(trustedParent.FinalPath), "trusted-parent") ||
            !LeafName(runRoot.FinalPath).StartsWith("run-", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The early prearm base paths do not form the fixture chain.");
        }
        if (offers.Select(item =>
                $"{item.Observation.VolumeSerialNumber:X16}:{item.Observation.FileId}")
            .Distinct(StringComparer.Ordinal).Count() != EarlyBaseResources.Length)
        {
            throw new InvalidDataException("The early prearm base handles alias objects.");
        }
    }

    private static void RequirePersistedPrefixExtension(
        IReadOnlyList<CustodyEvent> previous,
        IReadOnlyList<CustodyEvent> current)
    {
        ValidateCustodyPrefix(current);
        if (current.Count < previous.Count || current.Any(entry => !entry.Persisted))
            throw new InvalidDataException("The early custody prefix is incomplete or unpersisted.");
        for (var index = 0; index < previous.Count; index++)
        {
            if (previous[index] != current[index])
                throw new InvalidDataException("The early custody prefix rewrote an ACKed entry.");
        }
    }

    private static void RequirePrearmCustody(IReadOnlyList<CustodyEvent> prefix)
    {
        var forbidden = new HashSet<string>(StringComparer.Ordinal)
        {
            "transfer-intent",
            "transfer-success-obligation",
            "receiver-adopted",
            "ack-issued",
        };
        if (prefix.Any(entry => forbidden.Contains(entry.Event)))
            throw new InvalidDataException("The early prearm occurred after an O/W transfer began.");
    }

    private static void RequirePrearmResumeOrdering(
        IReadOnlyList<CustodyEvent> prefix,
        int controllerPid,
        int ownerPid,
        int writerPid,
        long writerCreationFileTime)
    {
        var prearmed = prefix.Where(entry =>
            entry.Event == "witness-prearmed-before-writer-resume").ToArray();
        var resumed = prefix.Where(entry =>
            entry.Event == "writer-primary-thread-resumed").ToArray();
        var firstIntent = prefix.FirstOrDefault(entry => entry.Event == "transfer-intent");
        if (prearmed.Length != 1 || resumed.Length != 1 || firstIntent is null ||
            prearmed[0].SourcePid != controllerPid || prearmed[0].TargetPid != ownerPid ||
            resumed[0].SourcePid != ownerPid || resumed[0].TargetPid != writerPid ||
            string.IsNullOrWhiteSpace(resumed[0].Detail))
        {
            throw new InvalidDataException("The early prearm/resume custody binding is incomplete.");
        }
        var evidence = JsonSerializer.Deserialize<LaunchEvidence>(resumed[0].Detail!) ??
            throw new InvalidDataException("The early resume evidence is malformed.");
        if (evidence.NativeProcessId != checked((uint)writerPid) ||
            evidence.NativePrimaryThreadId == 0 ||
            evidence.CreationFileTime != writerCreationFileTime || evidence.ResumeResult != 1)
        {
            throw new InvalidDataException("The early resume evidence does not bind exact W birth.");
        }
        var prearmedIndex = IndexOfCustodyEvent(prefix, prearmed[0]);
        var resumedIndex = IndexOfCustodyEvent(prefix, resumed[0]);
        var intentIndex = IndexOfCustodyEvent(prefix, firstIntent);
        if (!(prearmedIndex < resumedIndex && resumedIndex < intentIndex))
            throw new InvalidDataException("The early prearm/resume/transfer order is invalid.");
    }

    private static void RequireParentCompletedBeforeCleanup(
        IReadOnlyList<CustodyEvent> prefix,
        int ownerPid,
        int writerPid)
    {
        var transferEvents = prefix.Where(entry => entry.Event is
            "transfer-intent" or "transfer-success-obligation" or "receiver-adopted" or "ack-issued")
            .ToArray();
        if (transferEvents.Length != 4 ||
            transferEvents.Any(entry =>
                !StringComparer.Ordinal.Equals(entry.Resource, "trusted-parent")))
        {
            throw new InvalidDataException(
                "P cleanup preparation must follow only the completed parent transfer.");
        }
        var intent = transferEvents.Single(entry => entry.Event == "transfer-intent");
        var success = transferEvents.Single(entry => entry.Event == "transfer-success-obligation");
        var adoption = transferEvents.Single(entry => entry.Event == "receiver-adopted");
        var ack = transferEvents.Single(entry => entry.Event == "ack-issued");
        if (intent.SourcePid != ownerPid || intent.TargetPid != writerPid ||
            intent.IntendedAccess != NativeMethods.ParentTransferAccess ||
            success.SourcePid != ownerPid || success.TargetPid != writerPid ||
            success.IntendedAccess != NativeMethods.ParentTransferAccess ||
            success.HandleCoordinate is null or 0 or -1 ||
            adoption.SourcePid != writerPid || adoption.TargetPid != writerPid ||
            adoption.IntendedAccess != NativeMethods.ParentTransferAccess ||
            adoption.HandleCoordinate is null or 0 or -1 ||
            adoption.HandleCoordinate != success.HandleCoordinate || adoption.Observation is null ||
            ack.SourcePid != ownerPid || ack.TargetPid != writerPid)
        {
            throw new InvalidDataException("The completed parent transfer custody is invalid.");
        }
        NativeMethods.RequireObservation(
            adoption.Observation,
            NativeMethods.ParentTransferAccess,
            directory: true,
            adoption.Observation.FinalPath);
        var intentIndex = IndexOfCustodyEvent(prefix, intent);
        var successIndex = IndexOfCustodyEvent(prefix, success);
        var adoptionIndex = IndexOfCustodyEvent(prefix, adoption);
        var ackIndex = IndexOfCustodyEvent(prefix, ack);
        if (!(intentIndex < successIndex && successIndex < adoptionIndex && adoptionIndex < ackIndex))
            throw new InvalidDataException("The completed parent transfer custody is reordered.");
    }

    private static void RequireNoSelectedOrLaterAcks(
        IReadOnlyList<CustodyEvent> prefix,
        int selectedIndex)
    {
        foreach (var entry in prefix.Where(item => item.Event == "ack-issued"))
        {
            var resourceIndex = Array.IndexOf(EarlyTransferResources, entry.Resource);
            if (resourceIndex < 0 || resourceIndex >= selectedIndex)
                throw new InvalidDataException("The selected or a later ordinary ACK already exists.");
        }
    }

    private static void RequireSequentialCustodyOrdering(
        IReadOnlyList<CustodyEvent> prefix,
        int successCount,
        int precedingAckCount)
    {
        if (successCount < 1 || successCount > EarlyTransferResources.Length ||
            precedingAckCount != successCount - 1)
        {
            throw new InvalidDataException("The early sequential custody counts are invalid.");
        }

        for (var index = 1; index < successCount; index++)
        {
            var precedingResource = EarlyTransferResources[index - 1];
            var currentResource = EarlyTransferResources[index];
            var precedingAck = prefix.SingleOrDefault(entry =>
                entry.Event == "ack-issued" &&
                StringComparer.Ordinal.Equals(entry.Resource, precedingResource)) ??
                throw new InvalidDataException(
                    $"The ACK preceding {currentResource} is missing.");
            var currentIntent = prefix.SingleOrDefault(entry =>
                entry.Event == "transfer-intent" &&
                StringComparer.Ordinal.Equals(entry.Resource, currentResource)) ??
                throw new InvalidDataException(
                    $"The transfer intent for {currentResource} is missing.");
            if (IndexOfCustodyEvent(prefix, precedingAck) >=
                IndexOfCustodyEvent(prefix, currentIntent))
            {
                throw new InvalidDataException(
                    $"The transfer for {currentResource} began before the preceding ACK.");
            }
        }
    }

    private static void RequireStage(EarlyStage actual, EarlyStage expected, string operation)
    {
        if (actual != expected)
            throw new InvalidDataException($"{operation} is out of order or replayed.");
    }

    private static void RequireEarlyEnvelope(
        string actualType,
        string expectedType,
        string actualNonce,
        string expectedNonce,
        Guid sessionId,
        EarlyScenarioBinding actualScenario,
        ScenarioOptions expectedScenario,
        int actualOwnerPid,
        int? expectedOwnerPid,
        int actualControllerPid,
        int expectedControllerPid)
    {
        if (!StringComparer.Ordinal.Equals(actualType, expectedType) ||
            !StringComparer.Ordinal.Equals(actualNonce, expectedNonce) ||
            sessionId == Guid.Empty || actualScenario != BindScenario(expectedScenario) ||
            actualOwnerPid != expectedOwnerPid || actualControllerPid != expectedControllerPid)
        {
            throw new InvalidDataException($"The {expectedType} envelope is not bound to this session.");
        }
    }

    private static void RequireEarlySessionEnvelope(
        string actualType,
        string expectedType,
        string nonce,
        Guid sessionId,
        EarlyScenarioBinding scenario,
        int ownerPid,
        int controllerPid,
        int writerPid,
        EarlyControllerState state)
    {
        if (!StringComparer.Ordinal.Equals(actualType, expectedType) ||
            !StringComparer.Ordinal.Equals(nonce, state.Nonce) || sessionId != state.SessionId ||
            scenario != BindScenario(state.Scenario) || ownerPid != state.Core.OwnerPid ||
            controllerPid != Environment.ProcessId || writerPid != state.Core.WriterPid)
        {
            throw new InvalidDataException($"The {expectedType} message drifted from the prearmed session.");
        }
    }

    private static EarlyAck NewEarlyAck(
        string type,
        EarlyStage stage,
        EarlyControllerState state,
        int prefixCount,
        int handleCount) =>
        new(type, state.Nonce, state.SessionId, BindScenario(state.Scenario), stage,
            Environment.ProcessId, state.Core.OwnerPid!.Value, state.Core.WriterPid!.Value,
            prefixCount, handleCount);

    private static void RequireEarlyAck(
        EarlyAck ack,
        string type,
        EarlyStage stage,
        Guid sessionId,
        string nonce,
        EarlyScenarioBinding scenario,
        int controllerPid,
        int ownerPid,
        int writerPid,
        int prefixCount,
        int handleCount)
    {
        if (!StringComparer.Ordinal.Equals(ack.Type, type) ||
            !StringComparer.Ordinal.Equals(ack.Nonce, nonce) || ack.SessionId != sessionId ||
            ack.Scenario != scenario || ack.Stage != stage || ack.ControllerPid != controllerPid ||
            ack.OwnerPid != ownerPid || ack.WriterPid != writerPid ||
            ack.PrefixCount != prefixCount || ack.HandleCount != handleCount)
        {
            throw new InvalidDataException($"The {type} acknowledgment is invalid.");
        }
    }

    private static void ValidateEarlyScenario(ScenarioOptions scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        if (!StringComparer.Ordinal.Equals(scenario.Kind, "owner-death-edge") ||
            scenario.Resource is not ("trusted-parent" or "private-root" or "frames" or "anchor") ||
            string.IsNullOrWhiteSpace(scenario.Name))
        {
            throw new ArgumentException("An owner-death-edge scenario and selected resource are required.",
                nameof(scenario));
        }
    }

    private static bool IsParentScenario(ScenarioOptions scenario) =>
        StringComparer.Ordinal.Equals(scenario.Resource, "trusted-parent");

    private static EarlyScenarioBinding BindScenario(ScenarioOptions scenario) =>
        new(scenario.Name, scenario.Kind, scenario.Resource!);

    private static void RequireResourceKeys<T>(
        IReadOnlyDictionary<string, T> values,
        IReadOnlyList<string> required,
        string operation)
    {
        if (values.Count != required.Count || required.Any(resource => !values.ContainsKey(resource)))
            throw new InvalidDataException($"{operation} has an incomplete or expanded resource set.");
    }

    private static EarlyReceiptHandle ToEarlyReceiptHandle(WitnessHandleOffer offer) =>
        new(offer.Resource, offer.Coordinate, offer.DesiredAccess, offer.Observation);

    private static void RequireContractCheckFailure(Action action)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidOperationException("An early fixture-free contract rejection did not fail closed.");
    }

    internal sealed class EarlyOwnerSession : IAsyncDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly WitnessJsonChannel _channel;
        private readonly Guid _sessionId;
        private readonly string _nonce;
        private readonly ScenarioOptions _scenario;
        private readonly int _controllerPid;
        private readonly int _ownerPid;
        private readonly int _writerPid;
        private readonly long _writerCreationFileTime;
        private IReadOnlyList<CustodyEvent> _prefix;
        private IReadOnlyList<EarlySourceBinding> _sources = [];
        private EarlyStage _stage = EarlyStage.Prearmed;
        private bool _disposed;

        internal EarlyOwnerSession(
            NamedPipeClientStream pipe,
            WitnessJsonChannel channel,
            Guid sessionId,
            string nonce,
            ScenarioOptions scenario,
            int controllerPid,
            int ownerPid,
            int writerPid,
            long writerCreationFileTime,
            IReadOnlyList<CustodyEvent> prefix)
        {
            _pipe = pipe;
            _channel = channel;
            _sessionId = sessionId;
            _nonce = nonce;
            _scenario = scenario;
            _controllerPid = controllerPid;
            _ownerPid = ownerPid;
            _writerPid = writerPid;
            _writerCreationFileTime = writerCreationFileTime;
            _prefix = prefix.ToArray();
        }

        internal async Task PrepareCleanupAsync(
            IReadOnlyList<SourceOffer> offers,
            IReadOnlyList<CustodyEvent> prefix)
        {
            ThrowIfDisposed();
            RequireStage(_stage, EarlyStage.Prearmed, "owner early cleanup");
            if (IsParentScenario(_scenario))
                throw new InvalidDataException("The parent edge must not create a W cleanup manifest.");
            ArgumentNullException.ThrowIfNull(offers);
            ArgumentNullException.ThrowIfNull(prefix);
            RequirePersistedPrefixExtension(_prefix, prefix);
            RequireParentCompletedBeforeCleanup(prefix, _ownerPid, _writerPid);
            if (offers.Count != EarlyWriterResources.Length)
                throw new InvalidDataException("O must provide exactly three W source offers to P.");

            var sources = new EarlySourceBinding[offers.Count];
            for (var index = 0; index < offers.Count; index++)
            {
                var offer = offers[index];
                var resource = EarlyWriterResources[index];
                if (!StringComparer.Ordinal.Equals(offer.Resource, resource))
                    throw new InvalidDataException("O supplied W cleanup offers out of canonical order.");
                sources[index] = new(
                    _writerPid,
                    _writerPid,
                    _controllerPid,
                    _controllerPid,
                    resource,
                    offer.Name,
                    offer.Handle,
                    offer.LeaseId,
                    offer.Generation,
                    AccessFor(resource),
                    offer.Observation);
            }

            var manifest = new EarlyCleanupManifest(
                "early-cleanup",
                _nonce,
                _sessionId,
                BindScenario(_scenario),
                EarlyStage.CleanupPrepared,
                _ownerPid,
                _controllerPid,
                _writerPid,
                _writerCreationFileTime,
                sources,
                prefix.ToArray());
            await _channel.SendAsync(manifest, CancellationToken.None).ConfigureAwait(false);
            var ack = await _channel.ReceiveAsync<EarlyAck>(CancellationToken.None).ConfigureAwait(false);
            RequireEarlyAck(ack, "early-cleanup-ack", EarlyStage.CleanupPrepared,
                _sessionId, _nonce, BindScenario(_scenario), _controllerPid, _ownerPid,
                _writerPid, prefix.Count, EarlyWriterResources.Length);
            _sources = sources;
            _prefix = prefix.ToArray();
            _stage = EarlyStage.CleanupPrepared;
        }

        internal async Task NeverReturnBarrierAsync(
            IReadOnlyList<CustodyEvent> prefix,
            WireMessage noncooperativeReady)
        {
            ThrowIfDisposed();
            var expectedStage = IsParentScenario(_scenario)
                ? EarlyStage.Prearmed
                : EarlyStage.CleanupPrepared;
            RequireStage(_stage, expectedStage, "owner early barrier");
            ArgumentNullException.ThrowIfNull(prefix);
            ArgumentNullException.ThrowIfNull(noncooperativeReady);
            RequirePersistedPrefixExtension(_prefix, prefix);

            var selectedIndex = Array.IndexOf(EarlyTransferResources, _scenario.Resource!);
            var successes = prefix.Where(entry => entry.Event == "transfer-success-obligation").ToArray();
            if (successes.Length != selectedIndex + 1)
                throw new InvalidDataException("O reached the early barrier at the wrong native success count.");
            var selected = successes[^1];
            Guid? leaseId = null;
            long? generation = null;
            if (selectedIndex > 0)
            {
                var source = _sources[selectedIndex - 1];
                leaseId = source.LeaseId;
                generation = source.Generation;
            }
            var readiness = new EarlyBoundReadiness(
                noncooperativeReady.Type,
                noncooperativeReady.Nonce,
                noncooperativeReady.Pid,
                noncooperativeReady.Resource,
                noncooperativeReady.Name,
                noncooperativeReady.Handle,
                noncooperativeReady.LeaseId,
                noncooperativeReady.Generation,
                noncooperativeReady.Detail);
            var barrier = new EarlyBarrierManifest(
                "early-barrier",
                _nonce,
                _sessionId,
                BindScenario(_scenario),
                EarlyStage.BarrierAcknowledged,
                _ownerPid,
                _controllerPid,
                _writerPid,
                selectedIndex + 1,
                selectedIndex,
                selected.HandleCoordinate ?? throw new InvalidDataException(
                    "The selected native success has no coordinate."),
                leaseId,
                generation,
                readiness,
                prefix.ToArray());
            await _channel.SendAsync(barrier, CancellationToken.None).ConfigureAwait(false);
            var ack = await _channel.ReceiveAsync<EarlyAck>(CancellationToken.None).ConfigureAwait(false);
            RequireEarlyAck(ack, "early-barrier-ack", EarlyStage.BarrierAcknowledged,
                _sessionId, _nonce, BindScenario(_scenario), _controllerPid, _ownerPid,
                _writerPid, prefix.Count,
                IsParentScenario(_scenario) ? EarlyBaseResources.Length : HandleOrder.Length);
            _stage = EarlyStage.BarrierAcknowledged;
            await _channel.SendAsync(
                new EarlyArmed(
                    "early-owner-armed",
                    _nonce,
                    _sessionId,
                    BindScenario(_scenario),
                    EarlyStage.Armed,
                    _ownerPid,
                    _controllerPid,
                    _writerPid),
                CancellationToken.None).ConfigureAwait(false);
            _stage = EarlyStage.Armed;

            var retentionAnchor = new object[] { _pipe, _channel, prefix, noncooperativeReady, barrier };
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            GC.KeepAlive(retentionAnchor);
            throw new InvalidOperationException("The early owner-death barrier unexpectedly resumed.");
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;
            await _channel.DisposeAsync().ConfigureAwait(false);
            await _pipe.DisposeAsync().ConfigureAwait(false);
        }

        private void ThrowIfDisposed() =>
            ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private enum EarlyStage
    {
        None = 0,
        Prearmed = 1,
        CleanupPrepared = 2,
        BarrierAcknowledged = 3,
        Armed = 4,
    }

    private sealed record EarlyScenarioBinding(string Name, string Kind, string Resource);

    private sealed record EarlyHandleOffer(
        string Resource,
        long Coordinate,
        uint DesiredAccess,
        HandleObservation Observation);

    private sealed record EarlyPrearmManifest(
        string Type,
        string Nonce,
        Guid SessionId,
        EarlyScenarioBinding Scenario,
        int OwnerPid,
        int ControllerPid,
        long WriterProcessCoordinate,
        int WriterPid,
        long WriterCreationFileTime,
        IReadOnlyList<EarlyHandleOffer> BaseHandles,
        IReadOnlyList<CustodyEvent> Prefix);

    private sealed record EarlySourceBinding(
        int CreatorPid,
        int SourceHolderPid,
        int InvokingPid,
        int TargetPid,
        string Resource,
        string Name,
        long SourceCoordinate,
        Guid LeaseId,
        long Generation,
        uint DesiredAccess,
        HandleObservation SourceObservation);

    private sealed record EarlyCleanupManifest(
        string Type,
        string Nonce,
        Guid SessionId,
        EarlyScenarioBinding Scenario,
        EarlyStage Stage,
        int OwnerPid,
        int ControllerPid,
        int WriterPid,
        long WriterCreationFileTime,
        IReadOnlyList<EarlySourceBinding> Sources,
        IReadOnlyList<CustodyEvent> Prefix);

    private sealed record EarlyBoundReadiness(
        string Type,
        string WireNonce,
        int WriterPid,
        string? Resource,
        string? Name,
        long? Coordinate,
        Guid? LeaseId,
        long? Generation,
        string? Detail);

    private sealed record EarlyBarrierManifest(
        string Type,
        string Nonce,
        Guid SessionId,
        EarlyScenarioBinding Scenario,
        EarlyStage Stage,
        int OwnerPid,
        int ControllerPid,
        int WriterPid,
        int ExpectedSuccessCount,
        int ExpectedPrecedingAckCount,
        long SelectedNativeCoordinate,
        Guid? SelectedLeaseId,
        long? SelectedGeneration,
        EarlyBoundReadiness Readiness,
        IReadOnlyList<CustodyEvent> Prefix);

    private sealed record EarlyAck(
        string Type,
        string Nonce,
        Guid SessionId,
        EarlyScenarioBinding Scenario,
        EarlyStage Stage,
        int ControllerPid,
        int OwnerPid,
        int WriterPid,
        int PrefixCount,
        int HandleCount);

    private sealed record EarlyArmed(
        string Type,
        string Nonce,
        Guid SessionId,
        EarlyScenarioBinding Scenario,
        EarlyStage Stage,
        int OwnerPid,
        int ControllerPid,
        int WriterPid);

    private sealed record EarlyReceiptHandle(
        string Resource,
        long Coordinate,
        uint DesiredAccess,
        HandleObservation Observation);

    private sealed record EarlyCleanupReceipt(
        int CreatorPid,
        int SourceHolderPid,
        int InvokingPid,
        int TargetPid,
        string Resource,
        string Name,
        long SourceCoordinate,
        Guid LeaseId,
        long Generation,
        uint DesiredAccess,
        HandleObservation SourceObservation,
        long RetainedCoordinate,
        HandleObservation RetainedObservation);

    private sealed class EarlyControllerState(
        ControllerState core,
        ScenarioOptions scenario)
    {
        internal ControllerState Core { get; } = core;
        internal ScenarioOptions Scenario { get; } = scenario;
        internal Guid SessionId { get; set; }
        internal string Nonce { get; set; } = string.Empty;
        internal long WriterProcessCoordinate { get; set; }
        internal KernelHandleObservation? WriterProcessCapability { get; set; }
        internal EarlyStage Stage { get; set; }
        internal IReadOnlyList<CustodyEvent> PrearmPrefix { get; set; } = [];
        internal IReadOnlyList<CustodyEvent> CleanupPrefix { get; set; } = [];
        internal IReadOnlyList<EarlySourceBinding> CleanupSources { get; set; } = [];
        internal List<EarlyReceiptHandle> PrearmHandles { get; } = [];
        internal List<EarlyCleanupReceipt> PCleanupReads { get; } = [];
        internal DateTimeOffset? PrearmAckedAtUtc { get; set; }
        internal DateTimeOffset? CleanupManifestAckedAtUtc { get; set; }
        internal DateTimeOffset? BarrierAckedAtUtc { get; set; }
        internal bool BarrierArmed { get; set; }
        internal int ExpectedSuccessCount { get; set; }
        internal int ExpectedPrecedingAckCount { get; set; }
        internal int ObservedSuccessCount { get; set; }
        internal int ObservedAckCount { get; set; }
        internal long? SelectedNativeCoordinate { get; set; }
        internal Guid? SelectedLeaseId { get; set; }
        internal long? SelectedGeneration { get; set; }
        internal EarlyBoundReadiness? BoundReadiness { get; set; }
    }
}
