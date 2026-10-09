namespace CoreBusy.Core.Progression;

/// <summary>
/// 经验值（EXP）的计量口径常量。EXP 是唯一可以安全用于游戏化的货币 ——
/// 它是实测量的时间积分，不含任何模型推断，与
/// <see cref="Health.CoreHealthScore"/> 那类合成代理指标性质完全不同。
/// </summary>
public static class CoreExpCaliber
{
    /// <summary>账本口径标识。写入 <c>core-exp.json</c>，读取时不符即整份归档。</summary>
    /// <remarks>
    /// 沿用<code>energy-history</code> 的既有教训：v1.21.0 口径从「CPU 封装功耗」
    /// 换成「整机估算功率」时，两种量不可比，只能整份归档重开 ——
    /// 按比例硬折出来的历史只是"看起来完整"。EXP 一定会改口径（例如从纯负荷
    /// 改成负荷 + 陪伴兜底），这个坑必须提前留好。
    /// </remarks>
    public const string CaliberId = "load-core-minute/v1";

    /// <summary>EXP 单位：核·分（core-minute）。</summary>
    /// <remarks>
    /// 选核·分而非核·秒，是为了让 <see cref="CoreLevelCurve.T0"/> 的首个门槛
    /// 恰好等于"单核满载 12 分钟"—— 首日即可感知到升级。
    /// 1 核·小时 = 60 EXP。
    /// </remarks>
    public const string Unit = "core-minute";

    /// <summary>核·分 → 核·时 的换算（用于界面展示）。</summary>
    public const double CoreMinutesPerCoreHour = 60.0;

    /// <summary>
    /// 负荷经验权重：占用低于此值时，改由 <see cref="FloorFactor"/> 的陪伴兜底接管。
    /// </summary>
    /// <remarks>
    /// 15% 取在<see cref="CoreStatusClassifier"/> 的「空闲(5-20)」与「工作(20-50)」分界附近：
    /// 低于它算"在但没干活"，用兜底更贴近直觉。
    /// </remarks>
    public const double MinLoadPct = 15.0;

    /// <summary>陪伴兜底系数：观测时长 × 此值。</summary>
    /// <remarks>
    /// 0.15 的来由：让「整日占用 5%~15%」的核恰好与「整日占用 15%」的核拿到相同经验，
    /// 从而使"轻载但常驻"与"刚好压过阈值"在收益上不可区分 —— 避免用户去挑那条刚好越线的边界。
    /// </remarks>
    public const double FloorFactor = 0.15;

    /// <summary>
    /// 有感知阈值（占用百分比）。低于此值视为**完全摸鱼**，不给任何经验。
    /// </summary>
    /// <remarks>
    /// 这一条是设计上的刻意选择：完全闲置的核不该涨经验。否则「让软件挂着不干活」
    /// 就成了最优挂机策略 —— 那与本工具「不制造负载、不干扰前台」的立场直接冲突。
    /// 注意它与 <see cref="MinLoadPct"/> 是两件事：低于 5% 完全不给，
    /// 5%~15% 才启用兜底。
    /// </remarks>
    public const double PresentFloorPct = 5.0;
}