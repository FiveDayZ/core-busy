namespace CoreBusy.Core;

using CoreBusy.Core.Models;

/// <summary>核心负载状态分类器（阈值依据开发方案 3.2 节）。</summary>
public static class CoreStatusClassifier
{
    /// <summary>按占用率划分状态：0-5 摸鱼 / 5-20 空闲 / 20-50 工作 / 50-80 忙碌 / 80-95 高负载 / 95-100 爆肝。</summary>
    public static CoreStatus Classify(double usagePercent)
    {
        return usagePercent switch
        {
            < 5 => CoreStatus.Slacking,
            < 20 => CoreStatus.Idle,
            < 50 => CoreStatus.Working,
            < 80 => CoreStatus.Busy,
            < 95 => CoreStatus.HighLoad,
            _ => CoreStatus.Overloaded,
        };
    }

    /// <summary>状态的中文展示名。</summary>
    public static string ToLabel(this CoreStatus status) => status switch
    {
        CoreStatus.Slacking => "摸鱼",
        CoreStatus.Idle => "空闲",
        CoreStatus.Working => "工作",
        CoreStatus.Busy => "忙碌",
        CoreStatus.HighLoad => "高负载",
        CoreStatus.Overloaded => "爆肝",
        _ => "未知",
    };
}
