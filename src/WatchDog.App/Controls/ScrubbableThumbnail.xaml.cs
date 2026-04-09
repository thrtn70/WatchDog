using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using WatchDog.App.Services;

namespace WatchDog.App.Controls;

public partial class ScrubbableThumbnail : UserControl
{
    private BitmapImage[]? _stripFrames;
    private CancellationTokenSource? _loadCts;
    private bool _isLoading;
    private bool _isScrubbing;

    public static readonly DependencyProperty StaticThumbnailProperty =
        DependencyProperty.Register(nameof(StaticThumbnail), typeof(ImageSource), typeof(ScrubbableThumbnail),
            new PropertyMetadata(null, OnStaticThumbnailChanged));

    public static readonly DependencyProperty ClipFilePathProperty =
        DependencyProperty.Register(nameof(ClipFilePath), typeof(string), typeof(ScrubbableThumbnail));

    public ImageSource? StaticThumbnail
    {
        get => (ImageSource?)GetValue(StaticThumbnailProperty);
        set => SetValue(StaticThumbnailProperty, value);
    }

    public string? ClipFilePath
    {
        get => (string?)GetValue(ClipFilePathProperty);
        set => SetValue(ClipFilePathProperty, value);
    }

    public ScrubbableThumbnail()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
    }

    private static void OnStaticThumbnailChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrubbableThumbnail control)
            control.StaticImage.Source = e.NewValue as ImageSource;
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);

        var path = ClipFilePath;
        if (string.IsNullOrEmpty(path))
            return;

        var cache = App.Services.GetService<ThumbnailStripCache>();
        if (cache is null)
            return;

        // Check cache first — instant if already generated
        _stripFrames = cache.TryGetCached(path);
        if (_stripFrames is not null)
        {
            EnterScrubMode(e);
            return;
        }

        // First hover — generate strip in background
        _isLoading = true;
        LoadingOverlay.Visibility = Visibility.Visible;

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        _ = LoadStripAsync(cache, path, ct);
    }

    private async Task LoadStripAsync(ThumbnailStripCache cache, string path, CancellationToken ct)
    {
        try
        {
            var frames = await cache.GetOrGenerateAsync(path, ct);

            if (ct.IsCancellationRequested)
                return;

            await Dispatcher.InvokeAsync(() =>
            {
                if (!IsLoaded)
                    return;

                _isLoading = false;
                LoadingOverlay.Visibility = Visibility.Collapsed;

                if (frames is null || !IsMouseOver)
                    return;

                _stripFrames = frames;

                // Show frame under current cursor position immediately
                var pos = Mouse.GetPosition(RootGrid);
                EnterScrubModeAtPosition(pos);
            });
        }
        catch (OperationCanceledException)
        {
            // Expected on mouse leave during generation
        }
        catch
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (!IsLoaded) return;
                _isLoading = false;
                LoadingOverlay.Visibility = Visibility.Collapsed;
            });
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (!_isScrubbing || _stripFrames is null || _stripFrames.Length == 0)
            return;

        var pos = e.GetPosition(RootGrid);
        ShowFrameAtPosition(pos);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        ExitScrubMode();

        if (_isLoading)
        {
            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _loadCts = null;
            _isLoading = false;
            LoadingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void EnterScrubMode(MouseEventArgs e)
    {
        _isScrubbing = true;
        ScrubImage.Visibility = Visibility.Visible;
        ScrubBar.Visibility = Visibility.Visible;
        ShowFrameAtPosition(e.GetPosition(RootGrid));
    }

    private void EnterScrubModeAtPosition(Point pos)
    {
        _isScrubbing = true;
        ScrubImage.Visibility = Visibility.Visible;
        ScrubBar.Visibility = Visibility.Visible;
        ShowFrameAtPosition(pos);
    }

    private void ShowFrameAtPosition(Point pos)
    {
        if (_stripFrames is null || _stripFrames.Length == 0)
            return;

        var width = RootGrid.ActualWidth;
        if (width <= 0)
            return;

        var fraction = Math.Clamp(pos.X / width, 0, 1);
        var frameIndex = (int)(fraction * (_stripFrames.Length - 1));

        if (_stripFrames[frameIndex] is { } frame)
            ScrubImage.Source = frame;

        ScrubBar.Width = fraction * width;
    }

    private void ExitScrubMode()
    {
        _isScrubbing = false;
        ScrubImage.Visibility = Visibility.Collapsed;
        ScrubBar.Visibility = Visibility.Collapsed;
        ScrubBar.Width = 0;
    }
}
