namespace CoreBusy.Core.Models;

/// <summary>
/// Intel 平台基频取证结果（v1.10.5）：MSR_PLATFORM_INFO(0xCE) 的最大非睿频比，以及
/// MSR_PKG_POWER_LIMIT(0x610) 的 PL1/PL2 功耗上限。
///
/// 为什么要把功耗上限一起带出来：0xCE 与 CPUID.16H 报的都是**芯片按当前平台功耗配置自报**的
/// 非睿频比，不是 SKU 的"标称基频"。Intel 只在默认 PL1 档位公布 HFM（i3-N305 数据手册
/// 15W 档 = 1.8 GHz），OEM 把 PL1 调低后芯片自报值会同步下降（实测某 N305 整机 PL1=10W
/// 时 0xCE 比 = 10 → 1.0 GHz）。把 PL1/PL2 写进日志，这类"看起来读错"的现象就能自证。
/// </summary>
public sealed record IntelBaseClockProbe
{
    /// <summary>是否成功取到基频（false 时其余字段无意义，<see cref="Note"/> 为失败原因）。</summary>
    public bool Available { get; init; }

    /// <summary>芯片自报非睿频（基）频，MHz；= MSR 0xCE[15:8] × 100 MHz 总线。</summary>
    public double BaseFrequencyMhz { get; init; } = double.NaN;

    /// <summary>MSR 0x610 的 PL1（长期功耗上限），瓦；不可读为 NaN。</summary>
    public double Pl1W { get; init; } = double.NaN;

    /// <summary>MSR 0x610 的 PL2（短时功耗上限），瓦；不可读为 NaN。</summary>
    public double Pl2W { get; init; } = double.NaN;

    /// <summary>取证说明：成功时为来源串，失败时为可审计的原因（不要吞异常）。</summary>
    public string Note { get; init; } = string.Empty;
}
