namespace CoreBusy.Windows.Hardware;

using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using CoreBusy.Core.Energy;
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
/// v1.20.1 增补「型号信息」三项，供整机功耗的逐部件估算使用：内存模组的根数/单根容量/代际
/// （<c>Win32_PhysicalMemory</c> 的 SMBIOS 类型）、各盘的介质类型（<c>MSFT_PhysicalDisk</c>
/// 的 BusType/MediaType）、主卡是否独显（与状态栏选卡共用同一判别）。
/// 三项都在构造时各读一次：它们都是**不会热变化**的静态属性，每帧去查 WMI 只是白花开销。
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
    private readonly bool _gpuDiscrete;

    /// <summary>物理内存模组布局（v1.20.1）。构造时读一次：内存不会热插拔，不必每帧查 WMI。</summary>
    private readonly MemoryModuleLayout _memoryModules;

    /// <summary>各物理固定盘的介质类型（v1.20.1）。构造时读一次：介质类型不会变。</summary>
    private readonly IReadOnlyList<StorageMediaKind> _storageKinds;

    private int _driveCount;
    private double _driveTotalGb;
    private double _driveVolumeFreeGb;
    private double _driveVolumeUsedPercent;
    private bool _hasDrive;

    public WindowsSystemHardwareService()
    {
        (_gpuName, _gpuMemoryGb, _gpuDiscrete) = ProbeGpu();
        _memoryModules = ProbeMemoryModules();
        _storageKinds = ProbeStorageKinds();
        RefreshDrive();
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
            _driveVolumeUsedPercent)
        {
            MemoryModuleCount = _memoryModules.Count,
            MemoryModuleCapacityGb = _memoryModules.CapacityGb,
            MemoryGeneration = _memoryModules.Generation,
            StorageKinds = _storageKinds,
            GpuIsDiscrete = _gpuDiscrete,
        };
    }

    // ---------------------------------------------------------------- 功耗模型所需的型号信息

    /// <summary>
    /// 物理内存模组布局（根数 / 单根容量 / 代际）。
    /// <para>
    /// 走 WMI <c>Win32_PhysicalMemory</c>：其中的 <c>SMBIOSMemoryType</c> 是 SMBIOS 7.18.2 的
    /// 内存类型枚举（26 = DDR4、34 = DDR5、30 = LPDDR4、35 = LPDDR5），比旧字段
    /// <c>MemoryType</c> 可靠（后者在 Win10 以后常为 0，故仅作兜底）。
    /// </para>
    /// <para>
    /// 读不到时返回"未知"而不是猜一个：整机功耗模型对未知代际按 DDR4 系数处理，
    /// 影响的是 1.0/1.35 这类倍率，不会把"读不到"变成"读到了"。
    /// </para>
    /// </summary>
    private static MemoryModuleLayout ProbeMemoryModules()
    {
        try
        {
            var count = 0;
            var capacityBytes = 0L;
            var generation = MemoryGeneration.Unknown;

            using var searcher = new ManagementObjectSearcher(
                "SELECT Capacity, SMBIOSMemoryType, MemoryType FROM Win32_PhysicalMemory");

            foreach (var item in searcher.Get())
            {
                using var module = (ManagementObject)item;

                var bytes = ToNullableUInt64(module["Capacity"]) ?? 0;
                if (bytes == 0)
                    continue;

                count++;
                capacityBytes = Math.Max(capacityBytes, (long)bytes);

                var kind = ClassifyMemory((int)(ToNullableUInt64(module["SMBIOSMemoryType"]) ?? 0));
                if (kind == MemoryGeneration.Unknown)
                    kind = ClassifyMemory((int)(ToNullableUInt64(module["MemoryType"]) ?? 0));

                if (kind != MemoryGeneration.Unknown)
                    generation = kind;
            }

            return count == 0
                ? MemoryModuleLayout.Unknown
                : new MemoryModuleLayout(count, capacityBytes / BytesPerGb, generation);
        }
        catch
        {
            return MemoryModuleLayout.Unknown;
        }
    }

    /// <summary>
    /// SMBIOS Memory Device Type（7.18.2）→ 代际。**只映射能确定的值**：
    /// DDR2 及更早（18 等）与 DMI/SDRAM 一类一律返回 Unknown，交给模型按 DDR4 处理 ——
    /// 给十几年前的机器编一个代际系数，收益远小于"我并不知道"这个事实的价值。
    /// </summary>
    private static MemoryGeneration ClassifyMemory(int smbiosType) => smbiosType switch
    {
        24 => MemoryGeneration.Ddr3,
        26 => MemoryGeneration.Ddr4,
        34 => MemoryGeneration.Ddr5,
        29 => MemoryGeneration.LpDdr4, // LPDDR3 归入 LPDDR4 档（同为板载低压，系数接近）
        30 => MemoryGeneration.LpDdr4,
        35 => MemoryGeneration.LpDdr5,
        _ => MemoryGeneration.Unknown,
    };

    /// <summary>内存模组布局（根数 / 单根容量 GB / 代际）。未知用 <see cref="Unknown"/> 表达。</summary>
    private readonly record struct MemoryModuleLayout(int Count, double CapacityGb, MemoryGeneration Generation)
    {
        public static MemoryModuleLayout Unknown { get; } = new(0, 0, MemoryGeneration.Unknown);
    }

    /// <summary>
    /// 各物理盘的介质类型。两条来源按可靠性排序：
    /// <list type="number">
    ///   <item><c>MSFT_PhysicalDisk</c>（<c>root\Microsoft\Windows\Storage</c>，与任务管理器同源）：
    ///     <c>BusType</c> 17 = NVMe、<c>MediaType</c> 3 = HDD / 4 = SSD；</item>
    ///   <item>WMI <c>Win32_DiskDrive.Model</c> 的型号串关键字（"NVMe"/"SSD"）。
    ///     型号串不含关键字时返回 Unknown —— 不根据容量/转速瞎猜。</item>
    /// </list>
    /// <para>
    /// <b>回退门槛看的是"有没有判别成功"，不是"有没有条目"</b>（v1.21.0 修）：
    /// 原实现写的是 <c>kinds.Count > 0</c> 就直接返回，于是"字段读到了但全是 Unknown"会
    /// 彻底挡住型号串回退 —— 本机实测正卡在这里（UInt16 字段读取失败 → 全 Unknown → 回退不执行）。
    /// </para>
    /// </summary>
    private static IReadOnlyList<StorageMediaKind> ProbeStorageKinds()
    {
        var kinds = new List<StorageMediaKind>();

        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            using var searcher = new ManagementObjectSearcher(
                scope, new ObjectQuery("SELECT MediaType, BusType, DeviceId FROM MSFT_PhysicalDisk"));

            var entries = new List<(ulong Order, StorageMediaKind Kind)>();
            foreach (var item in searcher.Get())
            {
                using var disk = (ManagementObject)item;
                var media = (int)(ToNullableUInt64(disk["MediaType"]) ?? 0);
                var bus = (int)(ToNullableUInt64(disk["BusType"]) ?? 0);

                // 按 DeviceId 排序，使本列表与 Win32_DiskDrive 的 Index 顺序对齐 ——
                // 下面按位置合并两条来源的前提就是这个。
                entries.Add((ToNullableUInt64(disk["DeviceId"]) ?? 0, ClassifyPhysicalDisk(media, bus)));
            }

            entries.Sort((left, right) => left.Order.CompareTo(right.Order));
            kinds.AddRange(entries.Select(entry => entry.Kind));
        }
        catch
        {
            // 该命名空间不可用（精简版系统/权限受限）→ 退回型号串判断。
        }

        if (kinds.Count > 0 && kinds.All(kind => kind != StorageMediaKind.Unknown))
            return kinds;

        var byModel = ProbeStorageKindsByModel();
        if (byModel.Count == 0)
            return kinds;

        // 数量对不上（某一路少枚举了盘）时无法逐个对齐，整体改用型号串结果：
        // 两路都覆盖不到的那块盘本来就只能按未知档算，混着两份不可对齐的清单更糟。
        if (kinds.Count != byModel.Count)
            return byModel;

        // 逐块合并：MSFT 判出来的保留（更权威），只是它读不出来的那块用型号串补。
        for (var i = 0; i < kinds.Count; i++)
        {
            if (kinds[i] == StorageMediaKind.Unknown)
                kinds[i] = byModel[i];
        }

        return kinds;
    }

    /// <summary>型号串口径的介质判别（<c>Win32_DiskDrive</c>，按 <c>Index</c> 升序）。</summary>
    private static IReadOnlyList<StorageMediaKind> ProbeStorageKindsByModel()
    {
        var kinds = new List<StorageMediaKind>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Model, MediaType, Index FROM Win32_DiskDrive");

            var entries = new List<(ulong Order, StorageMediaKind Kind)>();
            foreach (var item in searcher.Get())
            {
                using var disk = (ManagementObject)item;
                var media = disk["MediaType"] as string ?? string.Empty;
                if (!media.StartsWith("Fixed", StringComparison.OrdinalIgnoreCase))
                    continue;

                entries.Add((ToNullableUInt64(disk["Index"]) ?? 0, ClassifyDriveModel(disk["Model"] as string)));
            }

            entries.Sort((left, right) => left.Order.CompareTo(right.Order));
            kinds.AddRange(entries.Select(entry => entry.Kind));
        }
        catch
        {
            kinds.Clear();
        }

        return kinds;
    }

    /// <summary>MSFT_PhysicalDisk 的 (MediaType, BusType) → 介质类型。</summary>
    private static StorageMediaKind ClassifyPhysicalDisk(int mediaType, int busType)
    {
        if (busType == 17)
            return StorageMediaKind.Nvme;

        return mediaType switch
        {
            3 => StorageMediaKind.Hdd,
            4 => StorageMediaKind.SataSsd,
            _ => StorageMediaKind.Unknown,
        };
    }

    /// <summary>硬盘型号串 → 介质类型（只认明确关键字，认不出就是 Unknown）。</summary>
    private static StorageMediaKind ClassifyDriveModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return StorageMediaKind.Unknown;

        if (model.Contains("NVMe", StringComparison.OrdinalIgnoreCase))
            return StorageMediaKind.Nvme;

        if (model.Contains("SSD", StringComparison.OrdinalIgnoreCase))
            return StorageMediaKind.SataSsd;

        return StorageMediaKind.Unknown;
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
    private static (string Name, double? MemoryGb, bool Discrete) ProbeGpu()
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
                return (string.Empty, null, false);

            var best = candidates
                .OrderByDescending(c => c.Discrete)
                .ThenByDescending(c => c.MemoryGb ?? 0)
                .ThenBy(c => c.Order)
                .First();
            return (best.Name, best.MemoryGb, best.Discrete);
        }
        catch
        {
            return (string.Empty, null, false);
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

    /// <summary>
    /// 是否独显产品线（核显不匹配：UHD / HD / Iris / 无 RX 的 Radeon / 680M 这类）。
    /// <para>
    /// v1.20.1 起改为转调 <see cref="GpuRatedPower.LooksDiscrete"/>：同一判别必须同时服务两处
    /// ——状态栏选卡排序（这里）与整机功耗模型（"独显要单独计、核显已含在封装内"），
    /// 各写一套关键字迟早会分叉，而分叉的后果是凭空多算/少算一二百瓦。
    /// </para>
    /// </summary>
    private static bool IsDiscreteGpu(string shortName) => GpuRatedPower.LooksDiscrete(shortName);

    /// <summary>WMI 属性装箱值 → ulong（AdapterRAM/Size 等均为无符号整型属性）。</summary>
    /// <summary>
    /// WMI 数值字段 → <c>ulong?</c>。**必须覆盖全部整型宽度**。
    /// <para>
    /// WMI 的 <c>UInt16</c> 字段在 .NET 侧是 <c>ushort</c>：只写 <c>uint</c>/<c>ulong</c> 两个分支，
    /// 它就会一律返回 null。v1.21.0 实测两处后果：
    /// </para>
    /// <list type="bullet">
    /// <item><c>MSFT_PhysicalDisk.BusType</c> = 17 (UInt16)、<c>MediaType</c> = 4 (UInt16)
    /// 双双读成 0 → 单块 NVMe 被判成「介质未知」，硬盘功耗按最保守的未知档算；
    /// 又因下面的回退门槛看的是条目数而非"是否判别成功"，型号串回退也没能兜住。</item>
    /// <item><c>Win32_PhysicalMemory.SMBIOSMemoryType</c> (UInt16) 同样读不到，
    /// 只因另有一路 <c>MemoryType</c> (UInt32) 兜底才没暴露 —— 属于同一处漏写的另一个受害者。</item>
    /// </list>
    /// <para>约定保持不变：0 与非整型（含 null）返回 null。</para>
    /// </summary>
    private static ulong? ToNullableUInt64(object? value)
    {
        var raw = value switch
        {
            byte asByte => asByte,
            // 有符号的小整型要显式转 int：Math.Max(0, asSbyte) 里的字面量 0 是 int，
            // 会同时匹配 Math.Max(int,int) 与 Math.Max(sbyte,sbyte) 而报 CS0121。
            sbyte asSbyte => (ulong)Math.Max(0, (int)asSbyte),
            ushort asUshort => asUshort,
            short asShort => (ulong)Math.Max(0, (int)asShort),
            uint asUint => asUint,
            int asInt => (ulong)Math.Max(0, asInt),
            ulong asUlong => asUlong,
            long asLong => (ulong)Math.Max(0, asLong),
            _ => 0UL,
        };

        return raw > 0 ? raw : null;
    }

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
