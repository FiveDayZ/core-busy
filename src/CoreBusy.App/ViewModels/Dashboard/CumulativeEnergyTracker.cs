namespace CoreBusy.App.ViewModels.Dashboard;

/// <summary>
/// 运行期累积能耗累加器：对 CPU 封装功耗做**时间加权积分**。
/// <para>
/// 定义：<c>能耗(J) = Σ(封装功耗W × Δt)</c>，与累积负载用的是同一套 Δt（墙钟差），
/// 因此刷新档位、性能模式覆盖、系统节流都不会让积分权重失真。
/// </para>
/// <para>
/// 与负载 <see cref="CumulativeLoadTracker"/> 的关键差别在于**数据源可靠性**：
/// 使用率来自 Performance Counter，永远可读；而封装功耗来自硬件传感器
/// （LibreHardwareMonitor，依赖 Ring0 内核驱动）——非管理员会话、传感器被设置关闭、
/// 性能模式下轮询暂停，都会让它变成 <see cref="double.NaN"/>。
/// </para>
/// <para>
/// 因此这里维护**独立于负载的第二个时间基准** <see cref="ObservedSeconds"/>：
/// 只有读数有效的时间才计入。它不是冗余记账，而是让下面两个量保持诚实的前提：
/// </para>
/// <list type="bullet">
///   <item>平均功率必须对有效时长归一。若拿运行时间去当分母，传感器断了 10 分钟
///         就会把均值稀释成一个虚构的偏小数 —— 界面看着正常、数据是错的。</item>
///   <item>能耗可以在 Tooltip 里报告「覆盖率」，让用户知道数字背后的观测窗口有多完整。</item>
/// </list>
/// <para>
/// 读数还按 <see cref="DefaultMaxPlausibleWatt"/> 做了合理性筛选：某些平台偶发返回单位错误
/// 的离谱读数，这类异常值若参与积分会永久污染累计量，故视为无效样本跳过。
/// 上限可由构造参数覆盖（整机能耗的估算值量级更高，见 <see cref="_maxPlausibleWatt"/>）。
/// </para>
/// </summary>
public sealed class CumulativeEnergyTracker
{
    /// <summary>
    /// 功耗读数的合理性上限默认值（W）。消费级/工作站 CPU 封装功耗峰值一般在 300W 量级，
    /// 超出此值几乎必然是单位错误或传感器异常读数，参与积分会污染累计量。
    /// </summary>
    public const double DefaultMaxPlausibleWatt = 1000.0;

    /// <summary>
    /// 本实例的合理性上限（W）。做成实例字段而非常量，是因为 v1.20.0 起整机能耗也走本类积分
    /// （估算值 = k×封装 + b，量级必然高于封装本身，见 <see cref="CoreBusy.Core.Energy.SystemPowerEstimator"/>）。
    /// 若沿用 CPU 的 1000 W 闸，一份合法配置在满载时可能算出超过闸门的整机功率，
    /// 于是被静默丢样本 —— 表现为「整机能耗覆盖率莫名低于 CPU」，只看界面查不出来。
    /// </summary>
    private readonly double _maxPlausibleWatt;

    /// <param name="maxPlausibleWatt">
    /// 合理性上限（W）。非有限或非正时回落 <see cref="DefaultMaxPlausibleWatt"/>。
    /// </param>
    public CumulativeEnergyTracker(double maxPlausibleWatt = DefaultMaxPlausibleWatt)
    {
        _maxPlausibleWatt = double.IsFinite(maxPlausibleWatt) && maxPlausibleWatt > 0
            ? maxPlausibleWatt
            : DefaultMaxPlausibleWatt;
    }

    /// <summary>累计能耗（焦耳）。1 Wh = 3600 J。</summary>
    public double Joules { get; private set; }

    /// <summary>
    /// 功耗数据**有效**的累计时长（秒）——累积能耗的真实统计窗口。
    /// 与负载观测时长可能不同（传感器缺失的时间不计入）。
    /// </summary>
    public double ObservedSeconds { get; private set; }

    /// <summary>统计窗口内的平均封装功率（W）。尚无有效观测时返回 NaN，由 UI 降级显示。</summary>
    public double AverageWatt
        => ObservedSeconds > 0 ? Joules / ObservedSeconds : double.NaN;

    /// <summary>
    /// 最近一次 <see cref="Accumulate"/> 的读数是否有效。false 表示当前读不到功耗，
    /// 累计停滞 —— UI 据此把数值转弱色，避免呈现一个看起来仍在刷新的死数字。
    /// </summary>
    public bool HasLiveSample { get; private set; }

    /// <summary>
    /// 积分一次采样。
    /// </summary>
    /// <returns>读数是否有效。无效时不累积也不计时。</returns>
    public bool Accumulate(double powerWatt, double deltaSeconds)
    {
        // 功耗 0 W **不是有效读数**（v1.16.1）：AMD 在拿不到内核驱动时把 SMU 读数填 0，
        // 旧口径放它过去，等于把"读不到"当成"耗电为 0"积分进去 —— 结果是覆盖率虚高、
        // 平均功率被稀释、当日账本记下一格假的"几乎不耗电"。通电运行的 CPU 必然有功耗。
        var valid = deltaSeconds > 0
            && double.IsFinite(powerWatt)
            && powerWatt > 0
            && powerWatt <= _maxPlausibleWatt;

        HasLiveSample = valid;
        if (!valid)
            return false;

        Joules += powerWatt * deltaSeconds;
        ObservedSeconds += deltaSeconds;
        return true;
    }

    /// <summary>清空累计，重新开始统计。</summary>
    public void Reset()
    {
        Joules = 0;
        ObservedSeconds = 0;
        HasLiveSample = false;
    }

    /// <summary>
    /// 能耗的可读文案，单位按量级自动换档：
    /// 瓦时以下 → mWh，千瓦时以下 → Wh，更久 → kWh。
    /// <para>
    /// 主用 Wh 而非焦耳，是因为它落在日常经验的量级上（笔记本电池约 60 Wh），
    /// 而同一份能量用焦耳表示会是六位数起步的天文数字，读不出多少电的直觉。
    /// </para>
    /// </summary>
    public static string FormatEnergy(double joules)
    {
        var wh = joules / 3600.0;
        return wh switch
        {
            < 1 => $"{wh * 1000:0} mWh",
            < 100 => $"{wh:0.00} Wh",
            < 1000 => $"{wh:0.0} Wh",
            _ => $"{wh / 1000:0.00} kWh",
        };
    }
}
