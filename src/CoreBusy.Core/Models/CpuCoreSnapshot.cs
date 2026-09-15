namespace CoreBusy.Core.Models;

/// <summary>单个核心的运行状态快照。</summary>
public sealed record CpuCoreSnapshot
{
    /// <summary>核心显示名，例如 "P0" / "E2" / "C5"。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>核心类别（Standard / Performance / Efficiency，规范 §51）。</summary>
    public CoreClass Class { get; init; } = CoreClass.Standard;

    /// <summary>是否为性能核（P-Core）。AMD 与非混合 Intel 恒为 false。</summary>
    public bool IsPerformance => Class == CoreClass.Performance;

    /// <summary>所属 CCD 序号（非多 CCD 平台为 null，规范 §50）。</summary>
    public int? CcdIndex { get; init; }

    /// <summary>使用率（0-100）。</summary>
    public double UsagePercent { get; init; }

    /// <summary>
    /// 当前频率（GHz）—— 界面 Tile 显示的就是它，取自传感器逐核时钟。
    /// <para>
    /// 注意它的口径是 **P-state 倍频 × 总线频率**（离散档位），与负载无关：
    /// 本机所有非停放核都会读到同一个最高档（4567 MHz），这正是 v1.17.x "8 核同值"
    /// 那一幕的成因。**判分不能用它**，见 <see cref="EffectiveFrequencyGHz"/>。
    /// </para>
    /// </summary>
    public double FrequencyGHz { get; init; }

    /// <summary>
    /// 该核的**有效频率**（GHz，v1.18.0）：硬件驻留加权的"这段时间实际跑多快"。
    /// <para>
    /// 与 <see cref="FrequencyGHz"/> 的关键差别是**它是时间平均量**：一颗 5% 负载的核
    /// 即使瞬时冲到 4.5 GHz，一秒平均下来也只有 200 余 MHz；只有整秒都在高时钟上执行
    /// 才会读到接近上限的值。因此它同时回答了两件事 —— "实际频率"与"是否真的在执行"，
    /// 不需要再借操作系统使用率去猜。
    /// </para>
    /// <para>
    /// 读不到时为 NaN（沿用全线"读不到就不猜"的约定）。来源见
    /// <see cref="CoreBusy.Core.Models.CoreFrequencySource"/>。
    /// </para>
    /// </summary>
    public double EffectiveFrequencyGHz { get; init; } = double.NaN;

    /// <summary>
    /// 硬件活跃度（0-100，v1.18.0）：<c>有效频率 ÷ 本机最高加速档频率</c>。
    /// <para>
    /// 它是"这颗核整秒都在高时钟上跑"的硬件级判据，替代 v1.17.0 用的纯操作系统使用率：
    /// OS 使用率是调度器视角的非空闲时间，在 SMT 共享、频率门控、中断风暴下都会失真
    /// （一颗被压在 400 MHz 的核照样能报 100% 使用率）。
    /// </para>
    /// <para>
    /// **口径必须说清**：turbostat 的 <c>Busy% = ΔMPERF/ΔTSC</c> 在这里取不到 ——
    /// 实测本机 PawnIO 的 MSR 模块整体不工作（连 TSC/0x10 都返回 0，见
    /// .workbuddy/health-diag/aperfprobe-result.txt），所以这里用的是
    /// <c>Avg_MHz / Max_MHz</c>，严格说是"频率加权的活跃度"而不是 C0 驻留百分比。
    /// 两者对"空转"的判别一致（空转核两者都接近 0），对"满载但被降频"的判别不同
    /// （前者给 60 上下、后者给 100）—— 界面与文档一律按前者表述。
    /// </para>
    /// </summary>
    public double HardwareActivityPercent { get; init; } = double.NaN;

    /// <summary><see cref="FrequencyGHz"/> 的来源（用于界面/日志标注，不参与判定）。</summary>
    public CoreFrequencySource FrequencySource { get; init; } = CoreFrequencySource.Unknown;

    /// <summary>
    /// 该核电压（V，v1.18.0，逐核读数）。读不到时为 NaN。
    /// <para>
    /// **不参与评分，只作展示与存档。** 理由是"同频率所需电压上升"虽然是硅老化最灵敏的
    /// 信号，但要把它变成分数需要一个"标定过的同频点"，而本工具不写 MSR、不控频，
    /// 拿不到这样的点；硬折成分数就是拿工况当结论。
    /// </para>
    /// </summary>
    public double CoreVoltageV { get; init; } = double.NaN;

    /// <summary>负载状态（由 CoreStatusClassifier 划分）。</summary>
    public CoreStatus Status { get; init; }

    /// <summary>
    /// 该物理核包含的操作系统逻辑处理器索引（0 基，v1.18.0）。
    /// <para>
    /// 逐核绑核的场合需要它：确定性自检要把计算线程钉在**目标核的全部逻辑处理器**上
    /// （SMT 双线程核两个都绑），否则测到的只是半颗核的负载行为。
    /// 界面上的线程序号（<c>CpuThreadSnapshot.Index</c>）是核内相对序号，
    /// 不能拿来绑核 —— 那是两个不同的编号体系，混用会让"自检 C3"变成"在 LP2 上跑了跑"。
    /// </para>
    /// </summary>
    public IReadOnlyList<int> LogicalProcessors { get; init; } = [];

    /// <summary>
    /// 该物理核内的逻辑线程明细（SMT），顺序即核内线程序号。
    /// 多线程核为 2 条、单线程核为 1 条；界面据此在 Tile 内画线程条、
    /// 并把热力图从"核心行"展开成"线程行"，保证标称线程数全部可见。
    /// </summary>
    public IReadOnlyList<CpuThreadSnapshot> Threads { get; init; } = [];
}
