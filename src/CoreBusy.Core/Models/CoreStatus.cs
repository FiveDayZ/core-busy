namespace CoreBusy.Core.Models;

/// <summary>核心负载状态分类（依据开发方案 3.2 节状态表）。</summary>
public enum CoreStatus
{
    /// <summary>0-5% 摸鱼</summary>
    Slacking = 0,

    /// <summary>5-20% 空闲</summary>
    Idle = 1,

    /// <summary>20-50% 工作</summary>
    Working = 2,

    /// <summary>50-80% 忙碌</summary>
    Busy = 3,

    /// <summary>80-95% 高负载</summary>
    HighLoad = 4,

    /// <summary>95-100% 爆肝</summary>
    Overloaded = 5,
}
