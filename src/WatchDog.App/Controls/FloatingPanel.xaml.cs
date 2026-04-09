using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace WatchDog.App.Controls;

/// <summary>
/// A reusable floating panel container with drag header, resize grip, and collapse toggle.
/// Host inside a Canvas and bind PanelLeft/PanelTop for absolute positioning.
/// </summary>
[System.Windows.Markup.ContentProperty(nameof(PanelContent))]
public partial class FloatingPanel : UserControl
{
    private bool _isDragging;
    private Point _dragOffset;

    public FloatingPanel()
    {
        InitializeComponent();
    }

    #region Dependency Properties

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(FloatingPanel),
            new PropertyMetadata("Panel"));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DependencyProperty PanelContentProperty =
        DependencyProperty.Register(nameof(PanelContent), typeof(object), typeof(FloatingPanel));

    public object PanelContent
    {
        get => GetValue(PanelContentProperty);
        set => SetValue(PanelContentProperty, value);
    }

    public static readonly DependencyProperty PanelLeftProperty =
        DependencyProperty.Register(nameof(PanelLeft), typeof(double), typeof(FloatingPanel),
            new PropertyMetadata(0.0, OnPositionChanged));

    public double PanelLeft
    {
        get => (double)GetValue(PanelLeftProperty);
        set => SetValue(PanelLeftProperty, value);
    }

    public static readonly DependencyProperty PanelTopProperty =
        DependencyProperty.Register(nameof(PanelTop), typeof(double), typeof(FloatingPanel),
            new PropertyMetadata(0.0, OnPositionChanged));

    public double PanelTop
    {
        get => (double)GetValue(PanelTopProperty);
        set => SetValue(PanelTopProperty, value);
    }

    public static readonly DependencyProperty PanelWidthProperty =
        DependencyProperty.Register(nameof(PanelWidth), typeof(double), typeof(FloatingPanel),
            new PropertyMetadata(200.0, OnSizeChanged));

    public double PanelWidth
    {
        get => (double)GetValue(PanelWidthProperty);
        set => SetValue(PanelWidthProperty, value);
    }

    public static readonly DependencyProperty PanelHeightProperty =
        DependencyProperty.Register(nameof(PanelHeight), typeof(double), typeof(FloatingPanel),
            new PropertyMetadata(150.0, OnSizeChanged));

    public double PanelHeight
    {
        get => (double)GetValue(PanelHeightProperty);
        set => SetValue(PanelHeightProperty, value);
    }

    public static readonly DependencyProperty MinPanelWidthProperty =
        DependencyProperty.Register(nameof(MinPanelWidth), typeof(double), typeof(FloatingPanel),
            new PropertyMetadata(120.0));

    public double MinPanelWidth
    {
        get => (double)GetValue(MinPanelWidthProperty);
        set => SetValue(MinPanelWidthProperty, value);
    }

    public static readonly DependencyProperty MinPanelHeightProperty =
        DependencyProperty.Register(nameof(MinPanelHeight), typeof(double), typeof(FloatingPanel),
            new PropertyMetadata(80.0));

    public double MinPanelHeight
    {
        get => (double)GetValue(MinPanelHeightProperty);
        set => SetValue(MinPanelHeightProperty, value);
    }

    public static readonly DependencyProperty IsCollapsedProperty =
        DependencyProperty.Register(nameof(IsCollapsed), typeof(bool), typeof(FloatingPanel),
            new PropertyMetadata(false, OnIsCollapsedChanged));

    public bool IsCollapsed
    {
        get => (bool)GetValue(IsCollapsedProperty);
        set => SetValue(IsCollapsedProperty, value);
    }

    #endregion

    #region Property Changed Callbacks

    private static void OnPositionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FloatingPanel panel)
        {
            Canvas.SetLeft(panel, panel.PanelLeft);
            Canvas.SetTop(panel, panel.PanelTop);
        }
    }

    private static void OnSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FloatingPanel panel)
        {
            panel.Width = panel.PanelWidth;
            panel.Height = panel.IsCollapsed ? double.NaN : panel.PanelHeight;
        }
    }

    private static void OnIsCollapsedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FloatingPanel panel)
        {
            var collapsed = (bool)e.NewValue;
            panel.ContentArea.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            panel.ResizeGrip.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            panel.CollapseIcon.Text = collapsed ? "\u25B6" : "\u25BC"; // right-arrow : down-arrow
            panel.Height = collapsed ? double.NaN : panel.PanelHeight;
            panel.Width = panel.PanelWidth;
        }
    }

    #endregion

    #region Drag Behavior

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = true;
        _dragOffset = e.GetPosition(this);
        CaptureMouse(); // Capture on FloatingPanel for reliable drag during fast movement
        e.Handled = true;
    }

    private void Header_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;

        var canvas = FindParentCanvas();
        if (canvas is null) return;

        var mousePos = e.GetPosition(canvas);
        var newLeft = mousePos.X - _dragOffset.X;
        var newTop = mousePos.Y - _dragOffset.Y;

        // Clamp to canvas bounds
        newLeft = Math.Max(0, Math.Min(newLeft, canvas.ActualWidth - ActualWidth));
        newTop = Math.Max(0, Math.Min(newTop, canvas.ActualHeight - ActualHeight));

        PanelLeft = newLeft;
        PanelTop = newTop;
    }

    private void Header_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDragging = false;
        ReleaseMouseCapture();
    }

    #endregion

    #region Resize Behavior

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var newWidth = PanelWidth + e.HorizontalChange;
        var newHeight = PanelHeight + e.VerticalChange;

        // Enforce minimums
        PanelWidth = Math.Max(MinPanelWidth, newWidth);
        PanelHeight = Math.Max(MinPanelHeight, newHeight);

        // Clamp to canvas bounds
        var canvas = FindParentCanvas();
        if (canvas is not null)
        {
            var maxWidth = canvas.ActualWidth - PanelLeft;
            var maxHeight = canvas.ActualHeight - PanelTop;
            PanelWidth = Math.Min(PanelWidth, maxWidth);
            PanelHeight = Math.Min(PanelHeight, maxHeight);
        }
    }

    #endregion

    #region Collapse

    private void CollapseToggle_Click(object sender, RoutedEventArgs e)
    {
        IsCollapsed = !IsCollapsed;
    }

    #endregion

    private Canvas? FindParentCanvas()
    {
        DependencyObject current = this;
        while (current is not null)
        {
            if (current is Canvas canvas) return canvas;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    protected override void OnVisualParentChanged(DependencyObject oldParent)
    {
        base.OnVisualParentChanged(oldParent);
        if (System.Windows.Media.VisualTreeHelper.GetParent(this) is null) return;

        // Apply initial position and size when added to visual tree
        Canvas.SetLeft(this, PanelLeft);
        Canvas.SetTop(this, PanelTop);
        Width = PanelWidth;
        Height = IsCollapsed ? double.NaN : PanelHeight;
    }
}
