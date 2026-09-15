namespace CoreBusy.Core.Models;

/// <summary>
/// 单块硬盘的温度读数（v1.16.2）。带型号名是刻意的：状态栏只能挤下一个数字，
/// 但"是哪块盘这么热"是排查必需的信息，悬浮详情里要能逐块列出来。
/// </summary>
public sealed record DriveTemperatureReading
{
    /// <summary>盘型号（取自 LHM 的 Storage 硬件名，通常即型号字符串）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>该盘温度（℃）。取不到读数的盘不进列表。</summary>
    public double TemperatureC { get; init; }
}
