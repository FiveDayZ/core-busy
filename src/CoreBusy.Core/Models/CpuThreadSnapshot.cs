namespace CoreBusy.Core.Models;

/// <summary>
/// 单条逻辑处理器（SMT 线程）的运行状态快照。
/// <para>
/// 一个物理核含 1~2 条线程：开启 SMT 的核（AMD Zen 全系、Intel P-Core）为 2 条，
/// 未开启 SMT 的核与 Intel E-Core 为 1 条。界面用它把"核心数 / 线程数"两者都完整呈现：
/// Tile 保持物理核为格、核内展开线程条；热力图则按线程逐行铺开。
/// </para>
/// </summary>
public sealed record CpuThreadSnapshot
{
    /// <summary>线程显示名（单线程核与核心同名 "C0"，多线程核为 "C0·0" / "C0·1"）。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>所属物理核显示名（"C0" / "P3" / "E2"）。</summary>
    public string CoreId { get; init; } = string.Empty;

    /// <summary>核内线程序号（从 0 起）。</summary>
    public int Index { get; init; }

    /// <summary>使用率（0-100）。</summary>
    public double UsagePercent { get; init; }

    /// <summary>当前频率（GHz）。</summary>
    public double FrequencyGHz { get; init; }

    /// <summary>负载状态（由 CoreStatusClassifier 划分）。</summary>
    public CoreStatus Status { get; init; }
}
