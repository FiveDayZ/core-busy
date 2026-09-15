namespace CoreBusy.App.ViewModels.Dashboard;

using System.Windows.Media;
using CoreBusy.App.Controls;
using CoreBusy.App.Infrastructure;
using CoreBusy.Core.Models;

/// <summary>
/// 物理核内单条逻辑线程（SMT）的视图模型：驱动 Tile 内那一行细线程条。
/// 线程对象在构建时一次性建好、之后只更新数值，不重建也不新增元素。
/// </summary>
public sealed class CoreThreadVm : ObservableObject
{
    private double _usage;
    private string _usageText = "0%";
    private Brush _usageBrush = UsagePalette.UsageBrush(0);

    public CoreThreadVm(int index)
    {
        Index = index;
        Label = $"T{index}";
    }

    /// <summary>核内线程序号（0 / 1）。</summary>
    public int Index { get; }

    /// <summary>线程条左侧标签（T0 / T1）。</summary>
    public string Label { get; }

    /// <summary>使用率（0-100）。</summary>
    public double Usage
    {
        get => _usage;
        private set => SetProperty(ref _usage, value);
    }

    /// <summary>使用率文本（如 "26%"）。</summary>
    public string UsageText
    {
        get => _usageText;
        private set => SetProperty(ref _usageText, value);
    }

    /// <summary>线程条填充色（品牌色阶分档）。</summary>
    public Brush UsageBrush
    {
        get => _usageBrush;
        private set => SetProperty(ref _usageBrush, value);
    }

    /// <summary>应用**实时**线程快照。</summary>
    public void ApplyLive(CpuThreadSnapshot snapshot)
    {
        Usage = snapshot.UsagePercent;
        UsageText = $"{snapshot.UsagePercent:0}%";
        UsageBrush = UsagePalette.UsageBrush(snapshot.UsagePercent);
    }

    /// <summary>
    /// 应用**累积**线程均值。线程条与核心卡走同一套口径：
    /// 核心卡显示核的累积均值，条上逐条显示该核内各线程的累积均值。
    /// </summary>
    public void ApplyCumulative(double averagePercent)
    {
        Usage = averagePercent;
        UsageText = $"{averagePercent:0}%";
        UsageBrush = UsagePalette.UsageBrush(averagePercent);
    }
}
