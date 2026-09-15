namespace CoreBusy.Core.Interfaces;

using CoreBusy.Core.Models;

/// <summary>
/// 整机硬件状态采集（内存 / 显卡 / 系统盘），供状态栏展示。
/// <para>
/// 与 <see cref="ICpuMonitorService"/> 分开：本接口只提供不需要特殊权限、
/// 不需要内核驱动的静态/半静态设备信息，读取成本远低于传感器层，
/// 因此可在每个刷新帧调用（内部对硬盘等慢变量自带缓存节流）。
/// </para>
/// </summary>
public interface ISystemHardwareService
{
    /// <summary>读取一次整机硬件状态快照。不抛异常：不可用的项以 Has* = false 表达。</summary>
    SystemHardwareInfo Read();
}
