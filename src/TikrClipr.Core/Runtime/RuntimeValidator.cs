using TikrClipr.Native.Obs;

namespace TikrClipr.Core.Runtime;

/// <summary>
/// Validates all required runtime dependencies at startup.
/// Returns a list of missing components with instructions.
/// </summary>
public static class RuntimeValidator
{
    public sealed record ValidationResult(
        bool IsValid,
        IReadOnlyList<string> MissingComponents);

    public static ValidationResult Validate()
    {
        var missing = new List<string>();

        if (!FFmpegRuntime.IsAvailable())
            missing.Add(FFmpegRuntime.GetMissingComponentsMessage());

        if (!ObsRuntime.IsAvailable())
            missing.Add(ObsRuntime.GetMissingComponentsMessage());

        return new ValidationResult(missing.Count == 0, missing);
    }
}
