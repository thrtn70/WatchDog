using WatchDog.Core.Capture;
using WatchDog.Core.Settings;

namespace WatchDog.Core.Recording;

public enum SessionSegmentLivenessStartupState
{
    Disabled,
    SessionRecordingDisabled,
    InvalidPolicy,
    ProviderNotAdmitted,
    Enabled,
}

public enum CaptureLivenessProviderAdmissionFailure
{
    None,
    SynchronousWorkBoundUnavailable,
    SynchronousWorkExceedsExecutionTimeout,
    CooperativeCancellationUnavailable,
    CancellationDrainBoundUnavailable,
    AcceptedCancellationDrainCeilingUnavailable,
    CancellationDrainExceedsAcceptedCeiling,
}

/// <summary>
/// Describes properties of one concrete telemetry adapter. These values come from adapter code,
/// never user-controlled settings. Null bounds mean unknown and are not production-admissible.
/// </summary>
public sealed record CaptureLivenessProviderCapabilities
{
    public CaptureLivenessProviderCapabilities(
        string providerId,
        TimeSpan? maximumSynchronousWork,
        bool cooperativeAsyncCancellation,
        TimeSpan? maximumCancellationDrain)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            throw new ArgumentException("A stable provider ID is required.", nameof(providerId));
        ValidateOptionalBound(maximumSynchronousWork, nameof(maximumSynchronousWork));
        ValidateOptionalBound(maximumCancellationDrain, nameof(maximumCancellationDrain));

        ProviderId = providerId;
        MaximumSynchronousWork = maximumSynchronousWork;
        CooperativeAsyncCancellation = cooperativeAsyncCancellation;
        MaximumCancellationDrain = maximumCancellationDrain;
    }

    public string ProviderId { get; }
    public TimeSpan? MaximumSynchronousWork { get; }
    public bool CooperativeAsyncCancellation { get; }
    public TimeSpan? MaximumCancellationDrain { get; }

    private static void ValidateOptionalBound(TimeSpan? value, string parameterName)
    {
        if (value is { } bound && (bound <= TimeSpan.Zero || bound == Timeout.InfiniteTimeSpan))
            throw new ArgumentOutOfRangeException(parameterName, "Capability bounds must be finite and positive.");
    }
}

/// <summary>
/// Code-owned product admission budget for provider cancellation drain. Persisted settings express
/// requested behavior only and cannot create or widen this ceiling.
/// </summary>
public sealed record SessionSegmentLivenessAdmissionBudget
{
    public SessionSegmentLivenessAdmissionBudget(TimeSpan maximumAcceptedCancellationDrain)
    {
        if (maximumAcceptedCancellationDrain <= TimeSpan.Zero ||
            maximumAcceptedCancellationDrain == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumAcceptedCancellationDrain),
                "The accepted cancellation drain ceiling must be finite and positive.");
        }

        MaximumAcceptedCancellationDrain = maximumAcceptedCancellationDrain;
    }

    public TimeSpan MaximumAcceptedCancellationDrain { get; }
}

/// <summary>
/// Immutable process-start decision. Persisted settings express intent only; the effective policy
/// is present only when the complete request and the adapter's code-owned capabilities are admitted.
/// </summary>
public sealed class SessionSegmentLivenessStartupDecision
{
    internal SessionSegmentLivenessStartupDecision(
        bool requestedEnabled,
        SessionSegmentLivenessPolicySettings? requestedPolicy,
        SessionSegmentLivenessStartupState state,
        CaptureLivenessProviderAdmissionFailure admissionFailure,
        string reasonCode,
        string providerId,
        SessionSegmentLivenessPolicy? effectivePolicy)
    {
        RequestedEnabled = requestedEnabled;
        RequestedPolicy = requestedPolicy is null ? null : requestedPolicy with { };
        State = state;
        AdmissionFailure = admissionFailure;
        ReasonCode = reasonCode;
        ProviderId = providerId;
        EffectivePolicy = effectivePolicy;
    }

    public bool RequestedEnabled { get; }
    public SessionSegmentLivenessPolicySettings? RequestedPolicy { get; }
    public SessionSegmentLivenessStartupState State { get; }
    public CaptureLivenessProviderAdmissionFailure AdmissionFailure { get; }
    public string ReasonCode { get; }
    public string ProviderId { get; }
    public SessionSegmentLivenessPolicy? EffectivePolicy { get; }
}

public static class SessionSegmentLivenessStartupGate
{
    /// <summary>
    /// Evaluates without a product cancellation-drain ceiling. An otherwise admissible enabled
    /// request fails closed until code-owned wiring supplies an explicit budget.
    /// </summary>
    public static SessionSegmentLivenessStartupDecision Evaluate(
        SessionSegmentLivenessSettings? requested,
        bool sessionRecordingEnabled,
        CaptureLivenessProviderCapabilities provider) =>
        EvaluateCore(
            requested,
            sessionRecordingEnabled,
            provider,
            admissionBudget: null);

    /// <summary>
    /// Evaluates against an explicit code-owned cancellation-drain budget.
    /// </summary>
    public static SessionSegmentLivenessStartupDecision Evaluate(
        SessionSegmentLivenessSettings? requested,
        bool sessionRecordingEnabled,
        CaptureLivenessProviderCapabilities provider,
        SessionSegmentLivenessAdmissionBudget admissionBudget)
    {
        ArgumentNullException.ThrowIfNull(admissionBudget);
        return EvaluateCore(requested, sessionRecordingEnabled, provider, admissionBudget);
    }

    private static SessionSegmentLivenessStartupDecision EvaluateCore(
        SessionSegmentLivenessSettings? requested,
        bool sessionRecordingEnabled,
        CaptureLivenessProviderCapabilities provider,
        SessionSegmentLivenessAdmissionBudget? admissionBudget)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var requestedEnabled = requested?.Enabled == true;
        var requestedPolicy = requested?.Policy;

        if (!requestedEnabled)
        {
            return Decision(
                requestedEnabled,
                requestedPolicy,
                SessionSegmentLivenessStartupState.Disabled,
                "capture.liveness.session-segment.disabled");
        }

        if (!sessionRecordingEnabled)
        {
            return Decision(
                requestedEnabled,
                requestedPolicy,
                SessionSegmentLivenessStartupState.SessionRecordingDisabled,
                "capture.liveness.session-segment.session-recording-disabled");
        }

        if (!TryCreatePolicy(requestedPolicy, out var policy))
        {
            return Decision(
                requestedEnabled,
                requestedPolicy,
                SessionSegmentLivenessStartupState.InvalidPolicy,
                "capture.liveness.session-segment.invalid-policy");
        }

        var admissionFailure = EvaluateProvider(provider, policy!, admissionBudget);
        if (admissionFailure != CaptureLivenessProviderAdmissionFailure.None)
        {
            return new SessionSegmentLivenessStartupDecision(
                requestedEnabled,
                requestedPolicy,
                SessionSegmentLivenessStartupState.ProviderNotAdmitted,
                admissionFailure,
                "capture.liveness.session-segment.provider-not-admitted",
                provider.ProviderId,
                effectivePolicy: null);
        }

        return new SessionSegmentLivenessStartupDecision(
            requestedEnabled,
            requestedPolicy,
            SessionSegmentLivenessStartupState.Enabled,
            CaptureLivenessProviderAdmissionFailure.None,
            "capture.liveness.session-segment.enabled",
            provider.ProviderId,
            policy);

        SessionSegmentLivenessStartupDecision Decision(
            bool enabled,
            SessionSegmentLivenessPolicySettings? settings,
            SessionSegmentLivenessStartupState state,
            string reasonCode) =>
            new(
                enabled,
                settings,
                state,
                CaptureLivenessProviderAdmissionFailure.None,
                reasonCode,
                provider.ProviderId,
                effectivePolicy: null);
    }

    private static CaptureLivenessProviderAdmissionFailure EvaluateProvider(
        CaptureLivenessProviderCapabilities provider,
        SessionSegmentLivenessPolicy policy,
        SessionSegmentLivenessAdmissionBudget? admissionBudget)
    {
        if (provider.MaximumSynchronousWork is not { } synchronousBound)
            return CaptureLivenessProviderAdmissionFailure.SynchronousWorkBoundUnavailable;
        if (policy.TimingPolicy.ProviderExecutionTimeout is not { } executionTimeout)
            return CaptureLivenessProviderAdmissionFailure.SynchronousWorkExceedsExecutionTimeout;
        if (synchronousBound > executionTimeout)
            return CaptureLivenessProviderAdmissionFailure.SynchronousWorkExceedsExecutionTimeout;
        if (!provider.CooperativeAsyncCancellation)
            return CaptureLivenessProviderAdmissionFailure.CooperativeCancellationUnavailable;
        if (provider.MaximumCancellationDrain is not { } cancellationDrain)
            return CaptureLivenessProviderAdmissionFailure.CancellationDrainBoundUnavailable;
        if (admissionBudget is null)
            return CaptureLivenessProviderAdmissionFailure.AcceptedCancellationDrainCeilingUnavailable;
        if (cancellationDrain > admissionBudget.MaximumAcceptedCancellationDrain)
            return CaptureLivenessProviderAdmissionFailure.CancellationDrainExceedsAcceptedCeiling;
        return CaptureLivenessProviderAdmissionFailure.None;
    }

    private static bool TryCreatePolicy(
        SessionSegmentLivenessPolicySettings? settings,
        out SessionSegmentLivenessPolicy? policy)
    {
        policy = null;
        if (settings?.ProviderExecutionTimeoutMilliseconds is not { } executionMilliseconds)
            return false;

        try
        {
            var pollInterval = PositiveMilliseconds(settings.PollIntervalMilliseconds);
            if (pollInterval.TotalMilliseconds > uint.MaxValue - 1d)
                return false;
            var timingPolicy = new CaptureLivenessTimingPolicy(
                PositiveMilliseconds(settings.StartupTimeoutMilliseconds),
                PositiveMilliseconds(settings.StallTimeoutMilliseconds),
                PositiveMilliseconds(settings.TelemetryTimeoutMilliseconds),
                PositiveMilliseconds(executionMilliseconds));
            policy = new SessionSegmentLivenessPolicy(pollInterval, timingPolicy);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static TimeSpan PositiveMilliseconds(long milliseconds)
    {
        if (milliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(milliseconds));
        return TimeSpan.FromMilliseconds(milliseconds);
    }
}
