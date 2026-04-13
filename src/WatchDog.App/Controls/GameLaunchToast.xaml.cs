using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WatchDog.Core.GameDetection;
using WatchDog.Core.Settings;

namespace WatchDog.App.Controls;

/// <summary>
/// Non-blocking toast shown when a new game is detected without a saved recording profile.
/// Offers mode selection (Highlight / Session / Replay Buffer) with auto-dismiss.
/// </summary>
public partial class GameLaunchToast : Window
{
    private const int AutoDismissSeconds = 15;
    private const int FadeOutMs = 300;
    private readonly DispatcherTimer _countdownTimer;
    private int _secondsRemaining = AutoDismissSeconds;
    private GameRecordingMode _selectedMode = GameRecordingMode.ReplayBuffer;
    private bool _eventFired;

    /// <summary>Fires when the user selects a mode (or auto-dismiss triggers).</summary>
    public event Action<GameRecordingMode, bool>? ModeSelected; // (mode, remember)

    public GameLaunchToast(GameInfo game, bool highlightsAvailable, bool isAiFallback, string? caveat)
    {
        InitializeComponent();

        GameNameText.Text = $"\U0001f3ae {game.DisplayName} detected";

        if (highlightsAvailable && caveat is null)
        {
            RecommendationText.Text = isAiFallback
                ? "AI highlights available for this game."
                : "Highlight detection supported.";
        }
        else if (caveat is not null)
        {
            RecommendationText.Text = $"{caveat}\nRecommended: Session recording or Replay buffer.";
        }
        else
        {
            RecommendationText.Text = "Choose a recording mode.";
        }

        // Add mode buttons based on genre compatibility
        if (highlightsAvailable && caveat is null)
        {
            var label = isAiFallback ? "Highlights (AI)" : "Highlights";
            AddModeButton(label, GameRecordingMode.Highlight);
        }

        AddModeButton("Session", GameRecordingMode.SessionRecording);
        AddModeButton("Replay Buffer", GameRecordingMode.ReplayBuffer);

        // Position near system tray (bottom-right)
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - 360;
        Top = workArea.Bottom - 200;

        // Countdown timer — auto-dismiss to Replay Buffer after 15s
        CountdownText.Text = $"{_secondsRemaining}s";
        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += OnCountdownTick;
        _countdownTimer.Start();

        // Safety net: stop timer if window is closed externally (app shutdown, duplicate toast)
        Closed += (_, _) => _countdownTimer.Stop();
    }

    private void AddModeButton(string label, GameRecordingMode mode)
    {
        var btn = new Button
        {
            Content = label,
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 8, 0),
            FontFamily = (FontFamily)FindResource("UIFont"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = (Brush)FindResource("SurfaceBrush"),
            Foreground = (Brush)FindResource("TextBrush"),
            BorderBrush = (Brush)FindResource("Surface1Brush"),
            BorderThickness = new Thickness(1),
        };

        btn.Click += (_, _) => SelectMode(mode);
        ModeButtonsPanel.Children.Add(btn);
    }

    private void SelectMode(GameRecordingMode mode)
    {
        // Guard: prevent double-fire from countdown + button click race
        if (_eventFired) return;
        _eventFired = true;

        _countdownTimer.Stop();
        _selectedMode = mode;
        var remember = RememberCheckBox.IsChecked == true;
        ModeSelected?.Invoke(_selectedMode, remember);
        FadeOutAndClose();
    }

    private void OnCountdownTick(object? sender, EventArgs e)
    {
        _secondsRemaining--;
        CountdownText.Text = $"{_secondsRemaining}s";

        if (_secondsRemaining <= 0)
        {
            // Unified path through SelectMode — guarded by _eventFired
            SelectMode(GameRecordingMode.ReplayBuffer);
        }
    }

    private void FadeOutAndClose()
    {
        _countdownTimer.Stop();
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(FadeOutMs));
        fadeOut.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fadeOut);
    }
}
