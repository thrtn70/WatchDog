using System.Collections;
using System.Numerics;
using WatchDog.Core.Audio;
using WatchDog.Core.Capture;
using WatchDog.Core.Devices;
using WatchDog.Core.Settings;
using WatchDog.Core.Tests.Helpers;
using WatchDog.Core.Tutorial;
using WatchDog.Core.Webcam;

namespace WatchDog.Core.Tests.Tutorial;

public sealed class PauseParticipantContractTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task OwnedCopiedAcksDriveExistingPauseAndResumeReducers(bool camera, bool separate)
    {
        var fixture = Create(camera, separate);
        var owner = new PauseParticipantOwner(fixture.Context, fixture.State);
        var pause = owner.CreateOperation(Command(fixture, owner.CurrentState, PauseCommandAction.Pause))!;
        Assert.NotNull(pause);
        CopyAll(pause, 1);
        Assert.Null(pause.Reduce(true)); // No commitment before callback admission is closed and drained.
        await pause.CloseAsync();
        var paused = pause.Reduce(true)!;
        Assert.Equal(PauseModelDisposition.ModeledCommitted, paused.Disposition);
        Assert.Same(paused.State, owner.CurrentState);
        Assert.Same(paused, pause.Reduce(false));
        Assert.False(paused.NativePauseProven);
        Assert.False(paused.NativeAlignmentProven);
        Assert.False(paused.NativeLivenessAdmitted);

        var resume = owner.CreateOperation(Command(fixture, owner.CurrentState, PauseCommandAction.Resume))!;
        CopyAll(resume, 2);
        await resume.CloseAsync();
        var running = resume.Reduce(true)!;
        Assert.Equal(PauseModelPhase.Running, running.State.Phase);
        Assert.Same(fixture.Context, running.State.Context);
        Assert.Equal(PauseLivenessRequirement.RearmProgressAfterAggregateCommit, running.LivenessRequirement);
        Assert.Equal(nameof(PauseParticipantEvidenceAdapter), resume.ToString());
    }

    [Fact]
    public void OwnerRejectsForeignContextAdvancedStateAndInvalidCapacity()
    {
        var fixture = Create();
        Assert.Throws<ArgumentException>(() => new PauseParticipantOwner(
            fixture.Context with { Generation = 99 }, fixture.State));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PauseParticipantOwner(fixture.Context, fixture.State, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PauseParticipantOwner(fixture.Context, fixture.State, 1025));
        var command = Command(fixture, fixture.State);
        var paused = PauseAcknowledgmentPolicy.Evaluate(command, fixture.State, true,
            command.Outputs.Select(output => Ack(command, output, 1)).ToArray());
        Assert.Throws<ArgumentException>(() => new PauseParticipantOwner(fixture.Context, paused.State));
    }

    [Fact]
    public void CapacityReservationIsAtomicAndDuplicateOrForeignCommandsCannotMintRegistrations()
    {
        var fixture = Create(camera: true);
        var command = Command(fixture, fixture.State);
        Assert.Null(new PauseParticipantOwner(fixture.Context, fixture.State, 1).CreateOperation(command));
        var owner = new PauseParticipantOwner(fixture.Context, fixture.State, 2);
        var operation = owner.CreateOperation(command)!;
        Assert.NotNull(operation);
        Assert.Equal(new long[] { 1, 2 }, operation.Registrations.Select(r => r.RegistrationGeneration));
        Assert.Null(owner.CreateOperation(command));
        Assert.Null(owner.CreateOperation(Command(fixture, fixture.State)));
        var other = Create();
        Assert.Null(owner.CreateOperation(Command(other, other.State)));
        Assert.Same(fixture.State, owner.CurrentState);
        Assert.Throws<NotSupportedException>(() => ((IList<PauseParticipantRegistration>)operation.Registrations).Clear());
    }

    [Fact]
    public async Task DispatchAndTerminalIntentStayLatchedWhileEnteredCallbacksDrain()
    {
        var fixture = Create();
        var owner = new PauseParticipantOwner(fixture.Context, fixture.State);
        var operation = owner.CreateOperation(Command(fixture, fixture.State))!;
        var gate = operation.Gates[0];
        Assert.True(gate.TryEnter(gate.Registration, out var lease));
        try
        {
            Assert.Null(operation.Reduce(true));
            var terminal = operation.Reduce(false, PauseTerminalIntent.SourceLost)!;
            Assert.Equal(PauseModelDisposition.StopAndFinalizeAllOwnedOutputs, terminal.Disposition);
            Assert.True(owner.CurrentState.IsTerminal);
            Assert.Equal(1, operation.PendingMediaCoverageCount);
            terminal = AccountMediaFailures(operation, fixture.Context);
            var cleanup = owner.CreateCleanup(terminal)!;
            Assert.False(cleanup.Tickets[0].IsDrained);
            Assert.False(cleanup.TryAccept(cleanup.Tickets[0], fixture.Context,
                Release(terminal, cleanup.Tickets[0], mediaFailed: true)));
            Assert.True(lease!.TryCopy(Ack(operation.Command, gate.Registration.Participant, 1)));
            Assert.Same(terminal.State, operation.Reduce(false)!.State);
            Assert.False(cleanup.Evaluate().CleanupCompleted);
        }
        finally { lease!.Dispose(); }
        await operation.CloseAsync();
        Assert.Equal(PauseModelDisposition.StopAndFinalizeAllOwnedOutputs, operation.Reduce(false)!.Disposition);
    }

    [Fact]
    public async Task PredispatchIntentPreventsLaterAcknowledgmentFromRevivingTheOperation()
    {
        var fixture = Create();
        var owner = new PauseParticipantOwner(fixture.Context, fixture.State);
        var operation = owner.CreateOperation(Command(fixture, fixture.State))!;
        var rejected = operation.Reduce(false, PauseTerminalIntent.Stop)!;
        Assert.Equal(PauseModelDisposition.RejectedNoEffect, rejected.Disposition);
        CopyAll(operation, 1);
        await operation.CloseAsync();
        Assert.Equal(PauseModelDisposition.StopAndFinalizeAllOwnedOutputs, operation.Reduce(true)!.Disposition);
    }

    [Fact]
    public async Task NewTerminalIntentOverridesPreviouslyCommittedResult()
    {
        var fixture = Create();
        var owner = new PauseParticipantOwner(fixture.Context, fixture.State);
        var operation = owner.CreateOperation(Command(fixture, fixture.State))!;
        CopyAll(operation, 1);
        await operation.CloseAsync();
        Assert.Equal(PauseModelDisposition.ModeledCommitted, operation.Reduce(true)!.Disposition);
        var terminal = operation.Reduce(false, PauseTerminalIntent.Stop)!;
        Assert.Equal(PauseModelDisposition.StopAndFinalizeAllOwnedOutputs, terminal.Disposition);
        Assert.Same(terminal, operation.Reduce(false));
        Assert.Null(owner.CreateOperation(Command(fixture, fixture.State)));
    }

    [Fact]
    public async Task ConflictingAcceptedReleaseCannotLeaveCleanupSuccessful()
    {
        var fixture = Create();
        var owner = new PauseParticipantOwner(fixture.Context, fixture.State);
        var operation = owner.CreateOperation(Command(fixture, fixture.State))!;
        CopyAll(operation, 1);
        await operation.CloseAsync();
        Assert.Equal(PauseModelDisposition.ModeledCommitted, operation.Reduce(true)!.Disposition);
        var registration = operation.Registrations[0];
        Assert.True(operation.TryAcceptMediaCoverage(registration, fixture.Context,
            Coverage(registration, 1, -50, [new(0, 10)])));
        var terminal = operation.Reduce(false, PauseTerminalIntent.Stop)!;
        var cleanup = owner.CreateCleanup(terminal)!;
        await cleanup.CloseAsync();
        var ticket = cleanup.Tickets[0];
        var release = Release(terminal, ticket);
        Assert.True(cleanup.TryAccept(ticket, fixture.Context, release));
        Assert.True(cleanup.Evaluate().CleanupCompleted);
        Assert.False(cleanup.TryAccept(ticket, fixture.Context, release with { FinalizationSucceeded = false }));
        Assert.False(cleanup.Evaluate().CleanupCompleted);
        Assert.False(cleanup.TryAccept(ticket, fixture.Context, release));
    }

    [Fact]
    public async Task DrainAloneCannotCompleteCleanupAndOriginalPreservationOnlyYieldsWarning()
    {
        var fixture = Create(camera: true);
        var owner = new PauseParticipantOwner(fixture.Context, fixture.State);
        var operation = owner.CreateOperation(Command(fixture, fixture.State))!;
        var terminal = operation.Reduce(true, PauseTerminalIntent.Cancel)!;
        Assert.Equal(operation.Registrations.Count, operation.PendingMediaCoverageCount);
        var pendingCleanup = owner.CreateCleanup(terminal)!;
        await pendingCleanup.CloseAsync();
        Assert.False(pendingCleanup.TryAccept(pendingCleanup.Tickets[0], fixture.Context,
            Release(terminal, pendingCleanup.Tickets[0])));
        Assert.False(pendingCleanup.Evaluate().CleanupCompleted);
        terminal = AccountMediaFailures(operation, fixture.Context);
        Assert.False(pendingCleanup.Evaluate().CleanupCompleted);
        var cleanup = owner.CreateCleanup(terminal)!;
        await cleanup.CloseAsync();
        Assert.All(cleanup.Tickets, ticket => Assert.True(ticket.IsDrained));
        Assert.False(cleanup.Evaluate().CleanupCompleted);
        var screen = cleanup.Tickets[0];
        Assert.False(cleanup.TryAccept(screen, fixture.Context,
            Release(terminal, screen, mediaFailed: true) with { FinalizationAccounted = false }));
        Assert.True(cleanup.TryAccept(screen, fixture.Context, Release(terminal, screen, mediaFailed: true)));
        Assert.False(cleanup.Evaluate().CleanupCompleted);
        var camera = cleanup.Tickets[1];
        Assert.False(cleanup.TryAccept(camera, fixture.Context, Release(terminal, camera, mediaFailed: true) with
        {
            FinalizationSucceeded = false,
            OriginalPreserved = false,
        }));
        Assert.True(cleanup.TryAccept(camera, fixture.Context, Release(terminal, camera, mediaFailed: true)));
        var completed = cleanup.Evaluate();
        Assert.True(completed.CleanupCompleted);
        Assert.True(completed.FinalizationWarning);
        Assert.True(owner.CurrentState.IsTerminal);
        foreach (var registration in operation.Registrations)
        {
            Assert.True(operation.AccountMediaCoverageFailure(registration, fixture.Context));
            Assert.False(operation.TryAcceptMediaCoverage(registration, fixture.Context,
                Coverage(registration, 100, -100, [new(0, 10)])));
        }
        Assert.Same(cleanup, owner.CreateCleanup(operation.Reduce(false)!));
        Assert.True(cleanup.Evaluate().CleanupCompleted);
    }

    [Theory]
    [InlineData("request")]
    [InlineData("binding")]
    [InlineData("operation")]
    [InlineData("sequence")]
    [InlineData("identity")]
    [InlineData("kind")]
    [InlineData("epoch")]
    [InlineData("resources")]
    public async Task CleanupRejectsWrongBindingWithoutReplacingExactEvidence(string change)
    {
        var fixture = Create();
        var owner = new PauseParticipantOwner(fixture.Context, fixture.State);
        var operation = owner.CreateOperation(Command(fixture, fixture.State))!;
        var terminal = operation.Reduce(true, PauseTerminalIntent.Shutdown)!;
        terminal = AccountMediaFailures(operation, fixture.Context);
        var cleanup = owner.CreateCleanup(terminal)!;
        await cleanup.CloseAsync();
        var ticket = cleanup.Tickets[0];
        var context = fixture.Context;
        var request = context.Request;
        if (change is "request" or "binding")
            context = context with
            {
                Request = new(change == "request" ? Guid.NewGuid() : request.RequestId,
                request.RequestedAt, request.Initiation, request.Target, request.Webcam, request.StartPolicy)
            };
        var valid = Release(terminal, ticket, mediaFailed: true);
        var invalid = change switch
        {
            "operation" => valid with { OperationId = Guid.NewGuid() },
            "sequence" => valid with { Sequence = valid.Sequence + 1 },
            "identity" => valid with { Identity = valid.Identity with { Generation = 99 } },
            "kind" => valid with { Kind = PauseOutputKind.Camera },
            "epoch" => valid with { OutputEpoch = Guid.NewGuid() },
            "resources" => valid with { ResourcesReleased = false },
            _ => valid,
        };
        Assert.False(cleanup.TryAccept(ticket, context, invalid));
        Assert.False(cleanup.Evaluate().CleanupCompleted);
        Assert.True(cleanup.TryAccept(ticket, fixture.Context, valid));
        Assert.True(cleanup.TryAccept(ticket, fixture.Context, valid));
        Assert.True(cleanup.Evaluate().CleanupCompleted);
        Assert.Same(cleanup, owner.CreateCleanup(operation.Reduce(false)!));
        Assert.True(cleanup.Evaluate().CleanupCompleted);
    }

    [Fact]
    public async Task CleanupReferencesAllIssuedGatesForAnEpochAndCannotInventDrain()
    {
        var fixture = Create();
        var owner = new PauseParticipantOwner(fixture.Context, fixture.State);
        var first = owner.CreateOperation(Command(fixture, fixture.State))!;
        var second = owner.CreateOperation(Command(fixture, fixture.State))!;
        var secondGate = second.Gates[0];
        Assert.True(secondGate.TryEnter(secondGate.Registration, out var lease));
        var terminal = first.Reduce(true, PauseTerminalIntent.Stop)!;
        terminal = AccountMediaFailures(first, fixture.Context);
        var cleanup = owner.CreateCleanup(terminal)!;
        var drain = cleanup.CloseAsync();
        try
        {
            Assert.False(drain.IsCompleted);
            Assert.False(cleanup.TryAccept(cleanup.Tickets[0], fixture.Context,
                Release(terminal, cleanup.Tickets[0], mediaFailed: true)));
            Assert.True(secondGate.IsClosed);
        }
        finally { lease!.Dispose(); }
        await drain.WaitAsync(TestTimeouts.Signal);
        Assert.True(cleanup.TryAccept(cleanup.Tickets[0], fixture.Context,
            Release(terminal, cleanup.Tickets[0], mediaFailed: true)));
        Assert.True(cleanup.Evaluate().CleanupCompleted);
        var forged = PauseAcknowledgmentPolicy.Evaluate(first.Command, fixture.State, true, []);
        Assert.Null(owner.CreateCleanup(forged));
    }

    [Fact]
    public async Task TerminalCleanupIncludesEveryAlreadyDispatchedEpochBeforeItCanComplete()
    {
        var fixture = Create();
        var owner = new PauseParticipantOwner(fixture.Context, fixture.State);
        var first = owner.CreateOperation(Command(fixture, fixture.State))!;
        var rotated = fixture with { Capabilities = [fixture.Capabilities[0] with { OutputEpoch = Guid.NewGuid() }] };
        var other = owner.CreateOperation(Command(rotated, fixture.State))!;
        var otherGate = other.Gates[0];
        Assert.True(otherGate.TryEnter(otherGate.Registration, out var lease));
        Assert.Null(other.Reduce(true));
        Task? drain = null;
        PauseParticipantCleanupAdapter? cleanup = null;
        PauseAcknowledgmentResult? terminal = null;
        try
        {
            terminal = first.Reduce(true, PauseTerminalIntent.Stop)!;
            Assert.Equal(2, terminal.CleanupOutputs.Count);
            AccountMediaFailures(other, fixture.Context);
            terminal = AccountMediaFailures(first, fixture.Context);
            cleanup = owner.CreateCleanup(terminal)!;
            drain = cleanup.CloseAsync();
            Assert.False(drain.IsCompleted);
            Assert.True(otherGate.IsClosed);
            var firstTicket = cleanup.Tickets.Single(ticket => ticket.OutputEpoch == first.Registrations[0].OutputEpoch);
            Assert.True(cleanup.TryAccept(firstTicket, fixture.Context,
                Release(terminal, firstTicket, mediaFailed: true)));
            Assert.False(cleanup.Evaluate().CleanupCompleted);
        }
        finally { lease!.Dispose(); }
        await drain!.WaitAsync(TestTimeouts.Signal);
        var otherTicket = cleanup!.Tickets.Single(ticket => ticket.OutputEpoch == otherGate.Registration.OutputEpoch);
        Assert.True(cleanup.TryAccept(otherTicket, fixture.Context,
            Release(terminal!, otherTicket, mediaFailed: true)));
        Assert.True(cleanup.Evaluate().CleanupCompleted);
    }

    [Fact]
    public async Task LateIssuedEpochInvalidatesCleanupAndRequiresFreshEvidenceForEveryOutput()
    {
        var fixture = Create();
        var owner = new PauseParticipantOwner(fixture.Context, fixture.State);
        var first = owner.CreateOperation(Command(fixture, fixture.State))!;
        var rotated = fixture with { Capabilities = [fixture.Capabilities[0] with { OutputEpoch = Guid.NewGuid() }] };
        var late = owner.CreateOperation(Command(rotated, fixture.State))!;
        var terminal = first.Reduce(true, PauseTerminalIntent.Stop)!;
        terminal = AccountMediaFailures(first, fixture.Context);
        var oldCleanup = owner.CreateCleanup(terminal)!;
        await oldCleanup.CloseAsync();
        var oldTicket = oldCleanup.Tickets[0];
        Assert.True(oldCleanup.TryAccept(oldTicket, fixture.Context,
            Release(terminal, oldTicket, mediaFailed: true)));
        Assert.True(oldCleanup.Evaluate().CleanupCompleted);
        CopyAll(late, 1);
        var expanded = late.Reduce(true)!;
        Assert.Equal(PauseModelDisposition.StopAndFinalizeAllOwnedOutputs, expanded.Disposition);
        Assert.Equal(2, expanded.CleanupOutputs.Count);
        expanded = AccountMediaFailures(late, fixture.Context);
        Assert.False(oldCleanup.Evaluate().CleanupCompleted);
        Assert.Null(owner.CreateCleanup(terminal));
        Assert.False(oldCleanup.TryAccept(oldTicket, fixture.Context, Release(terminal, oldTicket)));
        var current = owner.CreateCleanup(expanded)!;
        await current.CloseAsync();
        Assert.False(current.TryAccept(oldTicket, fixture.Context, Release(terminal, oldTicket)));
        Assert.False(current.Evaluate().CleanupCompleted);
        foreach (var ticket in current.Tickets)
            Assert.True(current.TryAccept(ticket, fixture.Context,
                Release(expanded, ticket, mediaFailed: true)));
        Assert.True(current.Evaluate().CleanupCompleted);
        Assert.True(owner.CurrentState.IsTerminal);
        Assert.Same(current, owner.CreateCleanup(late.Reduce(false)!));
    }

    [Fact]
    public async Task IndependentSuppliedMediaPeriodsAndTrimCountsPreserveExactCaptureEffects()
    {
        var fixture = Create(true, true);
        var owner = new PauseParticipantOwner(fixture.Context, fixture.State);
        var operation = owner.CreateOperation(Command(fixture, fixture.State))!;
        CopyAll(operation, 1);
        await operation.CloseAsync();
        var committed = operation.Reduce(true)!;
        Assert.Equal(PauseModelDisposition.ModeledCommitted, committed.Disposition);
        Assert.Equal(TimeSpan.FromSeconds(1), committed.State.LastBoundary);
        Assert.False(committed.NativeAlignmentProven);
        foreach (var registration in operation.Registrations)
        {
            Assert.All(registration.Streams, anchor => Assert.NotEqual(
                anchor.InputClock.SecondsPerInputDenominator, anchor.MediaTiming.SecondsPerDecodedUnitDenominator));
            var receipt = Coverage(registration, 91, -100, [new(0, 10)]);
            receipt = receipt with
            {
                Streams = receipt.Streams.Select(stream => stream with
                {
                    Packets = [stream.Packets[0] with
                    {
                        DecodedInputUnits = 13, Duration = 13, LeadingTrimUnits = 1, TrailingTrimUnits = 2,
                    }],
                }).ToArray(),
            };
            Assert.True(operation.TryAcceptMediaCoverage(registration, fixture.Context, receipt));
            Assert.True(operation.TryAcceptMediaCoverage(registration, fixture.Context, receipt));
        }
        Assert.Equal(0, operation.PendingMediaCoverageCount);
        Assert.False(operation.HasMediaCoverageFailure);
        Assert.Equal(PauseModelPhase.Paused, owner.CurrentState.Phase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public async Task InvalidSuppliedTimingBindingOrTrimSupportLatchesOwnedMediaFailure(int defect)
    {
        var fixture = Create();
        var owner = new PauseParticipantOwner(fixture.Context, fixture.State);
        var operation = owner.CreateOperation(Command(fixture, fixture.State))!;
        CopyAll(operation, 1);
        await operation.CloseAsync();
        Assert.Equal(PauseModelDisposition.ModeledCommitted, operation.Reduce(true)!.Disposition);
        var registration = operation.Registrations[0];
        var original = Coverage(registration, 92, -100, [new(0, 10)]);
        var first = original.Streams[0];
        var trimmed = first.Packets[0] with
        {
            DecodedInputUnits = 13,
            Duration = 13,
            LeadingTrimUnits = 1,
            TrailingTrimUnits = 2,
        };
        var invalid = defect switch
        {
            0 => trimmed with { MediaTimingId = Guid.Empty },
            1 => trimmed with { MediaTimingId = Guid.NewGuid() },
            2 => trimmed with { MediaTimingId = registration.Streams[1].MediaTiming.TimingId },
            // Satisfies the former capture-period equation; fails independent media timing.
            3 => trimmed with { TimebaseNumerator = 1, TimebaseDenominator = 10 },
            4 => trimmed with { DecodedInputUnits = 14, Duration = 14 },
            5 => trimmed with { LeadingTrimUnits = -1 },
            6 => trimmed with { LeadingTrimUnits = 11 },
            7 => trimmed with { LeadingTrimUnits = 12 },
            8 => trimmed with { DecodedInputUnits = 0, Duration = 1 },
            9 => trimmed with { Duration = 10 },
            _ => trimmed with { TrailingTrimUnits = -1 },
        };
        var receipt = original with { Streams = [first with { Packets = [invalid] }, .. original.Streams.Skip(1)] };
        Assert.False(operation.TryAcceptMediaCoverage(registration, fixture.Context, receipt));
        Assert.True(operation.HasMediaCoverageFailure);
        Assert.True(owner.CurrentState.IsTerminal);
        Assert.Equal(1, operation.PendingMediaCoverageCount);
        Assert.False(operation.TryAcceptMediaCoverage(registration, fixture.Context, original));
        Assert.True(operation.AccountMediaCoverageFailure(registration, fixture.Context));
        Assert.Equal(0, operation.PendingMediaCoverageCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedSuppliedPacketRejectsChangedTimingIdentityOrEquivalentPacketTimebase(bool changeTimingId)
    {
        var fixture = Create();
        var owner = new PauseParticipantOwner(fixture.Context, fixture.State);
        var pause = owner.CreateOperation(Command(fixture, owner.CurrentState))!;
        CopyAll(pause, 1);
        await pause.CloseAsync();
        Assert.Equal(PauseModelDisposition.ModeledCommitted, pause.Reduce(true)!.Disposition);
        var resume = owner.CreateOperation(Command(fixture, owner.CurrentState, PauseCommandAction.Resume))!;
        CopyAll(resume, 2);
        await resume.CloseAsync();
        Assert.Equal(PauseModelDisposition.ModeledCommitted, resume.Reduce(true)!.Disposition);
        var spans = new PauseInputSpan[] { new(0, 10), new(20, 30) };
        Assert.True(pause.TryAcceptMediaCoverage(pause.Registrations[0], fixture.Context,
            Coverage(pause.Registrations[0], 93, -100, spans)));
        var receipt = Coverage(resume.Registrations[0], 93, -100, spans);
        var stream = receipt.Streams[0];
        var packet = stream.Packets[0];
        packet = changeTimingId ? packet with { MediaTimingId = Guid.NewGuid() } : packet with
        {
            Duration = packet.Duration * 2,
            TimebaseDenominator = packet.TimebaseDenominator * 2,
        };
        receipt = receipt with { Streams = [stream with { Packets = [packet] }, .. receipt.Streams.Skip(1)] };
        Assert.False(resume.TryAcceptMediaCoverage(resume.Registrations[0], fixture.Context, receipt));
        Assert.True(resume.HasMediaCoverageFailure);
        Assert.True(owner.CurrentState.IsTerminal);
    }

    [Fact]
    public async Task EffectsCommitWhileDeferredRawMediaCoverageArrivesAfterResume()
    {
        var fixture = Create();
        var owner = new PauseParticipantOwner(fixture.Context, fixture.State);
        var pause = owner.CreateOperation(Command(fixture, owner.CurrentState, PauseCommandAction.Pause))!;
        CopyAll(pause, 1);
        await pause.CloseAsync();
        Assert.Equal(PauseModelDisposition.ModeledCommitted, pause.Reduce(true)!.Disposition);
        Assert.Equal(1, pause.PendingMediaCoverageCount);

        var resume = owner.CreateOperation(Command(fixture, owner.CurrentState, PauseCommandAction.Resume))!;
        CopyAll(resume, 2);
        await resume.CloseAsync();
        Assert.Equal(PauseModelDisposition.ModeledCommitted, resume.Reduce(true)!.Disposition);
        Assert.Equal(PauseModelPhase.Running, owner.CurrentState.Phase);

        var crossingSupport = new PauseInputSpan[] { new(0, 10), new(20, 30) };
        var pauseRegistration = pause.Registrations[0];
        Assert.True(pause.TryAcceptMediaCoverage(pauseRegistration, fixture.Context,
            Coverage(pauseRegistration, 2, -8_000, crossingSupport)));
        Assert.Equal(0, pause.PendingMediaCoverageCount);

        var resumeRegistration = resume.Registrations[0];
        Assert.True(resume.TryAcceptMediaCoverage(resumeRegistration, fixture.Context,
            Coverage(resumeRegistration, 2, -8_000, crossingSupport)));
        Assert.Equal(0, resume.PendingMediaCoverageCount);
        Assert.False(pause.HasMediaCoverageFailure);
        Assert.False(resume.HasMediaCoverageFailure);
        Assert.Equal(PauseModelPhase.Running, owner.CurrentState.Phase);
    }

    [Fact]
    public async Task SharedPacketFactsCanRepeatButConflictingFactsFailClosed()
    {
        var fixture = Create();
        var owner = new PauseParticipantOwner(fixture.Context, fixture.State);
        var pause = owner.CreateOperation(Command(fixture, owner.CurrentState, PauseCommandAction.Pause))!;
        CopyAll(pause, 1);
        await pause.CloseAsync();
        Assert.Equal(PauseModelDisposition.ModeledCommitted, pause.Reduce(true)!.Disposition);
        var resume = owner.CreateOperation(Command(fixture, owner.CurrentState, PauseCommandAction.Resume))!;
        CopyAll(resume, 2);
        await resume.CloseAsync();
        Assert.Equal(PauseModelDisposition.ModeledCommitted, resume.Reduce(true)!.Disposition);

        var sharedSpans = new PauseInputSpan[] { new(0, 10), new(20, 30) };
        var pauseCoverage = Coverage(pause.Registrations[0], 77, -700, sharedSpans);
        var resumeCoverage = Coverage(resume.Registrations[0], 77, -700, sharedSpans);
        Assert.True(pause.TryAcceptMediaCoverage(pause.Registrations[0], fixture.Context, pauseCoverage));
        Assert.True(pause.TryAcceptMediaCoverage(pause.Registrations[0], fixture.Context, pauseCoverage));
        Assert.True(resume.TryAcceptMediaCoverage(resume.Registrations[0], fixture.Context, resumeCoverage));

        var conflict = Coverage(resume.Registrations[0], 77, -699, sharedSpans);
        Assert.False(resume.TryAcceptMediaCoverage(resume.Registrations[0], fixture.Context, conflict));
        Assert.True(resume.HasMediaCoverageFailure);
        Assert.Equal(1, resume.PendingMediaCoverageCount);
        Assert.True(resume.AccountMediaCoverageFailure(resume.Registrations[0], fixture.Context));
        Assert.Equal(0, resume.PendingMediaCoverageCount);
        Assert.True(owner.CurrentState.IsTerminal);
    }

    [Fact]
    public async Task RetainedResumeInputCannotBeExcludedByANewerPause()
    {
        var fixture = Create();
        var capability = fixture.Capabilities[0] with
        {
            Streams = fixture.Capabilities[0].Streams.Select(anchor => anchor with
            {
                InputClock = Clock(60),
            }).ToArray(),
        };
        fixture = fixture with { Capabilities = [capability] };
        var owner = new PauseParticipantOwner(fixture.Context, fixture.State);

        var pause = owner.CreateOperation(Command(fixture, owner.CurrentState, PauseCommandAction.Pause))!;
        CopyAll(pause, TimeSpan.FromSeconds(1.6)); // input 96 is first excluded
        await pause.CloseAsync();
        Assert.Equal(PauseModelDisposition.ModeledCommitted, pause.Reduce(true)!.Disposition);
        Assert.True(pause.TryAcceptMediaCoverage(pause.Registrations[0], fixture.Context,
            Coverage(pause.Registrations[0], 1, 0, [new(0, 96)])));

        var resume = owner.CreateOperation(Command(fixture, owner.CurrentState, PauseCommandAction.Resume))!;
        CopyAll(resume, TimeSpan.FromSeconds(3.2)); // input 192 is first admitted
        await resume.CloseAsync();
        Assert.Equal(PauseModelDisposition.ModeledCommitted, resume.Reduce(true)!.Disposition);
        Assert.True(resume.TryAcceptMediaCoverage(resume.Registrations[0], fixture.Context,
            // Capture resumes at 3.2 s (input 192), while raw media PTS 96 at 1/60 is 1.6 s.
            Coverage(resume.Registrations[0], 2, 96, [new(192, 252)])));

        var nextPause = owner.CreateOperation(Command(fixture, owner.CurrentState, PauseCommandAction.Pause))!;
        CopyAll(nextPause, TimeSpan.FromSeconds(4)); // input 240 would exclude retained [240,252)
        await nextPause.CloseAsync();
        var terminal = nextPause.Reduce(true)!;
        Assert.Equal(PauseModelDisposition.StopAndFinalizeAllOwnedOutputs, terminal.Disposition);
        Assert.True(terminal.State.IsTerminal);
    }

    [Fact]
    public async Task RepeatedPauseResumeCyclesAcceptLateCoverageExcludingEveryPausedInterval()
    {
        var fixture = Create();
        var owner = new PauseParticipantOwner(fixture.Context, fixture.State);
        var operations = new List<PauseParticipantEvidenceAdapter>();
        foreach (var (action, seconds) in new[]
        {
            (PauseCommandAction.Pause, 1),
            (PauseCommandAction.Resume, 2),
            (PauseCommandAction.Pause, 3),
            (PauseCommandAction.Resume, 4),
        })
        {
            var operation = owner.CreateOperation(Command(fixture, owner.CurrentState, action))!;
            CopyAll(operation, seconds);
            await operation.CloseAsync();
            var committed = operation.Reduce(true)!;
            Assert.Equal(PauseModelDisposition.ModeledCommitted, committed.Disposition);
            Assert.Equal(TimeSpan.FromSeconds(seconds), committed.State.LastBoundary);
            operations.Add(operation);
        }

        Assert.Equal(PauseModelPhase.Running, owner.CurrentState.Phase);
        var retained = new PauseInputSpan[] { new(0, 10), new(20, 30), new(40, 50) };
        var first = operations[0];
        Assert.True(first.TryAcceptMediaCoverage(first.Registrations[0], fixture.Context,
            Coverage(first.Registrations[0], 88, -500, retained)));
        foreach (var operation in operations.Skip(1))
            Assert.True(operation.TryAcceptMediaCoverage(operation.Registrations[0], fixture.Context,
                Coverage(operation.Registrations[0], 88, -500, retained)));
        Assert.All(operations, operation =>
        {
            Assert.Equal(0, operation.PendingMediaCoverageCount);
            Assert.False(operation.HasMediaCoverageFailure);
        });
        Assert.Equal(PauseModelPhase.Running, owner.CurrentState.Phase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MediaPacketAndSpanCapacitiesFailClosed(bool exceedPacketCapacity)
    {
        var fixture = Create();
        var owner = new PauseParticipantOwner(fixture.Context, fixture.State);
        var operation = owner.CreateOperation(Command(fixture, fixture.State))!;
        CopyAll(operation, 1);
        await operation.CloseAsync();
        Assert.Equal(PauseModelDisposition.ModeledCommitted, operation.Reduce(true)!.Disposition);
        var registration = operation.Registrations[0];
        var receipt = Coverage(registration, 1, -100, [new(0, 10)]);
        var first = receipt.Streams[0];
        receipt = receipt with
        {
            Streams = [exceedPacketCapacity
                ? first with
                {
                    Packets = Enumerable.Repeat(first.Packets[0], PauseParticipantOwner.MaxMediaPacketsPerStream + 1).ToArray(),
                }
                : first with
                {
                    Packets = [first.Packets[0] with
                    {
                        CaptureSupport = Enumerable.Range(0, PauseParticipantOwner.MaxCaptureSpansPerPacket + 1)
                            .Select(index => new PauseInputSpan(index, index + 1)).ToArray(),
                    }],
                }, .. receipt.Streams.Skip(1)],
        };

        Assert.False(operation.TryAcceptMediaCoverage(registration, fixture.Context, receipt));
        Assert.True(operation.HasMediaCoverageFailure);
        Assert.Equal(1, operation.PendingMediaCoverageCount);
        Assert.True(operation.AccountMediaCoverageFailure(registration, fixture.Context));
        Assert.Equal(0, operation.PendingMediaCoverageCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrDuplicateMediaStreamsFailClosed(bool duplicate)
    {
        var fixture = Create();
        var owner = new PauseParticipantOwner(fixture.Context, fixture.State);
        var operation = owner.CreateOperation(Command(fixture, fixture.State))!;
        CopyAll(operation, 1);
        await operation.CloseAsync();
        Assert.Equal(PauseModelDisposition.ModeledCommitted, operation.Reduce(true)!.Disposition);
        var registration = operation.Registrations[0];
        var receipt = Coverage(registration, 1, -100, [new(0, 10)]);
        receipt = receipt with
        {
            Streams = duplicate
                ? Enumerable.Repeat(receipt.Streams[0], receipt.Streams.Count).ToArray()
                : receipt.Streams.Skip(1).ToArray(),
        };

        Assert.False(operation.TryAcceptMediaCoverage(registration, fixture.Context, receipt));
        Assert.True(operation.HasMediaCoverageFailure);
        Assert.True(operation.AccountMediaCoverageFailure(registration, fixture.Context));
    }

    [Fact]
    public async Task ForeignMediaRegistrationAndContextRejectWithoutPoisoning()
    {
        var fixture = Create();
        var owner = new PauseParticipantOwner(fixture.Context, fixture.State);
        var operation = owner.CreateOperation(Command(fixture, fixture.State))!;
        CopyAll(operation, 1);
        await operation.CloseAsync();
        Assert.Equal(PauseModelDisposition.ModeledCommitted, operation.Reduce(true)!.Disposition);
        var registration = operation.Registrations[0];
        var receipt = Coverage(registration, 1, -100, [new(0, 10)]);
        var foreignFixture = Create();
        var foreignOwner = new PauseParticipantOwner(foreignFixture.Context, foreignFixture.State);
        var foreign = foreignOwner.CreateOperation(Command(foreignFixture, foreignFixture.State))!;

        Assert.False(operation.TryAcceptMediaCoverage(foreign.Registrations[0], fixture.Context, receipt));
        Assert.False(operation.TryAcceptMediaCoverage(registration,
            fixture.Context with { Generation = fixture.Context.Generation + 1 }, receipt));
        Assert.False(operation.HasMediaCoverageFailure);
        Assert.Equal(1, operation.PendingMediaCoverageCount);
        Assert.True(operation.TryAcceptMediaCoverage(registration, fixture.Context, receipt));
        Assert.Equal(0, operation.PendingMediaCoverageCount);
    }

    [Fact]
    public async Task MediaCoverageIsDeepCopiedByBoundedIndexReadsAndCallerFailuresAreLatched()
    {
        var fixture = Create();
        var owner = new PauseParticipantOwner(fixture.Context, fixture.State);
        var operation = owner.CreateOperation(Command(fixture, fixture.State))!;
        CopyAll(operation, 1);
        await operation.CloseAsync();
        Assert.Equal(PauseModelDisposition.ModeledCommitted, operation.Reduce(true)!.Disposition);
        var registration = operation.Registrations[0];
        var canonical = Coverage(registration, 1, -100, [new(0, 10)]);
        var spanArrays = new List<PauseInputSpan[]>();
        var spanLists = new List<IndexOnlyList<PauseInputSpan>>();
        var packetLists = new List<IndexOnlyList<PauseMediaPacketWitness>>();
        var callerStreams = canonical.Streams.Select(stream =>
        {
            var packets = stream.Packets.Select(packet =>
            {
                var spans = packet.CaptureSupport.ToArray();
                spanArrays.Add(spans);
                var list = new IndexOnlyList<PauseInputSpan>(spans);
                spanLists.Add(list);
                return packet with { CaptureSupport = list };
            }).ToArray();
            var list = new IndexOnlyList<PauseMediaPacketWitness>(packets);
            packetLists.Add(list);
            return stream with { Packets = list };
        }).ToArray();
        var streamList = new IndexOnlyList<PauseStreamMediaCoverage>(callerStreams);
        Assert.True(operation.TryAcceptMediaCoverage(registration, fixture.Context,
            canonical with { Streams = streamList }));
        Assert.Equal(callerStreams.Length, streamList.IndexReads);
        Assert.All(packetLists, list => Assert.Equal(1, list.IndexReads));
        Assert.All(spanLists, list => Assert.Equal(1, list.IndexReads));

        spanArrays[0][0] = new(100, 110);
        callerStreams[0] = callerStreams[0] with { Id = new(PauseStreamKind.CameraVideo, 99) };
        Assert.True(operation.TryAcceptMediaCoverage(registration, fixture.Context, canonical));

        Assert.False(operation.TryAcceptMediaCoverage(registration, fixture.Context, canonical with
        {
            Streams = new ThrowingIndexList<PauseStreamMediaCoverage>(canonical.Streams.Count),
        }));
        Assert.True(operation.HasMediaCoverageFailure);
        Assert.True(operation.AccountMediaCoverageFailure(registration, fixture.Context));
    }

    [Fact]
    public async Task BlockingCallerMediaCollectionDoesNotHoldTheOwnerLock()
    {
        var fixture = Create();
        var owner = new PauseParticipantOwner(fixture.Context, fixture.State);
        var operation = owner.CreateOperation(Command(fixture, fixture.State))!;
        CopyAll(operation, 1);
        await operation.CloseAsync();
        Assert.Equal(PauseModelDisposition.ModeledCommitted, operation.Reduce(true)!.Disposition);
        var registration = operation.Registrations[0];
        var canonical = Coverage(registration, 1, -100, [new(0, 10)]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocking = new BlockingCountList<PauseStreamMediaCoverage>(canonical.Streams.ToArray(), entered, release);
        var copy = Task.Run(() => operation.TryAcceptMediaCoverage(registration, fixture.Context,
            canonical with { Streams = blocking }));

        await entered.Task.WaitAsync(TestTimeouts.Signal);
        try
        {
            Assert.Equal(1, await Task.Run(() => operation.PendingMediaCoverageCount)
                .WaitAsync(TestTimeouts.Signal));
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.True(await copy.WaitAsync(TestTimeouts.Signal));
        Assert.Equal(0, operation.PendingMediaCoverageCount);
    }

    private static void CopyAll(PauseParticipantEvidenceAdapter operation, int seconds) =>
        CopyAll(operation, TimeSpan.FromSeconds(seconds));

    private static void CopyAll(PauseParticipantEvidenceAdapter operation, TimeSpan boundary)
    {
        foreach (var gate in operation.Gates)
        {
            Assert.True(gate.TryEnter(gate.Registration, out var lease));
            using (lease)
                Assert.True(lease!.TryCopy(Ack(operation.Command, gate.Registration.Participant, boundary)));
        }
    }

    private static PauseOutputAck Ack(PauseCommand command, PauseOutputParticipant output, int seconds) =>
        Ack(command, output, TimeSpan.FromSeconds(seconds));

    private static PauseOutputAck Ack(PauseCommand command, PauseOutputParticipant output, TimeSpan boundary) =>
        new(command.OperationId, command.Sequence, command.Identity, command.Action, output.Kind, output.OutputEpoch,
            PauseOutputEffect.Applied, boundary, output.Streams.Select(stream =>
                new PauseStreamEffect(stream.Id, stream.InputClock.TransformId,
                    ExactInputIndex(stream.InputClock, boundary))).ToArray());

    private static PauseOutputRelease Release(PauseAcknowledgmentResult terminal, PauseParticipantCleanupTicket ticket,
        bool mediaFailed = false)
    {
        var command = terminal.State.TerminalCommand!;
        return new(command.OperationId, command.Sequence, command.Identity, ticket.Kind, ticket.OutputEpoch,
            true, true, !mediaFailed, true);
    }

    private static PauseAcknowledgmentResult AccountMediaFailures(PauseParticipantEvidenceAdapter operation,
        CaptureSessionContext context)
    {
        foreach (var registration in operation.Registrations)
            Assert.True(operation.AccountMediaCoverageFailure(registration, context));
        Assert.Equal(0, operation.PendingMediaCoverageCount);
        Assert.True(operation.HasMediaCoverageFailure);
        return operation.Reduce(false)!;
    }

    private static PauseOutputMediaCoverage Coverage(PauseParticipantRegistration registration, long packetIdentity,
        long rawPts, IReadOnlyList<PauseInputSpan> spans)
    {
        var supportUnits = spans.Sum(span => span.EndIndexExclusive - span.StartIndex);
        var streams = registration.Streams.Select(anchor => new PauseStreamMediaCoverage(anchor.Id,
            [new PauseMediaPacketWitness(packetIdentity, anchor.MediaTiming.TimingId, rawPts, supportUnits,
                anchor.MediaTiming.SecondsPerDecodedUnitNumerator, anchor.MediaTiming.SecondsPerDecodedUnitDenominator,
                supportUnits, 0, 0, spans)])).ToArray();
        var command = registration.Command;
        return new(command.OperationId, command.Sequence, command.Identity, command.Action,
            registration.Kind, registration.OutputEpoch, registration.RegistrationGeneration, streams);
    }

    private static PauseCommand Command(Fixture fixture, PauseModelState state, PauseCommandAction action = PauseCommandAction.Pause) =>
        PauseCommandPolicy.Plan(fixture.Plan, fixture.Snapshot, state, action, Guid.NewGuid(), state.Sequence + 1, fixture.Capabilities).Command!;

    private static Fixture Create(bool camera = false, bool separate = false)
    {
        var settings = new AppSettings { Audio = new AudioMixConfig { SeparateAudioTracks = separate } };
        WebcamCaptureSelection? webcam = camera ? new("camera-id", "Camera", 640, 480, 30, "MJPEG", 1500) : null;
        var plan = TutorialRecordingPolicy.Create(Guid.NewGuid(), DateTimeOffset.UnixEpoch,
            new WindowCaptureTarget(23, new IntPtr(234), "Tutorial", "tutorial.exe", "class"),
            CaptureStartupBaselineResult.Create(settings), webcam, camera).Plan!;
        var context = new CaptureSessionContext(Guid.NewGuid(), 1, plan.Request, plan.Request.Target, plan.Request.Target)
        {
            AdmittedDevices = camera ? new([AdmittedCaptureDevice.Exact(CaptureDeviceRole.Webcam, "camera-id")]) : CaptureDeviceManifest.Empty,
        };
        var state = PauseCommandModel.CreateInitial(context, Guid.NewGuid());
        var anchors = new[] { new PauseStreamAnchor(new(PauseStreamKind.ScreenVideo, 0), Clock(), MediaTiming(PauseDecodedUnitKind.VideoFrame)) }
            .Concat(plan.AudioTracks.Encoders.Select(route => new PauseStreamAnchor(
                new(PauseStreamKind.ProductAudioTrack, route.OutputTrackIndex), Clock(), MediaTiming(PauseDecodedUnitKind.AudioSample)))).ToArray();
        var capabilities = new List<PauseOutputCapability> { new(PauseOutputKind.Screen, Guid.NewGuid(), true, true, anchors, state.Identity, null) };
        if (camera)
            capabilities.Add(new(PauseOutputKind.Camera, Guid.NewGuid(), true, true,
                [new(new(PauseStreamKind.CameraVideo, 0), Clock(), MediaTiming(PauseDecodedUnitKind.VideoFrame)),
                 new(new(PauseStreamKind.CameraCompatibilityAudio, 5), Clock(), MediaTiming(PauseDecodedUnitKind.AudioSample))], state.Identity, "camera-id"));
        return new(plan, context, state,
            new(CaptureLifecycleStatus.Active, CaptureReadiness.Available, CaptureHealth.Healthy, context, null), capabilities);
    }

    private static PauseInputClock Clock(long inputsPerSecond = 10, long inputIndexAtOrigin = 0) =>
        new(Guid.NewGuid(), inputIndexAtOrigin, 0, 1, 1, inputsPerSecond);

    // Explicit synthetic media periods, independent of the fixture's capture clock.
    private static PauseMediaTimingDeclaration MediaTiming(PauseDecodedUnitKind kind) =>
        new(Guid.NewGuid(), kind, 1, kind == PauseDecodedUnitKind.VideoFrame ? 60 : 48_000);

    private static long ExactInputIndex(PauseInputClock clock, TimeSpan boundary)
    {
        var numerator = ((BigInteger)boundary.Ticks * clock.CaptureOriginDenominator -
            (BigInteger)clock.CaptureOriginNumerator * TimeSpan.TicksPerSecond) * clock.SecondsPerInputDenominator;
        var denominator = (BigInteger)TimeSpan.TicksPerSecond * clock.CaptureOriginDenominator *
            clock.SecondsPerInputNumerator;
        var delta = BigInteger.DivRem(numerator, denominator, out var remainder);
        if (!remainder.IsZero)
            throw new InvalidOperationException("The test boundary is not exactly representable by the input clock.");
        return checked(clock.InputIndexAtOrigin + (long)delta);
    }

    private sealed class IndexOnlyList<T>(T[] items) : IReadOnlyList<T>
    {
        public int Count => items.Length;
        public int IndexReads { get; private set; }
        public T this[int index]
        {
            get
            {
                IndexReads++;
                return items[index];
            }
        }
        public IEnumerator<T> GetEnumerator() =>
            throw new InvalidOperationException("The bounded media copy must not enumerate caller storage.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ThrowingIndexList<T>(int count) : IReadOnlyList<T>
    {
        public int Count => count;
        public T this[int index] => throw new InvalidOperationException("Caller media storage became unreadable.");
        public IEnumerator<T> GetEnumerator() =>
            throw new InvalidOperationException("The bounded media copy must not enumerate caller storage.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class BlockingCountList<T>(T[] items, TaskCompletionSource entered,
        TaskCompletionSource release) : IReadOnlyList<T>
    {
        public int Count
        {
            get
            {
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
                return items.Length;
            }
        }
        public T this[int index] => items[index];
        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)items).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed record Fixture(TutorialRecordingPlan Plan, CaptureSessionContext Context, PauseModelState State,
        CaptureLifecycleSnapshot Snapshot, IReadOnlyList<PauseOutputCapability> Capabilities);
}
