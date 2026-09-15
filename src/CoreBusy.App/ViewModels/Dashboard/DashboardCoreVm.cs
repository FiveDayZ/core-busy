namespace CoreBusy.App.ViewModels.Dashboard;

using System.Windows.Media;
using CoreBusy.App.Controls;
using CoreBusy.App.Infrastructure;
using CoreBusy.App.Themes;
using CoreBusy.Core;
using CoreBusy.Core.Health;
using CoreBusy.Core.Models;

/// <summary>单个核心 Tile 的视图模型：使用率/频率/状态与高负载强调色。</summary>
public sealed class DashboardCoreVm : ObservableObject
{
    private double _usage;
    private string _usageText = "0%";
    private string _freqText = "-";
    private string _statusLabel = "空闲";
    private string _statusEmoji = "💤";
    private Brush _usageBrush = UsagePalette.UsageBrush(0);
    private Brush _usageTextBrush = UsagePalette.UsageTextBrush(0);
    private Brush _statusBrush = UsagePalette.StatusBrush(CoreStatus.Idle);
    private Brush _statusDimBrush = UsagePalette.StatusDimBrush(CoreStatus.Idle);
    private Brush _tileBorderBrush = UiTheme.TileBorderBrush;
    private Brush _tileBackground = UiTheme.TileCardBrush;

    // ── 健康度（v1.17.0）──────────────────────────────────────────────
    private string _healthText = "-";
    private string _healthTipText = "健康度：尚未收到该核心的采样";
    private Brush _healthBrush = CoreHealthPalette.Foreground(CoreHealthGrade.Unknown);
    private Brush _healthDimBrush = CoreHealthPalette.Background(CoreHealthGrade.Unknown);

    public DashboardCoreVm(string id, bool isCompact, IReadOnlyList<CpuThreadSnapshot>? threads = null)
    {
        Id = id;
        IsCompact = isCompact;

        // 仅当核心含 ≥2 条线程（SMT）时才在 Tile 内展开线程明细：
        // 单线程核的"线程"就是核心本身，再画一条等于重复。
        if (threads is { Count: > 1 })
        {
            var list = new CoreThreadVm[threads.Count];
            for (var i = 0; i < threads.Count; i++)
            {
                list[i] = new CoreThreadVm(threads[i].Index);
                list[i].ApplyLive(threads[i]);
            }

            Threads = list;
            ShowThreads = true;
        }
    }

    /// <summary>核心显示名（P0 / E2 / C5 / CCD 内连续编号）。</summary>
    public string Id { get; }

    /// <summary>是否使用紧凑卡片布局（E-Core，或分段较多时整体收一档）。</summary>
    public bool IsCompact { get; }

    /// <summary>核内线程条（SMT 双线程时为 T0/T1 两条；单线程核为空）。</summary>
    public IReadOnlyList<CoreThreadVm> Threads { get; } = [];

    /// <summary>是否在 Tile 内展开线程明细（核心含 ≥2 条逻辑线程时）。</summary>
    public bool ShowThreads { get; }

    /// <summary>
    /// 是否显示核心级聚合条。与 <see cref="ShowThreads"/> 互斥：
    /// 展开线程明细时以 T0/T1 逐条呈现，核心总量由大号百分比承担，避免信息重复。
    /// </summary>
    public bool ShowAggregateBar => !ShowThreads;

    /// <summary>使用率（0-100，SegmentBar 直接绑定）。</summary>
    public double Usage
    {
        get => _usage;
        private set => SetProperty(ref _usage, value);
    }

    /// <summary>使用率文本（如 "12%"）。</summary>
    public string UsageText
    {
        get => _usageText;
        private set => SetProperty(ref _usageText, value);
    }

    /// <summary>使用率颜色（品牌色阶分档，用于进度条）。</summary>
    public Brush UsageBrush
    {
        get => _usageBrush;
        private set => SetProperty(ref _usageBrush, value);
    }

    /// <summary>使用率数值文字颜色（低负载近白，中高负载随色阶点亮）。</summary>
    public Brush UsageTextBrush
    {
        get => _usageTextBrush;
        private set => SetProperty(ref _usageTextBrush, value);
    }

    /// <summary>频率文本（如 "5.20 GHz"）。</summary>
    public string FreqText
    {
        get => _freqText;
        private set => SetProperty(ref _freqText, value);
    }

    /// <summary>状态标签（摸鱼/空闲/工作/忙碌/高负载/爆肝）。</summary>
    public string StatusLabel
    {
        get => _statusLabel;
        private set => SetProperty(ref _statusLabel, value);
    }

    /// <summary>状态表情图标（徽章内小图标）。</summary>
    public string StatusEmoji
    {
        get => _statusEmoji;
        private set => SetProperty(ref _statusEmoji, value);
    }

    /// <summary>状态徽章文字颜色。</summary>
    public Brush StatusBrush
    {
        get => _statusBrush;
        private set => SetProperty(ref _statusBrush, value);
    }

    /// <summary>状态徽章底色（对应状态色低透明度）。</summary>
    public Brush StatusDimBrush
    {
        get => _statusDimBrush;
        private set => SetProperty(ref _statusDimBrush, value);
    }

    /// <summary>Tile 边框颜色（高负载时红色强调）。</summary>
    public Brush TileBorderBrush
    {
        get => _tileBorderBrush;
        private set => SetProperty(ref _tileBorderBrush, value);
    }

    /// <summary>Tile 背景（高负载时轻微红色调）。</summary>
    public Brush TileBackground
    {
        get => _tileBackground;
        private set => SetProperty(ref _tileBackground, value);
    }

    /// <summary>是否高负载（≥80% 红色强调档）。</summary>
    public bool IsHighLoad { get; private set; }

    /// <summary>健康度数值文本（0–100 的整数；未判定时为纯 "-"）。</summary>
    public string HealthText
    {
        get => _healthText;
        private set => SetProperty(ref _healthText, value);
    }

    /// <summary>健康度悬浮说明（含三个分项、覆盖率与代理指标声明）。</summary>
    public string HealthTipText
    {
        get => _healthTipText;
        private set => SetProperty(ref _healthTipText, value);
    }

    /// <summary>健康度文字色（随评级）。</summary>
    public Brush HealthBrush
    {
        get => _healthBrush;
        private set => SetProperty(ref _healthBrush, value);
    }

    /// <summary>健康度徽章底色（随评级，低透明度）。</summary>
    public Brush HealthDimBrush
    {
        get => _healthDimBrush;
        private set => SetProperty(ref _healthDimBrush, value);
    }

    /// <summary>
    /// 应用健康度评分（v1.17.0）。**刻意独立于 <see cref="ApplyLive"/> / <see cref="ApplyCumulative"/>**：
    /// 健康度来自窗口统计，既不随"实时 / 累积"视图切换而变，也不该被瞬时负载带着抖 ——
    /// 把它塞进那两个方法里，切视图时就会闪出一个与本视图无关的数。
    /// </summary>
    public void ApplyHealth(CoreHealthScore? score)
    {
        var grade = score?.Grade ?? CoreHealthGrade.Unknown;

        HealthText = CoreHealthFormatter.ScoreText(score);
        HealthTipText = CoreHealthFormatter.BuildCoreTooltip(score);
        HealthBrush = CoreHealthPalette.Foreground(grade);
        HealthDimBrush = CoreHealthPalette.Background(grade);
    }

    /// <summary>
    /// 应用**实时**核心快照。画刷全部取自冻结缓存，仅状态切换时才更新底色。
    /// </summary>
    public void ApplyLive(CpuCoreSnapshot snapshot)
    {
        var usage = snapshot.UsagePercent;

        Usage = usage;
        UsageText = $"{usage:0}%";
        FreqText = double.IsNaN(snapshot.FrequencyGHz) || snapshot.FrequencyGHz <= 0
            ? "-"
            : $"{snapshot.FrequencyGHz:0.00} GHz";
        UsageBrush = UsagePalette.UsageBrush(usage);
        UsageTextBrush = UsagePalette.UsageTextBrush(usage);
        StatusLabel = UsagePalette.StatusLabel(snapshot.Status);
        StatusEmoji = UsagePalette.StatusEmoji(snapshot.Status);
        StatusBrush = UsagePalette.StatusBrush(snapshot.Status);
        StatusDimBrush = UsagePalette.StatusDimBrush(snapshot.Status);

        ApplyEmphasis(usage);

        // 核内线程条：数组长度固定，逐条就地更新。
        for (var i = 0; i < Threads.Count && i < snapshot.Threads.Count; i++)
            Threads[i].ApplyLive(snapshot.Threads[i]);
    }

    /// <summary>
    /// 应用**累积**视图。同一张卡片复用实时视图的槽位，只换口径，不新增行：
    /// <list type="bullet">
    ///   <item>大号数值 / 进度条 / 左侧色条 → 累积均值（0-100，与实时同一套色阶）。</item>
    ///   <item>频率槽位 → 等效满负荷量（核·秒/核·分/核·时），给出"累计了多少"的绝对量感。</item>
    ///   <item>状态槽位 → 名次（#n / 总数），即用户要的"高低排名"，颜色随累积均值。</item>
    /// </list>
    /// 这样累积视图不需要任何新的视觉元素，切换时卡片结构完全稳定、不闪动。
    /// </summary>
    public void ApplyCumulative(
        CpuCoreSnapshot snapshot, CumulativeLoadTracker tracker, int rank, int rankTotal)
    {
        var average = tracker.AveragePercent(snapshot.Id);

        Usage = average;
        UsageText = $"{average:0}%";
        FreqText = CumulativeLoadTracker.FormatBusy(tracker.BusySeconds(snapshot.Id));
        UsageBrush = UsagePalette.UsageBrush(average);
        UsageTextBrush = UsagePalette.UsageTextBrush(average);
        // 左侧色条跟随累积均值，与卡片自身的进度条同源，避免"边缘条说实时、中间条说累积"。
        StatusBrush = UsagePalette.UsageBrush(average);
        StatusDimBrush = UsagePalette.UsageDimBrush(average);
        StatusLabel = $"#{rank} / {rankTotal}";
        StatusEmoji = "#";

        ApplyEmphasis(average);

        for (var i = 0; i < Threads.Count && i < snapshot.Threads.Count; i++)
            Threads[i].ApplyCumulative(tracker.AveragePercent(snapshot.Threads[i].Id));
    }

    /// <summary>高负载红色强调（实时与累积共用同一阈值口径）。</summary>
    private void ApplyEmphasis(double usage)
    {
        var high = usage >= 80;
        if (high == IsHighLoad)
            return;

        IsHighLoad = high;
        TileBorderBrush = high ? UiTheme.TileDangerBorderBrush : UiTheme.TileBorderBrush;
        TileBackground = high ? UiTheme.TileDangerCardBrush : UiTheme.TileCardBrush;
    }
}
