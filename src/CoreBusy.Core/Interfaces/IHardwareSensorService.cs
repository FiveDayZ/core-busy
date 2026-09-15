namespace CoreBusy.Core.Interfaces;

using CoreBusy.Core.Models;

/// <summary>CPU 硬件传感器（温度/功耗/频率）服务。</summary>
public interface IHardwareSensorService : IDisposable
{
    /// <summary>初始化并打开硬件监控（LibreHardwareMonitor 首次枚举较慢，应异步调用）。</summary>
    void Start();

    /// <summary>读取一次快照。内部执行传感器刷新，耗时约数毫秒，建议低频调用。</summary>
    HardwareSensorSnapshot ReadSnapshot();

    /// <summary>
    /// Intel 平台基频取证（v1.10.5）：读 MSR_PLATFORM_INFO(0xCE) 的最大非睿频比（× 100 MHz 总线）
    /// 与 MSR_PKG_POWER_LIMIT(0x610) 的 PL1/PL2。
    ///
    /// 注意语义：0xCE 与 CPUID.16H 同源，都是芯片按**当前平台功耗配置**自报的非睿频比，
    /// 不等于 SKU 的"标称基频"（Intel 只在默认 PL1 档位公布 HFM）。
    /// 需要 PawnIO 内核驱动（LHM 0.9.x 起）；非 Intel / 驱动缺失 / 读取失败时
    /// 返回 <see cref="IntelBaseClockProbe.Available"/> = false 并带失败原因，调用方保持现值。
    /// </summary>
    IntelBaseClockProbe ProbeIntelBaseClock();
}
