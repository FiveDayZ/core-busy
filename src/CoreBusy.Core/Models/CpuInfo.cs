namespace CoreBusy.Core.Models;

/// <summary>CPU 静态信息（名称、厂商、核心与线程数量、CCD 数）。</summary>
public sealed record CpuInfo
{
    /// <summary>CPU 名称，例如 "Intel Core i5-14600KF"。</summary>
    public string Name { get; init; } = "Unknown CPU";

    /// <summary>厂商原始字符串（Intel / AMD / 未知）。</summary>
    public string Vendor { get; init; } = "Unknown";

    /// <summary>厂商枚举（规范 §49），由 <see cref="Vendor"/> 判定。</summary>
    public CpuVendor VendorKind { get; init; } = CpuVendor.Unknown;

    /// <summary>物理核心总数。</summary>
    public int PhysicalCores { get; init; }

    /// <summary>逻辑处理器（线程）总数。</summary>
    public int LogicalProcessors { get; init; }

    /// <summary>插槽标识（如 "LGA1700" / "AM5"），读取失败为空。</summary>
    public string Socket { get; init; } = string.Empty;

    /// <summary>
    /// 基础频率（GHz）：硬件自报的非睿频比——Intel 优先 CPUID.16H / MSR 0xCE，其余取 WMI MaxClockSpeed。
    /// 注意这是芯片按**当前平台功耗配置**自报的值，不等于 SKU 标称基频（Intel 只在默认 PL1 档公布 HFM）。
    /// </summary>
    public double BaseClockGHz { get; init; }

    /// <summary>性能核（P-Core）物理核心数（非混合平台为 0）。</summary>
    public int PerformanceCoreCount { get; init; }

    /// <summary>性能核线程数。</summary>
    public int PerformanceThreadCount { get; init; }

    /// <summary>能效核（E-Core）物理核心数（非混合平台为 0）。</summary>
    public int EfficiencyCoreCount { get; init; }

    /// <summary>能效核线程数。</summary>
    public int EfficiencyThreadCount { get; init; }

    /// <summary>
    /// CCD 数量（规范 §33）。1 = 单 CCD 或无法确认（此时 UI 只显示一段 CPU Core，不猜测）。
    /// </summary>
    public int CcdCount { get; init; } = 1;

    public override string ToString() =>
        $"{Name} | {Vendor} | {PhysicalCores}C/{LogicalProcessors}T | CCD={CcdCount}";
}
