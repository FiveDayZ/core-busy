namespace CoreBusy.App.ViewModels.Dashboard;

using CoreBusy.App.Infrastructure;
using CoreBusy.App.Services;

/// <summary>
/// 每日能耗账本：把逐帧的功耗读数归并到**自然日**，供「能耗日历」按月呈现。
/// <para>
/// 与 <see cref="CumulativeEnergyTracker"/> 的分工：
/// 后者回答「本次运行以来用了多少电」，前者回答「历史上每一天用了多少电」。
/// 两者吃同一份读数样本（见 DashboardViewModel.AccumulateSample），因此不会打架。
/// </para>
/// <para>
/// <b>口径（v1.21.0 变更）</b>：账本记的是**整机估算功率**的累计量，与状态栏「整机」那一路同源
/// （逐部件模型：CPU 实测 + 显卡 / 内存 / 硬盘 / 风扇 / 主板，见
/// <see cref="CoreBusy.Core.Energy.SystemPowerEstimator"/>）。
/// v1.0 – v1.20.1 记的是 CPU 封装功耗 —— 同一个"日历深浅"，换口径前后差约两倍，
/// 所以 <see cref="EnergyHistoryStore"/> 带口径标识并在不符时归档旧文件，绝不混画。
/// </para>
/// <para>
/// **读数可靠性决定了这套数据的边界**。整机估算的最上游仍是封装功耗，
/// 它来自 LibreHardwareMonitor（Ring0 驱动），非管理员会话、设置里关掉传感器、
/// 性能模式暂停轮询时都会读不到，此时整机估算也一并失效。所以本账本
/// ① 只记录读数有效的样本；② 每天单独记录 Seconds（有效观测时长），
/// 让「该日平均功率」之类的派生量有诚实的分母。
/// </para>
/// <para>
/// 另一个必须说清的边界：**它统计的是整机功率，但不是"整天耗电"**。
/// 程序没运行的时间完全看不到，显示器、外设、以及整机模型未覆盖的部件一概不在口径内；
/// 而且它是**估算**，不是从墙上量出来的（本机确认无整机功率来源）。
/// 日历上某天空白，意思是「那天没观测到」，不是「那天没耗电」——
/// 这一点在 UI 的悬浮说明里必须写明，否则就是把局部观测当成全天事实。
/// </para>
/// </summary>
public sealed class DailyEnergyLedger
{
    /// <summary>自动落盘的最小间隔（秒）。逐帧写盘是无谓的 IO。</summary>
    private const double SaveIntervalSeconds = 30.0;

    private readonly Dictionary<DateOnly, DayEnergyRecord> _days;

    /// <summary>最近一次记账所属的自然日，用于检测跨零点。</summary>
    private DateOnly _currentDay;

    private DateTime _lastSave = DateTime.Now;
    private bool _dirty;

    public DailyEnergyLedger()
    {
        _days = EnergyHistoryStore.Load().Days;
        _currentDay = DateOnly.FromDateTime(DateTime.Now);
    }

    /// <summary>某天的累计能耗（焦耳）；无记录返回 0。</summary>
    public double JoulesOf(DateOnly day)
        => _days.TryGetValue(day, out var record) ? record.Joules : 0.0;

    /// <summary>某天的有效观测秒数；无记录返回 0。</summary>
    public double SecondsOf(DateOnly day)
        => _days.TryGetValue(day, out var record) ? record.Seconds : 0.0;

    /// <summary>某天是否有任何功耗读数。</summary>
    public bool HasRecord(DateOnly day) => _days.ContainsKey(day);

    /// <summary>
    /// 记一次有效读数，并返回**自然日是否刚发生翻转**（界面据此立刻重算网格）。
    /// </summary>
    /// <param name="powerWatt">
    /// **整机估算功率**（W，逐部件模型的和），必须已通过合理性校验。
    /// 非有限值（估算不可用时上层传 NaN）会被拒收 —— 与"真的 0 W"是两回事。
    /// </param>
    /// <param name="deltaSeconds">本次积分代表的墙钟时长（秒）。</param>
    /// <param name="now">本次采样时刻（本地时间，用于判定自然日归属）。</param>
    public bool Record(double powerWatt, double deltaSeconds, DateTime now)
    {
        if (deltaSeconds <= 0 || !double.IsFinite(powerWatt) || powerWatt < 0)
            return false;

        var end = now;
        var today = DateOnly.FromDateTime(end);
        var rolled = today != _currentDay;
        _currentDay = today;

        var start = end.AddSeconds(-deltaSeconds);
        var startDay = DateOnly.FromDateTime(start);

        if (startDay == today)
        {
            Add(today, powerWatt * deltaSeconds, deltaSeconds);
        }
        else
        {
            // 跨零点：这一段能耗横跨两个自然日，按日边界比例切分。
            // 不做切分的话，睡前那段会被整体记到第二天头上（或反之），
            // 长期运行的用户会发现「总在某天突然多出一块」。
            var boundary = today.ToDateTime(TimeOnly.MinValue);
            var tail = Math.Clamp((end - boundary).TotalSeconds, 0, deltaSeconds);
            var head = deltaSeconds - tail;

            Add(startDay, powerWatt * head, head);
            Add(today, powerWatt * tail, tail);
            rolled = true; // 跨日军必须重算：昨天的格子也要更新。
        }

        return rolled;
    }

    /// <summary>把一次记账并入某天。</summary>
    private void Add(DateOnly day, double joules, double seconds)
    {
        if (!_days.TryGetValue(day, out var record))
        {
            record = new DayEnergyRecord();
            _days[day] = record;

            // 超出保留期时裁掉最旧的一天。字典装载是从磁盘读来的、已裁剪过的，
            // 这里只需处理长期运行不断新增的情况。
            if (_days.Count > EnergyHistoryStore.RetentionDays)
            {
                var oldest = _days.Keys.Min();
                _days.Remove(oldest);
            }
        }

        record.Joules += joules;
        record.Seconds += seconds;
        _dirty = true;
    }

    /// <summary>
    /// 按节流间隔落盘。<paramref name="force"/> 用于退出等场景立刻保存。
    /// </summary>
    public void SaveIfNeeded(bool force = false)
    {
        if (!_dirty)
            return;

        var now = DateTime.Now;
        if (!force && (now - _lastSave).TotalSeconds < SaveIntervalSeconds)
            return;

        _lastSave = now;
        _dirty = false;
        EnergyHistoryStore.Save(_days);
    }
}
