namespace CoreBusy.Core.Progression;

using CoreBusy.Core.Models;

/// <summary>
/// 核角色统计累加器：把逐帧快照归并成"长期行为画像"所需的统计量。
/// </summary>
/// <remarks>
/// <para>
/// <b>与 <see cref="CoreExpAccumulator"/> 的三处口径差异</b>（刻意不同，不可混用）：
/// <list type="number">
///   <item><b>负荷不含陪伴兜底。</b>EXP 的负荷在低载区被兜底顶起，而角色要回答
///   "它到底干了多少活" —— 兜底会把"闲"抹成"轻"、把"轻"抹成"勤"，
///   两颗实际差 10 倍的核可能拿到同一个角色。</item>
///   <item><b>额外累加负荷平方</b>，用于方差（稳定性）。只存均值无法区分
///   "稳定 50%" 与 "在 0% 和 100% 之间反复横跳" —— 而这两者恰是稳态搬运工
///   与尖峰侠的分界。</item>
///   <item><b>按昼夜切分负荷</b>，用于夜猫子判定。切分依据是采样时刻的本地时间。</item>
/// </list>
/// </para>
/// <para>
/// <b>昼夜边界</b>：白天 08:00–20:00，夜间其余。选 08:00 而非 06:00 是因为
/// 它把"上班/摸鱼时段"与"睡眠时段"大致分开，足以支撑"夜猫子"这个判定，
/// 又不至于细到要求用户作息规律。
/// </para>
/// </remarks>
public sealed class CoreRoleAccumulator
{
    /// <summary>白天起始小时（含）。</summary>
    public const int DayStartHour = 8;

    /// <summary>白天结束小时（不含）。</summary>
    public const int DayEndHour = 20;

    private readonly Dictionary<string, double> _observed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _load = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _loadSquared = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _dayLoad = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _nightLoad = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _peak = new(StringComparer.Ordinal);

    /// <summary>出现过的物理核 Id。</summary>
    public IReadOnlyCollection<string> CoreIds => _observed.Keys;

    /// <summary>
    /// 按 Δt 积分一次采样。
    /// </summary>
    /// <param name="snapshots">物理核级快照。</param>
    /// <param name="deltaSeconds">墙钟差（秒）。</param>
    /// <param name="now">本次采样时刻（本地时间），用于昼夜归属与跨零点切分。</param>
    public void Accumulate(
        IReadOnlyList<CpuCoreSnapshot> snapshots, double deltaSeconds, DateTime now)
    {
        if (double.IsNaN(deltaSeconds) || deltaSeconds <= 0 || snapshots.Count == 0)
            return;

        foreach (var core in snapshots)
        {
            if (string.IsNullOrEmpty(core.Id))
                continue;

            Add(_observed, core.Id, deltaSeconds / 60.0);

            var usage = core.UsagePercent;
            // NaN（读不到）不参与任何统计 —— 不拿 0 冒充"真的空闲"。
            if (!double.IsNaN(usage))
            {
                var (load, loadSquared, dayLoad, nightLoad) = SplitByDayNight(usage, deltaSeconds, now);
                Add(_load, core.Id, load);
                Add(_loadSquared, core.Id, loadSquared);
                Add(_dayLoad, core.Id, dayLoad);
                Add(_nightLoad, core.Id, nightLoad);

                if (usage > PeakOf(core.Id))
                    _peak[core.Id] = usage;
            }
        }
    }

    /// <summary>
    /// 按昼夜与跨零点把一段 Δt 拆成四项（负荷、负荷平方、白天负荷、夜间负荷）。
    /// </summary>
    /// <remarks>
    /// <b>跨零点必须切分</b>：一段 5 分钟的 Δt 若整块记到结束时刻所在的那一天，
    /// 睡前那一段会整体记到第二天头上。能耗账本已经踩过这个坑（见
    /// <see cref="Dashboard.DailyEnergyLedger"/> 的 Record），这里沿用同一做法。
    /// </remarks>
    private static (double Load, double LoadSquared, double Day, double Night) SplitByDayNight(
        double usagePercent, double deltaSeconds, DateTime now)
    {
        var start = now.AddSeconds(-deltaSeconds);

        //快路径只在「不跨零点<em>且</em>不跨昼夜分界」时成立。
        // 早先的版本只判了 `start.Date == now.Date`，漏掉了昼夜分界：
        // 19:59:30 起算 60 秒虽然同属一天，却有一半落在夜间，
        // 结果白天记 60、夜间记 0 —— 夜猫子判定因此失真。
        var sameDay = start.Date == now.Date;
        var sameDayPart = sameDay && IsDayTime(start) == IsDayTime(now);

        if (sameDayPart)
            return Single(usagePercent, deltaSeconds, IsDayTime(now));

        // 逐段处理。采样周期最长 2s，一段 Δt 内的分界至多一个；
        // 但仍用循环而非假设"只有两段"，以免长时间挂起后（休眠唤醒）
        // 跨过多个午夜时把中间的量都算到某一天。
        double load = 0, loadSquared = 0, day = 0, night = 0;
        var cursor = start;
        var remaining = deltaSeconds;

        // 上限保护：60 段足够覆盖 120 秒周期下跨 2 分钟的情形，
        // 也避免时钟异常回拨时死循环。超出的余量在循环后并入末段（少记而非错记）。
        for (var guard = 0; remaining > 1e-9 && guard < 60; guard++)
        {
            // 下一个分界点：若当前在夜间段则是次日 08:00，若在白天段则是当日 20:00。
            var boundary = NextBoundary(cursor);
            var toBoundary = (boundary - cursor).TotalSeconds;
            var step = toBoundary > 1e-9 ? Math.Min(remaining, toBoundary) : remaining;

            var (sLoad, sSquared, sDay, sNight) = Single(usagePercent, step, IsDayTime(cursor));
            load += sLoad;
            loadSquared += sSquared;
            day += sDay;
            night += sNight;

            cursor = cursor.AddSeconds(step);
            remaining -= step;

            if (step <= 0)
                break;
        }

        // 剩余部分（循环用尽）并入末段。
        if (remaining > 1e-9)
        {
            var (sLoad, sSquared, sDay, sNight) = Single(usagePercent, remaining, IsDayTime(cursor));
            load += sLoad;
            loadSquared += sSquared;
            day += sDay;
            night += sNight;
        }

        return (load, loadSquared, day, night);
    }

    /// <summary>
    /// 从 <paramref name="from"/> 起的下一个昼夜分界时刻。
    /// </summary>
    /// <remarks>
    /// 分界点有两个：20:00（白天→夜间）与次日 08:00（夜间→白天）。
    /// 取"距当前最近的那个"，循环据此推进。
    /// </remarks>
    private static DateTime NextBoundary(DateTime from)
    {
        var dayStart = from.Date.AddHours(DayStartHour);
        var dayEnd = from.Date.AddHours(DayEndHour);

        if (from < dayStart)
            return dayStart;
        if (from < dayEnd)
            return dayEnd;
        return dayStart.AddDays(1);
    }

    /// <summary>单段（不跨零点）的四项拆分。</summary>
    private static (double Load, double LoadSquared, double Day, double Night) Single(
        double usagePercent, double deltaSeconds, bool isDayTime)
    {
        var minutes = deltaSeconds / 60.0;
        var load = usagePercent / 100.0 * minutes;

        // 方差用 E[x²] 的一阶估计：单段内 x 取瞬时值，x² 乘时长。
        // 注意单位是 (%² × 核·分)，与 <see cref="CoreRoleStats.LoadSquaredCoreMinutes"/> 一致 ——
        // 方差公式里与 E[x²] 的时长因子正好抵消。
        var loadSquared = usagePercent * usagePercent * minutes;

        var byDay = isDayTime ? load : 0.0;
        var byNight = isDayTime ? 0.0 : load;
        return (load, loadSquared, byDay, byNight);
    }

    /// <summary>该时刻是否算白天（<see cref="DayStartHour"/> ≤ 时 &lt; <see cref="DayEndHour"/>）。</summary>
    public static bool IsDayTime(DateTime local)
        => local.Hour >= DayStartHour && local.Hour < DayEndHour;

    /// <summary>该核的历史峰值占用率；无记录返回 NaN。</summary>
    public double PeakOf(string id)
        => !string.IsNullOrEmpty(id) && _peak.TryGetValue(id, out var v) ? v : double.NaN;

    /// <summary>该核的统计量快照（供 <see cref="CoreRoleClassifier"/> 判定）。</summary>
    public CoreRoleStats StatsOf(string id)
    {
        var observed = ObservedOf(id);
        return new CoreRoleStats
        {
            ObservedCoreMinutes = observed,
            LoadCoreMinutes = Of(_load, id),
            LoadSquaredCoreMinutes = Of(_loadSquared, id),
            PeakPercent = PeakOf(id),
            DayLoadCoreMinutes = Of(_dayLoad, id),
            NightLoadCoreMinutes = Of(_nightLoad, id),
        };
    }

    /// <summary>该核的判定结果。</summary>
    public CoreRole RoleOf(string id) => CoreRoleClassifier.Classify(StatsOf(id));

    /// <summary>该核累计观测时长（核·分）。</summary>
    public double ObservedOf(string id) => Of(_observed, id);

    private static double Of(Dictionary<string, double> map, string id)
        => !string.IsNullOrEmpty(id) && map.TryGetValue(id, out var v) ? v : 0.0;

    private static void Add(Dictionary<string, double> map, string id, double value)
    {
        map.TryGetValue(id, out var current);
        map[id] = current + value;
    }
}