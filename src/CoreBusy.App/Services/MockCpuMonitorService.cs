namespace CoreBusy.App.Services;

using System.Diagnostics;
using CoreBusy.Core;
using CoreBusy.Core.Interfaces;
using CoreBusy.Core.Models;

/// <summary>
/// 模拟 CPU 监控服务（规范 §54/§55 Debug CPU）：
/// 提供四套预置平台，用于在没有对应硬件时验证动态布局与主题。
/// <list type="bullet">
///   <item><c>intel-hybrid</c>：Intel Core i5-14600KF（6P + 8E / 20 线程）。</item>
///   <item><c>intel-standard</c>：Intel Core i7-9700K（8 核 / 8 线程，非混合）。</item>
///   <item><c>amd-single</c>：AMD Ryzen 7 9700X（8 核 / 16 线程，单 CCD）。</item>
///   <item><c>amd-dual</c>：AMD Ryzen 9 7950X（16 核 / 32 线程，双 CCD）。</item>
/// </list>
/// 通过环境变量 <c>COREBUSY_MOCK_CPU</c> 选择（发布版亦可临时指定，不做 UI 入口，符合"Release 中隐藏"）。
/// 每个核心带"性格基线"，数值按平滑随机游走变化，不会突兀跳动。
/// </summary>
public sealed class MockCpuMonitorService : ICpuMonitorService
{
    private const double BaseTempC = 39.0;
    private const double TempPerUsage = 0.30;
    private const double BasePowerW = 22.0;
    private const double PowerPerUsage = 1.15;
    private const double BaseFanRpm = 950.0;
    private const double FanPerUsage = 7.5;

    /// <summary>环境变量名：指定 Debug CPU 预置平台（intel-hybrid / intel-standard / amd-single / amd-dual）。</summary>
    public const string MockCpuEnvVar = "COREBUSY_MOCK_CPU";

    private readonly CoreSim[] _cores;
    private readonly ProcessSim[] _processes;
    private readonly Random _random = new();
    private readonly MockProfile _profile;
    private readonly CoreLayoutMode _layout;
    private readonly List<CpuCoreGroup> _groups;

    private double _temperatureC = BaseTempC;
    private double _peakTempC = BaseTempC;
    private DateTime _peakTempTime = DateTime.Now;
    private int _sampleIntervalMs = 800;
    private bool _sensorsEnabled = true;

    /// <summary>当前生效的 Debug CPU 预置键。</summary>
    public string ProfileKey => _profile.Key;

    public MockCpuMonitorService(CoreLayoutMode layout = CoreLayoutMode.Spec, string? profileKey = null)
    {
        _layout = layout;
        _profile = ResolveProfile(profileKey);

        // 核心性格基线（%）：模拟日常负载分布 —— 个别核心高负载，其余在摸鱼。
        var pBase = new[] { 12.0, 86.0, 55.0, 8.0, 4.0, 70.0, 33.0, 19.0 };
        var eBase = new[] { 18.0, 25.0, 90.0, 37.0, 5.0, 42.0, 11.0, 28.0,
                            16.0, 31.0, 7.0, 24.0, 13.0, 46.0, 9.0, 21.0 };

        var tiles = BuildTiles();
        _cores = new CoreSim[tiles.Count];
        for (var i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            var baseline = tile.IsPerformance ? pBase[i % pBase.Length] : eBase[i % eBase.Length];
            _cores[i] = new CoreSim(tile.Id, tile.ThreadCount, baseline, tile.BaseGHz, _random);
        }

        _groups = BuildGroups(tiles);

        _processes =
        [
            new ProcessSim("Game.exe", "game", 28.4),
            new ProcessSim("Chrome.exe", "browser", 18.7),
            new ProcessSim("System", "system", 6.1),
            new ProcessSim("Discord.exe", "chat", 5.3),
            new ProcessSim("steam.exe", "steam", 4.8),
        ];
    }

    /// <inheritdoc />
    public CpuInfo GetCpuInfo() => new()
    {
        Name = _profile.Name,
        Vendor = _profile.Vendor,
        VendorKind = _profile.VendorKind,
        PhysicalCores = _profile.PhysicalCores,
        LogicalProcessors = _profile.Threads,
        Socket = _profile.Socket,
        BaseClockGHz = _profile.BaseGHz,
        CcdCount = _profile.CcdCount,
    };

    /// <inheritdoc />
    public CpuSnapshot GetSnapshot()
    {
        double usageSum = 0;
        foreach (var core in _cores)
        {
            core.Step(_random);
            usageSum += core.Usage;
        }

        var total = Math.Clamp(usageSum / _cores.Length, 0, 100);

        // 温度/功耗/风扇随总负载平滑变化并带轻微扰动。
        _temperatureC += (BaseTempC + total * TempPerUsage - _temperatureC) * 0.35 + (_random.NextDouble() - 0.5) * 0.6;
        _temperatureC = Math.Clamp(_temperatureC, 30, 95);
        if (_temperatureC > _peakTempC)
        {
            _peakTempC = _temperatureC;
            _peakTempTime = DateTime.Now;
        }

        var power = Math.Clamp(BasePowerW + total * PowerPerUsage + (_random.NextDouble() - 0.5) * 2, 10, 220);
        var fan = Math.Clamp(BaseFanRpm + total * FanPerUsage + (_random.NextDouble() - 0.5) * 40, 600, 2600);
        var avgFreq = _cores.Average(c => c.FrequencyGHz);

        foreach (var process in _processes)
            process.Step(_random);

        var processes = _processes
            .OrderByDescending(p => p.Usage)
            .Take(5)
            .Select(p => new ProcessLoadSample { Name = p.Name, UsagePercent = p.Usage, IconKind = p.IconKind })
            .ToList();

        // 硬件传感器关闭（设置 / 性能模式）时温度/功耗/风扇降级为 NaN，UI 显示 "-"。
        var sensorsOn = _sensorsEnabled;
        return new CpuSnapshot
        {
            TotalUsagePercent = total,
            PackageTemperatureC = sensorsOn ? _temperatureC : double.NaN,
            PeakTemperatureC = _peakTempC,
            PeakTemperatureTime = _peakTempTime,
            PackagePowerW = sensorsOn ? power : double.NaN,
            AverageFrequencyGHz = avgFreq,
            FanRpm = sensorsOn ? fan : double.NaN,
            // 演示数据：GPU 风扇跟随同一负载近似走势（Mock 不追求物理一致性，只为界面自检）。
            GpuFanRpm = sensorsOn ? Math.Clamp(fan * 0.9, 500, 2400) : double.NaN,
            FanDetail = sensorsOn ? "来源：演示数据（Mock）" : "性能模式已暂停传感器轮询",
            TopProcesses = processes,
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<CpuCoreSnapshot> GetCoreSnapshots() => _cores
        .Select((c, i) =>
        {
            var classKind = _layout == CoreLayoutMode.Draft && !IsHybrid
                ? CoreClass.Standard
                : TileClass(i);
            var ccd = _layout == CoreLayoutMode.Spec ? _tileCcd[i] : null;

            // 核内线程明细：与真实采集同一口径，撑起 Tile 内线程条与热力图线程行。
            var threads = new CpuThreadSnapshot[c.ThreadCount];
            for (var t = 0; t < c.ThreadCount; t++)
            {
                var threadUsage = c.ThreadUsages[t];
                threads[t] = new CpuThreadSnapshot
                {
                    Id = c.ThreadCount > 1 ? $"{c.Id}·{t}" : c.Id,
                    CoreId = c.Id,
                    Index = t,
                    UsagePercent = threadUsage,
                    FrequencyGHz = c.FrequencyGHz,
                    Status = CoreStatusClassifier.Classify(threadUsage),
                };
            }

            return new CpuCoreSnapshot
            {
                Id = c.Id,
                Class = classKind,
                CcdIndex = ccd,
                UsagePercent = c.Usage,
                FrequencyGHz = c.FrequencyGHz,
                Status = CoreStatusClassifier.Classify(c.Usage),
                Threads = threads,
            };
        })
        .ToList();

    /// <inheritdoc />
    public IReadOnlyList<CpuCoreGroup> GetCoreGroups() => _groups;

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

    // ---------------------------------------------------------------- 预置平台

    private static MockProfile ResolveProfile(string? key)
    {
        key ??= Environment.GetEnvironmentVariable(MockCpuEnvVar);
        return key?.Trim().ToLowerInvariant() switch
        {
            "intel-standard" => IntelStandard,
            "amd-single" => AmdSingleCcd,
            "amd-dual" => AmdDualCcd,
            _ => IntelHybrid,
        };
    }

    private static readonly MockProfile IntelHybrid = new(
        "intel-hybrid", "Intel Core i5-14600KF", "Intel", CpuVendor.Intel, "LGA1700",
        PhysicalCores: 14, PerformanceCores: 6, EfficiencyCores: 8, ThreadsPerPerformanceCore: 2,
        CcdCount: 1, PerformanceGHz: 5.20, EfficiencyGHz: 3.90);

    private static readonly MockProfile IntelStandard = new(
        "intel-standard", "Intel Core i7-9700K", "Intel", CpuVendor.Intel, "LGA1151",
        PhysicalCores: 8, PerformanceCores: 8, EfficiencyCores: 0, ThreadsPerPerformanceCore: 1,
        CcdCount: 1, PerformanceGHz: 4.60, EfficiencyGHz: 3.60);

    private static readonly MockProfile AmdSingleCcd = new(
        "amd-single", "AMD Ryzen 7 9700X", "AMD", CpuVendor.Amd, "AM5",
        PhysicalCores: 8, PerformanceCores: 8, EfficiencyCores: 0, ThreadsPerPerformanceCore: 2,
        CcdCount: 1, PerformanceGHz: 5.40, EfficiencyGHz: 5.40);

    private static readonly MockProfile AmdDualCcd = new(
        "amd-dual", "AMD Ryzen 9 7950X", "AMD", CpuVendor.Amd, "AM5",
        PhysicalCores: 16, PerformanceCores: 16, EfficiencyCores: 0, ThreadsPerPerformanceCore: 2,
        CcdCount: 2, PerformanceGHz: 5.70, EfficiencyGHz: 5.70);

    private bool IsHybrid => _profile.EfficiencyCores > 0;

    private int?[] _tileCcd = [];

    /// <summary>按分区口径生成 Tile 序列（名称 + 类别 + 基频 + CCD 归属）。</summary>
    private List<TileSpec> BuildTiles()
    {
        var tiles = new List<TileSpec>();
        var ccd = _profile.CcdCount;

        if (_layout == CoreLayoutMode.Draft && !IsHybrid)
        {
            // 设计稿口径：逻辑处理器逐线程成 Tile，前后对半切成 P 段 / E 段。
            var threads = _profile.Threads;
            var half = threads / 2;
            for (var i = 0; i < threads; i++)
            {
                var name = i < half ? $"P{i}" : $"E{i - half}";
                tiles.Add(new TileSpec(name, i < half, _profile.PerformanceGHz, null, ThreadCount: 1));
            }
        }
        else if (IsHybrid)
        {
            // 混合架构：P 核带 SMT（2 线程），E 核单线程。
            for (var i = 0; i < _profile.PerformanceCores; i++)
                tiles.Add(new TileSpec($"P{i}", true, _profile.PerformanceGHz, null,
                    ThreadCount: Math.Max(1, _profile.ThreadsPerPerformanceCore)));
            for (var i = 0; i < _profile.EfficiencyCores; i++)
                tiles.Add(new TileSpec($"E{i}", false, _profile.EfficiencyGHz, null, ThreadCount: 1));
        }
        else if (ccd >= 2)
        {
            var perCcd = (_profile.PerformanceCores + ccd - 1) / ccd;
            var index = 0;
            for (var c = 0; c < ccd; c++)
            {
                for (var i = 0; i < perCcd && index < _profile.PerformanceCores; i++, index++)
                    tiles.Add(new TileSpec($"C{index}", true, _profile.PerformanceGHz, c,
                        ThreadCount: Math.Max(1, _profile.ThreadsPerPerformanceCore)));
            }
        }
        else
        {
            for (var i = 0; i < _profile.PerformanceCores; i++)
                tiles.Add(new TileSpec($"C{i}", true, _profile.PerformanceGHz, null,
                    ThreadCount: Math.Max(1, _profile.ThreadsPerPerformanceCore)));
        }

        _tileCcd = new int?[tiles.Count];
        for (var i = 0; i < tiles.Count; i++)
            _tileCcd[i] = _layout == CoreLayoutMode.Spec ? tiles[i].CcdIndex : null;

        return tiles;
    }

    private CoreClass TileClass(int index)
    {
        if (!IsHybrid)
            return CoreClass.Standard;

        return index < _profile.PerformanceCores ? CoreClass.Performance : CoreClass.Efficiency;
    }

    private List<CpuCoreGroup> BuildGroups(List<TileSpec> tiles)
    {
        if (IsHybrid)
        {
            return
            [
                new CpuCoreGroup(CoreGroupKind.Performance, "P-Core",
                    tiles.Where(t => t.IsPerformance).Select(t => t.Id).ToList(),
                    _profile.PerformanceCores,
                    _profile.PerformanceCores * _profile.ThreadsPerPerformanceCore),
                new CpuCoreGroup(CoreGroupKind.Efficiency, "E-Core",
                    tiles.Where(t => !t.IsPerformance).Select(t => t.Id).ToList(),
                    _profile.EfficiencyCores,
                    _profile.EfficiencyCores),
            ];
        }

        if (_layout == CoreLayoutMode.Draft)
        {
            var threads = _profile.Threads;
            var half = threads / 2;
            return
            [
                new CpuCoreGroup(CoreGroupKind.Performance, "P-Core",
                    tiles.Take(half).Select(t => t.Id).ToList(), half, threads),
                new CpuCoreGroup(CoreGroupKind.Efficiency, "E-Core",
                    tiles.Skip(half).Select(t => t.Id).ToList(), tiles.Count - half, tiles.Count - half),
            ];
        }

        if (_profile.CcdCount >= 2)
        {
            var groups = new List<CpuCoreGroup>();
            for (var c = 0; c < _profile.CcdCount; c++)
            {
                var ids = tiles.Where(t => t.CcdIndex == c).Select(t => t.Id).ToList();
                if (ids.Count > 0)
                    groups.Add(new CpuCoreGroup(CoreGroupKind.Standard, $"CCD {c}", ids,
                        ids.Count, ids.Count * _profile.ThreadsPerPerformanceCore, c));
            }

            return groups;
        }

        return
        [
            new CpuCoreGroup(CoreGroupKind.Standard, "CPU Core",
                tiles.Select(t => t.Id).ToList(),
                _profile.PerformanceCores,
                _profile.Threads),
        ];
    }

    /// <summary>Debug CPU 预置平台描述。</summary>
    private sealed record MockProfile(
        string Key,
        string Name,
        string Vendor,
        CpuVendor VendorKind,
        string Socket,
        int PhysicalCores,
        int PerformanceCores,
        int EfficiencyCores,
        int ThreadsPerPerformanceCore,
        int CcdCount,
        double PerformanceGHz,
        double EfficiencyGHz)
    {
        /// <summary>整机线程数（P 核 2 线程 / E 核 1 线程）。</summary>
        public int Threads => (PerformanceCores * ThreadsPerPerformanceCore) + EfficiencyCores;

        /// <summary>基础频率（GHz）：取 P 核频率的一半作为 WMI 口径的基频。</summary>
        public double BaseGHz => PerformanceGHz / 2;
    }

    /// <summary>一个 Tile 的静态描述。</summary>
    private sealed record TileSpec(string Id, bool IsPerformance, double BaseGHz, int? CcdIndex, int ThreadCount);

    /// <summary>单个核心模拟器：基线回归 + 随机游走，保证画面"活着"但不乱跳。</summary>
    private sealed class CoreSim
    {
        private const double StepNoise = 7.0;
        private const double BaselinePull = 0.12;

        /// <summary>同核两条 SMT 线程的固定偏置：真实 SMT 下两条线程负载本就不同，这里让差异可见。</summary>
        private static readonly double[] ThreadBias = [7.0, -9.0];

        private readonly double _baseline;
        private readonly double _baseGHz;

        public CoreSim(string id, int threadCount, double baseline, double baseGHz, Random random)
        {
            Id = id;
            ThreadCount = Math.Max(1, threadCount);
            _baseline = baseline;
            _baseGHz = baseGHz;

            Usage = Math.Clamp(baseline + (random.NextDouble() - 0.5) * 10, 0.5, 99.5);
            ThreadUsages = new double[ThreadCount];
            for (var t = 0; t < ThreadCount; t++)
                ThreadUsages[t] = ClampThread(t, random);
        }

        public string Id { get; }

        /// <summary>该物理核的逻辑线程数（1 或 2）。</summary>
        public int ThreadCount { get; }

        /// <summary>核内各线程使用率（长度 = ThreadCount）。</summary>
        public double[] ThreadUsages { get; }

        public double Usage { get; private set; }

        public double FrequencyGHz { get; private set; }

        public void Step(Random random)
        {
            Usage = Math.Clamp(
                Usage + (random.NextDouble() - 0.5) * StepNoise + (_baseline - Usage) * BaselinePull,
                0.5,
                99.5);

            // 轻负载时略降频，带微小抖动。
            var idleDrop = (1 - Usage / 100) * 0.25;
            FrequencyGHz = Math.Round(_baseGHz - idleDrop + (random.NextDouble() - 0.5) * 0.03, 2);

            for (var t = 0; t < ThreadCount; t++)
                ThreadUsages[t] = ClampThread(t, random);
        }

        private double ClampThread(int index, Random random)
        {
            if (ThreadCount <= 1)
                return Usage;

            var bias = index < ThreadBias.Length ? ThreadBias[index] : 0;
            return Math.Clamp(Usage + bias + (random.NextDouble() - 0.5) * 4, 0.5, 99.5);
        }
    }

    /// <summary>进程模拟器：围绕基线小幅波动。</summary>
    private sealed class ProcessSim
    {
        private readonly double _baseline;

        public ProcessSim(string name, string iconKind, double baseline)
        {
            Name = name;
            IconKind = iconKind;
            _baseline = baseline;
            Usage = baseline;
        }

        public string Name { get; }

        public string IconKind { get; }

        public double Usage { get; private set; }

        public void Step(Random random)
            => Usage = Math.Clamp(
                Usage + (random.NextDouble() - 0.5) * 2.4 + (_baseline - Usage) * 0.2,
                0.2,
                45);
    }
}
