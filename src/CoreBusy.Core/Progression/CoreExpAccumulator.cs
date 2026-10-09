namespace CoreBusy.Core.Progression;

using CoreBusy.Core.Models;

/// <summary>
/// 单个物理核的 EXP 累加器：按 Δt 对<see cref="CpuCoreSnapshot.UsagePercent"/> 做时间加权积分。
/// </summary>
/// <remarks>
/// <para>
/// <b>物理核归并。</b>本累加器只消费 <see cref="CpuCoreSnapshot.UsagePercent"/>，
/// <em>不</em>碰 <see cref="CpuCoreSnapshot.Threads"/>。原因是
/// <see cref="Models.CpuCoreSnapshot"/> 体系里物理核与逻辑线程（<c>T0</c>/<c>T1</c>）
/// 是两个不同层级的对象，若像 <c>CumulativeLoadTracker</c> 那样按 <c>Id</c> 混在一个字典里，
/// 一颗物理核会分裂成两个独立等级。从接口上排除这种可能：只有核级快照能喂进来。
/// </para>
/// <para>
/// <b>Δt 一律取墙钟差</b>，与 <c>CumulativeLoadTracker.Accumulate</c> 完全一致。
/// 采样周期可被用户改档（500 / 1000 / 2000ms，性能模式另有 2000ms 覆盖），
/// 定时器也可能被系统节流；按固定周期累加会让周期长的那次权重翻倍。
/// </para>
/// <para>
/// <b>口径</b>（全部判定的唯一入口是 <see cref="SplitCoreMinutes"/>）：
/// <code>
/// 占用 ≥ 15%    → 负荷 = 占用%/100 × Δt
/// 5% ≤ 占用 &lt; 15% → 兜底 = Δt × 0.15
/// 占用 &lt; 5%     → 两者皆0（完全摸鱼不给经验）
/// EXP = max(负荷, 兜底)
/// </code>
/// 两项原始量分别累加保存，界面才能回答"这颗核的经验是干活挣的还是陪着挣的"——
/// 兜底长期生效是核角色判定的输入。
/// </para>
/// </remarks>
public sealed class CoreExpAccumulator
{
    private readonly Dictionary<string, double> _loadCoreMinutes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _floorCoreMinutes = new(StringComparer.Ordinal);

    /// <summary>已积分的墙钟时长（秒）。</summary>
    public double ObservedSeconds { get; private set; }

    /// <summary>出现过的物理核 Id。</summary>
    public IReadOnlyCollection<string> CoreIds => _loadCoreMinutes.Keys;

    /// <summary>
    /// 把一次采样的占用率拆成两项原始量（核·分）。
    /// </summary>
    /// <param name="usagePercent">核占用率（0-100）。NaN 视为无证据，两项皆 0。</param>
    /// <param name="deltaSeconds">墙钟差（秒）。非正数视为无效，两项皆 0。</param>
    public static (double Load, double Floor) SplitCoreMinutes(double usagePercent, double deltaSeconds)
    {
        // 读不到就不给：NaN 不参与任何判定，更不能拿 0 冒充"真的空闲"。
        if (double.IsNaN(usagePercent) || double.IsNaN(deltaSeconds) || deltaSeconds <= 0)
            return (0.0, 0.0);

        var minutes = deltaSeconds / 60.0;

        // 完全摸鱼：低于有感知阈值不给任何经验。
        // 刻意如此 —— 否则「挂着软件不干活」会变成最优挂机策略。
        if (usagePercent < CoreExpCaliber.PresentFloorPct)
            return (0.0, 0.0);

        var load = usagePercent / 100.0 * minutes;

        // 轻载区间由陪伴兜底接管；两项并存但**不相加**，
        // 取 max 是在 ExpCoreMinutes 里做的（相加会在 5%~15% 区间产生约 2.5 倍套利）。
        if (usagePercent < CoreExpCaliber.MinLoadPct)
            return (load, minutes * CoreExpCaliber.FloorFactor);

        return (load, 0.0);
    }

    /// <summary>
    /// 一次采样的授奖量（核·分）。
    /// </summary>
    /// <remarks>保留为独立入口便于单测直接打点；语义与 <see cref="ExpCoreMinutes"/> 的 max 口径一致。</remarks>
    public static double AwardCoreMinutes(double usagePercent, double deltaSeconds)
    {
        var (load, floor) = SplitCoreMinutes(usagePercent, deltaSeconds);
        return Math.Max(load, floor);
    }

    /// <summary>按 Δt 积分一次采样。</summary>
    /// <param name="snapshots">物理核级快照。</param>
    /// <param name="deltaSeconds">墙钟差（秒）。非正数直接忽略。</param>
    public void Accumulate(IReadOnlyList<CpuCoreSnapshot> snapshots, double deltaSeconds)
    {
        if (double.IsNaN(deltaSeconds) || deltaSeconds <= 0 || snapshots.Count == 0)
            return;

        ObservedSeconds += deltaSeconds;

        foreach (var core in snapshots)
        {
            if (string.IsNullOrEmpty(core.Id))
                continue;

            var (load, floor) = SplitCoreMinutes(core.UsagePercent, deltaSeconds);

            _loadCoreMinutes.TryGetValue(core.Id, out var accLoad);
            _floorCoreMinutes.TryGetValue(core.Id, out var accFloor);

            _loadCoreMinutes[core.Id] = accLoad + load;

            // 兜底必须**累加**，因为它是逐 Δt 区间的量：每段的 Δt 不同，判定出的兜底也不同。
            //（曾误用 Math.Max，结果 240 段 × 14% 只剩最后一段的 0.9 而非 216。）
            //「不相加」指的是同一段内负荷与兜底不叠加，由 ExpCoreMinutes 的 max 承担；
            // 不同段之间是各自独立的观测，必须都记上。
            _floorCoreMinutes[core.Id] = accFloor + floor;
        }
    }

    /// <summary>该核累计获得的 EXP（核·分）= max(负荷, 兜底)。未出现过的 Id 返回 0。</summary>
    public double ExpCoreMinutes(string id)
    {
        if (string.IsNullOrEmpty(id))
            return 0.0;

        var load = _loadCoreMinutes.TryGetValue(id, out var l) ? l : 0.0;
        var floor = _floorCoreMinutes.TryGetValue(id, out var f) ? f : 0.0;
        return Math.Max(load, floor);
    }

    /// <summary>该核的负荷经验原始量（核·分）。</summary>
    public double LoadCoreMinutes(string id)
        => !string.IsNullOrEmpty(id) && _loadCoreMinutes.TryGetValue(id, out var v) ? v : 0.0;

    /// <summary>该核的陪伴兜底原始量（核·分）。</summary>
    public double FloorCoreMinutes(string id)
        => !string.IsNullOrEmpty(id) && _floorCoreMinutes.TryGetValue(id, out var v) ? v : 0.0;

    /// <summary>该核当前等级。</summary>
    public int LevelOf(string id) => CoreLevelCurve.LevelOf(ExpCoreMinutes(id));

    /// <summary>该核距下一级的 EXP 缺口。已满级为 0。</summary>
    public double ExpToNextLevel(string id) => CoreLevelCurve.ExpToNextLevel(ExpCoreMinutes(id));

    /// <summary>该核本级进度比例 [0,1]。</summary>
    public double LevelProgress(string id) => CoreLevelCurve.LevelProgress(ExpCoreMinutes(id));

    /// <summary>该核是否处于兜底主导（轻载但常驻），是核角色判定的输入。</summary>
    public bool IsFloorDominated(string id)
    {
        if (string.IsNullOrEmpty(id))
            return false;

        var load = LoadCoreMinutes(id);
        var floor = FloorCoreMinutes(id);
        return floor > 0 && floor >= load;
    }

    /// <summary>清空累计，重新开始。</summary>
    public void Reset()
    {
        _loadCoreMinutes.Clear();
        _floorCoreMinutes.Clear();
        ObservedSeconds = 0;
    }
}