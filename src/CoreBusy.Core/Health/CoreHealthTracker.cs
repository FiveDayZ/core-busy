namespace CoreBusy.Core.Health;

using CoreBusy.Core.Models;

/// <summary>
/// 每核健康度评分器（v1.17.0）。**纯逻辑、无 IO、无平台依赖**，便于直接构造数据复核。
/// <para>
/// ──────────────────────────── 设计前提（必须先读） ────────────────────────────
/// </para>
/// <para>
/// **1. 没有任何一个数能直接代表"损耗"。** CPU 不像电池有标定容量、不像 SSD 有 P/E 计数，
/// 没有可读的健康度寄存器。本评分器的全部输入都是代理量，输出是**合成值**。
/// 界面上必须如实说明这一点，不能把它包装成厂商认证的指标。
/// </para>
/// <para>
/// **2. 单点读数毫无意义。** 硅老化在数年尺度上对频率的影响通常只有个位数百分比，
/// 而室温、风扇曲线、功耗墙、调度策略带来的波动可以轻松超过 10% ——
/// 噪声稳压信号。因此本类的一切判定都建立在**窗口统计**上，绝不看瞬时值；
/// 样本不足时返回 NaN（界面显示 "-"），而不是给一个看着像那么回事的假分数。
/// </para>
/// <para>
/// **3. 只在"这颗核真被调用过"的帧上采样。** 一颗空闲核跑在 800 MHz 不是"不健康"。
/// 频率读数本身是采样区间内的均值，负载越低均值越低，跨核比较会被负载结构污染。
/// 所以只有越过 <see cref="HeavyLoadPercent"/>（OS 使用率）**或** <see cref="HeavyActivityPercent"/>
/// （硬件活跃度）的帧才进统计。
/// </para>
/// <para>
/// v1.18.0 起频率输入改为**逐核有效频率**（硬件驻留加权的时间平均值，见
/// <see cref="CoreBusy.Core.Models.CpuCoreSnapshot.EffectiveFrequencyGHz"/>）。
/// 这一改动同时解掉了三个老问题：① P-state 档位时钟与负载无关（本机 8 核读到同一个 4567 MHz，
/// 比值恒 1 → 人人满分）；② "必须连续满载 8 秒才有分"（有效频率一次读数即一个可用值，
/// <see cref="MinHeavySamples"/> 随之降到 3）；③ 小样本分位退化（估计量改为上四分位均值）。
/// </para>
/// <para>
/// **4. 三个分量必须能分开看。** 它们指向完全不同的处置动作：
/// 掉队（<see cref="CoreHealthScore.ClockScore"/>）是硅/供电问题，
/// 裕度低（<see cref="CoreHealthScore.ThermalScore"/>）是散热问题，
/// 抖动大（<see cref="CoreHealthScore.StabilityScore"/>）是供电质量问题。
/// 揉成一个数字再丢掉分项，等于把唯一可执行的信息扔了。
/// </para>
/// <para>
/// ──────────────────────────── 评分口径 ────────────────────────────
/// </para>
/// <list type="bullet">
///   <item><b>A 频率达成度（权重 60%）</b>：该核满载峰值频率 ÷ <b>同封装各核峰值的中位数</b>，
///         自由带 <see cref="PeerTolerance"/>（吸收体质分档差异）。</item>
///   <item><b>B 封装裕度（权重 25%）</b>：仅温度余量。**全核共担** ——
///         散热是整颗封装的属性，逐核给出不同数值是伪造精度。
///         （WHEA 纠错惩罚**不在**此分量内，它作用在总分上，见 <see cref="ApplyWhea"/>。）</item>
///   <item><b>C 时钟稳定性（权重 15%）</b>：满载窗口内频率的鲁棒变异系数（MAD 口径）。</item>
/// </list>
/// <para>
/// 任一分量不可用时，其余分量**按权重重新归一化**，并把覆盖率带进
/// <see cref="CoreHealthScore.CoveragePercent"/> —— 缺项不等于满分，也不等于零分。
/// </para>
/// <para>
/// ──────────────────────────── 一个被实测否掉的维度 ────────────────────────────
/// </para>
/// <para>
/// 曾经还有一条"纵向掉队"分量：拿该核**历史最好站位**当参照，看它有没有相对自己过去退步。
/// 两轮真机往返验证（各 240 s 满载）把它否掉了：八颗核的历史最好站位**一致**高于本次实测
/// 0.2%–4.6%（平均 −2.5%）—— 不是谁退化了，是"取历史最大值当锚"这个估计量自带偏差。
/// 该偏差大于我们要找的信号（数年尺度的硅老化远低于此），折成分数就只是报估计噪声
/// （实测把 C4 误判成 Watch 79）。所以：**站位只作信息展示，不进评分**。
/// </para>
/// <para>
/// 由此框定了本类的诚实边界：<b>它衡量"这颗核现在相对同封装同伴健不健康"，
/// 不衡量"整颗芯片相比半年前掉了多少"</b>。后者在现有可观测量的噪声水平下无法计量 ——
/// 详见 <see cref="CoreHealthBaselineEntry"/>。
/// </para>
/// </summary>
public sealed class CoreHealthTracker
{
    /// <summary>
    /// 每核保留的帧数。
    /// <para>
    /// 240 帧 ≈ 3–4 分钟（按默认 800 ms 采样）。这个长度是两组需求的折中：
    /// </para>
    /// <list type="bullet">
    ///   <item><b>不能太短</b>：窗口决定了"一次满载之后分数还能保留多久"。
    ///         早先取 90 帧（≈70 秒），机器一空下来分数就整屏变 "-"，
    ///         用户会以为功能坏了 —— 而它其实只是"当前没有证据"。</item>
    ///   <item><b>不必太长</b>：本指标描述的是以月计的变化，几分钟前的峰值频率
    ///         与此刻同样有效；窗口再拉长只会让"负载结构变了"更早污染稳定性分量。</item>
    /// </list>
    /// </summary>
    public const int WindowSize = 240;

    /// <summary>
    /// 计入统计的**操作系统**使用率门槛（%）。低于此值的帧不算"这颗核被调用了"。
    /// <para>
    /// v1.18.0 起它不再是唯一判据，而是与 <see cref="HeavyActivityPercent"/> 取"或"：
    /// 两者任一成立即为证据帧。保留它的理由是它**独立于传感器**：
    /// 有效频率读数缺失时（非提权 / 平台不支持 / 传感器关闭）它是仅剩的可用判据。
    /// </para>
    /// <para>
    /// 它的已知弱点（v1.18.0 的修正动机）：使用率是**调度器视角的非空闲时间**，
    /// 一颗被压在 400 MHz 的核照样能报 100% —— 而"满载但被降频"恰恰是硅/供电退化
    /// 最典型的形态。所以判据不能只有它。
    /// </para>
    /// </summary>
    public const double HeavyLoadPercent = 70.0;

    /// <summary>
    /// 计入统计的**硬件活跃度**门槛（%）：有效频率 ÷ 本机最高加速档频率。
    /// <para>
    /// 这是 v1.18.0 新增的主判据。它之所以能与 OS 使用率并列而不是更弱，是因为
    /// **有效频率本身是时间平均量**：一颗 5% 负载的核即使瞬时冲到 4.5 GHz，
    /// 一秒平均下来也只有 200 余 MHz，读不到高活跃度。因此
    /// "活跃度 ≥ 70%" 等价于"这一秒里它大部分时间都在高时钟上执行"，
    /// 正是 turbostat 用 <c>Busy%</c> 想表达的意思（口径差异见
    /// <see cref="CoreBusy.Core.Models.CpuCoreSnapshot.HardwareActivityPercent"/>）。
    /// </para>
    /// </summary>
    public const double HeavyActivityPercent = 70.0;

    /// <summary>
    /// 判定所需的最少证据帧数（v1.18.0 由 8 降为 3）。
    /// <para>
    /// 原先取 8 是因为"1 秒点采样的统计分位"需要足够样本才不发散（8 帧 ≈ 8 秒真实满载）。
    /// 改用**逐核有效频率**后这个理由不再成立：每一帧本身就是硬件在整秒上的积分结果，
    /// 一次读数就是一个可用频率值，不需要靠堆样本量去逼近。降到 3 帧的目的是：
    /// </para>
    /// <list type="bullet">
    ///   <item>挡住**单帧**伪影（偶发的一次坏读数不足以定案）；</item>
    ///   <item>让"跑一段负载就有分"这件事在真实使用下发生 —— 用户不会为了让仪表出数
    ///         而故意满载 8 秒。</item>
    /// </list>
    /// <para>
    /// 样本量变小带来的统计风险由**参照有效性闸**承担（见 <see cref="MinScoredCores"/>）：
    /// 与其让每颗核都攒够 8 帧才算数，不如要求"够多的核都达标"，后者才是真正
    /// 决定这个分数有没有意义的前提。
    /// </para>
    /// </summary>
    public const int MinHeavySamples = 3;

    /// <summary>
    /// 同封装参照成立的**最少达标核数**（v1.18.0）。
    /// <para>
    /// 达标核数少于这个数时，参照中位数会退化成"某一颗核自己"，该核比值恒 1.0 →
    /// 频率达成恒 100 → 总分 100。v1.17.1 实机就出现过这一幕：`100 / 最低 C1 100`，
    /// 而那一刻 8 颗核里只有 1 颗达标。**`-` 是诚实的"没有证据"，`100` 不是。**
    /// </para>
    /// <para>
    /// 取 3 的依据：中位数至少要 3 个点才谈得上"中间"，2 个点的中位数就是平均，
    /// 对"一颗好核 + 一颗坏核"会给出一个掩盖两侧的居中值。
    /// </para>
    /// </summary>
    public const int MinScoredCores = 3;

    /// <summary>
    /// 达标核数相对已跟踪核数的最低占比（v1.18.0，与 <see cref="MinScoredCores"/> 取"与"）。
    /// <para>
    /// 为什么还要一条比例闸：16 核机器上只有 3 颗核达标时，"同封装中位数"其实只描述了
    /// 五分之一的芯片，剩下 13 颗核的状态完全未知 —— 此时给出整机均分是过度外推。
    /// </para>
    /// <para>
    /// 阈值的标定局限**必须说明**：本机是 8 同构核，因此 0.5 只在这类对称拓扑上验证过。
    /// 异构大小核平台（P/E 核频率域本就不同）需要单独标定，不能直接沿用。
    /// </para>
    /// </summary>
    public const double MinScoredCoreRatio = 0.5;

    /// <summary>
    /// 健康统计的**最小帧距**（秒，v1.18.0）。
    /// <para>
    /// turbostat 明确警告测量区间不应短于 1 秒（短于此结果不一致）。采样周期可被用户在
    /// 设置里调到 200 ms，那种帧距下"整秒都在高时钟上"这个前提就不成立了。
    /// </para>
    /// <para>
    /// 取 0.9 而不是 1.0：刷新用 <c>DispatcherTimer(Background)</c>，实测正常抖动可达 ±10%，
    /// 按 1.0 卡会把整帧误丢掉、把有效采样率腰斩。0.9 只拦"用户把周期调快到 200–800 ms"
    /// 这一种情况，代价是这种情况下健康统计按 ~1 秒节拍走（界面刷新照旧快），
    /// 而不是让判定口径随用户的显示偏好漂移。
    /// </para>
    /// </summary>
    public const double MinSampleSpacingSeconds = 0.9;

    /// <summary>
    /// **建立/抬高历史基线**所需的最少满载帧数（60 帧 ≈ 48 秒持续满载）。
    /// <para>
    /// 刻意远高于 <see cref="MinHeavySamples"/>，因为两者要挡的错误完全不同。
    /// </para>
    /// <para>
    /// 实机教训（v1.17.0 首轮验证）：峰值频率当时取的是满载窗口的 **90 分位**，
    /// 而 8 个样本的 90 分位**数学上就是最大值** —— 于是开跑后前几秒的冷启动瞬时高峰
    /// （芯片未热、boost 最激进）被当成"这核的能力上限"钉进基线，之后稳态再也够不着它，
    /// 该核被永久误报为"损耗"。
    /// </para>
    /// <para>
    /// <b>v1.18.0 补充</b>：估计量已由 90 分位改为"最好的四分之一帧的均值"
    /// （见 <c>UpperQuartileMean</c>），同一条教训在**判分**侧也已落实（
    /// <see cref="MinHeavySamples"/> 由 8 降为 3 并配了参照闸）。但基线门槛**不降**：
    /// 判分允许证据少一点（错了只影响一次显示），而基线是**写进磁盘、跨越数月**的锚点，
    /// 钉错了会长期污染参照基准，两者不该用同一个门槛。
    /// </para>
    /// <para>
    /// 实测记录（7940HS，非提权）：C2 在前 8 帧录得 4.25 GHz 并写入基线，
    /// 随后持续满载稳定在 4.01 GHz → 被连判 Normal(89.6) / Watch(76.5) / Poor(69.9)。
    /// 那不是损耗，那是"8 个样本估不出分位"。
    /// </para>
    /// <para>
    /// 样本足够多以后，上四分位均值才真的是"上四分位"：5 帧尖峰混在 80 帧稳态里会被
    /// 平均摊薄掉。基线是跨越数月才算数的锚点，多等 40 秒完全值得；样本不够时基线保持 NaN
    /// （界面显示"尚无历史基线"），评分改由同封装参照承担 —— 那是如实的"证据不足"。
    /// </para>
    /// </summary>
    public const int BaselineMinSamples = 60;

    /// <summary>
    /// 基线被抬高的最小幅度（相对已有站位）。
    /// <para>
    /// 只有"明显更高"才抬高基线。若允许高频刷新成当前值，基线就会被"工况最有利的那一阵"
    /// 压低到不真实的水平，之后每次都在跟自己造出来的高标准比 —— 损耗于是永远测不出来。
    /// </para>
    /// </summary>
    public const double BaselineRaiseMargin = 1.002;

    /// <summary>
    /// 横向（同封装同伴）比较的自由带：与同伴中位数的差距在 5% 以内不扣分。
    /// <para>
    /// 这个带宽不是拍出来的。实机两次独立采样：一台 7940HS 满核满载时八颗核的峰值稳定地
    /// 分布在 3.86 – 4.24 GHz（约 9%），且**两次跑的排序完全一致**（C0/C4 恒最快、
    /// C3/C7 恒最慢）—— 也就是说这是真实的每核体质差异，不是随机噪声。
    /// </para>
    /// <para>
    /// **带宽的定量依据是"同时刻同负载"的那组数**：236 帧时八核峰值为
    /// 4.21 / 3.99 / 4.02 / 3.84 / 4.14 / 3.89 / 4.02 / 3.85，中位数 4.005 ——
    /// 最慢的核只比中位数低 **4.1%**。早期误算成 6.6%，是因为拿"不同时刻记录的基线"
    /// 去比"当前值"，把工况差混进了核间差 —— 那个错误的完整分析见
    /// <see cref="CoreHealthBaselineEntry"/>（结论：锚点必须用无量纲站位，不能用绝对频率）。
    /// 以同时刻数据为准，5% 正好够用且留有余量。
    /// </para>
    /// <para>
    /// 而"体质比同伴差几个百分点"不等于"不健康"：AMD 本来就把最好的核心标为优选核，
    /// 各核的供电与热点位置也天然不同。用窄带（1.5%）去卡，健康机器会被整片报成故障，
    /// 正是本项目一直警惕的"把噪声当信息、顺便制造焦虑"。
    /// </para>
    /// <para>
    /// 带宽要够宽以免误报，惩罚要够陡以免漏报 —— 两者配合，专抓"明显掉队"。
    /// </para>
    /// </summary>
    public const double PeerTolerance = 0.95;

    /// <summary>
    /// 缺口惩罚：比值每低于对应自由带 1 个百分点扣 10 分。
    /// <para>
    /// 这个系数决定"损耗"这条曲线陡不陡，取值经过实测校准（见 .workbuddy/healthprobe）：
    /// </para>
    /// <list type="bullet">
    ///   <item>取 3 时：满载频率比同伴低 7% 的核只扣到 87 分，把明确的异常报成"正常"。</item>
    ///   <item>取 5 时：相对自身历史峰值掉 4.3% 仍得 92 分，用户感知不到"损耗"。</item>
    ///   <item>取 10 时（配合两条自由带）：超出自由带 1% → 90 分，3% → 70 分，5% → 50 分，
    ///         与「轻微 / 明显 / 严重」三档的实际严重程度对得上。</item>
    /// </list>
    /// <para>
    /// **为什么是 10 而不是上一轮标定的 8**：自由带从单一 0.985 拆成两条后，
    /// 纵向这条取 0.98 —— 缺口起点从"低于 1.5%"退到"低于 2%"，同样的 4.3% 退化
    /// 少扣 4 个达成分。把斜率提到 10 恰好把纵向路径的响应拉回标定时的强度
    /// （−4.3% ≈ 77 分），否则这次拆分会在无人察觉的情况下**削弱**最难测、也最重要的那一维。
    /// 斜率本身的含义（"每超出自由带 1% 的严重度"）没有变，变的只是带的位置。
    /// </para>
    /// <para>
    /// 注意惩罚从**自由带边缘**起算，不是从 100% 起算：自由带内的体质差异与测量误差不扣分。
    /// </para>
    /// </summary>
    public const double DeficitPenaltyPerPoint = 10.0;

    /// <summary>稳定性满分线（鲁棒变异系数）。</summary>
    public const double StabilityCvGood = 0.02;

    /// <summary>稳定性零分线（鲁棒变异系数）。</summary>
    public const double StabilityCvBad = 0.12;

    /// <summary>
    /// 稳定性分量所需的**最少工作点样本数**（v1.19.1）。
    /// <para>
    /// 低于此值时 <see cref="CoreHealthScore.StabilityScore"/> 取 NaN，由 <c>Combine</c>
    /// 按剩余权重归一化、覆盖率随之从 100% 降到 85% —— 而不是给 0 分。
    /// </para>
    /// <para>
    /// 这条门槛补的是一个真实事故：实机日志里稳定性分量在
    /// <c>0.00 / 15.70 / 45.17 / 54.49 / 85.60</c> 之间逐秒跳（21 秒内 31 条评级行）。
    /// 根因不是「抖动大」，而是**证据帧只有 3–13 帧**时 MAD 口径的变异系数根本不可用：
    /// 样本里混进一两个升频过程帧，中位数被拉低、MAD 被撑大到「两档间距」量级，于是 0 分。
    /// **0 分会被读成「抖动极大」—— 那是把「没测准」说成了「测得差」。**
    /// </para>
    /// <para>
    /// 取 8 而不是沿用 <see cref="MinHeavySamples"/>（3）的理由：两者要回答的问题不同。
    /// 3 帧足以估计一个**位置型**统计量（上四分位均值 → 峰值频率），因为每一帧本身就是
    /// 硬件在整秒上的积分结果；而 8 帧是**尺度型**统计量（MAD）的可用下限 ——
    /// 4 个样本的中位数只是两点的平均，MAD 会随一帧的进出整档跳变。
    /// 位置量可以少样本，尺度量不行。
    /// </para>
    /// </summary>
    public const int StabilityMinSamples = 8;

    /// <summary>
    /// 计入抖动统计的最低频率（相对该核**峰值**，v1.19.1）。
    /// <para>
    /// 抖动只在**一个工作点上**才有定义。证据帧里混着升频过程帧时（实测：起步帧有效频率
    /// 1519 MHz，稳态 4.1 GHz），算出来的变异系数描述的是「工况建立了没有」，
    /// 不是「时钟稳不稳」—— 却会被当成后者扣分。
    /// </para>
    /// <para>
    /// 取 0.70 的依据（本机实测）：
    /// </para>
    /// <list type="bullet">
    ///   <item>污染帧落在峰值的 **0.37** 处（1519 / 4100 MHz）—— 必须被挡掉；</item>
    ///   <item>真实抖动用例落在 **0.86** 处（4.40 / 3.80 GHz 交替，见 healthprobe 的
    ///         <c>CaseJitteryClock</c>）—— 必须被保留，否则这里会变成「把不稳定藏起来」。</item>
    /// </list>
    /// <para>
    /// 0.70 把两者分开且两侧都留有余量。**已知代价（必须说明）**：摆幅超过本核峰值
    /// 30% 的时钟摆动（例如 4.4 ↔ 3.0 GHz）会被当成「不在工作点上」而退出统计，
    /// 于是稳定性被高估。这类摆动已经不是「工作点抖不抖」能描述的形态，
    /// 它更可能表现为峰值的同伴比较（达成分量）异常 —— 但那不是等价替代，本分量在
    /// 该形态下确实会漏报。
    /// </para>
    /// </summary>
    public const double StabilityWorkingPointRatio = 0.70;

    /// <summary>
    /// 评级跨档所需的连续确认时长（秒，v1.19.1）。
    /// <para>
    /// 评级是**结论**，不是读数 —— 它不该跟着工况逐帧变色。实测本机 6 s 开 / 4 s 关的
    /// 占空满载下，封装温度在 10 秒内摆动 13.7 ℃，热裕度分量随之上下来回，
    /// 总分在 82.8–96.4 之间摆动 → 87 帧里跨 90 分档 **14 次**（Normal ↔ Good）。
    /// </para>
    /// <para>
    /// 为什么不用「缓冲带」（照抄累积排名那种 0.4 个百分点的做法）：那种滞回挡的是
    /// **噪声级**的越界；这里的振幅是 13.6 分，要挡住它需要 6 分以上的死区，
    /// 而分档本身只有 10 分宽 —— 死区会吃掉大半个档位，一颗稳定在 91 分的核会被长期
    /// 标成「正常」。**在时间上做滞回与振幅无关**，这才是这类抖动的正确解法。
    /// </para>
    /// <para>
    /// 取 20 秒：两倍于实测的工况周期（10 s），且落在封装热时间常数（数秒至数十秒）量级上。
    /// 对以分钟计的评分窗口（<see cref="WindowSize"/> = 240 帧 ≈ 3–4 分钟）而言，
    /// 20 秒的确认延迟可以忽略；对以秒计的抖动，它是完全过滤。
    /// </para>
    /// <para>
    /// 两条**刻意的例外**（见 <c>ResolveConfirmedGrade</c>）：
    /// ① 瞬时评级变成 Unknown（失去证据）时**立即**回落 ——「没有读数」必须马上如实呈现；
    /// ② 首次判定**立即**生效 —— 滞回是给「已经成立的结论」服务的，不是让用户干等 20 秒。
    /// </para>
    /// </summary>
    public const double GradeConfirmSeconds = 20.0;

    /// <summary>
    /// 热余量的舒适线（℃）：不高于此温度**不扣分**。
    /// <para>
    /// 这一条是为"别制造焦虑"服务的。笔记本满载 70–85 ℃ 完全正常，
    /// 若从 50 ℃ 就开始线性扣分，满大街的机器都会得到 50 上下的裕度分，
    /// 用户于是学到"我的 CPU 一直不太好"—— 那是噪声，不是信息。
    /// 只有逼近 TjMax 才真的构成风险。
    /// </para>
    /// </summary>
    public const double ThermalComfortC = 70.0;

    /// <summary>热余量的归零点（℃）：达到或超过此温度，封装裕度归零。</summary>
    public const double ThermalCeilingC = 95.0;

    /// <summary>
    /// 同封装参照分位：取**中位数**。
    /// <para>
    /// 这是本类最关键的一个口径选择，也是实机数据逼出来的：
    /// 一台 7940HS 满载时八颗核的峰值散布在 3.86 – 4.20 GHz（约 8%），
    /// 其中相当一部分是正常的分档差异与测量抖动。
    /// </para>
    /// <list type="bullet">
    ///   <item>用 <b>90 分位</b>（≈最好的那一颗）当标尺 → 大半核心被判"掉队"扣到 50 分，
    ///         把正常体质差异报成故障。</item>
    ///   <item>用 <b>中位数</b> 当标尺 → 典型核心得满分，只有明显偏离的少数核心被扣分。</item>
    /// </list>
    /// <para>
    /// 顺便澄清一个**曾经写错的说法**：早先这里写着"整颗芯片一起变差由纵向基线负责发现，
    /// 两个参照各管一个方向"。后半句对、前半句错 —— 纵向站位**同样**是横向比较
    /// （该核 ÷ 同伴中位数），整颗芯片一起变慢时它也不动。真正的原因是：
    /// **整颗芯片一起变慢与散热/功耗工况的变化在几%量级上不可区分**，
    /// 所以本工具不声称能测它，而不是换一个参照就能测。见 <see cref="CoreHealthBaselineEntry"/>。
    /// </para>
    /// </summary>
    public const double PeerPercentile = 0.50;

    /// <summary>
    /// 每个已纠正 WHEA 错误从**总分**里扣掉的分数。
    /// <para>
    /// 注意这里是扣总分，不是扣某个分量的分。早先的写法把它塞进"封装裕度"分量内部，
    /// 于是 25% 的权重把一次硬件错误稀释成 −1.25 分 —— 而机器检查异常是硬件自己报出来的
    /// 唯一直接证据，不该被权重稀释到这个地步。
    /// </para>
    /// </summary>
    public const double WheaPenaltyPerCorrected = 5.0;

    /// <summary>已纠正错误的累计扣分上限，避免一次爆发把分数打到地板。</summary>
    public const double WheaCorrectedPenaltyCap = 25.0;

    /// <summary>
    /// 出现**未纠正**硬件错误时的总分上限。
    /// <para>
    /// 未纠正的机器检查异常是"已经发生过的事实"，不是一个可以和其他指标平均掉的扣分项：
    /// 一台抛过不可恢复硬件错误、却因为温度凉快、频率漂亮而显示"健康度 92"的机器，
    /// 是在骗人。所以这里用硬上限而不是加权扣分。
    /// </para>
    /// </summary>
    private const double FatalWheaScoreCap = 60.0;

    /// <summary>三个分量的权重（未归一化）。</summary>
    private const double WeightClock = 0.60;
    private const double WeightThermal = 0.25;
    private const double WeightStability = 0.15;

    /// <summary>
    /// 全表心跳日志的间隔（秒）。
    /// <para>
    /// 评级只在**跨档**时写一行，这样不刷屏，但代价是"分数到底稳不稳"无法取证 ——
    /// v1.17.0 首轮验证就是吃了这个亏：只看到 C2 的几次跨档，看不到其余七核在同一个窗口里
    /// 参照与峰值怎么漂，于是无法判断那是芯片问题还是参照不稳。
    /// 本表每轮把**所有**核心的完整读数落一行（8 核约 200 字节），足以回答"漂移/稳定"。
    /// </para>
    /// </summary>
    private const int HeartbeatSeconds = 120;

    private sealed class CoreWindow
    {
        public readonly double[] Usage = new double[WindowSize];
        public readonly double[] Freq = new double[WindowSize];

        /// <summary>同帧的硬件活跃度（%）。缺失为 NaN —— 判定时会退回到只看使用率。</summary>
        public readonly double[] Activity = new double[WindowSize];

        /// <summary>
        /// 同帧频率是否取自"有效频率"口径（true）还是回退口径（false）。
        /// 与 <see cref="Freq"/> 同下标 —— 判定要把"这个分数建立在哪个口径上"如实带出去，
        /// 只记累计次数不够（窗口是环形的，早期帧会被覆盖）。
        /// </summary>
        public readonly bool[] EffectiveBasis = new bool[WindowSize];

        public int WriteIndex;
        public int Filled;

        /// <summary>最近一次被**接受**的帧的时刻，用于界面标注"数据有多新"。</summary>
        public DateTime LastSeenUtc = DateTime.MinValue;

        /// <summary>上一帧被接受的时刻，用于执行 <see cref="MinSampleSpacingSeconds"/> 节拍闸。</summary>
        public DateTime LastAcceptedUtc = DateTime.MinValue;

        /// <summary>因帧距过短被丢弃的帧数（只在首次与汇总时落日志，不逐帧刷屏）。</summary>
        public int TooFastFrames;

        /// <summary>因频率读数缺失而未进统计的帧数（界面据此区分"没负载"与"读不到频率"）。</summary>
        public int FrequencyMissingFrames;

        /// <summary>本核最近一次的逐核电压（V）；读不到为 NaN。只作展示与存档。</summary>
        public double LastVoltageV = double.NaN;

        /// <summary>本核最近一次的硬件活跃度（%）；读不到为 NaN。</summary>
        public double LastActivityPercent = double.NaN;

        /// <summary>上次写入日志时的评级，用于只在**跨档**时落一行，避免刷屏。</summary>
        public CoreHealthGrade LastLoggedGrade = CoreHealthGrade.Unknown;

        /// <summary>当前**已确认**的展示评级（带滞回，v1.19.1）。Unknown = 尚无证据。</summary>
        public CoreHealthGrade StableGrade = CoreHealthGrade.Unknown;

        /// <summary>正在等待确认的候选评级；等于 <see cref="StableGrade"/> 表示「无候选」。</summary>
        public CoreHealthGrade PendingGrade = CoreHealthGrade.Unknown;

        /// <summary>候选评级**首次**出现的时刻；中途被打断即重取（见 ResolveConfirmedGrade）。</summary>
        public DateTime PendingSinceUtc = DateTime.MinValue;

        /// <summary>清空跨档候选 —— 瞬时评级回到已确认档、或失去证据时调用。</summary>
        public void ClearPendingGrade()
        {
            PendingGrade = CoreHealthGrade.Unknown;
            PendingSinceUtc = DateTime.MinValue;
        }

        public void Push(double usage, double freq, double activity, bool effectiveBasis, DateTime nowUtc)
        {
            Usage[WriteIndex] = usage;
            Freq[WriteIndex] = freq;
            Activity[WriteIndex] = activity;
            EffectiveBasis[WriteIndex] = effectiveBasis;
            WriteIndex = (WriteIndex + 1) % WindowSize;
            if (Filled < WindowSize)
                Filled++;
            LastSeenUtc = nowUtc;
            LastAcceptedUtc = nowUtc;
        }
    }

    private readonly Dictionary<string, CoreWindow> _windows = new(StringComparer.Ordinal);

    /// <summary>历史基线（构造时由持久化层注入的**同一个引用**，本类就地更新，由宿主负责落盘）。</summary>
    private readonly Dictionary<string, CoreHealthBaselineEntry> _baselines;

    private double _packageTempC = double.NaN;
    private int _wheaCorrected;
    private int _wheaFatal;
    private bool _wheaAvailable;
    private bool _inventoryLogged;
    private bool _spacingNoticeLogged;
    private DateTime _lastHeartbeatUtc = DateTime.MinValue;

    /// <param name="baselines">
    /// 历史基线表（可为空）。传入的字典会被**就地更新** —— 这样宿主不必在每次采样后
    /// 做一次"取出-合并-写回"，也就不会漏掉更新。
    /// </param>
    public CoreHealthTracker(Dictionary<string, CoreHealthBaselineEntry>? baselines = null)
        => _baselines = baselines ?? new Dictionary<string, CoreHealthBaselineEntry>(StringComparer.Ordinal);

    /// <summary>已被观测过的核心数（含尚未攒够样本的）。</summary>
    public int TrackedCoreCount => _windows.Count;

    /// <summary>
    /// 记一帧。<paramref name="packageTempC"/> 由调用方从同一帧的 CPU 快照传入，
    /// 读不到时传 NaN（**不要传 0**，否则会把"读不到"当成 0 ℃ 满裕度）。
    /// <para>
    /// 频率取值口径（v1.18.0，优先级从上到下）：
    /// </para>
    /// <list type="number">
    ///   <item><see cref="CpuCoreSnapshot.EffectiveFrequencyGHz"/> —— 逐核有效频率（驻留加权），
    ///         这是唯一"一次读数就是一个可用频率值"的量，判定采用它；</item>
    ///   <item><see cref="CpuCoreSnapshot.FrequencyGHz"/> —— P-state 档位时钟。**它不能判分**
    ///         （离散档位、与负载无关，本机 8 核读到同一个 4567 MHz，比值恒 1 → 人人满分），
    ///         仅在有效频率缺失时**降级使用**并把口径如实带进
    ///         <see cref="CoreHealthScore.FrequencyBasis"/>，让界面能标注"这个分数建立在
    ///         较差的口径上"。</item>
    /// </list>
    /// </summary>
    public void Observe(IReadOnlyList<CpuCoreSnapshot> cores, double packageTempC, DateTime nowUtc)
    {
        _packageTempC = packageTempC;

        if (cores.Count > 0)
            PruneWindows(cores);

        foreach (var core in cores)
        {
            if (string.IsNullOrEmpty(core.Id))
                continue;

            if (!_windows.TryGetValue(core.Id, out var window))
            {
                window = new CoreWindow();
                _windows[core.Id] = window;
            }

            // 电压与活跃度**先记**：即使本帧频率读不到，这两个值仍然有展示价值
            // （且电压正是"频率读不到"时唯一还在动的那一列）。
            window.LastVoltageV = core.CoreVoltageV;
            window.LastActivityPercent = core.HardwareActivityPercent;

            // 口径选择：优先有效频率（驻留加权），缺失时降级到 P-state 档位时钟。
            // **判据是"NaN 与否"，不是"大于 0"**：有效频率读到 0 是一个**真实读数**
            // （该核整秒没在执行），必须当成有效频率口径处理 —— 若因 0 而回退到 P-state，
            // 一颗停在深睡态的核会被读成"处在最高档"，正好把结论颠倒过来。
            double freq;
            var effectiveBasis = true;
            if (!double.IsNaN(core.EffectiveFrequencyGHz))
            {
                freq = core.EffectiveFrequencyGHz;
            }
            else if (!double.IsNaN(core.FrequencyGHz) && core.FrequencyGHz > 0)
            {
                freq = core.FrequencyGHz;
                effectiveBasis = false;
            }
            else
            {
                // 频率缺失（NaN）的帧不进统计：把它当成 0 会把均值拉塌，当成上一帧又会伪造稳定。
                window.FrequencyMissingFrames++;
                continue;
            }

            // 节拍闸（v1.18.0）：帧距过短会让"整秒都在高时钟上"这个前提失效（见 MinSampleSpacingSeconds）。
            if (window.LastAcceptedUtc != DateTime.MinValue
                && (nowUtc - window.LastAcceptedUtc).TotalSeconds < MinSampleSpacingSeconds)
            {
                window.TooFastFrames++;
                if (!_spacingNoticeLogged)
                {
                    _spacingNoticeLogged = true;
                    HealthLog.Write(
                        $"[HEALTH] 采样周期短于 {MinSampleSpacingSeconds:0.0} 秒：健康统计按该节拍取帧"
                        + "（界面刷新不受影响）。判定口径不随用户的显示偏好漂移。");
                }

                continue;
            }

            window.Push(core.UsagePercent, freq, core.HardwareActivityPercent, effectiveBasis, nowUtc);
        }
    }

    /// <summary>更新 WHEA 纠错计数（由 Windows 层的事件日志读取器周期性喂入）。</summary>
    public void SetWhea(int corrected, int fatal, bool available)
    {
        _wheaCorrected = Math.Max(0, corrected);
        _wheaFatal = Math.Max(0, fatal);
        _wheaAvailable = available;
    }

    /// <summary>
    /// 丢掉已经不在当前拓扑里的核心窗口（例如用户改了核心分区口径、或热插拔场景）。
    /// <para>
    /// 只在 <paramref name="cores"/> 非空时调用：采集层尚未就绪时可能给出空列表，
    /// 那种"暂时看不到核心"绝不能等价于"核心没了"，否则会白白清空几十分钟的样本。
    /// </para>
    /// </summary>
    private void PruneWindows(IReadOnlyList<CpuCoreSnapshot> cores)
    {
        if (_windows.Count == 0)
            return;

        HashSet<string>? live = null;
        foreach (var core in cores)
        {
            if (string.IsNullOrEmpty(core.Id))
                continue;

            (live ??= new HashSet<string>(StringComparer.Ordinal)).Add(core.Id);
        }

        if (live is null)
            return;

        List<string>? stale = null;
        foreach (var id in _windows.Keys)
        {
            if (!live.Contains(id))
                (stale ??= []).Add(id);
        }

        if (stale is null)
            return;

        foreach (var id in stale)
        {
            _windows.Remove(id);
            HealthLog.Write($"[HEALTH] 核心 {id} 已不在当前拓扑中，采样窗口丢弃");
        }
    }

    /// <summary>
    /// 计算当前所有核心的评分。每帧调用一次，成本为 O(核数 × 窗口)。
    /// <para>
    /// **本方法有副作用**：会就地抬高构造时注入的基线字典（见 <see cref="UpdateBaseline"/>），
    /// 宿主负责把该字典按期落盘。之所以把刷新放在这里而不是另开一个方法，
    /// 是因为"基线只在满载样本上更新"这个条件只有本方法掌握；
    /// 让调用方自己再判断一次，早晚会漏。
    /// </para>
    /// </summary>
    public IReadOnlyList<CoreHealthScore> Score()
    {
        // ── 第一遍：取每核的满载统计量。
        var peaks = new List<double>(_windows.Count);
        var stats = new Dictionary<string, CoreStats>(_windows.Count, StringComparer.Ordinal);
        var eligible = 0;

        foreach (var (id, window) in _windows)
        {
            var stat = CoreStats.From(window);
            stats[id] = stat;
            if (stat.HasEvidence)
            {
                eligible++;
                peaks.Add(stat.PeakGHz);
            }
        }

        // ── 参照有效性闸（v1.18.0）。
        //    这一步必须在算参照**之前**：达标核太少时中位数会退化成"某一颗核自己"，
        //    该核比值恒 1.0 → 频率达成满分 → 总分 100。v1.17.1 实机出现过这一幕
        //    （8 核里只有 1 核达标，界面报 `100 / 最低 C1 100`）。宁可全体显示 "-"，
        //    也不能把"证据不足"渲染成"整机满分" —— 前者是诚实的沉默，后者是误导。
        var referenceValid = IsPeerReferenceValid(eligible, _windows.Count);

        // ── 参照频率：同封装各核峰值的中位数。
        //    **必须先排序** —— Percentile 假定入参升序，直接拿字典遍历顺序喂进去会得到一个
        //    毫无意义的数（首次实机验证时就是这么拿到 4.03 而非 4.19 的：
        //    字典序是 C0..C7，插值落在 4.10 与 3.86 之间，恰好是个看着挺合理的错值）。
        var peerReference = double.NaN;
        if (referenceValid)
        {
            peaks.Sort();
            peerReference = Percentile(peaks, PeerPercentile);
        }

        // ── 热裕度（分量 B）：全核共担，只算一次。温度不可读时是 NaN，权重由 Combine 归一化。
        var thermalScore = ComputeThermalScore();

        var result = new List<CoreHealthScore>(_windows.Count);

        foreach (var (id, stat) in stats)
        {
            // ── 历史站位：**只作为信息展示，不参与评分**。
            //    存的是"该核曾经达到过的最好站位"，折算成当前同伴水平下的当量频率供人看。
            //    为什么不进评分：这个估计量本身带 ~2.5% 的系统性偏差（实测两轮：八核的历史最好
            //    站位一致比当前实测站位高 0.2%–4.6%，平均 −2.5%）—— 因为锚记的是站位偶然冲高的
            //    那一瞬，而当前值取的是窗口统计量。既然偏差大于我们要找的信号（数年尺度的硅老化
            //    远小于 2.5%），把差值折成分数就只是在报估计噪声。详见 CoreHealthBaselineEntry。
            var baselineRatio = _baselines.TryGetValue(id, out var anchor) && anchor.RatioToPeer > 0
                ? anchor.RatioToPeer
                : double.NaN;

            var baseline = double.IsNaN(baselineRatio) || double.IsNaN(peerReference)
                ? double.NaN
                : peerReference * baselineRatio;

            // 参照只有一个来源：同封装各核峰值的中位数。自由带也随之只有一条（见 PeerTolerance）。
            var reference = peerReference;
            const double tolerance = PeerTolerance;

            // 未判定的原因按"最需要用户行动"的顺序取第一条：先看有没有读数，
            // 再看有没有样本，最后才说参照不成立（前两条是等待能解决的，最后一条不是）。
            var reason = !stat.HasFrequency
                ? CoreHealthUnscoredReason.NoFrequency
                : !stat.HasEvidence
                    ? CoreHealthUnscoredReason.NotEnoughHeavyFrames
                    : !referenceValid
                        ? CoreHealthUnscoredReason.PeerReferenceInvalid
                        : CoreHealthUnscoredReason.None;

            var hasSamples = reason == CoreHealthUnscoredReason.None;

            // 样本不足时**三个分项一并置 NaN**：否则界面会出现"总分是 -，但裕度有 88"的怪状态，
            // 而那个 88 对用户毫无意义（他连这一核的达成情况都还不知道）。
            var clock = hasSamples ? ComputeClockScore(stat.PeakGHz, reference, tolerance) : double.NaN;

            // 稳定性分量有自己的样本门槛（v1.19.1，见 StabilityMinSamples）：
            // 工作点样本不够时按「缺项」处理（NaN + 覆盖率下降），而不是给 0 分。
            var stability = hasSamples && stat.StabilitySamples >= StabilityMinSamples
                ? ComputeStabilityScore(stat.RobustCv)
                : double.NaN;

            var thermal = hasSamples ? thermalScore : double.NaN;

            var (score, coverage) = hasSamples
                ? Combine(clock, thermal, stability)
                : (double.NaN, 0.0);

            // WHEA 在**总分**上生效，不进分量（详见 WheaPenaltyPerCorrected）。
            score = ApplyWhea(score);

            var window = _windows[id];

            // ── 评级滞回（v1.19.1）：见 GradeConfirmSeconds 与 ResolveConfirmedGrade。
            //    滞回作用在**评级**上而不是分数上：分数是读数（逐帧照实给），评级是结论（要站得住）。
            var confirmedGrade = ResolveConfirmedGrade(
                window, CoreHealthScore.GradeOf(score), window.LastSeenUtc);

            var entry = new CoreHealthScore
            {
                CoreId = id,
                Score = score,
                ConfirmedGrade = confirmedGrade,
                ClockScore = clock,
                ThermalScore = thermal,
                StabilityScore = stability,
                StabilitySamples = stat.StabilitySamples,
                PeakGHz = hasSamples ? stat.PeakGHz : double.NaN,
                ReferenceGHz = reference,
                BaselineRatio = baselineRatio,
                BaselineGHz = baseline,
                HeavySamples = stat.HeavySamples,
                RequiredSamples = MinHeavySamples,
                CoveragePercent = coverage,
                FrequencyBasis = stat.Basis,
                UnscoredReason = reason,
                ReferenceValid = referenceValid,
                EligibleCoreCount = eligible,
                RequiredEligibleCores = RequiredEligibleCores(_windows.Count),
                TrackedCoreCount = _windows.Count,
                CoreVoltageV = window.LastVoltageV,
                HardwareActivityPercent = window.LastActivityPercent,
                SampledAt = window.LastSeenUtc == DateTime.MinValue
                    ? default
                    : window.LastSeenUtc.ToLocalTime(),
                PackageTempC = _packageTempC,
                WheaCorrectedCount = _wheaCorrected,
                WheaFatalCount = _wheaFatal,
                WheaAvailable = _wheaAvailable,
            };

            result.Add(entry);
            LogGradeTransition(entry, window);
            UpdateBaseline(id, stat, peerReference);
        }

        LogInventoryOnce(result.Count);
        LogHeartbeatIfDue(result);
        return result;
    }

    /// <summary>
    /// 全表心跳：每隔 <see cref="HeartbeatSeconds"/> 秒把所有核心的完整读数落一行。
    /// <para>
    /// 列的含义：<c>总分(峰/参同/站/样/活)[口径]</c>。参照恒为同封装中位数（标 <c>同</c>），
    /// 站位是该核历史最好站位比值（仅展示、不计分），活=硬件活跃度，样=证据帧数，
    /// 方括号里是频率口径（<c>有效</c> / <c>档位</c>）。未判分的核额外带 <c>!</c> 与原因码。
    /// 这条日志是判断"分数波动源于芯片还是源于同伴参照"的唯一依据。
    /// </para>
    /// <para>
    /// v1.18.0 增补：把**未判分的原因**写进心跳。v1.17.1 排查"10 分钟没显示"时，
    /// 心跳只写 `-(样0)`，无法区分"没负载"和"参照不成立"，只能靠人肉比对界面与代码。
    /// </para>
    /// </summary>
    private void LogHeartbeatIfDue(IReadOnlyList<CoreHealthScore> scores)
    {
        if (scores.Count == 0)
            return;

        var now = DateTime.UtcNow;
        if (_lastHeartbeatUtc != DateTime.MinValue
            && (now - _lastHeartbeatUtc).TotalSeconds < HeartbeatSeconds)
        {
            return;
        }

        _lastHeartbeatUtc = now;

        var parts = new List<string>(scores.Count + 1);
        foreach (var s in scores)
        {
            // 站位（基/当量）一并带上：它是"这颗核相对同伴的位置"，人工判读时最有用的一列，
            // 但它**不参与分数**（理由见 CoreHealthBaselineEntry）。
            var standing = double.IsNaN(s.BaselineRatio)
                ? "站-"
                : $"站{s.BaselineRatio:0.000}";

            var basis = s.FrequencyBasis == CoreFrequencySource.SensorEffective ? "有效" : "档位";

            var line = $"{s.CoreId} {Fmt(s.Score)}(峰{Fmt(s.PeakGHz)}/参{Fmt(s.ReferenceGHz)}同"
                       + $"/{standing}/样{s.HeavySamples}/活{Fmt(s.HardwareActivityPercent)})[{basis}]";

            // 未判定时把原因直接写出来 —— 这一列的存在就是为了让"沉默的横杠"变成可读的结论。
            if (s.UnscoredReason != CoreHealthUnscoredReason.None)
                line += $"!{ReasonCode(s.UnscoredReason)}";

            parts.Add(line);
        }

        parts.Insert(
            0,
            referenceValidTag(scores));

        HealthLog.Write($"[HEALTH·表] {now.ToLocalTime():HH:mm:ss} " + string.Join("  ", parts));
    }

    /// <summary>心跳行的参照状态前缀：让"为什么全体都是 -"一眼可见。</summary>
    private static string referenceValidTag(IReadOnlyList<CoreHealthScore> scores)
    {
        var first = scores[0];
        return first.ReferenceValid
            ? $"参照OK({first.EligibleCoreCount}/{first.TrackedCoreCount})"
            : $"参照不足({first.EligibleCoreCount}/{first.TrackedCoreCount}，需{first.RequiredEligibleCores})";
    }

    /// <summary>未判定原因的短码（日志用，与枚举一一对应）。</summary>
    private static string ReasonCode(CoreHealthUnscoredReason reason) => reason switch
    {
        CoreHealthUnscoredReason.NoFrequency => "无频率",
        CoreHealthUnscoredReason.NotEnoughHeavyFrames => "样本不足",
        CoreHealthUnscoredReason.PeerReferenceInvalid => "参照不足",
        _ => "已判定",
    };

    /// <summary>
    /// WHEA 惩罚（作用于总分，独立于分量）。
    /// <para>
    /// 与分量的关键区别：分量可以"缺席"（读不到就归一化），而机器检查异常是**事实**，
    /// 不依赖任何传感器是否可读。所以未提权时温度读不到、热裕度缺席，
    /// 但只要事件日志里有硬件错误，它照样要扣分。
    /// </para>
    /// </summary>
    private double ApplyWhea(double score)
    {
        if (double.IsNaN(score) || !_wheaAvailable)
            return score;

        if (_wheaFatal > 0)
            return Math.Min(score, FatalWheaScoreCap);

        if (_wheaCorrected > 0)
            return Math.Max(0, score - Math.Min(_wheaCorrected * WheaPenaltyPerCorrected, WheaCorrectedPenaltyCap));

        return score;
    }

    /// <summary>
    /// 基线刷新：只在**足够长的**满载窗口上、且**站位明显更高**（相对同封装中位数）时才抬高。
    /// <para>
    /// 判据是"站位"而不是绝对频率 —— 这是本方法唯一重要的一点。早先比的是绝对 GHz
    /// （<c>stat.PeakGHz &gt; existing.PeakGHz * 1.002</c>），于是任何一次工况上浮
    /// （芯片刚凉、功耗档切换、甚至读数整体漂移）都会把锚点抬到够不着的地方，
    /// 而且这个虚高的锚还会写进磁盘长期使用。改比站位后，这类整体漂移在比值上自动抵消。
    /// </para>
    /// <para>
    /// 不高频刷新的理由不变：只有"明显更高"才动，否则锚点会被当前这一阵的水平拖低，
    /// 之后都在跟自己造出来的低标准比，掉队永远测不出来。
    /// </para>
    /// </summary>
    private void UpdateBaseline(string coreId, CoreStats stat, double peerReference)
    {
        // 门槛是 BaselineMinSamples 而不是 MinHeavySamples —— 理由见该常量的注释：
        // 样本太少时 90 分位退化成最大值，会把冷启动瞬时高峰钉成永久锚点。
        if (stat.HeavySamples < BaselineMinSamples || stat.PeakGHz <= 0)
            return;

        // 没有同伴参照就算不出站位。单核机器（或同伴样本都不足）会走到这里 ——
        // 此时如实不建立基线，而不是退化成一个绝对频率锚。
        if (double.IsNaN(peerReference) || peerReference <= 0)
            return;

        var ratio = stat.PeakGHz / peerReference;

        _baselines.TryGetValue(coreId, out var existing);

        if (existing is not null
            && existing.RatioToPeer > 0
            && ratio <= existing.RatioToPeer * BaselineRaiseMargin)
        {
            return;
        }

        _baselines[coreId] = new CoreHealthBaselineEntry
        {
            RatioToPeer = ratio,
            PeakGHz = stat.PeakGHz,
            ObservedAt = DateTime.Now,
        };
    }

    /// <summary>
    /// 频率达成度分量（0–100）。
    /// <paramref name="tolerance"/> 是本次参照来源对应的自由带（本版本只有横向一条，
    /// 即 <see cref="PeerTolerance"/>），由调用方决定；本方法只管把超出自由带的缺口折算成分数。
    /// </summary>
    private static double ComputeClockScore(double peakGHz, double referenceGHz, double tolerance)
    {
        if (double.IsNaN(peakGHz) || double.IsNaN(referenceGHz) || referenceGHz <= 0)
            return double.NaN;

        var ratio = peakGHz / referenceGHz;
        if (ratio >= tolerance)
            return 100.0;

        // 缺口从自由带边缘起算，不是从 100% 起算：带内的体质差异与测量误差不扣分。
        var deficitPoints = (tolerance - ratio) * 100.0;
        return Math.Clamp(100.0 - deficitPoints * DeficitPenaltyPerPoint, 0, 100);
    }

    private static double ComputeStabilityScore(double robustCv)
    {
        if (double.IsNaN(robustCv))
            return double.NaN;

        if (robustCv <= StabilityCvGood)
            return 100.0;
        if (robustCv >= StabilityCvBad)
            return 0.0;

        return 100.0 * (StabilityCvBad - robustCv) / (StabilityCvBad - StabilityCvGood);
    }

    /// <summary>
    /// 热裕度分量（0–100）。温度不可读时返回 NaN（**不是 100**）。
    /// <para>
    /// 这里踩过一次实机才会发现的坑：早先把 WHEA 也并进这个方法，并在温度缺失时
    /// 用 <c>100</c> 做 WHEA 的起算基准，结果非提权会话（温度恒为 NaN）会一路返回 100 ——
    /// 日志里同时出现"温度不可读，裕度分量不参与"和"裕度=100.00 覆盖率=100%"，
    /// 自相矛盾且把"没测到"伪装成了"很理想"。WHEA 已拆出去（见 <see cref="ApplyWhea"/>）。
    /// </para>
    /// </summary>
    private double ComputeThermalScore()
    {
        if (double.IsNaN(_packageTempC))
            return double.NaN;

        // 舒适线以下满分，再往上线性衰减到归零点。
        var margin = (_packageTempC - ThermalComfortC) / (ThermalCeilingC - ThermalComfortC);
        return Math.Clamp(1.0 - margin, 0, 1) * 100.0;
    }

    /// <summary>
    /// 按可用分量加权合成。**缺项按剩余权重归一化** —— 这是本类最容易写错的一处：
    /// 直接把缺失分量当 0 分会让非提权用户看到全线崩坏的假象，
    /// 当成满分又会让"读不到温度"和"温度很理想"看起来一样。
    /// </summary>
    private static (double Score, double Coverage) Combine(
        double clock, double thermal, double stability)
    {
        double weightSum = 0, weighted = 0;

        if (!double.IsNaN(clock))
        {
            weightSum += WeightClock;
            weighted += clock * WeightClock;
        }

        if (!double.IsNaN(thermal))
        {
            weightSum += WeightThermal;
            weighted += thermal * WeightThermal;
        }

        if (!double.IsNaN(stability))
        {
            weightSum += WeightStability;
            weighted += stability * WeightStability;
        }

        if (weightSum <= 0)
            return (double.NaN, 0);

        var coverage = 100.0 * weightSum / (WeightClock + WeightThermal + WeightStability);
        return (Math.Clamp(weighted / weightSum, 0, 100), coverage);
    }

    /// <summary>
    /// 把逐帧跳动的瞬时评级过滤成**需要连续确认**的展示评级（v1.19.1，见 GradeConfirmSeconds）。
    /// <para>
    /// 状态机很窄，但三条分支各自都对应一个真实后果：
    /// </para>
    /// <list type="number">
    ///   <item><b>无证据 → 立即 Unknown</b>：不给滞回。读数没了却还挂着上一档的颜色，
    ///         等于用旧结论冒充当前状态 —— 与本项目「NaN 一律降级为 -」的约定同源。</item>
    ///   <item><b>首次判定 → 立即生效</b>：不给滞回。否则用户要多等 20 秒才看到第一个结论，
    ///         而滞回的用途是「不让已成立的结论被推翻」，不是「让第一个结论也排队」。</item>
    ///   <item><b>换档 → 需连续 <see cref="GradeConfirmSeconds"/> 秒</b>：候选档只有在这期间
    ///         **每一帧都成立**才被采纳；中途任何一帧回到旧档（或跳到第三档），计时从头开始。
    ///         这一步就是实测「87 帧跨档 14 次」→ 0 次的来源。</item>
    /// </list>
    /// <para>
    /// 用时间而不是帧数：采样周期可由用户在 200–2000 ms 之间设置，帧数门槛会让「确认时长」
    /// 随显示偏好漂移（同一类错误在 <see cref="MinSampleSpacingSeconds"/> 那里已经避免过一次）。
    /// 时刻取**该核最近一帧被接受的时刻**而非墙钟 —— <c>Score()</c> 只在有新帧时才有意义，
    /// 用墙钟会让「没有新数据」的时间也算进确认时长。
    /// </para>
    /// </summary>
    private static CoreHealthGrade ResolveConfirmedGrade(
        CoreWindow window, CoreHealthGrade rawGrade, DateTime nowUtc)
    {
        if (rawGrade == CoreHealthGrade.Unknown)
        {
            window.ClearPendingGrade();
            return CoreHealthGrade.Unknown;
        }

        if (window.StableGrade == CoreHealthGrade.Unknown)
        {
            window.ClearPendingGrade();
            window.StableGrade = rawGrade;
            return rawGrade;
        }

        if (rawGrade == window.StableGrade)
        {
            window.ClearPendingGrade();
            return window.StableGrade;
        }

        if (window.PendingGrade != rawGrade)
        {
            window.PendingGrade = rawGrade;
            window.PendingSinceUtc = nowUtc;
            return window.StableGrade;
        }

        if ((nowUtc - window.PendingSinceUtc).TotalSeconds < GradeConfirmSeconds)
            return window.StableGrade;

        window.ClearPendingGrade();
        window.StableGrade = rawGrade;
        return rawGrade;
    }

    /// <summary>
    /// 评级跨档时落一行日志 —— 只在边沿写，不逐帧刷屏。
    /// <para>
    /// v1.19.1 起比较的是**确认后的评级**（<see cref="CoreHealthScore.Grade"/>）而不是瞬时档位：
    /// 日志要回答的是「用户看到的角标什么时候变了」。被滞回压住的瞬时越界不落日志 ——
    /// 这正是它不该刷屏的证明；要看瞬时档位请用回放工具读
    /// <see cref="CoreHealthScore.RawGrade"/>。
    /// </para>
    /// </summary>
    private static void LogGradeTransition(CoreHealthScore score, CoreWindow window)
    {
        if (score.Grade == window.LastLoggedGrade)
            return;

        window.LastLoggedGrade = score.Grade;

        if (score.Grade == CoreHealthGrade.Unknown)
            return;

        HealthLog.Write(
            $"[HEALTH] {score.CoreId} 评级 {score.Grade}：总分={score.Score:0.0} "
            + $"(达成={Fmt(score.ClockScore)} 热裕度={Fmt(score.ThermalScore)} 稳定={Fmt(score.StabilityScore)}) "
            + $"峰值={Fmt(score.PeakGHz)}GHz 参照={Fmt(score.ReferenceGHz)}GHz(同封装) "
            + $"站位={Fmt(score.BaselineRatio)} 满载样本={score.HeavySamples} 覆盖率={score.CoveragePercent:0}%");
    }

    private void LogInventoryOnce(int coreCount)
    {
        if (_inventoryLogged)
            return;

        _inventoryLogged = true;
        HealthLog.Write(
            $"[HEALTH] 评分器就绪：{coreCount} 核，窗口={WindowSize} 帧，"
            + $"证据判据=OS使用率≥{HeavyLoadPercent:0}% 或 硬件活跃度≥{HeavyActivityPercent:0}%（取或），"
            + $"最少证据帧={MinHeavySamples}（稳定性另需工作点样本≥{StabilityMinSamples}，"
            + $"工作点=峰值×{StabilityWorkingPointRatio:0.00} 以上）；"
            + $"参照需≥{RequiredEligibleCores(coreCount)} 核达标；"
            + $"帧距下限={MinSampleSpacingSeconds:0.0}s；评级确认={GradeConfirmSeconds:0}s；"
            + $"封装温度={Fmt(_packageTempC)}℃"
            + $"（{(!double.IsNaN(_packageTempC) ? "可读" : "不可读，热裕度分量不参与")}）；"
            + $"WHEA={( _wheaAvailable ? $"可读（纠正 {_wheaCorrected} / 致命 {_wheaFatal}）" : "不可读")}；"
            + $"历史基线={_baselines.Count} 核");
    }

    private static string Fmt(double value) => double.IsNaN(value) ? "-" : value.ToString("0.00");

    /// <summary>单核的窗口统计量。</summary>
    private readonly struct CoreStats
    {
        /// <summary>峰值频率估计（GHz）。</summary>
        public double PeakGHz { get; private init; }

        /// <summary>构成证据的帧数（见 <see cref="IsEvidenceFrame"/>）。</summary>
        public int HeavySamples { get; private init; }

        /// <summary>窗口内带有效频率的帧数 —— 用来区分"频率读不到"与"只是没负载"。</summary>
        public int FrequencyFrames { get; private init; }

        /// <summary>
        /// 鲁棒变异系数：1.4826 × MAD ÷ 中位数，**在工作点帧上算**（v1.19.1）。
        /// <para>
        /// 用 MAD 而非标准差是为了抗单帧尖峰；但 MAD 挡的是「离群值」，
        /// 挡不住「集合本身就是两个工况拼起来的」—— 后者要靠工作点筛选
        /// （见 <see cref="StabilityWorkingPointRatio"/>）。
        /// </para>
        /// </summary>
        public double RobustCv { get; private init; }

        /// <summary>参与 <see cref="RobustCv"/> 的帧数（工作点内的证据帧；可能少于 <see cref="HeavySamples"/>）。</summary>
        public int StabilitySamples { get; private init; }

        /// <summary>本次判定所依据帧的频率口径（多数票）。</summary>
        public CoreFrequencySource Basis { get; private init; }

        /// <summary>窗口内是否存在任何带有效频率的帧。</summary>
        public bool HasFrequency => FrequencyFrames > 0;

        /// <summary>证据帧数是否达到判定门槛。</summary>
        public bool HasEvidence => HasFrequency && HeavySamples >= MinHeavySamples;

        public static CoreStats From(CoreWindow window)
        {
            var heavy = new List<double>(window.Filled);
            var n = window.Filled;
            var frequencyFrames = 0;
            var evidenceFrames = 0;
            var effectiveEvidence = 0;

            for (var i = 0; i < n; i++)
            {
                // Filled 未满时数据从下标 0 开始；写满之后整个数组都是有效数据。
                var index = n < WindowSize ? i : (window.WriteIndex + i) % WindowSize;

                if (double.IsNaN(window.Freq[index]) || window.Freq[index] <= 0)
                    continue;

                frequencyFrames++;

                if (!IsEvidenceFrame(window.Usage[index], window.Activity[index]))
                    continue;

                heavy.Add(window.Freq[index]);
                evidenceFrames++;
                if (window.EffectiveBasis[index])
                    effectiveEvidence++;
            }

            if (heavy.Count == 0)
            {
                return new CoreStats
                {
                    PeakGHz = double.NaN,
                    HeavySamples = 0,
                    FrequencyFrames = frequencyFrames,
                    RobustCv = double.NaN,
                    StabilitySamples = 0,
                    Basis = CoreFrequencySource.Unknown,
                };
            }

            heavy.Sort();

            var peak = UpperQuartileMean(heavy);

            // 抖动统计只在**工作点上**做（见 StabilityWorkingPointRatio）：
            // heavy 里混着升频过程帧时，中位数会被拉到两个档位之间、MAD 被撑到
            // 「两档间距」量级，算出来的变异系数描述的是「工况建立了没有」。
            // heavy 已升序，筛出来的 workingPoint 仍是升序，可直接喂 Percentile。
            var workingPoint = new List<double>(heavy.Count);
            var workingPointFloor = peak * StabilityWorkingPointRatio;
            for (var i = 0; i < heavy.Count; i++)
            {
                if (heavy[i] >= workingPointFloor)
                    workingPoint.Add(heavy[i]);
            }

            var cv = double.NaN;
            if (workingPoint.Count > 0)
            {
                var center = Percentile(workingPoint, 0.5);

                var deviations = new double[workingPoint.Count];
                for (var i = 0; i < workingPoint.Count; i++)
                    deviations[i] = Math.Abs(workingPoint[i] - center);
                Array.Sort(deviations);
                var mad = Percentile(deviations, 0.5);

                cv = center > 0 ? 1.4826 * mad / center : double.NaN;
            }

            return new CoreStats
            {
                PeakGHz = peak,
                HeavySamples = evidenceFrames,
                FrequencyFrames = frequencyFrames,
                RobustCv = cv,
                StabilitySamples = workingPoint.Count,

                // 口径按证据帧的多数票如实带出去：只要有一半以上证据帧用的是有效频率，
                // 这个分数就是建立在有效频率上的；反之界面必须能看出它建立在较差的口径上。
                Basis = effectiveEvidence * 2 >= evidenceFrames
                    ? CoreFrequencySource.SensorEffective
                    : CoreFrequencySource.SensorPState,
            };
        }
    }

    /// <summary>
    /// 该帧是否构成"这颗核被真正调用过"的证据（v1.18.0 起为"或"关系）。
    /// <para>
    /// 两条判据分工不同：<see cref="HeavyLoadPercent"/> 看**调度器**是否把活派给了它，
    /// <see cref="HeavyActivityPercent"/> 看**硬件**是否真的在高时钟上执行。
    /// 前者独立于传感器（读不到有效频率时仍可用），后者不受调度器视角失真影响
    /// （SMT 共享、频率门控、中断风暴都会让 OS 使用率偏离真实执行量）。
    /// 任一成立即收作证据 —— 这是"宁可多收真实的高频帧，也不要因为一条判据失效而全体沉默"。
    /// </para>
    /// <para>
    /// 需要警惕的不是"门槛太松"：有效频率是时间平均量，一颗 5% 负载的核即使瞬时冲到
    /// 4.5 GHz，一秒平均也只有 200 余 MHz，压根够不着 70% 活跃度。
    /// </para>
    /// </summary>
    private static bool IsEvidenceFrame(double usagePercent, double activityPercent)
        => usagePercent >= HeavyLoadPercent
           || (!double.IsNaN(activityPercent) && activityPercent >= HeavyActivityPercent);

    /// <summary>
    /// 峰值频率估计量：升序序列中**最好四分之一帧的均值**（v1.18.0 起取代 90 分位）。
    /// <para>
    /// 为什么不继续用 90 分位：n 个样本的 90 分位在 n 小时**数学上就是最大值**
    /// （n=8 时 rank=6.3 → 0.7·v[6] + 0.3·v[7]，两个都来自最大端）。于是开跑初期
    /// 任何一次孤立尖峰（芯片未热、boost 最激进）都会被当成"这核的能力上限"。
    /// 这正是 v1.17.0 建锚门槛被抬到 60 帧的原因 —— 但判分侧当时没跟上，
    /// 于是"单核达标 → 总分 100"的假象得以出现。
    /// </para>
    /// <para>
    /// 上四分位均值的性质：
    /// </para>
    /// <list type="bullet">
    ///   <item>n=1 → 就是该值；n=2/3/4 → **2 个最好帧的均值**（take 下限刻意取 2，见实现）；
    ///         n=8 → 2 个；n=30 → 8 个；n=85 → 22 个。
    ///         单个孤立尖峰最多占 1/2 权重（n ≤ 4）或 1/take 权重（n 大时），**不会独占结果** ——
    ///         这正是它与 90 分位的分界：后者在 n=8 时 rank=6.3，取到的仍落在最大端。</item>
    ///   <item>随 n 增长平滑收敛到约 87.5 分位，**没有**"分位在 n 小时退化成极值"这个台阶。</item>
    ///   <item>它的偏差在小 n 与大 n 之间连续，因此"有的核只有 3 帧、有的核有 200 帧"这种
    ///         样本量不齐的情况**不会**引入额外的核间系统偏差 —— 而这正是横向比较最怕的。</item>
    /// </list>
    /// <para>
    /// 仍然保留的局限（必须承认）：它依旧是个高分位型估计量，对"持续更长时间的高频"敏感，
    /// 而不是严格的数学定义。要彻底去掉这层近似，需要的不是更好的分位，而是更好的观测量 ——
    /// 那正是 v1.18.0 把输入换成逐核有效频率所做的事。
    /// </para>
    /// </summary>
    /// <param name="ascending">已升序排列的序列。</param>
    private static double UpperQuartileMean(IReadOnlyList<double> ascending)
    {
        var n = ascending.Count;
        if (n == 0)
            return double.NaN;
        if (n == 1)
            return ascending[0];

        // 下限取 2 而不是 1：⌈n/4⌉ 在 n ≤ 4 时等于 1，此时"上四分位均值"**就是最大值**，
        // 抗尖峰能力归零 —— 而 MinHeavySamples 恰好是 3，正落在这一段里。
        // 取 2 保证**单帧尖峰的权重最多 1/2**，即"冷启动瞬时高峰不能独占结果"这条设计意图
        // 在小样本下也成立（上四分位均值的文档注释一直这么写，实现原先却没做到）。
        var take = n <= 1 ? 1 : Math.Max(2, (int)Math.Ceiling(n / 4.0));
        double sum = 0;
        for (var i = n - take; i < n; i++)
            sum += ascending[i];

        return sum / take;
    }

    /// <summary>
    /// 同封装参照是否成立（v1.18.0）。达不到 <see cref="RequiredEligibleCores"/> 时全体不给分。
    /// </summary>
    private static bool IsPeerReferenceValid(int eligibleCores, int trackedCores)
        => trackedCores > 0 && eligibleCores >= RequiredEligibleCores(trackedCores);

    /// <summary>
    /// 参照成立所需的达标核数：<c>max(MinScoredCores, ⌈已跟踪核数 × 0.5⌉)</c>。
    /// <para>
    /// 暴露成公开静态方法而不是内联，是为了让界面与日志能用**同一条规则**回答
    /// "还差几颗核"，而不是各自硬编码一个数字（那种两处口径打架的坑本项目已经踩过）。
    /// </para>
    /// </summary>
    public static int RequiredEligibleCores(int trackedCores)
        => Math.Max(MinScoredCores, (int)Math.Ceiling(trackedCores * MinScoredCoreRatio));

    /// <summary>线性插值分位（与 Excel PERCENTILE.INC 同口径）。<paramref name="sorted"/> 必须已升序。</summary>
    private static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        var n = sorted.Count;
        if (n == 0)
            return double.NaN;
        if (n == 1)
            return sorted[0];

        var rank = Math.Clamp(p, 0, 1) * (n - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper)
            return sorted[lower];

        return sorted[lower] + ((sorted[upper] - sorted[lower]) * (rank - lower));
    }
}
