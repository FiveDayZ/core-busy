namespace CoreBusy.App.ViewModels.Dashboard;

using System.Windows.Media;
using CoreBusy.App.Controls;
using CoreBusy.App.Infrastructure;
using CoreBusy.Core.Models;

/// <summary>
/// 排行榜行视图模型（核心劳模 / 摸鱼王）。
/// 恒定 5 行，不动态生成/合并/删减；名次与核心名由 Apply 写入，行对象复用不重建列表。
/// </summary>
public sealed class RankRowVm : ObservableObject
{
    private int _rank;
    private string _coreId;
    private string _usageText = "-";
    private Brush _barBrush = UsagePalette.UsageBrush(0);
    private double _barValue;
    private readonly Brush? _fixedBrush;

    public RankRowVm(int rank, string coreId, double usage = 0, Brush? fixedBarBrush = null)
    {
        _rank = rank;
        _coreId = coreId;
        _fixedBrush = fixedBarBrush;
        Update(usage);
    }

    /// <summary>榜内名次（1-5，随排序位置变化，不代表分类编号）。</summary>
    public int Rank
    {
        get => _rank;
        private set => SetProperty(ref _rank, value);
    }

    /// <summary>核心显示名（P0 / E2 / C5；缺数据时为 "-"）。</summary>
    public string CoreId
    {
        get => _coreId;
        private set => SetProperty(ref _coreId, value);
    }

    public double Usage { get; private set; }

    public string UsageText
    {
        get => _usageText;
        private set => SetProperty(ref _usageText, value);
    }

    /// <summary>进度条颜色（劳模按占用分档，摸鱼王用固定蓝色）。</summary>
    public Brush BarBrush
    {
        get => _barBrush;
        private set => SetProperty(ref _barBrush, value);
    }

    /// <summary>进度条数值（0-100）。</summary>
    public double BarValue
    {
        get => _barValue;
        private set => SetProperty(ref _barValue, value);
    }

    /// <summary>写入名次、分类与占用率（行对象复用，避免列表重建）。</summary>
    public void Apply(int rank, string coreId, double? usage)
    {
        Rank = rank;
        CoreId = coreId;
        Update(usage);
    }

    /// <summary>更新占用率；分类缺失（null）时保留固定结构，显示 "-"。</summary>
    public void Update(double? usage)
    {
        if (usage is null)
        {
            Usage = 0;
            UsageText = "-";
            BarValue = 0;
            return;
        }

        Usage = usage.Value;
        UsageText = $"{usage.Value:0}%";
        BarValue = usage.Value;
        BarBrush = _fixedBrush ?? UsagePalette.UsageBrush(usage.Value);
    }
}
