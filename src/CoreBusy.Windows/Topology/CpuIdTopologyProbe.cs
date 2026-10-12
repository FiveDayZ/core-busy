namespace CoreBusy.Windows.Topology;

using System.Runtime.InteropServices;
using CoreBusy.Core.Models;

/// <summary>
/// 基于 CPUID 指令的物理核心归并探针（独立于固件 ACPI / 内核拓扑表）。
/// 原理：把当前线程逐个绑定到每颗逻辑处理器，读取 CPUID.01H.EBX[31:24] 的初始 APIC ID，
/// 再按 SMT 位宽右移得到核心 ID —— 这份数据来自 CPU 芯片自身的拓扑层级，
/// 不经过 BIOS 的 ACPI 表，是 <c>GetLogicalProcessorInformationEx</c> 之外的独立真相来源。
/// 用途：个别整机固件会误报物理核拓扑（实测 i3-N305 8 核 8 线程被报成 4 核 × 2 线程），
/// 此时以 CPUID 归并结果为准做交叉校验。
/// 混合架构（Intel 12 代起，P 核带 SMT、E 核单线程）每核线程数不均一：
/// 统一移位会把相邻 E 核两两并成假核心（实测 U300 5 核 6 线程被读成 3 核 6 线程），
/// 因此混合架构改走逐逻辑处理器识别核心类型（CPUID.1AH）再按类型归并的路径。
/// </summary>
internal static class CpuIdTopologyProbe
{
    /// <summary>探测结果：<see cref="CoreIdByOsIndex"/> 按系统逻辑处理器编号给出核心 ID。</summary>
    /// <param name="CoreTypeByOsIndex">
    /// 混合架构时按逻辑处理器给出核心类别（CPUID.1AH 核心类型）；非混合架构为 null。
    /// </param>
    public sealed record ProbeResult(
        int ThreadsPerCore,
        int[] CoreIdByOsIndex,
        CoreClass[]? CoreTypeByOsIndex = null)
    {
        /// <summary>物理核心数（核心 ID 去重数）。</summary>
        public int CoreCount => CoreIdByOsIndex.Distinct().Count();

        /// <summary>探针是否逐逻辑处理器识别了核心类别（混合架构模式）。</summary>
        public bool HasCoreTypes => CoreTypeByOsIndex is not null;

        public string Detail => HasCoreTypes
            ? $"hybrid cores={CoreCount}"
            : $"tpc={ThreadsPerCore} cores={CoreCount}";
    }

    private const int VendorOther = 0;
    private const int VendorAmd = 1;
    private const int VendorIntel = 2;

    /// <summary>
    /// 尝试探测。任何前提不满足（&gt;64 逻辑处理器的多处理器组 / 亲和性受限 / APIC ID 异常）
    /// 都返回 null 并经 <paramref name="reason"/> 给出原因，调用方应保守地维持 GLPI 结果。
    /// </summary>
    public static ProbeResult? TryProbe(int logicalCount, out string reason)
    {
        reason = "uninitialized";
        if (logicalCount <= 0 || logicalCount > 64)
        {
            reason = $"logicalCount={logicalCount} out of range";
            return null; // 多处理器组（>64 逻辑处理器）不在本探针覆盖范围。
        }

        if (!Environment.Is64BitProcess || !Environment.Is64BitOperatingSystem)
        {
            reason = "not 64-bit process/os";
            return null;
        }

        ProbeResult? result = null;
        var workerReason = reason;
        // 用专用线程跑：需要反复改当前线程亲和性，不能污染线程池线程。
        var worker = new Thread(() => result = Run(logicalCount, out workerReason))
        {
            IsBackground = true,
            Name = "corebusy-cpuid-probe",
        };
        worker.Start();
        worker.Join();
        reason = workerReason;

        return result;
    }

    private static ProbeResult? Run(int logicalCount, out string reason)
    {
        using var cpuId = new CpuIdAccessor();
        if (!cpuId.Available)
        {
            reason = "cpuid thunk unavailable";
            return null;
        }

        var fullMask = new IntPtr((1L << logicalCount) - 1);
        var original = SetThreadAffinityMask(GetCurrentThread(), fullMask);
        if (original == IntPtr.Zero)
        {
            reason = $"SetThreadAffinityMask(full) failed err={Marshal.GetLastWin32Error()}";
            return null; // 进程亲和性受限（如用户手工绑核），探测不可信。
        }

        try
        {
            var vendor = DetectVendor(cpuId);
            var maxLeaf = cpuId.Read(0, 0).Eax;

            // Intel 混合架构判别：0x1F 各子叶的核心类型同时含 P(Core,0x40) 与 E(Atom,0x20)。
            var hybrid = vendor == VendorIntel && maxLeaf >= 0x1F && IsIntelHybrid(cpuId);
            if (hybrid && maxLeaf < 0x1A)
            {
                // 核心类型无法读取就无法正确归并（统一移位必然并错 E 核），保守放弃，维持 GLPI 结果。
                reason = "hybrid cpu without cpuid leaf 0x1A (core type unavailable)";
                return null;
            }

            // 混合架构：按核心类型分别取每核线程数；同构：统一每核线程数。
            var tpcByType = hybrid ? DetectThreadsPerCoreByType(cpuId) : null;
            var threadsPerCore = hybrid ? 0 : DetectThreadsPerCore(cpuId, vendor, logicalCount);
            var smtShift = threadsPerCore switch
            {
                >= 8 => 3,
                >= 4 => 2,
                >= 2 => 1,
                _ => 0,
            };

            var apicByOs = new int[logicalCount];
            var rawTypeByOs = hybrid ? new byte[logicalCount] : null;
            var seenApicIds = new HashSet<int>();
            for (var os = 0; os < logicalCount; os++)
            {
                var prev = SetThreadAffinityMask(GetCurrentThread(), new IntPtr(1L << os));
                if (prev == IntPtr.Zero)
                {
                    reason = $"SetThreadAffinityMask(os={os}) failed err={Marshal.GetLastWin32Error()}";
                    return null;
                }

                // 等待调度器把线程迁移到目标处理器：Sleep(0) 在无就绪线程时不发生切换，
                // 必须 Sleep(1) 并用 GetCurrentProcessorNumber 确认迁移完成后才读 APIC ID。
                var migrated = false;
                for (var attempt = 0; attempt < 8 && !migrated; attempt++)
                {
                    Thread.Sleep(1);
                    migrated = GetCurrentProcessorNumber() == os;
                }

                if (!migrated)
                {
                    reason = $"thread stuck on cpu={GetCurrentProcessorNumber()}, expected os={os}";
                    return null;
                }

                // CPUID.01H.EBX[31:24] = 初始 APIC ID（当前正在执行的处理器）。
                // 注意在 EBX：EAX[31:24] 是版本信息（Family/Model/Stepping），每核相同。
                var ebx = cpuId.Read(1, 0).Ebx;
                var apicId = (int)((ebx >> 24) & 0xFF);
                if (!seenApicIds.Add(apicId))
                {
                    reason = $"duplicate apic id={apicId} at os={os} (affinity not effective)";
                    return null; // 两个 OS 编号读到同一 APIC ID：亲和性未生效，探测作废。
                }

                apicByOs[os] = apicId;
                if (hybrid)
                {
                    // CPUID.1AH.EAX[31:24] = 当前处理器的核心类型（0x40=Core/P，0x20=Atom/E）。
                    rawTypeByOs![os] = (byte)((cpuId.Read(0x1A, 0).Eax >> 24) & 0xFF);
                }
            }

            int[] coreByOs;
            CoreClass[]? typeByOs;
            if (hybrid)
            {
                (coreByOs, typeByOs) = GroupHybridByApic(apicByOs, rawTypeByOs!, tpcByType!);
                reason = $"ok vendor={vendor} hybrid tpcByType={DescribeTpcByType(tpcByType!)} " +
                         $"cores={coreByOs.Distinct().Count()}";
            }
            else
            {
                coreByOs = new int[logicalCount];
                for (var os = 0; os < logicalCount; os++)
                    coreByOs[os] = apicByOs[os] >> smtShift;

                typeByOs = null;
                reason = $"ok vendor={vendor} tpc={threadsPerCore} cores={coreByOs.Distinct().Count()}";
            }

            return new ProbeResult(threadsPerCore, coreByOs, typeByOs);
        }
        finally
        {
            SetThreadAffinityMask(GetCurrentThread(), original);
        }
    }

    /// <summary>
    /// Intel 混合架构判别：扫描 CPUID.1F 各子叶的核心类型（EAX[31:24]），
    /// 同时出现 Core(0x40) 与 Atom(0x20) 即为混合架构（P 核 + E 核并存）。
    /// </summary>
    private static bool IsIntelHybrid(CpuIdAccessor cpuId)
    {
        bool core = false, atom = false;
        for (uint sub = 0; sub < 8 && !(core && atom); sub++)
        {
            var r = cpuId.Read(0x1F, (int)sub);
            if (r.Eax == 0 && r.Ebx == 0 && r.Ecx == 0)
                break; // 无效子叶（超出实际层级数）。

            var type = (r.Eax >> 24) & 0xFF;
            if (type == 0x40)
                core = true;
            else if (type == 0x20)
                atom = true;
        }

        return core && atom;
    }

    /// <summary>
    /// 混合架构下按核心类型取每核线程数：扫描 CPUID.1F 各子叶，
    /// SMT 层级（ECX[7:0]==0）的 EBX 即该核心类型（ECX[15:8]）的每核线程数。
    /// P 域典型为 2，E 域无 SMT 子叶时缺失（归并侧按 1 处理）。
    /// </summary>
    private static Dictionary<byte, int> DetectThreadsPerCoreByType(CpuIdAccessor cpuId)
    {
        var tpcByType = new Dictionary<byte, int>();
        for (uint sub = 0; sub < 8; sub++)
        {
            var r = cpuId.Read(0x1F, (int)sub);
            if (r.Eax == 0 && r.Ebx == 0 && r.Ecx == 0)
                break; // 无效子叶（超出实际层级数）。

            if ((r.Ecx & 0xFF) == 0 && r.Ebx > 0)
                tpcByType.TryAdd((byte)((r.Ecx >> 8) & 0xFF), (int)r.Ebx);
        }

        return tpcByType;
    }

    private static string DescribeTpcByType(Dictionary<byte, int> tpcByType)
        => tpcByType.Count == 0
            ? "none"
            : string.Join(",", tpcByType.Select(kv => $"0x{kv.Key:X}:{kv.Value}"));

    /// <summary>
    /// 混合架构核心归并（纯函数，供模拟验证）：按「核心类型 + 类型内移位后的 APIC ID」分组。
    /// 每个类型的移位档位取自该类型自身的每核线程数——P 域 tpc=2 右移 1 位、
    /// E 域 tpc=1 右移 0 位，各自独立归并；分组编号按 OS 序首次出现顺序分配
    /// （Windows 上 P 核通常排在逻辑处理器序列前端）。
    /// 绝不能用统一移位：U300 5C6T 按 P 域 tpc=2 统一移位时，4 颗 E 核的连续 APIC ID
    /// 会被两两合并，得到 3 核的错误结论。
    /// </summary>
    /// <returns>按系统逻辑处理器编号给出的分组 ID 与核心类别。</returns>
    internal static (int[] GroupIdByOsIndex, CoreClass[] TypeByOsIndex) GroupHybridByApic(
        int[] apicIdByOsIndex,
        byte[] rawCoreTypeByOsIndex,
        Dictionary<byte, int> threadsPerCoreByType)
    {
        var count = apicIdByOsIndex.Length;
        var groupByOs = new int[count];
        var typeByOs = new CoreClass[count];
        var groupIndexOf = new Dictionary<(byte Type, int CoreId), int>();

        for (var os = 0; os < count; os++)
        {
            var typeByte = rawCoreTypeByOsIndex[os];
            typeByOs[os] = typeByte switch
            {
                0x40 => CoreClass.Performance,
                0x20 => CoreClass.Efficiency,
                _ => CoreClass.Standard,
            };

            var tpc = threadsPerCoreByType.TryGetValue(typeByte, out var t) && t > 0 ? t : 1;
            var smtShift = tpc switch
            {
                >= 8 => 3,
                >= 4 => 2,
                >= 2 => 1,
                _ => 0,
            };

            var key = (typeByte, apicIdByOsIndex[os] >> smtShift);
            if (!groupIndexOf.TryGetValue(key, out var group))
                groupIndexOf[key] = group = groupIndexOf.Count;

            groupByOs[os] = group;
        }

        return (groupByOs, typeByOs);
    }

    /// <summary>从 CPUID 叶 0 的厂商标识串判别厂商（EBX+EDX+ECX 拼出 "GenuineIntel" / "AuthenticAMD"）。</summary>
    private static int DetectVendor(CpuIdAccessor cpuId)
    {
        var r = cpuId.Read(0, 0);
        Span<byte> bytes = stackalloc byte[12];
        GetBytes(r.Ebx).CopyTo(bytes);
        GetBytes(r.Edx).CopyTo(bytes.Slice(4));
        GetBytes(r.Ecx).CopyTo(bytes.Slice(8));
        var text = System.Text.Encoding.ASCII.GetString(bytes);

        if (text.Contains("GenuineIntel"))
            return VendorIntel;
        if (text.Contains("AuthenticAMD"))
            return VendorAmd;
        return VendorOther;
    }

    /// <summary>
    /// 每物理核线程数探测，按厂商选路径：
    /// Intel 依次取 CPUID.1FH / 0BH 的 SMT 层（EBX = 每核线程数）；
    /// AMD 取扩展叶 8000_001EH.EBX[15:8]（值为 tpc-1；注意 [7:0] 是 CoreId 逐核不同，
    /// 绝不能读——实测曾因读到 CoreId=0 导致 tpc 退化。0BH 在 AMD 上也不可靠，实测返回无效值 1）。
    /// 两者都不可用时回退 CPUID.04H EAX[31:26]+1（共享 L1d 的最大逻辑处理器数，Intel/AMD 通用）。
    /// <paramref name="logicalCount"/> 用于拒绝不整除的异常值（字段异常时保守回退）。
    /// </summary>
    private static int DetectThreadsPerCore(CpuIdAccessor cpuId, int vendor, int logicalCount)
    {
        var maxLeaf = cpuId.Read(0, 0).Eax;

        if (vendor == VendorIntel)
        {
            if (maxLeaf >= 0x1F && cpuId.Read(0x1F, 0).Ebx > 0)
                return (int)cpuId.Read(0x1F, 0).Ebx;

            if (maxLeaf >= 0x0B && cpuId.Read(0x0B, 0).Ebx > 0)
                return (int)cpuId.Read(0x0B, 0).Ebx;
        }
        else if (vendor == VendorAmd)
        {
            var maxExt = cpuId.Read(unchecked((int)0x80000000), 0).Eax;
            if (maxExt >= 0x8000001Eu)
            {
                // AMD APM：EBX[15:8] = ThreadsPerCore，字段值 = 每核线程数 - 1；包级统一。
                var tpc = (int)((cpuId.Read(unchecked((int)0x8000001E), 0).Ebx >> 8) & 0xFF) + 1;
                if (tpc > 0 && logicalCount % tpc == 0)
                    return tpc;
            }
        }

        if (maxLeaf >= 0x04)
            return (int)((cpuId.Read(0x04, 0).Eax >> 26) & 0x3F) + 1;

        return 1;
    }

    /// <summary>
    /// CPUID 静态信息（v1.10.2）：品牌串、Intel 标称基频、混合架构标志。
    /// 全部来自 CPU 芯片自报，不经固件 SMBIOS/ACPI，用于交叉校验 WMI 的单一来源参数。
    /// </summary>
    public sealed record StaticCpuInfo(
        string VendorId,
        string BrandString,
        int? IntelBaseFrequencyMhz,
        bool IntelHybrid,
        int Intel16RawMhz = 0,
        int? IntelBusMhz = null);

    /// <summary>
    /// 读取 CPUID 静态信息（不需要绑定核心，在调用线程直接执行）：
    /// - 厂商串：CPUID.0H（EBX+EDX+ECX）；
    /// - 品牌串：扩展叶 0x80000002..04，48 字节 ASCII；
    /// - Intel 基频：CPUID.16H.EAX（MHz，Skylake 起支持，芯片自报，不经固件）。它报的是
    ///   按当前平台功耗配置自报的非睿频比（实测某 i3-N305 整机 PL1=10W 时报 1000），
    ///   不等于 SKU 标称 HFM——Intel 只在默认 PL1 档公布后者（N305 15W 档 = 1.8 GHz）；
    /// - 混合架构：0x1F 子叶 EAX[31:24] 同时出现 Core(0x40) 与 Atom(0x20)。
    /// </summary>
    public static bool TryReadStaticInfo(out StaticCpuInfo info, out string reason)
    {
        info = new StaticCpuInfo("Unknown", string.Empty, null, false);
        using var cpuId = new CpuIdAccessor();
        if (!cpuId.Available)
        {
            reason = "cpuid thunk unavailable";
            return false;
        }

        var vendorLeaf = cpuId.Read(0, 0);
        Span<byte> vendorBytes = stackalloc byte[12];
        GetBytes(vendorLeaf.Ebx).CopyTo(vendorBytes);
        GetBytes(vendorLeaf.Edx).CopyTo(vendorBytes.Slice(4));
        GetBytes(vendorLeaf.Ecx).CopyTo(vendorBytes.Slice(8));
        var vendorId = System.Text.Encoding.ASCII.GetString(vendorBytes);

        var isIntel = vendorId.Contains("GenuineIntel");
        var maxLeaf = vendorLeaf.Eax;
        var maxExt = cpuId.Read(unchecked((int)0x80000000), 0).Eax;

        // 品牌串：扩展叶 0x80000002..04，48 字节 ASCII，去 NUL 并压缩空白。
        var brand = string.Empty;
        if (maxExt >= 0x80000004u)
        {
            Span<byte> brandBytes = stackalloc byte[48];
            for (uint i = 0; i < 3; i++)
            {
                var r = cpuId.Read(unchecked((int)(0x80000002u + i)), 0);
                GetBytes(r.Eax).CopyTo(brandBytes.Slice((int)i * 16));
                GetBytes(r.Ebx).CopyTo(brandBytes.Slice((int)i * 16 + 4));
                GetBytes(r.Ecx).CopyTo(brandBytes.Slice((int)i * 16 + 8));
                GetBytes(r.Edx).CopyTo(brandBytes.Slice((int)i * 16 + 12));
            }

            brand = System.Text.Encoding.ASCII.GetString(brandBytes).TrimEnd('\0');
            brand = string.Join(' ', brand.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        // Intel 基频：CPUID.16H.EAX（MHz），合理域 100..10000。
        // 实测 Alder Lake-N 该叶有效（i3-N305 → 1000 / 总线 100 MHz）；个别平台若返回 0，
        // 原始值随日志带出以便核对，MSR_PLATFORM_INFO 层可兜底
        // （WindowsCpuMonitorService.RefineBaseClockWithMsr）。
        int? intelBaseMhz = null;
        int intel16RawMhz = 0;
        int? intelBusMhz = null;
        if (isIntel && maxLeaf >= 0x16)
        {
            var r16 = cpuId.Read(0x16, 0);
            intel16RawMhz = (int)(r16.Eax & 0xFFFF);

            var bus = (int)(r16.Ecx & 0xFFFF);
            if (bus is >= 1 and <= 1000)
                intelBusMhz = bus;

            if (intel16RawMhz is >= 100 and <= 10000)
                intelBaseMhz = intel16RawMhz;
        }

        // Intel 混合架构检测：0x1F 各子叶的核心类型同时含 P(Core,0x40) 与 E(Atom,0x20)。
        var hybrid = false;
        if (isIntel && maxLeaf >= 0x1F)
        {
            bool core = false, atom = false;
            for (uint sub = 0; sub < 8 && !(core && atom); sub++)
            {
                var type = (cpuId.Read(0x1F, (int)sub).Eax >> 24) & 0xFF;
                if (type == 0x40)
                    core = true;
                else if (type == 0x20)
                    atom = true;
            }

            hybrid = core && atom;
        }

        info = new StaticCpuInfo(vendorId.Trim(), brand, intelBaseMhz, hybrid, intel16RawMhz, intelBusMhz);
        reason = $"ok brand='{brand}' intelBaseMhz={(intelBaseMhz.HasValue ? intelBaseMhz.Value.ToString() : "-")} intel16Raw={intel16RawMhz} bus={(intelBusMhz.HasValue ? intelBusMhz.Value.ToString() : "-")} hybrid={hybrid}";
        return true;
    }

    private static byte[] GetBytes(uint value)
        => [(byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24)];

    // ---------------------------------------------------------------- CPUID 执行通道

    /// <summary>CPUID 四寄存器结果。</summary>
    private readonly record struct CpuIdRegs(uint Eax, uint Ebx, uint Ecx, uint Edx);

    /// <summary>
    /// .NET 8 没有托管 CPUID API（<c>X86Base.Cpuid</c> 要到 .NET 9 才加入），
    /// 这里用一段原生 thunk 执行 CPUID 指令：先以 RW 写入机器码，再翻转为 RX 执行
    /// （不使用 RWX，避免触发安全软件启发式）。机器码按 Win64 ABI：rcx=leaf，rdx=subleaf，r8=结果指针。
    /// </summary>
    private sealed class CpuIdAccessor : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void CpuIdDelegate(int leaf, int subleaf, IntPtr regs);

        private readonly IntPtr _code;
        private readonly CpuIdDelegate? _invoke;

        public CpuIdAccessor()
        {
            try
            {
                // push rbx; mov eax,ecx; mov ecx,edx; cpuid;
                // mov [r8],eax; mov [r8+4],ebx; mov [r8+8],ecx; mov [r8+12],edx; pop rbx; ret
                byte[] code =
                [
                    0x53, 0x89, 0xC8, 0x89, 0xD1, 0x0F, 0xA2,
                    0x41, 0x89, 0x00,
                    0x41, 0x89, 0x58, 0x04,
                    0x41, 0x89, 0x48, 0x08,
                    0x41, 0x89, 0x50, 0x0C,
                    0x5B, 0xC3,
                ];

                _code = VirtualAlloc(IntPtr.Zero, (UIntPtr)code.Length, MemCommit | MemReserve, PageReadWrite);
                if (_code == IntPtr.Zero)
                    return;

                Marshal.Copy(code, 0, _code, code.Length);

                if (!VirtualProtect(_code, (UIntPtr)code.Length, PageExecuteRead, out _))
                {
                    VirtualFree(_code, UIntPtr.Zero, MemRelease);
                    _code = IntPtr.Zero;
                    return;
                }

                _invoke = Marshal.GetDelegateForFunctionPointer<CpuIdDelegate>(_code);
            }
            catch
            {
                // 分配 / 保护翻转失败（如被策略拦截）：Available=false，探针保守退出。
                _code = IntPtr.Zero;
                _invoke = null;
            }
        }

        public bool Available => _invoke is not null;

        public CpuIdRegs Read(int leaf, int subleaf)
        {
            var buf = Marshal.AllocHGlobal(16);
            try
            {
                _invoke!(leaf, subleaf, buf);
                return new CpuIdRegs(
                    (uint)Marshal.ReadInt32(buf, 0),
                    (uint)Marshal.ReadInt32(buf, 4),
                    (uint)Marshal.ReadInt32(buf, 8),
                    (uint)Marshal.ReadInt32(buf, 12));
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        public void Dispose()
        {
            if (_code != IntPtr.Zero)
                VirtualFree(_code, UIntPtr.Zero, MemRelease);
        }

        private const int MemCommit = 0x1000;
        private const int MemReserve = 0x2000;
        private const int MemRelease = 0x8000;
        private const int PageReadWrite = 0x04;
        private const int PageExecuteRead = 0x20;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAlloc(IntPtr lpAddress, UIntPtr dwSize, int flAllocationType, int flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize, int flNewProtect, out int lpflOldProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualFree(IntPtr lpAddress, UIntPtr dwSize, int dwFreeType);
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentProcessorNumber();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr SetThreadAffinityMask(IntPtr hThread, IntPtr dwThreadAffinityMask);
}
