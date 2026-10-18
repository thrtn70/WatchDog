using WatchDog.CaptureProtocol;

namespace WatchDog.CaptureIsolation;

public enum CleanupOrigin
{
    ExplicitStop,
    FailedStart,
    FailedReplacement,
    SourceSafety,
    DeviceSafety,
    LivenessSafety,
    StorageSafety,
    Shutdown,
}

public enum CleanupTerminalStatus
{
    Idle,
    Faulted,
    Shutdown,
}

public enum CleanupTerminalReadiness
{
    Available,
    Closed,
}

public enum CleanupTerminalHealth
{
    Healthy,
    Degraded,
    Failed,
}

public enum CleanupContextDisposition
{
    Release,
    Retain,
}

public enum CleanupManualReservationDisposition
{
    Release,
    Preserve,
}

public enum CleanupBarrierDisposition
{
    Open,
    KeepClosed,
    Clear,
    Preserve,
}

/// <summary>
/// Immutable policy captured when cleanup is accepted. This tranche stores and returns the
/// policy but deliberately does not integrate it with CaptureLifecycleCoordinator.
/// </summary>
public sealed record CaptureCleanupContinuation
{
    public CaptureCleanupContinuation(
        CaptureCorrelation correlation,
        CleanupOrigin origin,
        string failureCode,
        CleanupTerminalStatus terminalStatus,
        CleanupTerminalReadiness terminalReadiness,
        CleanupTerminalHealth terminalHealth,
        CleanupContextDisposition contextDisposition,
        CleanupManualReservationDisposition manualReservationDisposition,
        CleanupBarrierDisposition admissionBarrierDisposition,
        CleanupBarrierDisposition storageBarrierDisposition)
    {
        Correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        if (!IsolationValueValidation.IsCorrelationValid(correlation) ||
            !IsolationValueValidation.IsIdentifier(failureCode))
            throw new ArgumentException("A bounded failure code is required.", nameof(failureCode));
        if (!Enum.IsDefined(origin) || !Enum.IsDefined(terminalStatus) ||
            !Enum.IsDefined(terminalReadiness) || !Enum.IsDefined(terminalHealth) ||
            !Enum.IsDefined(contextDisposition) || !Enum.IsDefined(manualReservationDisposition) ||
            !Enum.IsDefined(admissionBarrierDisposition) || !Enum.IsDefined(storageBarrierDisposition))
        {
            throw new ArgumentOutOfRangeException(nameof(origin));
        }

        Origin = origin;
        FailureCode = failureCode;
        TerminalStatus = terminalStatus;
        TerminalReadiness = terminalReadiness;
        TerminalHealth = terminalHealth;
        ContextDisposition = contextDisposition;
        ManualReservationDisposition = manualReservationDisposition;
        AdmissionBarrierDisposition = admissionBarrierDisposition;
        StorageBarrierDisposition = storageBarrierDisposition;
    }

    public CaptureCorrelation Correlation { get; }
    public CleanupOrigin Origin { get; }
    public string FailureCode { get; }
    public CleanupTerminalStatus TerminalStatus { get; }
    public CleanupTerminalReadiness TerminalReadiness { get; }
    public CleanupTerminalHealth TerminalHealth { get; }
    public CleanupContextDisposition ContextDisposition { get; }
    public CleanupManualReservationDisposition ManualReservationDisposition { get; }
    public CleanupBarrierDisposition AdmissionBarrierDisposition { get; }
    public CleanupBarrierDisposition StorageBarrierDisposition { get; }
}

public sealed record CaptureCleanupCompletion
{
    public CaptureCleanupCompletion(
        CaptureCorrelation correlation,
        RetainedProcessIdentity process,
        RetainedJobIdentity job,
        CaptureCleanupContinuation continuation)
    {
        Correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        Process = process ?? throw new ArgumentNullException(nameof(process));
        Job = job ?? throw new ArgumentNullException(nameof(job));
        Continuation = continuation ?? throw new ArgumentNullException(nameof(continuation));
        process.Validate();
        job.Validate();
        if (!IsolationValueValidation.IsCorrelationValid(correlation) ||
            continuation.Correlation != correlation ||
            process.HelperInstanceId != correlation.HelperInstanceId ||
            job.HelperInstanceId != correlation.HelperInstanceId)
        {
            throw new ArgumentException("Cleanup completion identity is not exact.");
        }
    }

    public CaptureCorrelation Correlation { get; }
    public RetainedProcessIdentity Process { get; }
    public RetainedJobIdentity Job { get; }
    public CaptureCleanupContinuation Continuation { get; }
}
