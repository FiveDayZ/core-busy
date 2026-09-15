namespace CoreBusy.Sensor;

/// <summary>
/// 内核驱动（PawnIO）可用性描述（v1.16.1）。
/// <para>
/// 为什么需要它：CPU 温度/功耗、风扇转速、硬盘温度全部经由 LibreHardwareMonitor 的内核驱动读取，
/// 而 PawnIO 设备只对提升后的进程开放。未以管理员身份运行时这些字段必然读不到，界面若只留一个
/// "-"/"0 W"，用户无法判断是"机器没有该传感器"还是"没提权"。把原因显式带出来，
/// 界面才能给出可执行的下一步（提权重启 / 装驱动）。
/// </para>
/// </summary>
/// <param name="Badge">概览卡右上角的短标（空 = 内核访问正常，不显示任何提示）。</param>
/// <param name="Detail">完整解释：既写进 debug.log，也作为提示的 Tooltip。</param>
public sealed record SensorAccessInfo(string Badge, string Detail)
{
    /// <summary>内核访问正常（已提权且驱动就绪）。</summary>
    public static readonly SensorAccessInfo Ok = new(string.Empty, "内核驱动可用（已提权且 PawnIO 就绪）");

    /// <summary>是否需要向用户提示内核访问问题。</summary>
    public bool HasProblem => Badge.Length > 0;
}
