using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WatchDog.Core.Capture;
using WatchDog.Core.Settings;
using WatchDog.Core.Tests.Helpers;

namespace WatchDog.Core.Tests.Settings;

public sealed class JsonSettingsServiceSavePathTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"WatchDog-SettingsSavePathTests-{Guid.NewGuid():N}");

    private string SettingsPath => Path.Combine(_directory, "settings.json");
    private string OldVideosFolder => Path.Combine(_directory, "Videos", "WatchDog");

    [Fact]
    public void ChangingSavePathMovesTheReplayBufferAndPublishesTheSyncedFolder()
    {
        var service = CreateService();
        var first = Path.Combine(_directory, "First clips");
        var second = Path.Combine(_directory, "Second clips");
        SaveSavePath(service, first);
        AppSettings? published = null;
        service.SettingsChanged += settings => published = settings;

        SaveSavePath(service, second);

        var cached = service.Load();
        Assert.Equal(second, cached.Storage.SavePath);
        Assert.Equal(second, cached.Buffer.OutputDirectory);
        Assert.Contains(first, cached.Storage.LegacySavePaths);
        var notified = Assert.IsType<AppSettings>(published);
        Assert.Equal(second, notified.Buffer.OutputDirectory);
        var reloaded = CreateService().Load();
        Assert.Equal(second, reloaded.Storage.SavePath);
        Assert.Equal(second, reloaded.Buffer.OutputDirectory);
        Assert.Contains(first, reloaded.Storage.LegacySavePaths);
    }

    [Theory]
    [InlineData(@"{drive}:\WatchDog Clips", @"{drive}:\WatchDog Clips")]
    [InlineData(@"{drive}:\WatchDog Clips\", @"{drive}:\WatchDog Clips")]
    [InlineData("{drive}:/WatchDog Clips/", @"{drive}:\WatchDog Clips")]
    [InlineData(@"{drive}:\", @"{drive}:\")]
    public void SavePathOnAnotherDriveIsNormalizedForBothFolders(string requested, string expected)
    {
        var service = CreateService();

        SaveSavePath(service, OnOtherDrive(requested));

        var reloaded = CreateService().Load();
        Assert.Equal(OnOtherDrive(expected), reloaded.Storage.SavePath);
        Assert.Equal(OnOtherDrive(expected), reloaded.Buffer.OutputDirectory);
    }

    [Fact]
    public void ReturningToAnEarlierFolderTakesItOutOfTheLegacyList()
    {
        var service = CreateService();
        var first = Path.Combine(_directory, "First clips");
        var second = Path.Combine(_directory, "Second clips");
        SaveSavePath(service, first);
        SaveSavePath(service, second);

        SaveSavePath(service, first.ToUpperInvariant() + Path.DirectorySeparatorChar);

        var reloaded = CreateService().Load();
        Assert.Equal(first.ToUpperInvariant(), reloaded.Storage.SavePath);
        Assert.Equal(first.ToUpperInvariant(), reloaded.Buffer.OutputDirectory);
        Assert.Contains(second, reloaded.Storage.LegacySavePaths);
        Assert.DoesNotContain(reloaded.Storage.LegacySavePaths,
            path => string.Equals(path, first, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SavingUnrelatedSettingsKeepsEachEarlierFolderOnce()
    {
        var service = CreateService();
        var first = Path.Combine(_directory, "First clips");
        var second = Path.Combine(_directory, "Second clips");
        SaveSavePath(service, first);
        SaveSavePath(service, second);
        var current = service.Load();

        service.Save(current with { StartMinimized = !current.StartMinimized });
        service.Save(service.Load() with { StartMinimized = current.StartMinimized });

        var legacy = CreateService().Load().Storage.LegacySavePaths;
        Assert.Single(legacy, path => path == first);
        Assert.DoesNotContain(second, legacy);
        Assert.Equal(legacy.Count, legacy.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData(@"{drive}:\Game Clips")]
    [InlineData(@"{drive}:\Game Clips\")]
    [InlineData("{drive}:/Game Clips")]
    public void OlderSettingsWithACustomSavePathMoveTheBufferAndKeepTheOldFolderReadable(string savedPath)
    {
        WriteSettingsFileInV123Format(OnOtherDrive(savedPath), OldVideosFolder);
        var fileBeforeLoad = File.ReadAllBytes(SettingsPath);

        var settings = CreateService().Load();

        var expected = OnOtherDrive(@"{drive}:\Game Clips");
        Assert.Equal(expected, settings.Storage.SavePath);
        Assert.Equal(expected, settings.Buffer.OutputDirectory);
        Assert.Equal(OldVideosFolder, Assert.Single(settings.Storage.LegacySavePaths));
        AssertOlderChoicesKept(settings);
        Assert.Null(CaptureStartupBaselineResult.Create(settings).Failure);
        Assert.Equal(fileBeforeLoad, File.ReadAllBytes(SettingsPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Clips")]
    [InlineData(@"\Clips")]
    [InlineData("{drive}:")]
    [InlineData("{drive}:Clips")]
    public void OlderSettingsWithAnUnusableSavePathKeepEveryOtherChoice(string savedPath)
    {
        WriteSettingsFileInV123Format(OnOtherDrive(savedPath), OldVideosFolder);

        var settings = CreateService().Load();

        Assert.Equal(OldVideosFolder, settings.Storage.SavePath);
        Assert.Equal(OldVideosFolder, settings.Buffer.OutputDirectory);
        Assert.Empty(settings.Storage.LegacySavePaths);
        AssertOlderChoicesKept(settings);
    }

    [Fact]
    public void OlderSettingsWithNoUsableFolderUseTheDefaultFolderAndKeepEveryOtherChoice()
    {
        WriteSettingsFileInV123Format("Clips", bufferDirectory: "");

        var settings = CreateService().Load();

        var defaultFolder = new StorageSettings().SavePath;
        Assert.Equal(defaultFolder, settings.Storage.SavePath);
        Assert.Equal(defaultFolder, settings.Buffer.OutputDirectory);
        AssertOlderChoicesKept(settings);
    }

    [Fact]
    public void RepairedOlderSettingsSurviveTheNextSave()
    {
        WriteSettingsFileInV123Format("Clips", OldVideosFolder);
        var service = CreateService();

        service.Save(service.Load() with { StartWithWindows = false });

        var reloaded = CreateService().Load();
        Assert.Equal(OldVideosFolder, reloaded.Storage.SavePath);
        Assert.Equal(OldVideosFolder, reloaded.Buffer.OutputDirectory);
        Assert.Equal(0, reloaded.Storage.AutoDeleteDays);
        Assert.Equal(200, reloaded.Storage.MaxStorageGb);
        Assert.Equal(0x77, reloaded.Hotkey.SaveClipKey);
        Assert.False(reloaded.StartWithWindows);
    }

    public void Dispose() => TestFs.DeleteDirectoryWithRetry(_directory);

    private JsonSettingsService CreateService() =>
        new(NullLogger<JsonSettingsService>.Instance, SettingsPath);

    private static void SaveSavePath(JsonSettingsService service, string savePath)
    {
        var current = service.Load();
        service.Save(current with { Storage = current.Storage with { SavePath = savePath } });
    }

    private string OnOtherDrive(string path)
    {
        var drive = char.ToUpperInvariant(_directory[0]) == 'Q' ? "R" : "Q";
        return path.Replace("{drive}", drive, StringComparison.Ordinal);
    }

    private void WriteSettingsFileInV123Format(string savePath, string bufferDirectory)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, $$"""
            {
              "buffer": {
                "maxSeconds": 120,
                "maxSizeMb": 512,
                "outputDirectory": {{JsonSerializer.Serialize(bufferDirectory)}},
                "filenameFormat": "Clip %CCYY-%MM-%DD %hh-%mm-%ss"
              },
              "hotkey": {
                "saveClipKey": 119,
                "modifiers": 0,
                "toggleRecordingKey": 121,
                "toggleRecordingModifiers": 0
              },
              "storage": {
                "savePath": {{JsonSerializer.Serialize(savePath)}},
                "maxStorageGb": 200,
                "autoDeleteDays": 0
              },
              "startWithWindows": true,
              "startMinimized": false
            }
            """);
    }

    private static void AssertOlderChoicesKept(AppSettings settings)
    {
        Assert.Equal(0, settings.Storage.AutoDeleteDays);
        Assert.Equal(200, settings.Storage.MaxStorageGb);
        Assert.Equal(0x77, settings.Hotkey.SaveClipKey);
        Assert.True(settings.StartWithWindows);
        Assert.False(settings.StartMinimized);
    }
}
