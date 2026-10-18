using System.Collections;
using System.Numerics;
using WatchDog.Core.Audio;
using WatchDog.Core.Capture;
using WatchDog.Core.Devices;
using WatchDog.Core.Settings;
using WatchDog.Core.Tutorial;
using WatchDog.Core.Webcam;

namespace WatchDog.Core.Tests.Tutorial;

public sealed class PauseCommandContractTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void CompleteOutputAndStreamAcksOnlyCommitAModeledPauseAndResume(bool camera, bool separateAudio)
    {
        var fixture = Fixture(camera, separateAudio);
        var pause = Plan(fixture, PauseCommandAction.Pause, 1).Command!;
        Assert.Equal(camera ? 2 : 1, pause.Outputs.Count);
        Assert.Equal(separateAudio ? 3 : 2, pause.Outputs[0].Streams.Count);
        Assert.False(pause.NativePauseProven);
        Assert.False(pause.NativeAlignmentProven);
        Assert.False(pause.NativeLivenessAdmitted);
        var paused = PauseAcknowledgmentPolicy.Evaluate(pause, fixture.State, true, Acks(pause, 1));
        Assert.Equal(PauseModelDisposition.ModeledCommitted, paused.Disposition);
        Assert.Equal(PauseModelPhase.Paused, paused.State.Phase);
        Assert.Equal(TimeSpan.FromSeconds(1), paused.State.LastBoundary);
        Assert.Equal(PauseLivenessRequirement.SuspendProgressAfterAggregateCommit, paused.LivenessRequirement);
        var resume = Plan(fixture with { State = paused.State }, PauseCommandAction.Resume, 2).Command!;
        var resumed = PauseAcknowledgmentPolicy.Evaluate(resume, paused.State, true, Acks(resume, 2));
        Assert.Equal(PauseModelDisposition.ModeledCommitted, resumed.Disposition);
        Assert.Equal(PauseModelPhase.Running, resumed.State.Phase);
        Assert.Equal(PauseLivenessRequirement.RearmProgressAfterAggregateCommit, resumed.LivenessRequirement);
        Assert.False(resumed.NativePauseProven);
        Assert.Null(PauseCommandPolicy.Plan(fixture.Plan, fixture.Snapshot, resumed.State,
            PauseCommandAction.Pause, resume.OperationId, 3, fixture.Capabilities).Command);
    }

    [Fact]
    public void DifferentInputClocksMapPauseAndResumeToExactCaptureTicks()
    {
        var fixture = Fixture(false, true);
        var capabilities = fixture.Capabilities.ToArray();
        var screen = capabilities[0];
        capabilities[0] = screen with
        {
            Streams = screen.Streams.Select(anchor => anchor.Id.Kind == PauseStreamKind.ProductAudioTrack
                ? anchor with { InputClock = Clock(48_000, inputIndexAtOrigin: 40) }
                : anchor with { InputClock = Clock(60, inputIndexAtOrigin: 2) }).ToArray(),
        };
        fixture = fixture with { Capabilities = capabilities };
        var pause = Plan(fixture, PauseCommandAction.Pause, 1).Command!;
        var paused = PauseAcknowledgmentPolicy.Evaluate(
            pause, fixture.State, true, Acks(pause, TimeSpan.FromSeconds(1.6)));
        Assert.Equal(PauseModelDisposition.ModeledCommitted, paused.Disposition);
        Assert.Equal(TimeSpan.FromSeconds(1.6), paused.State.LastBoundary);

        var resumeFixture = fixture with { State = paused.State };
        var resume = Plan(resumeFixture, PauseCommandAction.Resume, 2).Command!;
        var resumed = PauseAcknowledgmentPolicy.Evaluate(
            resume, paused.State, true, Acks(resume, TimeSpan.FromSeconds(3.2)));
        Assert.Equal(PauseModelDisposition.ModeledCommitted, resumed.Disposition);
        Assert.Equal(TimeSpan.FromSeconds(3.2), resumed.State.LastBoundary);
    }

    [Fact]
    public void InvalidSnapshotPriorSequenceOrUnsupportedCapabilityRejectBeforeEffects()
    {
        var fixture = Fixture(false, false);
        Assert.Null(Plan(fixture, PauseCommandAction.Resume, 1).Command);
        Assert.Null(Plan(fixture, PauseCommandAction.Pause, 2).Command);
        Assert.Null(Plan(fixture with { Snapshot = fixture.Snapshot with { Context = null } }, PauseCommandAction.Pause, 1).Command);
        Assert.Null(Plan(fixture with { Snapshot = fixture.Snapshot with { Readiness = CaptureReadiness.Busy } }, PauseCommandAction.Pause, 1).Command);
        Assert.Null(Plan(fixture with { Snapshot = fixture.Snapshot with { Status = CaptureLifecycleStatus.Faulted } }, PauseCommandAction.Pause, 1).Command);
        Assert.Null(Plan(fixture with { Snapshot = fixture.Snapshot with { Readiness = CaptureReadiness.Closed } }, PauseCommandAction.Pause, 1).Command);
        Assert.Null(Plan(fixture with { Snapshot = fixture.Snapshot with { Context = fixture.Context with { AdmittedDevices = null! } } }, PauseCommandAction.Pause, 1).Command);
        Assert.Null(Plan(fixture with { Capabilities = [fixture.Capabilities[0] with { CanPause = false }] }, PauseCommandAction.Pause, 1).Command);
        Assert.Null(Plan(fixture with { Capabilities = [fixture.Capabilities[0] with { CanResume = false }] }, PauseCommandAction.Pause, 1).Command);
        Assert.Null(Plan(fixture with { Capabilities = [] }, PauseCommandAction.Pause, 1).Command);
        Assert.Null(Plan(fixture with { Capabilities = [fixture.Capabilities[0], fixture.Capabilities[0]] }, PauseCommandAction.Pause, 1).Command);
    }

    [Fact]
    public void InvalidOrBroaderCaptureAndUnadmittedCameraReject()
    {
        var fixture = Fixture(true, false);
        Assert.Null(Plan(fixture with { Snapshot = fixture.Snapshot with { Context = fixture.Context with { AdmittedDevices = CaptureDeviceManifest.Empty } } }, PauseCommandAction.Pause, 1).Command);
        Assert.Null(Plan(fixture with { Snapshot = fixture.Snapshot with { Context = fixture.Context with { ActualTarget = new DisplayCaptureTarget("display") } } }, PauseCommandAction.Pause, 1).Command);
        var both = fixture.Plan.Request.StartPolicy!.Recording with { Mode = WatchDog.Core.Recording.RecordingMode.Both };
        var bothRequest = new CaptureRequest(fixture.Plan.Request.RequestId, fixture.Plan.Request.RequestedAt,
            CaptureInitiation.Manual, fixture.Plan.Request.Target, fixture.Plan.Request.Webcam,
            fixture.Plan.Request.StartPolicy with { Recording = both });
        Assert.Null(PauseCommandPolicy.Plan(fixture.Plan,
            fixture.Snapshot with { Context = fixture.Context with { Request = bothRequest } },
            fixture.State, PauseCommandAction.Pause, Guid.NewGuid(), 1, fixture.Capabilities).Command);
    }

    [Fact]
    public void ForeignOutputCapabilitiesAndCameraBindingRejectBeforeEffects()
    {
        var own = Fixture(true, false);
        var foreign = Fixture(true, false);
        Assert.Null(Plan(own with { Capabilities = foreign.Capabilities }, PauseCommandAction.Pause, 1).Command);
        Assert.Null(Plan(own with
        {
            Capabilities = [own.Capabilities[0], own.Capabilities[1] with { CameraDeviceId = "other-camera" }],
        }, PauseCommandAction.Pause, 1).Command);
        Assert.Null(Plan(own with
        {
            Capabilities = [own.Capabilities[0] with { CameraDeviceId = "camera-id" }, own.Capabilities[1]],
        }, PauseCommandAction.Pause, 1).Command);
    }

    [Fact]
    public void MissingDuplicateOrExtraStreamAnchorsAndOutputEpochReject()
    {
        var fixture = Fixture(true, true);
        var screen = fixture.Capabilities[0];
        foreach (var streams in new[]
        {
            screen.Streams.Skip(1).ToArray(),
            screen.Streams.Concat([screen.Streams[0]]).ToArray(),
            screen.Streams.Concat([new PauseStreamAnchor(new(PauseStreamKind.ProductAudioTrack, 5), Clock(), MediaTiming(PauseDecodedUnitKind.AudioSample))]).ToArray(),
        })
        {
            var invalid = new[] { screen with { Streams = streams }, fixture.Capabilities[1] };
            Assert.Null(Plan(fixture with { Capabilities = invalid }, PauseCommandAction.Pause, 1).Command);
        }
        Assert.Null(Plan(fixture with { Capabilities = [screen with { OutputEpoch = Guid.Empty }, fixture.Capabilities[1]] }, PauseCommandAction.Pause, 1).Command);
        Assert.Null(Plan(fixture with { Capabilities = [screen, fixture.Capabilities[1] with { OutputEpoch = screen.OutputEpoch }] }, PauseCommandAction.Pause, 1).Command);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void ForeignOrMalformedAckCannotCommit(int defect)
    {
        var fixture = Fixture(true, true);
        var command = Plan(fixture, PauseCommandAction.Pause, 1).Command!;
        var acks = Acks(command, 1).ToArray();
        acks[0] = defect switch
        {
            0 => acks[0] with { OperationId = Guid.NewGuid() },
            1 => acks[0] with { Identity = acks[0].Identity with { Generation = acks[0].Identity.Generation + 1 } },
            2 => acks[0] with { OutputEpoch = Guid.NewGuid() },
            3 => acks[0] with { Sequence = 2 },
            4 => acks[0] with { Streams = acks[0].Streams.Skip(1).ToArray() },
            5 => acks[0] with { EffectiveBoundary = TimeSpan.FromSeconds(2) },
            _ => acks[0] with
            {
                Streams = [acks[0].Streams[0] with { TransformId = Guid.NewGuid() }, .. acks[0].Streams.Skip(1)],
            },
        };
        var result = PauseAcknowledgmentPolicy.Evaluate(command, fixture.State, true, acks);
        Assert.Equal(PauseModelDisposition.StopAndFinalizeAllOwnedOutputs, result.Disposition);
        Assert.True(result.State.IsTerminal);
        Assert.Equal(2, result.CleanupOutputs.Count);
    }

    [Fact]
    public void InputIndexMismatchAndNonintegralCaptureTicksCannotCommit()
    {
        var fixture = Fixture(false, false);
        var command = Plan(fixture, PauseCommandAction.Pause, 1).Command!;
        var ack = Acks(command, 1)[0];
        var wrongIndex = ack with
        {
            Streams = [ack.Streams[0] with { InputBoundaryIndex = ack.Streams[0].InputBoundaryIndex + 1 }, ack.Streams[1]],
        };
        Assert.Equal(PauseModelDisposition.StopAndFinalizeAllOwnedOutputs,
            PauseAcknowledgmentPolicy.Evaluate(command, fixture.State, true, [wrongIndex]).Disposition);
        var rawMediaPtsSubstitution = ack with
        {
            Streams = ack.Streams.Select(effect => effect with { InputBoundaryIndex = -9_000 }).ToArray(),
        };
        Assert.Equal(PauseModelDisposition.StopAndFinalizeAllOwnedOutputs,
            PauseAcknowledgmentPolicy.Evaluate(command, fixture.State, true, [rawMediaPtsSubstitution]).Disposition);

        var screen = fixture.Capabilities[0];
        var fractional = fixture with
        {
            Capabilities = [screen with
            {
                Streams = screen.Streams.Select(a => a with
                {
                    InputClock = new(Guid.NewGuid(), 0, 0, 1, 1, 3),
                }).ToArray(),
            }],
        };
        var fractionalCommand = Plan(fractional, PauseCommandAction.Pause, 1).Command!;
        var fractionalAck = Acks(fractionalCommand, 1)[0] with
        {
            EffectiveBoundary = TimeSpan.FromTicks(3_333_333),
            Streams = fractionalCommand.Outputs[0].Streams.Select(a =>
                new PauseStreamEffect(a.Id, a.InputClock.TransformId, 1)).ToArray(),
        };
        Assert.Equal(PauseModelDisposition.StopAndFinalizeAllOwnedOutputs,
            PauseAcknowledgmentPolicy.Evaluate(fractionalCommand, fixture.State, true, [fractionalAck]).Disposition);

        var phased = fixture with
        {
            Capabilities = [screen with
            {
                Streams = screen.Streams.Select((a, index) => a with
                {
                    InputClock = index == 0
                        ? new(Guid.NewGuid(), 0, 0, 1, 1, 60)
                        : new(Guid.NewGuid(), 0, 1, 120, 1, 60),
                }).ToArray(),
            }],
        };
        var phasedCommand = Plan(phased, PauseCommandAction.Pause, 1).Command!;
        var phasedAck = new PauseOutputAck(phasedCommand.OperationId, phasedCommand.Sequence,
            phasedCommand.Identity, phasedCommand.Action, PauseOutputKind.Screen,
            phasedCommand.Outputs[0].OutputEpoch, PauseOutputEffect.Applied, TimeSpan.FromSeconds(1.6),
            phasedCommand.Outputs[0].Streams.Select(anchor =>
                new PauseStreamEffect(anchor.Id, anchor.InputClock.TransformId, 96)).ToArray());
        Assert.Equal(PauseModelDisposition.StopAndFinalizeAllOwnedOutputs,
            PauseAcknowledgmentPolicy.Evaluate(phasedCommand, fixture.State, true, [phasedAck]).Disposition);
    }

    [Fact]
    public void PartialOrUncertainEffectsRequireAllOutputTerminalCleanup()
    {
        var fixture = Fixture(true, false);
        var command = Plan(fixture, PauseCommandAction.Pause, 1).Command!;
        var acks = Acks(command, 1).ToArray();
        foreach (var changed in new[]
        {
            new[] { acks[0] },
            new[] { acks[0], acks[1] with { Effect = PauseOutputEffect.Rejected } },
            new[] { acks[0] with { Effect = PauseOutputEffect.Unknown }, acks[1] },
            new[] { acks[0], acks[0], acks[1] },
        })
        {
            var result = PauseAcknowledgmentPolicy.Evaluate(command, fixture.State, true, changed);
            Assert.Equal(PauseModelDisposition.StopAndFinalizeAllOwnedOutputs, result.Disposition);
            Assert.Equal(2, result.CleanupOutputs.Count);
            Assert.True(result.State.IsTerminal);
        }
    }

    [Fact]
    public void StaleDispatchedCommandRetainsCurrentAndCommandedOutputEpochsForCleanup()
    {
        var fixture = Fixture(false, false);
        var command = Plan(fixture, PauseCommandAction.Pause, 1).Command!;
        var committed = PauseAcknowledgmentPolicy.Evaluate(command, fixture.State, true, Acks(command, 1));
        var rotated = fixture.Capabilities[0] with { OutputEpoch = Guid.NewGuid() };
        // A stale command can still have unknown effects. Its old output and the newer
        // owned output both remain cleanup obligations, with no modeled state revival.
        var later = PauseCommandPolicy.Plan(fixture.Plan, fixture.Snapshot, committed.State,
            PauseCommandAction.Resume, Guid.NewGuid(), 2, fixture.Capabilities).Command!;
        var resumed = PauseAcknowledgmentPolicy.Evaluate(later, committed.State, true, Acks(later, 2));
        var next = PauseCommandPolicy.Plan(fixture.Plan, fixture.Snapshot, resumed.State,
            PauseCommandAction.Pause, Guid.NewGuid(), 3, [rotated]).Command!;
        var newer = PauseAcknowledgmentPolicy.Evaluate(next, resumed.State, true, Acks(next, 3));
        var stale = PauseAcknowledgmentPolicy.Evaluate(command, newer.State, true, Acks(command, 1));
        Assert.Equal(PauseModelDisposition.StopAndFinalizeAllOwnedOutputs, stale.Disposition);
        Assert.Equal(2, stale.CleanupOutputs.Count);
        Assert.False(PauseCleanupPolicy.Evaluate(stale, []).CleanupCompleted);
    }

    [Fact]
    public void LateDispatchedEpochExpandsAlreadyTerminalCleanupWithoutRevival()
    {
        var fixture = Fixture(false, false);
        var first = Plan(fixture, PauseCommandAction.Pause, 1).Command!;
        var firstPaused = PauseAcknowledgmentPolicy.Evaluate(first, fixture.State, true, Acks(first, 1));
        var firstResume = Plan(fixture with { State = firstPaused.State }, PauseCommandAction.Resume, 2).Command!;
        var running = PauseAcknowledgmentPolicy.Evaluate(firstResume, firstPaused.State, true, Acks(firstResume, 2));
        var secondCapabilities = new[] { fixture.Capabilities[0] with { OutputEpoch = Guid.NewGuid() } };
        var secondFixture = fixture with { State = running.State, Capabilities = secondCapabilities };
        var second = Plan(secondFixture, PauseCommandAction.Pause, 3).Command!;
        var secondPaused = PauseAcknowledgmentPolicy.Evaluate(second, running.State, true, Acks(second, 3));
        var secondResume = Plan(secondFixture with { State = secondPaused.State }, PauseCommandAction.Resume, 4).Command!;
        var secondRunning = PauseAcknowledgmentPolicy.Evaluate(secondResume, secondPaused.State, true, Acks(secondResume, 4));
        var thirdCapabilities = new[] { secondCapabilities[0] with { OutputEpoch = Guid.NewGuid() } };
        var third = Plan(secondFixture with { State = secondRunning.State, Capabilities = thirdCapabilities },
            PauseCommandAction.Pause, 5).Command!;
        var terminal = PauseAcknowledgmentPolicy.Evaluate(third, secondRunning.State, true, []);
        Assert.Equal(2, terminal.CleanupOutputs.Count);

        var enlarged = PauseAcknowledgmentPolicy.Evaluate(first, terminal.State, true, Acks(first, 1));
        Assert.Equal(PauseModelDisposition.StopAndFinalizeAllOwnedOutputs, enlarged.Disposition);
        Assert.True(enlarged.State.IsTerminal);
        Assert.Equal(3, enlarged.CleanupOutputs.Count);
        var releases = enlarged.CleanupOutputs.Select(output => new PauseOutputRelease(
            first.OperationId, first.Sequence, first.Identity, output.Kind, output.OutputEpoch,
            true, true, true, true)).ToArray();
        Assert.False(PauseCleanupPolicy.Evaluate(enlarged, releases[..2]).CleanupCompleted);
        Assert.True(PauseCleanupPolicy.Evaluate(enlarged, releases).CleanupCompleted);
        Assert.Same(enlarged.State,
            PauseAcknowledgmentPolicy.Evaluate(first, enlarged.State, true, Acks(first, 1)).State);
    }

    [Fact]
    public void ReusingOutputEpochWithDifferentAnchorRejectsAfterResume()
    {
        var fixture = Fixture(false, false);
        var first = Plan(fixture, PauseCommandAction.Pause, 1).Command!;
        var paused = PauseAcknowledgmentPolicy.Evaluate(first, fixture.State, true, Acks(first, 1));
        var resume = Plan(fixture with { State = paused.State }, PauseCommandAction.Resume, 2).Command!;
        var running = PauseAcknowledgmentPolicy.Evaluate(resume, paused.State, true, Acks(resume, 2));
        var altered = fixture.Capabilities[0] with
        {
            Streams = fixture.Capabilities[0].Streams.Select(s => s with
            {
                InputClock = s.InputClock with { InputIndexAtOrigin = 20 },
            }).ToArray(),
        };
        Assert.Null(Plan(fixture with { State = running.State, Capabilities = [altered] },
            PauseCommandAction.Pause, 3).Command);
        Assert.NotNull(Plan(fixture with
        {
            State = running.State,
            Capabilities = [altered with { OutputEpoch = Guid.NewGuid() }],
        }, PauseCommandAction.Pause, 3).Command);
    }

    [Fact]
    public void ForeignCaptureCommandCannotTerminalizeOrReleaseAnotherCapture()
    {
        var first = Fixture(true, false);
        var second = Fixture(true, false);
        var command = Plan(first, PauseCommandAction.Pause, 1).Command!;
        var foreign = PauseAcknowledgmentPolicy.Evaluate(command, second.State, true, Acks(command, 1));
        Assert.Equal(PauseModelDisposition.RejectedNoEffect, foreign.Disposition);
        Assert.Same(second.State, foreign.State);
        Assert.Empty(foreign.CleanupOutputs);
        var firstRelease = command.Outputs.Select(output => new PauseOutputRelease(
            command.OperationId, command.Sequence, command.Identity, output.Kind, output.OutputEpoch,
            true, true, true, true)).ToArray();
        Assert.False(PauseCleanupPolicy.Evaluate(foreign, firstRelease).CleanupCompleted);

        var ownTerminal = PauseAcknowledgmentPolicy.Evaluate(command, first.State, true, []);
        Assert.Equal(PauseModelDisposition.StopAndFinalizeAllOwnedOutputs, ownTerminal.Disposition);
        Assert.True(PauseCleanupPolicy.Evaluate(ownTerminal, firstRelease).CleanupCompleted);
        Assert.False(PauseCleanupPolicy.Evaluate(ownTerminal,
            firstRelease.Select(release => release with { Identity = second.State.Identity }).ToArray()).CleanupCompleted);
    }

    [Fact]
    public void OverflowingInputClockMappingAndRotatedResumeParticipantReject()
    {
        var fixture = Fixture(false, false);
        var command = Plan(fixture, PauseCommandAction.Pause, 1).Command!;
        var paused = PauseAcknowledgmentPolicy.Evaluate(command, fixture.State, true, Acks(command, 1));
        Assert.Null(Plan(fixture with
        {
            State = paused.State,
            Capabilities = [fixture.Capabilities[0] with { OutputEpoch = Guid.NewGuid() }],
        }, PauseCommandAction.Resume, 2).Command);

        var extreme = fixture.Capabilities[0] with
        {
            Streams = fixture.Capabilities[0].Streams.Select(s => s with
            {
                InputClock = new(Guid.NewGuid(), long.MinValue, 0, 1, long.MaxValue, 1),
            }).ToArray(),
        };
        var extremeCommand = Plan(fixture with { Capabilities = [extreme] }, PauseCommandAction.Pause, 1).Command!;
        var bad = new PauseOutputAck(extremeCommand.OperationId, extremeCommand.Sequence,
            extremeCommand.Identity, extremeCommand.Action, PauseOutputKind.Screen,
            extremeCommand.Outputs[0].OutputEpoch, PauseOutputEffect.Applied, TimeSpan.FromSeconds(1),
            extremeCommand.Outputs[0].Streams.Select(s =>
                new PauseStreamEffect(s.Id, s.InputClock.TransformId, long.MaxValue)).ToArray());
        Assert.Equal(PauseModelDisposition.StopAndFinalizeAllOwnedOutputs,
            PauseAcknowledgmentPolicy.Evaluate(extremeCommand, fixture.State, true, [bad]).Disposition);
    }

    [Theory]
    [InlineData(PauseTerminalIntent.Cancel)]
    [InlineData(PauseTerminalIntent.Stop)]
    [InlineData(PauseTerminalIntent.SourceLost)]
    [InlineData(PauseTerminalIntent.DeviceLost)]
    [InlineData(PauseTerminalIntent.Shutdown)]
    public void TerminalIntentBeforeDispatchRejectsAndAfterDispatchRequiresCleanup(PauseTerminalIntent intent)
    {
        var fixture = Fixture(true, false);
        var command = Plan(fixture, PauseCommandAction.Pause, 1).Command!;
        var before = PauseAcknowledgmentPolicy.Evaluate(command, fixture.State, false, [], intent);
        Assert.Equal(PauseModelDisposition.RejectedNoEffect, before.Disposition);
        Assert.Same(fixture.State, before.State);
        var after = PauseAcknowledgmentPolicy.Evaluate(command, fixture.State, true, [], intent);
        Assert.Equal(PauseModelDisposition.StopAndFinalizeAllOwnedOutputs, after.Disposition);
        var late = PauseAcknowledgmentPolicy.Evaluate(command, after.State, true, Acks(command, 1));
        Assert.Equal(PauseModelDisposition.StopAndFinalizeAllOwnedOutputs, late.Disposition);
        Assert.Same(after.State, late.State);
    }

    [Fact]
    public void ExactReleaseAndFinalizationReceiptsAreRequiredForEveryOwnedOutput()
    {
        var fixture = Fixture(true, false);
        var command = Plan(fixture, PauseCommandAction.Pause, 1).Command!;
        var terminal = PauseAcknowledgmentPolicy.Evaluate(command, fixture.State, true, [Acks(command, 1)[0]]);
        Assert.Equal(PauseModelDisposition.StopAndFinalizeAllOwnedOutputs, terminal.Disposition);
        var releases = command.Outputs.Select(output => new PauseOutputRelease(
            command.OperationId, command.Sequence, command.Identity, output.Kind, output.OutputEpoch,
            ResourcesReleased: true, FinalizationAccounted: true, FinalizationSucceeded: true, OriginalPreserved: true)).ToArray();
        Assert.False(PauseCleanupPolicy.Evaluate(terminal, releases[..1]).CleanupCompleted);
        Assert.False(PauseCleanupPolicy.Evaluate(terminal, [releases[0], releases[1] with { FinalizationAccounted = false }]).CleanupCompleted);
        Assert.False(PauseCleanupPolicy.Evaluate(terminal, [releases[0], releases[1] with { ResourcesReleased = false }]).CleanupCompleted);
        Assert.False(PauseCleanupPolicy.Evaluate(terminal, [releases[0], releases[1] with { OutputEpoch = Guid.NewGuid() }]).CleanupCompleted);
        Assert.False(PauseCleanupPolicy.Evaluate(terminal, [releases[0], releases[0]]).CleanupCompleted);
        Assert.True(PauseCleanupPolicy.Evaluate(terminal, releases).CleanupCompleted);
        var warning = PauseCleanupPolicy.Evaluate(terminal, [releases[0], releases[1] with { FinalizationSucceeded = false }]);
        Assert.True(warning.CleanupCompleted);
        Assert.True(warning.FinalizationWarning);
        Assert.False(PauseCleanupPolicy.Evaluate(terminal, [releases[0], releases[1] with { FinalizationSucceeded = false, OriginalPreserved = false }]).CleanupCompleted);
    }

    [Fact]
    public void InputCollectionsAreFrozenAndOutputEpochRotationInvalidatesPriorAck()
    {
        var fixture = Fixture(true, true);
        var capabilities = fixture.Capabilities.ToArray();
        var command = Plan(fixture with { Capabilities = capabilities }, PauseCommandAction.Pause, 1).Command!;
        var oldAcks = Acks(command, 1);
        capabilities[0] = capabilities[0] with { OutputEpoch = Guid.NewGuid() };
        Assert.NotEqual(capabilities[0].OutputEpoch, command.Outputs[0].OutputEpoch);
        Assert.Throws<NotSupportedException>(() => ((IList<PauseOutputParticipant>)command.Outputs).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<PauseStreamAnchor>)command.Outputs[0].Streams).Clear());
        var rotated = oldAcks.ToArray();
        rotated[0] = rotated[0] with { OutputEpoch = capabilities[0].OutputEpoch };
        Assert.Equal(PauseModelDisposition.StopAndFinalizeAllOwnedOutputs,
            PauseAcknowledgmentPolicy.Evaluate(command, fixture.State, true, rotated).Disposition);
    }

    [Fact]
    public void StartupExpectationIsPartOfTheCompletePauseRequest()
    {
        var fixture = Fixture(false, false);
        var request = fixture.Plan.Request;
        var unbound = new CaptureRequest(request.RequestId, request.RequestedAt, request.Initiation,
            request.Target, request.Webcam, request.StartPolicy);
        Assert.NotNull(request.StartupExpectation);
        Assert.Null(Plan(fixture with
        {
            Snapshot = fixture.Snapshot with { Context = fixture.Context with { Request = unbound } },
        }, PauseCommandAction.Pause, 1).Command);
    }

    [Fact]
    public void FrozenStateRejectsSameNumericIdentityWithChangedStartupBinding()
    {
        var fixture = Fixture(false, false);
        var request = fixture.Plan.Request;
        var otherPlan = TutorialRecordingPolicy.Create(request.RequestId, request.RequestedAt, request.Target,
            CaptureStartupBaselineResult.Create(new AppSettings())).Plan!;
        var otherContext = fixture.Context with { Request = otherPlan.Request };
        var otherSnapshot = fixture.Snapshot with { Context = otherContext };
        Assert.Null(PauseCommandPolicy.Plan(otherPlan, otherSnapshot, fixture.State, PauseCommandAction.Pause,
            Guid.NewGuid(), 1, fixture.Capabilities).Command);
        var command = Plan(fixture, PauseCommandAction.Pause, 1).Command!;
        Assert.Same(fixture.Context, command.Context);
        Assert.Same(fixture.Context, fixture.State.Context);
        var foreignState = PauseCommandModel.CreateInitial(otherContext, fixture.State.Identity.EpochId);
        var rejected = PauseAcknowledgmentPolicy.Evaluate(command, foreignState, true, Acks(command, 1));
        Assert.Equal(PauseModelDisposition.RejectedNoEffect, rejected.Disposition);
        Assert.Same(foreignState, rejected.State);
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
    public void InvalidSuppliedMediaTimingRejectsEveryRequiredStreamBeforeEffects(int defect)
    {
        var fixture = Fixture(true, true);
        for (var outputIndex = 0; outputIndex < fixture.Capabilities.Count; outputIndex++)
        {
            var output = fixture.Capabilities[outputIndex];
            for (var streamIndex = 0; streamIndex < output.Streams.Count; streamIndex++)
            {
                var streams = output.Streams.ToArray();
                var timing = streams[streamIndex].MediaTiming;
                streams[streamIndex] = streams[streamIndex] with
                {
                    MediaTiming = defect switch
                    {
                        0 => null!,
                        1 => timing with { TimingId = Guid.Empty },
                        2 => timing with { SecondsPerDecodedUnitNumerator = 0 },
                        3 => timing with { SecondsPerDecodedUnitNumerator = -1 },
                        4 => timing with { SecondsPerDecodedUnitDenominator = 0 },
                        5 => timing with { SecondsPerDecodedUnitDenominator = -1 },
                        6 => timing with { UnitKind = (PauseDecodedUnitKind)99 },
                        _ => timing with
                        {
                            UnitKind = timing.UnitKind == PauseDecodedUnitKind.VideoFrame
                            ? PauseDecodedUnitKind.AudioSample : PauseDecodedUnitKind.VideoFrame
                        },
                    },
                };
                var capabilities = fixture.Capabilities.ToArray();
                capabilities[outputIndex] = output with { Streams = streams };
                Assert.Null(Plan(fixture with { Capabilities = capabilities }, PauseCommandAction.Pause, 1).Command);
                Assert.Equal(PauseModelPhase.Running, fixture.State.Phase);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RetainedOutputRejectsChangedMediaTimingEvenForEquivalentFractions(int defect)
    {
        var fixture = Fixture(false, false);
        var pause = Plan(fixture, PauseCommandAction.Pause, 1).Command!;
        var paused = PauseAcknowledgmentPolicy.Evaluate(pause, fixture.State, true, Acks(pause, 1));
        var resume = Plan(fixture with { State = paused.State }, PauseCommandAction.Resume, 2).Command!;
        var running = PauseAcknowledgmentPolicy.Evaluate(resume, paused.State, true, Acks(resume, 2));
        var output = fixture.Capabilities[0];
        var streams = output.Streams.ToArray();
        var timing = streams[0].MediaTiming;
        streams[0] = streams[0] with
        {
            MediaTiming = defect switch
            {
                0 => timing with { TimingId = Guid.NewGuid() },
                1 => timing with { SecondsPerDecodedUnitNumerator = 2 },
                2 => timing with { SecondsPerDecodedUnitDenominator = 30 },
                _ => timing with { SecondsPerDecodedUnitNumerator = 2, SecondsPerDecodedUnitDenominator = 120 },
            },
        };
        var changed = fixture with { Capabilities = [output with { Streams = streams }] };
        Assert.Null(Plan(changed with { State = paused.State }, PauseCommandAction.Resume, 2).Command);
        Assert.Null(Plan(changed with { State = running.State }, PauseCommandAction.Pause, 3).Command);
        Assert.Equal(timing, pause.Outputs[0].Streams[0].MediaTiming);
    }

    [Fact]
    public void SuppliedTimingUsesOneBoundedValidatedSnapshotWithoutRereadingCallerStorage()
    {
        var fixture = Fixture(false, false);
        var output = fixture.Capabilities[0];
        var callerAnchors = output.Streams.ToArray();
        var original = callerAnchors.ToArray();
        var changing = new ChangingAnchorList(callerAnchors);
        var command = Plan(fixture with { Capabilities = [output with { Streams = changing }] },
            PauseCommandAction.Pause, 1).Command!;
        Assert.NotNull(command);
        Assert.All(changing.IndexReads, reads => Assert.Equal(1, reads));
        callerAnchors[0] = callerAnchors[0] with { MediaTiming = null! };
        Assert.Equal(original, command.Outputs[0].Streams.ToArray());
        Assert.All(command.Outputs[0].Streams, anchor =>
            Assert.NotEqual(anchor.InputClock.SecondsPerInputDenominator, anchor.MediaTiming.SecondsPerDecodedUnitDenominator));
        var committed = PauseAcknowledgmentPolicy.Evaluate(command, fixture.State, true, Acks(command, 1));
        Assert.Equal(PauseModelDisposition.ModeledCommitted, committed.Disposition);
        Assert.Equal(TimeSpan.FromSeconds(1), committed.State.LastBoundary);
        Assert.False(committed.NativeAlignmentProven);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreadableSuppliedAnchorStorageRejectsBeforeEffects(bool throwOnCount)
    {
        var fixture = Fixture(false, false);
        var output = fixture.Capabilities[0];
        var unreadable = new UnreadableAnchorList(output.Streams.Count, throwOnCount);
        Assert.Null(Plan(fixture with { Capabilities = [output with { Streams = unreadable }] },
            PauseCommandAction.Pause, 1).Command);
    }

    private sealed class ChangingAnchorList(PauseStreamAnchor[] items) : IReadOnlyList<PauseStreamAnchor>
    {
        public int Count => items.Length;
        public int[] IndexReads { get; } = new int[items.Length];
        public PauseStreamAnchor this[int index] => ++IndexReads[index] == 1
            ? items[index] : items[index] with { MediaTiming = null! };
        public IEnumerator<PauseStreamAnchor> GetEnumerator() =>
            throw new InvalidOperationException("The bounded anchor snapshot must not enumerate caller storage.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class UnreadableAnchorList(int count, bool throwOnCount) : IReadOnlyList<PauseStreamAnchor>
    {
        public int Count => throwOnCount ? throw new InvalidOperationException("Unreadable count.") : count;
        public PauseStreamAnchor this[int index] => throw new InvalidOperationException("Unreadable anchor.");
        public IEnumerator<PauseStreamAnchor> GetEnumerator() => throw new InvalidOperationException("Unexpected enumeration.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static PauseCommandPlanResult Plan(FixtureState fixture, PauseCommandAction action, long sequence) =>
        PauseCommandPolicy.Plan(fixture.Plan, fixture.Snapshot, fixture.State, action, Guid.NewGuid(), sequence, fixture.Capabilities);

    private static IReadOnlyList<PauseOutputAck> Acks(PauseCommand command, int seconds) =>
        Acks(command, TimeSpan.FromSeconds(seconds));

    private static IReadOnlyList<PauseOutputAck> Acks(PauseCommand command, TimeSpan boundary) => command.Outputs.Select(output =>
        new PauseOutputAck(command.OperationId, command.Sequence, command.Identity, command.Action,
            output.Kind, output.OutputEpoch, PauseOutputEffect.Applied, boundary,
            output.Streams.Select(stream => new PauseStreamEffect(stream.Id, stream.InputClock.TransformId,
                ExactInputIndex(stream.InputClock, boundary))).ToArray())).ToArray();

    private static FixtureState Fixture(bool camera, bool separateAudio)
    {
        var settings = new AppSettings { Audio = new AudioMixConfig { SeparateAudioTracks = separateAudio } };
        WebcamCaptureSelection? cameraSelection = camera ? new("camera-id", "Camera", 640, 480, 30, "MJPEG", 1500) : null;
        var plan = TutorialRecordingPolicy.Create(Guid.NewGuid(), DateTimeOffset.UnixEpoch,
            new WindowCaptureTarget(23, new IntPtr(234), "Tutorial", "tutorial.exe", "class"),
            CaptureStartupBaselineResult.Create(settings), cameraSelection, camera).Plan!;
        var devices = camera ? new CaptureDeviceManifest([AdmittedCaptureDevice.Exact(CaptureDeviceRole.Webcam, "camera-id")]) : CaptureDeviceManifest.Empty;
        var context = new CaptureSessionContext(Guid.NewGuid(), 1, plan.Request, plan.Request.Target, plan.Request.Target) { AdmittedDevices = devices };
        var snapshot = new CaptureLifecycleSnapshot(CaptureLifecycleStatus.Active, CaptureReadiness.Available, CaptureHealth.Healthy, context, null);
        var state = PauseCommandModel.CreateInitial(context, Guid.NewGuid());
        var anchors = new List<PauseStreamAnchor> { new(new(PauseStreamKind.ScreenVideo, 0), Clock(), MediaTiming(PauseDecodedUnitKind.VideoFrame)) };
        anchors.AddRange(plan.AudioTracks.Encoders.Select(route => new PauseStreamAnchor(
            new(PauseStreamKind.ProductAudioTrack, route.OutputTrackIndex), Clock(), MediaTiming(PauseDecodedUnitKind.AudioSample))));
        var capabilities = new List<PauseOutputCapability> { new(PauseOutputKind.Screen, Guid.NewGuid(), true, true, anchors, state.Identity, null) };
        if (camera)
        {
            capabilities.Add(new(PauseOutputKind.Camera, Guid.NewGuid(), true, true,
                [new(new(PauseStreamKind.CameraVideo, 0), Clock(), MediaTiming(PauseDecodedUnitKind.VideoFrame)),
                 new(new(PauseStreamKind.CameraCompatibilityAudio, 5), Clock(), MediaTiming(PauseDecodedUnitKind.AudioSample))], state.Identity, "camera-id"));
        }
        return new(plan, context, snapshot, state, capabilities);
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

    private sealed record FixtureState(
        TutorialRecordingPlan Plan,
        CaptureSessionContext Context,
        CaptureLifecycleSnapshot Snapshot,
        PauseModelState State,
        IReadOnlyList<PauseOutputCapability> Capabilities);
}
