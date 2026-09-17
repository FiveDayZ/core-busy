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

    // ── 整机功耗估算所需的"型号"信息（v1.20.1）──────────────────────────
    //    这些字段不参与状态栏显示口径，只喂给 SystemPowerEstimator。
    //    放在同一个快照里而不是另开接口：它们与内存/显卡/磁盘三项同源、同节流、
    //    同一次 WMI 查询就能拿到，另开一条采集链只会多一份不一致的机会。

    /// <summary>内存模组数量（0 = 未知）。仅用于提示文案（"2×16G DDR4"）。</summary>
    public int MemoryModuleCount { get; init; }

    /// <summary>单根内存容量（GB，0 = 未知）。</summary>
    public double MemoryModuleCapacityGb { get; init; }

    /// <summary>内存代际（决定功耗模型的每 GB 系数；未知按 DDR4 处理）。</summary>
    public MemoryGeneration MemoryGeneration { get; init; }

    /// <summary>
    /// 各物理固定盘的介质类型（整机功耗模型据此逐盘累加空闲功率、取摆幅最大的那种介质；
    /// 混合盘机（SSD + 机械）与纯 SSD 机在这里就分开了）。列表可能短于 <see cref="DriveCount"/>：
    /// 介质读不出来的盘不写入，模型按"未知"档计。
    /// </summary>
    public IReadOnlyList<StorageMediaKind> StorageKinds { get; init; } = [];

    /// <summary>
    /// 主显卡是否为独显。整机功耗模型据此决定显卡项"单独计"还是"已含在 CPU 封装内"——
    /// 判错的后果是凭空多算一二百瓦或少算同样多，故由采集层用型号表统一判别后带出，
    /// 而不是各处各写一套关键字。
    /// </summary>
    public bool GpuIsDiscrete { get; init; }

    /// <summary>内存使用率（0-100）。总量无效时返回 0。</summary>
    public double MemoryUsagePercent =>
        HasMemory && MemoryTotalGb > 0 ? Math.Clamp(MemoryUsedGb / MemoryTotalGb * 100.0, 0, 100) : 0;
}
