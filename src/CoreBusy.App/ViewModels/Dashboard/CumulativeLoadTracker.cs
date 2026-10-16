namespace CoreBusy.App.ViewModels.Dashboard;

using CoreBusy.Core.Models;

/// <summary>
/// 本次运行的时间加权负载。核心与线程分别积分，缺失读数不计入该 Id 的分子或分母。
/// 单线程核心与线程共用 Id 时只记核心一次；均值 = 有效负载积分 / 该 Id 的有效观测时长。
/// </summary>
public sealed class CumulativeLoadTracker
{
    private readonly Dictionary<string, (double Busy, double Observed)> _samples = new(StringComparer.Ordinal);

    /// <summary>本次统计窗口时长（秒），不充当缺失核心或线程的归一化分母。</summary>
    public double ObservedSeconds { get; private set; }

    /// <summary>
    /// 按 Δt 积分一次采样。
    /// Δt 由调用方按**墙钟**给定（而非采样周期常量），因此刷新频率、性能模式改档、
    /// 乃至定时器被系统节流，都不会让积分权重失真。
    /// </summary>
    public void Accumulate(IReadOnlyList<CpuCoreSnapshot> snapshots, double deltaSeconds)
    {
        if (!double.IsFinite(deltaSeconds) || deltaSeconds <= 0 || snapshots.Count == 0)
            return;

        ObservedSeconds += deltaSeconds;

        foreach (var core in snapshots)
        {
            Add(core.Id, core.UsagePercent, deltaSeconds);

            foreach (var thread in core.Threads)
                if (!StringComparer.Ordinal.Equals(thread.Id, core.Id))
                    Add(thread.Id, thread.UsagePercent, deltaSeconds);
        }
    }

    /// <summary>累积均值（0-100）。尚未观测到任何时长时返回 0，避免除零与 NaN 上界面。</summary>
    public double AveragePercent(string id)
        => _samples.TryGetValue(id, out var sample) ? sample.Busy / sample.Observed * 100.0 : 0;

    /// <summary>等效满负荷秒数（核·秒）。未出现的 Id 返回 0。</summary>
    public double BusySeconds(string id)
        => _samples.TryGetValue(id, out var sample) ? sample.Busy : 0.0;

    /// <summary>该 Id 实际取得有效负载读数的时长（秒）。</summary>
    public double ValidSeconds(string id)
        => _samples.TryGetValue(id, out var sample) ? sample.Observed : 0.0;

    /// <summary>清空累计，运行期内重新开始统计。</summary>
    public void Reset()
    {
        _samples.Clear();
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

    private void Add(string id, double usagePercent, double deltaSeconds)
    {
        if (string.IsNullOrEmpty(id) || !double.IsFinite(usagePercent) || usagePercent is < 0 or > 100)
            return;

        _samples.TryGetValue(id, out var sample);
        _samples[id] = (sample.Busy + usagePercent / 100.0 * deltaSeconds, sample.Observed + deltaSeconds);
    }
}
