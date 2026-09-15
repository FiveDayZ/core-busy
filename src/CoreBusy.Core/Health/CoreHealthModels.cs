namespace CoreBusy.Core.Health;

using CoreBusy.Core.Models;

/// <summary>健康度评级。分档只用于着色与文案，判定门槛见 <see cref="CoreHealthScore"/>。</summary>
public enum CoreHealthGrade
{
    /// <summary>样本不足或参照缺失 —— 尚未能判定（界面显示 "-"，不猜数）。</summary>
    Unknown = 0,

    /// <summary>≥ 90：良好。</summary>
    Good,

    /// <summary>80 – 89：正常。</summary>
    Normal,

    /// <summary>70 – 79：需要关注。</summary>
    Watch,

    /// <summary>&lt; 70：异常。</summary>
    Poor,
}

/// <summary>
/// 无法判定的原因（v1.18.0）。<see cref="CoreHealthScore.Score"/> 为 NaN 时才有意义。
/// <para>
/// 引入它的直接动机是 v1.17.1 的用户提问"跑了 10 分钟健康值都没显示"：
/// 界面上"没有证据"和"功能坏了"**长得一模一样**（都是 "-"），
/// 用户没有任何办法区分，只能来问。把原因变成一个可判读的枚举后，
/// 界面就能明确写出"还差什么"，而不是留一个沉默的横杠。
/// </para>
/// </summary>
public enum CoreHealthUnscoredReason
{
    /// <summary>已判定（<see cref="CoreHealthScore.Score"/> 是有效值）。</summary>
    None = 0,

    /// <summary>该核频率读数不可用（传感器缺失 / 未提权 / 采集层未就绪）。</summary>
    NoFrequency,

    /// <summary>该核的有效样本不足 —— 需要它在高负载或高时钟下被观测到足够多帧。</summary>
    NotEnoughHeavyFrames,

    /// <summary>
    /// 同封装参照不成立：达标核数太少（详见 <see cref="CoreHealthTracker.MinScoredCores"/>）。
    /// <para>
    /// 这是 v1.18.0 修掉的那个假象："8 颗核里只有 1 颗达标 → 参照的中位数就是它自己 →
    /// 比值恒 1.0 → 总分 100"。`-` 至少是诚实的"没有证据"，而 `100` 把证据不足
    /// 渲染成了整机满分 —— 后者危险得多。
    /// </para>
    /// </summary>
    PeerReferenceInvalid,
}

/// <summary>
/// 单个物理核心的健康度评分（v1.17.0，v1.18.0 起频率口径改为逐核**有效频率**）。
/// <para>
/// **这是一组代理指标的合成，不是厂商认证的"损耗百分比"。** CPU 没有可读的健康度寄存器
/// （不像电池有标定容量、SSD 有 P/E 计数），因此本结构里的每一个数都来自可观测量，
/// 并用 <see cref="CoveragePercent"/> 明确回答"这个分数有多少依据"。
/// </para>
/// <para>
/// 三个分量各自独立、互不掩盖，界面必须能分别看到 —— 因为它们的处置方式完全不同：
/// 掉队是硅/供电问题，裕度低是散热问题，抖动大是供电质量问题。
/// 把三者揉成一个数字再丢掉分项，等于把可执行的信息扔了。
/// </para>
/// </summary>
public sealed record CoreHealthScore
{
    /// <summary>核心显示名（与 <c>CpuCoreSnapshot.Id</c> 同源）。</summary>
    public string CoreId { get; init; } = string.Empty;

    /// <summary>
    /// 综合评分（0–100）。<see cref="double.NaN"/> = 尚不能判定（满载样本不足）——
    /// 界面必须据此显示 "-"，**绝不允许拿 0 或 100 冒充**（沿用项目全线约定）。
    /// </summary>
    public double Score { get; init; } = double.NaN;

    /// <summary>分量 A · 频率达成度（0–100）：该核满载频率相对参照的比值。</summary>
    public double ClockScore { get; init; } = double.NaN;

    /// <summary>
    /// 分量 B · 热裕度（0–100）：封装温度距 TjMax 的余量。**全核共担** ——
    /// 散热是整颗封装的属性，逐核给出不同数值是伪造精度。
    /// <para>
    /// 温度读不到（非提权 / 传感器关闭）时为 NaN，此时权重由剩余分量归一化，
    /// 覆盖率随之下降。**不要在这里塞 WHEA 惩罚** —— 那是"事实"而非"传感器读数"，
    /// 与温度是否可读无关，见 <see cref="CoreBusy.Core.Health.CoreHealthTracker"/> 的 ApplyWhea。
    /// </para>
    /// </summary>
    public double ThermalScore { get; init; } = double.NaN;

    /// <summary>
    /// 分量 C · 时钟稳定性（0–100）：**工作点上**频率的鲁棒变异系数（MAD 口径）。
    /// <para>
    /// 两点口径（v1.19.1，常量注释在 <c>CoreHealthTracker</c> 上）：
    /// </para>
    /// <list type="bullet">
    ///   <item>只在**工作点**上统计（该核峰值 × <c>StabilityWorkingPointRatio</c> 以上的帧）——
    ///         升频过程帧混进来时，变异系数描述的是「工况建立了没有」，不是「时钟稳不稳」；</item>
    ///   <item>工作点样本数不足 <c>StabilityMinSamples</c> 时为 NaN（按缺项处理、覆盖率下降），
    ///         **不是 0** —— 0 会被读成「抖动极大」，那是把「没测准」说成「测得差」。</item>
    /// </list>
    /// </summary>
    public double StabilityScore { get; init; } = double.NaN;

    /// <summary>
    /// 稳定性分量实际使用的样本数（落在工作点内的证据帧数，v1.19.1）。
    /// <para>
    /// 与 <see cref="HeavySamples"/> 可能不等：后者是所有证据帧，本字段是其中落在工作点内的部分。
    /// 单独带出来是为了让 <see cref="StabilityScore"/> 为 NaN 时**能解释原因** ——
    /// 否则「证据 83 帧却说样本不足」看起来就是个 bug。
    /// </para>
    /// </summary>
    public int StabilitySamples { get; init; }

    /// <summary>
    /// 本次判定采用的核心峰值频率（GHz）：**该核有效频率窗口统计量**（上四分位均值，v1.18.0）。
    /// <para>
    /// 估计量取"最好的四分之一帧的均值"而不是"90 分位"，理由见
    /// <see cref="CoreHealthTracker"/> 的 <c>UpperQuartileMean</c>：
    /// 8 个样本的 90 分位**数学上就是最大值**，会把冷启动瞬时高峰当成该核的能力上限。
    /// </para>
    /// </summary>
    public double PeakGHz { get; init; } = double.NaN;

    /// <summary>
    /// 本次判定使用的参照频率（GHz）：同封装各核峰值的**中位数**（唯一的参照来源）。
    /// <para>
    /// 用中位数而非高分位，是因为实机八核峰值天然散布 3.86–4.24 GHz（分档差异），
    /// 拿"最好的那颗"当标尺会把大半核心报成异常。
    /// 历史基线**不参与参照** —— 它自带 ~2.5% 的估计偏差，只作展示（见 <see cref="BaselineRatio"/>）。
    /// </para>
    /// </summary>
    public double ReferenceGHz { get; init; } = double.NaN;

    /// <summary>
    /// 该核历史最好站位：记录时它的峰值相对同封装中位数的比值（无量纲）。无基线时为 NaN。
    /// <para>
    /// 之所以存比值而不是绝对频率：绝对频率随散热/功耗工况整体漂移（实测同一会话内可达
    /// 7.6%，八核还会一起跳 17%），拿它当锚必然误报；比值是**站位**，工况整体上下浮动时不动。
    /// </para>
    /// <para>
    /// <b>它不参与评分</b>，只作信息展示 —— 因为"取历史最大值当锚"自带 ~2.5% 的系统性偏差，
    /// 大于要测的信号。理由见 <see cref="CoreHealthBaselineEntry"/>。
    /// </para>
    /// </summary>
    public double BaselineRatio { get; init; } = double.NaN;

    /// <summary>
    /// 历史最好站位折算到**当前同伴水平**的当量频率（GHz）：<c>同封装中位数 × BaselineRatio</c>。
    /// <para>
    /// 只用于展示与人工比对，不参与评分：它减去当前峰值得到的那点差值里，
    /// 大部分是估计偏差而不是退化（实测平均 −2.5%）。
    /// </para>
    /// </summary>
    public double BaselineGHz { get; init; } = double.NaN;

    /// <summary>已积累的满载样本数。</summary>
    public int HeavySamples { get; init; }

    /// <summary>判定所需的满载样本数（<see cref="HeavySamples"/> 低于此值时 <see cref="Score"/> 为 NaN）。</summary>
    public int RequiredSamples { get; init; }

    /// <summary>
    /// 本次判定采用的频率口径（v1.18.0）。正常应为
    /// <see cref="CoreBusy.Core.Models.CoreFrequencySource.SensorEffective"/>。
    /// </summary>
    public CoreFrequencySource FrequencyBasis { get; init; } = CoreFrequencySource.Unknown;

    /// <summary>未能判定的具体原因（<see cref="Score"/> 有效时为 <see cref="CoreHealthUnscoredReason.None"/>）。</summary>
    public CoreHealthUnscoredReason UnscoredReason { get; init; } = CoreHealthUnscoredReason.None;

    /// <summary>
    /// 同封装参照是否成立（v1.18.0）。为 false 时全体核心一律不给分 ——
    /// 达标核数不足意味着"中位数"其实是某一颗核自己，比值恒 1 会伪造出满分。
    /// </summary>
    public bool ReferenceValid { get; init; }

    /// <summary>本帧达标（可参与参照）的核心数。</summary>
    public int EligibleCoreCount { get; init; }

    /// <summary>
    /// 参照成立所需的达标核数（由 <c>CoreHealthTracker.RequiredEligibleCores</c> 给出）。
    /// 界面用它回答"还差几颗核"，避免两处硬编码同一个规则。
    /// </summary>
    public int RequiredEligibleCores { get; init; }

    /// <summary>本帧被跟踪的核心总数。</summary>
    public int TrackedCoreCount { get; init; }

    /// <summary>
    /// 该核电压（V，v1.18.0，逐核读数）。**不参与评分**，只作展示与存档。
    /// 读不到时为 NaN —— 沿用全线约定，绝不用 0 冒充。
    /// </summary>
    public double CoreVoltageV { get; init; } = double.NaN;

    /// <summary>
    /// 硬件活跃度（0-100，v1.18.0）：有效频率 ÷ 本机最高加速档频率。**仅展示，不参与评分**。
    /// 口径与其局限见 <see cref="CoreBusy.Core.Models.CpuCoreSnapshot.HardwareActivityPercent"/>。
    /// </summary>
    public double HardwareActivityPercent { get; init; } = double.NaN;

    /// <summary>参与合成的分量权重之和占满权的比例（0–100）。60 = 缺了部分分量。</summary>
    public double CoveragePercent { get; init; }

    /// <summary>本次判定依据的最后一次采样时刻。</summary>
    public DateTime SampledAt { get; init; }

    /// <summary>判定时的封装温度（℃）；非提权读不到时为 NaN。</summary>
    public double PackageTempC { get; init; } = double.NaN;

    /// <summary>系统日志中已纠正的 WHEA 机器检查错误数（近 7 天）。0 = 干净。</summary>
    public int WheaCorrectedCount { get; init; }

    /// <summary>系统日志中致命的 WHEA 错误数（近 7 天）。</summary>
    public int WheaFatalCount { get; init; }

    /// <summary>WHEA 计数是否成功读到（读不到与"读到 0 条"必须区分）。</summary>
    public bool WheaAvailable { get; init; }

    /// <summary>
    /// **展示用**评级：<see cref="ConfirmedGrade"/> 有值就取它，否则回落到瞬时评级。
    /// <para>
    /// 与 <see cref="Score"/> 的关系是「结论 vs 读数」：分数逐帧照实给，
    /// 评级则需要连续 <c>CoreHealthTracker.GradeConfirmSeconds</c> 秒确认才换档（v1.19.1）。
    /// 因此 <see cref="Grade"/> 可以**滞后于** <see cref="Score"/> —— 这不是缺陷，是滞回本身：
    /// 实测本机占空满载下分数在 82.8–96.4 间摆动（13.6 分），不加滞回时角标 87 帧里跨档
    /// 14 次，用户看到的是每秒变色。
    /// </para>
    /// <para>
    /// 采用「有值才覆盖、否则回落」而不是「未赋值即 Unknown」，是为了**不引入新的失败模式**：
    /// 任何构造 <see cref="CoreHealthScore"/> 的地方漏了这个字段，行为就等于滞回之前的版本。
    /// </para>
    /// </summary>
    public CoreHealthGrade Grade => ConfirmedGrade ?? RawGrade;

    /// <summary>
    /// 瞬时评级：直接由 <see cref="Score"/> 分档得到的档位，**不含滞回**。
    /// <para>
    /// 保留它是因为「分数已经进档、评级还没确认」这个中间状态必须可取证 ——
    /// 界面上那个「94（正常）」的组合就是它，日志与回放工具也要能读到。
    /// </para>
    /// </summary>
    public CoreHealthGrade RawGrade => GradeOf(Score);

    /// <summary>
    /// 分数 → 档位的唯一映射（0–100；NaN → <see cref="CoreHealthGrade.Unknown"/>）。
    /// 门槛：≥90 良好 / ≥80 正常 / ≥70 关注 / &lt;70 异常。
    /// <para>
    /// 暴露成公开静态方法是刻意的：评分器（算确认档）、界面（算瞬时档）与日志必须用
    /// **同一条规则**，各自硬编码一次早晚会错位 —— 本项目在 <c>RequiredEligibleCores</c> 上
    /// 已经吃过一次两处口径打架的亏。
    /// </para>
    /// </summary>
    public static CoreHealthGrade GradeOf(double score) => score switch
    {
        double.NaN => CoreHealthGrade.Unknown,
        >= 90 => CoreHealthGrade.Good,
        >= 80 => CoreHealthGrade.Normal,
        >= 70 => CoreHealthGrade.Watch,
        _ => CoreHealthGrade.Poor,
    };

    /// <summary>
    /// 已确认的展示评级（v1.19.1，由 <c>CoreHealthTracker</c> 写入）。
    /// null = 未经滞回，此时 <see cref="Grade"/> 回落到 <see cref="RawGrade"/>。
    /// </summary>
    public CoreHealthGrade? ConfirmedGrade { get; init; }

    /// <summary>是否已能判定。</summary>
    public bool HasScore => !double.IsNaN(Score);
}

/// <summary>
/// 单核的历史基线（v1.17.0，持久化到 <c>%APPDATA%\CORE-BUSY\core-health.json</c>）。
/// <para>
/// 基线记录"这颗核历史上站到的最高位置"，本意是回答"它现在相对自己的峰值有没有退步"。
/// **但这个纵向比较最终没有进评分** —— 下面三段事故记录说明了原因；它现在只作信息展示。
/// </para>
/// <para>
/// <b>锚点存的是"站位"而不是绝对频率，这是被两次实机事故逼出来的口径。</b>
/// 最早存绝对 GHz（该核观测到的最高峰值），结果两次都出事，且本质是同一个病：
/// </para>
/// <list type="bullet">
///   <item><b>凉热差</b>：7940HS 上 C4 的基线在开跑初期被记为 4.33 GHz，90 秒后热稳态只有
///         4.00 GHz —— 同一会话、零老化，光工况差就 7.6%，比该机型真实的核间差异还大；</item>
///   <item><b>整体跳变</b>：另一轮里八颗核的频率在某一刻**同时**跳到 4.698 GHz，
///         八核的基线于是全被抬到同一个够不着的值 —— 全核同频 4.7 GHz 超出该芯片的功耗能力，
///         这是工况/读数整体漂移，不是能力提升。</item>
/// </list>
/// <para>
/// 病灶是"绝对频率随工况整体漂移"。所以锚点改成**无量纲**的
/// <see cref="RatioToPeer"/>（该核峰值 ÷ 同封装中位数）：工况整体上下浮动时站位不动，
/// 天然免疫；只有**单核相对同伴掉队**才会让这个比值下降。
/// </para>
/// <para>
/// <b>但它最终也没能进评分</b>，这是第三轮实测（两轮各 240 s 满载的往返验证）的结论：
/// 八颗核的"历史最好站位"**一致**高于本次实测站位 0.2%–4.6%（平均 −2.5%）。
/// 不是谁退化了，而是**"取历史最大值当锚"这个估计量自带偏差** ——
/// 锚记的是站位偶然冲高的那一瞬（某核恰好独占 boost、或同伴中位数恰好偏低），
/// 而当前值取的是窗口统计量。
/// </para>
/// <para>
/// 该偏差远大于数年尺度的硅老化影响，折成分数只是在报估计噪声（实测把 C4 误判成 Watch 79）。
/// 所以本字段与 <see cref="CoreHealthScore.BaselineRatio"/> 一律**只作信息展示**。
/// </para>
/// <para>
/// 最终划定的诚实边界：<b>本功能衡量"这颗核现在相对同伴健不健康"，
/// 不衡量"整颗芯片相比半年前掉了多少"</b>；后者在现有可观测量的噪声水平下无法计量，
/// 需要的不是更好的算法而是更好的观测量（固定电压下的 V/F 曲线 —— 要写 MSR，本工具不碰）。
/// </para>
/// </summary>
public sealed record CoreHealthBaselineEntry
{
    /// <summary>
    /// 记录时该核峰值频率相对同封装中位数的比值（无量纲）。**只作信息展示，不参与评分**。
    /// </summary>
    public double RatioToPeer { get; init; } = double.NaN;

    /// <summary>
    /// 记录时的绝对峰值频率（GHz）。
    /// <para>
    /// **仅供人看，不参与任何判定** —— 留着是为了让 JSON 档案能回答"当初到底是什么水平"，
    /// 排查问题时不必再去猜。判定一律走 <see cref="RatioToPeer"/>。
    /// </para>
    /// </summary>
    public double PeakGHz { get; init; } = double.NaN;

    /// <summary>达到该站位的时间（基线被抬高时随之刷新）。</summary>
    public DateTime ObservedAt { get; init; }
}
