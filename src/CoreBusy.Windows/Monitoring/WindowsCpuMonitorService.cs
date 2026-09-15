namespace CoreBusy.Windows.Monitoring;

using System.Diagnostics;
using CoreBusy.Core;
using CoreBusy.Core.Interfaces;
using CoreBusy.Core.Models;
using CoreBusy.Windows.PerformanceCounter;
using CoreBusy.Windows.Topology;
using CoreBusy.Windows.Wmi;

/// <summary>
/// 真实设备 CPU 监控服务：组装拓扑识别（GetLogicalProcessorInformationEx）、
/// 每逻辑处理器占用率（Performance Counter）、传感器（LibreHardwareMonitorLib：温度/功耗/频率/风扇）、
/// 进程排行（CPU 时间增量）与 ACPI 温度兜底。
/// 内部以 1 秒周期在后台线程采样，UI 通过 ICpuMonitorService 读取最新快照。
/// 温度/功耗依赖管理员权限加载内核驱动；无权限时温度降级为 ACPI 温区，功耗/风扇显示为缺省。
/// </summary>
public sealed class WindowsCpuMonitorService : ICpuMonitorService, IDisposable
{
    private const string PerfCategory = "Processor Information";
    private const string PerfPerformanceCounter = "% Processor Performance";

    private readonly IHardwareSensorService _sensors;
    private readonly AcpiTemperatureFallback _acpi = new();
    private readonly AsusWmiFanReader _asusFan = new();
    private readonly ProcessCpuRanker _ranker;
    private readonly PerCoreUsageProvider? _usageProvider;
    private readonly System.Diagnostics.PerformanceCounter?[] _perfPerf;
    private readonly Thread _sampler;
    private readonly ManualResetEventSlim _stopEvent = new();
    private readonly object _gate = new();

    // 两者都可能被采样线程首帧的 MSR 基频修正（RefineBaseClockWithMsr）改写，故不设 readonly。
    // _info 的跨线程访问只发生在整体引用替换（原子），GetCpuInfo 读取安全。
    private CpuInfo _info;
    private double _baseClockMhz;
    private bool _baseClockRefined;
    private readonly CoreLayoutMode _layout;
    private readonly List<CoreGroup> _cores;
    private readonly List<CpuCoreGroup> _coreGroups;

    // 采样状态（_gate 保护）。
    private double[] _logicalUsage = [];
    private double? _packageTemp;
    private double? _packagePower;
    private double? _fanRpm;
    private double? _gpuFanRpm;
    private string _fanDetail = string.Empty;
    private double? _gpuTemp;
    private double? _gpuUsage;
    private double? _driveTemp;
    private IReadOnlyList<DriveTemperatureReading> _driveTemps = [];
    private double _peakTemp = double.NaN;
    private DateTime _peakTempTime = DateTime.Now;
    private double[] _coreUsage = [];
    private double[] _coreFreqGhz = [];

    /// <summary>逐核**有效频率**（GHz，驻留加权，v1.18.0）。健康判定只认这一列。</summary>
    private double[] _coreEffectiveGhz = [];

    /// <summary>逐核硬件活跃度（%，v1.18.0）= 有效频率 ÷ 本机最高加速档频率。</summary>
    private double[] _coreActivity = [];

    /// <summary>逐核电压（V，v1.18.0）。读不到为 NaN，只作展示与存档。</summary>
    private double[] _coreVoltage = [];

    /// <summary>逐核显示频率的来源（v1.18.0），用于界面/日志标注。</summary>
    private CoreFrequencySource[] _coreFreqSource = [];

    /// <summary>
    /// 本次会话观测到的最高 P-state 频率（MHz），作为硬件活跃度的分母。
    /// 运行取最大值而不是取标称 SKU 值：OEM 调整最大处理器状态后，标称值会算出一个恒偏的比值。
    /// </summary>
    private double _maxCoreClockMhz;

    /// <summary>频率来源是否已落日志（v1.18.0）—— 只在首次采样后写一行，避免刷屏。</summary>
    private bool _frequencySourceLogged;
    private List<ProcessLoadSample> _topProcesses = [];

    // 运行时可调配置（设置窗口 / 性能模式）：volatile 保证采样线程立即可见。
    private volatile bool _sensorsEnabled = true;
    private volatile int _sampleIntervalMs = 1000;

    public WindowsCpuMonitorService(IHardwareSensorService sensors, CoreLayoutMode layout = CoreLayoutMode.Spec)
    {
        _sensors = sensors;
        _layout = layout;

        var baseInfo = new WmiCpuInfoService()
            .GetCpuInfoAsync()
            .GetAwaiter()
            .GetResult();

        var topology = new LogicalProcessorTopologyService().GetTopology();
        _baseClockMhz = double.IsNaN(baseInfo.BaseClockGHz) ? 0 : baseInfo.BaseClockGHz * 1000;

        // 厂商：优先用 WMI 映射结果，兜底用原始字符串判定（规范 §49）。
        var vendor = baseInfo.VendorKind != CpuVendor.Unknown
            ? baseInfo.VendorKind
            : baseInfo.Vendor.ToCpuVendor();

        var logicalTotal = Math.Max(1, Math.Max(baseInfo.LogicalProcessors, topology.LogicalProcessors.Count));
        var physical = BuildPhysicalCores(topology, logicalTotal);
        var hybrid = physical.Any(c => c.Class == CoreClass.Efficiency);

        // CCD 数（规范 §33）：设计稿口径不启用 CCD 分组，但仍如实记录以供状态/日志核对。
        var ccdCount = ResolveCcdCount(vendor, physical.Count);

        List<CpuCoreGroup> groups;
        if (_layout == CoreLayoutMode.Draft)
        {
            // 设计稿口径（v1.5.0 一比一还原）：按逻辑处理器逐线程成 Tile；
            // 非混合架构前后对半切成 P 段 / E 段，与 16 行热力图（P0-P7 / E0-E7）一致。
            (_cores, groups) = BuildDraftLayout(physical, hybrid, logicalTotal);
        }
        else
        {
            // 规范口径（§20/§32/§33/§52）：Intel 混合 → P/E 两段；
            // 非混合 Intel 与 AMD → 单段 CPU Core；多 CCD Ryzen → CCD 0..N。
            (_cores, groups) = BuildSpecLayout(ccdCount, physical);
        }

        _coreGroups = groups;

        // 计数由分段元数据推导（P/E 段存在时为其真实值；单段平台为 0）。
        // 设计稿非混合口径下 P 段标注整机规格（N 核 / 2N 线程），由分段元数据自带，避免两处口径打架。
        var perfSections = _coreGroups.Where(g => g.Kind == CoreGroupKind.Performance).ToList();
        var effSections = _coreGroups.Where(g => g.Kind == CoreGroupKind.Efficiency).ToList();

        // 物理核数：设计稿非混合口径沿用"逻辑线程对半"（v1.5.0），其余为真实解析值。
        var physicalCount = _layout == CoreLayoutMode.Draft && !hybrid
            ? Math.Max(1, logicalTotal / 2)
            : physical.Count;

        _info = baseInfo with
        {
            VendorKind = vendor,
            PhysicalCores = physicalCount,
            LogicalProcessors = logicalTotal,
            CcdCount = ccdCount,
            PerformanceCoreCount = perfSections.Sum(g => g.CoreCount),
            PerformanceThreadCount = perfSections.Sum(g => g.ThreadCount),
            EfficiencyCoreCount = effSections.Sum(g => g.CoreCount),
            EfficiencyThreadCount = effSections.Sum(g => g.ThreadCount),
        };

        _usageProvider = new PerCoreUsageProvider(_info.LogicalProcessors);
        _perfPerf = BuildPerfPerformanceCounters(_info.LogicalProcessors);
        _ranker = new ProcessCpuRanker(_info.LogicalProcessors);

        _logicalUsage = new double[_info.LogicalProcessors];
        _coreUsage = new double[_cores.Count];
        _coreFreqGhz = new double[_cores.Count];
        _coreEffectiveGhz = new double[_cores.Count];
        _coreActivity = new double[_cores.Count];
        _coreVoltage = new double[_cores.Count];
        _coreFreqSource = new CoreFrequencySource[_cores.Count];

        for (var i = 0; i < _cores.Count; i++)
        {
            _coreEffectiveGhz[i] = double.NaN;
            _coreActivity[i] = double.NaN;
            _coreVoltage[i] = double.NaN;
        }

        _sampler = new Thread(SamplerLoop)
        {
            IsBackground = true,
            Name = "corebusy-sampler",
        };
        _sampler.Start();
    }

    /// <inheritdoc />
    public TimeSpan SampleInterval
    {
        get => TimeSpan.FromMilliseconds(_sampleIntervalMs);
        set => _sampleIntervalMs = (int)Math.Clamp(value.TotalMilliseconds, 200, 5000);
    }

    /// <inheritdoc />
    public bool SensorsEnabled
    {
        get => _sensorsEnabled;
        set => _sensorsEnabled = value;
    }

    /// <inheritdoc />
    public CpuInfo GetCpuInfo() => _info;

    /// <inheritdoc />
    public CpuSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            var total = _logicalUsage.Length > 0
                ? Math.Clamp(_logicalUsage.Average(), 0, 100)
                : 0;

            var freqValues = _coreFreqGhz.Where(f => !double.IsNaN(f)).ToArray();
            var avgFreq = freqValues.Length > 0 ? freqValues.Average() : double.NaN;

            return new CpuSnapshot
            {
                TotalUsagePercent = total,
                PackageTemperatureC = _packageTemp ?? double.NaN,
                PeakTemperatureC = _peakTemp,
                PeakTemperatureTime = _peakTempTime,
                PackagePowerW = _packagePower ?? double.NaN,
                AverageFrequencyGHz = avgFreq,
                FanRpm = _fanRpm ?? double.NaN,
                GpuFanRpm = _gpuFanRpm ?? double.NaN,
                FanDetail = _fanDetail,
                GpuTemperatureC = _gpuTemp ?? double.NaN,
                GpuUtilizationPercent = _gpuUsage ?? double.NaN,
                DriveTemperatureC = _driveTemp ?? double.NaN,
                TopProcesses = _topProcesses,
            };
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<CpuCoreSnapshot> GetCoreSnapshots()
    {
        lock (_gate)
        {
            var result = new List<CpuCoreSnapshot>(_cores.Count);
            for (var i = 0; i < _cores.Count; i++)
            {
                var core = _cores[i];
                var usage = Math.Clamp(_coreUsage[i], 0, 100);
                var osIndexes = core.OsIndexes;

                // 线程明细：UI 的 Tile 内线程条与热力图线程行都取自这一份数据，
                // 保证"标称 8 核 / 16 线程"的 16 条线程在界面上逐条可见。
                var threads = new CpuThreadSnapshot[osIndexes.Length];
                for (var t = 0; t < osIndexes.Length; t++)
                {
                    var osIndex = osIndexes[t];
                    var threadUsage = osIndex < _logicalUsage.Length && _logicalUsage.Length > 0
                        ? Math.Clamp(_logicalUsage[osIndex], 0, 100)
                        : usage;

                    threads[t] = new CpuThreadSnapshot
                    {
                        // 单线程核与核心同名，多线程核按 "C0·0 / C0·1" 区分。
                        Id = osIndexes.Length > 1 ? $"{core.DisplayName}·{t}" : core.DisplayName,
                        CoreId = core.DisplayName,
                        Index = t,
                        UsagePercent = threadUsage,
                        FrequencyGHz = _coreFreqGhz[i],
                        Status = CoreStatusClassifier.Classify(threadUsage),
                    };
                }

                result.Add(new CpuCoreSnapshot
                {
                    Id = core.DisplayName,
                    Class = core.Class,
                    CcdIndex = core.CcdIndex,
                    UsagePercent = usage,
                    FrequencyGHz = _coreFreqGhz[i],
                    FrequencySource = _coreFreqSource[i],
                    EffectiveFrequencyGHz = _coreEffectiveGhz[i],
                    HardwareActivityPercent = _coreActivity[i],
                    CoreVoltageV = _coreVoltage[i],
                    LogicalProcessors = osIndexes,
                    Status = CoreStatusClassifier.Classify(usage),
                    Threads = threads,
                });
            }

            return result;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<CpuCoreGroup> GetCoreGroups() => _coreGroups;

    public void Dispose()
    {
        _stopEvent.Set();
        try { _sampler.Join(1500); } catch { }
        try { _sensors.Dispose(); } catch { }
        try { _asusFan.Dispose(); } catch { }
        _stopEvent.Dispose();
    }

    // ---------------------------------------------------------------- 后台采样

    /// <summary>
    /// MSR 基频复核（v1.10.4 引入，v1.10.5 修正）：驱动就绪后读 MSR_PLATFORM_INFO(0xCE) 的最大
    /// 非睿频比复核基频，并把 MSR_PKG_POWER_LIMIT(0x610) 的 PL1/PL2 一并写进日志。
    ///
    /// 语义边界（v1.10.5 更正，重要）：0xCE 与 CPUID.16H **同源**——都是芯片按当前平台功耗配置
    /// 自报的非睿频比，两者通常一致；它们**不是** SKU 的"标称基频"。Intel 只在默认 PL1 档位公布
    /// HFM（i3-N305 数据手册 15W 档 = 1.8 GHz），OEM 下调 PL1 后芯片自报值同步下降：实测整机
    /// i3-N305（PL1=10W / PL2=15W）0xCE 比 = 10 → 1.0 GHz，与 CPUID.16H、SMBIOS/WMI、内核
    /// ProcessorInformation、TSC 四路一致。故本层只做"兜底 + 取证"，不引入 SKU 硬编码表——
    /// 基频一律以硬件自报为准，PL1/PL2 入日志以便解释"为什么不是厂商宣传的标称值"。
    /// </summary>
    private void RefineBaseClockWithMsr()
    {
        if (_baseClockRefined)
            return;

        _baseClockRefined = true;

        if (_info.VendorKind != CpuVendor.Intel)
            return;

        IntelBaseClockProbe probe;
        try
        {
            probe = _sensors.ProbeIntelBaseClock();
        }
        catch (Exception ex)
        {
            TopologyLog.Write(
                $"[TOPOLOGY] base clock msr probe threw ({ex.GetType().Name}: {ex.Message}), keep current");
            return;
        }

        if (!probe.Available)
        {
            // 不再把"失败原因未知"糊成 not elevated or unsupported：Note 由传感器层给出可审计原因。
            TopologyLog.Write($"[TOPOLOGY] base clock msr unavailable: {probe.Note}, keep current");
            return;
        }

        TopologyLog.Write(
            $"[TOPOLOGY] base clock msr evidence: base={probe.BaseFrequencyMhz:0} MHz ({probe.Note}) "
            + $"PL1={(double.IsNaN(probe.Pl1W) ? "-" : probe.Pl1W.ToString("0.0") + " W")} "
            + $"PL2={(double.IsNaN(probe.Pl2W) ? "-" : probe.Pl2W.ToString("0.0") + " W")}");

        var mhz = probe.BaseFrequencyMhz;
        var current = _baseClockMhz;
        if (current > 0 && Math.Abs(current - mhz) <= mhz * 0.05)
        {
            TopologyLog.Write($"[TOPOLOGY] base clock msr: {mhz:0} MHz matches current, keep");
            return;
        }

        TopologyLog.Write(
            $"[TOPOLOGY] base clock msr: current={(current > 0 ? current.ToString("0") : "-")} MHz -> {mhz:0} MHz (msr.0xCE)");
        _baseClockMhz = mhz;
        _info = _info with { BaseClockGHz = mhz / 1000.0 };
    }

    private void SamplerLoop()
    {
        try
        {
            // LibreHardwareMonitor 首次枚举并安装内核驱动较慢，放后台线程执行。
            _sensors.Start();
        }
        catch
        {
            // 非管理员会话下驱动加载失败：温度/功耗降级，UI 显示缺省值。
        }

        // 驱动就绪后立即做一次 MSR 基频修正（详见方法注释；仅执行一次）。
        RefineBaseClockWithMsr();

        // PerformanceCounter 首次 NextValue 返回 0，先预热丢弃。
        PrimeCounters();

        while (!_stopEvent.IsSet)
        {
            _stopEvent.Wait(TimeSpan.FromMilliseconds(_sampleIntervalMs));
            if (_stopEvent.IsSet)
                break;

            try
            {
                SampleOnce();
            }
            catch
            {
                // 单次采样失败忽略，下一周期重试。
            }
        }
    }

    private void PrimeCounters()
    {
        try { _usageProvider?.SampleAll(); } catch { }
        SamplePerfPerformance();
    }

    private void SampleOnce()
    {
        double[]? logicalUsage = null;
        try { logicalUsage = _usageProvider?.SampleAll(); } catch { }

        var perf = SamplePerfPerformance();

        // 传感器轮询可被设置/性能模式关闭：此时跳过 LHM 读取与 ACPI 兜底，温度/功耗/风扇降级为缺省。
        var sensorsOn = _sensorsEnabled;
        var sensor = sensorsOn ? ReadSensorSnapshot() : HardwareSensorSnapshot.Empty;

        // 风扇在**锁外**解析（v1.16.3）：ASUS 回退要过一次 WMI 查询，不该占着 _gate 挡 UI 取快照。
        var fan = ResolveFan(sensorsOn, sensor);

        lock (_gate)
        {
            _logicalUsage = logicalUsage ?? _logicalUsage;

            // 每物理核心使用率取其线程最大值（一线程忙即核心忙）。
            for (var i = 0; i < _cores.Count; i++)
            {
                double usage = 0;
                foreach (var osIndex in _cores[i].OsIndexes)
                {
                    if (logicalUsage is not null && osIndex < logicalUsage.Length)
                        usage = Math.Max(usage, logicalUsage[osIndex]);
                }

                _coreUsage[i] = usage;

                // ── 频率（v1.18.0 分两条口径，两者不可混用）──
                //   · 显示口径 _coreFreqGhz：传感器逐核 P-state 时钟 → 计数器比值 → 基频。
                //     Tile 上显示的就是它（离散档位，所有非停放核读到同一个最高档）。
                //   · 判定口径 _coreEffectiveGhz：传感器逐核**有效频率**（驻留加权时间平均）。
                //     它才是"这段时间实际跑多快"，健康评分只认它；取不到就是 NaN，
                //     **绝不拿 P-state 值去冒充** —— 那会让"读不到"变成"读到最高档"，
                //     判分时会得到"人人满分"的假象（v1.17.1 实测到的就是这个）。
                //
                //   索引约定：LHM 的 "Core #N" 为 1 基物理核序号，与 _cores 的自然核序
                //   （按 OS 逻辑处理器升序构建）对齐。这与 v1.17.0 起 CoreClockMhz 的既有映射
                //   完全一致；若某平台两者顺序不一致，症状会是"两颗核的频率互换"，
                //   届时须改为按逻辑处理器索引匹配而不是按序号。
                double mhz = double.NaN;
                var source = CoreFrequencySource.Unknown;
                if (sensor.CoreClockMhz.TryGetValue(i, out var sensorMhz) && sensorMhz > 100)
                {
                    mhz = sensorMhz;
                    source = CoreFrequencySource.SensorPState;
                }

                var firstThread = _cores[i].OsIndexes[0];
                if (double.IsNaN(mhz) && _baseClockMhz > 0
                    && perf is not null && firstThread < perf.Length && perf[firstThread] is { } ratio)
                {
                    mhz = _baseClockMhz * ratio / 100.0;
                    source = CoreFrequencySource.PerfCounterRatio;
                }

                if (double.IsNaN(mhz) && _baseClockMhz > 0)
                {
                    mhz = _baseClockMhz;
                    source = CoreFrequencySource.BaseClock;
                }

                _coreFreqGhz[i] = mhz > 100 ? Math.Round(mhz / 1000.0, 2) : double.NaN;
                _coreFreqSource[i] = source;

                if (sensor.CoreClockMhz.TryGetValue(i, out var pstateMhz) && pstateMhz > _maxCoreClockMhz)
                    _maxCoreClockMhz = pstateMhz;

                var effectiveMhz = double.NaN;
                if (sensor.CoreEffectiveClockMhz.TryGetValue(i, out var sensorEffective) && sensorEffective >= 0)
                    effectiveMhz = sensorEffective;

                _coreEffectiveGhz[i] = effectiveMhz >= 0 ? Math.Round(effectiveMhz / 1000.0, 3) : double.NaN;

                // 硬件活跃度（v1.18.0）：有效频率 ÷ 本机最高加速档频率。
                // 分母取**本次会话观测到的最高 P-state**（本机 4567 MHz），不硬编码 SKU 标称值 ——
                // 那样在 OEM 改了功耗档/最大状态的机器上会算出一个恒偏的比值。
                // 分母尚未建立（首帧）时给 NaN，界面显示 "-"，而不是拿一个 0 去做除法。
                _coreActivity[i] = !double.IsNaN(_coreEffectiveGhz[i]) && _maxCoreClockMhz > 100
                    ? Math.Clamp(_coreEffectiveGhz[i] * 1000.0 / _maxCoreClockMhz * 100.0, 0, 100)
                    : double.NaN;

                _coreVoltage[i] = sensor.CoreVoltageV.TryGetValue(i, out var volts) ? volts : double.NaN;
            }

            LogFrequencySourceOnce(sensor);

            // 温度：传感器 → ACPI 温区兜底（传感器关闭时不读取，直接置空 → UI 显示 "-"）。
            double? temp = null;
            if (sensorsOn)
                temp = sensor.PackageTemperatureC ?? sensor.MaxCoreTemperatureC ?? _acpi.ReadTemperatureC();

            if (temp is { } t && t > 0)
            {
                _packageTemp = t;
                if (double.IsNaN(_peakTemp) || t > _peakTemp)
                {
                    _peakTemp = t;
                    _peakTempTime = DateTime.Now;
                }
            }
            else
            {
                _packageTemp = null;
            }

            _packagePower = sensorsOn ? sensor.PackagePowerW : null;
            _fanRpm = fan.Rpm;
            _gpuFanRpm = fan.Gpu;
            _fanDetail = fan.Detail;
            _gpuTemp = sensorsOn ? sensor.GpuTemperatureC : null;
            _gpuUsage = sensorsOn ? sensor.GpuUtilizationPercent : null;
            _driveTemp = sensorsOn ? sensor.DriveTemperatureC : null;
            _driveTemps = sensorsOn ? sensor.DriveTemperatures : [];

            // 进程排行（首次调用无增量基准，返回空列表）。
            try
            {
                var top = _ranker.SampleTop(5);
                if (top.Count > 0)
                    _topProcesses = top
                        .Select(p => new ProcessLoadSample
                        {
                            Name = p.Name,
                            UsagePercent = Math.Clamp(p.CpuPercent, 0, 100),
                            IconKind = MapIconKind(p.Name),
                        })
                        .ToList();
            }
            catch
            {
                // 排行失败沿用上次结果。
            }
        }
    }

    /// <summary>
    /// 频率/电压读数来源的一次性取证（v1.18.0）。
    /// <para>
    /// 这一行存在的直接理由是 v1.17.x 的一次误判：当时日志里没有任何"时钟传感器是否存在"的证据，
    /// 于是"日志没有 Clock 行"被读成"LHM 没有时钟传感器"，白跑了两轮实测才发现
    /// 界面显示的其实是 SMU 的逐核时钟。**降级/取舍路径必须带出真实原因** —— 这条约定在
    /// v1.16.1（温度）、v1.16.2（盘温）都吃过亏，频率这一列同样适用。
    /// </para>
    /// </summary>
    private void LogFrequencySourceOnce(HardwareSensorSnapshot sensor)
    {
        if (_frequencySourceLogged || _cores.Count == 0)
            return;

        var effectiveCount = _coreEffectiveGhz.Count(v => !double.IsNaN(v));
        var pstateCount = sensor.CoreClockMhz.Count;

        // 首帧传感器可能还没出快照，两者都空时先不写，等有读数再落这一行。
        if (effectiveCount == 0 && pstateCount == 0)
            return;

        _frequencySourceLogged = true;

        var sources = string.Join(",", _coreFreqSource.Select(DescribeSource));
        TopologyLog.Write(
            $"[FREQ] 逐核频率来源：显示列={sources}（LHM 逐核 P-state/计数器）；"
            + $"有效频率={(effectiveCount > 0 ? $"可用 {effectiveCount}/{_cores.Count} 核" : "不可用")}；"
            + $"逐核电压={(sensor.CoreVoltageV.Count > 0 ? $"可用 {sensor.CoreVoltageV.Count}/{_cores.Count} 核" : "不可用")}；"
            + $"最高加速档={(_maxCoreClockMhz > 0 ? $"{_maxCoreClockMhz:0} MHz" : "-")}"
            + "（硬件活跃度的分母，取会话内观测到的最高 P-state）");
    }

    private static string DescribeSource(CoreFrequencySource source) => source switch
    {
        CoreFrequencySource.SensorPState => "档位",
        CoreFrequencySource.SensorEffective => "有效",
        CoreFrequencySource.PerfCounterRatio => "计数器",
        CoreFrequencySource.BaseClock => "基频",
        _ => "-",
    };

    private HardwareSensorSnapshot ReadSensorSnapshot()
    {
        try
        {
            return _sensors.ReadSnapshot();
        }
        catch
        {
            return HardwareSensorSnapshot.Empty;
        }
    }

    /// <summary>风扇读数（转速 + 来源/原因）。</summary>
    private readonly record struct FanResolution(double? Rpm, double? Gpu, string Detail);

    /// <summary>
    /// 解析风扇读数（v1.16.3）：先取 LibreHardwareMonitor，取不到再问 ASUS WMI。
    /// <para>
    /// 为什么要两级：台式机主板的风扇挂在 SuperIO 上，LHM 直接可读；笔记本（尤其 ASUS）的风扇由
    /// EC 控制，LHM 在 GA402XV 上整棵硬件树里没有一个 Fan 传感器，只能由 ASUS 自家的只读 ACPI
    /// 接口（<c>AsusAtkWmi_WMNB.DSTS</c>）补上。两级都拿不到时，把原因带回界面与日志，
    /// 而不是留一个无法解释的 "-"。
    /// </para>
    /// </summary>
    private FanResolution ResolveFan(bool sensorsOn, HardwareSensorSnapshot sensor)
    {
        if (!sensorsOn)
            return new FanResolution(null, null, "性能模式已暂停传感器轮询");

        if (sensor.FanRpm is { } lhmFan)
            return new FanResolution(lhmFan, null, "来源：LibreHardwareMonitor（主板 SuperIO）");

        var asus = _asusFan.Read();
        return new FanResolution(asus.CpuRpm, asus.GpuRpm, asus.Detail);
    }

    private double?[] SamplePerfPerformance()
    {
        var result = new double?[_perfPerf.Length];
        for (var i = 0; i < _perfPerf.Length; i++)
        {
            var counter = _perfPerf[i];
            if (counter is null)
                continue;

            try
            {
                var value = counter.NextValue();
                if (value > 0)
                    result[i] = value;
            }
            catch
            {
                // 计数器失效按缺失处理。
            }
        }

        return result;
    }

    private static System.Diagnostics.PerformanceCounter?[] BuildPerfPerformanceCounters(int logicalCount)
    {
        var counters = new System.Diagnostics.PerformanceCounter?[logicalCount];
        try
        {
            if (!PerformanceCounterCategory.Exists(PerfCategory))
                return counters;

            // 实例名形如 "组号,组内编号"（如 "0,3"）；"_Total" 等忽略。
            foreach (var instance in new PerformanceCounterCategory(PerfCategory).GetInstanceNames())
            {
                var parts = instance.Split(',');
                if (parts.Length != 2
                    || !ushort.TryParse(parts[0], out var group)
                    || !ushort.TryParse(parts[1], out var index))
                {
                    continue;
                }

                var osIndex = group * 64 + index;
                if (osIndex < logicalCount)
                    counters[osIndex] = new System.Diagnostics.PerformanceCounter(
                        PerfCategory, PerfPerformanceCounter, instance, readOnly: true);
            }
        }
        catch
        {
            // 类别不可用时全部置空，频率回退基频。
        }

        return counters;
    }

    /// <summary>进程名 → 图标类别（与 Mock 数据的图标键一致）。</summary>
    private static string MapIconKind(string processName)
    {
        var name = processName.ToLowerInvariant();

        if (name.Contains("chrome") || name.Contains("msedge") || name.Contains("firefox")
            || name.Contains("opera") || name.Contains("brave") || name.Contains("360se")
            || name.Contains("sogou") || name.Contains("qqbrowser"))
            return "browser";

        if (name.Contains("steam") || name.Contains("epicgames") || name.Contains("wegame"))
            return "steam";

        if (name.Contains("wechat") || name.Contains("weixin") || name.Contains("qq")
            || name.Contains("discord") || name.Contains("telegram") || name.Contains("dingtalk")
            || name.Contains("feishu") || name.Contains("slack") || name.Contains("tim.exe"))
            return "chat";

        if (name is "system" or "system idle process" or "registry" or "csrss" or "dwm"
            or "services" or "svchost" or "wmiprvse" or "lsass" or "winlogon" or "explorer")
            return "system";

        if (IsLikelyGame(name))
            return "game";

        return "app";
    }

    private static bool IsLikelyGame(string name) => name.Contains("cs2") || name.Contains("csgo")
        || name.Contains("valorant") || name.Contains("league") || name.Contains("dota")
        || name.Contains("gtav") || name.Contains("cyberpunk") || name.Contains("minecraft")
        || name.Contains("javaw") || name.Contains("genshin") || name.Contains("yuanshen")
        || name.Contains("starrail") || name.Contains("pubg") || name.Contains("apex")
        || name.Contains("overwatch") || name.Contains("crossfire") || name.Contains("dnf")
        || name.Contains("client-wbp") || name.Contains("game");

    // ---------------------------------------------------------------- 拓扑分组

    /// <summary>
    /// 物理核心列表（SMT 线程归并）。拓扑可信时用真实解析结果；
    /// 退化拓扑（虚拟机/云主机只暴露单条核心掩码，无法区分物理核）按 SMT 口径还原物理核数，
    /// 与任务管理器口径保持一致。
    /// </summary>
    private static List<PhysicalCore> BuildPhysicalCores(CpuTopology topology, int logicalTotal)
    {
        var grouped = topology.LogicalProcessors
            .GroupBy(p => p.PhysicalCoreIndex)
            .OrderBy(g => g.Key)
            .Select(g => g.OrderBy(p => p.OsIndex).ToList())
            .ToList();

        var maxThreadsPerCore = grouped.Count == 0 ? 0 : grouped.Max(g => g.Count);

        // 可信判据：每物理核最多 2 个线程（真实 SMT 上限）+ 物理核数不少于逻辑数的一半。
        var resolved = grouped.Count > 0
            && grouped.Count <= logicalTotal
            && maxThreadsPerCore <= 2
            && grouped.Count * 2 >= logicalTotal;

        if (resolved)
        {
            return grouped
                .Select(g => new PhysicalCore(g[0].CoreClass, g.Select(p => p.OsIndex).ToArray()))
                .ToList();
        }

        // 退化路径：逻辑处理器两两归并成一颗物理核。
        var threadsPerCore = topology.HasSmt || logicalTotal % 2 == 0 ? 2 : 1;
        var coreCount = Math.Max(1, logicalTotal / threadsPerCore);
        var synthesized = new List<PhysicalCore>(coreCount);
        for (var c = 0; c < coreCount; c++)
        {
            var indexes = new List<int>(threadsPerCore);
            for (var t = 0; t < threadsPerCore; t++)
            {
                var osIndex = (c * threadsPerCore) + t;
                if (osIndex < logicalTotal)
                    indexes.Add(osIndex);
            }

            synthesized.Add(new PhysicalCore(CoreClass.Standard, indexes.ToArray()));
        }

        return synthesized;
    }

    /// <summary>
    /// CCD 数推导（规范 §33）：Zen 消费级单 CCD 最多 8 核，据此由物理核数推导 CCD 数量。
    /// 超出消费级范围（&gt;16 核的 Threadripper / EPYC）不做猜测，回退单段（规范 §34 禁止按名称猜测）；
    /// X3D 的 3D V-Cache 标注同理——无数据确认时一律不显示。
    /// </summary>
    private static int ResolveCcdCount(CpuVendor vendor, int physicalCores)
        => vendor == CpuVendor.Amd && physicalCores is > 8 and <= 16
            ? (physicalCores + 7) / 8
            : 1;

    /// <summary>
    /// 规范口径布局（默认）：
    /// Intel 混合架构 → P-Core + E-Core；多 CCD Ryzen → CCD 0..N；其余 → 单段 CPU Core。
    /// AMD 严禁出现 P-Core / E-Core（规范 §32）。
    /// </summary>
    private static (List<CoreGroup> Tiles, List<CpuCoreGroup> Sections) BuildSpecLayout(
        int ccdCount, List<PhysicalCore> physical)
    {
        var segments = new List<(CoreGroupKind Kind, string Title, int? Ccd, List<PhysicalCore> Cores)>();

        if (physical.Any(p => p.Class == CoreClass.Efficiency))
        {
            // Intel 混合架构：真实 P / E 分段。
            segments.Add((
                CoreGroupKind.Performance, "P-Core", null,
                physical.Where(p => p.Class == CoreClass.Performance).ToList()));
            segments.Add((
                CoreGroupKind.Efficiency, "E-Core", null,
                physical.Where(p => p.Class == CoreClass.Efficiency).ToList()));
        }
        else if (ccdCount >= 2)
        {
            // 多 CCD Ryzen：按 CCD 均分（单 CCD 最多 8 核）。
            var perCcd = (physical.Count + ccdCount - 1) / ccdCount;
            for (var ccd = 0; ccd < ccdCount; ccd++)
            {
                var slice = physical.Skip(ccd * perCcd).Take(perCcd).ToList();
                if (slice.Count > 0)
                    segments.Add((CoreGroupKind.Standard, $"CCD {ccd}", ccd, slice));
            }
        }
        else
        {
            // 同构平台（AMD / 非混合 Intel / 未知厂商）：单段 CPU Core。
            segments.Add((CoreGroupKind.Standard, "CPU Core", null, physical));
        }

        return Materialize(segments);
    }

    /// <summary>
    /// 设计稿口径布局（v1.5.0 一比一还原）：非混合架构把逻辑处理器前后对半切成 P 段 / E 段，
    /// 混合架构仍用真实 P/E（设计稿 Intel 为 6P + 8E）。
    /// </summary>
    private static (List<CoreGroup> Tiles, List<CpuCoreGroup> Sections) BuildDraftLayout(
        List<PhysicalCore> physical, bool hybrid, int logicalTotal)
    {
        if (hybrid)
        {
            var segments = new List<(CoreGroupKind Kind, string Title, int? Ccd, List<PhysicalCore> Cores)>
            {
                (CoreGroupKind.Performance, "P-Core", null,
                    physical.Where(p => p.Class == CoreClass.Performance).ToList()),
                (CoreGroupKind.Efficiency, "E-Core", null,
                    physical.Where(p => p.Class == CoreClass.Efficiency).ToList()),
            };
            return Materialize(segments);
        }

        var half = logicalTotal / 2;
        var tiles = new List<CoreGroup>(logicalTotal);
        var pIds = new List<string>(half);
        var eIds = new List<string>(Math.Max(0, logicalTotal - half));

        for (var i = 0; i < logicalTotal; i++)
        {
            var isPerformance = i < half;
            var name = isPerformance ? $"P{i}" : $"E{i - half}";
            tiles.Add(new CoreGroup(CoreClass.Standard, name, [i], null));
            (isPerformance ? pIds : eIds).Add(name);
        }

        // P 段标注整机规格（N 核 / 2N 线程）、E 段标注后半段（N 核 / N 线程），与设计稿字面一致。
        var sections = new List<CpuCoreGroup>
        {
            new(CoreGroupKind.Performance, "P-Core", pIds, pIds.Count, logicalTotal),
            new(CoreGroupKind.Efficiency, "E-Core", eIds, eIds.Count, eIds.Count),
        };

        return (tiles, sections);
    }

    /// <summary>按分段顺序生成 Tile 显示名（P0.. / E0.. / C0.. 或 CCD 内连续编号）与分段元数据。</summary>
    private static (List<CoreGroup> Tiles, List<CpuCoreGroup> Sections) Materialize(
        List<(CoreGroupKind Kind, string Title, int? Ccd, List<PhysicalCore> Cores)> segments)
    {
        var tiles = new List<CoreGroup>();
        var sections = new List<CpuCoreGroup>();
        int pIndex = 0, eIndex = 0, cIndex = 0;

        foreach (var segment in segments)
        {
            var ids = new List<string>(segment.Cores.Count);

            foreach (var core in segment.Cores)
            {
                var name = segment.Kind switch
                {
                    CoreGroupKind.Performance => $"P{pIndex++}",
                    CoreGroupKind.Efficiency => $"E{eIndex++}",
                    _ => $"C{cIndex++}",
                };

                tiles.Add(new CoreGroup(core.Class, name, core.OsIndexes, segment.Ccd));
                ids.Add(name);
            }

            sections.Add(new CpuCoreGroup(
                segment.Kind,
                segment.Title,
                ids,
                segment.Cores.Count,
                segment.Cores.Sum(c => c.OsIndexes.Length),
                segment.Ccd));
        }

        return (tiles, sections);
    }

    /// <summary>解析出的物理核心（类别 + 归属线程）。</summary>
    private sealed record PhysicalCore(CoreClass Class, int[] OsIndexes);

    /// <summary>一个核心 Tile（SMT 线程归并后的物理核；设计稿口径下即单个逻辑处理器）。</summary>
    private sealed class CoreGroup(CoreClass coreClass, string displayName, int[] osIndexes, int? ccdIndex)
    {
        public CoreClass Class { get; } = coreClass;

        public string DisplayName { get; } = displayName;

        public int[] OsIndexes { get; } = osIndexes;

        public int? CcdIndex { get; } = ccdIndex;
    }
}
