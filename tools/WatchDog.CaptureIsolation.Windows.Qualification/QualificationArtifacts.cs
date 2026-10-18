using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace WatchDog.CaptureIsolation.Windows.Qualification;

internal sealed class QualificationArtifacts
{
    private readonly object _logSync = new();
    private readonly Stopwatch _clock;
    private readonly QualificationOptions _options;

    internal QualificationArtifacts(QualificationOptions options, Stopwatch clock)
    {
        _options = options;
        _clock = clock;
    }

    internal string Root => _options.ArtifactRoot;

    internal string PathFor(string fileName) => Path.Combine(Root, fileName);

    internal void Log(string @event, string? code = null, uint? processId = null)
    {
        var role = _options.Role.ToString().ToLowerInvariant();
        var scenario = _options.Scenario?.ToString() ?? "run";
        var record = new RoleRecord(
            DateTimeOffset.UtcNow,
            _clock.ElapsedMilliseconds,
            _options.Nonce,
            role,
            scenario,
            @event,
            code,
            processId);
        var line = JsonSerializer.Serialize(record, QualificationJsonContext.Default.RoleRecord);
        var path = PathFor($"{role}-{scenario}.jsonl");
        lock (_logSync)
        {
            var content = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty;
            WriteTextAtomic(path, content + line + Environment.NewLine);
        }
    }

    internal void WriteJson<T>(string fileName, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        WriteTextAtomic(PathFor(fileName), JsonSerializer.Serialize(value, typeInfo));

    internal T ReadJson<T>(string fileName, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        var json = File.ReadAllText(PathFor(fileName), Encoding.UTF8);
        return JsonSerializer.Deserialize(json, typeInfo) ??
            throw new QualificationAssertionException("fixture.coordination.invalid");
    }

    internal async Task<T> WaitForJsonAsync<T>(
        string fileName,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = PathFor(fileName);
            if (File.Exists(path))
            {
                try
                {
                    return ReadJson(fileName, typeInfo);
                }
                catch (IOException)
                {
                    // A rename can race the existence check. Retry within the fixed deadline.
                }
                catch (JsonException)
                {
                    throw new QualificationAssertionException("fixture.coordination.invalid");
                }
            }
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
        }
        throw new QualificationTimeoutException("fixture.coordination.timeout");
    }

    internal static void WriteTextAtomic(string path, string content)
    {
        var directory = Path.GetDirectoryName(path) ??
            throw new InvalidOperationException("An artifact directory is required.");
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temporary, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporary, path, overwrite: true);
    }
}

internal sealed class QualificationAssertionException(string code) : Exception
{
    internal string Code { get; } = code;
}

internal sealed class QualificationTimeoutException(string code) : TimeoutException
{
    internal string Code { get; } = code;
}

internal sealed class QualificationCleanupException(string code) : Exception
{
    internal string Code { get; } = code;
}
