using System.Text;
using System.Text.Json;

namespace WatchDog.CaptureIsolation.Windows.Qualification;

internal sealed record FixtureB1LauncherCommand(
    Guid RunNonce,
    string Executable,
    string RepoHead,
    string DirtySummaryHash,
    string SourceHash,
    string BinaryHash,
    string QualificationAssemblyHash,
    string CaptureIsolationWindowsHash,
    string CaptureIsolationHash,
    string CaptureProtocolHash);

internal sealed record FixtureB1RunnerCommand(
    Guid RunNonce, Guid LaunchId, string ControlEndpoint);

internal enum FixtureB1StatusOutcome { Passed, Rejected, Failed }

internal sealed record FixtureB1StatusRecord(
    Guid RunNonce,
    Guid? LaunchId,
    FixtureHandoffProcessIdentity Launcher,
    FixtureB1StatusOutcome Outcome,
    QualificationExitCode ExitCode,
    bool CleanupConfirmed);

/// <summary>
/// Pure, unregistered B1 argument and terminal-record syntax. A parsed record is not
/// process, stream, cleanup, artifact, or native qualification evidence.
/// </summary>
internal static class FixtureB1Command
{
    internal const string StatusScope = "b1-handshake-cleanup";
    internal const int MaximumStatusBytes = 1024;
    private const int MaximumArgumentCodeUnits = 1024;
    private const int MaximumArgumentPayloadCodeUnits = 8192;
    private const string LauncherMode = "artifact-launcher";
    private const string RunnerMode = "artifact-runner";
    private const string EndpointPrefix = "WDB1-";

    private static readonly string[] LauncherKeys =
    [
        "--opt-in", "--mode", "--nonce", "--executable", "--repo-head",
        "--dirty-summary-hash", "--source-hash", "--binary-hash",
        "--qualification-assembly-hash", "--capture-isolation-windows-hash",
        "--capture-isolation-hash", "--capture-protocol-hash",
    ];

    private static readonly string[] RunnerKeys =
    ["--opt-in", "--mode", "--nonce", "--launch-id", "--control-endpoint"];

    private static readonly HashSet<string> StatusKeys = new(StringComparer.Ordinal)
    {
        "version", "scope", "nonce", "launchId", "launcherPid",
        "launcherCreationFileTime", "outcome", "exitCode", "cleanupConfirmed",
    };

    internal static bool TryParseLauncher(
        IReadOnlyList<string>? arguments,
        string? expectedExecutable,
        out FixtureB1LauncherCommand? command)
    {
        command = null;
        if (!TryReadPairs(arguments, LauncherKeys, out var values) ||
            values[0] != QualificationConstants.OptInToken ||
            values[1] != LauncherMode ||
            !TryCanonicalGuid(values[2], out var nonce) ||
            !IsExpectedExecutable(values[3], expectedExecutable) ||
            !IsLowerHex(values[4], 40) ||
            values.Skip(5).Any(value => !IsLowerHex(value, 64)))
            return false;

        command = new(nonce, values[3], values[4], values[5], values[6],
            values[7], values[8], values[9], values[10], values[11]);
        return true;
    }

    internal static bool TryParseRunner(
        IReadOnlyList<string>? arguments,
        Guid expectedRunNonce,
        out FixtureB1RunnerCommand? command)
    {
        command = null;
        if (expectedRunNonce == Guid.Empty ||
            !TryParseRunnerSyntax(arguments, out var syntax) ||
            syntax!.RunNonce != expectedRunNonce)
            return false;

        command = syntax;
        return true;
    }

    /// <summary>Parses untrusted routing claims; authentication must bind both IDs later.</summary>
    internal static bool TryParseRunnerSyntax(
        IReadOnlyList<string>? arguments,
        out FixtureB1RunnerCommand? command)
    {
        command = null;
        if (!TryReadPairs(arguments, RunnerKeys, out var values) ||
            values[0] != QualificationConstants.OptInToken ||
            values[1] != RunnerMode ||
            !TryCanonicalGuid(values[2], out var nonce) ||
            !TryCanonicalGuid(values[3], out var launchId) ||
            launchId == nonce ||
            !values[4].StartsWith(EndpointPrefix, StringComparison.Ordinal) ||
            !IsLowerHex(values[4].AsSpan(EndpointPrefix.Length), 32))
            return false;

        command = new(nonce, launchId, values[4]);
        return true;
    }

    internal static bool TryParseTerminalStatus(
        ReadOnlySpan<byte> record,
        Guid expectedRunNonce,
        FixtureHandoffProcessIdentity expectedLauncher,
        QualificationExitCode processExitCode,
        out FixtureB1StatusRecord? status)
    {
        status = null;
        if (expectedRunNonce == Guid.Empty || !expectedLauncher.IsValid ||
            record.Length is < 3 or > MaximumStatusBytes ||
            record[0] != (byte)'{' || record[^2] != (byte)'}' ||
            record[^1] != (byte)'\n' ||
            record[..^1].Contains((byte)'\n') ||
            record[..^1].Contains((byte)'\r'))
            return false;

        try
        {
            // Strict UTF-8 rejects malformed bytes rather than replacing them before JSON parse.
            _ = new UTF8Encoding(false, true).GetString(record[..^1]);
            using var document = JsonDocument.Parse(record[..^1].ToArray(),
                new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!StatusKeys.Contains(property.Name) || !seen.Add(property.Name))
                    return false;
            if (seen.Count != StatusKeys.Count ||
                !TryInt32(root.GetProperty("version"), out var version) || version != 1 ||
                !TryString(root.GetProperty("scope"), out var scope) || scope != StatusScope ||
                !TryString(root.GetProperty("nonce"), out var nonceText) ||
                !TryCanonicalGuid(nonceText, out var nonce) || nonce != expectedRunNonce ||
                !TryOptionalGuid(root.GetProperty("launchId"), out var launchId) ||
                launchId == nonce ||
                !TryUInt32(root.GetProperty("launcherPid"), out var pid) || pid == 0 ||
                !TryInt64(root.GetProperty("launcherCreationFileTime"), out var creation) ||
                creation <= 0 ||
                new FixtureHandoffProcessIdentity(pid, creation) != expectedLauncher ||
                !TryString(root.GetProperty("outcome"), out var outcomeText) ||
                !TryOutcome(outcomeText, out var outcome) ||
                !TryInt32(root.GetProperty("exitCode"), out var exitCode) ||
                exitCode != (int)processExitCode ||
                !TryBoolean(root.GetProperty("cleanupConfirmed"), out var cleanupConfirmed) ||
                !Consistent(outcome, processExitCode, cleanupConfirmed, launchId))
                return false;

            status = new(nonce, launchId, expectedLauncher, outcome,
                processExitCode, cleanupConfirmed);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool TryReadPairs(
        IReadOnlyList<string>? arguments, IReadOnlyList<string> keys, out string[] values)
    {
        values = [];
        if (arguments is null || arguments.Count != keys.Count * 2)
            return false;

        var total = 0;
        var parsed = new string[keys.Count];
        for (var index = 0; index < keys.Count; index++)
        {
            var key = arguments[index * 2];
            var value = arguments[index * 2 + 1];
            if (key is null || value is null ||
                !key.Equals(keys[index], StringComparison.Ordinal) ||
                value.Length is 0 or > MaximumArgumentCodeUnits ||
                value.Any(char.IsControl))
                return false;
            total = checked(total + key.Length + value.Length);
            if (total > MaximumArgumentPayloadCodeUnits)
                return false;
            parsed[index] = value;
        }
        values = parsed;
        return true;
    }

    private static bool IsExpectedExecutable(string value, string? expected)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(expected) &&
                Path.IsPathFullyQualified(value) &&
                Path.IsPathFullyQualified(expected) &&
                value.Equals(expected, StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryCanonicalGuid(string value, out Guid guid) =>
        Guid.TryParseExact(value, "D", out guid) && guid != Guid.Empty &&
        guid.ToString("D").Equals(value, StringComparison.Ordinal);

    private static bool TryOptionalGuid(JsonElement value, out Guid? guid)
    {
        guid = null;
        if (value.ValueKind == JsonValueKind.Null)
            return true;
        if (!TryString(value, out var text) || !TryCanonicalGuid(text, out var parsed))
            return false;
        guid = parsed;
        return true;
    }

    private static bool IsLowerHex(string value, int length) =>
        IsLowerHex(value.AsSpan(), length);

    private static bool IsLowerHex(ReadOnlySpan<char> value, int length)
    {
        if (value.Length != length)
            return false;
        foreach (var character in value)
            if (!((character is >= '0' and <= '9') ||
                    (character is >= 'a' and <= 'f')))
                return false;
        return true;
    }

    private static bool TryString(JsonElement value, out string text)
    {
        text = string.Empty;
        if (value.ValueKind != JsonValueKind.String)
            return false;
        text = value.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryInt32(JsonElement value, out int result)
    {
        result = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out result);
    }

    private static bool TryUInt32(JsonElement value, out uint result)
    {
        result = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out result);
    }

    private static bool TryInt64(JsonElement value, out long result)
    {
        result = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out result);
    }

    private static bool TryBoolean(JsonElement value, out bool result)
    {
        result = value.ValueKind == JsonValueKind.True;
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }

    private static bool TryOutcome(string value, out FixtureB1StatusOutcome outcome)
    {
        outcome = value switch
        {
            "passed" => FixtureB1StatusOutcome.Passed,
            "rejected" => FixtureB1StatusOutcome.Rejected,
            "failed" => FixtureB1StatusOutcome.Failed,
            _ => (FixtureB1StatusOutcome)(-1),
        };
        return Enum.IsDefined(outcome);
    }

    private static bool Consistent(
        FixtureB1StatusOutcome outcome, QualificationExitCode exitCode,
        bool cleanupConfirmed, Guid? launchId) => outcome switch
        {
            FixtureB1StatusOutcome.Passed =>
                exitCode == QualificationExitCode.Pass && cleanupConfirmed &&
                launchId.HasValue,
            FixtureB1StatusOutcome.Rejected =>
                exitCode == QualificationExitCode.UsageOrPrerequisite && cleanupConfirmed,
            FixtureB1StatusOutcome.Failed => exitCode switch
            {
                QualificationExitCode.AssertionFailedCleanupConfirmed or
                QualificationExitCode.TimeoutCleanupConfirmed or
                QualificationExitCode.InfrastructureExceptionCleanupConfirmed => cleanupConfirmed,
                QualificationExitCode.CleanupIncomplete => !cleanupConfirmed,
                _ => false,
            },
            _ => false,
        };
}
