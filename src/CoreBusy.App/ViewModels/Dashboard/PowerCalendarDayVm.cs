namespace CoreBusy.App.ViewModels.Dashboard;

using CoreBusy.App.Infrastructure;

/// <summary>
/// 「功耗日历」中一格的视图模型。
/// <para>
/// 格子本身**不带颜色**：只暴露 <see cref="Level"/>（0 = 无数据，1..5 = 五档强度），
/// 由 XAML 的 DataTrigger 映射到热力图色带画刷。这样配色仍然只定义在一次资源里，
/// 换主题时不必回头改 C# 里的十六进制字符串。
/// </para>
/// </summary>
public sealed class PowerCalendarDayVm : ObservableObject
{
    /// <summary>档位数（Level 的最大值）。</summary>
    public const int MaxLevel = 5;

    private DateOnly _date;
    private string _dayText = string.Empty;
    private string _tooltip = string.Empty;
    private int _level;
    private bool _isInMonth;
    private bool _isToday;

    /// <summary>该格代表的日期。</summary>
    public DateOnly Date
    {
        get => _date;
        private set => _date = value;
    }

    /// <summary>显示的日期数字；不在当月时留空（占位格仍保留以对齐周次）。</summary>
    public string DayText { get => _dayText; private set => SetProperty(ref _dayText, value); }

    /// <summary>悬浮说明：日期 + 当日能耗 + 观测时长与平均功率。</summary>
    public string Tooltip { get => _tooltip; private set => SetProperty(ref _tooltip, value); }

    /// <summary>强度档位：0 = 无数据，1..5 由低到高。</summary>
    public int Level { get => _level; private set => SetProperty(ref _level, value); }

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
    /// <param name="maxJoules">当前显示月份的当日最高能耗，用于归一化档位。</param>
    /// <param name="inMonth">是否属于当前显示月份。</param>
    /// <param name="today">今天。</param>
    public void Apply(
        DateOnly date, double joules, double seconds, bool hasRecord,
        double maxJoules, bool inMonth, DateOnly today)
    {
        Date = date;
        IsInMonth = inMonth;
        IsToday = date == today;
        DayText = date.Day.ToString();
        Level = ComputeLevel(joules, hasRecord, maxJoules);
        Tooltip = BuildTooltip(date, joules, seconds, hasRecord);
    }

    /// <summary>清空为占位格（当月之外且无数据）。</summary>
    private static int ComputeLevel(double joules, bool hasRecord, double maxJoules)
    {
        if (!hasRecord || joules <= 0 || maxJoules <= 0)
            return 0;

        // 归一化到当月最高那一天。用月内相对而非绝对值定档：不同 CPU 的功耗量级
        // 相差数倍（轻薄本 15W vs 桌面 150W），绝对值定档会让低压平台永远一片浅色。
        var ratio = joules / maxJoules;
        return ratio switch
        {
            < 0.20 => 1,
            < 0.40 => 2,
            < 0.60 => 3,
            < 0.80 => 4,
            _ => 5,
        };
    }

    /// <summary>
    /// 悬浮说明。这里刻意把「观测时长」也写出来 ——
    /// 日历上的一个重要边界是：本程序没运行的时间完全看不到，
    /// 因此某天的数值只代表"观测到的那部分时间"，不属于全天事实。
    /// </summary>
    private static string BuildTooltip(DateOnly date, double joules, double seconds, bool hasRecord)
    {
        var label = $"{date.Month}月{date.Day}日";

        if (!hasRecord || joules <= 0)
            return $"{label} · 无记录\n当天本程序未记录到功耗读数";

        var window = TimeSpan.FromSeconds(seconds);
        var average = seconds > 0 ? joules / seconds : double.NaN;
        var averageText = double.IsNaN(average) ? "-" : $"{average:0} W";

        return $"{label} · {CumulativeEnergyTracker.FormatEnergy(joules)}\n"
            + $"观测 {(int)window.TotalHours}:{window.Minutes:00}:{window.Seconds:00}"
            + $" · 平均 {averageText}";
    }
}
