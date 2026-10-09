namespace CoreBusy.Core.Progression;

/// <summary>解锁项的类别。</summary>
public enum CoreUnlockKind
{
    /// <summary>档案记录：把某个事实开始记下来并可回看。</summary>
    Archive = 0,

    /// <summary>统计口径：解锁一种新的聚合或对比。</summary>
    Statistic = 1,

    /// <summary>叙事：由已有数据自动生成的一段文字。</summary>
    Narrative = 2,

    /// <summary>可视化：解锁一种图表。</summary>
    Visualization = 3,
}

/// <summary>一个解锁项的定义。</summary>
/// <param name="Level">解锁等级。</param>
/// <param name="Title">短标题。</param>
/// <param name="Description">解锁后能看到什么。</param>
/// <param name="Kind">类别。</param>
public sealed record CoreUnlock(int Level, string Title, string Description, CoreUnlockKind Kind);

/// <summary>
/// 等级 → 解锁内容的映射（纯逻辑，可静态测试）。
/// </summary>
/// <remarks>
/// <para>
/// EXP 只涨不花等于进度条。等级必须<em>解锁某种东西</em>才有意义。
/// </para>
/// <para>
/// <b>原则：解锁的是「你能看到什么」，不是「你变强了什么」。</b>
/// 这与项目既有约定一致 —— 逐核电压与历史基线<em>只展示、不计分</em>，
/// 因为「同频所需电压上升」要变成分数需要一个标定过的同频点，而本工具不写 MSR、不控频。
/// 等级同理：它可以揭示信息，但不能凭空赋予能力。
/// </para>
/// <para>
/// <b>明确禁止</b>（不得新增此类解锁）：
/// <list type="bullet">
///   <item><c>+X% 性能</c> / 解锁超频 / 永久加成 —— 无中生有，且超频与本工具立场冲突。</item>
///   <item>把<em>逐核自检</em>做成可重复的日常任务 —— 它会把每颗核拉到满载数秒，
///   做成打卡等于每天烧电。触发时机必须由数据决定（异常时建议排查），不由日程决定。</item>
/// </list>
/// </para>
/// </remarks>
public static class CoreProgressionRules
{
    private static readonly CoreUnlock[] Table =
    [
        new(2, "首次满负荷时刻", "记录这颗核第一次跑到 100% 的时间", CoreUnlockKind.Archive),
        new(5, "贡献占比", "这颗核占整机负载的比例", CoreUnlockKind.Statistic),
        new(10, "昼夜节律", "白天与夜间的负载比，看它什么时候醒着", CoreUnlockKind.Statistic),
        new(15, "峰值留档", "历史最高负载及其发生时刻", CoreUnlockKind.Archive),
        new(20, "连续出勤", "连续有观测记录的天数", CoreUnlockKind.Statistic),
        new(30, "周对比", "本周与上周的负荷差异", CoreUnlockKind.Statistic),
        new(40, "核心小传", "由已有数据自动生成的一段经历", CoreUnlockKind.Narrative),
        new(50, "活动热力", "时段 × 星期的二维分布", CoreUnlockKind.Visualization),
        new(60, "频率差距走势", "与同封装各核峰值中位数的差距变化", CoreUnlockKind.Statistic),
        new(70, "齐飞与独跑", "全核齐飞、单核独跑的时刻记录", CoreUnlockKind.Archive),
        new(80, "月度归档", "按月回看它的历史", CoreUnlockKind.Statistic),
        new(90, "负荷直方图", "全时段负载分布", CoreUnlockKind.Visualization),
        new(95, "异常回放", "换人、跳档、越线事件的回放", CoreUnlockKind.Narrative),
        new(99, "满级铭牌", "可导出的纪念铭牌", CoreUnlockKind.Narrative),
    ];

    /// <summary>全部解锁项，按等级升序。</summary>
    public static IReadOnlyList<CoreUnlock> All => Table;

    /// <summary>该等级新解锁的内容。满级返回 null。</summary>
    public static CoreUnlock? UnlockAt(int level)
    {
        foreach (var item in Table)
        {
            if (item.Level == level)
                return item;
        }

        return null;
    }

    /// <summary>
    /// 已解锁的全部内容（等级 ≥ <paramref name="level"/>）。
    /// </summary>
    public static IReadOnlyList<CoreUnlock> Unlocked(int level)
    {
        var list = new List<CoreUnlock>(Table.Length);
        foreach (var item in Table)
        {
            if (item.Level <= level)
                list.Add(item);
        }

        return list;
    }

    /// <summary>
    /// 下一个尚未解锁的项。满级返回 null（没有"下一个"可期待的东西 —— 这是刻意的：
    /// 满级后仍可继续积累 EXP 并被展示，只是不再有新的解锁作为目标）。
    /// </summary>
    public static CoreUnlock? NextUnlock(int level)
    {
        foreach (var item in Table)
        {
            if (item.Level > level)
                return item;
        }

        return null;
    }

    /// <summary>该核是否为兜底主导（长期轻载但常驻）时的角色提示文案。</summary>
    /// <remarks>与核角色系统的衔接点；这里只给判定依据，文案由界面层决定。</remarks>
    public static string FloorDominanceHint(double expCoreMinutes)
    {
        // 满级后不再提示 —— 挂机提示的意义在于"还有事可做"。
        if (double.IsNaN(expCoreMinutes) || CoreLevelCurve.LevelOf(expCoreMinutes) >= CoreLevelCurve.MaxLevel)
            return string.Empty;

        return "经验主要来自陪伴而非负荷";
    }
}