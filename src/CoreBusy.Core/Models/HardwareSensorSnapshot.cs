namespace CoreBusy.Core.Models;

/// <summary>硬件传感器一次采样快照（温度/功耗/频率）。不可用项为 null，由 UI 降级显示。</summary>
public sealed class HardwareSensorSnapshot
{
    /// <summary>CPU Package 温度（℃）。</summary>
    public double? PackageTemperatureC { get; init; }

    /// <summary>核心最高温度（℃）。部分平台（如 AMD）无每核温度时为 null。</summary>
    public double? MaxCoreTemperatureC { get; init; }

    /// <summary>CPU Package 功耗（W）。</summary>
    public double? PackagePowerW { get; init; }

    /// <summary>CPU 风扇转速（RPM），来自主板 SuperIO；不可用时为 null。</summary>
    public double? FanRpm { get; init; }

    /// <summary>
    /// 正在转动的风扇数量（v1.20.1）。只数"转速落在合理域内"的风扇传感器：
    /// 空风扇接口常报 0（本机 5 个接口只 1 个在转），少数主板会报一个恒定的非零噪声值，
    /// 故既排除 0 也排除越界值。整机功耗估算用它的**个数**，不用转速 ——
    /// 风扇的 P–Q 曲线随型号差异太大，用转速反推功率比取常数更不准。
    /// </summary>
    public int ActiveFanCount { get; init; }

    /// <summary>按物理核心序号（0 基）索引的实际频率（MHz）；缺失核心表示该核心频率不可用。</summary>
    public IReadOnlyDictionary<int, double> CoreClockMhz { get; init; } = new Dictionary<int, double>();

    /// <summary>
    /// 按物理核心序号（0 基）索引的**有效频率**（MHz，v1.18.0）：硬件驻留加权的时间平均值。
    /// <para>
    /// 与 <see cref="CoreClockMhz"/> 的区别是口径而非来源：后者是 P-state 倍频 × 总线（离散档位、
    /// 与负载无关），前者是"整秒里它实际跑多快"。本机实测（R9 5900HX）：空闲核 39–855 MHz、
    /// 满载核 2102+ MHz，而 P-state 一列所有非停放核都是 4567 MHz。
    /// </para>
    /// <para>
    /// 缺失核心表示该平台不提供该传感器（**不要用 <see cref="CoreClockMhz"/> 去填**：
    /// 那会把"读不到"变成"读到了档位值"，而两者在判定上的含义完全不同）。
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<int, double> CoreEffectiveClockMhz { get; init; } = new Dictionary<int, double>();

    /// <summary>
    /// 按物理核心序号（0 基）索引的**逐核电压**（V，v1.18.0）。缺失表示该平台不提供。
    /// 读不到一律缺席，绝不用 0 冒充（沿用全线约定）。
    /// </summary>
    public IReadOnlyDictionary<int, double> CoreVoltageV { get; init; } = new Dictionary<int, double>();

    /// <summary>主显卡核心温度（℃）。GPU 读取不可用（驱动/权限/未启用）时为 null。</summary>
    public double? GpuTemperatureC { get; init; }

    /// <summary>主显卡核心占用率（0-100）。不可用时为 null。</summary>
    public double? GpuUtilizationPercent { get; init; }

    /// <summary>
    /// **全部独显**实测功率之和（W，v1.20.1）。null 表示"没有任何独显功率传感器"
    /// （不是 0 W）—— 核显与驱动缺失都属于这一情形，由功耗模型按型号估算兜底。
    /// <para>
    /// 只有独显才可能有功率传感器：NVIDIA 经 NVML、AMD 独显经 ADL 暴露 <c>SensorType.Power</c>。
    /// 多卡机器（含双独显）取**求和**，因为功耗模型是逐部件累加的。
    /// </para>
    /// </summary>
    public double? DiscreteGpuPowerW { get; init; }

    /// <summary>
    /// 被判为**独显**的 GPU 节点名（v1.20.1）。判据是型号（<c>GpuRatedPower.LooksDiscrete</c>）：
    /// 核显的功耗已在 CPU 封装读数之内，把它算进来就是重复计；独显必须在封装之外单独计。
    /// 型号判别不出的卡不进本列表（宁可不计也不冒充核显）。
    /// </summary>
    public IReadOnlyList<string> DiscreteGpuNames { get; init; } = [];

    /// <summary>硬盘最高温度（℃，多块盘取最热）。SMART 不可用时为 null。</summary>
    public double? DriveTemperatureC { get; init; }

    /// <summary>
    /// 逐块盘的盘温（v1.16.2）。status bar 只显示最热的那一个数字，
    /// 但"哪块盘"必须能查，否则"114 ℃"这种坏读数无从定位。
    /// </summary>
    public IReadOnlyList<DriveTemperatureReading> DriveTemperatures { get; init; } = [];

    /// <summary>
    /// 最忙那块盘的吞吐（MB/s，v1.20.1）。硬盘没有功率传感器，但吞吐是**实测**的，
    /// 整机功耗模型用它驱动存储项的功率摆幅（空闲 → 满载之间插值）。
    /// 优先取 LHM 的 Throughput 传感器；本机 NVMe 的 Throughput 为 null，
    /// 故实现上退到"累计读写量差商"（见传感器实现）。两个口径都拿不到时为 null。
    /// </summary>
    public double? StorageThroughputMbps { get; init; }

    /// <summary>
    /// 最忙那块盘的忙率（%，v1.20.1）。吞吐不可读时的备选口径，
    /// 取 <c>Read Activity</c> 与 <c>Write Activity</c> 的大者 ——
    /// **刻意不用 Total Activity**：本机实测该传感器在读写都≈0 时仍报 99.999985，
    /// 放它进来会把一块空闲盘常年算成满载。
    /// </summary>
    public double? StorageBusyPercent { get; init; }

    public static HardwareSensorSnapshot Empty { get; } = new();
}
