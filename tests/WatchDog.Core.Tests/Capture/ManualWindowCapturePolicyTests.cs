using WatchDog.Core.Capture;

namespace WatchDog.Core.Tests.Capture;

public sealed class ManualWindowCapturePolicyTests
{
    [Fact]
    public void ExactStartOwnsOnlyItsFullWindowAndConfirmedContext()
    {
        var request = Request(10, 0x1010, "First", "first.exe", "FirstClass");
        var state = ManualWindowCapturePolicy.BeginSelection(ManualWindowCaptureState.Initial, request);
        Assert.False(ManualWindowCapturePolicy.Project(state, CaptureLifecycleSnapshot.Initial with
        {
            Status = CaptureLifecycleStatus.Starting,
            Readiness = CaptureReadiness.Busy,
        }, false).IsManualCaptureActive);
        var context = Context(request);
        var after = Active(context);
        state = ManualWindowCapturePolicy.CompleteStart(state, state.Revision, request,
            Result(CaptureOperationKind.Start, CaptureCommandDisposition.Completed,
                CaptureLifecycleSnapshot.Initial, after), after);
        var owned = ManualWindowCapturePolicy.Project(state, after, false);
        Assert.True(owned.IsManualCaptureActive);
        Assert.True(owned.CanRequestStop);
        Assert.Equal("First", owned.ActiveCaptureTitle);
        Assert.Same(context, state.OwnedContext);

        foreach (var changed in new CaptureSessionContext[]
        {
            context with { SessionId = Guid.NewGuid() },
            context with { Generation = context.Generation + 1 },
            context with { Request = Request(10, 0x1010, "First", "first.exe", "FirstClass") },
            context with { RequestedTarget = ((WindowCaptureTarget)request.Target) with { WindowClass = "OtherClass" } },
            context with { ActualTarget = ((WindowCaptureTarget)request.Target) with { WindowTitle = "Other" } },
            context with { ActualTarget = ((WindowCaptureTarget)request.Target) with { ExecutableName = "other.exe" } },
            context with { ActualTarget = ((WindowCaptureTarget)request.Target) with { ProcessId = 11 } },
            context with { ActualTarget = ((WindowCaptureTarget)request.Target) with { WindowHandle = new IntPtr(0x2020) } },
        })
        {
            var projection = ManualWindowCapturePolicy.Project(state, Active(changed), false);
            Assert.False(projection.IsManualCaptureActive);
            Assert.False(projection.CanRequestStop);
            Assert.Equal(string.Empty, projection.ActiveCaptureTitle);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedStartOffersStopOnlyForExactRetainedCleanup(bool retained)
    {
        var request = Request(10, 0x1010, "Selected", "selected.exe", "SelectedClass");
        var state = ManualWindowCapturePolicy.BeginSelection(ManualWindowCaptureState.Initial, request);
        var context = Context(request) with { ActualTarget = new DisplayCaptureTarget("unapproved-display") };
        var failure = new CaptureFailure(CaptureFailureKind.Backend, "fixture.start", "start failed");
        var after = new CaptureLifecycleSnapshot(CaptureLifecycleStatus.Faulted,
            CaptureReadiness.Available, CaptureHealth.Failed, retained ? context : null, failure);
        state = ManualWindowCapturePolicy.CompleteStart(state, state.Revision, request,
            Result(CaptureOperationKind.Start, CaptureCommandDisposition.Failed,
                CaptureLifecycleSnapshot.Initial, after, failure), after);
        var projection = ManualWindowCapturePolicy.Project(state, after, false);
        Assert.False(projection.IsManualCaptureActive);
        Assert.Equal(string.Empty, projection.ActiveCaptureTitle);
        Assert.Equal(retained, projection.CanRequestStop);
        Assert.Contains(retained ? "cleanup" : "warning", projection.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("start failed", projection.StatusText);
        Assert.DoesNotContain("Recording selected", projection.StatusText);
    }

    [Fact]
    public void StaleStartCompletionCannotReplaceNewerSelection()
    {
        var first = Request(10, 0x1010, "First", "first.exe", "FirstClass");
        var second = Request(20, 0x2020, "Second", "second.exe", "SecondClass");
        var one = ManualWindowCapturePolicy.BeginSelection(ManualWindowCaptureState.Initial, first);
        var two = ManualWindowCapturePolicy.BeginSelection(one, second);
        var firstAfter = Active(Context(first));
        Assert.Same(two, ManualWindowCapturePolicy.CompleteStart(two, one.Revision, first,
            Result(CaptureOperationKind.Start, CaptureCommandDisposition.Completed,
                CaptureLifecycleSnapshot.Initial, firstAfter), firstAfter));
        var secondAfter = Active(Context(second));
        var accepted = ManualWindowCapturePolicy.CompleteStart(two, two.Revision, second,
            Result(CaptureOperationKind.Start, CaptureCommandDisposition.Completed,
                firstAfter, secondAfter), secondAfter);
        Assert.Equal("Second", ManualWindowCapturePolicy.Project(accepted, secondAfter, false).ActiveCaptureTitle);
        Assert.False(ManualWindowCapturePolicy.Project(accepted, firstAfter, false).CanRequestStop);
    }

    [Fact]
    public void FailedReplacementPreservesPriorOwnedContext()
    {
        var first = Request(10, 0x1010, "First", "first.exe", "FirstClass");
        var firstAfter = Active(Context(first));
        var state = ManualWindowCapturePolicy.BeginSelection(ManualWindowCaptureState.Initial, first);
        state = ManualWindowCapturePolicy.CompleteStart(state, state.Revision, first,
            Result(CaptureOperationKind.Start, CaptureCommandDisposition.Completed,
                CaptureLifecycleSnapshot.Initial, firstAfter), firstAfter);
        var second = Request(20, 0x2020, "Second", "second.exe", "SecondClass");
        state = ManualWindowCapturePolicy.BeginSelection(state, second);
        var failure = new CaptureFailure(CaptureFailureKind.Backend, "fixture.replace", "replacement cleanup failed");
        var retained = firstAfter with { Status = CaptureLifecycleStatus.Faulted, Health = CaptureHealth.Failed, LastFailure = failure };
        state = ManualWindowCapturePolicy.CompleteStart(state, state.Revision, second,
            Result(CaptureOperationKind.Start, CaptureCommandDisposition.Failed, firstAfter, retained, failure), retained);
        Assert.Equal(firstAfter.Context, state.OwnedContext);
        var projection = ManualWindowCapturePolicy.Project(state, retained, false);
        Assert.False(projection.IsManualCaptureActive);
        Assert.True(projection.CanRequestStop);
        Assert.Contains("cleanup", projection.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("replacement cleanup failed", projection.StatusText);
    }

    [Fact]
    public void FailedPreflightReplacementKeepsActiveOwnerAndShowsWarning()
    {
        var first = Request(10, 0x1010, "First", "first.exe", "FirstClass");
        var active = Active(Context(first));
        var state = ManualWindowCapturePolicy.BeginSelection(ManualWindowCaptureState.Initial, first);
        state = ManualWindowCapturePolicy.CompleteStart(state, state.Revision, first,
            Result(CaptureOperationKind.Start, CaptureCommandDisposition.Completed,
                CaptureLifecycleSnapshot.Initial, active), active);
        var second = Request(20, 0x2020, "Second", "second.exe", "SecondClass");
        state = ManualWindowCapturePolicy.BeginSelection(state, second);
        var failure = new CaptureFailure(CaptureFailureKind.InvalidRequest, "fixture.replace", "replacement rejected");
        state = ManualWindowCapturePolicy.CompleteStart(state, state.Revision, second,
            Result(CaptureOperationKind.Start, CaptureCommandDisposition.Rejected, active, active, failure), active);
        var projection = ManualWindowCapturePolicy.Project(state, active, false);
        Assert.True(projection.IsManualCaptureActive);
        Assert.True(projection.CanRequestStop);
        Assert.Equal("First", projection.ActiveCaptureTitle);
        Assert.Contains("request", projection.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("replacement rejected", projection.StatusText);
    }

    [Theory]
    [InlineData(CaptureLifecycleStatus.Starting)]
    [InlineData(CaptureLifecycleStatus.Saving)]
    [InlineData(CaptureLifecycleStatus.Stopping)]
    public void OwnedBusySnapshotIsNeverDescribedAsAnotherCapture(CaptureLifecycleStatus busyStatus)
    {
        var request = Request(10, 0x1010, "Selected", "selected.exe", "SelectedClass");
        var active = Active(Context(request));
        var state = ManualWindowCapturePolicy.BeginSelection(ManualWindowCaptureState.Initial, request);
        state = ManualWindowCapturePolicy.CompleteStart(state, state.Revision, request,
            Result(CaptureOperationKind.Start, CaptureCommandDisposition.Completed,
                CaptureLifecycleSnapshot.Initial, active), active);
        var projection = ManualWindowCapturePolicy.Project(state,
            active with { Status = busyStatus, Readiness = CaptureReadiness.Busy }, false);
        Assert.False(projection.CanRequestStop);
        Assert.Contains(busyStatus.ToString().ToLowerInvariant(), projection.StatusText);
        Assert.DoesNotContain("Another capture", projection.StatusText);
    }

    [Fact]
    public void StopRetainsFailedCleanupButReleaseWarningAndSuccessClearStopAuthority()
    {
        var request = Request(10, 0x1010, "Selected", "selected.exe", "SelectedClass");
        var active = Active(Context(request));
        var state = ManualWindowCapturePolicy.BeginSelection(ManualWindowCaptureState.Initial, request);
        state = ManualWindowCapturePolicy.CompleteStart(state, state.Revision, request,
            Result(CaptureOperationKind.Start, CaptureCommandDisposition.Completed,
                CaptureLifecycleSnapshot.Initial, active), active);
        var failure = new CaptureFailure(CaptureFailureKind.Backend, "fixture.stop", "finalization failed");
        var retained = active with { Status = CaptureLifecycleStatus.Faulted, Health = CaptureHealth.Failed, LastFailure = failure };
        var stopping = ManualWindowCapturePolicy.BeginStop(state);
        var failed = ManualWindowCapturePolicy.CompleteStop(stopping, stopping.Revision, active.Context!,
            Result(CaptureOperationKind.Stop, CaptureCommandDisposition.Failed, active, retained, failure), retained);
        Assert.True(ManualWindowCapturePolicy.Project(failed, retained, false).CanRequestStop);
        var released = retained with { Context = null, Health = CaptureHealth.Degraded };
        var warningAttempt = ManualWindowCapturePolicy.BeginStop(failed);
        var warning = ManualWindowCapturePolicy.CompleteStop(warningAttempt, warningAttempt.Revision, active.Context!,
            Result(CaptureOperationKind.Stop, CaptureCommandDisposition.Failed, retained, released, failure), released);
        var warningView = ManualWindowCapturePolicy.Project(warning, released, false);
        Assert.False(warningView.CanRequestStop);
        Assert.Contains("warning", warningView.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("finalization failed", warningView.StatusText);
        var unrelated = Active(Context(Request(20, 0x2020, "Other", "other.exe", "OtherClass")));
        var unrelatedView = ManualWindowCapturePolicy.Project(warning, unrelated, false);
        Assert.False(unrelatedView.CanRequestStop);
        Assert.Contains("Another capture", unrelatedView.StatusText);
        Assert.Contains("warning", unrelatedView.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("finalization failed", unrelatedView.StatusText);

        var successAttempt = ManualWindowCapturePolicy.BeginStop(failed);
        var success = ManualWindowCapturePolicy.CompleteStop(successAttempt, successAttempt.Revision, active.Context!,
            Result(CaptureOperationKind.Stop, CaptureCommandDisposition.Completed, retained, CaptureLifecycleSnapshot.Initial),
            CaptureLifecycleSnapshot.Initial);
        Assert.False(ManualWindowCapturePolicy.Project(success, CaptureLifecycleSnapshot.Initial, false).CanRequestStop);
        Assert.Null(success.OwnedContext);
    }

    [Fact]
    public void OldStopCompletionCannotClearNewerSelection()
    {
        var first = Request(10, 0x1010, "First", "first.exe", "FirstClass");
        var active = Active(Context(first));
        var state = ManualWindowCapturePolicy.BeginSelection(ManualWindowCaptureState.Initial, first);
        state = ManualWindowCapturePolicy.CompleteStart(state, state.Revision, first,
            Result(CaptureOperationKind.Start, CaptureCommandDisposition.Completed,
                CaptureLifecycleSnapshot.Initial, active), active);
        var stopping = ManualWindowCapturePolicy.BeginStop(state);
        var second = Request(20, 0x2020, "Second", "second.exe", "SecondClass");
        var newer = ManualWindowCapturePolicy.BeginSelection(stopping, second);
        var historical = Result(CaptureOperationKind.Stop, CaptureCommandDisposition.Completed,
            active, CaptureLifecycleSnapshot.Initial);
        Assert.Same(newer, ManualWindowCapturePolicy.CompleteStop(newer, stopping.Revision,
            active.Context!, historical, CaptureLifecycleSnapshot.Initial));
    }

    [Fact]
    public void IdleSourceLossBusyUnrelatedAndClosedNeverOfferStop()
    {
        var request = Request(10, 0x1010, "Selected", "selected.exe", "SelectedClass");
        var active = Active(Context(request));
        var state = ManualWindowCapturePolicy.BeginSelection(ManualWindowCaptureState.Initial, request);
        state = ManualWindowCapturePolicy.CompleteStart(state, state.Revision, request,
            Result(CaptureOperationKind.Start, CaptureCommandDisposition.Completed,
                CaptureLifecycleSnapshot.Initial, active), active);
        foreach (var snapshot in new[]
        {
            CaptureLifecycleSnapshot.Initial,
            active with { Status = CaptureLifecycleStatus.Stopping, Readiness = CaptureReadiness.Busy },
            active with { Status = CaptureLifecycleStatus.Saving, Readiness = CaptureReadiness.Busy },
            Active(Context(Request(10, 0x1010, "Selected", "selected.exe", "SelectedClass"))),
            active with { Status = CaptureLifecycleStatus.Shutdown, Readiness = CaptureReadiness.Closed },
        })
        {
            Assert.False(ManualWindowCapturePolicy.Project(state, snapshot, false).CanRequestStop);
        }
        Assert.False(ManualWindowCapturePolicy.Project(state, active, true).CanRequestStop);
        Assert.Contains("unknown", ManualWindowCapturePolicy.Project(state, CaptureLifecycleSnapshot.Initial, false).StatusText);
    }

    [Fact]
    public void ManualPickerNeverDisplaysFailurePathOrOpaqueDeviceId()
    {
        const string path = @"C:\Users\private\Videos\take.mp4";
        const string deviceId = "opaque-device-identifier";
        var failure = new CaptureFailure(CaptureFailureKind.Storage,
            $"private-code-{deviceId}", $"Failed to write {path} on {deviceId}");
        var request = Request(10, 0x1010, "Selected", "selected.exe", "SelectedClass");
        var active = Active(Context(request));
        var state = ManualWindowCapturePolicy.BeginSelection(ManualWindowCaptureState.Initial, request);
        state = ManualWindowCapturePolicy.CompleteStart(state, state.Revision, request,
            Result(CaptureOperationKind.Start, CaptureCommandDisposition.Completed,
                CaptureLifecycleSnapshot.Initial, active), active);

        var activeWarning = ManualWindowCapturePolicy.Project(state,
            active with { Health = CaptureHealth.Degraded, LastFailure = failure }, false);
        var retained = ManualWindowCapturePolicy.Project(state,
            active with
            {
                Status = CaptureLifecycleStatus.Faulted,
                Health = CaptureHealth.Failed,
                LastFailure = failure
            }, false);
        var orphanState = ManualWindowCaptureState.Initial with { LastFailure = failure };
        var orphan = ManualWindowCapturePolicy.Project(orphanState, CaptureLifecycleSnapshot.Initial, false);
        // Storage pressure can close admission after the selected context was released;
        // no picker command owns the resulting global failure.
        var closed = ManualWindowCapturePolicy.Project(state,
            CaptureLifecycleSnapshot.Initial with
            {
                Readiness = CaptureReadiness.Closed,
                Health = CaptureHealth.Degraded,
                LastFailure = failure,
            }, false);

        Assert.True(activeWarning.CanRequestStop);
        Assert.True(retained.CanRequestStop);
        Assert.Contains("cleanup", retained.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.False(orphan.CanRequestStop);
        Assert.False(closed.CanRequestStop);
        Assert.Equal(string.Empty, closed.ActiveCaptureTitle);
        Assert.Contains("closed", closed.StatusText, StringComparison.OrdinalIgnoreCase);
        foreach (var text in new[] { activeWarning.StatusText, retained.StatusText, orphan.StatusText, closed.StatusText })
        {
            Assert.Contains("storage", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(path, text, StringComparison.Ordinal);
            Assert.DoesNotContain(deviceId, text, StringComparison.Ordinal);
            Assert.DoesNotContain("private-code", text, StringComparison.Ordinal);
        }
        Assert.Contains(path, failure.Message); // The typed failure is unchanged for bounded diagnostics.
    }

    [Fact]
    public void ComposedWebcamUncertaintyUsesFixedTextWithoutChangingStopAuthority()
    {
        const string privateDetail = @"C:\Users\private\Devices\camera-id";
        var request = Request(10, 0x1010, "Selected", "selected.exe", "SelectedClass");
        var context = Context(request);
        var primary = new CaptureFailure(
            CaptureFailureKind.SourceLost,
            "capture.source.private",
            privateDetail,
            IsRetryable: true);
        var uncertainty = new CaptureFailure(
            CaptureFailureKind.Backend,
            CaptureFailure.WebcamViewReleaseUncertainCode,
            privateDetail);
        var failure = primary.WithCleanupFailure(uncertainty);
        var snapshot = Active(context) with
        {
            Status = CaptureLifecycleStatus.Faulted,
            Health = CaptureHealth.Failed,
            LastFailure = failure,
        };
        var state = ManualWindowCaptureState.Initial with
        {
            OwnedContext = context,
            LastFailure = failure,
        };

        var projection = ManualWindowCapturePolicy.Project(state, snapshot, false);

        Assert.True(projection.CanRequestStop);
        Assert.Contains("may still be active", projection.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot resolve", projection.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(privateDetail, projection.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void KnownLocalSelectionFailureKeepsSafeSettingsGuidance()
    {
        const string privateDetail = @"C:\Users\private\Devices\camera-id";
        var failure = new CaptureFailure(CaptureFailureKind.Unexpected,
            "capture.manual.selection-invalid", privateDetail);
        var state = ManualWindowCaptureState.Initial with { LastFailure = failure };

        var status = ManualWindowCapturePolicy.Project(state, CaptureLifecycleSnapshot.Initial, false).StatusText;

        Assert.Contains("capture settings are invalid", status, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(privateDetail, status, StringComparison.Ordinal);
        Assert.False(ManualWindowCapturePolicy.Project(state, CaptureLifecycleSnapshot.Initial, false).CanRequestStop);
    }

    private static CaptureRequest Request(int pid, long handle, string title, string executable, string windowClass) =>
        new(Guid.NewGuid(), DateTimeOffset.UnixEpoch, CaptureInitiation.Manual,
            new WindowCaptureTarget(pid, new IntPtr(handle), title, executable, windowClass));

    private static CaptureSessionContext Context(CaptureRequest request) =>
        new(Guid.NewGuid(), 1, request, request.Target, request.Target);

    private static CaptureLifecycleSnapshot Active(CaptureSessionContext context) =>
        new(CaptureLifecycleStatus.Active, CaptureReadiness.Available, CaptureHealth.Healthy, context, null);

    private static CaptureTransitionResult Result(CaptureOperationKind kind, CaptureCommandDisposition disposition,
        CaptureLifecycleSnapshot before, CaptureLifecycleSnapshot after, CaptureFailure? failure = null) =>
        new(Guid.NewGuid(), kind, disposition, before, after, failure);
}
