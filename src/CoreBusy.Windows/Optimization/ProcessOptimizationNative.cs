namespace CoreBusy.Windows.Optimization;

using System.Runtime.InteropServices;

/// <summary>
/// 进程优化用到的原生 API 声明（v1.12.0）。
/// <para>
/// 刻意不使用 CPU Sets（<c>SetProcessDefaultCpuSets</c>）：那套 API 需要先用
/// <c>GetSystemCpuSetInformation</c> 枚举 CPU 集 ID 并建立"逻辑处理器 ↔ CPU 集"映射，
/// 复杂度高，而它的价值只体现在**超过 64 个逻辑处理器**（需要跨处理器组）的场景。
/// 本文具的目标平台是 8–16 线程的桌面机，单处理器组内的位掩码完全够用，
/// 多处理器组的情况在 <c>AffinityMaskBuilder</c> 里明确拒绝而不是给出错误结果。
/// </para>
/// </summary>
internal static class ProcessOptimizationNative
{
    // ===== 进程访问权限 =====
    internal const uint PROCESS_SET_INFORMATION = 0x0200;
    internal const uint PROCESS_QUERY_INFORMATION = 0x0400;
    internal const uint PROCESS_SET_QUOTA = 0x0100;

    /// <summary>设置亲和性与优先级所需的最小权限集合。</summary>
    internal const uint AccessRequired =
        PROCESS_QUERY_INFORMATION | PROCESS_SET_INFORMATION;

    /// <summary>作业对象需要额外的 SET_QUOTA 权限。</summary>
    internal const uint AccessRequiredForJob =
        PROCESS_QUERY_INFORMATION | PROCESS_SET_INFORMATION | PROCESS_SET_QUOTA;

    // ===== 优先级类 =====
    internal const uint IDLE_PRIORITY_CLASS = 0x00000040;
    internal const uint BELOW_NORMAL_PRIORITY_CLASS = 0x00004000;
    internal const uint NORMAL_PRIORITY_CLASS = 0x00000020;
    internal const uint ABOVE_NORMAL_PRIORITY_CLASS = 0x00008000;
    internal const uint HIGH_PRIORITY_CLASS = 0x00000080;

    // ===== 作业对象 =====
    /// <summary>JobObjectBasicLimitInformation 信息类编号。</summary>
    internal const int JobObjectBasicLimitInformation = 2;

    /// <summary>限制作业内进程的亲和性（内核强制，进程自身无法通过 SetProcessAffinityMask 逃逸）。</summary>
    internal const uint JOB_OBJECT_LIMIT_AFFINITY = 0x00000010;

    // ===== I/O 优先级 =====
    /// <summary>ntdll!NtSetInformationProcess 的 ProcessIoPriority 信息类。</summary>
    internal const int ProcessIoPriority = 33;

    /// <summary>I/O 优先级 VeryLow（低于系统默认的 Normal）。</summary>
    internal const int IoPriorityVeryLow = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetProcessAffinityMask(IntPtr process, UIntPtr affinityMask);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetPriorityClass(IntPtr process, uint priorityClass);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetInformationJobObject(IntPtr job, int informationClass, IntPtr information, uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("ntdll.dll")]
    internal static extern int NtSetInformationProcess(IntPtr process, int informationClass, ref int information, int informationLength);

    /// <summary>
    /// JOBOBJECT_BASIC_LIMIT_INFORMATION。
    /// 字段顺序与对齐严格按 winnt.h：两个 LARGE_INTEGER 在前，
    /// 之后 32 位 LimitFlags，再是两个 SIZE_T，最后三个 32 位字段。
    /// 写错顺序不会编译失败，但会被内核按错误偏移解读 —— 所以这里必须逐字段照抄。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }
}
