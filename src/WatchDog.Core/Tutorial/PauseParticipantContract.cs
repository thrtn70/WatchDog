using System.Numerics;
using WatchDog.Core.Capture;

namespace WatchDog.Core.Tutorial;

/// <summary>Opaque managed registration; it grants no native callback or output authority.</summary>
public sealed class PauseParticipantRegistration
{
    internal PauseParticipantRegistration(PauseCommand command, PauseOutputParticipant participant, long registrationGeneration)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(participant);
        if (registrationGeneration <= 0 || !command.Outputs.Contains(participant))
            throw new ArgumentException("A current command participant and positive registration generation are required.");
        Command = command;
        Participant = participant;
        RegistrationGeneration = registrationGeneration;
    }

    public PauseCommand Command { get; }
    public CaptureSessionContext Context => Command.Context;
    public PauseOutputParticipant Participant { get; }
    public long RegistrationGeneration { get; }
    public PauseOutputKind Kind => Participant.Kind;
    public Guid OutputEpoch => Participant.OutputEpoch;
    public IReadOnlyList<PauseStreamAnchor> Streams => Participant.Streams;
    public override string ToString() => nameof(PauseParticipantRegistration);
}

/// <summary>Bounded fake-evidence owner. Never registered with a production capture backend.</summary>
public sealed class PauseParticipantOwner
{
    private readonly object _sync = new();
    private readonly CaptureSessionContext _context;
    private readonly int _maxRegistrations;
    private readonly List<PauseParticipantEvidenceAdapter> _operations = [];
    private readonly Dictionary<PauseParticipantRegistration, MediaSlot> _media = [];
    private long _registrationGeneration;
    private PauseModelState _currentState;
    private PauseAcknowledgmentResult? _terminalResult;
    private PauseParticipantCleanupAdapter? _cleanup;

    public PauseParticipantOwner(CaptureSessionContext context, PauseModelState initialState, int maxRegistrations = 64)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(initialState);
        if (maxRegistrations is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(maxRegistrations));
        if (!PauseCommandPolicy.ValidContext(context) || !PauseCommandPolicy.SameContext(context, initialState.Context) ||
            initialState.Identity.EpochId == Guid.Empty ||
            initialState.Identity != TutorialTimelineIdentity.From(context, initialState.Identity.EpochId) ||
            initialState.Phase != PauseModelPhase.Running || initialState.Sequence != 0 ||
            initialState.LastBoundary != TimeSpan.Zero || initialState.LastOperationId != Guid.Empty ||
            initialState.Outputs.Count != 0 || initialState.TerminalCommand is not null)
            throw new ArgumentException("A pristine initial state with the exact frozen context is required.", nameof(initialState));
        _context = context;
        _maxRegistrations = maxRegistrations;
        _currentState = initialState;
    }

    public PauseModelState CurrentState { get { lock (_sync) return _currentState; } }

    public PauseParticipantEvidenceAdapter? CreateOperation(PauseCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_sync)
        {
            if (_currentState.IsTerminal || command.PriorState != _currentState ||
                !PauseCommandPolicy.SameContext(_context, command.Context) ||
                _operations.Any(operation => operation.Command.OperationId == command.OperationId) ||
                command.Outputs.Count > _maxRegistrations - _registrationGeneration)
                return null;
            // Reserve every output together; retained registrations are never evicted or recycled.
            var gates = command.Outputs.Select(output => new PauseParticipantCallbackGate(
                new(command, output, ++_registrationGeneration))).ToArray();
            var adapter = new PauseParticipantEvidenceAdapter(this, command, gates);
            foreach (var gate in gates)
                _media.Add(gate.Registration, new MediaSlot());
            _operations.Add(adapter);
            return adapter;
        }
    }

    public PauseParticipantCleanupAdapter? CreateCleanup(PauseAcknowledgmentResult terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        lock (_sync)
        {
            if (terminal != _terminalResult || terminal.State != _currentState || !_currentState.IsTerminal)
                return null;
            return _cleanup ??= new(this, terminal, terminal.CleanupOutputs.Select(output =>
                new PauseParticipantCleanupTicket(output, Array.AsReadOnly(_operations.SelectMany(operation => operation.Gates)
                    .Where(gate => gate.Registration.Kind == output.Kind && gate.Registration.OutputEpoch == output.OutputEpoch)
                    .ToArray()))).ToArray());
        }
    }

    internal PauseAcknowledgmentResult? Reduce(PauseParticipantEvidenceAdapter adapter, bool dispatchStarted,
        PauseTerminalIntent intent)
    {
        lock (_sync)
        {
            if (!_operations.Contains(adapter))
                return null;
            if (dispatchStarted && !adapter.DispatchStarted && _currentState.IsTerminal)
                _cleanup = null; // A late operation on the same epoch adds fresh evidence obligations.
            adapter.DispatchStarted |= dispatchStarted;
            if (adapter.TerminalIntent == PauseTerminalIntent.None && intent != PauseTerminalIntent.None)
                adapter.TerminalIntent = intent;
            if (!_currentState.IsTerminal && adapter.TerminalIntent == PauseTerminalIntent.None &&
                adapter.LastResult?.Disposition == PauseModelDisposition.ModeledCommitted &&
                adapter.LastResult.State == _currentState)
                return adapter.LastResult;

            var acknowledgments = new List<PauseOutputAck>(adapter.Gates.Count);
            if (!_currentState.IsTerminal && adapter.DispatchStarted && adapter.TerminalIntent == PauseTerminalIntent.None)
            {
                if (adapter.Gates.Any(gate => !gate.IsDrained))
                    return null;
                foreach (var gate in adapter.Gates)
                {
                    if (!gate.TryGetAcknowledgment(out var acknowledgment))
                    {
                        acknowledgments.Clear();
                        break;
                    }
                    acknowledgments.Add(acknowledgment!);
                }
                // Previously accepted retained media cannot extend into a newly excluded
                // input range. Never accept a retroactive pause over known media support.
                if (adapter.Command.Action == PauseCommandAction.Pause && acknowledgments.Any(ack =>
                    ack.Streams.Any(effect => _media.Any(pair => pair.Key.Kind == ack.Kind &&
                        pair.Key.OutputEpoch == ack.OutputEpoch && pair.Value.Receipt is not null &&
                        pair.Value.Receipt.Streams.Where(s => s.Id == effect.Id).SelectMany(s => s.Packets)
                            .SelectMany(p => p.CaptureSupport).Any(span => span.EndIndexExclusive > effect.InputBoundaryIndex)))))
                    acknowledgments.Clear();
            }
            var result = PauseAcknowledgmentPolicy.Evaluate(adapter.Command, _currentState,
                adapter.DispatchStarted, acknowledgments, adapter.TerminalIntent);
            if (result.Disposition == PauseModelDisposition.ModeledCommitted)
                adapter.CommittedEffects ??= Array.AsReadOnly(acknowledgments.ToArray());
            if (result.State.IsTerminal)
            {
                // A different operation may already have possible effects while its callbacks
                // are still entered. Reconcile that known custody before exposing cleanup.
                foreach (var dispatched in _operations.Where(operation => operation.DispatchStarted))
                    result = PauseAcknowledgmentPolicy.Evaluate(dispatched.Command, result.State, true, []);
            }
            if (result.State != _currentState)
                _cleanup = null; // A changed terminal obligation requires fresh receipts for every output.
            _currentState = result.State;
            if (_currentState.IsTerminal)
            {
                if (_terminalResult?.State != _currentState)
                    _terminalResult = result;
                result = _terminalResult;
            }
            adapter.LastResult = result;
            return result;
        }
    }

    internal bool AcceptRelease(PauseParticipantCleanupAdapter adapter, PauseParticipantCleanupTicket ticket,
        CaptureSessionContext context, PauseOutputRelease release)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(release);
        lock (_sync)
        {
            var command = _currentState.TerminalCommand;
            if (adapter != _cleanup || adapter.Terminal.State != _currentState || adapter.HasConflictingRelease ||
                !adapter.Tickets.Contains(ticket) || !ticket.IsDrained || !CoverageAccounted(ticket) ||
                !PauseCommandPolicy.SameContext(_context, context) ||
                command is null || release.OperationId != command.OperationId || release.Sequence != command.Sequence ||
                release.Identity != command.Identity || release.Kind != ticket.Kind || release.OutputEpoch != ticket.OutputEpoch ||
                !release.ResourcesReleased || !release.FinalizationAccounted ||
                (!release.FinalizationSucceeded && !release.OriginalPreserved) ||
                (CoverageFailed(ticket) && (release.FinalizationSucceeded || !release.OriginalPreserved)))
                return false;
            if (adapter.Releases.TryGetValue(ticket, out var previous))
            {
                if (previous == release)
                    return true;
                adapter.HasConflictingRelease = true;
                return false;
            }
            adapter.Releases.Add(ticket, release);
            return true;
        }
    }

    internal PauseCleanupResult EvaluateCleanup(PauseParticipantCleanupAdapter adapter)
    {
        lock (_sync)
        {
            if (adapter != _cleanup || adapter.Terminal.State != _currentState || adapter.HasConflictingRelease ||
                adapter.Tickets.Any(ticket => !ticket.IsDrained || !CoverageAccounted(ticket) ||
                    (CoverageFailed(ticket) && adapter.Releases.TryGetValue(ticket, out var release) &&
                        (release.FinalizationSucceeded || !release.OriginalPreserved))))
                return new(false, false);
            return PauseCleanupPolicy.Evaluate(adapter.Terminal, adapter.Releases.Values.ToArray());
        }
    }
    // Explicit model capacities, not native codec promises. Slots are never evicted.
    public const int MaxMediaPacketsPerStream = 8;
    public const int MaxCaptureSpansPerPacket = 8;

    internal int PendingMedia(PauseParticipantEvidenceAdapter adapter)
    {
        lock (_sync)
            return adapter.Registrations.Count(r => !Accounted(_media[r]));
    }

    internal bool FailedMedia(PauseParticipantEvidenceAdapter adapter)
    {
        lock (_sync)
            return adapter.Registrations.Any(r => _media[r].Failed);
    }

    internal bool AcceptMedia(PauseParticipantEvidenceAdapter adapter, PauseParticipantRegistration registration,
        CaptureSessionContext context, PauseOutputMediaCoverage receipt)
    {
        MediaSlot slot;
        lock (_sync)
        {
            if (!OwnsMedia(adapter, registration, context) || !adapter.DispatchStarted)
                return false;
            slot = _media[registration];
            if (slot.Failed)
                return false; // Late coverage cannot erase or reopen accounted failure.
            if (slot.Copying || adapter.CommittedEffects is null)
            {
                FailMedia(adapter, slot, accounted: false);
                return false;
            }
            slot.Copying = true;
        }

        // Caller indexers can throw, mutate, or block. Never read them under the owner lock.
        PauseOutputMediaCoverage? frozen = null;
        try { frozen = FreezeMedia(registration, receipt); }
        catch { /* An incomplete copy is unknown evidence, not a release receipt. */ }
        lock (_sync)
        {
            slot.Copying = false;
            if (slot.Failed)
                return false;
            if (frozen is null || !ValidMedia(adapter, registration, frozen) ||
                (slot.Receipt is not null && !SameCoverage(slot.Receipt, frozen)))
            {
                FailMedia(adapter, slot, accounted: false);
                return false;
            }
            slot.Receipt ??= frozen;
            return true;
        }
    }

    internal bool AccountMediaFailure(PauseParticipantEvidenceAdapter adapter,
        PauseParticipantRegistration registration, CaptureSessionContext context)
    {
        lock (_sync)
        {
            if (!OwnsMedia(adapter, registration, context) || !adapter.DispatchStarted)
                return false;
            if (_media[registration] is { Failed: true, FailureAccounted: true })
                return true;
            FailMedia(adapter, _media[registration], accounted: true);
            return true;
        }
    }

    private bool OwnsMedia(PauseParticipantEvidenceAdapter adapter, PauseParticipantRegistration registration,
        CaptureSessionContext context) => registration is not null && context is not null &&
        _operations.Contains(adapter) && adapter.Registrations.Contains(registration) &&
        PauseCommandPolicy.SameContext(_context, context) && _media.ContainsKey(registration);

    private void FailMedia(PauseParticipantEvidenceAdapter adapter, MediaSlot slot, bool accounted)
    {
        slot.Failed = true;
        slot.FailureAccounted = accounted;
        // Successful old-operation coverage never replays effects. A failure may only
        // force terminal reconciliation of every known possibly dispatched epoch.
        Reduce(adapter, true, PauseTerminalIntent.MediaEvidenceLost);
        _cleanup = null;
    }

    private IEnumerable<MediaSlot> RequiredMedia(PauseParticipantCleanupTicket ticket) =>
        _operations.Where(o => o.DispatchStarted).SelectMany(o => o.Registrations)
            .Where(r => r.Kind == ticket.Kind && r.OutputEpoch == ticket.OutputEpoch).Select(r => _media[r]);

    private static bool Accounted(MediaSlot slot) => !slot.Copying &&
        (slot.Failed ? slot.FailureAccounted : slot.Receipt is not null);
    private bool CoverageAccounted(PauseParticipantCleanupTicket ticket) => RequiredMedia(ticket).All(Accounted);
    private bool CoverageFailed(PauseParticipantCleanupTicket ticket) => RequiredMedia(ticket).Any(s => s.Failed);

    private static PauseOutputMediaCoverage? FreezeMedia(PauseParticipantRegistration registration,
        PauseOutputMediaCoverage receipt)
    {
        var command = registration.Command;
        if (receipt is null || receipt.OperationId != command.OperationId || receipt.Sequence != command.Sequence ||
            receipt.Identity != command.Identity || receipt.Action != command.Action || receipt.Kind != registration.Kind ||
            receipt.OutputEpoch != registration.OutputEpoch || receipt.RegistrationGeneration != registration.RegistrationGeneration ||
            receipt.Streams is null || receipt.Streams.Count != registration.Streams.Count)
            return null;
        var streams = new PauseStreamMediaCoverage[registration.Streams.Count];
        for (var i = 0; i < streams.Length; i++)
        {
            var stream = receipt.Streams[i];
            if (stream?.Id is null || stream.Packets is null ||
                !registration.Streams.Any(s => s.Id == stream.Id) || streams.Take(i).Any(s => s.Id == stream.Id))
                return null;
            var packetCount = stream.Packets.Count;
            if (packetCount is < 1 or > MaxMediaPacketsPerStream)
                return null;
            var packets = new PauseMediaPacketWitness[packetCount];
            for (var p = 0; p < packets.Length; p++)
            {
                var packet = stream.Packets[p];
                if (packet is null || packet.CaptureSupport is null)
                    return null;
                var spanCount = packet.CaptureSupport.Count;
                if (spanCount is < 1 or > MaxCaptureSpansPerPacket)
                    return null;
                var spans = new PauseInputSpan[spanCount];
                for (var j = 0; j < spans.Length; j++)
                {
                    var span = packet.CaptureSupport[j];
                    if (span is null)
                        return null;
                    spans[j] = new(span.StartIndex, span.EndIndexExclusive);
                }
                packets[p] = packet with { CaptureSupport = Array.AsReadOnly(spans) };
            }
            streams[i] = new(new(stream.Id.Kind, stream.Id.Index), Array.AsReadOnly(packets));
        }
        return receipt with { Streams = Array.AsReadOnly(streams) };
    }

    private bool ValidMedia(PauseParticipantEvidenceAdapter adapter, PauseParticipantRegistration registration,
        PauseOutputMediaCoverage receipt)
    {
        var history = _operations.Where(o => o.DispatchStarted && o.Registrations.Any(r =>
            r.Kind == registration.Kind && r.OutputEpoch == registration.OutputEpoch)).OrderBy(o => o.Command.Sequence).ToArray();
        // Unknown effects cannot justify which captured input reached media.
        if (history.Any(o => o.CommittedEffects is null))
            return false;
        var effect = adapter.CommittedEffects!.Single(a => a.Kind == registration.Kind && a.OutputEpoch == registration.OutputEpoch);
        foreach (var stream in receipt.Streams)
        {
            var anchor = registration.Streams.Single(s => s.Id == stream.Id);
            var cut = effect.Streams.Single(s => s.Id == stream.Id).InputBoundaryIndex;
            var adjacent = false;
            var identities = new HashSet<long>();
            var allSupport = stream.Packets.SelectMany(p => p.CaptureSupport).OrderBy(s => s.StartIndex).ToArray();
            for (var i = 1; i < allSupport.Length; i++)
                if (allSupport[i - 1].EndIndexExclusive > allSupport[i].StartIndex)
                    return false;
            foreach (var packet in stream.Packets)
            {
                if (!identities.Add(packet.PacketIdentity) || !ValidPacket(anchor.MediaTiming, packet))
                    return false;
                foreach (var span in packet.CaptureSupport)
                {
                    adjacent |= adapter.Command.Action == PauseCommandAction.Pause
                        ? span.EndIndexExclusive == cut : span.StartIndex == cut;
                    long? pauseStart = null;
                    foreach (var operation in history)
                    {
                        var ack = operation.CommittedEffects!.Single(a => a.Kind == registration.Kind && a.OutputEpoch == registration.OutputEpoch);
                        var index = ack.Streams.Single(s => s.Id == stream.Id).InputBoundaryIndex;
                        if (operation.Command.Action == PauseCommandAction.Pause)
                            pauseStart = index;
                        else if (pauseStart is { } start)
                        {
                            if (span.StartIndex < index && span.EndIndexExclusive > start)
                                return false;
                            pauseStart = null;
                        }
                    }
                    if (pauseStart is { } openStart && span.EndIndexExclusive > openStart)
                        return false;
                }
                // A packet shared by boundary receipts must remain the same immutable fact.
                foreach (var old in _media.Where(pair => pair.Key.Kind == registration.Kind &&
                    pair.Key.OutputEpoch == registration.OutputEpoch && pair.Value.Receipt is not null))
                    foreach (var previous in old.Value.Receipt!.Streams.Where(s => s.Id == stream.Id).SelectMany(s => s.Packets))
                    {
                        if (previous.PacketIdentity == packet.PacketIdentity)
                        {
                            if (!SamePacket(previous, packet))
                                return false;
                        }
                        else if (previous.CaptureSupport.Any(a => packet.CaptureSupport.Any(b =>
                            a.StartIndex < b.EndIndexExclusive && b.StartIndex < a.EndIndexExclusive)))
                            return false;
                    }
            }
            if (!adjacent)
                return false;
        }
        return true;
    }

    private static bool ValidPacket(PauseMediaTimingDeclaration timing, PauseMediaPacketWitness packet)
    {
        if (packet.MediaTimingId != timing.TimingId || packet.PacketIdentity <= 0 ||
            packet.Duration <= 0 || packet.TimebaseNumerator <= 0 ||
            packet.TimebaseDenominator <= 0 || packet.DecodedInputUnits <= 0 || packet.LeadingTrimUnits < 0 ||
            packet.TrailingTrimUnits < 0 || (BigInteger)packet.Pts + packet.Duration > long.MaxValue)
            return false;
        BigInteger supported = 0;
        long? previousEnd = null;
        foreach (var span in packet.CaptureSupport)
        {
            if (span.EndIndexExclusive <= span.StartIndex || previousEnd > span.StartIndex)
                return false;
            supported += (BigInteger)span.EndIndexExclusive - span.StartIndex;
            previousEnd = span.EndIndexExclusive;
        }
        return supported == (BigInteger)packet.DecodedInputUnits - packet.LeadingTrimUnits - packet.TrailingTrimUnits &&
            (BigInteger)packet.Duration * packet.TimebaseNumerator * timing.SecondsPerDecodedUnitDenominator ==
            (BigInteger)packet.DecodedInputUnits * timing.SecondsPerDecodedUnitNumerator * packet.TimebaseDenominator;
    }

    private static bool SamePacket(PauseMediaPacketWitness a, PauseMediaPacketWitness b) =>
        a.PacketIdentity == b.PacketIdentity && a.MediaTimingId == b.MediaTimingId &&
        a.Pts == b.Pts && a.Duration == b.Duration &&
        a.TimebaseNumerator == b.TimebaseNumerator && a.TimebaseDenominator == b.TimebaseDenominator &&
        a.DecodedInputUnits == b.DecodedInputUnits && a.LeadingTrimUnits == b.LeadingTrimUnits &&
        a.TrailingTrimUnits == b.TrailingTrimUnits && a.CaptureSupport.SequenceEqual(b.CaptureSupport);

    private static bool SameCoverage(PauseOutputMediaCoverage a, PauseOutputMediaCoverage b) =>
        a.Streams.Count == b.Streams.Count && a.Streams.All(s => b.Streams.Any(t => s.Id == t.Id &&
            s.Packets.Count == t.Packets.Count && s.Packets.All(p => t.Packets.Any(q => SamePacket(p, q)))));

    private sealed class MediaSlot
    {
        public bool Copying { get; set; }
        public bool Failed { get; set; }
        public bool FailureAccounted { get; set; }
        public PauseOutputMediaCoverage? Receipt { get; set; }
    }
}

public sealed class PauseParticipantEvidenceAdapter
{
    private readonly PauseParticipantOwner _owner;
    internal PauseParticipantEvidenceAdapter(PauseParticipantOwner owner, PauseCommand command,
        PauseParticipantCallbackGate[] gates)
    {
        _owner = owner;
        Command = command;
        Gates = Array.AsReadOnly(gates);
        Registrations = Array.AsReadOnly(gates.Select(gate => gate.Registration).ToArray());
    }

    public PauseCommand Command { get; }
    public IReadOnlyList<PauseParticipantRegistration> Registrations { get; }
    public IReadOnlyList<PauseParticipantCallbackGate> Gates { get; }
    internal bool DispatchStarted { get; set; }
    internal PauseTerminalIntent TerminalIntent { get; set; }
    internal PauseAcknowledgmentResult? LastResult { get; set; }
    internal IReadOnlyList<PauseOutputAck>? CommittedEffects { get; set; }
    public int PendingMediaCoverageCount => _owner.PendingMedia(this);
    public bool HasMediaCoverageFailure => _owner.FailedMedia(this);
    public bool TryAcceptMediaCoverage(PauseParticipantRegistration registration, CaptureSessionContext context,
        PauseOutputMediaCoverage receipt) => _owner.AcceptMedia(this, registration, context, receipt);
    // Caller supplies the completion point; no timers or native calls are introduced.
    public bool AccountMediaCoverageFailure(PauseParticipantRegistration registration, CaptureSessionContext context) =>
        _owner.AccountMediaFailure(this, registration, context);
    public Task CloseAsync(CancellationToken cancellationToken = default) =>
        Task.WhenAll(Gates.Select(gate => gate.CloseAsync(cancellationToken)));
    public PauseAcknowledgmentResult? Reduce(bool dispatchStarted, PauseTerminalIntent terminalIntent = PauseTerminalIntent.None) =>
        _owner.Reduce(this, dispatchStarted, terminalIntent);
    public override string ToString() => nameof(PauseParticipantEvidenceAdapter);
}

public sealed class PauseParticipantCleanupTicket
{
    internal PauseParticipantCleanupTicket(PauseOutputParticipant participant,
        IReadOnlyList<PauseParticipantCallbackGate> gates)
    {
        Participant = participant;
        Gates = gates;
    }

    public PauseOutputKind Kind => Participant.Kind;
    public Guid OutputEpoch => Participant.OutputEpoch;
    internal PauseOutputParticipant Participant { get; }
    internal IReadOnlyList<PauseParticipantCallbackGate> Gates { get; }
    public bool IsDrained => Gates.Count > 0 && Gates.All(gate => gate.IsDrained);
    public override string ToString() => nameof(PauseParticipantCleanupTicket);
}

public sealed class PauseParticipantCleanupAdapter
{
    private readonly PauseParticipantOwner _owner;
    internal PauseParticipantCleanupAdapter(PauseParticipantOwner owner, PauseAcknowledgmentResult terminal,
        PauseParticipantCleanupTicket[] tickets)
    {
        _owner = owner;
        Terminal = terminal;
        Tickets = Array.AsReadOnly(tickets);
    }

    internal PauseAcknowledgmentResult Terminal { get; }
    internal Dictionary<PauseParticipantCleanupTicket, PauseOutputRelease> Releases { get; } = [];
    internal bool HasConflictingRelease { get; set; }
    public IReadOnlyList<PauseParticipantCleanupTicket> Tickets { get; }
    public Task CloseAsync(CancellationToken cancellationToken = default) =>
        Task.WhenAll(Tickets.SelectMany(ticket => ticket.Gates).Distinct().Select(gate => gate.CloseAsync(cancellationToken)));
    public bool TryAccept(PauseParticipantCleanupTicket ticket, CaptureSessionContext context, PauseOutputRelease release) =>
        _owner.AcceptRelease(this, ticket, context, release);
    public PauseCleanupResult Evaluate() => _owner.EvaluateCleanup(this);
    public override string ToString() => nameof(PauseParticipantCleanupAdapter);
}
