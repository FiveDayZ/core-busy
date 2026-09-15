namespace CoreBusy.Core.Models;

/// <summary>
/// 核心类别（规范 §51）：Intel 混合架构区分 Performance / Efficiency；
/// Intel 非混合与 AMD 一律为 <see cref="Standard"/>（禁止把 AMD 显示成 P-Core / E-Core）。
/// </summary>
public enum CoreClass
{
    /// <summary>同构核心（AMD 全系、Intel 非混合架构）。</summary>
    Standard = 0,

    /// <summary>性能核（P-Core）。</summary>
    Performance = 1,

    /// <summary>能效核（E-Core）。</summary>
    Efficiency = 2,
}

/// <summary>单个逻辑处理器的拓扑信息。</summary>
/// <param name="OsIndex">操作系统逻辑处理器编号（与 PerformanceCounter 实例一致）。</param>
/// <param name="PhysicalCoreIndex">物理核心序号（0 基，枚举顺序）。</param>
/// <param name="CoreClass">核心类别。</param>
/// <param name="DisplayName">核心显示名，如 "P0"、"E3"、"C5"。</param>
/// <param name="Smt">该核心是否启用同步多线程（LTP_PC_SMT），用于退化拓扑下估算物理核数。</param>
public sealed record LogicalProcessorTopology(
    int OsIndex,
    int PhysicalCoreIndex,
    CoreClass CoreClass,
    string DisplayName,
    bool Smt = false);

/// <summary>CPU 拓扑（逻辑处理器 → 物理 → 类别）。</summary>
public sealed record CpuTopology(IReadOnlyList<LogicalProcessorTopology> LogicalProcessors)
{
    /// <summary>是否存在能效核（决定 E-Core 分区是否显示）。</summary>
    public bool HasEfficiencyCores =>
        LogicalProcessors.Any(p => p.CoreClass == CoreClass.Efficiency);

    /// <summary>是否存在性能核（Intel 混合架构判据之一）。</summary>
    public bool HasPerformanceCores =>
        LogicalProcessors.Any(p => p.CoreClass == CoreClass.Performance);

    /// <summary>是否为混合架构（同一 CPU 上同时存在 P 核与 E 核）。</summary>
    public bool IsHybrid => HasEfficiencyCores && HasPerformanceCores;

    /// <summary>是否启用同步多线程（退化拓扑下用于按 SMT 口径还原物理核心数）。</summary>
    public bool HasSmt => LogicalProcessors.Any(p => p.Smt);
}
