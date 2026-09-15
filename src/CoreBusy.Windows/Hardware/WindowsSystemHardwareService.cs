namespace CoreBusy.Windows.Hardware;

using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using CoreBusy.Core.Interfaces;
using CoreBusy.Core.Models;
using CoreBusy.Windows.Topology;
using Microsoft.Win32;

/// <summary>
/// 整机硬件状态采集（物理内存 / 显卡 / 系统盘）。
/// <para>
/// 三个数据源各自独立，任一失败只影响该项（对应 Has* = false），不会连带整条链路：
/// 物理内存走 <c>GlobalMemoryStatusEx</c>（内核直读性能计数器，微秒级，可每帧调用）；
/// 显卡型号走 WMI <c>Win32_VideoController</c>，显存走显示类驱动键
/// <c>HardwareInformation.qwMemorySize</c>（64 位，>4 GB 不会像 WMI AdapterRAM 那样溢出）。
/// </para>
/// <para>
/// v1.16.0 显卡口径修正：多卡（核显 + 独显）并存时**独显优先**，且每块卡的显存
/// 只从**自己**的驱动键读取——旧版「按名称匹配失败就取全部子键最大值」的全局兜底
/// 会把同一份显存发给每张卡，排序被污染，独显机反而显示核显（v1.15.x 实测）。
/// 显存三级回退：注册表（按 PNP 硬件 ID 精确配对）→ 注册表（DriverDesc 包含匹配）
/// → WMI AdapterRAM（uint32 字段，≥4 GB 必然溢出错报，只采信 <3.5 GiB 的小值）。
/// </para>
/// <para>
/// v1.16.0 磁盘口径修正：主显**物理磁盘总容量**（Win32_DiskDrive 固定硬盘求和），
/// 不再按盘符显示剩余空间；已用率按固定逻辑卷聚合（资源管理器口径），
/// 卷口径可用空间与磁盘块数进悬浮详情。
/// </para>
/// <para>
/// 缓存策略：显卡是慢变量，构造时取一次；内存量每帧重读、磁盘按
/// <see cref="DriveRefreshSeconds"/> 节流重读。
/// </para>
/// </summary>
public sealed class WindowsSystemHardwareService : ISystemHardwareService
{
    /// <summary>显示类驱动注册表根（{4d36e968-…} 是 Display 类 GUID）。</summary>
    private const string DisplayClassKeyPath =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>系统盘可用空间的重新读取间隔（秒）。盘容量变化慢，无需每帧查询。</summary>
    private const double DriveRefreshSeconds = 30.0;

    private const double BytesPerGb = 1024.0 * 1024.0 * 1024.0;

    private readonly Stopwatch _driveClock = Stopwatch.StartNew();

    private readonly string _gpuName;
    private readonly double? _gpuMemoryGb;

    private int _driveCount;
    private double _driveTotalGb;
    private double _driveVolumeFreeGb;
    private double _driveVolumeUsedPercent;
    private bool _hasDrive;

    public WindowsSystemHardwareService()
    {
        (_gpuName, _gpuMemoryGb) = ProbeGpu();
        RefreshDrive();

        TopologyLog.Write(
            $"[SYSINFO] gpu='{_gpuName}' vram={Fmt(_gpuMemoryGb)} "
            + $"disks={_driveCount} total={_driveTotalGb:0.#}GB "
            + $"volumeUsed={_driveVolumeUsedPercent:0.#}% volumeFree={_driveVolumeFreeGb:0.#}GB");
    }

    /// <inheritdoc />
    public SystemHardwareInfo Read()
    {
        if (_driveClock.Elapsed.TotalSeconds >= DriveRefreshSeconds)
            RefreshDrive();

        var hasMemory = TryReadMemory(out var usedGb, out var totalGb);

        return new SystemHardwareInfo(
            hasMemory,
            usedGb,
            totalGb,
            _gpuName.Length > 0,
            _gpuName,
            _gpuMemoryGb,
            _hasDrive,
            _driveCount,
            _driveTotalGb,
            _driveVolumeFreeGb,
            _driveVolumeUsedPercent);
    }

    // ---------------------------------------------------------------- 物理内存

    private static bool TryReadMemory(out double usedGb, out double totalGb)
    {
        usedGb = 0;
        totalGb = 0;

        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0)
                return false;

            totalGb = status.TotalPhys / BytesPerGb;
            var availableGb = status.AvailPhys / BytesPerGb;

            // 已用 = 总量 - 可用（与任务管理器的「使用中」同口径）。
            usedGb = Math.Max(0, totalGb - availableGb);
            return true;
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    // ---------------------------------------------------------------- 显卡

    /// <summary>
    /// 探测主显卡：型号取 WMI。多卡（核显 + 独显）并存时独显优先、其次显存大者
    /// （状态栏只有一个显卡位，用户关心的是独显）。显存只采信「这块卡自己」的读数，
    /// 三级回退见类注释。
    /// </summary>
    private static (string Name, double? MemoryGb) ProbeGpu()
    {
        try
        {
            var registryCards = ReadDisplayRegistryCards();
            var candidates = new List<(string Name, double? MemoryGb, bool Discrete, int Order)>();

            var order = 0;
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID, AdapterRAM FROM Win32_VideoController");
            foreach (var item in searcher.Get())
            {
                using var device = (ManagementObject)item;
                var raw = (device["Name"] as string ?? string.Empty).Trim();
                if (raw.Length == 0 || raw.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase))
                    continue; // 无厂商驱动时的兜底适配器，不反映真实硬件。

                order++;
                var shortName = ShortenGpuName(raw);
                var pnp = (device["PNPDeviceID"] as string ?? string.Empty).Trim();
                var adapterRam = ToNullableUInt64(device["AdapterRAM"]);
                var memory = ProbeGpuMemoryGb(raw, pnp, adapterRam, registryCards);
                candidates.Add((shortName, memory, IsDiscreteGpu(shortName), order));
            }

            if (candidates.Count == 0)
                return (string.Empty, null);

            var best = candidates
                .OrderByDescending(c => c.Discrete)
                .ThenByDescending(c => c.MemoryGb ?? 0)
                .ThenBy(c => c.Order)
                .First();
            return (best.Name, best.MemoryGb);
        }
        catch
        {
            return (string.Empty, null);
        }
    }

    /// <summary>显示类驱动键里的一块适配器（驱动键子键 0000/0001… 与 WMI 卡的桥接数据）。</summary>
    private sealed record DisplayRegistryCard(string Desc, string MatchingId, long? MemoryBytes);

    /// <summary>一次性读出显示类驱动键的全部适配器（DriverDesc / MatchingDeviceId / qwMemorySize）。</summary>
    private static List<DisplayRegistryCard> ReadDisplayRegistryCards()
    {
        var cards = new List<DisplayRegistryCard>();
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(DisplayClassKeyPath);
            if (classKey is null)
                return cards;

            foreach (var subName in classKey.GetSubKeyNames())
            {
                if (subName.Length != 4 || !subName.All(char.IsDigit))
                    continue;

                using var cardKey = classKey.OpenSubKey(subName);
                if (cardKey is null)
                    continue;

                var desc = Normalize(cardKey.GetValue("DriverDesc") as string ?? string.Empty);
                if (desc.Length == 0)
                    continue;

                var matching = (cardKey.GetValue("MatchingDeviceId") as string ?? string.Empty)
                    .Trim()
                    .ToUpperInvariant();
                var bytes = ReadQword(cardKey.OpenSubKey("HardwareInformation")?.GetValue("qwMemorySize"));
                cards.Add(new DisplayRegistryCard(desc, matching, bytes));
            }
        }
        catch
        {
            // 注册表不可读时 cards 为空，显存回退 WMI AdapterRAM。
        }

        return cards;
    }

    /// <summary>
    /// 读单块显卡的专用显存容量（三级回退，见类注释）：
    /// ① 注册表按 PNP 硬件 ID（VEN_xxxx&amp;DEV_xxxx）精确配对；
    /// ② 注册表按 DriverDesc ⊆ WMI 名称匹配；
    /// ③ WMI AdapterRAM —— uint32 字段，≥ 4 GB 必然溢出错报，只采信 &lt; 3.5 GiB 的读数
    ///    （多为核显 carve-out），大显存宁缺毋滥显示「未报告」。
    /// 旧版「匹配失败取全部子键最大值」的跨卡兜底已删除：它把同一份显存发给每张卡，
    /// 是独显机误显示核显的直接原因。
    /// </summary>
    private static double? ProbeGpuMemoryGb(
        string wmiName, string pnpDeviceId, ulong? adapterRam, List<DisplayRegistryCard> cards)
    {
        try
        {
            var hardwareId = ExtractHardwareId(pnpDeviceId);
            DisplayRegistryCard? matched = null;

            if (hardwareId.Length > 0)
            {
                matched = cards.FirstOrDefault(c =>
                    ExtractHardwareId(c.MatchingId) == hardwareId && c.MemoryBytes is > 0);
            }

            matched ??= cards.FirstOrDefault(c =>
                c.MemoryBytes is > 0
                && Normalize(wmiName).Contains(c.Desc, StringComparison.OrdinalIgnoreCase));

            if (matched?.MemoryBytes is { } regBytes && regBytes > 0)
                return regBytes / BytesPerGb;

            return adapterRam is > 0 && adapterRam.Value < 3.5 * BytesPerGb
                ? adapterRam.Value / BytesPerGb
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>PNP 设备 ID → "VEN_xxxx&amp;DEV_xxxx"（注册表 MatchingDeviceId 与 WMI 配对的键）。</summary>
    private static string ExtractHardwareId(string pnpDeviceId)
    {
        var match = Regex.Match(
            pnpDeviceId ?? string.Empty,
            @"VEN_[0-9A-F]{4}&DEV_[0-9A-F]{4}",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Value.ToUpperInvariant() : string.Empty;
    }

    /// <summary>是否独显产品线（核显不匹配：UHD / HD / Iris / 无 RX 的 Radeon / 680M 这类）。</summary>
    private static bool IsDiscreteGpu(string shortName)
    {
        foreach (var keyword in new[] { "GeForce", "RTX", "GTX", "Quadro", "RX ", "Arc " })
        {
            if (shortName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>WMI 属性装箱值 → ulong（AdapterRAM/Size 等均为无符号整型属性）。</summary>
    private static ulong? ToNullableUInt64(object? value) => value switch
    {
        uint asUint when asUint > 0 => asUint,
        ulong asUlong when asUlong > 0 => asUlong,
        _ => null,
    };

    /// <summary>qwMemorySize 可能是 REG_QWORD（long）或 REG_BINARY（8 字节小端）。</summary>
    private static long? ReadQword(object? value) => value switch
    {
        long asLong when asLong > 0 => asLong,
        byte[] { Length: >= 8 } bytes => BitConverter.ToInt64(bytes, 0),
        _ => null,
    };

    /// <summary>
    /// 显卡名压缩为可放进状态栏的短名：去掉 (R)/(TM)/(C) 商标标记与厂商前缀
    /// （"NVIDIA GeForce RTX 3060" → "RTX 3060"，"Intel(R) UHD Graphics" → "UHD Graphics"）。
    /// </summary>
    private static string ShortenGpuName(string raw)
    {
        var name = Normalize(raw);

        // NVIDIA 前缀恒可剥离："NVIDIA GeForce RTX 3060" → "RTX 3060"。
        foreach (var prefix in new[] { "NVIDIA GeForce ", "NVIDIA " })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[prefix.Length..].Trim();
                break;
            }
        }

        // 第一级（严格）：只有能剥出型号数字时才剥掉 "AMD Radeon "，
        // "AMD Radeon RX 6600M" → "RX 6600M"；核显 "AMD Radeon Graphics" 无型号数字，
        // 留给下一级只剥厂商词 —— 否则会把仅剩的品牌词也一并吃掉，退化成 "Graphics"。
        foreach (var prefix in new[] { "AMD Radeon " })
        {
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var rest = name[prefix.Length..].Trim();
            if (rest.Any(char.IsDigit))
                name = rest;
            break;
        }

        // 第二级（宽松）：剥厂商词后仍带品牌词或型号数字即可。
        // "AMD Radeon Graphics" → "Radeon Graphics"，"Intel UHD Graphics" → "UHD Graphics"。
        foreach (var prefix in new[] { "AMD ", "Intel " })
        {
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var rest = name[prefix.Length..].Trim();
            if (rest.Any(char.IsDigit) || HasBrandWord(rest))
                name = rest;
            break;
        }

        return name.Length > 0 ? name : Normalize(raw);
    }

    /// <summary>剩余词是否以显示品牌词开头（据此判断剥离厂商前缀后不会丢信息）。</summary>
    private static bool HasBrandWord(string value)
    {
        foreach (var brand in new[] { "Radeon", "UHD", "HD", "Iris", "Vega", "Arc", "Graphics" })
        {
            if (value.StartsWith(brand, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>去商标标记与冗余空白。</summary>
    private static string Normalize(string value)
    {
        var text = value
            .Replace("(R)", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("(TM)", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("(C)", " ", StringComparison.OrdinalIgnoreCase);

        while (text.Contains("  ", StringComparison.Ordinal))
            text = text.Replace("  ", " ", StringComparison.Ordinal);

        return text.Trim();
    }

    // ---------------------------------------------------------------- 系统盘

    private void RefreshDrive()
    {
        _driveClock.Restart();

        try
        {
            // 物理盘总容量：Win32_DiskDrive 固定硬盘求和（内置 NVMe/SATA 与 USB 固定盘均计入）。
            // 可移动介质（U 盘/读卡器）不计入，避免插拔造成总量跳变。
            var totalBytes = 0L;
            var diskCount = 0;
            using (var diskSearcher = new ManagementObjectSearcher("SELECT Size, MediaType FROM Win32_DiskDrive"))
            {
                foreach (var item in diskSearcher.Get())
                {
                    using var disk = (ManagementObject)item;
                    var media = disk["MediaType"] as string ?? string.Empty;
                    if (!media.StartsWith("Fixed", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var size = ToNullableUInt64(disk["Size"]) ?? 0;
                    if (size > 0)
                    {
                        totalBytes += (long)size;
                        diskCount++;
                    }
                }
            }

            // 占用率：固定逻辑卷聚合（资源管理器口径），不受分区表/未分配空间干扰。
            var volumeTotal = 0L;
            var volumeFree = 0L;
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                        continue;

                    volumeTotal += drive.TotalSize;
                    volumeFree += drive.AvailableFreeSpace;
                }
                catch
                {
                    // 单个卷不可读（BitLocker 未解锁等）跳过，不影响聚合。
                }
            }

            if (totalBytes <= 0 && volumeTotal <= 0)
            {
                _hasDrive = false;
                return;
            }

            _driveCount = diskCount;
            _driveTotalGb = totalBytes / BytesPerGb;
            _driveVolumeFreeGb = volumeFree / BytesPerGb;
            _driveVolumeUsedPercent = volumeTotal > 0
                ? Math.Clamp((volumeTotal - volumeFree) / (double)volumeTotal * 100.0, 0, 100)
                : 0;
            _hasDrive = true;
        }
        catch
        {
            _hasDrive = false;
        }
    }

    private static string Fmt(double? value) => value.HasValue ? $"{value.Value:0.#}" : "-";
}
