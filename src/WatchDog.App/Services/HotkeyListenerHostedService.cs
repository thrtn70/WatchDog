using System.Windows;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WatchDog.Core.Capture;
using WatchDog.Core.Hotkeys;
using WatchDog.Core.Settings;

namespace WatchDog.App.Services;

/// <summary>
/// Registers global hotkeys and triggers replay buffer save / recording toggle on press.
/// Re-registers hotkeys at runtime when settings change.
/// </summary>
public sealed class HotkeyListenerHostedService : IHostedService
{
    private const int SaveClipHotkeyId = 1;
    private const int ToggleRecordingHotkeyId = 2;

    private readonly Win32HotkeyService _hotkeyService;
    private readonly ICaptureEngine _captureEngine;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<HotkeyListenerHostedService> _logger;

    private HotkeySettings _currentHotkey = new();

    public HotkeyListenerHostedService(
        Win32HotkeyService hotkeyService,
        ICaptureEngine captureEngine,
        ISettingsService settingsService,
        ILogger<HotkeyListenerHostedService> logger)
    {
        _hotkeyService = hotkeyService;
        _captureEngine = captureEngine;
        _settingsService = settingsService;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = _settingsService.Load();
        _currentHotkey = settings.Hotkey;

        _hotkeyService.HotkeyPressed += OnHotkeyPressed;
        _settingsService.SettingsChanged += OnSettingsChanged;

        // RegisterHotKey must be called from the HWND's owning thread (UI thread).
        // Use InvokeAsync (not Invoke) to avoid deadlocking with the UI thread
        // that is synchronously awaiting _host.StartAsync().
        await Application.Current!.Dispatcher.InvokeAsync(() => RegisterAllHotkeys(_currentHotkey));

        _logger.LogInformation(
            "Hotkey listener started. SaveClip={SaveClip}, ToggleRecording={Toggle}",
            HotkeyConfig.FormatDisplay(_currentHotkey.SaveClipKey, _currentHotkey.Modifiers),
            HotkeyConfig.FormatDisplay(_currentHotkey.ToggleRecordingKey, _currentHotkey.ToggleRecordingModifiers));

    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _hotkeyService.HotkeyPressed -= OnHotkeyPressed;

        // UnregisterHotKey must be called from the HWND's owning thread
        if (Application.Current is not null)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _hotkeyService.Unregister(SaveClipHotkeyId);
                _hotkeyService.Unregister(ToggleRecordingHotkeyId);
            });
        }
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        var newHotkey = settings.Hotkey;

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (newHotkey == _currentHotkey)
                return;

            try
            {
                _hotkeyService.Unregister(SaveClipHotkeyId);
                _hotkeyService.Unregister(ToggleRecordingHotkeyId);
                RegisterAllHotkeys(newHotkey);
                _currentHotkey = newHotkey;

                _logger.LogInformation(
                    "Hotkeys re-registered. SaveClip={SaveClip}, ToggleRecording={Toggle}",
                    HotkeyConfig.FormatDisplay(newHotkey.SaveClipKey, newHotkey.Modifiers),
                    HotkeyConfig.FormatDisplay(newHotkey.ToggleRecordingKey, newHotkey.ToggleRecordingModifiers));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to re-register hotkeys after settings change");
            }
        });
    }

    private void RegisterAllHotkeys(HotkeySettings hotkey)
    {
        var saveRegistered = _hotkeyService.Register(new HotkeyConfig
        {
            Id = SaveClipHotkeyId,
            VirtualKey = (uint)hotkey.SaveClipKey,
            Modifiers = hotkey.Modifiers,
            Description = "Save Clip",
        });

        if (!saveRegistered)
        {
            _logger.LogWarning(
                "Failed to register save-clip hotkey ({Hotkey}). Another application may be using it.",
                HotkeyConfig.FormatDisplay(hotkey.SaveClipKey, hotkey.Modifiers));
        }

        var toggleRegistered = _hotkeyService.Register(new HotkeyConfig
        {
            Id = ToggleRecordingHotkeyId,
            VirtualKey = (uint)hotkey.ToggleRecordingKey,
            Modifiers = hotkey.ToggleRecordingModifiers,
            Description = "Toggle Recording",
        });

        if (!toggleRegistered)
        {
            _logger.LogWarning(
                "Failed to register toggle-recording hotkey ({Hotkey}). Another application may be using it.",
                HotkeyConfig.FormatDisplay(hotkey.ToggleRecordingKey, hotkey.ToggleRecordingModifiers));
        }
    }

    private async void OnHotkeyPressed(int id)
    {
        try
        {
            switch (id)
            {
                case SaveClipHotkeyId:
                    await HandleSaveClipAsync();
                    break;
                case ToggleRecordingHotkeyId:
                    HandleToggleRecording();
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in hotkey handler (id={Id})", id);
        }
    }

    private async Task HandleSaveClipAsync()
    {
        if (_captureEngine.State != CaptureState.Buffering)
        {
            _logger.LogDebug("Save-clip hotkey pressed but not currently buffering (state={State})", _captureEngine.State);
            return;
        }

        _logger.LogInformation("Save clip hotkey pressed");

        try
        {
            var path = await _captureEngine.SaveReplayAsync();
            if (path is not null)
                _logger.LogInformation("Clip saved: {Path}", path);
            else
                _logger.LogWarning("Clip save returned no path");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save clip");
        }
    }

    private async void HandleToggleRecording()
    {
        _logger.LogInformation("Toggle recording hotkey pressed (state={State})", _captureEngine.State);

        try
        {
            if (_captureEngine.State == CaptureState.Idle)
            {
                await _captureEngine.StartDesktopCaptureAsync();
                _logger.LogInformation("Recording started via hotkey");
            }
            else if (_captureEngine.State == CaptureState.Buffering)
            {
                await _captureEngine.StopAsync();
                _logger.LogInformation("Recording stopped via hotkey");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to toggle recording");
        }
    }
}
