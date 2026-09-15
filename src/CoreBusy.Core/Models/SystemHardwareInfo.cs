namespace CoreBusy.Core.Models;

/// <summary>
/// 当前设备的整机硬件状态快照（物理内存 / 显卡 / 系统盘）。
/// <para>
/// 三项各自独立可用：任一来源取不到（虚拟机无独显、盘未就绪、接口不可用）时，
/// 对应项以 <c>Has*</c> = false 表达「无数据」，由展示层整项隐藏，
/// 而不是拿 0 冒充读数 —— 与传感器 NaN 一律降级为 "-" 的口径一致。
/// </para>
/// <para>
/// 单位统一为 GiB（1024³ 字节）。数值口径（v1.16.0）：内存取「已用/总量」（任务管理器口径）；
/// 磁盘取**物理磁盘**总容量（Win32_DiskDrive 固定硬盘求和，不再按盘符显示剩余空间），
/// 已用率按固定逻辑卷聚合（资源管理器口径），卷口径可用空间与磁盘块数一并带出供悬浮详情。
/// </para>
/// </summary>
public sealed record SystemHardwareInfo(
    bool HasMemory,
    double MemoryUsedGb,
    double MemoryTotalGb,
    bool HasGpu,
    string GpuName,
    double? GpuMemoryGb,
    bool HasDrive,
    int DriveCount,
    double DriveTotalGb,
    double DriveVolumeFreeGb,
    double DriveVolumeUsedPercent)
{
    /// <summary>无任何可用数据的空快照（状态栏据此隐藏全部新增项）。</summary>
    public static SystemHardwareInfo Empty { get; } =
        new(false, 0, 0, false, string.Empty, null, false, 0, 0, 0, 0);

    /// <summary>内存使用率（0-100）。总量无效时返回 0。</summary>
    public double MemoryUsagePercent =>
        HasMemory && MemoryTotalGb > 0 ? Math.Clamp(MemoryUsedGb / MemoryTotalGb * 100.0, 0, 100) : 0;
}
