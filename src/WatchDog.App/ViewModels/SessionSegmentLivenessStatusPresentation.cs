using WatchDog.Core.Recording;

namespace WatchDog.App.ViewModels;

public sealed record SessionSegmentLivenessStatusPresentation(
    string Text,
    bool IsWarning);

/// <summary>
/// Converts the immutable startup decision into bounded, privacy-safe operator copy.
/// The presentation deliberately omits provider identifiers and persisted policy values.
/// </summary>
public static class SessionSegmentLivenessStatusPresenter
{
    public static SessionSegmentLivenessStatusPresentation Present(
        SessionSegmentLivenessStartupDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        return decision.State switch
        {
            SessionSegmentLivenessStartupState.Disabled => new(
                "Startup status: Encoded-output stall monitoring is off.",
                IsWarning: false),
            SessionSegmentLivenessStartupState.SessionRecordingDisabled => new(
                "Startup status: Encoded-output stall monitoring was requested, but session recording " +
                "was off when WatchDog started. Monitoring remains inactive until restart.",
                IsWarning: false),
            SessionSegmentLivenessStartupState.InvalidPolicy => new(
                "Startup status: Encoded-output stall monitoring is inactive because its saved settings " +
                "are invalid. Recording continues without this stall check.",
                IsWarning: true),
            SessionSegmentLivenessStartupState.ProviderNotAdmitted => new(
                "Startup status: Encoded-output stall monitoring was requested, but this build cannot " +
                "safely activate it. Recording continues without this stall check.",
                IsWarning: true),
            SessionSegmentLivenessStartupState.Enabled => new(
                "Startup status: Encoded-output stall monitoring is active for session recordings.",
                IsWarning: false),
            _ => throw new ArgumentOutOfRangeException(
                nameof(decision),
                decision.State,
                "Unknown liveness startup state."),
        };
    }
}
