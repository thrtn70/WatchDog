using WatchDog.Core.Recording;
using WatchDog.Core.Settings;

namespace WatchDog.Core.Tests.Recording;

public sealed class SessionSegmentLivenessStartupGateTests
{
    [Fact]
    public void MissingRequestIsDisabled()
    {
        var decision = Evaluate(requested: null);

        Assert.False(decision.RequestedEnabled);
        Assert.Null(decision.RequestedPolicy);
        Assert.Equal(SessionSegmentLivenessStartupState.Disabled, decision.State);
        Assert.Equal(CaptureLivenessProviderAdmissionFailure.None, decision.AdmissionFailure);
        Assert.Equal("capture.liveness.session-segment.disabled", decision.ReasonCode);
        Assert.Null(decision.EffectivePolicy);
    }

    [Fact]
    public void ExplicitDisabledRequestIgnoresInvalidPolicy()
    {
        var decision = Evaluate(new SessionSegmentLivenessSettings
        {
            Enabled = false,
            Policy = new SessionSegmentLivenessPolicySettings(),
        });

        Assert.False(decision.RequestedEnabled);
        Assert.Equal(SessionSegmentLivenessStartupState.Disabled, decision.State);
        Assert.NotNull(decision.RequestedPolicy);
        Assert.Null(decision.EffectivePolicy);
    }

    [Fact]
    public void SessionRecordingDisabledRejectsRequestBeforePolicyAdmission()
    {
        var decision = Evaluate(
            EnabledRequest(),
            sessionRecordingEnabled: false);

        Assert.True(decision.RequestedEnabled);
        Assert.Equal(SessionSegmentLivenessStartupState.SessionRecordingDisabled, decision.State);
        Assert.Equal(
            "capture.liveness.session-segment.session-recording-disabled",
            decision.ReasonCode);
        Assert.Null(decision.EffectivePolicy);
    }

    [Fact]
    public void MissingPolicyIsInvalid()
    {
        var decision = Evaluate(new SessionSegmentLivenessSettings { Enabled = true });

        Assert.Equal(SessionSegmentLivenessStartupState.InvalidPolicy, decision.State);
        Assert.Equal("capture.liveness.session-segment.invalid-policy", decision.ReasonCode);
        Assert.Null(decision.EffectivePolicy);
    }

    [Fact]
    public void MissingProviderExecutionTimeoutIsInvalid()
    {
        var request = EnabledRequest();
        var decision = Evaluate(request with
        {
            Policy = request.Policy! with { ProviderExecutionTimeoutMilliseconds = null },
        });

        Assert.Equal(SessionSegmentLivenessStartupState.InvalidPolicy, decision.State);
        Assert.Null(decision.EffectivePolicy);
    }

    [Theory]
    [InlineData(0, 5_000, 5_000, 2_000, 2_000)]
    [InlineData(250, -1, 5_000, 2_000, 2_000)]
    [InlineData(250, 5_000, 0, 2_000, 2_000)]
    [InlineData(250, 5_000, 5_000, 0, 2_000)]
    [InlineData(250, 5_000, 5_000, 2_000, 0)]
    [InlineData(4294967295, 5_000, 5_000, 2_000, 2_000)]
    [InlineData(250, 5_000, 5_000, 2_000, long.MaxValue)]
    public void InvalidPolicyValuesFailClosed(
        long pollMilliseconds,
        long startupMilliseconds,
        long stallMilliseconds,
        long telemetryMilliseconds,
        long executionMilliseconds)
    {
        var decision = Evaluate(new SessionSegmentLivenessSettings
        {
            Enabled = true,
            Policy = new SessionSegmentLivenessPolicySettings
            {
                PollIntervalMilliseconds = pollMilliseconds,
                StartupTimeoutMilliseconds = startupMilliseconds,
                StallTimeoutMilliseconds = stallMilliseconds,
                TelemetryTimeoutMilliseconds = telemetryMilliseconds,
                ProviderExecutionTimeoutMilliseconds = executionMilliseconds,
            },
        });

        Assert.Equal(SessionSegmentLivenessStartupState.InvalidPolicy, decision.State);
        Assert.Null(decision.EffectivePolicy);
    }

    [Fact]
    public void CurrentObsProviderIsRejectedBeforeActivation()
    {
        var decision = Evaluate(
            EnabledRequest(),
            provider: ObsSessionRecorder.SegmentFrameCounterCapabilities,
            budget: Budget());

        Assert.Equal(SessionSegmentLivenessStartupState.ProviderNotAdmitted, decision.State);
        Assert.Equal(
            CaptureLivenessProviderAdmissionFailure.SynchronousWorkBoundUnavailable,
            decision.AdmissionFailure);
        Assert.Equal("capture.liveness.session-segment.provider-not-admitted", decision.ReasonCode);
        Assert.Equal("obs.session-segment.total-frames", decision.ProviderId);
        Assert.Null(decision.EffectivePolicy);
    }

    [Theory]
    [MemberData(nameof(RejectedProviders))]
    public void ProviderCapabilityFailuresAreDistinct(
        CaptureLivenessProviderCapabilities provider,
        CaptureLivenessProviderAdmissionFailure expectedFailure)
    {
        var decision = Evaluate(EnabledRequest(), provider: provider);

        Assert.Equal(SessionSegmentLivenessStartupState.ProviderNotAdmitted, decision.State);
        Assert.Equal(expectedFailure, decision.AdmissionFailure);
        Assert.Null(decision.EffectivePolicy);
    }

    [Fact]
    public void CompletePolicyAndAdmittedProviderEnableExactEffectivePolicy()
    {
        var request = EnabledRequest();
        var decision = Evaluate(request, budget: Budget());

        Assert.True(decision.RequestedEnabled);
        Assert.Equal(SessionSegmentLivenessStartupState.Enabled, decision.State);
        Assert.Equal(CaptureLivenessProviderAdmissionFailure.None, decision.AdmissionFailure);
        Assert.Equal("capture.liveness.session-segment.enabled", decision.ReasonCode);
        Assert.Equal("fixture.bounded", decision.ProviderId);
        Assert.NotSame(request.Policy, decision.RequestedPolicy);
        var effective = Assert.IsType<SessionSegmentLivenessPolicy>(decision.EffectivePolicy);
        Assert.Equal(TimeSpan.FromMilliseconds(250), effective.PollInterval);
        Assert.Equal(TimeSpan.FromSeconds(5), effective.TimingPolicy.StartupTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), effective.TimingPolicy.StallTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2), effective.TimingPolicy.TelemetryTimeout);
        Assert.Equal(
            TimeSpan.FromSeconds(2),
            effective.TimingPolicy.ProviderExecutionTimeout);
    }

    [Fact]
    public void MaximumSupportedPollTimerRangeIsAccepted()
    {
        var request = EnabledRequest();
        var decision = Evaluate(request with
        {
            Policy = request.Policy! with
            {
                PollIntervalMilliseconds = uint.MaxValue - 1L,
            },
        }, budget: Budget());

        Assert.Equal(SessionSegmentLivenessStartupState.Enabled, decision.State);
        Assert.Equal(
            TimeSpan.FromMilliseconds(uint.MaxValue - 1d),
            decision.EffectivePolicy!.PollInterval);
    }

    [Fact]
    public void DecisionIsAnImmutableStartupSnapshot()
    {
        var request = EnabledRequest();
        var decision = Evaluate(request, budget: Budget());
        var changedForFutureStartup = request with
        {
            Enabled = false,
            Policy = request.Policy! with { PollIntervalMilliseconds = 999 },
        };

        Assert.False(changedForFutureStartup.Enabled);
        Assert.True(decision.RequestedEnabled);
        Assert.Equal(250, decision.RequestedPolicy!.PollIntervalMilliseconds);
        Assert.Equal(TimeSpan.FromMilliseconds(250), decision.EffectivePolicy!.PollInterval);
    }

    [Fact]
    public void ThreeArgumentEvaluateFailsClosedWhenAcceptedDrainCeilingIsMissing()
    {
        var decision = SessionSegmentLivenessStartupGate.Evaluate(
            EnabledRequest(),
            sessionRecordingEnabled: true,
            AdmittedProvider());

        Assert.Equal(SessionSegmentLivenessStartupState.ProviderNotAdmitted, decision.State);
        Assert.Equal(
            CaptureLivenessProviderAdmissionFailure.AcceptedCancellationDrainCeilingUnavailable,
            decision.AdmissionFailure);
        Assert.Null(decision.EffectivePolicy);
    }

    [Theory]
    [InlineData(99, true)]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public void ExplicitAdmissionBudgetAcceptsProviderDrainAtOrBelowCeiling(
        long providerDrainMilliseconds,
        bool expectedEnabled)
    {
        var provider = new CaptureLivenessProviderCapabilities(
            "fixture.drain-boundary",
            TimeSpan.FromMilliseconds(100),
            cooperativeAsyncCancellation: true,
            TimeSpan.FromMilliseconds(providerDrainMilliseconds));

        var decision = Evaluate(
            EnabledRequest(),
            provider: provider,
            budget: Budget(100));

        if (expectedEnabled)
        {
            Assert.Equal(SessionSegmentLivenessStartupState.Enabled, decision.State);
            Assert.Equal(CaptureLivenessProviderAdmissionFailure.None, decision.AdmissionFailure);
            Assert.NotNull(decision.EffectivePolicy);
        }
        else
        {
            Assert.Equal(SessionSegmentLivenessStartupState.ProviderNotAdmitted, decision.State);
            Assert.Equal(
                CaptureLivenessProviderAdmissionFailure.CancellationDrainExceedsAcceptedCeiling,
                decision.AdmissionFailure);
            Assert.Null(decision.EffectivePolicy);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-2)]
    public void AdmissionBudgetMustBeFiniteAndPositive(long milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SessionSegmentLivenessAdmissionBudget(TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public void PersistedPolicyCannotWidenCodeOwnedCancellationDrainBudget()
    {
        var request = EnabledRequest();
        request = request with
        {
            Policy = request.Policy! with
            {
                ProviderExecutionTimeoutMilliseconds = 10_000,
            },
        };
        var provider = new CaptureLivenessProviderCapabilities(
            "fixture.exceeds-product-budget",
            TimeSpan.FromMilliseconds(100),
            cooperativeAsyncCancellation: true,
            TimeSpan.FromMilliseconds(101));

        var decision = Evaluate(
            request,
            provider: provider,
            budget: Budget(100));

        Assert.Equal(SessionSegmentLivenessStartupState.ProviderNotAdmitted, decision.State);
        Assert.Equal(
            CaptureLivenessProviderAdmissionFailure.CancellationDrainExceedsAcceptedCeiling,
            decision.AdmissionFailure);
        Assert.Null(decision.EffectivePolicy);
    }

    [Fact]
    public void CurrentProviderDescriptorDoesNotClaimUnprovenBounds()
    {
        var capabilities = ObsSessionRecorder.SegmentFrameCounterCapabilities;

        Assert.Null(capabilities.MaximumSynchronousWork);
        Assert.True(capabilities.CooperativeAsyncCancellation);
        Assert.Null(capabilities.MaximumCancellationDrain);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ProviderIdMustBeStableAndNonblank(string providerId)
    {
        Assert.Throws<ArgumentException>(() => new CaptureLivenessProviderCapabilities(
            providerId,
            TimeSpan.FromMilliseconds(1),
            cooperativeAsyncCancellation: true,
            TimeSpan.FromMilliseconds(1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CapabilityBoundsMustBePositive(long milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureLivenessProviderCapabilities(
            "fixture.invalid",
            TimeSpan.FromMilliseconds(milliseconds),
            cooperativeAsyncCancellation: true,
            TimeSpan.FromMilliseconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureLivenessProviderCapabilities(
            "fixture.invalid",
            TimeSpan.FromMilliseconds(1),
            cooperativeAsyncCancellation: true,
            TimeSpan.FromMilliseconds(milliseconds)));
    }

    public static TheoryData<CaptureLivenessProviderCapabilities,
        CaptureLivenessProviderAdmissionFailure> RejectedProviders =>
        new()
        {
            {
                new CaptureLivenessProviderCapabilities(
                    "fixture.no-sync-bound",
                    maximumSynchronousWork: null,
                    cooperativeAsyncCancellation: true,
                    TimeSpan.FromMilliseconds(100)),
                CaptureLivenessProviderAdmissionFailure.SynchronousWorkBoundUnavailable
            },
            {
                new CaptureLivenessProviderCapabilities(
                    "fixture.sync-too-slow",
                    TimeSpan.FromMilliseconds(2_001),
                    cooperativeAsyncCancellation: true,
                    TimeSpan.FromMilliseconds(100)),
                CaptureLivenessProviderAdmissionFailure.SynchronousWorkExceedsExecutionTimeout
            },
            {
                new CaptureLivenessProviderCapabilities(
                    "fixture.no-cancellation",
                    TimeSpan.FromMilliseconds(100),
                    cooperativeAsyncCancellation: false,
                    TimeSpan.FromMilliseconds(100)),
                CaptureLivenessProviderAdmissionFailure.CooperativeCancellationUnavailable
            },
            {
                new CaptureLivenessProviderCapabilities(
                    "fixture.no-drain-bound",
                    TimeSpan.FromMilliseconds(100),
                    cooperativeAsyncCancellation: true,
                    maximumCancellationDrain: null),
                CaptureLivenessProviderAdmissionFailure.CancellationDrainBoundUnavailable
            },
        };

    private static SessionSegmentLivenessStartupDecision Evaluate(
        SessionSegmentLivenessSettings? requested,
        bool sessionRecordingEnabled = true,
        CaptureLivenessProviderCapabilities? provider = null,
        SessionSegmentLivenessAdmissionBudget? budget = null)
    {
        var selectedProvider = provider ?? AdmittedProvider();
        return budget is null
            ? SessionSegmentLivenessStartupGate.Evaluate(
                requested,
                sessionRecordingEnabled,
                selectedProvider)
            : SessionSegmentLivenessStartupGate.Evaluate(
                requested,
                sessionRecordingEnabled,
                selectedProvider,
                budget);
    }

    private static SessionSegmentLivenessSettings EnabledRequest() =>
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

    private static CaptureLivenessProviderCapabilities AdmittedProvider() =>
        new(
            "fixture.bounded",
            TimeSpan.FromMilliseconds(100),
            cooperativeAsyncCancellation: true,
            TimeSpan.FromMilliseconds(100));

    private static SessionSegmentLivenessAdmissionBudget Budget(long milliseconds = 100) =>
        new(TimeSpan.FromMilliseconds(milliseconds));
}
