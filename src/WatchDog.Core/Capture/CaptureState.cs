namespace WatchDog.Core.Capture;

public enum CaptureState
{
    Idle,
    Initializing,
    Buffering,
    Saving,
    Stopping
}

public static class CaptureStateTransitions
{
    private static readonly Dictionary<(CaptureState From, CaptureState To), bool> ValidTransitions = new()
    {
        [(CaptureState.Idle, CaptureState.Initializing)] = true,
        [(CaptureState.Initializing, CaptureState.Buffering)] = true,
        [(CaptureState.Initializing, CaptureState.Idle)] = true,        // init failed
        [(CaptureState.Buffering, CaptureState.Saving)] = true,
        [(CaptureState.Buffering, CaptureState.Stopping)] = true,
        [(CaptureState.Saving, CaptureState.Buffering)] = true,
        [(CaptureState.Saving, CaptureState.Stopping)] = true,
        [(CaptureState.Stopping, CaptureState.Idle)] = true,
    };

    public static bool IsValid(CaptureState from, CaptureState to)
        => ValidTransitions.ContainsKey((from, to));
}
