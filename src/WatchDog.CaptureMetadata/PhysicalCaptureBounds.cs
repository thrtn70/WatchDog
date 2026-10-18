namespace WatchDog.CaptureMetadata;

/// <summary>
/// A nonempty rectangle in physical pixels in the Windows virtual-desktop coordinate space.
/// Negative origins are valid. Endpoint calculations use 64-bit arithmetic to avoid overflow.
/// </summary>
public sealed class PhysicalCaptureBounds : IEquatable<PhysicalCaptureBounds>
{
    public PhysicalCaptureBounds(int x, int y, int width, int height)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }

    public bool Contains(PhysicalCaptureBounds selected)
    {
        ArgumentNullException.ThrowIfNull(selected);

        var left = (long)X;
        var top = (long)Y;
        var right = left + Width;
        var bottom = top + Height;
        var selectedLeft = (long)selected.X;
        var selectedTop = (long)selected.Y;
        var selectedRight = selectedLeft + selected.Width;
        var selectedBottom = selectedTop + selected.Height;
        return selectedLeft >= left && selectedTop >= top &&
            selectedRight <= right && selectedBottom <= bottom;
    }

    public bool Equals(PhysicalCaptureBounds? other) =>
        other is not null &&
        X == other.X && Y == other.Y && Width == other.Width && Height == other.Height;

    public override bool Equals(object? obj) => obj is PhysicalCaptureBounds other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y, Width, Height);

    public static bool operator ==(PhysicalCaptureBounds? left, PhysicalCaptureBounds? right) =>
        ReferenceEquals(left, right) || (left is not null && left.Equals(right));

    public static bool operator !=(PhysicalCaptureBounds? left, PhysicalCaptureBounds? right) =>
        !(left == right);

    public override string ToString() => "PhysicalCaptureBounds { redacted }";
}
