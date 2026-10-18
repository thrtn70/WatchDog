using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WatchDog.Core.ClipEditor;
using WatchDog.Core.Storage;

namespace WatchDog.App.ViewModels;

public partial class ClipEditorViewModel : ObservableObject
{
    private readonly IClipEditor _clipEditor;
    private readonly IClipExportCoordinator _clipExports;
    private readonly Action<ClipMetadata, ClipExportResult>? _exportCompleted;
    private ClipMetadata? _sourceClip;
    private CancellationTokenSource? _loadCancellation;
    private int _loadRevision;
    private bool _retired;
    private bool _playWhenReady;

    [ObservableProperty] private TimeSpan _duration;
    [ObservableProperty] private TimeSpan _position;
    [ObservableProperty] private TimeSpan _trimStart;
    [ObservableProperty] private TimeSpan _trimEnd;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isTrimming;
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private string _trimStatus = string.Empty;
    [ObservableProperty] private Uri? _source;
    [ObservableProperty] private List<string> _thumbnailFrames = [];

    public ClipEditorViewModel(
        IClipEditor clipEditor,
        IClipExportCoordinator clipExports,
        Action<ClipMetadata, ClipExportResult>? exportCompleted = null)
    {
        _clipEditor = clipEditor;
        _clipExports = clipExports;
        _exportCompleted = exportCompleted;
    }

    public async Task LoadClipAsync(ClipMetadata clip, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (_retired) return;

        _loadCancellation?.Cancel();
        var load = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loadCancellation = load;
        var revision = ++_loadRevision;
        _sourceClip = null;
        _playWhenReady = false;
        Source = null;
        Duration = TimeSpan.Zero;
        TrimStart = TimeSpan.Zero;
        TrimEnd = TimeSpan.Zero;
        Position = TimeSpan.Zero;
        ThumbnailFrames = [];
        IsPlaying = false;
        IsTrimming = false;
        TrimStatus = "Loading clip...";
        RefreshCommands();

        try
        {
            var duration = await _clipEditor.GetDurationAsync(clip.FilePath, load.Token);
            if (_retired || revision != _loadRevision || load.IsCancellationRequested) return;
            if (duration <= TimeSpan.Zero)
            {
                TrimStatus = "Clip duration unavailable. Export unavailable.";
                return;
            }

            _sourceClip = clip;
            Source = new Uri(clip.FilePath, UriKind.RelativeOrAbsolute);
            Duration = duration;
            TrimEnd = duration;
            IsPlaying = _playWhenReady;
            TrimStatus = string.Empty;
            RefreshCommands();

            try
            {
                var frames = await _clipEditor.GenerateThumbnailStripAsync(clip.FilePath, 20, 160, load.Token);
                if (!_retired && revision == _loadRevision && !load.IsCancellationRequested)
                    ThumbnailFrames = new List<string>(frames);
            }
            catch (OperationCanceledException) when (load.IsCancellationRequested)
            {
                // A replaced selection owns the current preview.
            }
            catch
            {
                if (!_retired && revision == _loadRevision)
                    ThumbnailFrames = [];
            }
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested)
        {
            // A replaced or retired selection owns the current preview.
        }
        catch
        {
            if (!_retired && revision == _loadRevision)
                TrimStatus = "Clip could not be loaded. Export unavailable.";
        }
        finally
        {
            if (ReferenceEquals(_loadCancellation, load))
                _loadCancellation = null;
            load.Dispose();
        }
    }

    public void Retire()
    {
        if (_retired) return;
        _retired = true;
        _loadRevision++;
        _playWhenReady = false;
        _loadCancellation?.Cancel();
        ExecuteTrimCommand.Cancel();
        RefreshCommands();
    }

    private bool CanStartTrim() => !_retired && _sourceClip is not null && Duration > TimeSpan.Zero && !IsExporting;

    private bool CanExecuteTrim() => CanStartTrim() &&
        TrimStart >= TimeSpan.Zero && TrimStart < TrimEnd && TrimEnd <= Duration;

    private void RefreshCommands()
    {
        StartTrimCommand.NotifyCanExecuteChanged();
        ExecuteTrimCommand.NotifyCanExecuteChanged();
    }

    partial void OnDurationChanged(TimeSpan value) => RefreshCommands();

    partial void OnTrimStartChanged(TimeSpan value)
    {
        RefreshCommands();
        UpdateRangeStatus();
    }

    partial void OnTrimEndChanged(TimeSpan value)
    {
        RefreshCommands();
        UpdateRangeStatus();
    }

    private void UpdateRangeStatus()
    {
        if (_sourceClip is null || IsExporting) return;
        const string invalid = "Choose a valid trim range within the clip duration.";
        if (!CanExecuteTrim())
            TrimStatus = invalid;
        else if (TrimStatus == invalid)
            TrimStatus = string.Empty;
    }

    partial void OnIsExportingChanged(bool value) => RefreshCommands();

    [RelayCommand]
    private void TogglePlayPause()
    {
        if (_retired) return;
        if (_sourceClip is null)
        {
            if (_loadCancellation is not null)
                _playWhenReady = !_playWhenReady;
            return;
        }

        IsPlaying = !IsPlaying;
    }

    [RelayCommand(CanExecute = nameof(CanStartTrim))]
    private void StartTrim()
    {
        IsTrimming = true;
        TrimStatus = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanExecuteTrim))]
    private async Task ExecuteTrimAsync(CancellationToken ct)
    {
        if (!CanExecuteTrim()) return;
        var source = _sourceClip!;
        var start = TrimStart;
        var end = TrimEnd;
        var revision = _loadRevision;

        IsExporting = true;
        TrimStatus = "Exporting clip...";
        try
        {
            var export = await _clipExports.ExportAsync(source, start, end, ct);
            try
            {
                _exportCompleted?.Invoke(source, export);
            }
            catch
            {
                // A presentation callback cannot change the coordinator's published result.
            }
            if (_retired || revision != _loadRevision) return;
            var name = Path.GetFileName(export.OutputPath);
            TrimStatus = export.IngestionDisposition switch
            {
                CompletedMediaIngestionDisposition.Indexed => $"Exported: {name}. Library indexed.",
                CompletedMediaIngestionDisposition.PendingRetry => $"Exported: {name}. Library indexing pending retry.",
                _ => $"Exported: {name}. Library indexing rejected; check the output and retry import.",
            };
            IsTrimming = false;
        }
        catch (OperationCanceledException)
        {
            if (!_retired && revision == _loadRevision)
                TrimStatus = "Export canceled; no output was confirmed. Check output and library before retrying.";
        }
        catch (InvalidOperationException)
        {
            if (!_retired && revision == _loadRevision)
                TrimStatus = "Export unavailable. Check output and library before retrying.";
        }
        catch
        {
            if (!_retired && revision == _loadRevision)
                TrimStatus = "Export outcome could not be confirmed. Check output and library before retrying.";
        }
        finally
        {
            IsExporting = false;
        }
    }

    [RelayCommand]
    private void CancelTrim()
    {
        if (_retired) return;
        if (IsExporting)
        {
            TrimStatus = "Cancellation requested; waiting for export outcome.";
            ExecuteTrimCommand.Cancel();
            return;
        }

        IsTrimming = false;
        TrimStatus = string.Empty;
    }
}
