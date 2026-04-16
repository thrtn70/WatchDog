using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WatchDog.App.ViewModels;
using WatchDog.Native.Win32;

namespace WatchDog.App.Controls;

public partial class WindowPickerPopup : Window
{
    private readonly ManualCaptureViewModel _viewModel;
    private bool _activated;

    public WindowPickerPopup(ManualCaptureViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;

        RefreshButton.Click += (_, _) => RefreshWindowList();
        WindowList.SelectionChanged += OnSelectionChanged;
        WindowList.KeyDown += OnWindowListKeyDown;

        RefreshWindowList();
    }

    private void RefreshWindowList()
    {
        _viewModel.RefreshWindowsCommand.Execute(null);
        WindowList.ItemsSource = _viewModel.WindowList;
    }

    /// <summary>Single-click confirms selection (standard picker UX).</summary>
    private async void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (WindowList.SelectedItem is WindowEnumerator.WindowInfo window)
            {
                Close();
                await _viewModel.SelectWindowCommand.ExecuteAsync(window);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Window selection failed: {ex.Message}");
        }
    }

    private async void OnWindowListKeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            if (e.Key == Key.Enter && WindowList.SelectedItem is WindowEnumerator.WindowInfo window)
            {
                Close();
                await _viewModel.SelectWindowCommand.ExecuteAsync(window);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Window selection failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Close the popup when it loses focus (click outside).
    /// Guard against premature close during the Show/Activate sequence.
    /// </summary>
    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_activated)
            Close();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _activated = true;
    }

    /// <summary>Position the popup near the system tray area.</summary>
    public void ShowNearTray()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 12;
        Top = workArea.Bottom - Height - 12;
        Show();
        Activate();
    }
}
