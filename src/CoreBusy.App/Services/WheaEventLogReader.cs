namespace CoreBusy.App.Services;

using System.Diagnostics.Eventing.Reader;
using CoreBusy.App.Infrastructure;
using CoreBusy.Core.Interfaces;
using CoreBusy.Core.Models;

/// <summary>
/// 从 Windows 事件日志读取 WHEA 硬件错误统计（v1.17.0）。
/// <para>
/// 选型说明：本类放在 **App 层**而不是 <c>CoreBusy.Windows</c>，是因为
/// <c>System.Diagnostics.EventLog</c> 属于 Windows 桌面共享框架 ——
/// App 是 WinExe（<c>UseWPF</c>）天然具备，而 CoreBusy.Windows 是普通类库，
/// 要用就得额外引入 NuGet 包。为一个只读事件日志的功能加依赖不划算，
/// 契约（<see cref="IWheaErrorSource"/>）仍留在 Core，评分器照样可测。
/// </para>
/// <para>
/// <b>免提权可用</b>：System 日志对普通用户可读（Security 日志才需要特权）。
/// 这一点很重要 —— 温度、功耗、风扇全都要提权才有，健康度体系里总该有一项
/// 在非提权会话下也说得上话。
/// </para>
/// </summary>
public sealed class WheaEventLogReader : IWheaErrorSource
{
    /// <summary>WHEA 提供程序名。</summary>
    public const string ProviderName = "Microsoft-Windows-WHEA-Logger";

    /// <summary>统计窗口：近 7 天。</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);

    /// <summary>查询节流间隔。WHEA 是慢变量，没有理由频繁查询。</summary>
    private static readonly TimeSpan Throttle = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 已纠正（硬件自行恢复）类事件 ID。
    /// <para>
    /// 17 / 19 / 47 是 WHEA-Logger 记录的"已纠正硬件错误"（可恢复机器检查）。
    /// **不在这个集合里的都按未纠正处理**是刻意选择：WHEA 的致命族事件 ID 远比
    /// 已纠正族多，把未知 ID 归到"可能有问题"一侧，对健康度而言是保守方向。
    /// 但为了避免误伤，未知 ID 会被单独记进 <c>[WHEA]</c> 日志以便事后核对。
    /// </para>
    /// </summary>
    private static readonly HashSet<int> CorrectedEventIds = [17, 19, 47];

    /// <summary>致命（未纠正）类事件 ID。</summary>
    private static readonly HashSet<int> FatalEventIds = [1, 18, 20, 46, 48];

    private readonly object _gate = new();
    private readonly List<int> _unknownEventIds = [];

    private WheaErrorCounts _cached = WheaErrorCounts.Unavailable("尚未读取");
    private DateTime _lastReadUtc = DateTime.MinValue;
    private (int Corrected, int Fatal)? _lastLoggedCounts;

    /// <summary>最近一次成功读取时的原始事件 ID 分布（取证用）。</summary>
    public IReadOnlyList<int> LastEventIds { get; private set; } = [];

    /// <inheritdoc />
    public WheaErrorCounts Read()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (_lastReadUtc != DateTime.MinValue && now - _lastReadUtc < Throttle)
                return _cached;

            _lastReadUtc = now;
            _cached = Query();
            return _cached;
        }
    }

    private WheaErrorCounts Query()
    {
        var milliseconds = (long)Window.TotalMilliseconds;

        // timediff() 由事件日志服务端求值，不会把整个 System 日志拉回本地 ——
        // 直接枚举 EventLog.Entries 在日志量大的机器上会卡住 UI 线程数秒。
        var xpath =
            $"*[System[Provider[@Name='{ProviderName}'] and TimeCreated[timediff(@SystemTime) <= {milliseconds}]]]";

        try
        {
            var query = new EventLogQuery("System", PathType.LogName, xpath);
            using var reader = new EventLogReader(query);

            var corrected = 0;
            var fatal = 0;
            var cpuRelated = 0;
            var allClassified = true;
            var ids = new List<int>();
            _unknownEventIds.Clear();

            for (var record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
            {
                using (record)
                {
                    var id = record.Id;
                    ids.Add(id);

                    if (CorrectedEventIds.Contains(id))
                        corrected++;
                    else if (FatalEventIds.Contains(id))
                        fatal++;
                    else
                        _unknownEventIds.Add(id);

                    // 来源判定：WHEA 的 ErrorSource 在事件数据段里是字符串
                    // （Processor / Cache Hierarchy / Memory / PCI Express …）。
                    var source = TryReadSource(record);
                    if (source is null)
                        allClassified = false;
                    else if (IsProcessorSource(source))
                        cpuRelated++;
                }
            }

            LastEventIds = ids;

            // 只要有一条判不出来，就不给出"处理器相关条数" —— 宁可退回总数并在界面说明，
            // 也不要报一个系统性偏小的数（那等于把真实的 CPU 错误漏掉一部分）。
            var cpuField = allClassified ? cpuRelated : -1;

            var detail = BuildDetail(corrected, fatal, cpuField, ids);
            var result = new WheaErrorCounts(corrected, fatal, cpuField, true, detail);

            LogOnce(result, ids);
            return result;
        }
        catch (Exception ex)
        {
            // 读不到必须与"读到 0 条"区分：前者返回 Available=false，界面显示"未读取"。
            var reason = $"{ex.GetType().Name}: {ex.Message}";
            AppLog.Write($"[WHEA] 事件日志读取失败（{reason}）");

            LastEventIds = [];
            return WheaErrorCounts.Unavailable(reason);
        }
    }

    private static string? TryReadSource(EventRecord record)
    {
        try
        {
            foreach (var property in record.Properties)
            {
                if (property?.Value is string text && text.Length is > 2 and < 64)
                {
                    // WHEA 的来源字段是少量固定词，命中即返回，避免把事件描述误当来源。
                    if (text.Contains("Processor", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("Cache", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("Memory", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("PCI", StringComparison.OrdinalIgnoreCase))
                    {
                        return text;
                    }
                }
            }
        }
        catch
        {
            // 某些事件缺少取值器会抛异常；判不出来就走 allClassified=false 的退化路径。
        }

        return null;
    }

    private static bool IsProcessorSource(string source)
        => source.Contains("Processor", StringComparison.OrdinalIgnoreCase)
           || source.Contains("Cache", StringComparison.OrdinalIgnoreCase)
           || source.Contains("Machine Check", StringComparison.OrdinalIgnoreCase);

    private string BuildDetail(int corrected, int fatal, int cpuField, List<int> ids)
    {
        if (ids.Count == 0)
            return $"近 {Window.TotalDays:0} 天无 WHEA 硬件错误记录";

        var cpuText = cpuField >= 0 ? $"{cpuField} 条处理器相关" : "未能判定来源（按总数计）";
        var unknown = _unknownEventIds.Count > 0
            ? $"；另有 {_unknownEventIds.Count} 条未知事件 ID（{string.Join('/', _unknownEventIds.Distinct())}）"
            : string.Empty;

        return $"近 {Window.TotalDays:0} 天：已纠正 {corrected} 条、未纠正 {fatal} 条；{cpuText}{unknown}";
    }

    /// <summary>
    /// 只在**计数发生变化**时落日志：首次读到是一行，之后每新增一条硬件错误再落一行。
    /// 逐次刷屏没有意义（5 分钟一次的查询会把日志淹掉），完全不记又会让
    /// "什么时候开始出错的"无从追溯。
    /// </summary>
    private void LogOnce(WheaErrorCounts counts, List<int> ids)
    {
        if (_lastLoggedCounts is { } previous
            && previous.Corrected == counts.Corrected
            && previous.Fatal == counts.Fatal)
        {
            return;
        }

        _lastLoggedCounts = (counts.Corrected, counts.Fatal);

        var idText = ids.Count == 0 ? "无" : string.Join(",", ids.Distinct().OrderBy(i => i));
        AppLog.Write(
            $"[WHEA] 读取成功：已纠正={counts.Corrected} 未纠正={counts.Fatal} "
            + $"处理器相关={(counts.CpuRelated >= 0 ? counts.CpuRelated.ToString() : "未判定")} "
            + $"事件ID=[{idText}]（免提权即可读）");
    }
}
