using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TikrClipr.Core.Storage;

namespace TikrClipr.App.ViewModels;

public partial class StorageDashboardViewModel : ObservableObject
{
    private readonly IClipStorage _clipStorage;
    private readonly StorageConfig _config;

    [ObservableProperty] private int _totalClips;
    [ObservableProperty] private string _usedDisplay = "0 GB";
    [ObservableProperty] private string _remainingDisplay = "0 GB";
    [ObservableProperty] private double _usagePercent;
    [ObservableProperty] private string _usagePercentDisplay = "0%";
    [ObservableProperty] private ObservableCollection<GameStorageUsage> _gameUsages = [];

    public StorageDashboardViewModel(IClipStorage clipStorage, StorageConfig config)
    {
        _clipStorage = clipStorage;
        _config = config;
        Refresh();
    }

    [RelayCommand]
    private void Refresh()
    {
        var report = StorageAnalytics.Analyze(_clipStorage, _config);

        TotalClips = report.TotalClips;
        UsedDisplay = $"{report.TotalGb:F2} GB";
        RemainingDisplay = $"{report.RemainingGb:F2} GB";
        UsagePercent = report.UsagePercent;
        UsagePercentDisplay = $"{report.UsagePercent:F1}%";
        GameUsages = new ObservableCollection<GameStorageUsage>(report.ByGame);
    }
}
