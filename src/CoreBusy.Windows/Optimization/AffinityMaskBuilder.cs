namespace CoreBusy.Windows.Optimization;

using CoreBusy.Core.Models;

/// <summary>
/// 把 <see cref="CoreAffinityMode"/> 按**真实拓扑**折算成处理器位掩码（v1.12.0）。
/// <para>
/// 掩码的语义：第 n 位对应操作系统逻辑处理器 n，与
/// <see cref="CoreBusy.Core.Models.LogicalProcessorTopology.OsIndex"/> 严格对齐 ——
/// 这也是 PerformanceCounter 的实例编号口径，两者必须同源，否则会出现
/// "界面显示绑到核 3、实际落在核 5"这类看不见的错位。
/// </para>
/// </summary>
internal sealed class AffinityMaskBuilder
{
    /// <summary>
    /// 单处理器组上限。Windows 在逻辑处理器超过 64 时会把它们拆进多个处理器组，
    /// 而 <c>SetProcessAffinityMask</c> 的掩码只在**单个**组内有效（且只作用于组 0）。
    /// 此时必须改用 CPU Sets，本实现选择明确拒绝而不是给出错误结果。
    /// </summary>
    private const int MaxSingleGroupProcessors = 64;

    private readonly CpuTopology _topology;

    public AffinityMaskBuilder(CpuTopology topology) => _topology = topology;

    /// <summary>逻辑处理器总数（掩码有效位数）。</summary>
    public int LogicalCount => _topology.LogicalProcessors.Count;

    /// <summary>是否可安全使用位掩码。多处理器组或空拓扑时为 false。</summary>
    public bool SupportsMask => LogicalCount is > 0 and <= MaxSingleGroupProcessors;

    /// <summary>是否为 SMT 平台（逻辑处理器多于物理核，决定「仅物理核」是否可用）。</summary>
    public bool HasSmt => LogicalCount > PhysicalCoreCount;

    /// <summary>物理核数（按物理核序号去重）。</summary>
    public int PhysicalCoreCount => _topology.LogicalProcessors
        .Select(p => p.PhysicalCoreIndex)
        .Distinct()
        .Count();

    /// <summary>全部逻辑处理器的掩码（选择器的「全选」与自定义掩码的校验上界共用）。</summary>
    public ulong FullMask
    {
        get
        {
            ulong bits = 0;
            foreach (var p in _topology.LogicalProcessors)
            {
                if (p.OsIndex is >= 0 and < MaxSingleGroupProcessors)
                    bits |= 1UL << p.OsIndex;
            }

            return bits;
        }
    }

    /// <summary>
    /// 按硬件实际能力过滤后的可选模式。
    /// 非混合架构（AMD 全系、Intel 非混合）不给出 P/E 两项；无 SMT 不给出「仅物理核」。
    /// 「自定义核心」在任何单组拓扑上都有意义（非混合机也能"只用前 4 个核"），
    /// 但前提是拓扑解析成功 —— 否则选择器没有数据可展示，等于空选项。
    /// </summary>
    public IReadOnlyList<CoreAffinityMode> AvailableModes()
    {
        if (!SupportsMask)
            return [CoreAffinityMode.None];

        var modes = new List<CoreAffinityMode> { CoreAffinityMode.None, CoreAffinityMode.AllCores };

        if (HasSmt)
            modes.Add(CoreAffinityMode.PhysicalOnly);
        if (_topology.HasPerformanceCores)
            modes.Add(CoreAffinityMode.PerformanceOnly);
        if (_topology.HasEfficiencyCores)
            modes.Add(CoreAffinityMode.EfficiencyOnly);
        modes.Add(CoreAffinityMode.Custom);

        return modes;
    }

    /// <summary>
    /// 校验自定义掩码。合法返回 null；否则给出可读原因。
    /// 界面与服务端共用：0 = 一个核都没留；越界位说明 settings.json 被手改、
    /// 或拓扑变了（例：换了 CPU 后旧掩码指向不存在的处理器）。两种情况都必须
    /// **拒绝**而不是静默取交集 —— 交集会让用户以为屏蔽了 4 个核，实际只有 2 个。
    /// </summary>
    public string? ValidateCustom(ulong customMask)
    {
        if (customMask == 0)
            return "至少保留 1 个核心";
        if ((customMask & ~FullMask) != 0)
            return $"掩码 0x{customMask:X} 含本机不存在的逻辑处理器";
        return null;
    }

    /// <summary>把自定义掩码折算成亲和性调用可用的掩码（先校验再折算）。</summary>
    public bool TryBuildCustom(ulong customMask, out UIntPtr mask, out string reason)
    {
        mask = UIntPtr.Zero;
        if (ValidateCustom(customMask) is { } error)
        {
            reason = error;
            return false;
        }

        mask = (UIntPtr)customMask;
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// 构建掩码。成功返回 true；失败（不支持的模式 / 拓扑不可用）返回 false 并给出原因。
    /// <paramref name="customMask"/> 仅在 <paramref name="mode"/> 为
    /// <see cref="CoreAffinityMode.Custom"/> 时使用。
    /// </summary>
    public bool TryBuild(CoreAffinityMode mode, ulong customMask, out UIntPtr mask, out string reason)
    {
        mask = UIntPtr.Zero;
        reason = string.Empty;

        if (!SupportsMask)
        {
            reason = LogicalCount > MaxSingleGroupProcessors
                ? $"逻辑处理器 {LogicalCount} 个，跨越多个处理器组，位掩码方式不可用"
                : "拓扑不可用";
            return false;
        }

        var osIndices = mode switch
        {
            CoreAffinityMode.AllCores => _topology.LogicalProcessors.Select(p => p.OsIndex),

            // 仅物理核：每个物理核保留**编号最小的**那个逻辑处理器。
            // 用 Min 而不是取首个枚举到的，是因为枚举顺序不保证；取确定的下标才能让
            // 同一条规则在多次运行间落在同一批线程上（可复现）。
            CoreAffinityMode.PhysicalOnly => _topology.LogicalProcessors
                .GroupBy(p => p.PhysicalCoreIndex)
                .Select(g => g.Min(p => p.OsIndex)),

            CoreAffinityMode.PerformanceOnly => _topology.LogicalProcessors
                .Where(p => p.CoreClass == CoreClass.Performance)
                .Select(p => p.OsIndex),

            CoreAffinityMode.EfficiencyOnly => _topology.LogicalProcessors
                .Where(p => p.CoreClass == CoreClass.Efficiency)
                .Select(p => p.OsIndex),

            CoreAffinityMode.Custom => EnumerateBits(customMask),

            _ => [],
        };

        ulong bits = 0;
        foreach (var index in osIndices)
        {
            if (index is < 0 or >= MaxSingleGroupProcessors)
                continue;
            bits |= 1UL << index;
        }

        if (bits == 0)
        {
            reason = mode switch
            {
                CoreAffinityMode.PerformanceOnly => "本机没有性能核（P-Core）",
                CoreAffinityMode.EfficiencyOnly => "本机没有能效核（E-Core）",
                CoreAffinityMode.PhysicalOnly => "拓扑未解析出物理核",
                CoreAffinityMode.Custom => "自定义掩码未配置或为空（至少保留 1 个核心）",
                _ => "拓扑未解析出可用逻辑处理器",
            };
            return false;
        }

        mask = (UIntPtr)bits;
        return true;
    }

    /// <summary>旧签名兼容（无自定义掩码的调用点）。</summary>
    public bool TryBuild(CoreAffinityMode mode, out UIntPtr mask, out string reason) =>
        TryBuild(mode, 0, out mask, out reason);

    private static IEnumerable<int> EnumerateBits(ulong bits)
    {
        for (var i = 0; i < MaxSingleGroupProcessors; i++)
        {
            if ((bits & (1UL << i)) != 0)
                yield return i;
        }
    }

    /// <summary>把优先级档位映射到 Windows 优先级类常量；None 返回 null。</summary>
    public static uint? ToPriorityClass(ProcessPriorityLevel level) => level switch
    {
        ProcessPriorityLevel.Idle => ProcessOptimizationNative.IDLE_PRIORITY_CLASS,
        ProcessPriorityLevel.BelowNormal => ProcessOptimizationNative.BELOW_NORMAL_PRIORITY_CLASS,
        ProcessPriorityLevel.Normal => ProcessOptimizationNative.NORMAL_PRIORITY_CLASS,
        ProcessPriorityLevel.AboveNormal => ProcessOptimizationNative.ABOVE_NORMAL_PRIORITY_CLASS,
        ProcessPriorityLevel.High => ProcessOptimizationNative.HIGH_PRIORITY_CLASS,
        _ => null,
    };
}
