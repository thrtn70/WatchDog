namespace WatchDog.CaptureMetadata;

public enum CaptureDeviceRole
{
    DesktopAudio,
    Microphone,
    Webcam,
}

public enum CaptureDeviceSelectionMode
{
    Exact,
    SystemDefault,
}

public sealed class CaptureDeviceSelection
{
    private CaptureDeviceSelection(
        CaptureDeviceRole role,
        CaptureDeviceSelectionMode mode,
        string? opaqueDeviceId)
    {
        Role = role;
        Mode = mode;
        OpaqueDeviceId = opaqueDeviceId;
    }

    public CaptureDeviceRole Role { get; }
    public CaptureDeviceSelectionMode Mode { get; }
    public string? OpaqueDeviceId { get; }

    public static CaptureDeviceSelection Exact(CaptureDeviceRole role, string opaqueDeviceId)
    {
        ValidateRole(role);
        if (string.IsNullOrWhiteSpace(opaqueDeviceId))
            throw new ArgumentException("A nonblank opaque device ID is required.", nameof(opaqueDeviceId));
        return new(role, CaptureDeviceSelectionMode.Exact, opaqueDeviceId);
    }

    public static CaptureDeviceSelection SystemDefault(CaptureDeviceRole role)
    {
        ValidateRole(role);
        if (role == CaptureDeviceRole.Webcam)
            throw new ArgumentException("System-default selection is valid only for audio roles.", nameof(role));
        return new(role, CaptureDeviceSelectionMode.SystemDefault, null);
    }

    public override string ToString() =>
        $"CaptureDeviceSelection {{ Role = {Role}, Mode = {Mode}, Identity = [redacted] }}";

    private static void ValidateRole(CaptureDeviceRole role)
    {
        if (!Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(role));
    }
}

public sealed class CaptureDeviceCatalogEntry
{
    public CaptureDeviceCatalogEntry(
        CaptureDeviceRole role,
        string opaqueDeviceId,
        bool isSystemDefault)
    {
        if (!Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(role));
        if (string.IsNullOrWhiteSpace(opaqueDeviceId))
            throw new ArgumentException("A nonblank opaque device ID is required.", nameof(opaqueDeviceId));
        if (role == CaptureDeviceRole.Webcam && isSystemDefault)
            throw new ArgumentException("A webcam cannot be marked as the system default.", nameof(isSystemDefault));
        Role = role;
        OpaqueDeviceId = opaqueDeviceId;
        IsSystemDefault = isSystemDefault;
    }

    public CaptureDeviceRole Role { get; }
    public string OpaqueDeviceId { get; }
    public bool IsSystemDefault { get; }

    public override string ToString() =>
        $"CaptureDeviceCatalogEntry {{ Role = {Role}, Identity = [redacted] }}";
}

public sealed class CaptureDeviceCatalogSnapshot
{
    private readonly IReadOnlyList<CaptureDeviceCatalogEntry> _entries;

    public CaptureDeviceCatalogSnapshot(IEnumerable<CaptureDeviceCatalogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var frozen = entries.ToArray();
        var idsByRole = new Dictionary<CaptureDeviceRole, HashSet<string>>();
        var defaultRoles = new HashSet<CaptureDeviceRole>();
        foreach (var entry in frozen)
        {
            if (entry is null)
                throw new ArgumentException("Catalog entries cannot contain null.", nameof(entries));
            if (!idsByRole.TryGetValue(entry.Role, out var ids))
            {
                ids = new HashSet<string>(StringComparer.Ordinal);
                idsByRole.Add(entry.Role, ids);
            }
            if (!ids.Add(entry.OpaqueDeviceId))
                throw new ArgumentException("Catalog device identities must be unique per role.", nameof(entries));
            if (entry.IsSystemDefault && !defaultRoles.Add(entry.Role))
                throw new ArgumentException("An audio role can have at most one system default.", nameof(entries));
        }

        _entries = Array.AsReadOnly(frozen);
    }

    public IReadOnlyList<CaptureDeviceCatalogEntry> Entries => _entries;

    public override string ToString() => "CaptureDeviceCatalogSnapshot { Identities = [redacted] }";
}

public enum CaptureDeviceCatalogObservationKind
{
    Available,
    Unavailable,
    Invalid,
}

public sealed class CaptureDeviceCatalogObservation
{
    private CaptureDeviceCatalogObservation(
        CaptureDeviceCatalogObservationKind kind,
        CaptureDeviceCatalogSnapshot? snapshot)
    {
        Kind = kind;
        Snapshot = snapshot;
    }

    public CaptureDeviceCatalogObservationKind Kind { get; }
    public CaptureDeviceCatalogSnapshot? Snapshot { get; }

    public static CaptureDeviceCatalogObservation Available(CaptureDeviceCatalogSnapshot snapshot) =>
        new(CaptureDeviceCatalogObservationKind.Available, snapshot ?? throw new ArgumentNullException(nameof(snapshot)));

    public static CaptureDeviceCatalogObservation Unavailable() =>
        new(CaptureDeviceCatalogObservationKind.Unavailable, null);

    public static CaptureDeviceCatalogObservation Invalid() =>
        new(CaptureDeviceCatalogObservationKind.Invalid, null);

    public override string ToString() => $"CaptureDeviceCatalogObservation {{ Kind = {Kind} }}";
}
