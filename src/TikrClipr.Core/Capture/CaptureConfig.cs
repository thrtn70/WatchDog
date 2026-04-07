namespace TikrClipr.Core.Capture;

public sealed record CaptureConfig
{
    public uint OutputWidth { get; init; } = 1920;
    public uint OutputHeight { get; init; } = 1080;
    public uint Fps { get; init; } = 60;
    public EncoderType Encoder { get; init; } = EncoderType.NvencH264;
    public RateControlType RateControl { get; init; } = RateControlType.CQP;
    public int Quality { get; init; } = 20;          // CQP level
    public int Bitrate { get; init; } = 20_000;      // kbps for VBR/CBR
    public int MaxBitrate { get; init; } = 40_000;   // kbps for VBR
    public int AudioBitrate { get; init; } = 192;    // kbps
    public string Preset { get; init; } = "p5";       // NVENC preset (p1=fastest, p7=slowest)
    public string Profile { get; init; } = "high";
}

public enum EncoderType
{
    NvencH264,
    NvencHevc,
    NvencAv1,
    AmfH264,
    AmfHevc,
    X264
}

public enum RateControlType
{
    CBR,
    VBR,
    CQP,
    CRF
}
