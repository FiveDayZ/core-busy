namespace CoreBusy.Core.Progression;

/// <summary>
/// 99 级成长曲线：**解析式**，不查表。
/// </summary>
/// <remarks>
/// <para>
/// 累计经验与等级的关系是一个等比数列的和：
/// <code>
/// C(L) = T0 × (r^(L-1) - 1) / (r - 1)      升到 Lv L 所需累计 EXP
/// S    = T0 / (r - 1)
/// L(c) = 1 + log(1 + c / S) / log(r)由累计 EXP 反解等级
/// </code>
/// </para>
/// <para>
/// <b>为什么必须用解析式而不是「增量表 + 二分查表」。</b>最初的做法是按等比数列
/// 逐级算出增量、取整到 1-2-5 阶梯、存成表再查表。它有两个致命问题：
/// <list type="number">
///   <item><b>不单调。</b>取整后相邻增量大量相等，必须额外做单调化修正；而修正若用
///   绝对量 <c>+1</c>，会与阶梯吸附互相锁死，使总量对 <c>r</c> 完全不敏感
///   （实测 r 从 1.02 到 1.15，总和恒为 1.08亿，二分法永远收敛到搜索区间边界）。
///   改用乘法修正后联动恢复，但台阶跨越又让总量数量级突跳（r 1.12→1.14 翻 4 倍），
///   于是"99 级"与"精确配速"无法同时满足。</item>
///   <item><b>取整误差不可控。</b>逐级取整之和 21,444,482 比精确总量 21,444,480 **多 2**。
///   这 2 点EXP 的偏差会随等级放大，且取决于从哪里开始累加。</item>
/// </list>
/// 解析式消除了取整：等级连续、配速可任意标定、跨版本不漂移。
/// 增量表仍会由 <see cref="BuildIncrementTable"/> 输出为精确整数，
/// 但它<em>只</em>用作文档与回归基线，运行时一律走解析式。
/// </para>
/// <para>
/// <b>标定方式。</b><see cref="R"/> 不是手调的，而是反解出来的：令
/// <c>C(99)/60 = 357,408 核·时</c>（满载机约 6 年），对 <c>r</c> 二分求解。
/// 唯一的标定输入是"满级累计核·时"，其余全部由公式派生。
/// </para>
/// </remarks>
public static class CoreLevelCurve
{
    /// <summary>最高等级。</summary>
    public const int MaxLevel = 99;

    /// <summary>首级门槛：Lv.1 → Lv.2 所需 EXP（核·分）=单核满载 12 分钟。</summary>
    public const double T0 = 12.0;

    /// <summary>
    /// 等级公共倍率。由 <see cref="TargetTotalCoreHours"/> 反解得到。
    /// </summary>
    /// <remarks>
    /// <b>必须存满 17 位有效数字。</b>总量对 <c>r</c> 极敏感：
    /// 只存 6 位（1.13479）时满级总量差<b>61.5 核·时</b>（相对误差 1.7e-4）；
    /// 9 位差 0.006；14 位起才进入1e-6 区间。标定输入是"满级累计核·时"，
    /// 把它标到 0.0001 核·时以内才算真的标上了。
    /// </remarks>
    public const double R = 1.1347921802051704;

    /// <summary>满级（Lv.99）累计经验 = 357,408 核·时 ≈ 满载机 6 年。</summary>
    public const double TargetTotalCoreHours = 357_408.0;

    /// <summary>序列因子<code>S = T0 / (r - 1)</code>，预先算好避免每次重算。</summary>
    private static readonly double SeriesFactor = T0 / (R - 1.0);

    /// <summary>
    /// 等级反解的容差系数（<b>相对</b>，乘以当前阈值）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 必须相对化，不能用固定绝对值。原先取固定 <c>1e-6</c>，结果阈值一旦超过该量级
    /// 容差就反过来吞掉了"刚过阈值一点点"的值 —— 逐级验下来<b>99 级全部</b>被误判成
    /// 高一级。按相对值取几个 ulp，既能吸收 <c>Math.Log</c> 的舍入误差
    /// （<c>C(6)=78.5</c> 这种恰在切换点上的值，差一个 ulp 就会掉级），
    /// 又不会大到掩盖真实的档位。
    /// </para>
    /// <para>
    /// 取 <c>1e-15</c> ≈ 4~5 个 ulp：<c>double</c> 的相对精度是 <c>2^-52 ≈ 2.2e-16</c>，
    /// 即 1 ulp 的相对量级。
    /// </para>
    /// </remarks>
    private const double RelativeEpsilon = 1e-15;

    /// <summary>升到 <paramref name="level"/> 所需的累计 EXP（核·分）。</summary>
    /// <param name="level">目标等级，1 起；超出 <see cref="MaxLevel"/> 按满级计。</param>
    public static double CumulativeExp(int level)
    {
        if (level <= 1)
            return 0.0;

        var clamped = level > MaxLevel ? MaxLevel : level;
        return T0 * (Math.Pow(R, clamped - 1) - 1.0) / (R - 1.0);
    }

    /// <summary>由累计 EXP 反解等级，满级封顶<see cref="MaxLevel"/>。</summary>
    /// <param name="cumExp">累计 EXP（核·分）。NaN / 负值 / 0 一律返回 1。</param>
    public static int LevelOf(double cumExp)
    {
        if (double.IsNaN(cumExp) || cumExp <= 0.0)
            return 1;

        // 先用解析式取一个起点（O(1)），再用带容差的比较修正 —— Math.Log 的舍入
        // 最多让结果偏离一级，一次修正足够；反向也只需一次。
        var raw = 1.0 + Math.Log(1.0 + cumExp / SeriesFactor) / Math.Log(R);
        var level = (int)Math.Floor(raw);
        if (level < 1)
            level = 1;

        // 向上：确实已越过下一级阈值（解析式算低了一级）。
        level = AdjustUp(cumExp, level);
        // 向下：解析式算高了 —— 恰好等于本级阈值时必须归本级（阈值是"含"的）。
        level = AdjustDown(cumExp, level);

        return level < 1 ? 1 : level > MaxLevel ? MaxLevel : level;
    }

    /// <summary>把等级上抬到"确实已越过阈值"的最高一级。</summary>
    private static int AdjustUp(double cumExp, int level)
    {
        while (level < MaxLevel && AtOrAbove(cumExp, level + 1))
            level++;

        return level;
    }

    /// <summary>把等级下压到"尚未越过阈值"的最低一级（处理恰好等于阈值的边界）。</summary>
    private static int AdjustDown(double cumExp, int level)
    {
        while (level > 1 && !AtOrAbove(cumExp, level))
            level--;

        return level;
    }

    /// <summary>累计 EXP 是否已达到 <paramref name="level"/> 的阈值（含相等）。</summary>
    private static bool AtOrAbove(double cumExp, int level)
    {
        var threshold = CumulativeExp(level);
        return cumExp >= threshold - Math.Abs(threshold) * RelativeEpsilon;
    }

    /// <summary>从 <paramref name="level"/> - 1 升到 <paramref name="level"/> 所需增量 EXP（核·分）。</summary>
    public static double StepExp(int level)
    {
        if (level <= 1)
            return 0.0;

        var clamped = level > MaxLevel ? MaxLevel : level;
        return CumulativeExp(clamped) - CumulativeExp(clamped - 1);
    }

    /// <summary>
    /// 距离下一级还差多少 EXP（核·分）。已满级返回 0。
    /// </summary>
    public static double ExpToNextLevel(double cumExp)
    {
        if (double.IsNaN(cumExp))
            return double.NaN;

        var level = LevelOf(cumExp);
        if (level >= MaxLevel)
            return 0.0;

        return CumulativeExp(level + 1) - cumExp;
    }

    /// <summary>
    /// 当前等级内的进度比例 [0, 1]。满级返回 1。
    /// </summary>
    /// <remarks>
    /// 用「本级已积累 / 本级所需」而非「离下一级还差」，前者从低等级起就有可见增长，
    /// 后者在Lv.1 时恒等于 0、看不出区别。
    /// </remarks>
    public static double LevelProgress(double cumExp)
    {
        if (double.IsNaN(cumExp) || cumExp <= 0.0)
            return 0.0;

        var level = LevelOf(cumExp);
        if (level >= MaxLevel)
            return 1.0;

        var floorExp = CumulativeExp(level);
        var ceilExp = CumulativeExp(level + 1);
        var span = ceilExp - floorExp;
        if (span <= 0.0)
            return 1.0;

        var ratio = (cumExp - floorExp) / span;
        // 夹到 [0,1]：cumExp 恰好等于阈值时可能越界一点，界面进度条不接受负值或 >100%。
        return Math.Clamp(ratio, 0.0, 1.0);
    }

    /// <summary>
    /// 输出 2..99 级的精确整数增量表，作为<em>文档与回归基线</em>。
    /// </summary>
    /// <remarks>
    /// 运行时<em>不得</em>用它算等级（见类型注释里的失败复盘）。逐级取整之和
    /// 21,444,482 比 <see cref="CumulativeExp"/>(99) 的 21,444,480 多 2 ——
    /// 这个差值正是"表不能当真源"的实证。
    /// </remarks>
    public static IReadOnlyList<long> BuildIncrementTable()
    {
        var table = new List<long>(MaxLevel - 1);
        for (var level = 2; level <= MaxLevel; level++)
            table.Add((long)Math.Round(StepExp(level)));
        return table;
    }

    /// <summary>累计核·分 → 核·时。</summary>
    public static double ToCoreHours(double coreMinutes)
        => double.IsNaN(coreMinutes) ? double.NaN : coreMinutes / CoreExpCaliber.CoreMinutesPerCoreHour;
}