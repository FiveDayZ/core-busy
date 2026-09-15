namespace CoreBusy.App.ViewModels;

using System.Collections.ObjectModel;
using CoreBusy.App.Infrastructure;
using CoreBusy.Core;
using CoreBusy.Core.Models;

/// <summary>热力图单元格：单样本的负载色块。</summary>
public sealed class HeatCellViewModel : ObservableObject
{
    private System.Windows.Media.Brush _brush = StatusPalette.Background(CoreStatus.Slacking);

    /// <summary>色块填充（与核心 Tile 同一套冷热配色）。</summary>
    public System.Windows.Media.Brush CellBrush
    {
        get => _brush;
        private set => SetProperty(ref _brush, value);
    }

    internal void SetUsage(double usagePercent)
        => CellBrush = StatusPalette.Background(CoreStatusClassifier.Classify(usagePercent));
}

/// <summary>热力图单行：一个核心最近 60 秒的负载色带（方案 3.3 节），最右为最新。</summary>
public sealed class HeatmapRowViewModel : ObservableObject
{
    public const int SampleCapacity = 60;

    /// <summary>核心显示名，如 "P0"。</summary>
    public string DisplayName { get; }

    /// <summary>固定容量 60 的样本色带。</summary>
    public ObservableCollection<HeatCellViewModel> Cells { get; } = new();

    public HeatmapRowViewModel(string displayName)
    {
        DisplayName = displayName;
        for (var i = 0; i < SampleCapacity; i++)
            Cells.Add(new HeatCellViewModel());
    }

    /// <summary>推入一个新样本：最旧样本出队、更新颜色后从右端入队。</summary>
    public void Push(double usagePercent)
    {
        var cell = Cells[0];
        Cells.RemoveAt(0);
        cell.SetUsage(usagePercent);
        Cells.Add(cell);
    }
}
