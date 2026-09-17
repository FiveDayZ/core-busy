namespace CoreBusy.Core.Energy;

using CoreBusy.Core.Models;

/// <summary>
/// 整机功耗估算的输入（v1.20.1）—— **逐部件**，每个部件都自带"实测/型号"两条腿。
/// <para>
/// 设计口径（用户 2026-09-17 明确要求）：<b>分别去读显卡、硬盘、内存的功耗，
/// 读不到再按型号用模型估算</b>。因此本记录里每一个可能实测的量都单独成字段
/// （<see cref="GpuMeasuredWatt"/>），而不是像 v1.20.0 那样只用封装功耗做一个线性外推 ——
/// 后者在带独显的机器上会把独显那一两百瓦整体漏掉，因为独显根本不随 CPU 封装功耗变化。
/// </para>
/// <para>
/// 各部件的数据现实（本机实测取证，见 .workbuddy/lhm_dump_v1200.txt）：
/// </para>
/// <list type="bullet">
///   <item><b>CPU</b>：封装功率可实测（LHM <c>Package</c>），是**唯一**可靠的功率传感器来源。</item>
///   <item><b>显卡</b>：核显无功率传感器（本机 AMD APU 实测）；独显经 NVML/ADL 一般可读。</item>
///   <item><b>内存</b>：任何消费级平台都无功率传感器，只有容量/代数可读 → 必然走模型。</item>
///   <item><b>硬盘</b>：无功率传感器，但可读介质类型与吞吐/忙率 → 活动度是模型里的变量。</item>
/// </list>
/// </summary>
public sealed record SystemPowerInputs
{
    /// <summary>CPU 封装功耗（W，实测）。为 null / 非正 / 超合理域时整条估算不成立（见 <see cref="SystemPowerEstimator.Estimate"/>）。</summary>
    public double? CpuPackageWatt { get; init; }

    /// <summary>
    /// **全部独显**的功率实测值之和（W）。null 表示没有任何独显功率传感器（核显、或驱动缺失），
    /// 此时由型号表估算兜底 —— 注意 0 是合法读数（独显在 Optimus/运行时 D3 下确实会下电）。
    /// </summary>
    public double? GpuMeasuredWatt { get; init; }

    /// <summary>
    /// 被判为**独显**的显卡名（可能有多块）。核显的功耗已含在 CPU 封装读数内，
    /// 只有独显需要在封装之外单独计；型号判别不出的卡不在本列表（宁可不计，也不冒充核显）。
    /// </summary>
    public IReadOnlyList<string> DiscreteGpuNames { get; init; } = [];

    /// <summary>是否存在显卡（含核显）。false 时显卡项恒为 0，且提示里不出现该行。</summary>
    public bool HasGpu { get; init; }

    /// <summary>主显卡型号名（用于查型号表与显示；含核显名也无妨，核显不会命中表）。</summary>
    public string GpuName { get; init; } = string.Empty;

    /// <summary>显卡核心占用率（0–100）；不可读时为 null。</summary>
    public double? GpuLoadPercent { get; init; }

    /// <summary>物理内存总量（GB）。</summary>
    public double MemoryTotalGb { get; init; }

    /// <summary>内存代际（型号信息，决定每 GB 功耗系数）。</summary>
    public MemoryGeneration MemoryGeneration { get; init; }

    /// <summary>内存占用率（0–100）；不可读时为 null（按空闲处理）。</summary>
    public double? MemoryLoadPercent { get; init; }

    /// <summary>物理固定盘数量。</summary>
    public int DriveCount { get; init; }

    /// <summary>
    /// 各盘的介质类型（型号信息，决定空闲/满载功率档）。可能短于 <see cref="DriveCount"/>
    /// ——读不出介质的盘由模型按"未知"档补齐，绝不跳过（跳过等于假设它不耗电）。
    /// </summary>
    public IReadOnlyList<StorageMediaKind> StorageKinds { get; init; } = [];

    /// <summary>最忙那块盘的吞吐（MB/s，实测）。</summary>
    public double? StorageThroughputMbps { get; init; }

    /// <summary>最忙那块盘的忙率（0–100，实测；吞吐不可读时的备选口径）。</summary>
    public double? StorageBusyPercent { get; init; }

    /// <summary>正在转动的风扇数量（实测计数；转速本身不可换算成功率，只取"几个在工作"）。</summary>
    public int ActiveFanCount { get; init; }
}

/// <summary>整机功耗里显卡项的来源。</summary>
public enum GpuPowerSource
{
    /// <summary>无显卡，或型号无法判别且无实测值 —— 未计入。</summary>
    Unavailable = 0,

    /// <summary>实测（独显的功率传感器）。</summary>
    Measured = 1,

    /// <summary>核显：与 CPU 同封装，功耗已含在封装读数里，不重复计。</summary>
    IntegratedInPackage = 2,

    /// <summary>按型号额定功率 × 负载估算。</summary>
    RatedByModel = 3,
}

/// <summary>
/// 一次整机功耗估算的**逐部件分解**（v1.20.1）。
/// <para>
/// 分解本身是可交付信息，不只是中间量：界面提示要逐项列出"哪一项是实测、哪一项是模型"，
/// 否则用户无法判断某个反直觉的数字该不该信、该去校准哪个系数。
/// </para>
/// </summary>
public readonly record struct SystemPowerBreakdown(
    double TotalWatt,
    double CpuWatt,
    double GpuWatt,
    double MemoryWatt,
    double StorageWatt,
    double BoardWatt,
    double FanWatt,
    GpuPowerSource GpuSource,
    double? GpuRatedWatt,
    double? GpuLoadPercent)
{
    /// <summary>整机功耗是否可用（不可用时上层必须显示 "-"，绝不拿 0 或分项和冒充）。</summary>
    public bool IsUsable => double.IsFinite(TotalWatt) && TotalWatt > 0;

    /// <summary>估算不成立时的分解（各项为 0/NaN，供上层走降级路径）。</summary>
    public static SystemPowerBreakdown Unavailable { get; } =
        new(double.NaN, double.NaN, 0, 0, 0, 0, 0, GpuPowerSource.Unavailable, null, null);
}
