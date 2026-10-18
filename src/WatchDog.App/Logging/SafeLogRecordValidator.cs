using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WatchDog.Core.Capture;

namespace WatchDog.App.Logging;

/// <summary>
/// Strict, filesystem-free validation for a possible future export path. A valid record has
/// permitted shape and values; this does not authenticate the file or prove native cleanup.
/// </summary>
internal static class SafeLogRecordValidator
{
    private static readonly Regex CandidateName = new(
        @"\Awatchdog-safe-v2-(?<date>[0-9]{4}-[0-9]{2}-[0-9]{2})-(?<slot>[0-9]{4})\.log\z",
        RegexOptions.CultureInvariant);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] Keys =
    [
        "schema", "timeUtc", "level", "source", "exception", "event", "operation",
        "disposition", "kind", "operationId", "beforeSessionId", "afterSessionId",
        "beforeGeneration", "afterGeneration", "beforeStatus", "afterStatus",
        "beforeHealth", "afterHealth", "afterLastFailureKind", "afterContextPresent",
    ];

    internal static bool IsCandidateFileName(string fileName) =>
        TryCandidateDate(fileName, out _, out _);

    internal static bool TryValidateCandidate(
        string fileName,
        ReadOnlySpan<byte> contents,
        out IReadOnlyList<SafeLogRecord> records)
    {
        records = Array.Empty<SafeLogRecord>();
        if (!TryCandidateDate(fileName, out var date, out var slot) ||
            contents.Length <= SafeLogSegmentFooter.FooterBytes ||
            contents.Length > FileLoggerProvider.DefaultMaxFileBytes ||
            contents[^1] != (byte)'\n')
            return false;

        var data = contents[..^SafeLogSegmentFooter.FooterBytes];
        if (data[^1] != (byte)'\n' ||
            !SafeLogSegmentFooter.TryValidate(contents[^SafeLogSegmentFooter.FooterBytes..],
                date, slot, data, out var expectedCount))
            return false;

        string text;
        try { text = StrictUtf8.GetString(data); }
        catch (DecoderFallbackException) { return false; }

        var parsed = new List<SafeLogRecord>();
        foreach (var line in text.Split('\n')[..^1])
        {
            if (!TryParseLine(line, out var record) || record!.TimeUtc.UtcDateTime.Date != date.ToDateTime(TimeOnly.MinValue))
                return false;
            parsed.Add(record);
        }
        if (parsed.Count == 0 || parsed.Count != expectedCount)
            return false;
        records = parsed.AsReadOnly();
        return true;
    }

    internal static bool TryParseLine(string line, out SafeLogRecord? record)
    {
        record = null;
        if (string.IsNullOrEmpty(line) || line.Length + 1 > FileLoggerProvider.MaxRecordBytes ||
            line.Any(ch => ch != ' ' && (ch < '!' || ch > '~')))
            return false;

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var previousKeyIndex = -1;
        foreach (var token in line.Split(' '))
        {
            var equals = token.IndexOf('=');
            if (equals <= 0 || equals == token.Length - 1) return false;
            var key = token[..equals];
            var index = Array.IndexOf(Keys, key);
            if (index <= previousKeyIndex || !values.TryAdd(key, token[(equals + 1)..]))
                return false;
            previousKeyIndex = index;
        }

        if (!Has(values, "schema", "watchdog.safe-log.v2") ||
            !values.TryGetValue("timeUtc", out var rawTime) ||
            !DateTimeOffset.TryParseExact(rawTime, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time) ||
            time.Offset != TimeSpan.Zero || time.ToString("O", CultureInfo.InvariantCulture) != rawTime ||
            !values.TryGetValue("level", out var rawLevel) ||
            !TryLevel(rawLevel, out var level) ||
            !values.TryGetValue("source", out var source) || !IsSource(source) ||
            !values.TryGetValue("exception", out var rawException) ||
            !TryBool(rawException, out var hasException))
            return false;

        values.TryGetValue("event", out var eventCode);
        if (!ValidEvent(source, eventCode, level)) return false;

        SafeCaptureTransitionRecord? transition = null;
        if (eventCode == "capture-transition")
        {
            if (!TryTransition(values, level, out transition)) return false;
        }
        else if (values.Keys.Any(key => Array.IndexOf(Keys, key) >= 6))
            return false;

        record = new SafeLogRecord(time, level, source, eventCode, hasException, transition);
        return true;
    }

    private static bool TryCandidateDate(string fileName, out DateOnly date, out int slot)
    {
        date = default;
        slot = default;
        if (string.IsNullOrEmpty(fileName)) return false;
        var match = CandidateName.Match(fileName);
        return match.Success &&
            DateOnly.TryParseExact(match.Groups["date"].Value, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out date) &&
            int.TryParse(match.Groups["slot"].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out slot) &&
            slot < FileLoggerProvider.DefaultMaxSlotsPerDay;
    }

    private static bool TryTransition(
        Dictionary<string, string> values,
        LogLevel level,
        out SafeCaptureTransitionRecord? transition)
    {
        transition = null;
        if (!TryRequiredEnum<CaptureOperationKind>(values, "operation", out var operation) ||
            !TryRequiredEnum<CaptureCommandDisposition>(values, "disposition", out var disposition) ||
            !TryRequiredGuid(values, "operationId", out var operationId) ||
            !TryRequiredEnum<CaptureLifecycleStatus>(values, "beforeStatus", out var beforeStatus) ||
            !TryRequiredEnum<CaptureLifecycleStatus>(values, "afterStatus", out var afterStatus) ||
            !TryRequiredEnum<CaptureHealth>(values, "beforeHealth", out var beforeHealth) ||
            !TryRequiredEnum<CaptureHealth>(values, "afterHealth", out var afterHealth) ||
            !values.TryGetValue("afterContextPresent", out var rawContextPresent) ||
            !TryBool(rawContextPresent, out var afterContextPresent) ||
            !TryOptionalEnum<CaptureFailureKind>(values, "kind", out var kind) ||
            !TryOptionalEnum<CaptureFailureKind>(values, "afterLastFailureKind", out var lastFailureKind) ||
            !TryOptionalContext(values, "beforeSessionId", "beforeGeneration", out var beforeSessionId, out var beforeGeneration) ||
            !TryOptionalContext(values, "afterSessionId", "afterGeneration", out var afterSessionId, out var afterGeneration) ||
            afterContextPresent != afterSessionId.HasValue)
            return false;

        var degraded = operation == CaptureOperationKind.HealthChanged &&
            disposition == CaptureCommandDisposition.Completed &&
            afterHealth == CaptureHealth.Degraded;
        if (degraded != lastFailureKind.HasValue ||
            (degraded && level != LogLevel.Warning) ||
            (!degraded && level != (disposition == CaptureCommandDisposition.Completed
                ? LogLevel.Information : LogLevel.Warning)))
            return false;

        transition = new SafeCaptureTransitionRecord(operation, disposition, kind, operationId,
            beforeSessionId, afterSessionId, beforeGeneration, afterGeneration,
            beforeStatus, afterStatus, beforeHealth, afterHealth, lastFailureKind,
            afterContextPresent);
        return true;
    }

    private static bool TryOptionalContext(
        Dictionary<string, string> values,
        string sessionKey,
        string generationKey,
        out Guid? sessionId,
        out long? generation)
    {
        sessionId = null;
        generation = null;
        var hasSession = values.TryGetValue(sessionKey, out var rawSession);
        var hasGeneration = values.TryGetValue(generationKey, out var rawGeneration);
        if (hasSession != hasGeneration) return false;
        if (!hasSession) return true;
        if (!TryGuid(rawSession!, out var parsedSession) ||
            !long.TryParse(rawGeneration, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedGeneration) ||
            parsedGeneration <= 0 ||
            parsedGeneration.ToString(CultureInfo.InvariantCulture) != rawGeneration)
            return false;
        sessionId = parsedSession;
        generation = parsedGeneration;
        return true;
    }

    private static bool TryRequiredGuid(Dictionary<string, string> values, string key, out Guid value)
    {
        value = default;
        return values.TryGetValue(key, out var raw) && TryGuid(raw, out value);
    }

    private static bool TryGuid(string raw, out Guid value) =>
        Guid.TryParseExact(raw, "D", out value) &&
        value != Guid.Empty && value.ToString("D") == raw;

    private static bool TryRequiredEnum<T>(Dictionary<string, string> values, string key, out T value)
        where T : struct, Enum
    {
        value = default;
        return values.TryGetValue(key, out var raw) && TryEnum(raw, out value);
    }

    private static bool TryOptionalEnum<T>(Dictionary<string, string> values, string key, out T? value)
        where T : struct, Enum
    {
        value = null;
        if (!values.TryGetValue(key, out var raw)) return true;
        if (!TryEnum<T>(raw, out var parsed)) return false;
        value = parsed;
        return true;
    }

    private static bool TryEnum<T>(string raw, out T value) where T : struct, Enum =>
        Enum.TryParse(raw, ignoreCase: false, out value) &&
        Enum.IsDefined(value) && value.ToString() == raw;

    private static bool TryLevel(string raw, out LogLevel level)
    {
        level = raw switch
        {
            "trace" => LogLevel.Trace,
            "debug" => LogLevel.Debug,
            "information" => LogLevel.Information,
            "warning" => LogLevel.Warning,
            "error" => LogLevel.Error,
            "critical" => LogLevel.Critical,
            _ => LogLevel.None,
        };
        return level != LogLevel.None;
    }

    private static bool TryBool(string raw, out bool value)
    {
        value = raw == "true";
        return value || raw == "false";
    }

    private static bool Has(Dictionary<string, string> values, string key, string value) =>
        values.TryGetValue(key, out var found) && found == value;

    private static bool IsSource(string source) => source is
        "capture-manager" or "obs-capture" or "capture-backend" or "replay-buffer" or
        "session-recorder" or "session-host" or "storage-maintenance" or "hotkey" or "other";

    private static bool ValidEvent(string source, string? eventCode, LogLevel level) => (source, eventCode, level) switch
    {
        (_, null, _) => true,
        ("capture-manager", "capture-transition", LogLevel.Information or LogLevel.Warning) => true,
        ("capture-manager", "source-loss-handler-failed" or "health-handler-failed", LogLevel.Error) => true,
        ("obs-capture", "capture-start-failed" or "replay-buffer-error", LogLevel.Error) => true,
        ("obs-capture", "capture-stopped", LogLevel.Information) => true,
        ("capture-backend", "backend-start-failed" or "backend-stop-failed" or
            "backend-save-failed" or "segment-metadata-pending" or "session-recorder-error", LogLevel.Error) => true,
        ("replay-buffer", "replay-start-failed", LogLevel.Error) => true,
        ("session-recorder", "session-started", LogLevel.Information) => true,
        ("session-host", "session-recorder-error", LogLevel.Error) => true,
        ("storage-maintenance", "storage-cleanup-failed", LogLevel.Error) => true,
        ("hotkey", "clip-save-failed", LogLevel.Warning) => true,
        _ => false,
    };
}

/// <summary>Structural closure for supplied bytes, not authenticity or durable storage proof.</summary>
internal static class SafeLogSegmentFooter
{
    internal const int FooterBytes = 183;
    private static readonly Regex Shape = new(
        @"\Aschema=watchdog\.safe-log\.close\.v1 closed=true dateUtc=(?<date>[0-9]{4}-[0-9]{2}-[0-9]{2}) slot=(?<slot>[0-9]{4}) records=(?<records>[0-9]{10}) bytes=(?<bytes>[0-9]{10}) sha256=(?<hash>[0-9a-f]{64})\n\z",
        RegexOptions.CultureInvariant);

    internal static byte[] Format(string date, int slot, int recordCount, int dataBytes, byte[] hash)
    {
        var footer = $"schema=watchdog.safe-log.close.v1 closed=true dateUtc={date} slot={slot:D4} records={recordCount:D10} bytes={dataBytes:D10} sha256={Convert.ToHexStringLower(hash)}\n";
        var bytes = Encoding.ASCII.GetBytes(footer);
        if (bytes.Length != FooterBytes) throw new InvalidOperationException("Invalid safe-log footer length.");
        return bytes;
    }

    internal static bool TryValidate(ReadOnlySpan<byte> footer, DateOnly date, int slot,
        ReadOnlySpan<byte> data, out int recordCount)
    {
        recordCount = 0;
        if (footer.Length != FooterBytes)
            return false;
        foreach (var value in footer)
            if (value != (byte)'\n' && (value < (byte)' ' || value > (byte)'~'))
                return false;
        var match = Shape.Match(Encoding.ASCII.GetString(footer));
        if (!match.Success || match.Groups["date"].Value != date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ||
            match.Groups["slot"].Value != slot.ToString("D4", CultureInfo.InvariantCulture) ||
            !int.TryParse(match.Groups["records"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out recordCount) ||
            recordCount <= 0 ||
            !int.TryParse(match.Groups["bytes"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var length) ||
            length != data.Length)
            return false;
        return string.Equals(match.Groups["hash"].Value,
            Convert.ToHexStringLower(SHA256.HashData(data)), StringComparison.Ordinal);
    }
}

internal sealed record SafeLogRecord(
    DateTimeOffset TimeUtc,
    LogLevel Level,
    string Source,
    string? Event,
    bool HasException,
    SafeCaptureTransitionRecord? Transition);

internal sealed record SafeCaptureTransitionRecord(
    CaptureOperationKind Operation,
    CaptureCommandDisposition Disposition,
    CaptureFailureKind? Kind,
    Guid OperationId,
    Guid? BeforeSessionId,
    Guid? AfterSessionId,
    long? BeforeGeneration,
    long? AfterGeneration,
    CaptureLifecycleStatus BeforeStatus,
    CaptureLifecycleStatus AfterStatus,
    CaptureHealth BeforeHealth,
    CaptureHealth AfterHealth,
    CaptureFailureKind? AfterLastFailureKind,
    bool AfterContextPresent);
