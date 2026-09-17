namespace CoreBusy.Core.Models;

/// <summary>CPU 整体快照（一次采样）。</summary>
public sealed record CpuSnapshot
{
    /// <summary>CPU 总使用率（0-100）。</summary>
    public double TotalUsagePercent { get; init; }

    /// <summary>封装温度（℃）。</summary>
    public double PackageTemperatureC { get; init; }

    /// <summary>观测期内的峰值温度（℃）。</summary>
    public double PeakTemperatureC { get; init; }

    /// <summary>峰值温度出现时间。</summary>
    public DateTime PeakTemperatureTime { get; init; }

    /// <summary>封装功耗（W）。</summary>
    public double PackagePowerW { get; init; }

    /// <summary>全核平均频率（GHz）。</summary>
    public double AverageFrequencyGHz { get; init; }

    /// <summary>风扇转速（RPM）。</summary>
    public double FanRpm { get; init; }

    /// <summary>GPU 风扇转速（RPM）。不可用时为 NaN（v1.16.3）。</summary>
    public double GpuFanRpm { get; init; } = double.NaN;

    /// <summary>
    /// 风扇读数的来源说明，或读不到时的原因（v1.16.3）。
    /// <para>
    /// 笔记本风扇既不在 LHM 的采集范围（ASUS 机型整棵树无 Fan 传感器），又受 ASUS WMI 权限限制，
    /// 界面若只留一个 "-" 就无法区分"机器没有风扇传感器"与"没提权"——沿用 v1.16.1 的做法，
    /// 把原因一并带到界面（Tooltip）与 debug.log（<c>[FAN]</c> 行）。
    /// </para>
    /// </summary>
    public string FanDetail { get; init; } = string.Empty;

    /// <summary>主要负载进程 TOP5。</summary>
    public IReadOnlyList<ProcessLoadSample> TopProcesses { get; init; } = [];

    /// <summary>主显卡核心温度（℃）。传感器缺失/关闭时为 NaN，UI 显示 "-"（v1.16.0）。</summary>
    public double GpuTemperatureC { get; init; } = double.NaN;

    /// <summary>主显卡核心占用率（0-100）。不可用时为 NaN（v1.16.0）。</summary>
    public double GpuUtilizationPercent { get; init; } = double.NaN;

    /// <summary>硬盘最高温度（℃，多块盘取最热）。不可用时为 NaN（v1.16.0）。</summary>
    public double DriveTemperatureC { get; init; } = double.NaN;

    /// <summary>逐块盘的盘温（v1.16.2）。空列表 = 读不到任何盘温。</summary>
    public IReadOnlyList<DriveTemperatureReading> DriveTemperatures { get; init; } = [];

    /// <summary>
    /// **全部独显**的实测功率之和（W，v1.20.1）。NaN = 没有独显功率传感器
    /// （核显、或驱动缺失），**不是 0 W** —— 0 W 是独显下电时的合法读数（Optimus 笔记本）。
    /// </summary>
    public double DiscreteGpuPowerW { get; init; } = double.NaN;

    /// <summary>
    /// 被判为**独显**的显卡名（v1.20.1）。空 = 未判别出独显（核显，或型号判别不出）。
    /// 整机功耗模型据此决定显卡项是"单独计"还是"已含在 CPU 封装内"。
    /// </summary>
    public IReadOnlyList<string> DiscreteGpuNames { get; init; } = [];

    /// <summary>正在转动的风扇数量（v1.20.1）。整机功耗模型按个数计风扇功耗。</summary>
    public int ActiveFanCount { get; init; }

    /// <summary>最忙那块盘的吞吐（MB/s，v1.20.1）。NaN = 读不到（模型按空闲计）。</summary>
    public double StorageThroughputMbps { get; init; } = double.NaN;

    /// <summary>最忙那块盘的忙率（%，v1.20.1）。NaN = 读不到。</summary>
    public double StorageBusyPercent { get; init; } = double.NaN;
}
