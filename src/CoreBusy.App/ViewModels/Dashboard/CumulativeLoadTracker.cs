namespace CoreBusy.App.ViewModels.Dashboard;

using CoreBusy.Core.Models;

/// <summary>
/// 运行期累积负载累加器：对每颗核心（以及核内每条逻辑线程）做**时间加权积分**。
/// <para>
/// 定义：<c>累积均值 = Σ(使用率% × Δt) / Σ(Δt)</c>，即运行期内在该核上的平均占用百分比。
/// 之所以取时间加权均值、而不是把逐帧瞬时值直接相加，有两个原因：
/// </para>
/// <list type="number">
///   <item>采样周期可被用户改档（500 / 1000 / 2000ms，性能模式另有 2000ms 覆盖）。
///         按帧累加会让周期长的那次采样权重翻倍，同一段负载换个档位就得到不同结论。</item>
///   <item>均值天然落在 0-100，可与实时使用率共用同一套色阶与进度条渲染，
///         界面不需要为累积视图引入第二套刻度（这也是"少即是多"的取舍）。</item>
/// </list>
/// <para>
/// 伴随量 <see cref="BusySeconds"/> 是同一份积分的未归一化形式（等效满负荷秒数），
/// 用于在界面上给出"累计了多少"的绝对量感。由于分母对全局唯一，
/// 它与 <see cref="AveragePercent"/> 的排序结果完全一致。
/// </para>
/// <para>
/// 按 <c>Id</c> 字符串索引而非数组下标：CPU 拓扑或线程数变化时不会错位，也无需处理扩容。
/// 条目数量级为 16-64，单帧查找开销可忽略。
/// </para>
/// </summary>
public sealed class CumulativeLoadTracker
{
    private readonly Dictionary<string, double> _busySeconds = new(StringComparer.Ordinal);

    /// <summary>已积分的墙钟时长（秒）——"运行时间内"的口径，作为归一化分母。</summary>
    public double ObservedSeconds { get; private set; }

    /// <summary>
    /// 按 Δt 积分一次采样。
    /// Δt 由调用方按**墙钟**给定（而非采样周期常量），因此刷新频率、性能模式改档、
    /// 乃至定时器被系统节流，都不会让积分权重失真。
    /// </summary>
    public void Accumulate(IReadOnlyList<CpuCoreSnapshot> snapshots, double deltaSeconds)
    {
        if (deltaSeconds <= 0 || snapshots.Count == 0)
            return;

        ObservedSeconds += deltaSeconds;

        foreach (var core in snapshots)
        {
            Add(core.Id, core.UsagePercent * deltaSeconds);

            foreach (var thread in core.Threads)
                Add(thread.Id, thread.UsagePercent * deltaSeconds);
        }
    }

    /// <summary>累积均值（0-100）。尚未观测到任何时长时返回 0，避免除零与 NaN 上界面。</summary>
    public double AveragePercent(string id)
        => ObservedSeconds <= 0 ? 0 : BusySeconds(id) / ObservedSeconds * 100.0;

    /// <summary>等效满负荷秒数（核·秒）。未出现的 Id 返回 0。</summary>
    public double BusySeconds(string id)
        => _busySeconds.TryGetValue(id, out var value) ? value : 0.0;

    /// <summary>清空累计，运行期内重新开始统计。</summary>
    public void Reset()
    {
        _busySeconds.Clear();
        ObservedSeconds = 0;
    }

    /// <summary>
    /// 等效满负荷量的可读文案。单位按量级自动换档：
    /// 秒级（&lt;60）→ 核·秒，分钟级（&lt;3600）→ 核·分，更久 → 核·时。
    /// 固定单位会让"运行 1 分钟"显示成 0.02 核·时，读不出量感。
    /// </summary>
    public static string FormatBusy(double busySeconds) => busySeconds switch
    {
        < 60 => $"{busySeconds:0.0} 核·秒",
        < 3600 => $"{busySeconds / 60:0.0} 核·分",
        _ => $"{busySeconds / 3600:0.00} 核·时",
    };

    private void Add(string id, double usagePercentSeconds)
    {
        _busySeconds.TryGetValue(id, out var accumulated);

        // 「使用率% × 秒」除以 100 = 等效满负荷秒数（占用 50% 跑 2 秒 = 1 核·秒）。
        _busySeconds[id] = accumulated + usagePercentSeconds / 100.0;
    }
}
