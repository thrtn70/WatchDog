namespace WatchDog.Core.Capture;

public enum EncoderAvailability
{
    Software,
    HardwareUnverified,
    Unsupported,
}

public sealed record EncoderOption(
    EncoderType Encoder,
    string Label,
    bool IsImplemented,
    EncoderAvailability Availability,
    string Status);

/// <summary>Source-backed factory admission, never a device or hardware capability probe.</summary>
public static class EncoderCapabilities
{
    public const string OutputBehavior =
        "Replay uses the selected encoder. Session and separate webcam files use x264. " +
        "The engine also prepares the selected encoder for session-only starts. No automatic fallback occurs.";

    public static IReadOnlyList<EncoderOption> ImplementedOptions { get; } = Array.AsReadOnly(new[]
    {
        Describe(EncoderType.NvencH264),
        Describe(EncoderType.NvencHevc),
        Describe(EncoderType.NvencAv1),
        Describe(EncoderType.X264),
    });

    public static EncoderOption Describe(EncoderType encoder) => encoder switch
    {
        EncoderType.NvencH264 => Hardware(encoder, "NVENC H.264"),
        EncoderType.NvencHevc => Hardware(encoder, "NVENC HEVC"),
        EncoderType.NvencAv1 => Hardware(encoder, "NVENC AV1"),
        EncoderType.X264 => new(encoder, "x264 (software)", true, EncoderAvailability.Software,
            "Implemented software encoder. Choose this explicitly to use CPU encoding."),
        _ => new(encoder, $"Saved {encoder} (unsupported)", false, EncoderAvailability.Unsupported,
            $"Encoder {encoder} has no implemented factory. Select x264 or an implemented NVENC option; saved settings are retained."),
    };

    public static IReadOnlyList<EncoderOption> OptionsForSavedSelection(EncoderType saved) =>
        Describe(saved).IsImplemented
            ? ImplementedOptions
            : Array.AsReadOnly(ImplementedOptions.Append(Describe(saved)).ToArray());

    public static CaptureFailure? Validate(EncoderType encoder) => Describe(encoder) is { IsImplemented: false } option
        ? new(CaptureFailureKind.Unsupported, "capture.encoder.unsupported", option.Status)
        : null;

    private static EncoderOption Hardware(EncoderType encoder, string label) => new(
        encoder, $"{label} (hardware unverified)", true, EncoderAvailability.HardwareUnverified,
        "Implemented factory; hardware availability is unverified. If startup fails, explicitly select x264 in Settings.");
}
