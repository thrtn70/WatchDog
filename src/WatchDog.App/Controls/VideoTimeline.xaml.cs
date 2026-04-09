using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WatchDog.App.Controls;

public partial class VideoTimeline : UserControl
{
    private enum DragMode { None, TrimStart, TrimEnd, Seek }

    private DragMode _dragMode = DragMode.None;

    public static readonly DependencyProperty DurationProperty =
        DependencyProperty.Register(nameof(Duration), typeof(TimeSpan), typeof(VideoTimeline),
            new PropertyMetadata(TimeSpan.Zero, OnLayoutPropertyChanged));

    public static readonly DependencyProperty PositionProperty =
        DependencyProperty.Register(nameof(Position), typeof(TimeSpan), typeof(VideoTimeline),
            new FrameworkPropertyMetadata(TimeSpan.Zero, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnLayoutPropertyChanged));

    public static readonly DependencyProperty TrimStartProperty =
        DependencyProperty.Register(nameof(TrimStart), typeof(TimeSpan), typeof(VideoTimeline),
            new FrameworkPropertyMetadata(TimeSpan.Zero, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnLayoutPropertyChanged));

    public static readonly DependencyProperty TrimEndProperty =
        DependencyProperty.Register(nameof(TrimEnd), typeof(TimeSpan), typeof(VideoTimeline),
            new FrameworkPropertyMetadata(TimeSpan.Zero, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnLayoutPropertyChanged));

    public static readonly DependencyProperty ThumbnailFramesProperty =
        DependencyProperty.Register(nameof(ThumbnailFrames), typeof(IReadOnlyList<string>), typeof(VideoTimeline),
            new PropertyMetadata(null, OnThumbnailFramesChanged));

    public TimeSpan Duration
    {
        get => (TimeSpan)GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    public TimeSpan Position
    {
        get => (TimeSpan)GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    public TimeSpan TrimStart
    {
        get => (TimeSpan)GetValue(TrimStartProperty);
        set => SetValue(TrimStartProperty, value);
    }

    public TimeSpan TrimEnd
    {
        get => (TimeSpan)GetValue(TrimEndProperty);
        set => SetValue(TrimEndProperty, value);
    }

    public IReadOnlyList<string>? ThumbnailFrames
    {
        get => (IReadOnlyList<string>?)GetValue(ThumbnailFramesProperty);
        set => SetValue(ThumbnailFramesProperty, value);
    }

    public VideoTimeline()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateLayout();
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((VideoTimeline)d).UpdateLayout();
    }

    private static void OnThumbnailFramesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (VideoTimeline)d;
        control.ThumbnailStrip.ItemsSource = e.NewValue as IReadOnlyList<string>;
    }

    private new void UpdateLayout()
    {
        var width = OverlayCanvas.ActualWidth;
        if (width <= 0 || Duration.TotalSeconds <= 0) return;

        var startFraction = TrimStart.TotalSeconds / Duration.TotalSeconds;
        var endFraction = TrimEnd.TotalSeconds / Duration.TotalSeconds;
        var posFraction = Position.TotalSeconds / Duration.TotalSeconds;

        // Clamp fractions
        startFraction = Math.Clamp(startFraction, 0, 1);
        endFraction = Math.Clamp(endFraction, 0, 1);
        posFraction = Math.Clamp(posFraction, 0, 1);

        var startX = startFraction * width;
        var endX = endFraction * width;
        var posX = posFraction * width;

        // Left exclusion zone
        LeftExclusion.Width = Math.Max(0, startX);

        // Right exclusion zone
        Canvas.SetLeft(RightExclusion, endX);
        RightExclusion.Width = Math.Max(0, width - endX);

        // Trim handles
        Canvas.SetLeft(StartHandle, Math.Max(0, startX - 3));
        Canvas.SetLeft(EndHandle, Math.Min(width - 6, endX - 3));

        // Playhead
        Canvas.SetLeft(Playhead, Math.Clamp(posX - 1, 0, width - 2));

        // Time labels
        StartTimeLabel.Text = FormatTime(TrimStart);
        EndTimeLabel.Text = FormatTime(TrimEnd);
    }

    private void OnStartHandleMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragMode = DragMode.TrimStart;
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void OnEndHandleMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragMode = DragMode.TrimEnd;
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_dragMode != DragMode.None) return;

        // Click on track to seek
        _dragMode = DragMode.Seek;
        OverlayCanvas.CaptureMouse();
        SeekToMousePosition(e);
        e.Handled = true;
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragMode == DragMode.None || e.LeftButton != MouseButtonState.Pressed) return;

        var width = OverlayCanvas.ActualWidth;
        if (width <= 0 || Duration.TotalSeconds <= 0) return;

        var x = e.GetPosition(OverlayCanvas).X;
        var fraction = Math.Clamp(x / width, 0, 1);
        var time = TimeSpan.FromSeconds(fraction * Duration.TotalSeconds);

        switch (_dragMode)
        {
            case DragMode.TrimStart:
                // Constrain: start must be before end (with minimum 0.5s gap)
                var maxStart = TrimEnd - TimeSpan.FromSeconds(0.5);
                if (time < TimeSpan.Zero) time = TimeSpan.Zero;
                if (time > maxStart) time = maxStart;
                TrimStart = time;
                break;

            case DragMode.TrimEnd:
                // Constrain: end must be after start (with minimum 0.5s gap)
                var minEnd = TrimStart + TimeSpan.FromSeconds(0.5);
                if (time > Duration) time = Duration;
                if (time < minEnd) time = minEnd;
                TrimEnd = time;
                break;

            case DragMode.Seek:
                Position = time;
                break;
        }
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragMode == DragMode.TrimStart || _dragMode == DragMode.TrimEnd)
        {
            // Release capture from the handle
            StartHandle.ReleaseMouseCapture();
            EndHandle.ReleaseMouseCapture();
        }
        else
        {
            OverlayCanvas.ReleaseMouseCapture();
        }

        _dragMode = DragMode.None;
    }

    private void SeekToMousePosition(MouseButtonEventArgs e)
    {
        var width = OverlayCanvas.ActualWidth;
        if (width <= 0 || Duration.TotalSeconds <= 0) return;

        var x = e.GetPosition(OverlayCanvas).X;
        var fraction = Math.Clamp(x / width, 0, 1);
        Position = TimeSpan.FromSeconds(fraction * Duration.TotalSeconds);
    }

    private static string FormatTime(TimeSpan ts)
    {
        return ts.TotalHours >= 1
            ? ts.ToString(@"h\:mm\:ss")
            : ts.ToString(@"m\:ss");
    }
}
