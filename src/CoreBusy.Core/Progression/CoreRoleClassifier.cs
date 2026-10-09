namespace CoreBusy.Core.Progression;

/// <summary>核心的行为角色（由长期统计派生，非瞬时状态）。</summary>
/// <remarks>
/// <para>
/// 与 <see cref="Models.CoreStatus"/> 的根本区别：后者按<em>瞬时</em>占用率分档
/// （摸鱼/ 空闲 / 工作 / 忙碌 / 高负载 / 爆肝），每帧都在变；
/// 角色则由<b>长期统计</b>（平均、方差、峰值占比、昼夜节律）判定，跨天相对稳定。
/// </para>
/// <para>
/// 角色会变，但变得慢 —— 这正是它的价值：用户看到「C4 昨天还是苦劳，今天变摸鱼王了」
/// 会有探究欲，而瞬时状态每秒都在变、没有这种感知。
/// </para>
/// <para>
/// <b>角色不代表硬件好坏。</b>摸鱼王不是"坏核"，尖峰侠也不是"不稳定"——
/// 它们只描述这颗核在<em>本机当前使用习惯</em>下的行为模式。
/// </para>
/// </remarks>
public enum CoreRole
{
    /// <summary>证据不足（观测样本太少或时长太短）。界面显示 "-"。</summary>
    Unknown = 0,

    /// <summary>全天摸鱼：平均极低且波动极小。</summary>
    Slacker = 1,

    /// <summary>闲时热手：整体负载不高，但偶有短时高峰。</summary>
    Burster = 2,

    /// <summary>稳态搬运工：负载中等偏高且波动很小（长时间稳定干活）。</summary>
    Steady = 3,

    /// <summary>苦劳：长期高负载且波动小（长时间满负荷值守）。</summary>
    Workhorse = 4,

    /// <summary>尖峰侠：均值不高但峰值极高（只偶尔爆发）。</summary>
    Spiker = 5,

    /// <summary>夜猫子：夜间负载占比显著高于白天。</summary>
    Nocturnal = 6,
}

/// <summary>判定核角色所需的统计量（全部为实测量的累积，不含推断）。</summary>
public sealed record CoreRoleStats
{
    /// <summary>累计观测时长（核·分）。分母，与 EXP 账本的兜底口径同源。</summary>
    public double ObservedCoreMinutes { get; init; }

    /// <summary>累计负荷（核·分）。注意这与 EXP 的口径不同：这里是<b>纯负荷</b>，
    /// 不含陪伴兜底 —— 角色要刻画"它干了多少活"，兜底会把这个信息抹平。</summary>
    public double LoadCoreMinutes { get; init; }

    /// <summary>负载平方的累计（用于算方差）。</summary>
    public double LoadSquaredCoreMinutes { get; init; }

    /// <summary>峰值占用率（0-100）。读不到时为 NaN。</summary>
    public double PeakPercent { get; init; } = double.NaN;

    /// <summary>白天（08:00–20:00）累计负荷（核·分）。</summary>
    public double DayLoadCoreMinutes { get; init; }

    /// <summary>夜间累计负荷（核·分）。</summary>
    public double NightLoadCoreMinutes { get; init; }
}

/// <summary>
/// 核角色判定（纯逻辑，无状态，可静态测试）。
/// </summary>
/// <remarks>
/// <para>
/// <b>判定顺序有意义</b>：尖峰与夜猫是"形状"特征，必须先于"水平"特征判定 ——
/// 一颗只在夜里爆发的核，若先按均值判成"闲时热手"，就丢掉了它最重要的特征。
/// </para>
/// <para>
/// <b>所有阈值都是可调常量而非拟合出来的参数</b>，且判定只依赖实测量的比值与方差，
/// 不含任何模型预测。这样做的代价是判定不"聪明"，换来的是每个结论都能手工复核。
/// </para>
/// </remarks>
public static class CoreRoleClassifier
{
    /// <summary>证据门槛：观测时长（核·分）。低于此不给角色。</summary>
    /// <remarks>60 核·分 = 单核满载 1 小时。低于此连"忙闲"都说不准。</remarks>
    public const double MinObservedCoreMinutes = 60.0;

    /// <summary>摸鱼：平均占用率上限（%）。</summary>
    public const double SlackerAvgPct = 8.0;

    /// <summary>苦劳：平均占用率下限（%）。</summary>
    public const double WorkhorseAvgPct = 60.0;

    /// <summary>稳态：平均占用率下限（%）。</summary>
    public const double SteadyAvgPct = 25.0;

    /// <summary>尖峰：峰值占用率下限（%）。</summary>
    public const double SpikerPeakPct = 90.0;

    /// <summary>尖峰：峰值 ÷ 均值 的下限。越高说明"平时闲、偶尔爆"。</summary>
    /// <remarks>5 倍：均值 18% 而峰值 90% → 5.0 恰好压线。稳态核这个比值接近 1。</remarks>
    public const double SpikerRatioMin = 5.0;

    /// <summary>波动系数（变异系数）上限：低于此算"稳"。</summary>
    /// <remarks>0.35 = 相对标准差 35%。稳态核约 0.2~0.3，尖峰核常 &gt; 1.0。</remarks>
    public const double SteadyCvMax = 0.35;

    /// <summary>夜猫子：夜间负荷占比下限。</summary>
    /// <remarks>0.62：全天负荷里超过 62% 落在 20:00–08:00 才算夜猫。</remarks>
    public const double NocturnalNightRatioMin = 0.62;

    /// <summary>
    /// 判定角色。
    /// </summary>
    /// <remarks>
    /// <paramref name="stats"/> 为 null、观测不足、或负荷为 0 且无任何峰值证据时，
    /// 返回 <see cref="CoreRole.Unknown"/> —— 界面显示 "-"，不猜。
    /// </remarks>
    public static CoreRole Classify(CoreRoleStats? stats)
    {
        if (stats is null)
            return CoreRole.Unknown;

        if (stats.ObservedCoreMinutes < MinObservedCoreMinutes)
            return CoreRole.Unknown;

        var avgPct = AveragePercent(stats);

        // 完全无负荷且无峰值记录：没有任何可据以判断的事实。
        // 不把"0% 平均"直接判成摸鱼王 —— 那可能只是它还没被调度到，
        // 与"它就是闲"是两回事。峰值有记录时才敢判。
        var hasPeak = !double.IsNaN(stats.PeakPercent) && stats.PeakPercent > 0;
        if (avgPct <= 0 && !hasPeak)
            return CoreRole.Unknown;

        // ── 形状特征优先于水平特征 ──
        // 顺序有讲究：夜猫与尖峰是"什么时候/多集中"的特征，
        // 一旦被"均值高低"抢先判定，这些信息就丢了。

        // 夜间负荷要够大才算夜猫，否则一个全天只忙 2 分钟夜的核会被误判。
        var totalLoad = stats.DayLoadCoreMinutes + stats.NightLoadCoreMinutes;
        if (totalLoad > 0 && stats.NightLoadCoreMinutes / totalLoad >= NocturnalNightRatioMin)
            return CoreRole.Nocturnal;

        // 尖峰：均值不高但峰值极高。
        if (hasPeak && avgPct > 0
            && stats.PeakPercent >= SpikerPeakPct
            && stats.PeakPercent / avgPct >= SpikerRatioMin)
        {
            return CoreRole.Spiker;
        }

        // ── 水平特征（此后只看均值与波动）──
        var cv = CoefficientOfVariation(stats);

        if (avgPct < SlackerAvgPct)
            return CoreRole.Slacker;

        if (avgPct >= WorkhorseAvgPct)
            return CoreRole.Workhorse;

        // 波动大且峰值高 → 闲时热手（剩下的那类爆发）。
        if (hasPeak && stats.PeakPercent >= SpikerPeakPct && cv > SteadyCvMax)
            return CoreRole.Burster;

        if (avgPct >= SteadyAvgPct && cv <= SteadyCvMax)
            return CoreRole.Steady;

        // 中等负载但波动大、或样本偏短：给"闲时热手"作为最保守的落点，
        // 而不是 Unknown —— 后者会让大部分核在积累足够证据前一直是 "-"。
        return CoreRole.Burster;
    }

    /// <summary>平均占用率（%）。</summary>
    /// <remarks>
    /// 分母用<b>观测时长</b>而非负荷时长：它回答"这颗核平均有多闲"，
    /// 与 EXP 的口径刻意不同（EXP 要的是"挣了多少"）。
    /// </remarks>
    public static double AveragePercent(CoreRoleStats stats)
        => stats.ObservedCoreMinutes <= 0
            ? 0.0
            : stats.LoadCoreMinutes / stats.ObservedCoreMinutes * 100.0;

    /// <summary>
    /// 变异系数（标准差 ÷ 均值），无量纲。均值 ≤ 0 时返回 NaN。
    /// </summary>
    /// <remarks>
    /// 用 E[x²] − E[x]² 算，数值上可能因减法放大误差出现微小负值，
    /// 故<see cref="Variance"/> 内已夹到 0。
    /// </remarks>
    public static double CoefficientOfVariation(CoreRoleStats stats)
    {
        var mean = AveragePercent(stats);
        if (mean <= 0)
            return double.NaN;

        return Math.Sqrt(Math.Max(0.0, Variance(stats))) / mean;
    }

    /// <summary>占用率的方差（%²）。</summary>
    public static double Variance(CoreRoleStats stats)
    {
        if (stats.ObservedCoreMinutes <= 0)
            return 0.0;

        var mean = AveragePercent(stats);
        var meanSquared = stats.LoadSquaredCoreMinutes / stats.ObservedCoreMinutes;
        return Math.Max(0.0, meanSquared - mean * mean);
    }

    /// <summary>夜间负荷占比 [0,1]。无任何负荷时为 NaN。</summary>
    public static double NightRatio(CoreRoleStats stats)
    {
        var total = stats.DayLoadCoreMinutes + stats.NightLoadCoreMinutes;
        return total <= 0 ? double.NaN : stats.NightLoadCoreMinutes / total;
    }

    /// <summary>角色短标签（用于 Tile 上的小字）。</summary>
    public static string Label(CoreRole role) => role switch
    {
        CoreRole.Slacker => "摸鱼",
        CoreRole.Burster => "闲时热手",
        CoreRole.Steady => "稳态",
        CoreRole.Workhorse => "苦劳",
        CoreRole.Spiker => "尖峰",
        CoreRole.Nocturnal => "夜猫",
        _ => "-",
    };

    /// <summary>角色的一句话说明（悬浮用）。全部为实测口径，无推断。</summary>
    public static string Describe(CoreRole role, CoreRoleStats? stats)
    {
        if (role == CoreRole.Unknown)
            return "角色：证据不足（需要至少 1 小时的观测）。";
        if (stats is null)
            return "角色：" + Label(role);

        var avg = AveragePercent(stats);
        var peakText = double.IsNaN(stats.PeakPercent) ? "无峰值记录" : $"峰值 {stats.PeakPercent:0}%";
        var cv = CoefficientOfVariation(stats);
        var cvText = double.IsNaN(cv) ? "" : $"波动 {cv:0.00}";

        var detail = $"平均 {avg:0.0}%｜{peakText}｜{cvText}".TrimEnd('｜');
        var head = role switch
        {
            CoreRole.Slacker => "摸鱼王：长期低负载且波动极小",
            CoreRole.Burster => "闲时热手：整体不高，偶尔爆发",
            CoreRole.Steady => "稳态搬运工：中等偏高且波动小",
            CoreRole.Workhorse => "苦劳：长期高负荷值守",
            CoreRole.Spiker => "尖峰侠：平时闲，偶尔冲很高",
            CoreRole.Nocturnal => "夜猫子：负荷集中在夜间",
            _ => "角色",
        };

        var night = NightRatio(stats);
        var nightText = double.IsNaN(night)
            ? string.Empty
            : $"｜夜间占比 {night * 100:0}%";

        return $"角色：{Label(role)} · {head}{Environment.NewLine}{detail}{nightText}";
    }
}