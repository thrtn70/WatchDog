using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace TikrClipr.App.Controls;

public partial class VideoPlayerControl : UserControl
{
    private readonly DispatcherTimer _positionTimer;
    private bool _isDragging;
    private bool _isMediaLoaded;

    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.Register(nameof(Source), typeof(Uri), typeof(VideoPlayerControl),
            new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty PositionProperty =
        DependencyProperty.Register(nameof(Position), typeof(TimeSpan), typeof(VideoPlayerControl),
            new FrameworkPropertyMetadata(TimeSpan.Zero, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnPositionChanged));

    public static readonly DependencyProperty DurationProperty =
        DependencyProperty.Register(nameof(Duration), typeof(TimeSpan), typeof(VideoPlayerControl),
            new PropertyMetadata(TimeSpan.Zero));

    public static readonly DependencyProperty IsPlayingProperty =
        DependencyProperty.Register(nameof(IsPlaying), typeof(bool), typeof(VideoPlayerControl),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnIsPlayingChanged));

    public Uri? Source
    {
        get => (Uri?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public TimeSpan Position
    {
        get => (TimeSpan)GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    public TimeSpan Duration
    {
        get => (TimeSpan)GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    public bool IsPlaying
    {
        get => (bool)GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public VideoPlayerControl()
    {
        InitializeComponent();

        _positionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33) // ~30 fps
        };
        _positionTimer.Tick += OnPositionTimerTick;

        Unloaded += OnUnloaded;
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (VideoPlayerControl)d;
        control._isMediaLoaded = false;

        if (e.NewValue is Uri uri)
        {
            control.Player.Source = uri;
            control.Player.Play();
            control.Player.Pause();
        }
        else
        {
            control.Player.Stop();
            control.Player.Source = null;
            control._positionTimer.Stop();
            control.UpdatePlayPauseButton(false);
        }
    }

    private static void OnPositionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (VideoPlayerControl)d;
        if (control._isDragging || !control._isMediaLoaded) return;

        var newPos = (TimeSpan)e.NewValue;
        var currentPos = control.Player.Position;

        // Only seek if the difference is significant (avoid feedback loops)
        if (Math.Abs((newPos - currentPos).TotalMilliseconds) > 100)
        {
            control.Player.Position = newPos;
        }
    }

    private static void OnIsPlayingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (VideoPlayerControl)d;
        var playing = (bool)e.NewValue;

        if (playing)
        {
            control.Player.Play();
            control._positionTimer.Start();
        }
        else
        {
            control.Player.Pause();
            control._positionTimer.Stop();
        }

        control.UpdatePlayPauseButton(playing);
    }

    private void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        _isMediaLoaded = true;

        if (Player.NaturalDuration.HasTimeSpan)
        {
            Duration = Player.NaturalDuration.TimeSpan;
        }

        Player.Volume = VolumeSlider.Value;
        UpdateTimeDisplay();
    }

    private void OnMediaEnded(object sender, RoutedEventArgs e)
    {
        IsPlaying = false;
        Player.Position = TimeSpan.Zero;
        Position = TimeSpan.Zero;
        UpdateTimeDisplay();
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        IsPlaying = !IsPlaying;
    }

    private void OnPositionTimerTick(object? sender, EventArgs e)
    {
        if (_isDragging || !_isMediaLoaded) return;

        var pos = Player.Position;
        Position = pos;

        if (Duration.TotalSeconds > 0)
        {
            PositionSlider.Value = pos.TotalSeconds / Duration.TotalSeconds;
        }

        UpdateTimeDisplay();
    }

    private void OnSliderDragStarted(object sender, DragStartedEventArgs e)
    {
        _isDragging = true;
    }

    private void OnSliderDragCompleted(object sender, DragCompletedEventArgs e)
    {
        _isDragging = false;

        if (!_isMediaLoaded || Duration.TotalSeconds <= 0) return;

        var seekPosition = TimeSpan.FromSeconds(PositionSlider.Value * Duration.TotalSeconds);
        Player.Position = seekPosition;
        Position = seekPosition;
        UpdateTimeDisplay();
    }

    private void OnSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isDragging || !_isMediaLoaded || Duration.TotalSeconds <= 0) return;

        // Update time display during drag for visual feedback
        var previewPos = TimeSpan.FromSeconds(e.NewValue * Duration.TotalSeconds);
        UpdateTimeDisplay(previewPos);
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        Player.Volume = e.NewValue;
    }

    private void UpdatePlayPauseButton(bool isPlaying)
    {
        PlayPauseBtn.Content = isPlaying ? "\u23F8" : "\u25B6";
    }

    private void UpdateTimeDisplay(TimeSpan? overridePosition = null)
    {
        var pos = overridePosition ?? Player.Position;
        TimeDisplay.Text = $"{FormatTime(pos)} / {FormatTime(Duration)}";
    }

    private static string FormatTime(TimeSpan ts)
    {
        return ts.TotalHours >= 1
            ? ts.ToString(@"h\:mm\:ss")
            : ts.ToString(@"m\:ss");
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _positionTimer.Stop();
        Player.Stop();
        Player.Source = null;
    }
}
