using System.Numerics;
using WatchDog.Core.Capture;
using WatchDog.Core.Devices;
using WatchDog.Core.Recording;

namespace WatchDog.Core.Tutorial;

// These packets describe a possible future owner-gated command. Nothing here calls an output.
public enum PauseCommandAction { Pause, Resume }
public enum PauseOutputKind { Screen, Camera }
public enum PauseStreamKind { ScreenVideo, ProductAudioTrack, CameraVideo, CameraCompatibilityAudio }
public enum PauseDecodedUnitKind { VideoFrame, AudioSample }
public enum PauseOutputEffect { Applied, Rejected, Unknown }
public enum PauseModelPhase { Running, Paused, CleanupPending }
public enum PauseModelDisposition { RejectedNoEffect, ModeledCommitted, StopAndFinalizeAllOwnedOutputs }
public enum PauseTerminalIntent { None, Cancel, Stop, SourceLost, DeviceLost, Shutdown, MediaEvidenceLost }
public enum PauseLivenessRequirement { None, SuspendProgressAfterAggregateCommit, RearmProgressAfterAggregateCommit }

public sealed record PauseStreamId(PauseStreamKind Kind, int Index);
// Input positions are video frames or audio samples, never encoded packet PTS.
public sealed record PauseInputClock(Guid TransformId, long InputIndexAtOrigin,
    long CaptureOriginNumerator, long CaptureOriginDenominator,
    long SecondsPerInputNumerator, long SecondsPerInputDenominator);
// Supplied fixed decoded-unit timing, not native timing or input-lineage proof.
public sealed record PauseMediaTimingDeclaration(Guid TimingId, PauseDecodedUnitKind UnitKind,
    long SecondsPerDecodedUnitNumerator, long SecondsPerDecodedUnitDenominator);
public sealed record PauseStreamAnchor(PauseStreamId Id, PauseInputClock InputClock,
    PauseMediaTimingDeclaration MediaTiming);
// Pause: first excluded input. Resume: first admitted input in the same frozen clock.
public sealed record PauseStreamEffect(PauseStreamId Id, Guid TransformId, long InputBoundaryIndex);
public sealed record PauseInputSpan(long StartIndex, long EndIndexExclusive);
public sealed record PauseMediaPacketWitness(long PacketIdentity, Guid MediaTimingId, long Pts, long Duration,
    long TimebaseNumerator, long TimebaseDenominator, long DecodedInputUnits,
    long LeadingTrimUnits, long TrailingTrimUnits, IReadOnlyList<PauseInputSpan> CaptureSupport);
public sealed record PauseStreamMediaCoverage(PauseStreamId Id, IReadOnlyList<PauseMediaPacketWitness> Packets);
public sealed record PauseOutputMediaCoverage(Guid OperationId, long Sequence, TutorialTimelineIdentity Identity,
    PauseCommandAction Action, PauseOutputKind Kind, Guid OutputEpoch, long RegistrationGeneration,
    IReadOnlyList<PauseStreamMediaCoverage> Streams)
{
    public override string ToString() => nameof(PauseOutputMediaCoverage);
}
public sealed record PauseOutputCapability(
    PauseOutputKind Kind, Guid OutputEpoch, bool CanPause, bool CanResume,
    IReadOnlyList<PauseStreamAnchor> Streams, TutorialTimelineIdentity OwnerIdentity, string? CameraDeviceId);
public sealed record PauseOutputAck(
    Guid OperationId, long Sequence, TutorialTimelineIdentity Identity, PauseCommandAction Action,
    PauseOutputKind Kind, Guid OutputEpoch, PauseOutputEffect Effect, TimeSpan EffectiveBoundary,
    IReadOnlyList<PauseStreamEffect> Streams);
public sealed record PauseOutputRelease(
    Guid OperationId, long Sequence, TutorialTimelineIdentity Identity, PauseOutputKind Kind, Guid OutputEpoch,
    bool ResourcesReleased, bool FinalizationAccounted, bool FinalizationSucceeded, bool OriginalPreserved);

public sealed class PauseOutputParticipant
{
    internal PauseOutputParticipant(PauseOutputCapability capability, PauseStreamAnchor[] frozenStreams)
    {
        Kind = capability.Kind;
        OutputEpoch = capability.OutputEpoch;
        Streams = Array.AsReadOnly(frozenStreams);
    }

    public PauseOutputKind Kind { get; }
    public Guid OutputEpoch { get; }
    public IReadOnlyList<PauseStreamAnchor> Streams { get; }
}

public sealed class PauseModelState
{
    internal PauseModelState(CaptureSessionContext context, TutorialTimelineIdentity identity, PauseModelPhase phase, long sequence,
        TimeSpan lastBoundary, Guid lastOperationId, IReadOnlyList<PauseOutputParticipant> outputs,
        PauseCommand? terminalCommand)
    {
        Context = context;
        Identity = identity;
        Phase = phase;
        Sequence = sequence;
        LastBoundary = lastBoundary;
        LastOperationId = lastOperationId;
        Outputs = outputs;
        TerminalCommand = terminalCommand;
    }

    public CaptureSessionContext Context { get; }
    public TutorialTimelineIdentity Identity { get; }
    public PauseModelPhase Phase { get; }
    public long Sequence { get; }
    public TimeSpan LastBoundary { get; }
    public Guid LastOperationId { get; }
    public bool IsTerminal => Phase == PauseModelPhase.CleanupPending;
    internal IReadOnlyList<PauseOutputParticipant> Outputs { get; }
    internal PauseCommand? TerminalCommand { get; }
}

public static class PauseCommandModel
{
    public static PauseModelState CreateInitial(CaptureSessionContext context, Guid epochId)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (epochId == Guid.Empty || !PauseCommandPolicy.ValidContext(context))
            throw new ArgumentException("A manual exact-source session and nonempty epoch are required.", nameof(context));
        return new(context, TutorialTimelineIdentity.From(context, epochId), PauseModelPhase.Running,
            0, TimeSpan.Zero, Guid.Empty, Array.Empty<PauseOutputParticipant>(), null);
    }
}

public sealed class PauseCommand
{
    internal PauseCommand(CaptureSessionContext context, Guid operationId, long sequence, TutorialTimelineIdentity identity,
        PauseCommandAction action, PauseModelState priorState, PauseOutputParticipant[] outputs)
    {
        Context = context;
        OperationId = operationId;
        Sequence = sequence;
        Identity = identity;
        Action = action;
        PriorState = priorState;
        Outputs = Array.AsReadOnly(outputs);
    }

    public CaptureSessionContext Context { get; }
    public Guid OperationId { get; }
    public long Sequence { get; }
    public TutorialTimelineIdentity Identity { get; }
    public PauseCommandAction Action { get; }
    public IReadOnlyList<PauseOutputParticipant> Outputs { get; }
    public bool NativePauseProven => false;
    public bool NativeAlignmentProven => false;
    public bool NativeLivenessAdmitted => false;
    internal PauseModelState PriorState { get; }
}

public sealed record PauseCommandPlanResult(PauseCommand? Command, CaptureFailure? Failure);

/// <summary>Pure admission against a caller-supplied snapshot and complete output manifest.</summary>
public static class PauseCommandPolicy
{
    public static PauseCommandPlanResult Plan(TutorialRecordingPlan plan, CaptureLifecycleSnapshot snapshot,
        PauseModelState state, PauseCommandAction action, Guid operationId, long sequence,
        IReadOnlyList<PauseOutputCapability> capabilities)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(capabilities);
        var context = snapshot.Context;
        if (context is null || !ValidContext(context) || !SameRequest(plan.Request, context.Request) ||
            snapshot.Status != CaptureLifecycleStatus.Active || snapshot.Readiness != CaptureReadiness.Available ||
            snapshot.Health != CaptureHealth.Healthy || snapshot.LastFailure is not null ||
            state.IsTerminal || !SameContext(state.Context, context) ||
            state.Identity != TutorialTimelineIdentity.From(context, state.Identity.EpochId) ||
            operationId == Guid.Empty || operationId == state.LastOperationId || state.Sequence == long.MaxValue ||
            sequence <= 0 || sequence != state.Sequence + 1 || !Enum.IsDefined(action) ||
            (action == PauseCommandAction.Pause ? state.Phase != PauseModelPhase.Running : state.Phase != PauseModelPhase.Paused))
            return Reject("tutorial.pause.admission-rejected");

        var webcam = plan.Request.Webcam;
        if (webcam is not null && (!context.AdmittedDevices.TryGet(CaptureDeviceRole.Webcam, out var admitted) ||
            admitted?.SelectionMode != CaptureDeviceSelectionMode.Exact ||
            !StringComparer.Ordinal.Equals(admitted.RequestedDeviceId, webcam.DeviceId)))
            return Reject("tutorial.pause.camera-not-admitted");
        if (webcam is null && context.AdmittedDevices.TryGet(CaptureDeviceRole.Webcam, out _))
            return Reject("tutorial.pause.camera-unexpected");

        var expectedKinds = webcam is null
            ? new[] { PauseOutputKind.Screen }
            : new[] { PauseOutputKind.Screen, PauseOutputKind.Camera };
        if (capabilities.Count != expectedKinds.Length || capabilities.Any(c => c is null) ||
            capabilities.Select(c => c.Kind).Distinct().Count() != capabilities.Count ||
            capabilities.Select(c => c.OutputEpoch).Distinct().Count() != capabilities.Count)
            return Reject("tutorial.pause.output-coverage");

        var participants = new List<PauseOutputParticipant>(expectedKinds.Length);
        foreach (var kind in expectedKinds)
        {
            var capability = capabilities.SingleOrDefault(c => c.Kind == kind);
            if (capability is null || capability.OutputEpoch == Guid.Empty ||
                capability.OwnerIdentity != state.Identity ||
                (kind == PauseOutputKind.Camera
                    ? !StringComparer.Ordinal.Equals(capability.CameraDeviceId, webcam!.DeviceId)
                    : capability.CameraDeviceId is not null) ||
                !capability.CanPause || !capability.CanResume || capability.Streams is null ||
                !TryFreezeExactStreams(kind, plan, capability.Streams, out var frozenStreams))
                return Reject("tutorial.pause.capability-incomplete");
            participants.Add(new PauseOutputParticipant(capability, frozenStreams));
        }

        // Reusing an output epoch cannot change either frozen timing declaration.
        if (participants.Any(p => state.Outputs.Any(previous =>
            previous.Kind == p.Kind && previous.OutputEpoch == p.OutputEpoch && !SameOutput(p, previous))))
            return Reject("tutorial.pause.output-anchor-changed");
        // A resumed recording must name the very outputs that committed the pause.
        if (action == PauseCommandAction.Resume &&
            (state.Outputs.Count != participants.Count ||
             participants.Where((p, i) => !SameOutput(p, state.Outputs[i])).Any()))
            return Reject("tutorial.pause.output-epoch-changed");
        return new(new PauseCommand(context, operationId, sequence, state.Identity, action, state, participants.ToArray()), null);
    }

    internal static bool ValidContext(CaptureSessionContext context) =>
        context.Request is not null && context.AdmittedDevices is not null &&
        context.SessionId != Guid.Empty && context.Generation > 0 &&
        context.Request.RequestId != Guid.Empty && context.Request.Initiation == CaptureInitiation.Manual &&
        context.Request.StartPolicy?.Recording.Mode == RecordingMode.SessionRecording &&
        context.RequestedTarget == context.Request.Target &&
        TutorialRecordingPolicy.SameBoundary(context.Request.Target, context.ActualTarget);

    private static bool SameRequest(CaptureRequest expected, CaptureRequest actual) =>
        expected == actual;

    internal static bool SameContext(CaptureSessionContext expected, CaptureSessionContext actual) =>
        expected.SessionId == actual.SessionId && expected.Generation == actual.Generation &&
        SameRequest(expected.Request, actual.Request) && expected.RequestedTarget == actual.RequestedTarget &&
        expected.ActualTarget == actual.ActualTarget;

    private static bool TryFreezeExactStreams(PauseOutputKind kind, TutorialRecordingPlan plan,
        IReadOnlyList<PauseStreamAnchor> streams, out PauseStreamAnchor[] frozen)
    {
        frozen = [];
        var expected = kind == PauseOutputKind.Screen
            ? new[] { new PauseStreamId(PauseStreamKind.ScreenVideo, 0) }
                .Concat(plan.AudioTracks.Encoders.Select(route => new PauseStreamId(PauseStreamKind.ProductAudioTrack, route.OutputTrackIndex))).ToArray()
            : new[] { new PauseStreamId(PauseStreamKind.CameraVideo, 0),
                new PauseStreamId(PauseStreamKind.CameraCompatibilityAudio, 5) };
        // Read caller storage once with a membership-derived bound. Validate and
        // retain this same snapshot; enumeration cannot swap in different timing.
        var snapshot = new PauseStreamAnchor[expected.Length];
        try
        {
            if (streams.Count != snapshot.Length)
                return false;
            for (var i = 0; i < snapshot.Length; i++)
                snapshot[i] = streams[i];
        }
        catch
        {
            return false;
        }
        if (!snapshot.All(s => s is not null && s.Id is not null &&
            s.InputClock is
            {
                TransformId: var transform, CaptureOriginDenominator: > 0,
                SecondsPerInputNumerator: > 0, SecondsPerInputDenominator: > 0
            } &&
            transform != Guid.Empty && expected.Contains(s.Id) &&
            s.MediaTiming is
            {
                TimingId: var timingId, SecondsPerDecodedUnitNumerator: > 0,
                SecondsPerDecodedUnitDenominator: > 0,
            } && timingId != Guid.Empty &&
            s.MediaTiming.UnitKind == (s.Id.Kind is PauseStreamKind.ScreenVideo or PauseStreamKind.CameraVideo
                ? PauseDecodedUnitKind.VideoFrame : PauseDecodedUnitKind.AudioSample)) ||
            snapshot.Select(s => s.Id).Distinct().Count() != expected.Length)
            return false;
        frozen = snapshot;
        return true;
    }

    internal static bool SameOutput(PauseOutputParticipant a, PauseOutputParticipant b) =>
        a.Kind == b.Kind && a.OutputEpoch == b.OutputEpoch &&
        a.Streams.Count == b.Streams.Count &&
        a.Streams.All(anchor => b.Streams.Contains(anchor));

    private static PauseCommandPlanResult Reject(string code) =>
        new(null, new(CaptureFailureKind.Unsupported, code,
            "The modeled pause command lacks a complete, current, exact session and output boundary."));
}

public sealed class PauseAcknowledgmentResult
{
    internal PauseAcknowledgmentResult(PauseModelDisposition disposition, PauseModelState state,
        IReadOnlyList<PauseOutputParticipant> cleanupOutputs, PauseLivenessRequirement livenessRequirement)
    {
        Disposition = disposition;
        State = state;
        CleanupOutputs = cleanupOutputs;
        LivenessRequirement = livenessRequirement;
    }

    public PauseModelDisposition Disposition { get; }
    public PauseModelState State { get; }
    public IReadOnlyList<PauseOutputParticipant> CleanupOutputs { get; }
    public PauseLivenessRequirement LivenessRequirement { get; }
    public bool NativePauseProven => false;
    public bool NativeAlignmentProven => false;
    public bool NativeLivenessAdmitted => false;
}

/// <summary>Reduces complete fake receipts only; dispatch/effect certainty is supplied by the owner.</summary>
public static class PauseAcknowledgmentPolicy
{
    public static PauseAcknowledgmentResult Evaluate(PauseCommand command, PauseModelState currentState,
        bool dispatchStarted, IReadOnlyList<PauseOutputAck> acknowledgments,
        PauseTerminalIntent terminalIntent = PauseTerminalIntent.None)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(currentState);
        ArgumentNullException.ThrowIfNull(acknowledgments);
        // A late operation from another capture cannot change this owner's state or
        // authorize cleanup receipts for its outputs. Its original owner must reconcile it.
        if (currentState.Identity != command.Identity || !PauseCommandPolicy.SameContext(currentState.Context, command.Context))
            return new(PauseModelDisposition.RejectedNoEffect, currentState,
                Array.Empty<PauseOutputParticipant>(), PauseLivenessRequirement.None);
        if (currentState.IsTerminal)
        {
            if (!dispatchStarted)
                return Terminal(currentState);
            var allOutputs = AllOwnedOutputs(currentState.Outputs, command.Outputs);
            if (allOutputs.Count == currentState.Outputs.Count)
                return Terminal(currentState);
            // A late same-capture operation with a previously unseen output epoch
            // reopens cleanup evidence for every epoch, without reviving recording.
            return Terminal(new PauseModelState(currentState.Context, currentState.Identity, PauseModelPhase.CleanupPending,
                Math.Max(currentState.Sequence, command.Sequence), currentState.LastBoundary,
                command.OperationId, allOutputs, command));
        }
        if (!dispatchStarted)
            return new(PauseModelDisposition.RejectedNoEffect, currentState,
                Array.Empty<PauseOutputParticipant>(), PauseLivenessRequirement.None);
        if (currentState != command.PriorState || terminalIntent != PauseTerminalIntent.None ||
            !Enum.IsDefined(terminalIntent) || !ValidAcks(command, currentState, acknowledgments, out var boundary))
            return Terminal(new PauseModelState(currentState.Context, currentState.Identity, PauseModelPhase.CleanupPending,
                Math.Max(currentState.Sequence, command.Sequence), currentState.LastBoundary,
                command.OperationId, AllOwnedOutputs(currentState.Outputs, command.Outputs), command));

        var phase = command.Action == PauseCommandAction.Pause ? PauseModelPhase.Paused : PauseModelPhase.Running;
        var next = new PauseModelState(currentState.Context, currentState.Identity, phase, command.Sequence, boundary,
            command.OperationId,
            command.Outputs, null);
        var liveness = phase == PauseModelPhase.Paused
            ? PauseLivenessRequirement.SuspendProgressAfterAggregateCommit
            : PauseLivenessRequirement.RearmProgressAfterAggregateCommit;
        return new(PauseModelDisposition.ModeledCommitted, next,
            Array.Empty<PauseOutputParticipant>(), liveness);
    }

    private static bool ValidAcks(PauseCommand command, PauseModelState state,
        IReadOnlyList<PauseOutputAck> acknowledgments, out TimeSpan boundary)
    {
        boundary = TimeSpan.Zero;
        if (acknowledgments.Count != command.Outputs.Count || acknowledgments.Any(a => a is null) ||
            acknowledgments.Select(a => a.Kind).Distinct().Count() != command.Outputs.Count)
            return false;
        TimeSpan? common = null;
        foreach (var output in command.Outputs)
        {
            var ack = acknowledgments.SingleOrDefault(a => a.Kind == output.Kind);
            if (ack is null || ack.OperationId != command.OperationId || ack.Sequence != command.Sequence ||
                ack.Identity != command.Identity || ack.Action != command.Action ||
                ack.OutputEpoch != output.OutputEpoch || ack.Effect != PauseOutputEffect.Applied ||
                ack.EffectiveBoundary <= state.LastBoundary || ack.Streams is null ||
                ack.Streams.Count != output.Streams.Count || ack.Streams.Any(s => s is null || s.Id is null) ||
                ack.Streams.Select(s => s.Id).Distinct().Count() != output.Streams.Count ||
                (common is not null && common != ack.EffectiveBoundary))
                return false;
            foreach (var anchor in output.Streams)
            {
                var packet = ack.Streams.SingleOrDefault(s => s.Id == anchor.Id);
                if (packet is null || packet.TransformId != anchor.InputClock.TransformId ||
                    !MapsExactly(anchor.InputClock, packet.InputBoundaryIndex, ack.EffectiveBoundary))
                    return false;
            }
            common = ack.EffectiveBoundary;
        }
        boundary = common ?? TimeSpan.Zero;
        return common is not null;
    }

    private static bool MapsExactly(PauseInputClock clock, long index, TimeSpan boundary)
    {
        var numerator = ((BigInteger)clock.CaptureOriginNumerator * clock.SecondsPerInputDenominator +
            ((BigInteger)index - clock.InputIndexAtOrigin) * clock.SecondsPerInputNumerator *
            clock.CaptureOriginDenominator) * TimeSpan.TicksPerSecond;
        var denominator = (BigInteger)clock.CaptureOriginDenominator * clock.SecondsPerInputDenominator;
        var ticks = BigInteger.DivRem(numerator, denominator, out var remainder);
        return remainder.IsZero && ticks >= TimeSpan.MinValue.Ticks && ticks <= TimeSpan.MaxValue.Ticks &&
            (long)ticks == boundary.Ticks;
    }

    private static PauseAcknowledgmentResult Terminal(PauseModelState state) =>
        new(PauseModelDisposition.StopAndFinalizeAllOwnedOutputs, state,
            state.Outputs, PauseLivenessRequirement.None);

    private static IReadOnlyList<PauseOutputParticipant> AllOwnedOutputs(
        IReadOnlyList<PauseOutputParticipant> current, IReadOnlyList<PauseOutputParticipant> commanded) =>
        Array.AsReadOnly(current.Concat(commanded)
            .DistinctBy(output => (output.Kind, output.OutputEpoch)).ToArray());
}

public sealed record PauseCleanupResult(bool CleanupCompleted, bool FinalizationWarning);

public static class PauseCleanupPolicy
{
    public static PauseCleanupResult Evaluate(PauseAcknowledgmentResult terminal,
        IReadOnlyList<PauseOutputRelease> releases)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(releases);
        var command = terminal.State.TerminalCommand;
        if (terminal.Disposition != PauseModelDisposition.StopAndFinalizeAllOwnedOutputs ||
            command is null || releases.Count != terminal.State.Outputs.Count || releases.Any(r => r is null) ||
            releases.Select(r => (r.Kind, r.OutputEpoch)).Distinct().Count() != terminal.State.Outputs.Count)
            return new(false, false);
        var warning = false;
        foreach (var output in terminal.State.Outputs)
        {
            var receipt = releases.SingleOrDefault(r => r.Kind == output.Kind && r.OutputEpoch == output.OutputEpoch);
            if (receipt is null || receipt.OperationId != command.OperationId ||
                receipt.Sequence != command.Sequence || receipt.Identity != command.Identity ||
                receipt.Identity != terminal.State.Identity ||
                receipt.OutputEpoch != output.OutputEpoch || !receipt.ResourcesReleased ||
                !receipt.FinalizationAccounted || (!receipt.FinalizationSucceeded && !receipt.OriginalPreserved))
                return new(false, false);
            warning |= !receipt.FinalizationSucceeded;
        }
        return new(true, warning);
    }
}
