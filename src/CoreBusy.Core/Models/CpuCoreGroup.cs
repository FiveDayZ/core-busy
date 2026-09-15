namespace CoreBusy.Core.Models;

/// <summary>
/// 核心分区类别（规范 §52）。UI 分组由数据推导，禁止硬编码固定分组。
/// </summary>
public enum CoreGroupKind
{
    /// <summary>同构核心（AMD / 非混合 Intel）——标题为 CPU Core 或 CCD n。</summary>
    Standard = 0,

    /// <summary>性能核（P-Core，Intel 混合架构）。</summary>
    Performance = 1,

    /// <summary>能效核（E-Core，Intel 混合架构）。</summary>
    Efficiency = 2,
}

/// <summary>
/// 主界面的一段核心分区（规范 §52）：Intel 混合架构为 P-Core / E-Core；
/// AMD 与非混合 Intel 为单一 CPU Core；多 CCD Ryzen 为 CCD 0..N。
/// </summary>
/// <param name="Kind">分区类别。</param>
/// <param name="Title">分区标题（"P-Core" / "E-Core" / "CPU Core" / "CCD 0"）。</param>
/// <param name="CoreIds">该分区包含的核心显示名，顺序与 <c>GetCoreSnapshots()</c> 一致。</param>
/// <param name="CoreCount">该分区物理核心数。</param>
/// <param name="ThreadCount">该分区线程数。</param>
/// <param name="CcdIndex">所属 CCD 序号（非多 CCD 平台为 null）。</param>
public sealed record CpuCoreGroup(
    CoreGroupKind Kind,
    string Title,
    IReadOnlyList<string> CoreIds,
    int CoreCount,
    int ThreadCount,
    int? CcdIndex = null);
