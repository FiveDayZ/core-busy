namespace CoreBusy.Core.Models;

/// <summary>
/// 内存代际（v1.20.1）。整机功耗估算里**唯一**需要"型号"信息的内存输入。
/// <para>
/// 为什么需要它：内存条**没有任何功耗传感器**（消费级平台全面如此，LHM 的内存节点只有
/// 容量/时序/温度阈值，实测见 .workbuddy/lhm_dump_v1200.txt），只能用型号模型估算，
/// 而不同代际的电压与每 GB 功耗差异显著（DDR3 1.5 V → DDR4 1.2 V → DDR5 1.1 V 但
/// 片上电源管理更复杂、每 GB 功耗反而上升）。
/// </para>
/// <para>
/// 来源：WMI <c>Win32_PhysicalMemory.SMBIOSMemoryType</c>（SMBIOS 7.18 表），
/// 该字段取不到时退 <c>MemoryType</c>（旧字段，Win10 以后常为 0）；两者都没有则
/// <see cref="Unknown"/>，模型按 <b>DDR4 系数</b>处理（最普遍的取值，
/// 且未知不等于可以编一个值 —— 只影响系数不改变"这是估算"的身份）。
/// </para>
/// </summary>
public enum MemoryGeneration
{
    /// <summary>未能确定代际：功耗模型按 DDR4 系数处理。</summary>
    Unknown = 0,

    /// <summary>DDR3（含 DDR3L）。</summary>
    Ddr3 = 1,

    /// <summary>DDR4（含 DDR4 ECC UDIMM/SO-DIMM）。</summary>
    Ddr4 = 2,

    /// <summary>DDR5。</summary>
    Ddr5 = 3,

    /// <summary>LPDDR4 / LPDDR4X（板载，通常无插槽）。</summary>
    LpDdr4 = 4,

    /// <summary>LPDDR5 / LPDDR5X（板载，通常无插槽）。</summary>
    LpDdr5 = 5,
}
