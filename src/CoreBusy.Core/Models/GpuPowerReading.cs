namespace CoreBusy.Core.Models;

/// <summary>同一块独显的传感器功率（W）与占用率（%）；null 表示该项不可读。</summary>
public sealed record GpuPowerReading(string Name, double? PowerWatt, double? LoadPercent);
