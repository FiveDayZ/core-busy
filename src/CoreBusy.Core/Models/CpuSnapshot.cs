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
}
