namespace CoreBusy.Core.Models;

/// <summary>
/// CPU 厂商（规范 §49）。决定品牌主题、核心分区口径与显示命名。
/// </summary>
public enum CpuVendor
{
    /// <summary>未知厂商（加载 Neutral 主题、单段 CPU Core 分区）。</summary>
    Unknown = 0,

    /// <summary>Intel（混合架构区分 P-Core / E-Core）。</summary>
    Intel = 1,

    /// <summary>AMD（同构核心，按 CPU Core / CCD 分区）。</summary>
    Amd = 2,
}

/// <summary>厂商字符串（WMI Manufacturer / ProcessorId 口径）→ <see cref="CpuVendor"/>。</summary>
public static class CpuVendorExtensions
{
    /// <summary>
    /// 判定厂商：兼容 "GenuineIntel" / "AuthenticAMD" 与 "Intel" / "AMD" 两种写法。
    /// 无法判定时返回 <see cref="CpuVendor.Unknown"/>。
    /// </summary>
    public static CpuVendor ToCpuVendor(this string? vendor)
    {
        if (string.IsNullOrWhiteSpace(vendor))
            return CpuVendor.Unknown;

        if (vendor.Contains("intel", StringComparison.OrdinalIgnoreCase))
            return CpuVendor.Intel;

        if (vendor.Contains("amd", StringComparison.OrdinalIgnoreCase))
            return CpuVendor.Amd;

        return CpuVendor.Unknown;
    }
}
