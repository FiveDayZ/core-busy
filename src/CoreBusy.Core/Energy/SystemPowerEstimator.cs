namespace CoreBusy.Core.Energy;

using CoreBusy.Core.Models;

/// <summary>
/// 整机功耗的**逐部件**估算模型（v1.20.1）。
/// <para>
/// <b>方法学。</b>整机功率 = 各部件之和，每一项都遵循「<b>先实测、读不到再按型号估算</b>」：
/// </para>
/// <list type="table">
///   <item>
///     <term>CPU</term><description>实测封装功率（唯一可靠的功率传感器）。读不到 → 整条估算不成立，
///     界面显示 "-"（理由见下）。</description>
///   </item>
///   <item>
///     <term>显卡</term><description>独显先读功率传感器（NVML/ADL → LHM <c>SensorType.Power</c>），
///     读不到则按型号额定功率 × 负载估算；核显**不额外计**（与 CPU 同封装，功耗已含在封装读数内）。
///     多独显按块求和；0 W 是合法读数（Optimus 笔记本的独显会被下电），不当"读不到"处理。</description>
///   </item>
///   <item>
///     <term>内存</term><description>消费级平台**没有任何内存功耗传感器**，恒为型号模型：
///     容量 × 每 GB 系数 × 代际系数 × 占用率。</description>
///   </item>
///   <item>
///     <term>硬盘</term><description>同样无功率传感器，但介质类型（NVMe / SATA SSD / HDD）与
///     实时吞吐/忙率可读 → 型号定档 + 实测活动度在档内插值。</description>
///   </item>
///   <item>
///     <term>风扇 / 主板</term><description>风扇数（转速 &gt; 0 的传感器计数）为实测；
///     每风扇功耗与主板固定开销为常数。</description>
///   </item>
/// </list>
/// <para>
/// <b>为什么不是 v1.20.0 的「整机 ≈ k×CPU封装 + b」。</b>那个模型只有一个输入（CPU 封装功耗），
/// 于是把「CPU 之外的一切」都塞进常数项与斜率，形式上简单，但有一个致命漏洞：
/// <b>独显功耗根本不随 CPU 封装功耗变化</b> —— 一台 170 W 独显的机器在 CPU 空闲时
/// 真实墙上功率可能 60 W，而该模型只会给出 1.35×25+5 ≈ 39 W；玩游戏时独显满载而 CPU 未必满载，
/// 误差进一步放大。用户据此判定"整机的算法不对"，并要求改为逐部件：这是正确的方向，
/// 且与业界做法一致（Cloud Carbon Footprint / Cloud Jewels 对 CPU、内存、SSD、HDD、网络
/// 各配独立系数；PowerProbe / WattSeal 亦按部件求和）。
/// </para>
/// <para>
/// <b>为什么 CPU 读不到时整条不成立（而不是按 TDP × 负载估一个）。</b>三个理由：
/// ① 没有可靠的 SKU→TDP 数据源（Intel 侧要 MSR PL1/PL2，同样依赖 Ring0 驱动，
/// 与封装功耗同生共死）；② 用负载百分比 × 一个猜来的 TDP 得到的数会与左侧 CPU 一路
/// 自相矛盾（左边 "-"、右边却有个数）；③ 本工具全线约定「读不到就是 "-"，绝不拿 0 冒充」，
/// 而一个"由猜的 TDP 推出来的整机功率"比 0 更难被用户识破。故保持两路同起同停。
/// </para>
/// <para>
/// <b>已知局限（不做掩饰）：</b>
/// </para>
/// <list type="bullet">
///   <item>只到"各部件直流功耗之和"这一层：<b>VRM 转换损耗、电源自身损耗、显示器/外设均不含</b>。
///     因此它通常低于墙上功率计读数，差额恰是 <see cref="SystemPowerSettings.Calibration"/>
///     存在的意义（拿功率计校准一次即可）。</item>
///   <item>内存与硬盘的系数是**公开规格 + 评测典型值**，不是本机实测；同型号不同批次/不同厂家
///     可有明显偏差。</item>
///   <item>显卡额定功率表未收录的老卡/专业卡回落兜底功率；型号表对笔记本（TGP 由 OEM 定）
///     按统一比例折算，属量级估计。</item>
///   <item>多盘机器只取"最忙那块盘"的活动度来驱动整个存储项的摆幅（无法把吞吐归因到单块盘）。
///     空闲项按盘数计，摆幅只加一份 —— 宁可少算一点，也不重复计。</item>
///   <item>核显卡在封装读数之内的前提，对 AMD APU（SMU 报整颗 SoC）与 Intel
///     （CPU Package 含核显）都成立；若某平台只报核心域不含核显，则本模型会少算几瓦。</item>
///   <item>多独显时副卡的负载比例读不到，按主卡代算；混合场景（一块有功率传感器、一块没有）
///     只采信有读数的那些，会少计无传感器的那块。</item>
/// </list>
/// <para>
/// <b>界面责任。</b>估算值必须始终带「≈」前缀，提示里逐项列出实测/模型身份与所用系数 ——
/// 绝不让估算值伪装成实测值（与「NaN 一律降级 -，绝不拿 0 冒充」同一条原则）。
/// </para>
/// </summary>
public static class SystemPowerEstimator
{
    // ── 可校准系数的默认值与合法域 ────────────────────────────────────────
    //    默认值的来源：能引用的就引用，不能引用的就用评测典型值并在注释里写明。

    /// <summary>整机总量校准乘数默认值（1.0 = 不校准）。</summary>
    public const double DefaultCalibration = 1.0;

    /// <summary>校准乘数下界。低于 0.3 说明填错了（不是"这台机器特别省电"）。</summary>
    public const double MinCalibration = 0.3;

    /// <summary>校准乘数上界。高于 3 说明填错了。</summary>
    public const double MaxCalibration = 3.0;

    /// <summary>
    /// 主板/芯片组/网卡/音频/USB 供电/供电空载损耗的固定开销（W）。
    /// <para>
    /// 取值 10 W 的依据：PowerProbe 的台式机模型给主板固定项 15 W（含风扇，故本模型拆出风扇后取更小值）；
    /// 迷你主机（本机 HX90 这类）的主板侧开销明显更低。取 10 W 作为两者之间的中值，
    /// 且它是**唯一一个不随任何输入变化的纯常数项**，用户拿功率计校准时最容易发现偏差。
    /// </para>
    /// </summary>
    public const double DefaultBoardWatts = 10.0;

    /// <summary>主板固定开销上界（W）。超过 100 W 的多半是把别的部件也塞进来了。</summary>
    public const double MaxBoardWatts = 100.0;

    /// <summary>
    /// 每个**转动中**风扇的功耗（W）。取 2 W：120 mm 机箱风扇满转约 1.5 W、
    /// CPU 散热器风扇满转约 2–3 W，取整为 2 W。
    /// <para>
    /// 只有"转速 &gt; 0 的风扇个数"是实测的，转速本身不换算功率 ——
    /// 风扇的 P–Q 曲线随型号差别太大，用转速反推功率会比取常数更不准。
    /// </para>
    /// </summary>
    public const double DefaultFanWatts = 2.0;

    /// <summary>单风扇功耗上界（W）。</summary>
    public const double MaxFanWatts = 20.0;

    /// <summary>
    /// 独显无功率传感器且型号不在表内时的额定功率兜底（W）。取 150 W：
    /// 覆盖主流中端独显（RTX 3060 170 W / RX 6600 132 W / RTX 4060 115 W 一线）的量级，
    /// 明显优于"不计"，也不会把核显机算爆（核显根本不走这条路径）。
    /// </summary>
    public const double DefaultGpuFallbackWatts = 150.0;

    /// <summary>兜底额定功率上界（W）。</summary>
    public const double MaxGpuFallbackWatts = 600.0;

    /// <summary>
    /// 内存每 GB 的**满载**功耗系数（W/GB）。取 0.392：这是 Cloud Carbon Footprint /
    /// Cloud Jewels 方法学公开的内存系数（0.000392 kWh per GB·h），也是本模型里唯一
    /// 能引用到具体公开来源的内存系数。
    /// </summary>
    public const double DefaultDramWattsPerGb = 0.392;

    /// <summary>内存系数的合法上界（W/GB）。</summary>
    public const double MaxDramWattsPerGb = 2.0;

    // ── 合理域守卫（越界的输入一律视为"读不到"，而不是照单全收）────────────

    /// <summary>CPU 封装功耗上界（W）。超过它几乎必然是单位错误。</summary>
    public const double MaxCpuWatt = 1000.0;

    /// <summary>显卡功耗上界（W）。含 H100 这类 700 W 级加速卡仍有余量。</summary>
    public const double MaxGpuWatt = 1000.0;

    /// <summary>物理内存总量上界（GB）。超过它说明读到的不是本机物理内存。</summary>
    public const double MaxMemoryGb = 2048.0;

    /// <summary>风扇计数上界。超过它说明把虚构/重复的传感器也算进来了。</summary>
    public const int MaxFanCount = 32;

    /// <summary>
    /// 存储吞吐上界（MB/s）。消费级存储的上限在 14000 MB/s（PCIe 5.0 x4 顺序读）量级，
    /// 取 100000 作守卫是拦"单位错误"（若把字节当 GB，差值会大 9 个数量级）。
    /// </summary>
    public const double MaxPlausibleThroughputMbps = 100_000.0;

    /// <summary>
    /// 吞吐口径与忙率口径**互检**的容忍倍数：两者相差超过这个倍数就采信忙率（v1.21.0）。
    /// <para>
    /// 吞吐是带单位的实测量，忙率是无量纲百分比。采集层的吞吐单位在不同驱动上并不统一
    /// （本机实测 NVMe 的 LHM <c>Throughput</c> 传感器是 bytes/s，而它名义上是 MB/s，
    /// 见 <c>LibreHardwareMonitorSensorService.ToMegabytesPerSecond</c>），
    /// 而百分比没有这种歧义 —— 所以量级对不上时以忙率为准。
    /// </para>
    /// <para>
    /// 阈值取 5 倍而不是更紧：忙率含寻道/协议开销，与「吞吐 ÷ 参考带宽」本就不是同一个量。
    /// 实测同一段 640 MB 写入里两者相差约 1.8 倍（0.114 vs 0.064），属正常分歧；
    /// 而单位错的偏差是 10^6 量级，5 倍足以分开这两类。**代价**：真实的"高吞吐低忙率"
    /// （例如大块顺序读）会被忙率低估 —— 二者都在同一档位内，误差不超过存储项的摆幅（6.8 W）。
    /// </para>
    /// </summary>
    public const double StorageCaliberDisagreementFactor = 5.0;

    // ── 模型内部的形态常数 ──────────────────────────────────────────────

    /// <summary>内存空闲功耗占满载系数的比例（空闲 ≈ 15% 满载）。</summary>
    public const double DramIdleFraction = 0.15;

    /// <summary>
    /// 显卡空闲功耗占额定功率的比例。独显待机（无 3D 负载但显存与供电在线）实测普遍在
    /// 额定功率的 10–15%（如 170 W 的 RTX 3060 桌面待机约 18–25 W），取 0.12。
    /// </summary>
    public const double GpuIdleFraction = 0.12;

    /// <summary>
    /// 判定"功率 0 与占用率矛盾"的占用率门槛（0–1 比例）。占用低于它时 0 W 与
    /// "卡在下电/深度省电"相容，采信为真读数；高于它而功率仍报 0，则该 0 是失效读数。
    /// </summary>
    private const double GpuLoadContradictionThreshold = 0.10;

    /// <summary>吞吐 → 存储项摆幅的参考带宽（MB/s）：到达该带宽即认为功率摆幅已基本实现。</summary>
    private const double NvmeReferenceMbps = 1000.0;

    /// <inheritdoc cref="NvmeReferenceMbps"/>
    private const double SataSsdReferenceMbps = 450.0;

    /// <inheritdoc cref="NvmeReferenceMbps"/>
    private const double HddReferenceMbps = 150.0;

    /// <summary>介质未知时的参考带宽（MB/s）。</summary>
    private const double UnknownReferenceMbps = 500.0;

    /// <summary>
    /// 估算总量的上界（W）。超出即**拒绝出数**（返回 NaN，界面显示 "-"），
    /// 而不是显示一个离谱的数。
    /// <para>
    /// 必须与 <c>CumulativeEnergyTracker</c> 的合理性闸取同一个值：若估算值能越过闸门，
    /// 那些样例会**被静默丢弃**，表现为"整机能耗覆盖率莫名低于 CPU 能耗"——
    /// 一个只看界面根本查不出来的分叉。两侧同源于本常量即为该不变量的实现方式。
    /// </para>
    /// </summary>
    public const double MaxPlausibleWatt = 2000.0;

    /// <summary>
    /// 估算整机功率（W），返回逐部件分解。
    /// <para>
    /// CPU 封装功耗读不到（NaN/非正/越界）时返回 <see cref="SystemPowerBreakdown.Unavailable"/> ——
    /// 上层据此走"读不到"而不是"0 W"的降级路径。
    /// </para>
    /// </summary>
    public static SystemPowerBreakdown Estimate(SystemPowerInputs inputs, SystemPowerSettings settings)
    {
        var cpu = inputs.CpuPackageWatt ?? double.NaN;
        if (!double.IsFinite(cpu) || cpu <= 0 || cpu > MaxCpuWatt)
            return SystemPowerBreakdown.Unavailable;

        var (gpuWatt, gpuSource, gpuRated, gpuLoad) = EstimateGpu(inputs, settings);
        var dram = EstimateMemory(inputs, settings);
        var storage = EstimateStorage(inputs);

        var board = NormalizeBoard(settings.BoardWatts);
        var fans = NormalizeFan(settings.FanWatts) * Math.Clamp(inputs.ActiveFanCount, 0, MaxFanCount);

        var total = (cpu + gpuWatt + dram + storage + board + fans) * NormalizeCalibration(settings.Calibration);

        // 越界不静默截断也不出数：分解照常带出（供提示里说明是哪一项异常），总量置 NaN。
        if (!double.IsFinite(total) || total <= 0 || total > MaxPlausibleWatt)
        {
            return new SystemPowerBreakdown(
                double.NaN, cpu, gpuWatt, dram, storage, board, fans, gpuSource, gpuRated, gpuLoad);
        }

        return new SystemPowerBreakdown(
            total, cpu, gpuWatt, dram, storage, board, fans, gpuSource, gpuRated, gpuLoad);
    }

    // ---------------------------------------------------------------- 显卡

    /// <summary>
    /// 显卡项，返回 (功率, 来源, 生效额定功率, 负载)。三级判定：
    /// <list type="number">
    ///   <item>有独显名单 → 先取**实测之和**（NVML/ADL；0 W 是合法的下电读数），
    ///         读不到则逐块按型号额定功率 × 负载求估算之和；</item>
    ///   <item>无独显名单且主卡判别为核显 → 0 W，来源标 <see cref="GpuPowerSource.IntegratedInPackage"/>
    ///         （功耗已在封装读数内）；</item>
    ///   <item>无独显名单且主卡型号判别不出 → 0 W 且来源标 Unavailable，
    ///         提示里必须说明"未计入"—— 静默当成核显等于替用户断言一件我们并不知道的事。</item>
    /// </list>
    /// <para>
    /// 多独显（含双卡）按块求和：实测和优先，模型路径下每块各取自己的额定功率，
    /// 但**共用主卡的负载比例**（副卡的实际负载读不到，按主卡代算是保守近似，
    /// 记为已知局限）。
    /// </para>
    /// </summary>
    private static (double Watt, GpuPowerSource Source, double? Rated, double? Load) EstimateGpu(
        SystemPowerInputs inputs, SystemPowerSettings settings)
    {
        var load = inputs.GpuLoadPercent;
        var names = inputs.DiscreteGpuNames;

        if (names.Count == 0)
        {
            if (!inputs.HasGpu)
                return (0, GpuPowerSource.Unavailable, null, load);

            var mainRole = GpuRatedPower.Classify(inputs.GpuName);
            return mainRole == GpuRole.Integrated
                ? (0, GpuPowerSource.IntegratedInPackage, null, load)
                : (0, GpuPowerSource.Unavailable, null, load);
        }

        // ① 实测优先：全部独显之和。0 W 必须接受（独显下电是真实状态）；
        //    上界按卡数放大，避免"两块 700 W 卡"这种合法组合被守卫误伤。
        //
        //    "报 0 而占用率明显不为 0"是自相矛盾的坏读数（个别驱动会这样），此时 0 不是实测而是失效值，
        //    必须转型号估算。这条守卫放在**模型层**而不是采集层：模型是"什么算可用读数"的唯一定义处，
        //    否则换个调用方（测试、mock、将来的别的采集实现）就会绕过它，而绕过它的后果是
        //    整机功耗凭空少算一块显卡，界面上完全看不出来。
        var loaded = NormalizeLoad(load) >= GpuLoadContradictionThreshold;
        var measuredLimit = MaxGpuWatt * names.Count;
        if (inputs.GpuMeasuredWatt is { } measured
            && double.IsFinite(measured) && measured >= 0 && measured <= measuredLimit
            && !(measured <= 0 && loaded))
        {
            return (measured, GpuPowerSource.Measured, null, load);
        }

        // ② 型号估算：逐块额定功率 × 负载曲线。
        var fallback = NormalizeGpuFallback(settings.GpuFallbackWatts);
        var ramp = GpuIdleFraction + (1 - GpuIdleFraction) * NormalizeLoad(load);

        var total = 0.0;
        double? firstRated = null;

        foreach (var name in names)
        {
            var rated = GpuRatedPower.ResolveRatedWatt(name) ?? fallback;
            if (rated <= 0)
                continue;

            firstRated ??= rated;
            total += rated * ramp;
        }

        return total > 0
            ? (total, GpuPowerSource.RatedByModel, firstRated, load)
            : (0, GpuPowerSource.Unavailable, null, load);
    }

    // ---------------------------------------------------------------- 内存

    /// <summary>
    /// 内存项（W）：容量 × 每 GB 满载系数 × 代际系数 × (空闲比 + 剩余比 × 占用率)。
    /// 消费级平台没有内存功耗传感器，故恒为模型值。
    /// </summary>
    private static double EstimateMemory(SystemPowerInputs inputs, SystemPowerSettings settings)
    {
        var gb = inputs.MemoryTotalGb;
        if (!double.IsFinite(gb) || gb <= 0 || gb > MaxMemoryGb)
            return 0;

        var perGb = NormalizeDramPerGb(settings.DramWattsPerGb) * MemoryGenerationFactor(inputs.MemoryGeneration);
        var load = NormalizeLoad(inputs.MemoryLoadPercent);

        return gb * perGb * (DramIdleFraction + (1 - DramIdleFraction) * load);
    }

    /// <summary>代际系数：以 DDR4 为 1.0，其余按电压/每 GB 功耗的公开规格相对取整。</summary>
    private static double MemoryGenerationFactor(MemoryGeneration generation) => generation switch
    {
        MemoryGeneration.Ddr3 => 0.85,
        MemoryGeneration.Ddr4 => 1.0,
        MemoryGeneration.Ddr5 => 1.35,
        MemoryGeneration.LpDdr4 => 0.70,
        MemoryGeneration.LpDdr5 => 0.75,
        _ => 1.0, // Unknown：按 DDR4 处理（最普遍），但"未知"不影响它本来就是模型值这一身份。
    };

    // ---------------------------------------------------------------- 硬盘

    /// <summary>
    /// 存储项（W）：逐盘累加空闲功率 + 摆幅 × 活动度。
    /// <para>
    /// 空闲是**逐盘**的（每块盘都在通电，且机械盘的空闲比 NVMe 高一个档，混合盘机不能只取一种介质）；
    /// 摆幅只加一份，取"本机出现的介质里摆幅最大的那一种"—— 吞吐无法归因到单块盘，
    /// 用最大摆幅可以避免"最忙的恰好是机械盘"时严重低估，同时也不会按盘数重复计。
    /// </para>
    /// </summary>
    private static double EstimateStorage(SystemPowerInputs inputs)
    {
        var count = inputs.DriveCount;
        if (count <= 0)
            return 0;

        var kinds = inputs.StorageKinds;
        var idle = 0.0;
        var swing = 0.0;
        var known = 0;

        foreach (var kind in kinds)
        {
            var (kindIdle, kindActive) = StorageWatts(kind);
            idle += kindIdle;
            swing = Math.Max(swing, kindActive - kindIdle);
            known++;
        }

        // 介质读不出来的盘：按"未知"档补齐（不是跳过 —— 跳过等于假设它不耗电）。
        var unknown = Math.Max(0, count - known);
        if (unknown > 0)
        {
            var (unknownIdle, unknownActive) = StorageWatts(StorageMediaKind.Unknown);
            idle += unknown * unknownIdle;
            swing = Math.Max(swing, unknownActive - unknownIdle);
        }

        return idle + swing * StorageActivityRatio(inputs, DominantKind(kinds));
    }

    /// <summary>本机介质里摆幅最大的那一种（用于把吞吐换算成活动度时的参考带宽）。</summary>
    private static StorageMediaKind DominantKind(IReadOnlyList<StorageMediaKind> kinds)
    {
        var kind = StorageMediaKind.Unknown;
        var best = 0.0;

        foreach (var candidate in kinds)
        {
            var (idle, active) = StorageWatts(candidate);
            if (active - idle > best)
            {
                best = active - idle;
                kind = candidate;
            }
        }

        return kind;
    }

    /// <summary>
    /// 各介质类型的（空闲 W，满载 W）。数值来自消费级盘的评测典型值：
    /// NVMe 无 ASPM 时待机 1–2 W、顺序读满速 6–8 W；SATA SSD 待机 0.5–0.7 W、满载 3–4 W；
    /// 3.5" 机械盘通电待机 4–5 W、寻道/顺序读写 8–9 W。
    /// </summary>
    private static (double Idle, double Active) StorageWatts(StorageMediaKind kind) => kind switch
    {
        StorageMediaKind.Nvme => (1.2, 8.0),
        StorageMediaKind.SataSsd => (0.6, 3.5),
        StorageMediaKind.Hdd => (4.5, 8.5),
        _ => (0.8, 5.0), // 未知：取 SSD 与机械盘之间，偏向保守。
    };

    private static double ReferenceMbps(StorageMediaKind kind) => kind switch
    {
        StorageMediaKind.Nvme => NvmeReferenceMbps,
        StorageMediaKind.SataSsd => SataSsdReferenceMbps,
        StorageMediaKind.Hdd => HddReferenceMbps,
        _ => UnknownReferenceMbps,
    };

    /// <summary>
    /// 存储活动度（0–1）。两个口径依次回落：吞吐（MB/s ÷ 介质参考带宽）→ 忙率百分比。
    /// <para>
    /// <b>刻意不用 LHM 的 <c>Total Activity</c>：</b>本机实测该传感器在读写活动都约为 0 时
    /// 仍报告 99.999985（见取证文件），用它会把一块空闲盘常年算成满载 +8 W。
    /// 只采信 <c>Read Activity</c>/<c>Write Activity</c> 这对分项（两者取大者），
    /// 且吞吐可读时优先用吞吐（物理量更直接，也避开了忙率定义在不同驱动上的歧义）。
    /// </para>
    /// <para>两个口径都读不到时按空闲计（保守），提示里会注明活动度不可读。</para>
    /// </summary>
    private static double StorageActivityRatio(SystemPowerInputs inputs, StorageMediaKind kind)
    {
        // 显式写成 double?：三元表达式两边是 double 与 null，推断不出可空类型（CS0173）。
        double? byBusy = inputs.StorageBusyPercent is { } busy && double.IsFinite(busy)
            ? Math.Clamp(busy / 100.0, 0, 1)
            : null;

        if (inputs.StorageThroughputMbps is { } mbps
            && double.IsFinite(mbps) && mbps >= 0 && mbps <= MaxPlausibleThroughputMbps)
        {
            var byThroughput = Math.Clamp(mbps / ReferenceMbps(kind), 0, 1);

            // 两个口径互检（v1.21.0）：见 StorageCaliberDisagreementFactor。
            // 加这一层是因为**量级错的吞吐能穿过绝对闸门**：本机实测一条空闲盘的
            // Throughput 读数是 86,968（bytes/s 被当 MB/s），落在 [0, 100000] 之内，
            // 于是被 clamp 成满载 —— 硬盘项凭空 +6.8 W，且时有时无。
            if (byBusy is { } busyRatio)
            {
                var high = Math.Max(byThroughput, busyRatio);
                var low = Math.Min(byThroughput, busyRatio);
                if (low <= 0 || high > low * StorageCaliberDisagreementFactor)
                    return busyRatio;
            }

            return byThroughput;
        }

        return byBusy ?? 0;
    }

    // ---------------------------------------------------------------- 归一化

    /// <summary>负载百分比 → 0–1；读不到（null）按空闲（0）处理。</summary>
    private static double NormalizeLoad(double? percent)
        => percent is { } value && double.IsFinite(value) ? Math.Clamp(value / 100.0, 0, 1) : 0;

    /// <summary>校准乘数收进合法区间；越界或非有限一律**回落默认值**（见 <see cref="SystemPowerSettings"/> 的说明）。</summary>
    public static double NormalizeCalibration(double value)
        => double.IsFinite(value) && value >= MinCalibration && value <= MaxCalibration ? value : DefaultCalibration;

    /// <summary>主板固定开销收进合法区间，规则同 <see cref="NormalizeCalibration"/>。</summary>
    public static double NormalizeBoard(double value)
        => double.IsFinite(value) && value >= 0 && value <= MaxBoardWatts ? value : DefaultBoardWatts;

    /// <summary>单风扇功耗收进合法区间，规则同 <see cref="NormalizeCalibration"/>。</summary>
    public static double NormalizeFan(double value)
        => double.IsFinite(value) && value >= 0 && value <= MaxFanWatts ? value : DefaultFanWatts;

    /// <summary>显卡兜底额定功率收进合法区间，规则同 <see cref="NormalizeCalibration"/>。</summary>
    public static double NormalizeGpuFallback(double value)
        => double.IsFinite(value) && value >= 0 && value <= MaxGpuFallbackWatts
            ? value
            : DefaultGpuFallbackWatts;

    /// <summary>内存每 GB 系数收进合法区间，规则同 <see cref="NormalizeCalibration"/>。</summary>
    public static double NormalizeDramPerGb(double value)
        => double.IsFinite(value) && value >= 0 && value <= MaxDramWattsPerGb ? value : DefaultDramWattsPerGb;
}
