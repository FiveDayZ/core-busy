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

    /// <summary>硬盘最高温度（℃，多块盘取最热）。SMART 不可用时为 null。</summary>
    public double? DriveTemperatureC { get; init; }

    /// <summary>
    /// 逐块盘的盘温（v1.16.2）。status bar 只显示最热的那一个数字，
    /// 但"哪块盘"必须能查，否则"114 ℃"这种坏读数无从定位。
    /// </summary>
    public IReadOnlyList<DriveTemperatureReading> DriveTemperatures { get; init; } = [];

    public static HardwareSensorSnapshot Empty { get; } = new();
}
