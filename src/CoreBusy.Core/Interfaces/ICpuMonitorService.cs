namespace CoreBusy.Core.Interfaces;

using CoreBusy.Core.Models;

/// <summary>
/// CPU 监控数据接口：UI 与真实硬件采集完全解耦。
/// 第一阶段由 <c>MockCpuMonitorService</c> 提供模拟数据，后续替换为真实采集实现即可。
/// </summary>
public interface ICpuMonitorService
{
    /// <summary>CPU 静态信息（名称、厂商、核心/线程数）。</summary>
    CpuInfo GetCpuInfo();

    /// <summary>CPU 整体快照（总使用率/温度/功耗/平均频率/风扇/峰值温度/主要进程）。</summary>
    CpuSnapshot GetSnapshot();

    /// <summary>每核心快照（顺序与 <see cref="GetCoreGroups"/> 的分组顺序一致）。</summary>
    IReadOnlyList<CpuCoreSnapshot> GetCoreSnapshots();

    /// <summary>
    /// 主界面核心分区（规范 §52，禁止硬编码固定分组）：
    /// Intel 混合架构 → P-Core + E-Core；Intel 非混合 / AMD → 单段 CPU Core；
    /// 多 CCD Ryzen → CCD 0..N；设计稿口径下恒为 P/E 两段。
    /// </summary>
    IReadOnlyList<CpuCoreGroup> GetCoreGroups();

    /// <summary>UI 刷新间隔（与底层采样节奏对齐），可在运行时调整（设置/性能模式）。</summary>
    TimeSpan SampleInterval { get; set; }

    /// <summary>
    /// 是否启用硬件传感器（温度/功耗/频率/风扇）轮询。
    /// 关闭后相关项降级为缺省值（UI 显示 "-"），用于性能模式 / 设置中的"低开销"选项。
    /// </summary>
    bool SensorsEnabled { get; set; }
}
