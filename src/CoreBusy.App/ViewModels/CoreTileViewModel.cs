namespace CoreBusy.App.ViewModels;

using CoreBusy.App.Infrastructure;
using CoreBusy.Core;
using CoreBusy.Core.Models;

/// <summary>单个核心 Tile 的视图模型。</summary>
public sealed class CoreTileViewModel : ObservableObject
{
    private double _usagePercent;
    private CoreStatus _status;
    private string _clockText = "—";

    /// <summary>逻辑处理器编号（0 基，即 OS 逻辑处理器序号）。</summary>
    public int Index { get; }

    /// <summary>显示名，如 "P0"、"E3"（非混合平台为 P 序列）。</summary>
    public string DisplayName { get; }

    public CoreTileViewModel(int osIndex, string displayName)
    {
        Index = osIndex;
        DisplayName = displayName;
    }

    /// <summary>当前占用率（0-100）。</summary>
    public double UsagePercent
    {
        get => _usagePercent;
        private set => SetProperty(ref _usagePercent, value);
    }

    /// <summary>当前实际频率文本，如 "5.21GHz"；不可用为 "—"。</summary>
    public string ClockText
    {
        get => _clockText;
        private set => SetProperty(ref _clockText, value);
    }

    /// <summary>当前负载状态。</summary>
    public CoreStatus Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusLabel));
                OnPropertyChanged(nameof(TileBackground));
                OnPropertyChanged(nameof(TileForeground));
            }
        }
    }

    public string StatusLabel => Status.ToLabel();

    public string UsageText => $"{UsagePercent:0}%";

    public System.Windows.Media.Brush TileBackground => StatusPalette.Background(Status);
    public System.Windows.Media.Brush TileForeground => StatusPalette.Foreground(Status);

    public void Update(double usagePercent)
    {
        UsagePercent = usagePercent;
        OnPropertyChanged(nameof(UsageText));
        Status = CoreStatusClassifier.Classify(usagePercent);
    }

    /// <summary>更新频率显示（MHz 输入，null 表示不可用）。</summary>
    public void UpdateClock(double? clockMhz)
    {
        ClockText = clockMhz is double mhz && mhz > 0
            ? $"{mhz / 1000d:0.00}GHz"
            : "—";
    }
}
