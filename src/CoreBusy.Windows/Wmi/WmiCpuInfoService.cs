namespace CoreBusy.Windows.Wmi;

using System.Management;
using System.Text.RegularExpressions;
using CoreBusy.Core.Interfaces;
using CoreBusy.Core.Models;
using CoreBusy.Windows.Topology;

/// <summary>
/// 基于 WMI（Win32_Processor）的 CPU 静态信息读取实现。
/// 支持多路 CPU 聚合（核心数/线程数累加，名称取首个实例）。
/// v1.10.2：名称/厂商/基频经 CPUID 静态信息交叉校验——这些参数直接问 CPU 芯片
/// （品牌串、Intel 16H 标称基频），不经固件 SMBIOS/ACPI。物理核心数不在本层修正，
/// 单一口径在 <see cref="LogicalProcessorTopologyService"/> 的 CPUID 交叉校验里。
/// </summary>
public sealed class WmiCpuInfoService : ICpuInfoService
{
    public Task<CpuInfo> GetCpuInfoAsync(CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, SocketDesignation, MaxClockSpeed FROM Win32_Processor");

            string name = "Unknown CPU";
            string vendor = "Unknown";
            string socket = string.Empty;
            double baseClockGHz = double.NaN;
            int cores = 0;
            int threads = 0;
            bool first = true;

            foreach (var instance in searcher.Get().Cast<ManagementObject>())
            {
                if (first)
                {
                    name = instance["Name"]?.ToString()?.Trim() ?? name;
                    vendor = NormalizeVendor(instance["Manufacturer"]?.ToString());
                    socket = instance["SocketDesignation"]?.ToString()?.Trim() ?? string.Empty;
                    var maxMhz = Convert.ToDouble(instance["MaxClockSpeed"] ?? 0);
                    baseClockGHz = maxMhz > 0 ? maxMhz / 1000.0 : double.NaN;
                    first = false;
                }

                cores += Convert.ToInt32(instance["NumberOfCores"] ?? 0);
                threads += Convert.ToInt32(instance["NumberOfLogicalProcessors"] ?? 0);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (threads <= 0)
                throw new InvalidOperationException("WMI 未返回有效的逻辑处理器数量。");

            (name, vendor, baseClockGHz) = CorrectWithCpuId(name, vendor, baseClockGHz);

            return new CpuInfo
            {
                Name = name,
                Vendor = vendor,
                VendorKind = vendor.ToCpuVendor(),
                PhysicalCores = cores > 0 ? cores : threads,
                LogicalProcessors = threads,
                Socket = socket,
                BaseClockGHz = baseClockGHz,
            };
        }, cancellationToken);

    /// <summary>
    /// CPUID 静态信息交叉校验（v1.10.2）：
    /// - 厂商：CPUID 供应商串为芯片自报，WMI 缺失或矛盾时以其为准；
    /// - 名称：WMI 缺失时用 CPUID 品牌串兜底，两者都在但规范化后不一致时仅取证不改写
    ///   （保持与任务管理器一致的展示口径）；
    /// - 基频：CPUID.16H（Intel 芯片自报非睿频比）→ 品牌串 "x.xx GHz"（AMD 口径）→ WMI MaxClockSpeed。
    ///   注意 CPUID.16H 报的是**按当前平台功耗配置**自报的非睿频比，不等于 SKU 标称基频：
    ///   实测某 i3-N305 整机（PL1=10W / PL2=15W，见 MSR 0x610）固件与芯片一致报 1.0 GHz，
    ///   而 Intel 数据手册的 1.8 GHz 是 15W 档 HFM——两者不矛盾，别把自报值当"读错"改掉。
    /// </summary>
    private static (string Name, string Vendor, double BaseClockGHz) CorrectWithCpuId(
        string wmiName, string wmiVendor, double wmiBaseClockGHz)
    {
        if (!CpuIdTopologyProbe.TryReadStaticInfo(out var si, out var probeReason))
        {
            TopologyLog.Write($"[TOPOLOGY] cpuid static info unavailable ({probeReason}), keep WMI values");
            return (wmiName, wmiVendor, wmiBaseClockGHz);
        }

        TopologyLog.Write(
            $"[TOPOLOGY] cpuid static: vendor='{si.VendorId}' brand='{si.BrandString}' "
            + $"intelBaseMhz={(si.IntelBaseFrequencyMhz.HasValue ? si.IntelBaseFrequencyMhz.Value.ToString() : "-")} "
            + $"intel16Raw={si.Intel16RawMhz} bus={(si.IntelBusMhz.HasValue ? si.IntelBusMhz.Value.ToString() : "-")} "
            + $"hybrid={si.IntelHybrid}");

        // ---- 厂商 ----
        var vendor = wmiVendor;
        var cpuidVendor = si.VendorId switch
        {
            "GenuineIntel" => "Intel",
            "AuthenticAMD" => "AMD",
            _ => "Unknown",
        };
        if (cpuidVendor != "Unknown")
        {
            if (string.IsNullOrEmpty(vendor) || vendor == "Unknown")
            {
                vendor = cpuidVendor;
                TopologyLog.Write($"[TOPOLOGY] vendor from cpuid: {cpuidVendor}");
            }
            else if (!string.Equals(vendor, cpuidVendor, StringComparison.OrdinalIgnoreCase))
            {
                TopologyLog.Write($"[TOPOLOGY] vendor mismatch: wmi='{vendor}' cpuid='{cpuidVendor}' -> using cpuid");
                vendor = cpuidVendor;
            }
        }

        // ---- 名称 ----
        var name = wmiName;
        var brand = si.BrandString;
        if (!string.IsNullOrWhiteSpace(brand))
        {
            if (string.IsNullOrWhiteSpace(name) || name == "Unknown CPU")
            {
                name = brand;
                TopologyLog.Write("[TOPOLOGY] cpu name from cpuid brand string");
            }
            else if (NormalizeName(name) != NormalizeName(brand))
            {
                TopologyLog.Write($"[TOPOLOGY] cpu name mismatch: wmi='{name}' cpuid='{brand}' (keep wmi)");
            }
        }

        // ---- 基频 ----
        double? corrected = null;
        var source = "wmi";
        if (si.IntelBaseFrequencyMhz is { } mhz)
        {
            corrected = mhz / 1000.0;
            source = "cpuid.16h";
        }
        else if (TryParseBrandClockGhz(brand) is { } ghz)
        {
            corrected = ghz;
            source = "cpuid.brand";
        }

        var baseClock = wmiBaseClockGHz;
        if (corrected is { } value && value is > 0.2 and < 6.5)
        {
            var wmiDiffers = double.IsNaN(baseClock) || Math.Abs(baseClock - value) > value * 0.05;
            if (wmiDiffers)
            {
                TopologyLog.Write(
                    $"[TOPOLOGY] base clock: wmi={(double.IsNaN(baseClock) ? "-" : baseClock.ToString("0.00"))} GHz "
                    + $"-> {value:0.00} GHz ({source})");
            }

            baseClock = value;
        }

        // 最终取值取证：无论走了哪条来源，每次启动都留一行可审计的结论。
        TopologyLog.Write(
            $"[TOPOLOGY] static resolved: name='{name}' vendor='{vendor}' "
            + $"baseClock={(double.IsNaN(baseClock) ? "-" : baseClock.ToString("0.00"))} GHz (source={source})");

        return (name, vendor, baseClock);
    }

    /// <summary>名称规范化比较：去掉商标符号与空白后按字母数字比对（"Intel(R) Core(TM) i3-N305" ≈ "Intel Core i3-N305"）。</summary>
    private static string NormalizeName(string text)
        => new string(text.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    /// <summary>从品牌串解析标称基频（AMD 惯例：品牌串末尾含 "3.30 GHz"），合理域 0.4..5.5 GHz。</summary>
    private static double? TryParseBrandClockGhz(string? brand)
    {
        if (string.IsNullOrWhiteSpace(brand))
            return null;

        var match = Regex.Match(brand, @"(\d+(?:\.\d+)?)\s*GHz", RegexOptions.IgnoreCase);
        if (!match.Success
            || !double.TryParse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var ghz))
            return null;

        return ghz is >= 0.4 and <= 5.5 ? ghz : null;
    }

    private static string NormalizeVendor(string? manufacturer) => manufacturer?.Trim() switch
    {
        "GenuineIntel" => "Intel",
        "AuthenticAMD" => "AMD",
        null or "" => "Unknown",
        var other => other,
    };
}
