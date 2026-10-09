namespace CoreBusy.App.ViewModels.Dashboard;

using CoreBusy.App.Infrastructure;
using CoreBusy.Core.Health;

/// <summary>
/// 焦点卡的展开详情（v1.22.0）：健康度三分量 + 证据 + 等级解锁。
/// </summary>
/// <remarks>
/// <para>
/// <b>诚实降级</b>是这里的硬约束：健康度任一分量读不到（NaN）就显示 "-"，
/// 绝不拿 0 冒充；整体未评分时只展示原因，不给"看起来像分数"的任何数字。
/// 这与卡角标那颗 0-100 分的口径声明一脉相承 —— 展开只是把同一份数据摊开，不新增推断。
/// </para>
/// <para>
/// 由 <c>DashboardCoreVm.ApplyHealth</c> 驱动刷新：健康度来自窗口统计、
/// 刻意不随实时/累积视图抖动，详情面板与它同一节奏。
/// </para>
/// </remarks>
public sealed class CoreFocusDetailVm : ObservableObject
{
    private string _clockText = "-";
    private string _thermalText = "-";
    private string _stabilityText = "-";
    private double _clockProgress;
    private double _thermalProgress;
    private double _stabilityProgress;
    private string _samplesText = "-";
    private string _peakText = "-";
    private string _unscoredText = string.Empty;
    private bool _hasScore;

    /// <summary>频率达成分量（0-100；NaN → "-"）。</summary>
    public string ClockText { get => _clockText; private set => SetProperty(ref _clockText, value); }

    /// <summary>热裕度分量。</summary>
    public string ThermalText { get => _thermalText; private set => SetProperty(ref _thermalText, value); }

    /// <summary>时钟稳定分量。</summary>
    public string StabilityText { get => _stabilityText; private set => SetProperty(ref _stabilityText, value); }

    /// <summary>频率达成进度（0-1，NaN → 0；SegmentBar 的量程）。</summary>
    public double ClockProgress { get => _clockProgress; private set => SetProperty(ref _clockProgress, value); }

    /// <summary>热裕度进度。</summary>
    public double ThermalProgress { get => _thermalProgress; private set => SetProperty(ref _thermalProgress, value); }

    /// <summary>时钟稳定进度。</summary>
    public double StabilityProgress { get => _stabilityProgress; private set => SetProperty(ref _stabilityProgress, value); }

    /// <summary>证据帧数文本（如 "样本 42/50"；无评分上下文 → "-"）。</summary>
    public string SamplesText { get => _samplesText; private set => SetProperty(ref _samplesText, value); }

    /// <summary>峰值频率对照文本（峰值 / 同封装参照）。</summary>
    public string PeakText { get => _peakText; private set => SetProperty(ref _peakText, value); }

    /// <summary>未评分原因（已评分时为空串）。</summary>
    public string UnscoredText { get => _unscoredText; private set => SetProperty(ref _unscoredText, value); }

    /// <summary>是否已评分（false 时面板只展示原因，不展示分量）。</summary>
    public bool HasScore { get => _hasScore; private set => SetProperty(ref _hasScore, value); }

    /// <summary>用一份健康度评分刷新（null = 本窗口无该核评分）。</summary>
    public void Apply(CoreHealthScore? score)
    {
        if (score is null)
        {
            ApplyUnscored("-");
            return;
        }

        if (score.UnscoredReason != CoreHealthUnscoredReason.None)
        {
            ApplyUnscored(ReasonText(score.UnscoredReason, score));
            return;
        }

        ClockText = Fmt(score.ClockScore);
        ThermalText = Fmt(score.ThermalScore);
        StabilityText = Fmt(score.StabilityScore);
        ClockProgress = ToProgress(score.ClockScore);
        ThermalProgress = ToProgress(score.ThermalScore);
        StabilityProgress = ToProgress(score.StabilityScore);

        // 证据帧数：样本口径只属于时钟稳定分量（抖动是工况周期级的）。
        SamplesText = score.RequiredSamples > 0
            ? $"稳定样本 {score.StabilitySamples}/{score.RequiredSamples}"
            : "-";

        // 峰值对照：任一端读不到就整条降级，绝不拼一半。
        PeakText = !double.IsNaN(score.PeakGHz) && !double.IsNaN(score.ReferenceGHz) && score.ReferenceValid
            ? $"峰值 {score.PeakGHz:0.00} / 参照 {score.ReferenceGHz:0.00} GHz"
            : "-";

        UnscoredText = string.Empty;
        HasScore = true;
    }

    private void ApplyUnscored(string reason)
    {
        ClockText = ThermalText = StabilityText = "-";
        ClockProgress = ThermalProgress = StabilityProgress = 0.0;
        SamplesText = "-";
        PeakText = "-";
        UnscoredText = reason;
        HasScore = false;
    }

    private static string Fmt(double v)
        => double.IsNaN(v) ? "-" : $"{Math.Clamp(v, 0.0, 100.0):0}";

    private static double ToProgress(double v)
        => double.IsNaN(v) ? 0.0 : Math.Clamp(v / 100.0, 0.0, 1.0);

    private static string ReasonText(CoreHealthUnscoredReason reason, CoreHealthScore score)
        => reason switch
        {
            CoreHealthUnscoredReason.NoFrequency => "频率读数不可用",
            CoreHealthUnscoredReason.NotEnoughHeavyFrames => "高负载样本不足，尚未评分",
            CoreHealthUnscoredReason.PeerReferenceInvalid => "达标核数不足，全封装暂不评分",
            _ => "暂未评分",
        };
}
