namespace CoreBusy.App.ViewModels;

using System.Collections.ObjectModel;
using CoreBusy.App.Infrastructure;
using CoreBusy.App.Services;
using CoreBusy.Core;
using CoreBusy.Core.Interfaces;
using CoreBusy.Core.Models;

/// <summary>主窗口视图模型：CPU 信息 + 拓扑分区 + 每核心实时占用率 + 温度/功耗/频率 + 热力图/排行/游戏模式。</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly ICpuInfoService _cpuInfoService;
    private readonly ICpuTopologyService _topologyService;
    private readonly Func<int, ICoreUsageProvider> _usageProviderFactory;
    private readonly IHardwareSensorService _sensorService;
    private readonly IGameDetector? _gameDetector;
    private readonly IProcessCpuRanker? _processRanker;

    private ICoreUsageProvider? _usageProvider;
    private CpuInfo? _cpuInfo;
    private double _averageUsage;
    private double? _packageTemperatureC;
    private double? _maxCoreTemperatureC;
    private double? _packagePowerW;
    private string _statusText = "正在初始化…";
    private bool _initialized;
    private int _sampleCount;
    private bool _showHeatmap = true;
    private string _laborModelText = "劳模：统计中…";
    private string _slackerKingText = "摸鱼王：统计中…";
    private string _schedulingText = "调度：统计中…";
    private string _gameSessionText = string.Empty;
    private bool _hasGameSession;
    private GameSession? _activeGame;
    private GameSession? _lastGame;

    // 单核心瓶颈检测状态（Phase 4）。
    private int _bottleneckStreak;
    private string _bottleneckText = string.Empty;
    private bool _hasBottleneck;

    // 进程关联面板状态（Phase 4，方案 4.3 节）。
    private CoreTileViewModel? _selectedCore;
    private string _selectedCoreHeaderText = string.Empty;
    private string _processPanelHint = "选中核心后显示占用该 CPU 的进程排行";

    /// <summary>会话内每核心负载统计（劳模排行数据源，方案 4.2 节；Phase 5 起按日持久化）。</summary>
    private readonly Dictionary<int, CoreStatEntry> _statsByCore = new();

    /// <summary>应用设置（Phase 5）。实例由 MainViewModel 持有并原地更新。</summary>
    private readonly AppSettings _settings;

    /// <summary>传感器服务是否已启动（设置中可关闭/重开）。</summary>
    private bool _sensorStarted;

    /// <summary>当日统计日期（跨日自动重置，Phase 5）。</summary>
    private DateTime _statsDate = DateTime.Today;

    public MainViewModel(
        ICpuInfoService cpuInfoService,
        ICpuTopologyService topologyService,
        Func<int, ICoreUsageProvider> usageProviderFactory,
        IHardwareSensorService sensorService,
        IGameDetector? gameDetector = null,
        IProcessCpuRanker? processRanker = null,
        AppSettings? settings = null)
    {
        _cpuInfoService = cpuInfoService;
        _topologyService = topologyService;
        _usageProviderFactory = usageProviderFactory;
        _sensorService = sensorService;
        _gameDetector = gameDetector;
        _processRanker = processRanker;
        _settings = settings ?? new AppSettings();
    }

    /// <summary>性能核（P-Core）Tile 列表。</summary>
    public ObservableCollection<CoreTileViewModel> PerformanceTiles { get; } = new();

    /// <summary>能效核（E-Core）Tile 列表（非混合平台为空）。</summary>
    public ObservableCollection<CoreTileViewModel> EfficiencyTiles { get; } = new();

    /// <summary>60 秒负载热力图行（P 核在前，方案 3.3 节）。</summary>
    public ObservableCollection<HeatmapRowViewModel> HeatmapRows { get; } = new();

    /// <summary>热力图显示开关。</summary>
    public bool ShowHeatmap
    {
        get => _showHeatmap;
        set => SetProperty(ref _showHeatmap, value);
    }

    /// <summary>劳模排行文本（平均负载最高的核心）。</summary>
    public string LaborModelText
    {
        get => _laborModelText;
        private set => SetProperty(ref _laborModelText, value);
    }

    /// <summary>摸鱼王排行文本（平均负载最低的核心）。</summary>
    public string SlackerKingText
    {
        get => _slackerKingText;
        private set => SetProperty(ref _slackerKingText, value);
    }

    /// <summary>游戏模式文本（进行中实时刷新 / 结束后保留摘要，方案 4.1 节）。</summary>
    public string GameSessionText
    {
        get => _gameSessionText;
        private set => SetProperty(ref _gameSessionText, value);
    }

    /// <summary>游戏会话信息可见性。</summary>
    public bool HasGameSession
    {
        get => _hasGameSession;
        private set => SetProperty(ref _hasGameSession, value);
    }

    /// <summary>单核心瓶颈提示文本（持续满足条件时显示，Phase 4）。</summary>
    public string BottleneckText
    {
        get => _bottleneckText;
        private set => SetProperty(ref _bottleneckText, value);
    }

    /// <summary>单核心瓶颈提示可见性。</summary>
    public bool HasBottleneck
    {
        get => _hasBottleneck;
        private set => SetProperty(ref _hasBottleneck, value);
    }

    /// <summary>调度分析文本（P/E 均衡或负载离散度，Phase 4）。</summary>
    public string SchedulingText
    {
        get => _schedulingText;
        private set => SetProperty(ref _schedulingText, value);
    }

    /// <summary>当前选中的核心（进程关联面板，方案 4.3 节）。</summary>
    public CoreTileViewModel? SelectedCore
    {
        get => _selectedCore;
        private set
        {
            if (SetProperty(ref _selectedCore, value))
            {
                OnPropertyChanged(nameof(HasSelectedCore));
                SelectedCoreHeaderText = value is null ? string.Empty : $"{value.DisplayName} 进程关联";
                ProcessList.Clear();
                _lastRankSample = DateTime.MinValue;
            }
        }
    }

    /// <summary>进程关联面板可见性。</summary>
    public bool HasSelectedCore => SelectedCore is not null;

    public string SelectedCoreHeaderText
    {
        get => _selectedCoreHeaderText;
        private set => SetProperty(ref _selectedCoreHeaderText, value);
    }

    /// <summary>核心详情面板中的进程排行。</summary>
    public ObservableCollection<ProcessCpuSample> ProcessList { get; } = new();

    public string ProcessPanelHint
    {
        get => _processPanelHint;
        private set => SetProperty(ref _processPanelHint, value);
    }

    /// <summary>选中核心（Tile 点击入口）。</summary>
    public void SelectCore(CoreTileViewModel tile) => SelectedCore = tile;

    /// <summary>取消选中，关闭进程关联面板。</summary>
    public void ClearSelectedCore() => SelectedCore = null;

    private DateTime _lastRankSample = DateTime.MinValue;

    public CpuInfo? CpuInfo
    {
        get => _cpuInfo;
        private set
        {
            if (SetProperty(ref _cpuInfo, value))
            {
                OnPropertyChanged(nameof(CpuName));
                OnPropertyChanged(nameof(Vendor));
                OnPropertyChanged(nameof(CoreSummary));
            }
        }
    }

    public string CpuName => CpuInfo?.Name ?? "读取中…";
    public string Vendor => $"厂商：{CpuInfo?.Vendor ?? "-"}";
    public string CoreSummary => CpuInfo is null ? "-" : $"物理核心 {CpuInfo.PhysicalCores} / 逻辑线程 {CpuInfo.LogicalProcessors}";

    /// <summary>E-Core 分区可见性。</summary>
    public bool HasEfficiencyCores => EfficiencyTiles.Count > 0;

    public double AverageUsage
    {
        get => _averageUsage;
        private set
        {
            if (SetProperty(ref _averageUsage, value))
                OnPropertyChanged(nameof(AverageUsageText));
        }
    }

    public string AverageUsageText => _initialized ? $"平均占用 {AverageUsage:0.0}%" : "-";

    public double? PackageTemperatureC
    {
        get => _packageTemperatureC;
        private set
        {
            if (SetProperty(ref _packageTemperatureC, value))
            {
                OnPropertyChanged(nameof(TemperatureText));
                OnPropertyChanged(nameof(SensorHintVisibility));
            }
        }
    }

    public double? MaxCoreTemperatureC
    {
        get => _maxCoreTemperatureC;
        private set
        {
            if (SetProperty(ref _maxCoreTemperatureC, value))
                OnPropertyChanged(nameof(TemperatureText));
        }
    }

    public double? PackagePowerW
    {
        get => _packagePowerW;
        private set
        {
            if (SetProperty(ref _packagePowerW, value))
            {
                OnPropertyChanged(nameof(PowerText));
                OnPropertyChanged(nameof(SensorHintVisibility));
            }
        }
    }

    /// <summary>温度文本：Package（Max Core）。</summary>
    public string TemperatureText
    {
        get
        {
            if (PackageTemperatureC is not double pkg)
                return "—";

            return MaxCoreTemperatureC is double max
                ? $"{pkg:0}℃ (峰值 {max:0}℃)"
                : $"{pkg:0}℃";
        }
    }

    public string PowerText => PackagePowerW is double w ? $"{w:0}W" : "—";

    /// <summary>温度/功耗不可用时的提示（需管理员权限）。</summary>
    public bool SensorHintVisibility => PackageTemperatureC is null && PackagePowerW is null;

    /// <summary>采样周期（毫秒），来自设置，方案 7.2 节（500/1000/2000）。</summary>
    public int IntervalMilliseconds => _settings.IntervalMilliseconds;

    /// <summary>
    /// 应用新设置（Phase 5）：保存到磁盘，采样周期由 MainWindow 更新定时器生效；
    /// 传感器关闭后停止刷新、重开时惰性启动；游戏检测关闭时结束进行中的会话。
    /// </summary>
    public void ApplySettings(AppSettings settings)
    {
        _settings.IntervalMilliseconds = settings.IntervalMilliseconds;
        _settings.SensorsEnabled = settings.SensorsEnabled;
        _settings.GameDetectionEnabled = settings.GameDetectionEnabled;
        SettingsStore.Save(_settings);
        OnPropertyChanged(nameof(IntervalMilliseconds));
        StatusText = $"采样中（{IntervalMilliseconds}ms）" + (SensorHintVisibility ? "｜温度/功耗需以管理员身份运行" : string.Empty);
        AppLog.Write($"settings applied: interval={settings.IntervalMilliseconds} sensors={settings.SensorsEnabled} game={settings.GameDetectionEnabled}");

        if (_settings.SensorsEnabled && !_sensorStarted)
            _ = StartSensorAsync();

        if (!_settings.GameDetectionEnabled && _activeGame is not null)
            EndGameSession();
    }

    /// <summary>惰性启动传感器（初始即启用或设置中重开时调用）。</summary>
    private async Task StartSensorAsync()
    {
        await Task.Run(() => _sensorService.Start());
        _sensorStarted = true;
        ReadSensorSnapshot();
    }

    /// <summary>状态栏文本（错误 / 初始化信息 / 权限提示）。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>读取 CPU 信息与拓扑、构建分区 Tile 列表、启动传感器。失败时记录状态并保持 UI 可用。</summary>
    public async Task InitializeAsync()
    {
        try
        {
            CpuInfo = await _cpuInfoService.GetCpuInfoAsync();
            var topology = _topologyService.GetTopology();

            PerformanceTiles.Clear();
            EfficiencyTiles.Clear();
            _physicalCoreByOsIndex.Clear();
            foreach (var lp in topology.LogicalProcessors.OrderBy(x => x.OsIndex))
            {
                var tile = new CoreTileViewModel(lp.OsIndex, lp.DisplayName);
                _physicalCoreByOsIndex[lp.OsIndex] = lp.PhysicalCoreIndex;
                if (lp.CoreClass == CoreClass.Performance)
                    PerformanceTiles.Add(tile);
                else
                    EfficiencyTiles.Add(tile);
            }

            OnPropertyChanged(nameof(HasEfficiencyCores));

            HeatmapRows.Clear();
            _statsByCore.Clear();
            foreach (var lp in topology.LogicalProcessors.OrderBy(x => x.OsIndex))
            {
                HeatmapRows.Add(new HeatmapRowViewModel(lp.DisplayName));
                _statsByCore[lp.OsIndex] = new CoreStatEntry();
            }

            _usageProvider = _usageProviderFactory(CpuInfo.LogicalProcessors);

            // Phase 5：恢复当日排行累计（"今日"口径跨会话延续）。
            var savedStats = StatsStore.LoadToday();
            if (savedStats is not null)
            {
                foreach (var (coreIndex, entry) in savedStats)
                {
                    if (_statsByCore.ContainsKey(coreIndex))
                        _statsByCore[coreIndex] = entry;
                }

                if (savedStats.Count > 0)
                    AppLog.Write($"daily stats restored: {savedStats.Count} cores from {DateTime.Today:yyyy-MM-dd}");
            }

            if (_settings.SensorsEnabled)
                await StartSensorAsync();
            else
                AppLog.Write("sensors disabled by settings");

            if (PackageTemperatureC is null && PackagePowerW is null)
                ReadSensorSnapshot();

            _initialized = true;
            StatusText = $"采样中（{IntervalMilliseconds}ms）" + (SensorHintVisibility ? "｜温度/功耗需以管理员身份运行" : string.Empty);
            OnPropertyChanged(nameof(AverageUsageText));
        }
        catch (Exception ex)
        {
            StatusText = $"初始化失败：{ex.Message}";
        }
    }

    /// <summary>采样占用率；每 2 个周期同步一次传感器快照（传感器刷新开销较大）。</summary>
    public void Sample()
    {
        if (_usageProvider is null)
            return;

        try
        {
            // Phase 5：跨日重置"今日"排行统计。
            if (DateTime.Today != _statsDate)
            {
                _statsDate = DateTime.Today;
                foreach (var coreIndex in _statsByCore.Keys.ToList())
                    _statsByCore[coreIndex] = new CoreStatEntry();
                AppLog.Write("daily stats reset (date changed)");
            }

            var usages = _usageProvider.SampleAll();
            double sum = 0;

            void Apply(ObservableCollection<CoreTileViewModel> tiles)
            {
                foreach (var tile in tiles)
                {
                    if (tile.Index >= usages.Length)
                        continue;

                    var usage = usages[tile.Index];
                    tile.Update(usage);
                    sum += usage;

                    // 热力图推入新样本（P 核在前、E 核在后，与 HeatmapRows 构建顺序一致）。
                    var rowIndex = PerformanceTiles.IndexOf(tile);
                    if (rowIndex < 0)
                        rowIndex = PerformanceTiles.Count + EfficiencyTiles.IndexOf(tile);
                    if (rowIndex >= 0 && rowIndex < HeatmapRows.Count)
                        HeatmapRows[rowIndex].Push(usage);

                    // 劳模统计累加。
                    if (!_statsByCore.TryGetValue(tile.Index, out var stat))
                        _statsByCore[tile.Index] = stat = new CoreStatEntry();
                    stat.Sum += usage;
                    stat.Count++;
                    if (usage > stat.Max)
                        stat.Max = usage;
                }
            }

            Apply(PerformanceTiles);
            Apply(EfficiencyTiles);

            AverageUsage = usages.Length > 0 ? sum / usages.Length : 0;

            if (_sampleCount % 5 == 0)
                UpdateRankings();
            UpdateGameSession();
            UpdateBottleneck();
            if (_sampleCount % 5 == 0)
                UpdateScheduling();
            UpdateProcessPanel();
        }
        catch (Exception ex)
        {
            StatusText = $"采样异常：{ex.Message}";
        }

        if (_sampleCount++ % 2 == 0)
            ReadSensorSnapshot();
    }

    /// <summary>单核心瓶颈检测：单核 ≥80% 且其余核心平均 ≤30% 持续 5 个采样周期。</summary>
    private void UpdateBottleneck()
    {
        CoreTileViewModel? maxTile = null;
        var maxUsage = -1d;
        double othersSum = 0;
        var othersCount = 0;

        void Scan(ObservableCollection<CoreTileViewModel> tiles)
        {
            foreach (var tile in tiles)
            {
                if (tile.UsagePercent > maxUsage)
                {
                    maxUsage = tile.UsagePercent;
                    maxTile = tile;
                }
            }
        }

        Scan(PerformanceTiles);
        Scan(EfficiencyTiles);

        foreach (var tile in PerformanceTiles.Concat(EfficiencyTiles))
        {
            if (!ReferenceEquals(tile, maxTile))
            {
                othersSum += tile.UsagePercent;
                othersCount++;
            }
        }

        var othersAvg = othersCount > 0 ? othersSum / othersCount : 0;
        if (_sampleCount % 5 == 0)
            AppLog.Write($"bottleneck-check: max={maxUsage:0.0} othersAvg={othersAvg:0.0} streak={_bottleneckStreak} has={HasBottleneck}");
        if (maxTile is not null && maxUsage >= 80 && othersAvg <= 30)
        {
            _bottleneckStreak++;
            if (_bottleneckStreak >= 5 && !HasBottleneck)
            {
                BottleneckText = $"疑似单核心瓶颈：{maxTile.DisplayName} {maxUsage:0}%（其余核心平均 {othersAvg:0}%）";
                HasBottleneck = true;
                AppLog.Write($"bottleneck detected: {BottleneckText}");
            }
            else if (HasBottleneck)
            {
                // 持续期间刷新数据。
                BottleneckText = $"疑似单核心瓶颈：{maxTile.DisplayName} {maxUsage:0}%（其余核心平均 {othersAvg:0}%）";
            }
        }
        else
        {
            _bottleneckStreak = 0;
            if (HasBottleneck)
            {
                HasBottleneck = false;
                BottleneckText = string.Empty;
                AppLog.Write("bottleneck cleared");
            }
        }
    }

    /// <summary>调度分析：混合架构对比 P/E 平均负载，单类平台显示 P 核负载离散度。</summary>
    private void UpdateScheduling()
    {
        if (!_initialized)
            return;

        if (HasEfficiencyCores)
        {
            var pAvg = PerformanceTiles.Count > 0 ? PerformanceTiles.Average(t => t.UsagePercent) : 0;
            var eAvg = EfficiencyTiles.Count > 0 ? EfficiencyTiles.Average(t => t.UsagePercent) : 0;
            var bias = pAvg > eAvg * 1.5 ? "，调度偏向 P 核" : eAvg > pAvg * 1.5 ? "，调度偏向 E 核" : "，负载均衡";
            SchedulingText = $"调度 P 核均 {pAvg:0.0}% / E 核均 {eAvg:0.0}%{bias}";
        }
        else
        {
            var usages = PerformanceTiles.Select(t => t.UsagePercent).ToList();
            if (usages.Count == 0)
                return;
            var spread = usages.Max() - usages.Min();
            var verdict = spread >= 60 ? "，负载高度集中" : spread >= 30 ? "，负载分布不均" : "，负载分布均匀";
            SchedulingText = $"调度离散度 {spread:0}%（{usages.Min():0}%–{usages.Max():0}%）{verdict}";
        }
    }

    /// <summary>进程关联面板：面板打开时每 2 个采样周期刷新一次进程排行。</summary>
    private void UpdateProcessPanel()
    {
        if (SelectedCore is null || _processRanker is null)
            return;

        var sinceLast = (DateTime.Now - _lastRankSample).TotalMilliseconds;
        if (sinceLast < IntervalMilliseconds * 2 - 50)
            return;

        _lastRankSample = DateTime.Now;
        var top = _processRanker.SampleTop(8);
        ProcessList.Clear();
        foreach (var sample in top)
            ProcessList.Add(sample);
        ProcessPanelHint = top.Count == 0
            ? "采样中…（增量基准建立后显示）"
            : "全系统进程 CPU 占用 Top 8（按核心归属需内核跟踪，后续版本支持）";
    }

    /// <summary>计算劳模/摸鱼王排行（会话内平均负载最高与最低的核心）。</summary>
    private void UpdateRankings()
    {
        CoreTileViewModel? best = null;
        CoreTileViewModel? worst = null;
        var bestAvg = -1d;
        var worstAvg = double.MaxValue;

        void Scan(ObservableCollection<CoreTileViewModel> tiles)
        {
            foreach (var tile in tiles)
            {
                var stat = _statsByCore.GetValueOrDefault(tile.Index);
                if (stat is null || stat.Count == 0)
                    continue;

                var avg = stat.Sum / stat.Count;
                if (avg > bestAvg)
                {
                    bestAvg = avg;
                    best = tile;
                }

                if (avg < worstAvg)
                {
                    worstAvg = avg;
                    worst = tile;
                }
            }
        }

        Scan(PerformanceTiles);
        Scan(EfficiencyTiles);

        if (best is not null)
        {
            var stat = _statsByCore[best.Index];
            LaborModelText = $"劳模 {best.DisplayName}：平均 {stat.Sum / stat.Count:0.0}%｜峰值 {stat.Max:0}%";
        }

        if (worst is not null)
        {
            var stat = _statsByCore[worst.Index];
            SlackerKingText = $"摸鱼王 {worst.DisplayName}：平均 {stat.Sum / stat.Count:0.0}%｜峰值 {stat.Max:0}%";
        }

        // Phase 5：当日统计落盘（每 5 个采样周期一次，随排行刷新节拍）。
        StatsStore.SaveToday(_statsByCore);
    }

    /// <summary>游戏模式：轮询前台全屏/无边框全屏应用，记录运行时长/CPU 平均/最高核心/最高温度。</summary>
    private void UpdateGameSession()
    {
        if (_gameDetector is null || !_settings.GameDetectionEnabled)
            return;

        var game = _gameDetector.DetectForegroundGame();

        if (game is not null)
        {
            _lastGame = null;
            if (_activeGame is null)
            {
                AppLog.Write($"game session started: {game}");
                _activeGame = new GameSession(game);
                HasGameSession = true;
            }

            var session = _activeGame;
            session.CpuSum += AverageUsage;
            session.Samples++;

            CoreTileViewModel? maxTile = null;
            void Scan(ObservableCollection<CoreTileViewModel> tiles)
            {
                foreach (var tile in tiles)
                {
                    if (maxTile is null || tile.UsagePercent > maxTile.UsagePercent)
                        maxTile = tile;
                }
            }

            Scan(PerformanceTiles);
            Scan(EfficiencyTiles);

            if (maxTile is not null && maxTile.UsagePercent > session.MaxCoreUsage)
            {
                session.MaxCoreUsage = maxTile.UsagePercent;
                session.MaxCoreName = maxTile.DisplayName;
            }

            var temp = MaxCoreTemperatureC ?? PackageTemperatureC;
            if (temp is double t && t > session.MaxTemperatureC)
                session.MaxTemperatureC = t;

            var cpuAvg = session.CpuSum / session.Samples;
            var maxCore = session.MaxCoreName is null
                ? "-"
                : $"{session.MaxCoreName} {session.MaxCoreUsage:0}%";
            var tempText = session.MaxTemperatureC is double mt ? $"{mt:0}℃" : "-";
            GameSessionText =
                $"游戏中 {session.Name}｜运行 {session.ElapsedText}｜CPU 平均 {cpuAvg:0.0}%｜最高核心 {maxCore}｜最高温度 {tempText}";
        }
        else if (_activeGame is not null)
        {
            EndGameSession();
        }
    }

    /// <summary>结束当前游戏会话：生成性能报告落盘（Phase 5）并保留"上次游戏"摘要。</summary>
    private void EndGameSession()
    {
        var session = _activeGame;
        if (session is null)
            return;

        _activeGame = null;
        _lastGame = session;
        session.MarkEnded();
        var cpuAvg = session.CpuSum / Math.Max(1, session.Samples);

        var report = new GameReport
        {
            Game = session.Name,
            StartedAt = session.StartedAt,
            EndedAt = session.EndedAt,
            DurationSeconds = (session.EndedAt - session.StartedAt).TotalSeconds,
            CpuAveragePercent = cpuAvg,
            MaxCoreName = session.MaxCoreName,
            MaxCoreUsagePercent = session.MaxCoreUsage,
            MaxTemperatureC = session.MaxTemperatureC,
        };
        var reportPath = GameReportStore.Save(report);

        AppLog.Write($"game session ended: {session.Name} elapsed={session.ElapsedText} report={reportPath ?? "save failed"}");
        var tempText = session.MaxTemperatureC is double mt ? $"{mt:0}℃" : "-";
        GameSessionText =
            $"上次游戏 {session.Name}｜时长 {session.ElapsedText}｜CPU 平均 {cpuAvg:0.0}%｜最高核心 {session.MaxCoreName ?? "-"} {session.MaxCoreUsage:0}%｜最高温度 {tempText}";
    }

    /// <summary>单个游戏会话的统计。</summary>
    private sealed class GameSession
    {
        public GameSession(string name) => Name = name;

        public string Name { get; }
        public DateTime StartedAt { get; } = DateTime.Now;
        public DateTime EndedAt { get; private set; }
        public double CpuSum { get; set; }
        public long Samples { get; set; }
        public double MaxCoreUsage { get; set; }
        public string? MaxCoreName { get; set; }
        public double? MaxTemperatureC { get; set; }

        /// <summary>会话结束时记录结束时间（供性能报告使用，Phase 5）。</summary>
        public void MarkEnded() => EndedAt = DateTime.Now;

        public string ElapsedText
        {
            get
            {
                var elapsed = DateTime.Now - StartedAt;
                return $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
            }
        }
    }

    private void ReadSensorSnapshot()
    {
        // Phase 5：设置中关闭传感器时跳过读取，温度/功耗/频率保持 "—"。
        if (!_settings.SensorsEnabled || !_sensorStarted)
            return;

        try
        {
            var snapshot = _sensorService.ReadSnapshot();
            PackageTemperatureC = snapshot.PackageTemperatureC;
            MaxCoreTemperatureC = snapshot.MaxCoreTemperatureC;
            PackagePowerW = snapshot.PackagePowerW;

            foreach (var tile in PerformanceTiles)
                tile.UpdateClock(snapshot.CoreClockMhz.GetValueOrDefault(PhysicalCoreOf(tile)));
            foreach (var tile in EfficiencyTiles)
                tile.UpdateClock(snapshot.CoreClockMhz.GetValueOrDefault(PhysicalCoreOf(tile)));
        }
        catch
        {
            // 传感器读取失败保持上次值，不打断主采样循环。
        }
    }

    private readonly Dictionary<int, int> _physicalCoreByOsIndex = new();

    private int PhysicalCoreOf(CoreTileViewModel tile)
        => _physicalCoreByOsIndex.GetValueOrDefault(tile.Index, tile.Index);
}
