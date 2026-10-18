using WatchDog.App.ViewModels;
using WatchDog.Core.Capture;
using WatchDog.Core.Runtime;

namespace WatchDog.Integration.Tests;

public sealed class CaptureStatusPresentationTests
{
    private const string UncertaintyText =
        "Webcam cleanup could not be confirmed. The camera may still be active, and recording and normal Exit remain blocked. " +
        "Repeating Stop or Exit cannot resolve this retained state. No supported in-app recovery is available.";

    [Fact]
    public void DegradedFinalizationFailure_IsProjectedForEveryStatusSurface()
    {
        var snapshot = new CaptureLifecycleSnapshot(
            CaptureLifecycleStatus.Faulted,
            CaptureReadiness.Available,
            CaptureHealth.Degraded,
            null,
            new CaptureFailure(
                CaptureFailureKind.Backend,
                "capture.backend.stop-failed",
                "Finalized media was missing."));

        var warning = Assert.IsType<CaptureStatusWarningPresentation>(
            CaptureStatusPresentation.Warning(snapshot));

        Assert.Contains("warning", warning.MainWindowText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("warning", warning.TrayText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WARN", warning.OverlayLine);
        Assert.DoesNotContain("Finalized media was missing.", warning.MainWindowText);
        Assert.DoesNotContain("Finalized media was missing.", warning.TrayText);
        Assert.DoesNotContain("Finalized media was missing.", warning.OverlayLine);
        Assert.Equal("Recording warning", warning.OverlayLabel);
    }

    [Theory]
    [InlineData(CaptureFailureKind.Storage, "storage")]
    [InlineData(CaptureFailureKind.SourceLost, "source")]
    [InlineData(CaptureFailureKind.Backend, "recording")]
    [InlineData((CaptureFailureKind)999, "recording")]
    public void SensitiveFailureDetailsNeverReachMainTrayOrOverlay(CaptureFailureKind kind, string category)
    {
        const string path = @"C:\Users\private\Videos\take.mp4";
        const string deviceId = "opaque-device-identifier";
        CaptureLifecycleSnapshot Snapshot(string detail) => new(CaptureLifecycleStatus.Faulted,
            CaptureReadiness.Available, CaptureHealth.Failed, null,
            new CaptureFailure(kind, $"private-code-{deviceId}", detail));

        var first = Assert.IsType<CaptureStatusWarningPresentation>(
            CaptureStatusPresentation.Warning(Snapshot($"{path} {deviceId}")));
        var second = Assert.IsType<CaptureStatusWarningPresentation>(
            CaptureStatusPresentation.Warning(Snapshot("other-private-detail")));

        Assert.Equal(first, second); // Unrecognized diagnostic details cannot affect public wording.
        foreach (var text in new[] { first.MainWindowText, first.TrayText, first.OverlayLine })
        {
            Assert.Contains(category, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(path, text, StringComparison.Ordinal);
            Assert.DoesNotContain(deviceId, text, StringComparison.Ordinal);
            Assert.DoesNotContain("private-code", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ComposedWebcamUncertaintyOverridesPrimaryCategoryOnEveryStatusSurface()
    {
        const string privateDetail = @"C:\Users\private\Devices\camera-id";
        var failure = new CaptureFailure(
                CaptureFailureKind.SourceLost,
                "capture.source.private",
                privateDetail,
                IsRetryable: true)
            .WithCleanupFailure(new CaptureFailure(
                CaptureFailureKind.Backend,
                CaptureFailure.WebcamViewReleaseUncertainCode,
                privateDetail));
        var snapshot = new CaptureLifecycleSnapshot(
            CaptureLifecycleStatus.Faulted,
            CaptureReadiness.Available,
            CaptureHealth.Failed,
            null,
            failure);

        var warning = Assert.IsType<CaptureStatusWarningPresentation>(
            CaptureStatusPresentation.Warning(snapshot));

        foreach (var text in new[] { warning.MainWindowText, warning.TrayText, warning.OverlayLine })
        {
            Assert.Contains("may still be active", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("cannot resolve", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(privateDetail, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SimilarCodesAndRetryAdviceCannotImpersonateWebcamUncertainty()
    {
        var similarCode = new CaptureFailure(
            CaptureFailureKind.Backend,
            CaptureFailure.WebcamViewReleaseUncertainCode + ".similar",
            "private",
            IsRetryable: false);
        var wrongKind = new CaptureFailure(
            CaptureFailureKind.Unexpected,
            CaptureFailure.WebcamViewReleaseUncertainCode,
            "private",
            IsRetryable: false);

        Assert.False(similarCode.HasUncertainWebcamViewRelease);
        Assert.False(wrongKind.HasUncertainWebcamViewRelease);
        Assert.DoesNotContain("may still be active", CaptureFailurePresentation.SafeText(similarCode));
        Assert.DoesNotContain("may still be active", CaptureFailurePresentation.SafeText(wrongKind));
    }

    [Fact]
    public void ShutdownPresentationUsesDiagnosticOnlyForTheApplicableStage()
    {
        const string privateDetail = @"C:\Users\private\Videos\take.mp4";
        var uncertainty = new CaptureFailure(
            CaptureFailureKind.Backend,
            CaptureFailure.WebcamViewReleaseUncertainCode,
            privateDetail);
        var captureFailure = new ApplicationShutdownDrainResult(
            false,
            ShutdownDrainFailureStage.Capture,
            privateDetail,
            new InvalidOperationException(privateDetail),
            CaptureDiagnostic: uncertainty);
        var hostFailure = captureFailure with
        {
            FailedStage = ShutdownDrainFailureStage.HostStop,
        };

        Assert.Equal(UncertaintyText, CaptureFailurePresentation.SafeShutdownFailureText(captureFailure));
        Assert.Equal(
            "WatchDog could not stop its background services. Exit did not complete.",
            CaptureFailurePresentation.SafeShutdownFailureText(hostFailure));
        Assert.DoesNotContain(privateDetail, CaptureFailurePresentation.SafeShutdownFailureText(captureFailure));
        Assert.DoesNotContain("camera", CaptureFailurePresentation.SafeShutdownFailureText(hostFailure),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShutdownWarningAndSessionEndingTextAreFixedAndPayloadIndependent()
    {
        const string privateDetail = @"C:\Users\private\Videos\take.mp4";
        var diagnostic = new CaptureFailure(CaptureFailureKind.Storage, "private-code", privateDetail);
        var first = new ApplicationShutdownDrainResult(
            true,
            WarningMessage: privateDetail,
            CaptureDiagnostic: diagnostic);
        var second = first with
        {
            WarningMessage = "other private detail",
            CaptureDiagnostic = diagnostic with { Message = "different private detail" },
        };

        Assert.Equal(
            "Some recording output could not be verified. Check the recording files.",
            CaptureFailurePresentation.SafeShutdownWarningText(first));
        Assert.Equal(
            CaptureFailurePresentation.SafeShutdownWarningText(first),
            CaptureFailurePresentation.SafeShutdownWarningText(second));
        Assert.Equal(
            "WatchDog canceled this sign-out request while checking recording cleanup. " +
            "Exit remains blocked if cleanup cannot be confirmed.",
            CaptureFailurePresentation.SafeSessionEndingText(isSignOut: true));
        Assert.Equal(
            "WatchDog canceled this Windows shutdown request while checking recording cleanup. " +
            "Exit remains blocked if cleanup cannot be confirmed.",
            CaptureFailurePresentation.SafeSessionEndingText(isSignOut: false));
    }

    [Theory]
    [InlineData(CaptureHealth.Healthy, true)]
    [InlineData(CaptureHealth.Degraded, false)]
    [InlineData(CaptureHealth.Failed, false)]
    public void Warning_RequiresBothUnhealthyStateAndFailure(
        CaptureHealth health,
        bool includeFailure)
    {
        var snapshot = new CaptureLifecycleSnapshot(
            CaptureLifecycleStatus.Idle,
            CaptureReadiness.Available,
            health,
            null,
            includeFailure
                ? new CaptureFailure(CaptureFailureKind.Backend, "failure", "failure")
                : null);

        Assert.Null(CaptureStatusPresentation.Warning(snapshot));
    }
}
