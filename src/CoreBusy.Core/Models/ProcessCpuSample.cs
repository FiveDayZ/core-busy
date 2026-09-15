namespace CoreBusy.Core.Models;

/// <summary>单个进程的 CPU 占用样本。</summary>
public record ProcessCpuSample(string Name, double CpuPercent);
