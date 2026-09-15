namespace CoreBusy.App.ViewModels.Dashboard;

using CoreBusy.App.Infrastructure;

/// <summary>
/// 「能耗日历」中一格的视图模型。
/// <para>
/// 格子本身**不带颜色**：只暴露 <see cref="Level"/>（0 = 不定档，1..5 = 五档强度），
/// 由 XAML 的 DataTrigger 映射到热力图色带画刷。这样配色仍然只定义在一次资源里，
/// 换主题时不必回头改 C# 里的十六进制字符串。
/// </para>
/// <para>
/// <b>v1.19.2 换了定档判据量。</b>原实现是「当日累计能耗 ÷ 当月最高一天的累计能耗」，
/// 两个缺陷在 2026-09-15 用本机真实档案（<c>%APPDATA%\CORE-BUSY\energy-history.json</c>）实测确认：
/// </para>
/// <list type="number">
/// <item>
/// <b>判据量选错 —— 把「程序开了多久」当成了「机器用得多不多」。</b>
/// 累计焦耳 ≈ 平均功率 × 观测时长，而观测时长逐日差别远大于功率本身。
/// 实测四天：累计焦耳相差 3.08 倍，平均功率只相差 1.54 倍（13.4 / 14.4 / 15.9 / 20.6 W）。
/// 也就是说旧口径给出的「多/少」主要在反映观测覆盖。
/// 极端后果：被判「偏少」的那天（20.6 W，仅观测 0.55 h）其实是四天里平均功率最高的一天。
/// 故判据量改为<b>当日平均功率</b>（累计焦耳 ÷ 观测秒数）—— 它与观测时长无关，逐日可比。
/// </item>
/// <item>
/// <b>参照量取了极值 —— 标准随数据漂移。</b>
/// 旧参照 <c>maxJoules</c> 是当月单日最大值，定义上「最高那天」恒为 5 档、其余都是它的一个比例：
/// 一个异常日（通宵跑测试/整夜开机）就能把整月压成浅色，而一个平淡的月也必然铺满五档。
/// 改为<b>稳健中心</b>：达标日平均功率的中位数，并设最少达标日门槛。
/// 这与本项目在频率峰值估计上把「90 分位」换成「上四分位均值」是同一条教训
/// —— 同一类疏漏的第三次出现，这次落在日历上。
/// </item>
/// </list>
/// <para>
/// 另外新增一道<b>观测覆盖门槛</b>：观测不足 <see cref="MinRankableObservedSeconds"/> 的日子
/// 不做多/少判断 —— 它是「没看够」，不是「用得少」，把两者画成同一个色阶是误导。
/// 这类日子与「有记录但本月参照不足」共用同一个不定档外观，具体原因写在悬浮说明里。
/// </para>
/// <para>
/// <b>代价要说清</b>：换成平均功率定档后，多数月份的日子会落在 3 档（相当）附近，
/// 视觉上比旧版"一片深浅分明"要平。这是真实的平——四天平均功率只差 1.54 倍，日历就该显示差不多。
/// 想看到区分度需要真实的负载差异，而不是把它造出来。
/// </para>
/// </summary>
public sealed class PowerCalendarDayVm : ObservableObject
{
    /// <summary>色带档位数（<see cref="Level"/> 的最大有效值）。</summary>
    public const int MaxLevel = 5;

    /// <summary>
    /// 参与定档所需的最小观测秒数。低于此值 = 「没看够」，不做多/少判断。
    /// 取 1 小时是<b>判断值而非推导值</b>：再短，当天的平均功率就更容易被"恰好当时在干什么"主导，
    /// 而不是代表这一天。它只影响"是否定档"，不影响已定档日子的数值。
    /// </summary>
    public const double MinRankableObservedSeconds = 3600.0;

    /// <summary>建立参照所需的最少达标日数；不足则本月整体不定档（宁可不说，不给假结论）。</summary>
    public const int MinReferenceDays = 3;

    // 档位边界：相对参照（达标日平均功率的中位数）的倍数。刻意按对数近似等宽：
    // ln(0.70/0.90)≈-0.25、ln(0.90/1.15)≈-0.25、ln(1.15/1.50)≈+0.27。
    // 带宽取得比"看上去合理"更宽，是为了让物理上相近的日子落在同一档
    // —— 否则又会退化成"每天都不同"的假区分度。

    /// <summary>低于此倍数判 1 档（明显偏少）。</summary>
    public const double BandTooLow = 0.70;

    /// <summary>低于此倍数判 2 档（偏少）。</summary>
    public const double BandLow = 0.90;

    /// <summary>低于此倍数判 3 档（相当）；否则继续看 <see cref="BandTooHigh"/>。</summary>
    public const double BandHigh = 1.15;

    /// <summary>低于此倍数判 4 档（偏多），否则判 5 档（明显偏多）。</summary>
    public const double BandTooHigh = 1.50;

    private DateOnly _date;
    private string _dayText = string.Empty;
    private string _tooltip = string.Empty;
    private int _level;
    private bool _isInMonth;
    private bool _isToday;
    private bool _hasRecord;
    private bool _isRanked;
    private double _averageWatt;

    /// <summary>该格代表的日期。</summary>
    public DateOnly Date
    {
        get => _date;
        private set => _date = value;
    }

    /// <summary>显示的日期数字；不在当月时留空（占位格仍保留以对齐周次）。</summary>
    public string DayText { get => _dayText; private set => SetProperty(ref _dayText, value); }

    /// <summary>悬浮说明：日期 + 累计能耗 + 观测时长 + 平均功率 + 全天等效 + 定档依据。</summary>
    public string Tooltip { get => _tooltip; private set => SetProperty(ref _tooltip, value); }

    /// <summary>强度档位：0 = 不定档，1..5 由少到多。仅 <see cref="IsRanked"/> 为 true 时有意义。</summary>
    public int Level { get => _level; private set => SetProperty(ref _level, value); }

    /// <summary>当天是否有有效功耗读数（累计值与观测时长都为正）。</summary>
    public bool HasRecord { get => _hasRecord; private set => SetProperty(ref _hasRecord, value); }

    /// <summary>当天是否参与了多/少定档（观测够长，且本月参照有效）。</summary>
    public bool IsRanked { get => _isRanked; private set => SetProperty(ref _isRanked, value); }

    /// <summary>当日平均功率（W）；无记录为 0。这是多/少定档所用的量。</summary>
    public double AverageWatt { get => _averageWatt; private set => SetProperty(ref _averageWatt, value); }

    /// <summary>是否属于当前显示的月份（前后补Week的邻月日期为 false）。</summary>
    public bool IsInMonth { get => _isInMonth; private set => SetProperty(ref _isInMonth, value); }

    /// <summary>是否为今天（用于描边强调）。</summary>
    public bool IsToday { get => _isToday; private set => SetProperty(ref _isToday, value); }

    /// <summary>
    /// 写入一格的全部显示状态。
    /// </summary>
    /// <param name="date">该格日期。</param>
    /// <param name="joules">当日累计能耗（焦耳），无数据传 0。</param>
    /// <param name="seconds">当日有效观测秒数。</param>
    /// <param name="hasRecord">当日是否有功耗读数。</param>
    /// <param name="referenceWatt">本月参照平均功率（达标日中位数）；无有效参照传 0。</param>
    /// <param name="inMonth">是否属于当前显示月份。</param>
    /// <param name="today">今天。</param>
    public void Apply(
        DateOnly date, double joules, double seconds, bool hasRecord,
        double referenceWatt, bool inMonth, DateOnly today)
    {
        Date = date;
        IsInMonth = inMonth;
        IsToday = date == today;
        DayText = date.Day.ToString();

        HasRecord = hasRecord && joules > 0 && seconds > 0;
        AverageWatt = HasRecord ? joules / seconds : 0.0;

        // 三重门槛依次是：有读数 → 观测够长 → 本月参照成立。任何一环不成立就不给档位。
        var enoughObservation = HasRecord && seconds >= MinRankableObservedSeconds;
        IsRanked = enoughObservation && referenceWatt > 0;
        Level = IsRanked ? ComputeLevel(AverageWatt, referenceWatt) : 0;

        Tooltip = BuildTooltip(date, joules, seconds, referenceWatt);
    }

    /// <summary>
    /// 由达标日的平均功率样本算参照值（中位数）。样本不足 <see cref="MinReferenceDays"/> 返回 0，
    /// 调用方据此把整月置为不定档。
    /// <para>
    /// 用中位数而不是均值或极值：这里要的是「常见日有多重」。均值会被通宵跑测试那种
    /// 极端日拖走，极值更是直接由它决定（这正是 v1.19.2 修掉的缺陷）。
    /// 放在这里而非 <c>DashboardViewModel</c>，是为了让"参照怎么算"只有一处定义、可被探针直测。
    /// </para>
    /// </summary>
    /// <param name="fulfilledWatt">达标日（观测 ≥ <see cref="MinRankableObservedSeconds"/>）的平均功率。</param>
    public static double ReferenceFrom(IReadOnlyList<double>? fulfilledWatt)
    {
        if (fulfilledWatt is null || fulfilledWatt.Count < MinReferenceDays)
            return 0.0;

        var sorted = new double[fulfilledWatt.Count];
        for (var i = 0; i < sorted.Length; i++)
            sorted[i] = fulfilledWatt[i];

        Array.Sort(sorted);
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    /// <summary>
    /// 按「当日平均功率 ÷ 参照平均功率」定档。
    /// 判据量刻意不用累计能耗：累计量把观测时长混了进来（见类注释里的实测数据）。
    /// </summary>
    private static int ComputeLevel(double averageWatt, double referenceWatt)
    {
        var ratio = averageWatt / referenceWatt;
        return ratio switch
        {
            < BandTooLow => 1,
            < BandLow => 2,
            < BandHigh => 3,
            < BandTooHigh => 4,
            _ => 5,
        };
    }

    /// <summary>
    /// 悬浮说明。这里刻意把「观测时长」与「平均功率」都写出来 ——
    /// 日历上的一个重要边界是：本程序没运行的时间完全看不到，
    /// 因此某天的累计值只代表"观测到的那部分时间"，不属于全天事实。
    /// 横向比较必须落在平均功率上，这也正是多/少定档的依据；
    /// 「全天等效」只作为把速率换算回电量直觉的辅助读数，明确标注为外推值。
    /// </summary>
    private static string BuildTooltip(
        DateOnly date, double joules, double seconds, double referenceWatt)
    {
        var label = $"{date.Month}月{date.Day}日";

        if (joules <= 0 || seconds <= 0)
            return $"{label} · 无记录\n当天本程序未记录到功耗读数";

        var window = TimeSpan.FromSeconds(seconds);
        var average = joules / seconds;

        var text = $"{label} · 累计 {CumulativeEnergyTracker.FormatEnergy(joules)}\n"
            + $"观测 {(int)window.TotalHours}:{window.Minutes:00}:{window.Seconds:00}"
            + $" · 平均 {average:0.0} W\n"
            + $"全天等效 {CumulativeEnergyTracker.FormatEnergy(average * 86400.0)}"
            + "（按观测期外推，仅为可比性）";

        if (seconds < MinRankableObservedSeconds)
        {
            return text
                + $"\n观测不足 {MinRankableObservedSeconds / 3600:0} 小时，不足以代表全天，"
                + "不参与多/少定档";
        }

        if (referenceWatt <= 0)
        {
            return text
                + $"\n本月达标记录不足 {MinReferenceDays} 天，尚未建立参照，故不定档";
        }

        var ratio = average / referenceWatt;
        return text
            + $"\n多/少按平均功率定档：为常见日（{referenceWatt:0.0} W）的 {ratio:0.00}×"
            + "，与观测时长无关";
    }
}
