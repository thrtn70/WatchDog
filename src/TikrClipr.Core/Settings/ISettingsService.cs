namespace TikrClipr.Core.Settings;

public interface ISettingsService
{
    AppSettings Load();
    void Save(AppSettings settings);
    event Action<AppSettings>? SettingsChanged;
}
