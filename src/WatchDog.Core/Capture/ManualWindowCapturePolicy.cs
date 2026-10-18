namespace WatchDog.Core.Capture;

/// <summary>
/// Picker-local history. The lifecycle coordinator remains the only resource and reservation
/// owner; retaining a context here never grants a Stop without a fresh exact observation.
/// </summary>
public sealed record ManualWindowCaptureState(
    long Revision,
    CaptureRequest? PendingRequest,
    CaptureSessionContext? OwnedContext,
    CaptureFailure? LastFailure,
    bool IsPending)
{
    public static ManualWindowCaptureState Initial { get; } = new(0, null, null, null, false);
}

public sealed record ManualWindowCaptureProjection(
    bool IsManualCaptureActive,
    bool CanRequestStop,
    bool CanSelectWindow,
    string ActiveCaptureTitle,
    string OwnedCaptureTitle,
    string StatusText);

/// <summary>Pure observation and stale-result reduction for the existing manual window picker.</summary>
public static class ManualWindowCapturePolicy
{
    public static ManualWindowCaptureState BeginSelection(ManualWindowCaptureState state, CaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(request);
        if (!ValidWindowRequest(request) || state.Revision == long.MaxValue)
            throw new ArgumentException("An exact manual window request and available revision are required.", nameof(request));
        return state with
        {
            Revision = state.Revision + 1,
            PendingRequest = request,
            LastFailure = null,
            IsPending = true,
        };
    }

    public static ManualWindowCaptureState CompleteStart(ManualWindowCaptureState state, long revision,
        CaptureRequest request, CaptureTransitionResult result, CaptureLifecycleSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(current);
        if (!state.IsPending || state.Revision != revision || state.PendingRequest != request ||
            result.Operation is not (CaptureOperationKind.Start or CaptureOperationKind.Switch))
            return state;

        var candidate = result.After.Context;
        var owned = candidate is not null && MatchesSubmittedRequest(request, candidate) &&
            MatchesOwnedContext(candidate, current.Context)
            ? candidate
            : MatchesOwnedContext(state.OwnedContext, current.Context) ? state.OwnedContext : null;
        return state with
        {
            OwnedContext = owned,
            PendingRequest = null,
            LastFailure = result.Failure,
            IsPending = false,
        };
    }

    public static ManualWindowCaptureState BeginStop(ManualWindowCaptureState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.IsPending || state.OwnedContext is null || state.Revision == long.MaxValue)
            throw new InvalidOperationException("An owned idle picker command is required.");
        return state with { Revision = state.Revision + 1, IsPending = true };
    }

    public static ManualWindowCaptureState CompleteStop(ManualWindowCaptureState state, long revision,
        CaptureSessionContext expected, CaptureTransitionResult result, CaptureLifecycleSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(current);
        if (!state.IsPending || state.Revision != revision ||
            !MatchesOwnedContext(state.OwnedContext, expected) || result.Operation != CaptureOperationKind.Stop)
            return state;

        var owned = MatchesOwnedContext(result.Before.Context, expected)
            ? MatchesOwnedContext(result.After.Context, expected) &&
              MatchesOwnedContext(current.Context, expected) ? expected : null
            : MatchesOwnedContext(current.Context, expected) ? expected : null;
        return state with
        {
            OwnedContext = owned,
            LastFailure = result.Failure,
            IsPending = false,
        };
    }

    public static ManualWindowCaptureProjection Project(ManualWindowCaptureState state,
        CaptureLifecycleSnapshot snapshot, bool admissionClosed)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(snapshot);
        var own = MatchesOwnedContext(state.OwnedContext, snapshot.Context);
        var exactActive = own && snapshot.Status == CaptureLifecycleStatus.Active &&
            snapshot.Readiness == CaptureReadiness.Available && snapshot.Context!.ActualTarget == snapshot.Context.Request.Target &&
            snapshot.Context.RequestedTarget == snapshot.Context.Request.Target;
        var retainedCleanup = own && snapshot.Status == CaptureLifecycleStatus.Faulted &&
            snapshot.Readiness == CaptureReadiness.Available;
        var closed = admissionClosed || snapshot.Readiness == CaptureReadiness.Closed ||
            snapshot.Status == CaptureLifecycleStatus.Shutdown;
        var canStop = !closed && !state.IsPending && (exactActive || retainedCleanup);
        var canSelect = !closed && !state.IsPending && snapshot.Readiness == CaptureReadiness.Available &&
            snapshot.Status is CaptureLifecycleStatus.Idle or CaptureLifecycleStatus.Active or CaptureLifecycleStatus.Faulted &&
            !(snapshot.Status == CaptureLifecycleStatus.Faulted && snapshot.Context is not null);
        var selectedTitle = own && state.OwnedContext?.Request.Target is WindowCaptureTarget window
            ? window.WindowTitle ?? string.Empty : string.Empty;
        // Storage closure is global even after the selected context was released. Show its
        // safe warning without treating the snapshot as picker ownership or granting Stop.
        var failure = closed && snapshot.LastFailure?.Kind == CaptureFailureKind.Storage
            ? snapshot.LastFailure
            : own ? snapshot.LastFailure ?? state.LastFailure : state.LastFailure;
        var safeFailure = failure is null ? null : CaptureFailurePresentation.SafeText(failure);
        var warning = safeFailure is null ? string.Empty : $" Capture warning: {safeFailure}";
        var status = closed
            ? $"Capture admission is closed; Stop is unavailable.{warning}"
            : state.IsPending
                ? "Capture command pending; ownership has not been confirmed."
                : exactActive
                    ? $"Recording selected window.{warning}"
                    : retainedCleanup
                        ? $"Selected window cleanup is required: {safeFailure ?? "retry Stop"}"
                        : own && snapshot.Status is CaptureLifecycleStatus.Starting or CaptureLifecycleStatus.Saving or CaptureLifecycleStatus.Stopping
                            ? $"Selected window capture is {snapshot.Status.ToString().ToLowerInvariant()}; Stop is unavailable until it settles.{warning}"
                        : snapshot.Context is not null
                            ? $"Another capture owns the lifecycle; this picker cannot stop it.{warning}"
                            : failure is not null
                                ? $"Capture warning: {safeFailure} Stop authority is unavailable."
                                : "No selected window is confirmed active; manual reservation ownership is unknown.";
        return new(exactActive && !state.IsPending && !closed, canStop, canSelect,
            exactActive && !state.IsPending && !closed ? selectedTitle : string.Empty,
            own ? selectedTitle : string.Empty, status);
    }

    internal static bool MatchesOwnedContext(CaptureSessionContext? expected, CaptureSessionContext? current) =>
        expected is not null && current is not null &&
        expected.SessionId != Guid.Empty && expected.Generation > 0 &&
        expected.Request.RequestId != Guid.Empty && expected.Request.Initiation == CaptureInitiation.Manual &&
        expected.SessionId == current.SessionId && expected.Generation == current.Generation &&
        expected.Request == current.Request && expected.RequestedTarget == current.RequestedTarget &&
        expected.ActualTarget == current.ActualTarget;

    private static bool MatchesSubmittedRequest(CaptureRequest request, CaptureSessionContext context) =>
        context.SessionId != Guid.Empty && context.Generation > 0 &&
        context.Request.RequestId == request.RequestId && context.Request == request &&
        context.RequestedTarget == request.Target;

    private static bool ValidWindowRequest(CaptureRequest request) =>
        request.RequestId != Guid.Empty && request.Initiation == CaptureInitiation.Manual &&
        request.Target is WindowCaptureTarget window && window.ProcessId > 0 &&
        window.WindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(window.WindowTitle) &&
        !string.IsNullOrWhiteSpace(window.ExecutableName);
}
