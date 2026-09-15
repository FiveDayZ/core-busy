namespace CoreBusy.Core.Interfaces;

using CoreBusy.Core.Models;

/// <summary>CPU 静态信息服务。</summary>
public interface ICpuInfoService
{
    /// <summary>读取 CPU 名称、厂商、核心数与线程数。读取失败时抛出异常。</summary>
    Task<CpuInfo> GetCpuInfoAsync(CancellationToken cancellationToken = default);
}
