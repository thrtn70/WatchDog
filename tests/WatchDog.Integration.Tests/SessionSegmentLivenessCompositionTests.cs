using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WatchDog.Core.Recording;
using WatchDog.Core.Settings;

namespace WatchDog.Integration.Tests;

public sealed class SessionSegmentLivenessCompositionTests
{
    [Fact]
    public void EnabledSessionLivenessComposesRejectedRealProviderAndDisabledOwner()
    {
        using var provider = Compose(Settings(
            RecordingMode.SessionRecording,
            LivenessRequest(enabled: true)), out var services);

        var decision = provider.GetRequiredService<SessionSegmentLivenessStartupDecision>();

        Assert.Same(
            decision,
            provider.GetRequiredService<SessionSegmentLivenessStartupDecision>());
        Assert.Equal(SessionSegmentLivenessStartupState.ProviderNotAdmitted, decision.State);
        Assert.Equal(
            CaptureLivenessProviderAdmissionFailure.SynchronousWorkBoundUnavailable,
            decision.AdmissionFailure);
        Assert.Null(decision.EffectivePolicy);
        Assert.Same(
            ObsSessionRecorder.SegmentFrameCounterCapabilities,
            provider.GetRequiredService<CaptureLivenessProviderCapabilities>());
        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType == typeof(SessionSegmentLivenessAdmissionBudget));
        Assert.Null(provider.GetService<SessionSegmentLivenessAdmissionBudget>());

        var recorder = Assert.IsType<ObsSessionRecorder>(
            provider.GetRequiredService<ISessionRecorder>());
        AssertSessionSegmentLivenessOwnerDisabled(recorder);
    }

    [Fact]
    public void ExplicitlyDisabledLivenessComposesDisabledDecisionAndInertObsOwner()
    {
        using var provider = Compose(Settings(
            RecordingMode.SessionRecording,
            LivenessRequest(enabled: false)), out _);

        var decision = provider.GetRequiredService<SessionSegmentLivenessStartupDecision>();

        Assert.Equal(SessionSegmentLivenessStartupState.Disabled, decision.State);
        Assert.Equal(CaptureLivenessProviderAdmissionFailure.None, decision.AdmissionFailure);
        Assert.Null(decision.EffectivePolicy);
        var recorder = Assert.IsType<ObsSessionRecorder>(
            provider.GetRequiredService<ISessionRecorder>());
        AssertSessionSegmentLivenessOwnerDisabled(recorder);
    }

    [Fact]
    public void EnabledLivenessOutsideSessionModeComposesDisabledDecisionAndInertNextStartRecorder()
    {
        using var provider = Compose(Settings(
            RecordingMode.ReplayBufferOnly,
            LivenessRequest(enabled: true)), out _);

        var decision = provider.GetRequiredService<SessionSegmentLivenessStartupDecision>();

        Assert.Equal(SessionSegmentLivenessStartupState.SessionRecordingDisabled, decision.State);
        Assert.Equal(CaptureLivenessProviderAdmissionFailure.None, decision.AdmissionFailure);
        Assert.Null(decision.EffectivePolicy);
        var recorder = Assert.IsType<ObsSessionRecorder>(provider.GetRequiredService<ISessionRecorder>());
        Assert.False(recorder.IsRecording);
        Assert.False(recorder.HasActiveResources);
        AssertSessionSegmentLivenessOwnerDisabled(recorder);
    }

    private static ServiceProvider Compose(
        AppSettings settings,
        out ServiceCollection services)
    {
        services = new ServiceCollection();
        services.AddLogging();
        WatchDog.App.App.ConfigureServices(
            new HostBuilderContext(new Dictionary<object, object>()),
            services);
        services.AddSingleton<ISettingsService>(new StaticSettingsService(settings));
        return services.BuildServiceProvider();
    }

    private static AppSettings Settings(
        RecordingMode recordingMode,
        SessionSegmentLivenessSettings livenessRequest) =>
        new()
        {
            Recording = new SessionRecordingConfig { Mode = recordingMode },
            SessionSegmentLiveness = livenessRequest,
        };

    private static SessionSegmentLivenessSettings LivenessRequest(bool enabled) =>
        new()
        {
            Enabled = enabled,
            Policy = new SessionSegmentLivenessPolicySettings
            {
                PollIntervalMilliseconds = 250,
                StartupTimeoutMilliseconds = 5_000,
                StallTimeoutMilliseconds = 5_000,
                TelemetryTimeoutMilliseconds = 2_000,
                ProviderExecutionTimeoutMilliseconds = 2_000,
            },
        };

    private static void AssertSessionSegmentLivenessOwnerDisabled(ObsSessionRecorder recorder)
    {
        var ownerField = typeof(ObsSessionRecorder).GetField(
            "_segmentLiveness",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(ownerField);
        var owner = ownerField!.GetValue(recorder);
        Assert.NotNull(owner);

        var enabledProperty = owner!.GetType().GetProperty(
            "Enabled",
            BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(enabledProperty);
        Assert.False(Assert.IsType<bool>(enabledProperty!.GetValue(owner)));

        var policyField = owner.GetType().GetField(
            "_policy",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(policyField);
        Assert.Null(policyField!.GetValue(owner));
    }

    private sealed class StaticSettingsService(AppSettings settings) : ISettingsService
    {
        public AppSettings Load() => settings;

        public void Save(AppSettings updatedSettings) =>
            throw new InvalidOperationException("The composition fixture is read-only.");

        public event Action<AppSettings>? SettingsChanged
        {
            add { }
            remove { }
        }
    }
}
