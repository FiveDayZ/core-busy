namespace CoreBusy.Windows.Topology;

using System.Runtime.InteropServices;
using CoreBusy.Core.Interfaces;
using CoreBusy.Core.Models;

/// <summary>
/// 基于 GetLogicalProcessorInformationEx(RelationProcessorCore) 的拓扑识别：
/// 每物理核心给出处理器掩码与 EfficiencyClass。Intel 混合架构上 P 核 EfficiencyClass &gt; 0、
/// E 核为 0；AMD / 非混合平台不区分 P/E，一律归为 <see cref="CoreClass.Standard"/>
/// （规范 §51：禁止把 AMD 显示成 P-Core / E-Core）。
/// </summary>
public sealed class LogicalProcessorTopologyService : ICpuTopologyService
{
    private const int RelationProcessorCore = 3;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    /// <summary>PROCESSOR_RELATIONSHIP.Flags 的 LTP_PC_SMT 位：该物理核心启用同步多线程。</summary>
    private const byte LtpPcSmt = 0x01;

    public CpuTopology GetTopology()
    {
        var cores = QueryProcessorCores();
        if (cores.Count == 0)
            throw new InvalidOperationException("GetLogicalProcessorInformationEx 未返回物理核心信息。");

        // 混合架构判据：同一 CPU 上存在多种 EfficiencyClass（Intel 12 代及以后的 P 核 / E 核）。
        bool hybrid = cores.Select(c => c.EfficiencyClass).Distinct().Count() > 1;
        byte performanceClass = hybrid ? cores.Max(c => c.EfficiencyClass) : byte.MinValue;

        var result = new List<LogicalProcessorTopology>();
        int pIndex = 0;
        int eIndex = 0;
        int standardIndex = 0;

        foreach (var core in cores.OrderBy(c => c.PhysicalCoreIndex))
        {
            var coreClass = !hybrid
                ? CoreClass.Standard
                : core.EfficiencyClass == performanceClass
                    ? CoreClass.Performance
                    : CoreClass.Efficiency;

            var smt = (core.Flags & LtpPcSmt) != 0;

            foreach (int osIndex in core.OsIndexes.OrderBy(i => i))
            {
                // 混合架构沿用 P0../E0..；同构平台用 C0.. 连续编号（与规范 §32 的 C0..Cn 一致）。
                var name = coreClass switch
                {
                    CoreClass.Performance => $"P{pIndex++}",
                    CoreClass.Efficiency => $"E{eIndex++}",
                    _ => $"C{standardIndex++}",
                };

                result.Add(new LogicalProcessorTopology(osIndex, core.PhysicalCoreIndex, coreClass, name, smt));
            }
        }

        return new CpuTopology(CrossCheckWithCpuId(result));
    }

    /// <summary>
    /// CPUID 交叉校验（v1.10.1）：GLPI 的物理核分组来自固件 ACPI 表，
    /// 个别整机固件会把无 SMT 平台误报成 SMT 对（实测 i3-N305 8 核 8 线程被报成 4 核 × 2 线程）。
    /// CPUID 探针直接读 CPU 内部 APIC ID 层级，与固件无关。两者物理核数不一致且非混合架构时，
    /// 以 CPUID 为准重建分组并写取证日志；混合架构（P/E 语义依赖 GLPI 的 EfficiencyClass）不改动。
    /// </summary>
    private static List<LogicalProcessorTopology> CrossCheckWithCpuId(List<LogicalProcessorTopology> lps)
    {
        if (lps.Any(p => p.OsIndex < 0 || p.OsIndex >= lps.Count))
        {
            return lps;
        }

        var probe = CpuIdTopologyProbe.TryProbe(lps.Count, out var probeReason);
        if (probe is null)
        {
            return lps;
        }

        var glpiCores = lps.Select(p => p.PhysicalCoreIndex).Distinct().Count();

        // 防呆：CPUID 核心数必须能整除线程数（每核线程数均一），否则数据不可信，不贸然改写。
        if (probe.CoreCount <= 0 || lps.Count % probe.CoreCount != 0)
        {
            return lps;
        }

        // 混合架构的 P/E 语义来自 GLPI 的 EfficiencyClass，CPUID 探针不解析核心类型，不参与改写。
        if (lps.Any(p => p.CoreClass is CoreClass.Performance or CoreClass.Efficiency))
        {
            if (glpiCores != probe.CoreCount)
            return lps;
        }

        if (glpiCores == probe.CoreCount)
            return lps;

        // 物理核数不一致 → 以 CPUID 归并为准重建（既覆盖固件虚报 SMT：N 核 1 线程被报成 N/2 核 × 2，
        // 也覆盖反向误报：SMT 对被拆成单线程核）。线程条 / 热力图行数不变（= 逻辑处理器数）。

        var coreIndexOf = new Dictionary<int, int>();
        var rebuilt = new List<LogicalProcessorTopology>(lps.Count);
        foreach (var lp in lps.OrderBy(p => probe.CoreIdByOsIndex[p.OsIndex]).ThenBy(p => p.OsIndex))
        {
            var coreId = probe.CoreIdByOsIndex[lp.OsIndex];
            if (!coreIndexOf.TryGetValue(coreId, out var index))
                coreIndexOf[coreId] = index = coreIndexOf.Count;

            rebuilt.Add(lp with
            {
                PhysicalCoreIndex = index,
                DisplayName = $"C{index}",
                Smt = probe.ThreadsPerCore > 1,
            });
        }

        return rebuilt;
    }

    private static List<(int PhysicalCoreIndex, byte EfficiencyClass, byte Flags, List<int> OsIndexes)> QueryProcessorCores()
    {
        uint length = 0;
        _ = GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref length);
        if (Marshal.GetLastWin32Error() != ERROR_INSUFFICIENT_BUFFER || length == 0)
            throw new InvalidOperationException(
                $"获取缓冲区长度失败（GetLastError={Marshal.GetLastWin32Error()}）。");

        var buffer = Marshal.AllocHGlobal((IntPtr)length);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length))
                throw new InvalidOperationException(
                    $"GetLogicalProcessorInformationEx 失败（GetLastError={Marshal.GetLastWin32Error()}）。");

            var cores = new List<(int, byte, byte, List<int>)>();
            IntPtr cursor = buffer;
            IntPtr end = buffer + (IntPtr)length;

            while (cursor < end)
            {
                var header = Marshal.PtrToStructure<SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX>(cursor);
                if (header.Relationship == RelationProcessorCore)
                {
                    IntPtr payload = cursor + Marshal.SizeOf<SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX>();
                    var procRel = Marshal.PtrToStructure<PROCESSOR_RELATIONSHIP>(payload);

                    var osIndexes = new List<int>();
                    IntPtr groupCursor = payload + Marshal.SizeOf<PROCESSOR_RELATIONSHIP>();
                    for (int g = 0; g < procRel.GroupCount; g++)
                    {
                        var group = Marshal.PtrToStructure<GROUP_AFFINITY>(groupCursor);
                        ulong mask = (ulong)group.Mask.ToInt64();
                        for (int bit = 0; bit < 64; bit++)
                        {
                            if ((mask & (1UL << bit)) != 0)
                                osIndexes.Add(g * 64 + bit);
                        }
                        groupCursor += Marshal.SizeOf<GROUP_AFFINITY>();
                    }

                    cores.Add((cores.Count, procRel.EfficiencyClass, procRel.Flags, osIndexes));
                }

                // 条目按 ULONG_PTR 对齐。
                cursor += (IntPtr)header.Size;
                long rem = cursor.ToInt64() % (long)sizeof(ulong);
                if (rem != 0)
                    cursor += (IntPtr)(sizeof(ulong) - rem);
            }

            return cores;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GROUP_AFFINITY
    {
        public IntPtr Mask;
        public ushort Group;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESSOR_RELATIONSHIP
    {
        public byte Flags;
        public byte EfficiencyClass;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)]
        public byte[] Reserved;
        public ushort GroupCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX
    {
        public uint Relationship;
        public uint Size;
        // 后接 PROCESSOR_RELATIONSHIP 载荷。
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(
        int relationshipClass,
        IntPtr buffer,
        ref uint returnedLength);
}
