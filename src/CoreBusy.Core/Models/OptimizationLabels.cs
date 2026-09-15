namespace CoreBusy.Core.Models;

/// <summary>
/// 优化功能的显示标签（v1.12.0）。
/// <para>
/// 集中放在 Core 层而不是各自写在界面/实现里：这些文案会同时出现在设置窗口下拉框、
/// 运行日志和状态提示三处。分散定义过一次的后果是"界面上写着仅物理核、
/// 日志里记着 PhysicalOnly"，排查时对不上号。
/// </para>
/// </summary>
public static class OptimizationLabels
{
    /// <summary>亲和性模式的中文短名。</summary>
    public static string Describe(CoreAffinityMode mode) => mode switch
    {
        CoreAffinityMode.AllCores => "全部核心",
        CoreAffinityMode.PhysicalOnly => "仅物理核",
        CoreAffinityMode.PerformanceOnly => "仅 P 核",
        CoreAffinityMode.EfficiencyOnly => "仅 E 核",
        CoreAffinityMode.Custom => "自定义核心",
        _ => "不干预",
    };

    /// <summary>优先级档位的中文短名。</summary>
    public static string Describe(ProcessPriorityLevel level) => level switch
    {
        ProcessPriorityLevel.Idle => "低",
        ProcessPriorityLevel.BelowNormal => "低于标准",
        ProcessPriorityLevel.Normal => "标准",
        ProcessPriorityLevel.AboveNormal => "高于标准",
        ProcessPriorityLevel.High => "高",
        _ => "不干预",
    };

    /// <summary>亲和性模式的下拉框选项顺序（与 <see cref="CoreAffinityMode"/> 的值序一致）。</summary>
    public static IReadOnlyList<string> AffinityOptions(IReadOnlyList<CoreAffinityMode> modes) =>
        modes.Select(Describe).ToList();

    /// <summary>
    /// 自定义核心掩码的摘要文案（v1.15.0）：显示在「选择核心…」按钮旁。
    /// 规则行与游戏预设共用一份，避免两处文案口径漂移。
    /// </summary>
    public static string DescribeCustomMask(ulong mask) =>
        mask == 0
            ? "尚未选择核心"
            : $"已选 {System.Numerics.BitOperations.PopCount(mask)} 个线程 · 0x{mask:X}";

    /// <summary>优先级档位的下拉框选项顺序（与 <see cref="ProcessPriorityLevel"/> 的值序一致）。</summary>
    public static IReadOnlyList<string> PriorityOptions(IReadOnlyList<ProcessPriorityLevel> levels) =>
        levels.Select(Describe).ToList();
}
