namespace CoreBusy.App.ViewModels.Dashboard;

using System.Globalization;
using System.Text;
using CoreBusy.Core.Health;
using CoreBusy.Core.Models;
using CoreBusy.Core.SelfTest;

/// <summary>
/// 健康度文案（v1.17.0；v1.18.0 起标注读数来源、硬件活跃度、逐核电压与未判分原因）。
/// 集中在这里的原因：这个功能最容易被误读，
/// 每一处措辞都在承担"防止用户把代理指标当成厂商结论"的责任，
/// 散落到各个 VM 里迟早会走样。
/// </summary>
public static class CoreHealthFormatter
{
    /// <summary>评级中文标签。</summary>
    public static string GradeLabel(CoreHealthGrade grade) => grade switch
    {
        CoreHealthGrade.Good => "良好",
        CoreHealthGrade.Normal => "正常",
        CoreHealthGrade.Watch => "关注",
        CoreHealthGrade.Poor => "异常",
        _ => "未判定",
    };

    /// <summary>评分文本：未判定时是纯 "-"（不挂单位、不补零）。</summary>
    public static string ScoreText(CoreHealthScore? score)
        => score is null || !score.HasScore
            ? "-"
            : Math.Round(score.Score).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// 概览行的完整文本（v1.18.0）。
    /// <para>
    /// 关键改进：**0 核达标时不再是光秃秃一个 "-"**。v1.17.1 的用户提问
    /// "跑了 10 分钟健康值都没显示"就是被这个横杠逼出来的 —— 界面上"还没攒够证据"
    /// 与"功能坏了"长得一模一样，用户没有任何可判读的线索。现在把它写成进度，
    /// 并把还差什么一并说清。
    /// </para>
    /// </summary>
    public static string BuildOverviewText(IReadOnlyList<CoreHealthScore> scores)
    {
        if (scores.Count == 0)
            return "-";

        var scored = scores.Where(s => s.HasScore).ToArray();
        if (scored.Length > 0)
        {
            // 均分回答"整机状态"，最低核回答"哪一颗在拖后腿" —— 只看均分会被一堆好核盖住问题核。
            return $"{scored.Average(s => s.Score):0} / 最低 {scored.MinBy(s => s.Score)!.CoreId} "
                   + $"{scored.Min(s => s.Score):0}";
        }

        var first = scores[0];
        var eligible = first.EligibleCoreCount;
        var required = first.RequiredEligibleCores;

        // 参照不足是"再多几颗核达标就能出分"，与"完全没有证据"是两件事，分开说。
        return first.UnscoredReason == CoreHealthUnscoredReason.PeerReferenceInvalid
            ? $"- · 参照不足 {eligible}/{required} 核"
            : $"- · {eligible}/{required} 核待评估";
    }

    /// <summary>单核完整说明（Tile 悬浮）。</summary>
    public static string BuildCoreTooltip(CoreHealthScore? score)
    {
        if (score is null)
            return "健康度：尚未收到该核心的采样";

        var text = new StringBuilder();
        text.Append(score.CoreId)
            .Append(" · 健康度 ")
            .Append(ScoreText(score))
            .Append("（")
            .Append(GradeLabel(score.Grade))
            .Append('）');

        // 评级带滞回（v1.19.1）：分数已进下一档、但确认时长还没攒够时如实说明。
        // 不写这一句，用户会看到「94（正常）」这种数字与标签不符的组合，
        // 第一反应是界面算错了 —— 把一个刻意的设计读成 bug。
        if (score.Grade != score.RawGrade)
        {
            text.Append("  ·  瞬时 ")
                .Append(GradeLabel(score.RawGrade))
                .Append("，换档需连续 ")
                .Append(CoreHealthTracker.GradeConfirmSeconds.ToString("0", CultureInfo.InvariantCulture))
                .Append(" 秒确认");
        }

        if (!score.HasScore)
        {
            text.AppendLine().AppendLine();
            AppendUnscoredExplanation(text, score);
            AppendReadings(text, score);
            AppendDisclaimer(text);
            return text.ToString();
        }

        text.AppendLine().AppendLine();

        text.Append("· 频率达成 ").Append(Fmt(score.ClockScore)).AppendLine(" / 100");
        text.Append("   ").Append(score.FrequencyBasis == CoreFrequencySource.SensorEffective
                ? "峰值有效频率 "
                : "峰值频率（**回退口径**）")
            .Append(Ghz(score.PeakGHz))
            .Append(" GHz，参照 ").Append(Ghz(score.ReferenceGHz)).Append(" GHz（同封装中位数）")
            .AppendLine();
        text.Append("   ").Append(DescribeStanding(score)).AppendLine();

        text.Append("· 热裕度 ").Append(Fmt(score.ThermalScore)).Append(" / 100").AppendLine();
        text.Append("   封装温度 ").Append(double.IsNaN(score.PackageTempC)
                ? "-（未提权读不到，该分量不参与合成）"
                : $"{score.PackageTempC:0} ℃（≤70 ℃ 满分）")
            .AppendLine();

        text.Append("· 时钟稳定 ").Append(Fmt(score.StabilityScore)).AppendLine(" / 100");
        if (double.IsNaN(score.StabilityScore))
        {
            text.Append("   本帧不参与合成：工作点样本 ")
                .Append(score.StabilitySamples).Append('/')
                .Append(CoreHealthTracker.StabilityMinSamples)
                .AppendLine(" 帧，不足（样本太少时 MAD 口径不可用 —— 宁缺勿假，不给 0 分）");
        }
        else
        {
            text.Append("   工作点上的有效频率波动（MAD 口径）：")
                .Append(score.StabilitySamples).Append(" 帧落在峰值 ")
                .Append((CoreHealthTracker.StabilityWorkingPointRatio * 100)
                    .ToString("0", CultureInfo.InvariantCulture))
                .AppendLine("% 以上（升频过程帧不计入 —— 那是工况建立过程，不是时钟抖动）");
        }

        AppendReadings(text, score);
        AppendWheaLine(text, score);

        text.AppendLine();
        text.Append("覆盖率 ").Append(score.CoveragePercent.ToString("0", CultureInfo.InvariantCulture))
            .Append("%（三分量齐全为 100%；缺项按剩余权重归一化，不等价于满分）");
        text.AppendLine();
        text.Append("最后更新 ").Append(score.SampledAt == default
            ? "-"
            : score.SampledAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture));

        AppendDisclaimer(text);
        return text.ToString();
    }

    /// <summary>
    /// 未判分原因的可执行说明（v1.18.0）。
    /// <para>
    /// 三种原因的处置方式完全不同，所以必须分开写：读数缺失要查权限/传感器，
    /// 样本不足要跑负载，参照不足要**更多颗核一起**跑 —— 都写成"暂无数据"等于什么都没说。
    /// </para>
    /// </summary>
    private static void AppendUnscoredExplanation(StringBuilder text, CoreHealthScore score)
    {
        switch (score.UnscoredReason)
        {
            case CoreHealthUnscoredReason.NoFrequency:
                text.AppendLine("该核心的频率读数不可用。");
                text.Append("可能是传感器未提供该平台的有效频率、未以管理员身份运行，或采集层尚未就绪。");
                break;

            case CoreHealthUnscoredReason.NotEnoughHeavyFrames:
                text.Append("该核心尚无足够的有效样本（")
                    .Append(score.HeavySamples).Append('/').Append(score.RequiredSamples)
                    .AppendLine(" 帧）。");
                text.Append("空闲频率不构成证据 —— 一颗空闲核不是「不健康」，只是没事干。");
                text.Append("让该核心跑一段高负载即可自动评估（判据：该核使用率 ≥ 70% 或硬件活跃度 ≥ 70%）。");
                break;

            case CoreHealthUnscoredReason.PeerReferenceInvalid:
                text.Append("同封装参照不成立：当前仅 ")
                    .Append(score.EligibleCoreCount).Append('/').Append(score.TrackedCoreCount)
                    .Append(" 核达标，需要 ≥ ").Append(score.RequiredEligibleCores).AppendLine(" 核。");
                text.Append("**此时不给分是刻意的。** 参照取的是同封装各核的中位数，");
                text.Append("达标核太少时这个「中位数」其实就是它自己，比值恒等于 1，");
                text.Append("会算出一个人人满分的假象 —— 那比一个诚实的横杠危险得多。");
                break;

            default:
                text.Append("尚未收到该核心的采样。");
                break;
        }
    }

    /// <summary>
    /// 读数来源与窗口（v1.18.0，P1-3）。
    /// <para>
    /// 这一段的用途是让"这个数从哪来"永远可回答。v1.17.x 为了弄清"界面那个 4.52 GHz 是
    /// 哪来的"花了整轮排查，根因不是读数错，而是来源不可见。列在这里的每一项都带口径说明，
    /// 混用两种口径（P-state 档位 vs 驻留加权有效频率）的可能性因此被消掉。
    /// </para>
    /// </summary>
    private static void AppendReadings(StringBuilder text, CoreHealthScore score)
    {
        text.AppendLine();
        text.Append("· 读数来源：").Append(DescribeBasis(score)).AppendLine();
        text.Append("· 硬件活跃度 ").Append(Percent(score.HardwareActivityPercent))
            .AppendLine("（有效频率 ÷ 本机最高加速档；口径差异见文档，不等于 C0 驻留）");
        text.Append("· 该核电压 ").Append(Volts(score.CoreVoltageV))
            .AppendLine("（逐核读数；读不到显示「-」，不用 0 冒充）");
    }

    private static string DescribeBasis(CoreHealthScore score) => score.FrequencyBasis switch
    {
        CoreFrequencySource.SensorEffective => "口径：LHM 逐核有效频率（硬件驻留加权的时间平均值）",
        CoreFrequencySource.SensorPState => "口径：**回退** —— LHM 逐核 P-state 档位（与负载无关，判分应谨慎看待）",
        CoreFrequencySource.PerfCounterRatio => "口径：**回退** —— 性能计数器比值 × 基频（实测与负载不相关）",
        CoreFrequencySource.BaseClock => "口径：**回退** —— 基频常数",
        _ => "口径：未知",
    };

    /// <summary>
    /// WHEA 单独成行 —— 它扣的是**总分**而不是某个分量，与三项并列会让人误会它也有权重。
    /// </summary>
    private static void AppendWheaLine(StringBuilder text, CoreHealthScore score)
    {
        text.Append("· WHEA 硬件错误（近 7 天）：");

        if (!score.WheaAvailable)
        {
            text.Append("未读取到（事件日志不可访问）");
            return;
        }

        text.Append("已纠正 ").Append(score.WheaCorrectedCount)
            .Append(" 条、未纠正 ").Append(score.WheaFatalCount).Append(" 条");

        if (score.WheaFatalCount > 0)
            text.Append(" → 总分封顶 60（不可恢复的硬件错误是既成事实，不能用其他指标平均掉）");
        else if (score.WheaCorrectedCount > 0)
            text.Append(" → 总分扣 ")
                  .Append(Math.Min(score.WheaCorrectedCount * 5, 25))
                  .Append(" 分");
        else
            text.Append(" → 无扣分。这是整套体系里最硬的一项：它是硬件自己报出来的，不是推断");
    }

    /// <summary>
    /// 站位信息 —— **只展示，不计分**。
    /// <para>
    /// 关键词是"不计分"和"偏差约 ±2.5%"：不写清这两点，用户看到历史 1.062、本次 1.013
    /// 就会读成"掉了 5%"，而实测那正是估计量自身的偏差（取历史最大值当锚所致），
    /// 大于同一时间尺度上的真实老化。宁可信息给全、把不确定性一起摊开。
    /// </para>
    /// </summary>
    private static string DescribeStanding(CoreHealthScore score)
    {
        if (double.IsNaN(score.BaselineRatio))
            return "历史站位：尚无记录（需在满载下持续数十秒）";

        var current = double.IsNaN(score.PeakGHz) || score.ReferenceGHz <= 0
            ? double.NaN
            : score.PeakGHz / score.ReferenceGHz;

        var text = "历史最好站位 "
                   + score.BaselineRatio.ToString("0.000", CultureInfo.InvariantCulture);

        if (!double.IsNaN(current))
            text += "，本次 " + current.ToString("0.000", CultureInfo.InvariantCulture);

        return text + "（仅参考、不计分；该比值自身偏差约 ±2.5%）";
    }

    /// <summary>总览行说明（概览卡片"健康"一行悬浮）。</summary>
    /// <param name="scores">本帧各核评分。</param>
    /// <param name="selfTestSummary">
    /// 最近一次逐核自检的摘要（可为 null）。自检结论**不进评分**，但它是唯一确定性证据，
    /// 所以挂在这里而不是另开一块界面。
    /// </param>
    public static string BuildOverviewTooltip(
        IReadOnlyList<CoreHealthScore> scores,
        string? selfTestSummary = null)
    {
        if (scores.Count == 0)
            return "健康度：尚未收到核心采样";

        var text = new StringBuilder();
        var scored = scores.Where(s => s.HasScore).ToArray();

        if (scored.Length == 0)
        {
            AppendNoScoreOverview(text, scores);
        }
        else
        {
            var average = scored.Average(s => s.Score);
            var worst = scored.OrderBy(s => s.Score).First();

            text.Append("健康度均分 ").Append(Math.Round(average).ToString(CultureInfo.InvariantCulture))
                .Append("（").Append(scored.Length).Append('/').Append(scores.Count).AppendLine(" 核已判定）");
            text.Append("最低：").Append(worst.CoreId).Append(' ').Append(ScoreText(worst))
                .Append("（").Append(GradeLabel(worst.Grade)).Append(')').AppendLine().AppendLine();

            text.Append("逐核：").AppendLine();
            foreach (var s in scores)
            {
                text.Append("  ").Append(s.CoreId.PadRight(4)).Append(' ').Append(ScoreText(s).PadLeft(3))
                    .Append("  ").Append(GradeLabel(s.Grade));

                if (s.HasScore)
                {
                    text.Append("  活跃 ")
                        .Append(Percent(s.HardwareActivityPercent))
                        .Append("  电压 ").Append(Volts(s.CoreVoltageV));
                }
                else
                {
                    text.Append("（").Append(DescribeShortReason(s)).Append('）');
                }

                text.AppendLine();
            }
        }

        AppendSelfTestLine(text, selfTestSummary);
        AppendDisclaimer(text);
        return text.ToString();
    }

    /// <summary>0 核达标时的概览正文（v1.18.0：把"还差什么"写清楚）。</summary>
    private static void AppendNoScoreOverview(StringBuilder text, IReadOnlyList<CoreHealthScore> scores)
    {
        var first = scores[0];

        switch (first.UnscoredReason)
        {
            case CoreHealthUnscoredReason.PeerReferenceInvalid:
                text.Append("暂不评分：同封装参照不成立 —— 仅 ")
                    .Append(first.EligibleCoreCount).Append('/').Append(first.TrackedCoreCount)
                    .Append(" 核达标，需要 ≥ ").Append(first.RequiredEligibleCores).AppendLine(" 核。");
                text.AppendLine();
                text.Append("参照取同封装各核的中位数，达标核太少时它其实就是某一颗核自己，");
                text.Append("会算出「人人满分」的假象 —— 所以宁可不给分。").AppendLine();
                text.AppendLine();
                text.Append("**怎么让它出分**：让更多核心同时跑负载（判据是该核使用率 ≥ 70% 或硬件活跃度 ≥ 70%）。");
                break;

            case CoreHealthUnscoredReason.NoFrequency:
                text.AppendLine("暂不评分：所有核心的频率读数都不可用。");
                text.AppendLine();
                text.Append("请确认以管理员身份运行（内核驱动只对提升后的进程开放），");
                text.Append("或检查设置里是否关闭了传感器读取。");
                break;

            default:
                text.AppendLine("尚无任何核心攒够有效样本，暂时无法评分。");
                text.AppendLine();
                text.Append("评分要求该核心在高负载（使用率 ≥ 70%）或高硬件活跃度（≥ 70%）下累计 ")
                    .Append(first.RequiredSamples)
                    .AppendLine(" 帧以上。");
                text.Append("空闲频率不构成证据 —— 拿 800 MHz 去判断好坏只会得到噪声。");
                break;
        }

        text.AppendLine().AppendLine();
        text.Append("逐核样本：").Append(string.Join("  ",
            scores.Select(s => $"{s.CoreId} {s.HeavySamples}/{s.RequiredSamples}")));
    }

    /// <summary>
    /// 自检结果单独成行（v1.18.0）。措辞上必须与健康度**分清**：一个是确定性证据，一个是推断。
    /// </summary>
    private static void AppendSelfTestLine(StringBuilder text, string? selfTestSummary)
    {
        text.AppendLine().AppendLine();
        text.Append("· 逐核确定性自检（不进评分，单项证据）：");

        if (string.IsNullOrEmpty(selfTestSummary))
        {
            text.Append("尚未运行。点击面板标题行的「自检」会真机满载逐核跑一段结果已知的计算");
            text.Append("（会明显占用 CPU，可随时中断），用于回答「这颗核算错过没有」。");
            return;
        }

        text.AppendLine().Append(selfTestSummary);
    }

    private static string DescribeShortReason(CoreHealthScore score) => score.UnscoredReason switch
    {
        CoreHealthUnscoredReason.NoFrequency => "无频率读数",
        CoreHealthUnscoredReason.NotEnoughHeavyFrames =>
            $"样本 {score.HeavySamples}/{score.RequiredSamples}",
        CoreHealthUnscoredReason.PeerReferenceInvalid => "参照不足",
        _ => "未判定",
    };

    private static void AppendDisclaimer(StringBuilder text)
    {
        text.AppendLine().AppendLine();
        text.Append("这是**代理指标**，不是厂商认证的健康度：CPU 没有可读的损耗寄存器。");
        text.Append("满分 100，由「频率达成 60% + 热裕度 25% + 时钟稳定 15%」合成，");
        text.Append("任一分量不可用时按剩余权重归一化；WHEA 硬件错误在总分上另行扣减。");
        text.AppendLine();
        text.Append("分数是**读数**（逐帧照实给），评级是**结论**：换档需连续 ")
            .Append(CoreHealthTracker.GradeConfirmSeconds.ToString("0", CultureInfo.InvariantCulture))
            .AppendLine(" 秒确认，");
        text.Append("所以「94（正常）」这种组合是正常的 —— 分数已进良好档，评级还在等确认。");
        text.AppendLine();
        text.Append("频率达成用的是**逐核有效频率**（硬件驻留加权的时间平均值） —— ");
        text.Append("它是「这段时间这颗核实际跑多快」，与界面上显示的 P-state 档位频率不是同一个量。");
        text.AppendLine();
        text.Append("硅老化在数年尺度上通常只有个位数百分比影响，而室温、风扇曲线、");
        text.Append("功耗墙的影响更大 —— 请重点看趋势，别抠单次读数。");
        text.AppendLine();
        text.Append("本分数衡量的是「这颗核现在相对同封装同伴健不健康」，");
        text.Append("**不衡量「整颗芯片相比半年前掉了多少」** —— 后者受限于可观测量的噪声");
        text.Append("（实测站位估计自身的偏差约 2.5%，大于数年尺度上的真实老化），本工具不声称能测。");
    }

    /// <summary>逐核自检摘要（概览悬浮用）。</summary>
    public static string BuildSelfTestSummary(IReadOnlyList<CoreSelfTestOutcome>? outcomes)
    {
        if (outcomes is null || outcomes.Count == 0)
            return string.Empty;

        var passed = outcomes.Count(o => o.Passed);
        var failed = outcomes.Count(o => o.Executed && !o.Passed);
        var notRun = outcomes.Count(o => !o.Executed);

        var text = new StringBuilder();
        text.Append($"最近一轮：{passed}/{outcomes.Count} 核通过");
        if (failed > 0)
            text.Append($"，**{failed} 核出现计算不一致**");
        if (notRun > 0)
            text.Append($"，{notRun} 核未执行");

        text.AppendLine();
        foreach (var o in outcomes)
        {
            text.Append("  ").Append(o.CoreId.PadRight(4)).Append(' ').Append(SelfTestVerdict(o));
            if (o.Executed)
            {
                text.Append("  轮次 ").Append(o.Passes)
                    .Append("  整数不一致 ").Append(o.IntegerMismatches)
                    .Append("  浮点失稳 ").Append(o.FpBitInstabilities);
            }

            text.AppendLine();
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>单核自检结论标签。</summary>
    public static string SelfTestVerdict(CoreSelfTestOutcome? outcome)
    {
        if (outcome is null)
            return "未运行";
        if (outcome.Error is not null)
            return $"未执行（{outcome.Error}）";
        return outcome.Passed ? "通过" : "**发现计算不一致**";
    }

    private static string Fmt(double value)
        => double.IsNaN(value) ? "-" : Math.Round(value).ToString(CultureInfo.InvariantCulture);

    private static string Ghz(double value)
        => double.IsNaN(value) ? "-" : value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Percent(double value)
        => double.IsNaN(value) ? "-" : Math.Round(value).ToString(CultureInfo.InvariantCulture) + "%";

    private static string Volts(double value)
        => double.IsNaN(value) ? "-" : value.ToString("0.000", CultureInfo.InvariantCulture) + " V";
}
