using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WatchDog.Core.Hotkeys;

namespace WatchDog.App.Controls;

public partial class HotkeyRecorderControl : UserControl
{
    private bool _isRecording;

    public static readonly DependencyProperty VirtualKeyProperty =
        DependencyProperty.Register(
            nameof(VirtualKey),
            typeof(int),
            typeof(HotkeyRecorderControl),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnHotkeyPropertyChanged));

    public static readonly DependencyProperty ModifiersProperty =
        DependencyProperty.Register(
            nameof(Modifiers),
            typeof(uint),
            typeof(HotkeyRecorderControl),
            new FrameworkPropertyMetadata(0u, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnHotkeyPropertyChanged));

    public static readonly DependencyProperty DisplayTextProperty =
        DependencyProperty.Register(
            nameof(DisplayText),
            typeof(string),
            typeof(HotkeyRecorderControl),
            new PropertyMetadata(string.Empty));

    public int VirtualKey
    {
        get => (int)GetValue(VirtualKeyProperty);
        set => SetValue(VirtualKeyProperty, value);
    }

    public uint Modifiers
    {
        get => (uint)GetValue(ModifiersProperty);
        set => SetValue(ModifiersProperty, value);
    }

    public string DisplayText
    {
        get => (string)GetValue(DisplayTextProperty);
        private set => SetValue(DisplayTextProperty, value);
    }

    public HotkeyRecorderControl()
    {
        InitializeComponent();
        UpdateDisplayText();
    }

    private static void OnHotkeyPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HotkeyRecorderControl control)
            control.UpdateDisplayText();
    }

    private void UpdateDisplayText()
    {
        DisplayText = VirtualKey == 0
            ? "None"
            : HotkeyConfig.FormatDisplay(VirtualKey, Modifiers);
    }

    private void OnBorderClick(object sender, MouseButtonEventArgs e)
    {
        EnterRecordingMode();
        e.Handled = true;
    }

    private void EnterRecordingMode()
    {
        if (_isRecording) return;

        _isRecording = true;
        // Set via the DP so the binding stays intact (direct TextBlock.Text = breaks it)
        DisplayText = "Press a key...";
        DisplayTextBlock.Foreground = (Brush)FindResource("TextBrush");
        RecorderBorder.BorderBrush = (Brush)FindResource("AccentBrush");
        RecorderBorder.BorderThickness = new Thickness(2);

        Focus();
        Keyboard.Focus(this);
    }

    private void ExitRecordingMode()
    {
        _isRecording = false;
        DisplayTextBlock.Foreground = (Brush)FindResource("AccentBrush");
        RecorderBorder.BorderBrush = (Brush)FindResource("Surface1Brush");
        RecorderBorder.BorderThickness = new Thickness(1);

        UpdateDisplayText();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!_isRecording)
        {
            base.OnKeyDown(e);
            return;
        }

        e.Handled = true;

        // Escape cancels recording
        if (e.Key == Key.Escape)
        {
            ExitRecordingMode();
            return;
        }

        // Ignore standalone modifier presses
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin)
        {
            return;
        }

        // Convert WPF Key to Win32 virtual key code
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);

        // Build Win32 modifier flags
        uint modifiers = 0;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) modifiers |= 0x0002;
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) modifiers |= 0x0001;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) modifiers |= 0x0004;
        if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) modifiers |= 0x0008;

        VirtualKey = virtualKey;
        Modifiers = modifiers;
        ExitRecordingMode();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);

        if (_isRecording)
            ExitRecordingMode();
    }
}
