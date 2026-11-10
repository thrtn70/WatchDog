using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace WatchDog.Core.Settings;

public sealed class JsonSettingsService : ISettingsService
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WatchDog");

    private readonly string _settingsPath;
    private readonly Func<string?> _defaultRecordingFolder;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly string SessionSegmentLivenessPropertyName =
        JsonOptions.PropertyNamingPolicy?.ConvertName(nameof(AppSettings.SessionSegmentLiveness)) ??
        nameof(AppSettings.SessionSegmentLiveness);

    private readonly ILogger<JsonSettingsService> _logger;
    private readonly object _saveLock = new();
    private AppSettings _cached;

    public event Action<AppSettings>? SettingsChanged;

    public JsonSettingsService(ILogger<JsonSettingsService> logger, string? settingsPath = null)
        : this(logger, settingsPath, () => new StorageSettings().SavePath)
    {
    }

    internal JsonSettingsService(
        ILogger<JsonSettingsService> logger,
        string? settingsPath,
        Func<string?> defaultRecordingFolder)
    {
        _logger = logger;
        _settingsPath = settingsPath ?? Path.Combine(SettingsDir, "settings.json");
        _defaultRecordingFolder = defaultRecordingFolder;
        _cached = LoadFromDisk();
    }

    public AppSettings Load()
    {
        lock (_saveLock) return _cached;
    }

    public void Save(AppSettings settings)
    {
        lock (_saveLock)
        {
            settings = NormalizePaths(settings, _cached);
            var settingsDirectory = Path.GetDirectoryName(_settingsPath)!;
            Directory.CreateDirectory(settingsDirectory);

            var tmp = Path.Combine(settingsDirectory, $"settings.{Guid.NewGuid():N}.tmp");
            try
            {
                var json = JsonSerializer.Serialize(settings, JsonOptions);
                File.WriteAllText(tmp, json);
                File.Move(tmp, _settingsPath, overwrite: true);
                _cached = settings;
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
                throw;
            }

            _logger.LogDebug("Settings saved to {Path}", _settingsPath);
        }

        SettingsChanged?.Invoke(settings);
    }

    private static AppSettings NormalizePaths(AppSettings settings, AppSettings? previous = null)
    {
        if (!TryNormalizeAbsoluteFolder(settings.Storage.SavePath, out var destination))
            throw new ArgumentException("Choose an absolute recording folder path.", nameof(settings));
        var olderPaths = (settings.Storage.LegacySavePaths ?? [])
            .Concat(previous?.Storage.LegacySavePaths ?? [])
            .Concat(new[] { settings.Buffer.OutputDirectory, previous?.Storage.SavePath, previous?.Buffer.OutputDirectory })
            .Select(path => TryNormalizeAbsoluteFolder(path, out var folder) ? folder : null)
            .OfType<string>()
            .Where(p => !string.Equals(p, destination, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return settings with
        {
            Storage = settings.Storage with { SavePath = destination, LegacySavePaths = olderPaths },
            Buffer = settings.Buffer with { OutputDirectory = destination },
        };
    }

    private AppSettings LoadFromDisk()
    {
        // Path repair must not turn a missing default folder into a reset of every preference.
        return NormalizePaths(WithUsableSavePath(ReadSettingsOrDefaults()));
    }

    private AppSettings ReadSettingsOrDefaults()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                _logger.LogInformation("No settings file found, using defaults");
                return new AppSettings();
            }

            var json = File.ReadAllText(_settingsPath);
            var settings = DeserializeSettings(json);

            if (settings is null || settings.Storage is null || settings.Buffer is null)
            {
                _logger.LogWarning("Settings file was empty or invalid, using defaults");
                return new AppSettings();
            }

            _logger.LogInformation("Settings loaded from {Path}", _settingsPath);
            return settings;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load settings, using defaults");
            return new AppSettings();
        }
    }

    private AppSettings WithUsableSavePath(AppSettings settings)
    {
        if (TryNormalizeAbsoluteFolder(settings.Storage.SavePath, out _))
            return settings;

        if (!TryNormalizeAbsoluteFolder(settings.Buffer.OutputDirectory, out var fallback) &&
            !TryNormalizeAbsoluteFolder(_defaultRecordingFolder(), out fallback))
        {
            throw new InvalidDataException(
                "No usable absolute recording folder is available. Settings were not changed.");
        }

        _logger.LogWarning("Invalid saved recording folder was replaced; other settings were preserved");
        return settings with { Storage = settings.Storage with { SavePath = fallback } };
    }

    private static bool TryNormalizeAbsoluteFolder(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return false;

        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private AppSettings? DeserializeSettings(string json)
    {
        var root = JsonNode.Parse(json);
        if (root is not JsonObject settingsObject)
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);

        var hasLivenessRequest = settingsObject.TryGetPropertyValue(
            SessionSegmentLivenessPropertyName,
            out var livenessNode);
        if (hasLivenessRequest)
            settingsObject.Remove(SessionSegmentLivenessPropertyName);

        var settings = settingsObject.Deserialize<AppSettings>(JsonOptions);
        if (settings is null || !hasLivenessRequest || livenessNode is null)
            return settings;

        SessionSegmentLivenessSettings? livenessRequest;
        try
        {
            livenessRequest = livenessNode.Deserialize<SessionSegmentLivenessSettings>(JsonOptions);
        }
        catch (JsonException)
        {
            LogInvalidLivenessRequest();
            livenessRequest = null;
        }
        catch (NotSupportedException)
        {
            LogInvalidLivenessRequest();
            livenessRequest = null;
        }

        return settings with { SessionSegmentLiveness = livenessRequest };
    }

    private void LogInvalidLivenessRequest() =>
        _logger.LogWarning(
            "Invalid session-segment liveness settings were ignored; liveness remains disabled");
}
