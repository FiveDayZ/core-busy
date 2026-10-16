namespace CoreBusy.App.Services;

using CoreBusy.Core.Interfaces;
using CoreBusy.Core.Models;

/// <summary>正式采集失败时明确降级，不生成 CPU 型号、负载或功率。</summary>
public sealed class UnavailableCpuMonitorService(string reason) : ICpuMonitorService
{
    public string UnavailableReason { get; } = reason;
    public TimeSpan SampleInterval { get; set; } = TimeSpan.FromSeconds(1);
    public bool SensorsEnabled { get; set; }
    public CpuInfo GetCpuInfo() => new() { Name = "CPU 采集不可用", Vendor = "Unknown" };
    public CpuSnapshot GetSnapshot() => CpuSnapshot.Unavailable;
    public IReadOnlyList<CpuCoreSnapshot> GetCoreSnapshots() => [];
    public IReadOnlyList<CpuCoreGroup> GetCoreGroups() => [];
}
