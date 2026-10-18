namespace WatchDog.CaptureMetadata;

public enum ResolvedCaptureTargetKind
{
    Window,
    Display,
    Region,
}

public abstract class ResolvedCaptureTarget : IEquatable<ResolvedCaptureTarget>
{
    private protected ResolvedCaptureTarget(ResolvedCaptureTargetKind kind)
    {
        Kind = kind;
    }

    public ResolvedCaptureTargetKind Kind { get; }

    /// <summary>
    /// Compares the complete physical target identity without normalization or substitution.
    /// </summary>
    public abstract bool Equals(ResolvedCaptureTarget? other);

    public sealed override bool Equals(object? obj) =>
        obj is ResolvedCaptureTarget other && Equals(other);

    public abstract override int GetHashCode();

    public override string ToString() => $"ResolvedCaptureTarget {{ Kind = {Kind}, Identity = [redacted] }}";
}

public sealed class ResolvedWindowTarget : ResolvedCaptureTarget
{
    public ResolvedWindowTarget(int processId, long opaqueWindowHandleValue)
        : base(ResolvedCaptureTargetKind.Window)
    {
        if (processId <= 0)
            throw new ArgumentOutOfRangeException(nameof(processId));
        if (opaqueWindowHandleValue == 0)
            throw new ArgumentOutOfRangeException(nameof(opaqueWindowHandleValue));
        ProcessId = processId;
        OpaqueWindowHandleValue = opaqueWindowHandleValue;
    }

    public int ProcessId { get; }
    public long OpaqueWindowHandleValue { get; }

    public override bool Equals(ResolvedCaptureTarget? other) =>
        other is ResolvedWindowTarget window &&
        ProcessId == window.ProcessId &&
        OpaqueWindowHandleValue == window.OpaqueWindowHandleValue;

    public override int GetHashCode() =>
        HashCode.Combine(Kind, ProcessId, OpaqueWindowHandleValue);

    public override string ToString() => "ResolvedWindowTarget { Identity = [redacted] }";
}

public sealed class ResolvedDisplayTarget : ResolvedCaptureTarget
{
    public ResolvedDisplayTarget(string stableDisplayId, PhysicalCaptureBounds frozenPhysicalBounds)
        : base(ResolvedCaptureTargetKind.Display)
    {
        if (string.IsNullOrWhiteSpace(stableDisplayId))
            throw new ArgumentException("A nonblank stable display ID is required.", nameof(stableDisplayId));
        ArgumentNullException.ThrowIfNull(frozenPhysicalBounds);
        StableDisplayId = stableDisplayId;
        FrozenPhysicalBounds = frozenPhysicalBounds;
    }

    public string StableDisplayId { get; }
    public PhysicalCaptureBounds FrozenPhysicalBounds { get; }

    public override bool Equals(ResolvedCaptureTarget? other) =>
        other is ResolvedDisplayTarget display &&
        string.Equals(StableDisplayId, display.StableDisplayId, StringComparison.Ordinal) &&
        FrozenPhysicalBounds.Equals(display.FrozenPhysicalBounds);

    public override int GetHashCode() =>
        HashCode.Combine(
            Kind,
            StringComparer.Ordinal.GetHashCode(StableDisplayId),
            FrozenPhysicalBounds);

    public override string ToString() => "ResolvedDisplayTarget { Identity = [redacted] }";
}

public sealed class ResolvedRegionTarget : ResolvedCaptureTarget
{
    public ResolvedRegionTarget(
        string owningDisplayId,
        PhysicalCaptureBounds selectedPhysicalBounds,
        PhysicalCaptureBounds frozenOwningDisplayBounds)
        : base(ResolvedCaptureTargetKind.Region)
    {
        if (string.IsNullOrWhiteSpace(owningDisplayId))
            throw new ArgumentException("A nonblank owning display ID is required.", nameof(owningDisplayId));
        ArgumentNullException.ThrowIfNull(selectedPhysicalBounds);
        ArgumentNullException.ThrowIfNull(frozenOwningDisplayBounds);
        if (!frozenOwningDisplayBounds.Contains(selectedPhysicalBounds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(selectedPhysicalBounds),
                "The selected physical bounds must be wholly contained by the frozen owning display.");
        }
        OwningDisplayId = owningDisplayId;
        SelectedPhysicalBounds = selectedPhysicalBounds;
        FrozenOwningDisplayBounds = frozenOwningDisplayBounds;
    }

    public string OwningDisplayId { get; }
    public PhysicalCaptureBounds SelectedPhysicalBounds { get; }
    public PhysicalCaptureBounds FrozenOwningDisplayBounds { get; }

    public override bool Equals(ResolvedCaptureTarget? other) =>
        other is ResolvedRegionTarget region &&
        string.Equals(OwningDisplayId, region.OwningDisplayId, StringComparison.Ordinal) &&
        SelectedPhysicalBounds.Equals(region.SelectedPhysicalBounds) &&
        FrozenOwningDisplayBounds.Equals(region.FrozenOwningDisplayBounds);

    public override int GetHashCode() =>
        HashCode.Combine(
            Kind,
            StringComparer.Ordinal.GetHashCode(OwningDisplayId),
            SelectedPhysicalBounds,
            FrozenOwningDisplayBounds);

    public override string ToString() => "ResolvedRegionTarget { Identity = [redacted] }";
}
