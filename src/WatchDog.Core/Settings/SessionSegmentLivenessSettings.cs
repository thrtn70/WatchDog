namespace WatchDog.Core.Settings;

/// <summary>
/// Persists an operator's restart-only request for session-segment encoded-output monitoring.
/// Missing, null, and disabled settings are all safely off. The request is not sufficient for
/// activation; startup admission must also accept the provider's non-user-configurable capabilities.
/// </summary>
public sealed record SessionSegmentLivenessSettings
{
    public bool Enabled { get; init; }
    public SessionSegmentLivenessPolicySettings? Policy { get; init; }
}

/// <summary>
/// Stores explicit policy values without constructing runtime timeout objects during JSON loading.
/// The startup gate validates the complete request before creating an effective policy.
/// </summary>
public sealed record SessionSegmentLivenessPolicySettings
{
    public long PollIntervalMilliseconds { get; init; }
    public long StartupTimeoutMilliseconds { get; init; }
    public long StallTimeoutMilliseconds { get; init; }
    public long TelemetryTimeoutMilliseconds { get; init; }
    public long? ProviderExecutionTimeoutMilliseconds { get; init; }
}
