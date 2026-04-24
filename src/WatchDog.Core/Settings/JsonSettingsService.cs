using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace WatchDog.Core.Settings;

public sealed class JsonSettingsService : ISettingsService
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WatchDog");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ILogger<JsonSettingsService> _logger;
    private readonly object _saveLock = new();
    private AppSettings _cached;

    public event Action<AppSettings>? SettingsChanged;

    public JsonSettingsService(ILogger<JsonSettingsService> logger)
    {
        _logger = logger;
        _cached = LoadFromDisk();
    }

    public AppSettings Load() => _cached;

    public void Save(AppSettings settings)
    {
        lock (_saveLock)
        {
            _cached = settings;
            Directory.CreateDirectory(SettingsDir);

            var tmp = Path.Combine(SettingsDir, $"settings.{Guid.NewGuid():N}.tmp");
            try
            {
                var json = JsonSerializer.Serialize(settings, JsonOptions);
                File.WriteAllText(tmp, json);
                File.Move(tmp, SettingsPath, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
                throw;
            }

            _logger.LogDebug("Settings saved to {Path}", SettingsPath);
        }

        SettingsChanged?.Invoke(settings);
    }

    private AppSettings LoadFromDisk()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                _logger.LogInformation("No settings file found, using defaults");
                return new AppSettings();
            }

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);

            if (settings is null)
            {
                _logger.LogWarning("Settings file was empty or invalid, using defaults");
                return new AppSettings();
            }

            _logger.LogInformation("Settings loaded from {Path}", SettingsPath);
            return settings;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load settings, using defaults");
            return new AppSettings();
        }
    }
}
