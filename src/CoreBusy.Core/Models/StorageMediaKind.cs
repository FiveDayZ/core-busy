namespace CoreBusy.Core.Models;

/// <summary>
/// 存储介质类型（v1.20.1）。硬盘同样**没有功耗传感器**，只能用介质类型 + 实测活动度估算，
/// 而三类的空闲/满载功耗差着一个数量级（NVMe SSD 空闲 1 W 级、机械盘空闲 4 W 级）。
/// <para>
/// 来源优先级：WMI <c>MSFT_PhysicalDisk.BusType/MediaType</c>（最准，与任务管理器同源）
/// → <c>Win32_DiskDrive.Model</c> 型号串里的关键字（"NVMe"/"SSD"/"HDD"/"HDD 转速"）
/// → <see cref="Unknown"/>（模型按 SATA SSD 与 HDD 之间的保守值处理）。
/// </para>
/// </summary>
public enum StorageMediaKind
{
    /// <summary>未能确定介质：模型取 SATA SSD 与 HDD 之间的保守值。</summary>
    Unknown = 0,

    /// <summary>NVMe（PCIe）固态盘。空闲功耗比 SATA 盘高（无 ASPM 时约 1 W 级），满载也更高。</summary>
    Nvme = 1,

    /// <summary>SATA 固态盘。</summary>
    SataSsd = 2,

    /// <summary>机械硬盘（含 2.5"/3.5"，SSHD 按机械盘计 —— 其主轴功耗才是主导项）。</summary>
    Hdd = 3,
}
