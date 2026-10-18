using WatchDog.App.ViewModels;
using WatchDog.Core.Capture;
using WatchDog.Core.Recording;
using WatchDog.Core.Settings;

namespace WatchDog.Integration.Tests;

public sealed class SessionSegmentLivenessStatusPresentationTests
{
    [Fact]
    public void DisabledRequest_IsPresentedAsOffWithoutWarning()
    {
        var presentation = Present(
            requested: null,
            sessionRecordingEnabled: true,
            Provider(unknownBounds: true));

        Assert.Equal(
            "Startup status: Encoded-output stall monitoring is off.",
            presentation.Text);
        Assert.False(presentation.IsWarning);
    }

    [Fact]
    public void RequestOutsideSessionMode_IsPresentedAsRestartOnlyAndInactive()
    {
        var presentation = Present(
            Request(),
            sessionRecordingEnabled: false,
            Provider(unknownBounds: true));

        Assert.Equal(
            "Startup status: Encoded-output stall monitoring was requested, but session recording " +
            "was off when WatchDog started. Monitoring remains inactive until restart.",
            presentation.Text);
        Assert.False(presentation.IsWarning);
    }

    [Fact]
    public void InvalidPolicy_IsPresentedAsInactiveWhileRecordingContinues()
    {
        var request = Request() with
        {
            Policy = Request().Policy! with { PollIntervalMilliseconds = 0 },
        };

        var presentation = Present(
            request,
            sessionRecordingEnabled: true,
            Provider(unknownBounds: true));

        Assert.Equal(
            "Startup status: Encoded-output stall monitoring is inactive because its saved settings " +
            "are invalid. Recording continues without this stall check.",
            presentation.Text);
        Assert.True(presentation.IsWarning);
    }

    [Fact]
    public void RejectedProvider_IsPresentedAsSafelyInactiveWithoutInternalDetails()
    {
        var presentation = Present(
            Request(),
            sessionRecordingEnabled: true,
            Provider(unknownBounds: true));

        Assert.Equal(
            "Startup status: Encoded-output stall monitoring was requested, but this build cannot " +
            "safely activate it. Recording continues without this stall check.",
            presentation.Text);
        Assert.True(presentation.IsWarning);
        Assert.DoesNotContain("test.provider", presentation.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(
            nameof(CaptureLivenessProviderAdmissionFailure.SynchronousWorkBoundUnavailable),
            presentation.Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AdmittedProvider_IsPresentedAsActive()
    {
        var provider = Provider(unknownBounds: false);
        var decision = SessionSegmentLivenessStartupGate.Evaluate(
            Request(),
            sessionRecordingEnabled: true,
            provider,
            new SessionSegmentLivenessAdmissionBudget(TimeSpan.FromMilliseconds(500)));

        var presentation = SessionSegmentLivenessStatusPresenter.Present(decision);

        Assert.Equal(
            "Startup status: Encoded-output stall monitoring is active for session recordings.",
            presentation.Text);
        Assert.False(presentation.IsWarning);
    }

    private static SessionSegmentLivenessStatusPresentation Present(
        SessionSegmentLivenessSettings? requested,
        bool sessionRecordingEnabled,
        CaptureLivenessProviderCapabilities provider) =>
        SessionSegmentLivenessStatusPresenter.Present(
            SessionSegmentLivenessStartupGate.Evaluate(
                requested,
                sessionRecordingEnabled,
                provider));

    private static SessionSegmentLivenessSettings Request() =>
        new()
        {
            Enabled = true,
            Policy = new SessionSegmentLivenessPolicySettings
            {
                PollIntervalMilliseconds = 250,
                StartupTimeoutMilliseconds = 5_000,
                StallTimeoutMilliseconds = 5_000,
                TelemetryTimeoutMilliseconds = 2_000,
                ProviderExecutionTimeoutMilliseconds = 2_000,
            },
        };

    private static CaptureLivenessProviderCapabilities Provider(bool unknownBounds) =>
        new(
            "test.provider",
            maximumSynchronousWork: unknownBounds ? null : TimeSpan.FromMilliseconds(100),
            cooperativeAsyncCancellation: !unknownBounds,
            maximumCancellationDrain: unknownBounds ? null : TimeSpan.FromMilliseconds(250));
}
