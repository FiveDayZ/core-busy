namespace CoreBusy.App.ViewModels.Dashboard;

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CoreBusy.App.Controls;
using CoreBusy.App.Infrastructure;
using CoreBusy.App.Services;
using CoreBusy.App.Themes;
using CoreBusy.Core.Health;
using CoreBusy.Core.Interfaces;
using CoreBusy.Core.Models;
using CoreBusy.Core.SelfTest;
using CoreBusy.Sensor;
using CoreBusy.Windows.SelfTest;

/// <summary>
/// 主仪表盘视图模型：驱动 Mock/真实 CPU 监控服务的采样节奏，
/// 汇聚总览指标、每核心 Tile、热力图历史、排行榜、主要进程与状态栏。
/// 数据每 800ms 平滑刷新，热力图保留最近 60 秒（每列 1 秒）。
/// </summary>
public sealed class DashboardViewModel : ObservableObject
{
    private const int HeatmapSeconds = 60;

    /// <summary>性能模式下的采样周期（毫秒）：走"低开销"档，降低本工具自身资源占用。</summary>
    private const int PerformanceModeIntervalMs = 2000;

    /// <summary>排行榜固定行数：恒 5 行，核心不足时缺位显示 "-"（不生成/合并/删减行）。</summary>
    private const int RankRowCount = 5;

    /// <summary>
    /// 调试用环境变量：<c>COREBUSY_CORE_VIEW=cumulative</c> 直接以累积视图启动。
    /// 累积均值要跑一段时间才有意义，这条通路让验收/截图不必每次都干等若干分钟。
    /// 与 COREBUSY_MOCK_CPU / COREBUSY_FORCE_BRAND 同一约定：发布版可用，不做 UI 入口。
    /// </summary>
    public const string CoreViewEnvVar = "COREBUSY_CORE_VIEW";

    private readonly ICpuMonitorService _monitor;

    /// <summary>整机硬件状态（内存/显卡/系统盘）。未接线时为 null，状态栏三项保持隐藏。</summary>
    private readonly ISystemHardwareService? _systemHardware;

    /// <summary>
    /// CPU 核心优化服务（v1.12.0）。未接线时为 null，优化功能整体静默停用 ——
    /// 优化属于"锦上添花"的能力，绝不能因为它不可用而拖垮监控主链路。
    /// </summary>
    private readonly ICpuOptimizationService? _optimization;

    /// <summary>电源策略服务（v1.12.0）。游戏模式需要时由本 VM 调用它应用 / 还原预设。</summary>
    private readonly IPowerPolicyService? _powerPolicy;

    /// <summary>前台全屏应用检测（游戏模式联动的输入源）。</summary>
    private readonly IGameDetector? _gameDetector;

    /// <summary>
    /// 内核驱动（PawnIO）访问状态（v1.16.1），由组合根（App）探测后注入。
    /// VM 不自己探测硬件，只负责把"为什么温度/功耗读不到"呈现成可执行的提示；
    /// 默认 <see cref="SensorAccessInfo.Ok"/>（Mock 与单测路径不提示）。
    /// </summary>
    private readonly SensorAccessInfo _sensorAccess;

    /// <summary>当前前台游戏进程名（null = 不在游戏中），用于识别进入 / 退出的**边沿**。</summary>
    private string? _activeGameProcess;

    /// <summary>游戏模式本轮是否真的应用过性能电源策略 —— 决定退出时该不该还原。</summary>
    private bool _gamePowerApplied;

    private readonly DispatcherTimer _timer;
    private readonly AppSettings _settings;
    private bool _isPerformanceMode;
    private bool _isCumulativeLoad;
    private string _statusText = "系统运行正常";

    // 状态栏药丸的语义色：正常=绿 / 性能模式=琥珀 / 传感器关闭=蓝。
    // 原先状态点是写死的绿色画刷，性能模式下文案变了而颜色不变，属于失真显示。
    private Brush _statusBrush = Frozen("#4EC48F");
    private Brush _statusDimBrush = Frozen("#264EC48F");

    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private readonly List<double> _temperatureHistory = [];
    private double[][] _heatmap = [];

    /// <summary>
    /// 热力图的双缓冲备用组（v1.19.0）。每帧把"已发布的那组"当读源、往"备用组"里写，
    /// 写完整组互换 —— 稳态下**零分配**（此前每帧要新建 16 行 × 60 列 = 约 8 KB）。
    /// 行数变化（平台 / 线程数变化）时两组一起重建，并把 <see cref="_heatmapLastUtc"/> 归零
    /// 让新矩阵重新铺满，避免新行继承到旧行的数据。
    /// </summary>
    private double[][] _heatmapSpare = [];

    /// <summary>
    /// Sparkline 序列（<see cref="PeakSeries"/>）的双缓冲（v1.19.0）。
    /// 此前每帧 <c>_temperatureHistory.ToArray()</c> 新建一个 60 元素数组；现在只在两个
    /// 固定数组之间交替发布，引用照样变化（绑定与重绘语义不变），但不再产生垃圾。
    /// </summary>
    private double[][] _peakSeriesBuffers = [];
    private int _peakSeriesIndex;

    /// <summary>劳模榜 / 摸鱼王的有界候选数组（v1.19.0）：固定 5 槽，每帧就地搬移，零分配。</summary>
    private readonly CpuCoreSnapshot?[] _busiestBoard = new CpuCoreSnapshot?[RankRowCount];
    private readonly CpuCoreSnapshot?[] _idlestBoard = new CpuCoreSnapshot?[RankRowCount];
    private DateTime _heatmapLastUtc = DateTime.MinValue;
    private string _cpuName = "读取中…";
    private string _cpuSpecs = string.Empty;
    private string _cpuVendorBadge = "CPU";
    private Brush _cpuBadgeBrush = Frozen("#1268D6");
    private string _heatmapLabels = string.Empty;

    /// <summary>扁平化的核心 Tile（顺序与热力图行序、快照序一致）。</summary>
    private readonly List<DashboardCoreVm> _allCores = [];

    /// <summary>运行期累积负载积分器（核心与核内线程同源累积）。</summary>
    private readonly CumulativeLoadTracker _cumulative = new();

    /// <summary>
    /// 运行期累积能耗积分器。与负载共用同一份 Δt（见 AccumulateSample），
    /// 但各自维护分母：负载的时间永远可读，能耗的时间要读数有效才算数。
    /// </summary>
    private readonly CumulativeEnergyTracker _energy = new();

    /// <summary>
    /// 每日能耗账本（功耗日历的数据源）。构造时即从磁盘读回历史，
    /// 因此日历天然能跨进程延续，而不是每次打开都从零开始。
    /// </summary>
    private readonly DailyEnergyLedger _energyLedger = new();

    /// <summary>
    /// WHEA 硬件错误事件源（v1.17.0）。未接线时为 null，健康度的"稳定性"维度
    /// 就少了唯一直接指向硅退化的信号 —— 其余分量照常工作，不拖垮整个功能。
    /// </summary>
    private readonly IWheaErrorSource? _whea;

    /// <summary>
    /// 核心健康度基线（v1.17.0）：从磁盘读回，由 <see cref="_healthTracker"/> **就地更新**，
    /// 本 VM 负责按期落盘。传引用而不是每帧复制，是为了不漏更新。
    /// </summary>
    private readonly Dictionary<string, CoreHealthBaselineEntry> _healthBaselines;

    /// <summary>每核健康度评分器（纯逻辑，见 <see cref="CoreHealthTracker"/> 的设计前提）。</summary>
    private readonly CoreHealthTracker _healthTracker;

    /// <summary>本帧各核评分，按核心名索引（供逐核 Tile 与概览汇总共用）。</summary>
    private readonly Dictionary<string, CoreHealthScore> _healthByCore = new(StringComparer.Ordinal);

    /// <summary>上一次落盘时的基线指纹（各核峰值之和），用于判断是否需要再写盘。</summary>
    private double _healthBaselineFingerprint = double.NaN;

    private DateTime _lastHealthSaveUtc = DateTime.UtcNow;

    /// <summary>本次运行是否已经成功写过一次基线（首次写入不受节流限制，见 SaveHealthBaselinesIfNeeded）。</summary>
    private bool _healthEverSaved;

    // ── 逐核电压档案（v1.18.0，P2）────────────────────────────────────
    //    与健康基线同节流节奏落盘；**不参与评分**，只作展示与长周期对照。
    //    理由见 CoreVoltageStore 的说明：没有固定工况点，电压差里混着工况差。
    private readonly Dictionary<string, CoreVoltageStore.CoreVoltageRecord> _coreVoltages = CoreVoltageStore.Load();

    // ── 逐核确定性自检（v1.18.0，P3）──────────────────────────────────
    //    唯一能**确定性**判定"这颗核算错了没有"的手段（借鉴 Prime95 的已知值比对方法论）。
    //    必须用户显式触发：它真的把核跑满，属状态变更。
    private readonly Dictionary<string, CoreSelfTestRecord> _selfTestArchive = CoreSelfTestStore.Load();
    private IReadOnlyList<CoreSelfTestOutcome> _selfTestOutcomes = [];
    private CancellationTokenSource? _selfTestCancellation;
    private bool _selfTestRunning;
    private string _selfTestText = "自检";
    private string _selfTestTipText = BuildSelfTestIdleTip();

    /// <summary>上一次积分对应的运行秒数 —— 与本次相减得到墙钟 Δt。</summary>
    private double _lastAccumulatedSeconds;

    /// <summary>
    /// 累积视图下的全局显示序（跨分区，按累积均值降序）。
    /// 保留上一帧顺序作为基序，配合滞回阈值抑制"均值接近的核反复换位"。
    /// </summary>
    private readonly List<DashboardCoreVm> _displayOrder = [];

    /// <summary>
    /// 名次滞回阈值（累积均值百分点）。低于该差距不换位：
    /// 每换一次位都要重建 ItemContainer（新控件从 0 动画到目标值），
    /// 均值接近的核心若是每帧互换，界面会持续闪动且白白吃掉 CPU。取 0.4 个百分点
    /// 约等于"运行 10 分钟内相差 2.4 核·秒"，是肉眼可辨的实质差距。
    /// </summary>
    private const double RankHysteresis = 0.4;

    /// <summary>
    /// 累积积分的断点判定倍率：Δt 超过「采样周期 × 该倍率」即判为休眠 / 严重节流造成的断点，
    /// 该间隔既不进分子也不进分母。
    /// <para>
    /// 取值要在两个错误之间权衡：太小会把 DispatcherTimer（Background 优先级）正常的抖动
    /// 误判成断点，白白丢掉观测时长；太大会放过短时挂起。5 倍实测足以吸收单帧超时
    /// （繁忙时可达 2-3 倍），而真实休眠通常是几十分钟起步，敏感度绰绰有余。
    /// </para>
    /// </summary>
    private const double IntegrationGapFactor = 5.0;

    /// <summary>日历格子数：6 周 × 7 天。恒定为 42，避免短月造成网格行数跳变。</summary>
    private const int CalendarCellCount = 42;

    /// <summary>日历自动重算的节流间隔（秒）。</summary>
    private const double CalendarRefreshSeconds = 10.0;

    /// <summary>
    /// 健康度基线的落盘节流间隔（秒，v1.17.0）。
    /// <para>
    /// 基线是慢变量（一颗核的峰值可能几周才刷新一次），但**必须及时落盘** ——
    /// 用户往往只开十分钟就关了，如果只在"攒够一小时"才写，基线永远建不起来，
    /// 「损耗」也就永远没有对照。5 分钟是"够勤"与"别拿 IO 烦人"的折中。
    /// </para>
    /// </summary>
    private const double HealthSaveIntervalSeconds = 300.0;

    /// <summary>热力图行源（线程粒度）：行标签在构建时固化，负载每次采样刷新。</summary>
    private (string Id, double Usage)[] _heatRows = [];

    /// <summary>热力图不变量是否已告警（避免每帧刷屏）。</summary>
    private bool _heatmapInvariantWarned;

    /// <summary>累积排名不变量是否已告警（避免每帧刷屏）。</summary>
    private bool _rankInvariantWarned;

    private double _totalUsage;
    private string _totalUsageText = "0%";
    private string _tempText = "-";
    private string _powerText = "-";
    private string _freqText = "-";
    private string _overviewTempText = "-";
    private string _overviewPowerText = "-";
    private string _overviewFreqText = "-";
    private string _overviewFanText = "-";
    private string _overviewHealthText = "-";
    private string _healthHintText = CoreHealthFormatter.BuildOverviewTooltip([]);
    private IReadOnlyList<CoreHealthScore> _healthScores = [];
    // 初值不取空串：风扇这一行恒可见，空 Tooltip 会在悬停时弹出一个空框（状态栏那三项用
    // 可见性折叠规避了同样的问题，这里没有折叠可用）。
    private string _fanHintText = "读取中…";
    private string _overviewClockText = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss");
    private string _topCoreId = "-";
    private string _topCoreUsageText = "0%";
    private string _topCoreFreqText = "-";
    private string _topCoreStatusLabel = "空闲";
    private Brush _topCoreStatusBrush = UsagePalette.StatusBrush(CoreStatus.Idle);
    private Brush _topCoreStatusDimBrush = Frozen("#263BA9FF");
    private string _peakTempText = "-";
    private string _peakTempTimeText = string.Empty;
    private IReadOnlyList<double> _peakSeries = [];
    private string _uptimeText = "0:00:00";
    private string _energyText = "-";
    private string _energyHintText = "运行期累计能耗";
    private bool _energyIsLive;

    // ===== 功耗日历（右侧「主要负载来源」之上） =====
    private DateOnly _calendarMonth;              // 正在显示的月份，恒取该月 1 日
    private DateTime _lastCalendarRefresh = DateTime.MinValue;
    private string _calendarMonthText = string.Empty;
    private string _calendarSummaryText = string.Empty;
    private bool _canGoNextMonth;

    // ===== 品牌标识（随 CPU 厂商切换，见 BrandTheme） =====
    private Visibility _amdBrandVisibility = Visibility.Collapsed;
    private Visibility _intelBrandVisibility = Visibility.Visible;
    private Visibility _neutralBrandVisibility = Visibility.Collapsed;
    private string _cpuBadgeLine1 = "CPU";
    private string _cpuBadgeLine2 = string.Empty;

    // ===== 状态栏：整机硬件状态（内存 / 显卡 / 磁盘，v1.11.0；v1.16.0 磁盘改物理总量口径并加温度） =====
    // 三项各自独立可见性：来源缺失时整项（含分隔线）折叠，不出现"有图标无数据"的半截信息。
    private string _memoryText = "-";
    private string _memoryHintText = string.Empty;
    private Visibility _memoryVisibility = Visibility.Collapsed;
    private string _gpuText = string.Empty;
    private string _gpuHintText = string.Empty;
    private Visibility _gpuVisibility = Visibility.Collapsed;
    private string _driveText = string.Empty;
    private string _driveHintText = string.Empty;
    private Visibility _driveVisibility = Visibility.Collapsed;

    // ===== 排行榜 Tab（劳模榜 / 摸鱼榜） =====
    private bool _isIdleRankTab;
    private string _rankPanelTitle = "劳模榜";
    private string _rankTabText = "摸鱼榜";
    private ObservableCollection<RankRowVm> _currentRanks = [];

    private string _topCoreTipText = "正在观察…";

    // v1.10.4：规格行延迟重同步（见 ResyncSpecsOnce 注释）。
    private bool _specsResynced;
    private int _specResyncTicks;

    public DashboardViewModel(
        ICpuMonitorService monitor,
        ISystemHardwareService? systemHardware = null,
        ICpuOptimizationService? optimization = null,
        IPowerPolicyService? powerPolicy = null,
        IGameDetector? gameDetector = null,
        SensorAccessInfo? sensorAccess = null,
        IWheaErrorSource? wheaErrorSource = null)
    {
        _monitor = monitor;
        _systemHardware = systemHardware;
        _optimization = optimization;
        _powerPolicy = powerPolicy;
        _gameDetector = gameDetector;
        _sensorAccess = sensorAccess ?? SensorAccessInfo.Ok;

        // 健康度（v1.17.0）：基线从磁盘读回、由评分器就地更新、退出时落盘。
        _whea = wheaErrorSource;
        _healthBaselines = CoreHealthStore.Load();
        _healthTracker = new CoreHealthTracker(_healthBaselines);
        _healthBaselineFingerprint = Fingerprint(_healthBaselines);
        AppLog.Write(
            $"[HEALTH] 基线载入 {_healthBaselines.Count} 核；WHEA 源={(_whea is null ? "未接线" : "已接线")}");

        var info = _monitor.GetCpuInfo();
        CpuName = info.Name;
        CpuSpecs = BuildSpecs(info);

        // CPU 身份徽章：始终反映真实 CPU 厂商（与主题无关，规范 §48 视觉优先级）。
        CpuVendorBadge = info.Vendor.ToLowerInvariant() switch
        {
            "intel" => "intel",
            "amd" => "amd",
            var v when !string.IsNullOrWhiteSpace(v) => v[..Math.Min(6, v.Length)],
            _ => "CPU",
        };
        CpuBadgeBrush = Frozen(info.VendorKind switch
        {
            CpuVendor.Intel => "#1268D6",
            CpuVendor.Amd => "#D64545",
            _ => "#455A64",
        });
        CpuBadgeLine1 = info.VendorKind switch
        {
            CpuVendor.Amd => "AMD",
            CpuVendor.Intel => "intel",
            _ => "CPU",
        };
        CpuBadgeLine2 = info.VendorKind == CpuVendor.Amd ? "RYZEN" : string.Empty;

        // 右上角品牌装饰区块：跟随主题（自动 / 手动锁定 Intel Blue / AMD Red / Neutral）。
        AmdBrandVisibility = BrandTheme.IsAmd ? Visibility.Visible : Visibility.Collapsed;
        IntelBrandVisibility = BrandTheme.EffectiveVendor == CpuVendor.Intel ? Visibility.Visible : Visibility.Collapsed;
        NeutralBrandVisibility = BrandTheme.IsNeutral ? Visibility.Visible : Visibility.Collapsed;
        // 状态栏 AMD/INTEL EDITION 徽章已随 v1.16.0 移除（BrandTheme.EditionText 仅剩启动日志使用）。

        // 排行榜恒 5 行（行对象复用，不重建列表），核心不足时留 "-" 占位。
        for (var i = 0; i < RankRowCount; i++)
        {
            BusyRanks.Add(new RankRowVm(i + 1, "-"));
            IdleRanks.Add(new RankRowVm(i + 1, "-", fixedBarBrush: UsagePalette.BrandBrush));
        }

        CurrentRanks = BusyRanks;

        // 默认视图（调试可覆盖）：必须在 BuildCoreGroups 之前定下来，
        // 否则首帧会先按实时渲染一帧再切，白闪一下。
        _isCumulativeLoad = string.Equals(
            Environment.GetEnvironmentVariable(CoreViewEnvVar),
            "cumulative",
            StringComparison.OrdinalIgnoreCase);

        if (_isCumulativeLoad)
            AppLog.Write($"core load view = cumulative (via {CoreViewEnvVar})");

        // 核心分区（规范 §52）：由采集服务的分组元数据推导，界面不硬编码 P/E。
        var snapshots = _monitor.GetCoreSnapshots();
        BuildCoreGroups(_monitor.GetCoreGroups(), snapshots);

        // 热力图按**线程**铺行（8 核 16 线程 → 16 行），与 Tile 的"物理核为格 + 核内线程条"
        // 一粗一细配合，保证标称线程数全部可见。
        // 行源同时承载行标签与最新负载：标签只在行数变化时重建，负载每帧就地刷新。
        UpdateHeatRows(snapshots);
        RefreshHeatmapLabels();

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = _monitor.SampleInterval,
        };
        _timer.Tick += (_, _) => Tick();

        // 载入用户设置（采样周期 / 硬件传感器开关 / 游戏检测），同步到底层采集与刷新定时器。
        _settings = SettingsStore.Load();
        ApplySamplingConfiguration(performanceMode: false);

        // 功耗日历：先把正在显示的月份定下来再铺 42 格，避免首帧空列表补一格的机会都没有。
        // 历史由 DailyEnergyLedger 在构造时从磁盘读回，因此这里直接就能画出过往记录。
        _calendarMonth = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
        RefreshCalendar();
    }

    /// <summary>优化服务（设置窗口用它取硬件能力、可用亲和性模式与运行中进程列表）。</summary>
    public ICpuOptimizationService? OptimizationService => _optimization;

    /// <summary>电源策略服务（设置窗口用它读取当前电源状态并执行还原）。</summary>
    public IPowerPolicyService? PowerPolicyService => _powerPolicy;

    /// <summary>核心分区（P-Core / E-Core / CPU Core / CCD n，规范 §52）。</summary>
    public ObservableCollection<CoreGroupVm> CoreGroups { get; } = [];

    /// <summary>核心劳模 TOP5。</summary>
    public ObservableCollection<RankRowVm> BusyRanks { get; } = [];

    /// <summary>摸鱼王 TOP5。</summary>
    public ObservableCollection<RankRowVm> IdleRanks { get; } = [];

    /// <summary>主要负载进程 TOP5。</summary>
    public ObservableCollection<ProcessRowVm> Processes { get; } = [];

    /// <summary>60 秒热力图数据（14 行 × 60 列，行序 P0-P5、E0-E7）。</summary>
    public double[][] Heatmap
    {
        get => _heatmap;
        private set => SetProperty(ref _heatmap, value);
    }

    /// <summary>温度 Sparkline 数据。</summary>
    public IReadOnlyList<double> PeakSeries
    {
        get => _peakSeries;
        private set => SetProperty(ref _peakSeries, value);
    }

    public string CpuName { get => _cpuName; private set => SetProperty(ref _cpuName, value); }

    public string CpuSpecs { get => _cpuSpecs; private set => SetProperty(ref _cpuSpecs, value); }

    /// <summary>CPU 厂商徽标文本（intel / amd）。</summary>
    public string CpuVendorBadge { get => _cpuVendorBadge; private set => SetProperty(ref _cpuVendorBadge, value); }

    /// <summary>CPU 厂商徽标底色。</summary>
    public Brush CpuBadgeBrush { get => _cpuBadgeBrush; private set => SetProperty(ref _cpuBadgeBrush, value); }

    /// <summary>热力图行标签（线程粒度，随真实拓扑生成，逗号分隔）。</summary>
    public string HeatmapLabels { get => _heatmapLabels; private set => SetProperty(ref _heatmapLabels, value); }

    /// <summary>热力图行数（= 逻辑线程数，供界面做密度自适应）。</summary>
    public int HeatmapRowCount => _heatRows.Length;

    public double TotalUsage { get => _totalUsage; private set => SetProperty(ref _totalUsage, value); }

    public string TotalUsageText { get => _totalUsageText; private set => SetProperty(ref _totalUsageText, value); }

    public string TempText { get => _tempText; private set => SetProperty(ref _tempText, value); }

    public string PowerText { get => _powerText; private set => SetProperty(ref _powerText, value); }

    public string FreqText { get => _freqText; private set => SetProperty(ref _freqText, value); }

    public string OverviewTempText { get => _overviewTempText; private set => SetProperty(ref _overviewTempText, value); }

    public string OverviewPowerText { get => _overviewPowerText; private set => SetProperty(ref _overviewPowerText, value); }

    public string OverviewFreqText { get => _overviewFreqText; private set => SetProperty(ref _overviewFreqText, value); }

    public string OverviewFanText { get => _overviewFanText; private set => SetProperty(ref _overviewFanText, value); }

    /// <summary>
    /// 健康度汇总文本（均分 / 最低核，v1.17.0）。无任何核心攒够样本时为纯 "-"，
    /// 而不是 0 分或 100 分 —— 没测过就是没测过。
    /// </summary>
    public string OverviewHealthText { get => _overviewHealthText; private set => SetProperty(ref _overviewHealthText, value); }

    /// <summary>健康度汇总的悬浮说明（逐核明细 + 口径与免责）。</summary>
    public string HealthHintText { get => _healthHintText; private set => SetProperty(ref _healthHintText, value); }

    /// <summary>风扇一行的悬浮详情：转速明细 + 来源（或读不到的原因，v1.16.3）。</summary>
    public string FanHintText { get => _fanHintText; private set => SetProperty(ref _fanHintText, value); }

    public string OverviewClockText { get => _overviewClockText; private set => SetProperty(ref _overviewClockText, value); }

    public string TopCoreId { get => _topCoreId; private set => SetProperty(ref _topCoreId, value); }

    public string TopCoreUsageText { get => _topCoreUsageText; private set => SetProperty(ref _topCoreUsageText, value); }

    public string TopCoreFreqText { get => _topCoreFreqText; private set => SetProperty(ref _topCoreFreqText, value); }

    public string TopCoreStatusLabel { get => _topCoreStatusLabel; private set => SetProperty(ref _topCoreStatusLabel, value); }

    public Brush TopCoreStatusBrush { get => _topCoreStatusBrush; private set => SetProperty(ref _topCoreStatusBrush, value); }

    public Brush TopCoreStatusDimBrush { get => _topCoreStatusDimBrush; private set => SetProperty(ref _topCoreStatusDimBrush, value); }

    public string PeakTempText { get => _peakTempText; private set => SetProperty(ref _peakTempText, value); }

    public string PeakTempTimeText { get => _peakTempTimeText; private set => SetProperty(ref _peakTempTimeText, value); }

    public string UptimeText { get => _uptimeText; private set => SetProperty(ref _uptimeText, value); }

    /// <summary>
    /// 运行期累计能耗（自动换档单位：mWh / Wh / kWh），显示在状态栏运行时长右侧。
    /// 从未拿到有效功耗读数时为 "-"，而不是拿 0 冒充。
    /// </summary>
    public string EnergyText { get => _energyText; private set => SetProperty(ref _energyText, value); }

    /// <summary>能耗数值的悬浮说明：统计窗口、传感器覆盖率与平均功率。</summary>
    public string EnergyHintText { get => _energyHintText; private set => SetProperty(ref _energyHintText, value); }

    /// <summary>
    /// 本帧是否读到有效功耗。false 时数值转弱色 —— 传感器缺失或性能模式暂停轮询期间，
    /// 累计量是冻结的，用正常色显示会让人以为它还在刷新。
    /// </summary>
    public bool EnergyIsLive { get => _energyIsLive; private set => SetProperty(ref _energyIsLive, value); }

    /// <summary>
    /// 功耗日历的日期格子：恒 42 格（6 周 × 7 天），翻月时**逐格更新**而非重建集合。
    /// 重建会让 WPF 重排整个 ItemsControl，是有成本的界面抖动；格子数恒定也让
    /// 日历不会因为某月只占 5 周而视觉跳变。
    /// </summary>
    public ObservableCollection<PowerCalendarDayVm> CalendarDays { get; } = [];

    /// <summary>日历标题，如「2026 年 9 月」。</summary>
    public string CalendarMonthText { get => _calendarMonthText; private set => SetProperty(ref _calendarMonthText, value); }

    /// <summary>当月汇总：本月累计能耗 + 有记录的天数。</summary>
    public string CalendarSummaryText { get => _calendarSummaryText; private set => SetProperty(ref _calendarSummaryText, value); }

    /// <summary>能否翻到下一个月：不允许越过当前月（未来没有数据）。</summary>
    public bool CanGoNextMonth { get => _canGoNextMonth; private set => SetProperty(ref _canGoNextMonth, value); }

    /// <summary>AMD 品牌装饰区块可见性。</summary>
    public Visibility AmdBrandVisibility { get => _amdBrandVisibility; private set => SetProperty(ref _amdBrandVisibility, value); }

    /// <summary>Intel 品牌装饰区块可见性。</summary>
    public Visibility IntelBrandVisibility { get => _intelBrandVisibility; private set => SetProperty(ref _intelBrandVisibility, value); }

    /// <summary>Neutral 主题装饰区块可见性（未知厂商或手动锁定中性主题）。</summary>
    public Visibility NeutralBrandVisibility { get => _neutralBrandVisibility; private set => SetProperty(ref _neutralBrandVisibility, value); }

    /// <summary>CPU 徽章主标题（AMD / intel）。</summary>
    public string CpuBadgeLine1 { get => _cpuBadgeLine1; private set => SetProperty(ref _cpuBadgeLine1, value); }

    /// <summary>CPU 徽章副标题（RYZEN，Intel 为空）。</summary>
    public string CpuBadgeLine2 { get => _cpuBadgeLine2; private set => SetProperty(ref _cpuBadgeLine2, value); }

    // v1.16.0：状态栏 AMD/INTEL EDITION 徽章移除，EditionText 属性一并删除（右上角品牌区块不受影响）。

    /// <summary>状态栏版本号：由程序集版本推导，避免与 csproj 的 &lt;Version&gt; 手写同步。</summary>
    public string VersionText { get; } = BuildVersionText();

    // ===== 状态栏整机硬件状态（v1.11.0） =====

    /// <summary>内存占用文案（已用/总量 + 占用率，如「12.4/32G 39%」）。</summary>
    public string MemoryText { get => _memoryText; private set => SetProperty(ref _memoryText, value); }

    /// <summary>内存悬浮详情（总量 / 已用 / 使用率）。</summary>
    public string MemoryHintText { get => _memoryHintText; private set => SetProperty(ref _memoryHintText, value); }

    /// <summary>内存项可见性（采集不可用时整项折叠）。</summary>
    public Visibility MemoryVisibility { get => _memoryVisibility; private set => SetProperty(ref _memoryVisibility, value); }

    /// <summary>显卡文案（短名 + 显存 + 占用 + 温度，如「RTX 4060 8G 45% 62℃」）。</summary>
    public string GpuText { get => _gpuText; private set => SetProperty(ref _gpuText, value); }

    /// <summary>显卡悬浮详情（完整型号 / 显存 / 占用 / 温度）。</summary>
    public string GpuHintText { get => _gpuHintText; private set => SetProperty(ref _gpuHintText, value); }

    /// <summary>显卡项可见性。</summary>
    public Visibility GpuVisibility { get => _gpuVisibility; private set => SetProperty(ref _gpuVisibility, value); }

    /// <summary>磁盘文案（物理总量 + 已用率 + 温度，如「1.9T 已用61% 43℃」）。</summary>
    public string DriveText { get => _driveText; private set => SetProperty(ref _driveText, value); }

    /// <summary>磁盘悬浮详情（磁盘块数 / 物理总量 / 卷占用与可用 / 温度）。</summary>
    public string DriveHintText { get => _driveHintText; private set => SetProperty(ref _driveHintText, value); }

    /// <summary>磁盘项可见性。</summary>
    public Visibility DriveVisibility { get => _driveVisibility; private set => SetProperty(ref _driveVisibility, value); }

    /// <summary>状态栏药丸前景色（脉冲图标 + 状态文案共用），随运行档位切换。</summary>
    public Brush StatusBrush { get => _statusBrush; private set => SetProperty(ref _statusBrush, value); }

    /// <summary>状态栏药丸底色（与前景色同源的半透明变体）。</summary>
    public Brush StatusDimBrush { get => _statusDimBrush; private set => SetProperty(ref _statusDimBrush, value); }

    /// <summary>当前排行榜列表（劳模榜 / 摸鱼榜 由 Tab 切换）。</summary>
    public ObservableCollection<RankRowVm> CurrentRanks
    {
        get => _currentRanks;
        private set => SetProperty(ref _currentRanks, value);
    }

    /// <summary>排行榜面板标题。</summary>
    public string RankPanelTitle { get => _rankPanelTitle; private set => SetProperty(ref _rankPanelTitle, value); }

    /// <summary>排行榜右侧可点击的另一个 Tab 文案。</summary>
    public string RankTabText { get => _rankTabText; private set => SetProperty(ref _rankTabText, value); }

    /// <summary>最忙核心的调侃提示。</summary>
    public string TopCoreTipText { get => _topCoreTipText; private set => SetProperty(ref _topCoreTipText, value); }

    /// <summary>状态栏运行状态文案（运行正常 / 采样周期 / 性能模式 / 传感器开关）。</summary>
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    // ===== 内核驱动（PawnIO）访问提示（v1.16.1）=====
    //
    // 背景：CPU 温度/功耗、风扇转速、硬盘温度全部经 LibreHardwareMonitor 的内核驱动读取，
    // PawnIO 设备只对提升后的进程开放。未以管理员身份运行时这些数值读不到，
    // 而界面上此前只剩一个 "-"（AMD 的 SMU 读数还会被填成 0，见传感器层守卫），
    // 用户无法分辨"机器没这个传感器"与"没提权"。于是把原因显式呈现出来，并给出一步到位的动作。

    /// <summary>概览卡右上角的提示短标（空 = 内核访问正常，不显示）。</summary>
    public string SensorWarningBadge => _sensorAccess.Badge;

    /// <summary>提示的完整解释（Tooltip；与 debug.log 里那条 [SENSOR] 原因同源）。</summary>
    public string SensorWarningDetail => _sensorAccess.Detail;

    /// <summary>
    /// 是否显示内核访问提示。只在内核驱动有问题、且**本轮确实在采传感器**时提示：
    /// 性能模式会主动停掉传感器轮询，那是我们自己关的，再怪权限就成了误报。
    /// </summary>
    public Visibility SensorWarningVisibility =>
        _sensorAccess.HasProblem && _settings.SensorsEnabled && !_isPerformanceMode
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>是否处于性能模式（低开销：低开销档刷新 + 暂停硬件传感器轮询）。</summary>
    public bool IsPerformanceMode { get => _isPerformanceMode; private set => SetProperty(ref _isPerformanceMode, value); }

    /// <summary>
    /// 核心区当前是否为累积负载视图。false = 实时负载（默认），true = 运行期累积均值排名。
    /// </summary>
    public bool IsCumulativeLoad { get => _isCumulativeLoad; private set => SetProperty(ref _isCumulativeLoad, value); }

    /// <summary>启动：预热热力图（用当前采样铺满 60 列），随后按采样周期刷新。</summary>
    public void Start()
    {
        // 预热：一打开就是满 60 列，而不是空白渐长；真实历史随墙钟秒滚动累积。
        PushHeatmap(_monitor.GetCoreSnapshots(), prefill: true);
        AppendTemperatureHistory(_monitor.GetSnapshot().PackageTemperatureC);

        Tick(); // 首帧完整刷新，避免要等一个采样周期才出数值。
        _timer.Start();
    }

    /// <summary>停止刷新，并把每日能耗账本与健康度基线立即落盘（退出前最后的保存机会）。</summary>
    public void Stop()
    {
        _timer.Stop();
        _energyLedger.SaveIfNeeded(force: true);
        SaveHealthBaselinesIfNeeded(force: true);
    }

    /// <summary>
    /// 切换性能模式（标题栏"性能模式"按钮）。开启后本工具以低开销档运行：
    /// 刷新降为 2000ms 且暂停硬件传感器轮询，尽量减少对前台程序（游戏/跑分）的干扰。
    /// </summary>
    public void TogglePerformanceMode()
    {
        IsPerformanceMode = !IsPerformanceMode;
        ApplySamplingConfiguration(IsPerformanceMode);
        Tick();
        AppLog.Write($"performance mode = {IsPerformanceMode}");
    }

    /// <summary>
    /// 切换核心区的负载口径（核心分区标题右侧的「实时 / 累积」开关）。
    /// <para>
    /// 累积积分器**始终在跑**（不区分当前视图），因此切到累积视图时立刻就能看到完整历史，
    /// 而不是从切换那一刻才开始从零累积——"运行时间内"这个口径不因视图切换而改变。
    /// </para>
    /// </summary>
    public void SetCumulativeLoad(bool cumulative)
    {
        if (IsCumulativeLoad == cumulative)
            return;

        IsCumulativeLoad = cumulative;
        AppLog.Write($"core load view = {(cumulative ? "cumulative" : "live")}");
        Tick(); // 立即按新口径重绘一帧，无需等下一个采样周期。
    }

    /// <summary>清空累积统计并立即重绘。运行期内可反复重置，用于观察"从现在起"的分布。</summary>
    public void ResetCumulativeLoad()
    {
        _cumulative.Reset();
        _displayOrder.Clear(); // 排名重新起步，避免沿用重置前的名次基序。
        _lastAccumulatedSeconds = _uptime.Elapsed.TotalSeconds;
        AppLog.Write("cumulative load reset");
        Tick();
    }

    /// <summary>
    /// 切换排行榜面板的 Tab（劳模榜 ↔ 摸鱼榜）。
    /// 两个榜单的分类集合始终是固定的 P0-P4，切换只改变显示哪一份。
    /// </summary>
    public void ToggleRankTab()
    {
        _isIdleRankTab = !_isIdleRankTab;
        CurrentRanks = _isIdleRankTab ? IdleRanks : BusyRanks;
        RankPanelTitle = _isIdleRankTab ? "摸鱼榜" : "劳模榜";
        RankTabText = _isIdleRankTab ? "劳模榜" : "摸鱼榜";
    }

    /// <summary>
    /// 优化功能每帧钩子（v1.12.0）：套用进程规则 + 游戏模式联动。
    /// <para>
    /// 游戏模式只在**边沿**动作（进入 / 退出各一次），不是每帧重复套用。
    /// 每帧重复写的代价不只是多余的系统调用：用户若在游戏运行中手动改过该进程的
    /// 优先级，下一帧就会被本工具改回去，表现为"我的设置老是弹回"。
    /// </para>
    /// </summary>
    private void TickOptimization()
    {
        var optimizationService = _optimization;
        if (optimizationService is null)
            return;

        var optimization = _settings.Optimization;

        // 总开关关闭时也要调用一次服务：它需要借这次调用撤销「仅游戏时生效」规则的残留，
        // 否则用户以为"关了就不影响"，相关进程却仍带着被压低的优先级在跑。
        var gameModeActive = optimization.Enabled && TrackGameMode(optimization);
        optimizationService.ApplyRulesNow(optimization, gameModeActive);
    }

    /// <summary>
    /// 游戏模式边沿检测。返回当前是否处于游戏模式 —— 进程规则据此过滤「仅游戏时生效」的项。
    /// <para>
    /// 把"检测"与"套用规则"分开，是为了让优化服务不去自己探测游戏状态：两处判定一旦
    /// 不一致，就会表现为"界面说在游戏中、规则却不生效"，而且没有任何线索指出谁判错了。
    /// </para>
    /// </summary>
    private bool TrackGameMode(OptimizationSettings optimization)
    {
        var optimizationService = _optimization;
        var detector = _gameDetector;
        if (optimizationService is null || detector is null
            || !optimization.GameModeEnabled || !_settings.GameDetectionEnabled)
            return false;

        string? game;
        try
        {
            game = detector.DetectForegroundGame();
        }
        catch
        {
            // 检测失败按"不在游戏中"处理：不能因为一次窗口枚举异常就打断采样主循环。
            game = null;
        }

        if (string.Equals(game, _activeGameProcess, StringComparison.OrdinalIgnoreCase))
            return game is not null;

        _activeGameProcess = game;
        optimizationService.ApplyGameMode(optimization, game);

        if (game is null)
        {
            // 只有"本次游戏会话确实由本工具改过电源方案"、且用户在设置里没有选择固定预设时，
            // 才还原。否则会把用户主动选的「性能优先」当成游戏残留一起撤掉 ——
            // 这是两处都对、组合起来错的典型场景。
            if (_gamePowerApplied && optimization.PowerPreset == PowerPreset.None && _powerPolicy is not null)
            {
                _powerPolicy.Restore(out var error);
                AppLog.Write($"game mode exit: power restored ({error})");
            }

            _gamePowerApplied = false;
            AppLog.Write("game mode exit: optimization reverted");
            return false;
        }

        if (optimization.GameAppliesPowerPreset && !_gamePowerApplied && _powerPolicy is not null)
        {
            // 先应用成功再置位，避免退出时去还原一个从未改动过的方案。
            _gamePowerApplied = _powerPolicy.Apply(PowerPreset.Performance, out var error);
            if (!_gamePowerApplied)
                AppLog.Write($"game mode power preset FAILED: {error}");
        }

        AppLog.Write(
            $"game mode enter: {game} affinity={optimization.GameAffinity} priority={optimization.GamePriority}");
        return true;
    }

    /// <summary>
    /// 应用设置窗口结果（采样周期 / 主题 / 核心分区 / 硬件传感器开关 / 游戏检测 / 核心优化）。
    /// 用户显式设置优先，会退出性能模式。
    /// 主题与核心分区需在窗口构建前生效（StaticResource 解析时机），此处只落盘，
    /// 由主窗口提示重启后生效。
    /// </summary>
    public void ApplySettings(AppSettings settings)
    {
        var gameModeWasActive = _settings.Optimization.GameModeEnabled && _settings.Optimization.Enabled;

        _settings.IntervalMilliseconds = NormalizeInterval(settings.IntervalMilliseconds);
        _settings.SensorsEnabled = settings.SensorsEnabled;
        _settings.GameDetectionEnabled = settings.GameDetectionEnabled;
        _settings.Theme = settings.Theme;
        _settings.CoreLayout = settings.CoreLayout;
        _settings.CloseToTray = settings.CloseToTray;
        _settings.Optimization = settings.Optimization;
        SettingsStore.Save(_settings);

        // 优化总开关或游戏联动被关掉时，若正处在游戏会话中，立即撤销并复位边沿状态 ——
        // 否则 _activeGameProcess 会一直停在游戏名上，重新开启后要等下次切换窗口才生效。
        var optimizationEnabledNow = _settings.Optimization.Enabled && _settings.Optimization.GameModeEnabled;
        if (!optimizationEnabledNow)
        {
            if (_activeGameProcess is not null)
                _optimization?.ApplyGameMode(_settings.Optimization, null);

            if (_gamePowerApplied && _settings.Optimization.PowerPreset == PowerPreset.None)
                _powerPolicy?.Restore(out _);

            _activeGameProcess = null;
            _gamePowerApplied = false;
        }

        IsPerformanceMode = false;
        ApplySamplingConfiguration(performanceMode: false);
        AppLog.Write(
            $"settings applied: interval={_settings.IntervalMilliseconds} sensors={_settings.SensorsEnabled} " +
            $"game={_settings.GameDetectionEnabled} theme={_settings.Theme} layout={_settings.CoreLayout} " +
            $"closeToTray={_settings.CloseToTray} opt={_settings.Optimization.Enabled} " +
            $"rules={_settings.Optimization.Rules.Count}");

        Tick(); // 立即按新配置刷新一帧，传感器开关的视觉变化无需等下个周期。
    }

    /// <summary>把当前配置（含性能模式覆盖）应用到采集服务与刷新定时器，并刷新状态栏文案。</summary>
    private void ApplySamplingConfiguration(bool performanceMode)
    {
        var intervalMs = performanceMode
            ? PerformanceModeIntervalMs                       // 性能模式：低开销档。
            : NormalizeInterval(_settings.IntervalMilliseconds);

        // 性能模式暂停硬件传感器轮询（温度/功耗/风扇显示为 "-"）。
        _monitor.SensorsEnabled = performanceMode ? false : _settings.SensorsEnabled;
        _monitor.SampleInterval = TimeSpan.FromMilliseconds(intervalMs);
        _timer.Interval = TimeSpan.FromMilliseconds(intervalMs);
        OnPropertyChanged(nameof(SensorWarningVisibility)); // 传感器开关/性能模式都会改变该提示的前提
        UpdateStatusText();
    }

    private void UpdateStatusText()
    {
        var effective = (int)_monitor.SampleInterval.TotalMilliseconds;
        StatusText = _isPerformanceMode
            ? $"性能模式 · 采样 {effective}ms · 硬件传感器已暂停"
            : _settings.SensorsEnabled
                ? $"系统运行正常 · 采样 {effective}ms"
                : $"系统运行正常 · 采样 {effective}ms · 硬件传感器已关闭";

        // 语义色：正常=绿，性能模式=琥珀（降档提示），传感器关闭=蓝（信息态，非告警）。
        (StatusBrush, StatusDimBrush) = _isPerformanceMode
            ? (Frozen("#E8C044"), Frozen("#26E8C044"))
            : _settings.SensorsEnabled
                ? (Frozen("#4EC48F"), Frozen("#264EC48F"))
                : (Frozen("#6FA8DC"), Frozen("#266FA8DC"));
    }

    /// <summary>状态栏版本号（v主.次.修订），从程序集版本读取以消除手写同步。</summary>
    private static string BuildVersionText()
    {
        var v = typeof(DashboardViewModel).Assembly.GetName().Version;
        return v is null ? "v-" : $"v{v.Major}.{v.Minor}.{v.Build}";
    }

    /// <summary>采样周期归一化到合法档位（500/1000/2000ms）。</summary>
    private static int NormalizeInterval(int value) => value switch
    {
        500 => 500,
        2000 => 2000,
        _ => 1000,
    };

    /// <summary>
    /// 按采集服务的分组元数据构建界面分区与 Tile（顺序与快照序、热力图行序严格一致）。
    /// 段数 ≥3 时整体收一档，保证一屏内不出现纵向滚动条（规范 §9）。
    /// </summary>
    private void BuildCoreGroups(IReadOnlyList<CpuCoreGroup> groups, IReadOnlyList<CpuCoreSnapshot> snapshots)
    {
        var dense = groups.Count >= 3;
        var index = 0;

        foreach (var group in groups)
        {
            var count = group.CoreIds.Count;

            // 列数上限 4：窄窗（900px）下 8 颗仍能保持 ≥130px 的卡宽，超出后折行。
            var columns = count <= 4 ? Math.Max(1, count)
                : count <= 8 ? 4
                : count <= 12 ? 6
                : 8;

            var compact = dense || group.Kind == CoreGroupKind.Efficiency || columns >= 6;
            var cores = new List<DashboardCoreVm>(count);

            foreach (var id in group.CoreIds)
            {
                var snapshot = index < snapshots.Count ? snapshots[index] : null;
                var vm = new DashboardCoreVm(id, compact, snapshot?.Threads);
                if (snapshot is not null)
                    vm.ApplyLive(snapshot);

                index++;
                cores.Add(vm);
                _allCores.Add(vm);
            }

            // 负载口径开关是全局唯一的，只挂到第一段分区（CoreGroupVm 内部据此决定可见性）。
            CoreGroups.Add(new CoreGroupVm(group, compact, columns, cores, isFirstGroup: CoreGroups.Count == 0));
        }
    }

    /// <summary>
    /// 累积负载积分的**断点保护**。
    /// <para>
    /// 正常帧的 Δt ≈ 采样周期，这一帧读到的缓存值也确实对应那段活动窗口。
    /// 但系统休眠 / 严重节流下，两次帧之间可能隔了几十分钟甚至几小时，而这一帧
    /// 拿到的只是「复活那一刻」的一份读数。照常积分会把整段挂起时长加进分母，
    /// 并让一个不具代表性的值乘上巨量权重 —— 一次休眠足以让累积数小时的均值失真，
    /// 而且需要同等长度的正常运行时间才能把它稀释掉。
    /// </para>
    /// <para>
    /// 对策：超过 <see cref="IntegrationGapFactor"/> 倍采样周期的间隔判为断点，
    /// 既不进分子也不进分母，使「运行时间内」严格等于「真实在采样的时间」。
    /// 注意无论是否跳过，<c>_lastAccumulatedSeconds</c> 都**必须推进** ——
    /// 否则断点之后每一帧都会继续被判定为断点，积分永久停摆。
    /// </para>
    /// <para>
    /// 负载与能耗**共用同一份 Δt 判定**：两路累计描写同一段时间轴，若各自维护基准，
    /// 一次休眠后可能只跳过其中一路，两个窗口就此错开且再也无法对齐。
    /// </para>
    /// </summary>
    private void AccumulateSample(
        IReadOnlyList<CpuCoreSnapshot> snapshots, double powerWatt, double elapsedSeconds)
    {
        var delta = elapsedSeconds - _lastAccumulatedSeconds;
        _lastAccumulatedSeconds = elapsedSeconds;

        if (delta < 0)
            return; // 秒表回拨或重置后的首帧：不产生负权重。

        var maxDelta = _monitor.SampleInterval.TotalSeconds * IntegrationGapFactor;
        if (delta > maxDelta)
        {
            AppLog.Write(
                $"cumulative: gap skipped, delta={delta:0.0}s > max={maxDelta:0.0}s");
            return;
        }

        _cumulative.Accumulate(snapshots, delta);

        // Accumulate 内部做读数有效性判定（有限值 + 0..1000W 合理域），HasLiveSample 即其结论。
        _energy.Accumulate(powerWatt, delta);

        // 同一份样本同时进每日账本——日历与状态栏的能耗因此严格同源，不会互相打架。
        // v1.10.3：只有通过能耗累加器合理性校验的读数才进账本。此前账本自带独立校验
        // （仅要求有限值与非负），缺 1000W 上限——单位错误的离谱读数会被状态栏拒收、
        // 却混进日历，两路口径就此分叉。
        // Record 返回 true 表示刚跨过零点：昨天那一格要立刻补上，不能等下一次节流刷新，
        // 否则用户会在零点后短暂看到「昨天还是空的」。
        if (_energy.HasLiveSample && _energyLedger.Record(powerWatt, delta, DateTime.Now))
        {
            AppLog.Write("energy ledger: day rolled over");
            RefreshCalendar();
        }

        _energyLedger.SaveIfNeeded();
    }

    /// <summary>一次采样：读取快照并刷新全部面板。</summary>
    public void Tick()
    {
        var snapshots = _monitor.GetCoreSnapshots();

        // Δt 的读数点必须紧贴 GetCoreSnapshots()：每帧都在同一相位取一次墙钟，
        // 相邻两帧之差才等于这段 return-to-return 的真实间隔。若放在 GetSnapshot()
        // （含传感器读取与锁竞争，耗时随帧波动）之后，差值会多出 S_i - S_{i-1} 的
        // 抖动项，等于往积分权重里掺噪声。
        var elapsed = _uptime.Elapsed;
        var snapshot = _monitor.GetSnapshot();

        UptimeText = $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";

        // 1. 累积负载与累积能耗的积分。**始终累积**（与当前视图无关），这样切到累积
        //    视图、或随时瞄一眼状态栏能耗，看到的都是完整历史而非从某刻才起步。
        //    Δt 取墙钟差而非采样周期常量：周期可被用户改档、性能模式另有一套覆盖，
        //    用常量会让积分权重随档位失真。
        AccumulateSample(snapshots, snapshot.PackagePowerW, elapsed.TotalSeconds);

        // 2. 核心 Tile + 热力图推入（扁平序 = 分区序 = 热力图行序）。
        if (IsCumulativeLoad)
            ApplyCumulativeCoreView(snapshots);
        else
            ApplyLiveCoreView(snapshots);

        PushHeatmap(snapshots);

        // 2b. 核心健康度（v1.17.0）。放在核心 Tile 与热力图之后：它依赖的是**窗口统计**，
        //     晚一帧看到分数没有影响；而它自己要在锁外跑一次事件日志查询（内部 5 分钟节流），
        //     排在所有"每帧必画"的面板之后，万一查询变慢也不会拖慢它们。
        ApplyHealthView(snapshots, snapshot.PackageTemperatureC);

        // 3. 温度历史（Sparkline）。
        AppendTemperatureHistory(snapshot.PackageTemperatureC);
        PublishPeakSeries();

        // 4. 总览指标（NaN 安全：传感器缺失显示 "-"）。
        TotalUsage = snapshot.TotalUsagePercent;
        TotalUsageText = $"{snapshot.TotalUsagePercent:0}%";
        TempText = FmtTempC(snapshot.PackageTemperatureC, "℃");
        PowerText = FmtValueUnit(snapshot.PackagePowerW, "0", "W");
        FreqText = FmtFreq(snapshot.AverageFrequencyGHz);
        OverviewTempText = FmtTempC(snapshot.PackageTemperatureC, " ℃");
        OverviewPowerText = FmtValueUnit(snapshot.PackagePowerW, "0", "W");
        OverviewFreqText = FmtFreq(snapshot.AverageFrequencyGHz);
        OverviewFanText = FmtValueUnit(snapshot.FanRpm, "0", "RPM");
        FanHintText = BuildFanHint(snapshot);
        OverviewClockText = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss");

        // 4b. 累积能耗（状态栏）：与主显功耗同源，但是从读数能否拿到、而非读到了多少出发。
        UpdateEnergyReadout();

        // 4c. 能耗日历：今天那一格会随时间增长，按节流间隔重算（详见 MaybeRefreshCalendar）。
        MaybeRefreshCalendar();

        // 4d. 规格行延迟重同步（v1.10.4）。
        ResyncSpecsOnce();

        // 4e. 整机硬件状态（内存/显卡/磁盘，v1.11.0；v1.16.0 加占用与温度）。文本未变时
        //     SetProperty 不触发通知，因此每帧调用不会造成无效重绘；磁盘等慢变量由服务内部节流。
        RefreshSystemHardware(snapshot);

        // 4f. CPU 核心优化（v1.12.0）：进程规则套用 + 游戏模式联动。
        //     规则重扫在服务内部按 5 秒节流，这里每帧调用只付出一次时间比较的代价。
        TickOptimization();

        // 5. 峰值温度。传感器不可用时 PeakTemperatureC 为 NaN，但 PeakTemperatureTime
        //    仍有值（服务构造时刻），直接显示会得到"峰值 - 09:12:33"这种半截信息，故时刻一并留空。
        PeakTempText = FmtTempC(snapshot.PeakTemperatureC, "℃");
        PeakTempTimeText = double.IsNaN(snapshot.PeakTemperatureC)
            ? string.Empty
            : snapshot.PeakTemperatureTime.ToString("HH:mm:ss");

        // 6. 劳模榜 / 摸鱼王 + 最忙的核心：单次遍历得到三项（v1.19.0 起不再用 LINQ）。
        //    恒 5 行（行对象复用），名次取全局 TOP5 / BOTTOM5，核心不足 5 颗时缺位显示 "-"
        //    （规范 §24；设计稿中两张榜的核心名并不相同）。
        //    排序语义与旧实现逐字对齐：TOP5 = 使用率降序 + 核心名 Ordinal 升序；
        //    BOTTOM5 = 使用率升序 + 核心名 Ordinal 升序；"最忙的核心"取**先出现的**最大值
        //    （旧写法 OrderByDescending(...).FirstOrDefault() 是稳定排序，同率取源序首个）。
        for (var i = 0; i < RankRowCount; i++)
        {
            _busiestBoard[i] = null;
            _idlestBoard[i] = null;
        }

        CpuCoreSnapshot? topSnapshot = null;
        for (var i = 0; i < snapshots.Count; i++)
        {
            var s = snapshots[i];
            InsertRanked(_busiestBoard, s, descending: true);
            InsertRanked(_idlestBoard, s, descending: false);

            if (topSnapshot is null || s.UsagePercent > topSnapshot.UsagePercent)
                topSnapshot = s;
        }

        for (var i = 0; i < RankRowCount; i++)
        {
            var busy = _busiestBoard[i];
            BusyRanks[i].Apply(i + 1, busy?.Id ?? "-", busy?.UsagePercent);

            var idle = _idlestBoard[i];
            IdleRanks[i].Apply(i + 1, idle?.Id ?? "-", idle?.UsagePercent);
        }

        if (topSnapshot is not null)
        {
            TopCoreId = topSnapshot.Id;
            TopCoreUsageText = $"{topSnapshot.UsagePercent:0}%";
            TopCoreFreqText = FmtFreq(topSnapshot.FrequencyGHz);
            TopCoreStatusLabel = UsagePalette.StatusLabel(topSnapshot.Status);
            TopCoreStatusBrush = UsagePalette.StatusBrush(topSnapshot.Status);
            TopCoreStatusDimBrush = UsagePalette.StatusDimBrush(topSnapshot.Status);
            TopCoreTipText = topSnapshot.UsagePercent switch
            {
                >= 85 => "😊 这家伙一直在加班…",
                >= 60 => "😅 忙得脚不沾地…",
                >= 30 => "🙂 在认真干活…",
                _ => "😴 大家都在摸鱼…",
            };
        }

        // 7. 主要负载进程（列表随数据源收缩，避免残留上一次采样的旧行）。
        while (Processes.Count > snapshot.TopProcesses.Count)
            Processes.RemoveAt(Processes.Count - 1);

        for (var i = 0; i < snapshot.TopProcesses.Count; i++)
        {
            var p = snapshot.TopProcesses[i];
            if (i < Processes.Count)
            {
                if (Processes[i].Name == p.Name)
                    Processes[i].Update(p.UsagePercent);
                else
                    Processes[i] = new ProcessRowVm(p.Name, p.UsagePercent, p.IconKind);
            }
            else
            {
                Processes.Add(new ProcessRowVm(p.Name, p.UsagePercent, p.IconKind));
            }
        }
    }

    /// <summary>
    /// 健康度视图（v1.17.0）：喂一帧给评分器、取回逐核分数、分发给 Tile 与概览行。
    /// <para>
    /// **按核心名匹配，而不是按下标。** 评分器返回的顺序由其内部字典决定，
    /// 与 <c>snapshots</c> 的自然核序不保证一致；按下标分发会把分数安到错误的核上，
    /// 而这种错位在界面上看起来完全正常（都是 0–100 的数），肉眼永远发现不了。
    /// </para>
    /// </summary>
    private void ApplyHealthView(IReadOnlyList<CpuCoreSnapshot> snapshots, double packageTempC)
    {
        // WHEA 查询在读取器内部按 5 分钟节流，这里逐帧调用只付出一次比较的代价。
        var whea = _whea?.Read() ?? WheaErrorCounts.Unavailable("未接线");
        _healthTracker.SetWhea(whea.ScoringCorrected, whea.ScoringFatal, whea.Available);

        _healthTracker.Observe(snapshots, packageTempC, DateTime.UtcNow);
        _healthScores = _healthTracker.Score();

        _healthByCore.Clear();
        foreach (var score in _healthScores)
            _healthByCore[score.CoreId] = score;

        foreach (var vm in _allCores)
            vm.ApplyHealth(_healthByCore.TryGetValue(vm.Id, out var score) ? score : null);

        // 逐核电压档案（P2）：只在有读数的帧上就地更新，落盘走与健康基线相同的节流节奏。
        var voltageNow = DateTime.Now;
        foreach (var core in snapshots)
        {
            CoreVoltageStore.Observe(
                _coreVoltages, core.Id, core.CoreVoltageV, core.HardwareActivityPercent, voltageNow);
        }

        UpdateHealthSummary(_healthScores);
        SaveHealthBaselinesIfNeeded(force: false);
        SaveVoltageArchiveIfNeeded(force: false);
    }

    /// <summary>
    /// 概览行文案与悬浮说明（v1.18.0）。
    /// <para>
    /// 文案与"未判分原因"统一由 <see cref="CoreHealthFormatter"/> 给出，这里不再自己拼字符串 ——
    /// 同一个规则写在两处，是这个功能最容易出的错（v1.17.x 的"1/8 核 → 整机 100"
    /// 就是判分与展示对"够不够格"这件事各有一套理解）。
    /// </para>
    /// </summary>
    private void UpdateHealthSummary(IReadOnlyList<CoreHealthScore> scores)
    {
        OverviewHealthText = CoreHealthFormatter.BuildOverviewText(scores);
        HealthHintText = CoreHealthFormatter.BuildOverviewTooltip(scores, SelfTestSummaryText);
    }

    /// <summary>最近一轮自检摘要（供概览悬浮显示；未跑过时为空串）。</summary>
    private string SelfTestSummaryText
        => CoreHealthFormatter.BuildSelfTestSummary(_selfTestOutcomes);

    /// <summary>
    /// 基线落盘。<paramref name="force"/> 用于退出路径。
    /// <para>
    /// 指纹 = 各核峰值之和。用指纹而不是"改动标志位"，是因为基线由评分器**就地**更新，
    /// 本 VM 拿不到"刚刚改过"的通知；指纹能可靠地识别出"这一轮确实有新峰值"。
    /// </para>
    /// <para>
    /// **首次建立基线时立即落盘**（<c>_healthEverSaved</c>），不受节流限制：
    /// 用户很可能只开几分钟就关了，如果第一份基线也要等满 5 分钟才写，
    /// 那么这个功能对"偶尔看一眼"的使用方式永远是空转 —— 基线建不起来，
    /// 「损耗」也就永远没有对照。节流只用于压制后续的反复改写。
    /// </para>
    /// </summary>
    private void SaveHealthBaselinesIfNeeded(bool force)
    {
        if (_healthBaselines.Count == 0)
            return;

        var fingerprint = Fingerprint(_healthBaselines);
        if (!force && fingerprint == _healthBaselineFingerprint)
            return;

        var now = DateTime.UtcNow;
        if (!force
            && _healthEverSaved
            && (now - _lastHealthSaveUtc).TotalSeconds < HealthSaveIntervalSeconds)
        {
            return;
        }

        _lastHealthSaveUtc = now;
        _healthEverSaved = true;
        _healthBaselineFingerprint = fingerprint;
        CoreHealthStore.Save(_healthBaselines);
    }

    private static double Fingerprint(IReadOnlyDictionary<string, CoreHealthBaselineEntry> baselines)
    {
        double sum = 0;
        foreach (var entry in baselines.Values)
        {
            if (!double.IsNaN(entry.PeakGHz))
                sum += entry.PeakGHz;
        }

        return sum + baselines.Count;
    }

    /// <summary>电压档案上次落盘时刻（与健康基线各自独立节流）。</summary>
    private DateTime _lastVoltageSaveUtc = DateTime.UtcNow;

    /// <summary>
    /// 逐核电压档案落盘（v1.18.0，P2）。节流与健康基线一致：它是慢变量
    /// （峰值只会在更热的工况下刷新），没必要每帧写盘。
    /// </summary>
    private void SaveVoltageArchiveIfNeeded(bool force)
    {
        if (_coreVoltages.Count == 0)
            return;

        var now = DateTime.UtcNow;
        if (!force && (now - _lastVoltageSaveUtc).TotalSeconds < HealthSaveIntervalSeconds)
            return;

        _lastVoltageSaveUtc = now;
        CoreVoltageStore.Save(_coreVoltages);
    }

    // ═══════════════════════════════════════════════════════════════
    //  逐核确定性自检（v1.18.0，P3）
    //
    //  借鉴 Prime95 / mprime 的 torture test 方法论（**只用方法、不引代码**，其 EULA 非 FOSS）：
    //  让被测核算一段结果已知的计算，比对结果。这是整个工具里唯一能**确定性**判定
    //  "这颗核算错了没有"的手段 —— 频率比值、电压、热裕度全都是间接推断。
    //
    //  三条硬约束（都在代码里落实，不只是注释）：
    //    ① 必须用户显式触发 —— 它真的把核跑满，属状态变更；
    //    ② 结果不进评分 —— 一次 2 秒的通过不能证明机器健康，折进 0–100 只会稀释信号的锋利度；
    //    ③ 可中断 —— 不能停的满载按钮没人敢点，功能等于不存在。
    // ═══════════════════════════════════════════════════════════════

    /// <summary>自检按钮文案（空闲="自检" / 进行中="停止"）。</summary>
    public string SelfTestText
    {
        get => _selfTestText;
        private set => SetProperty(ref _selfTestText, value);
    }

    /// <summary>自检按钮悬浮说明（含运行进度）。</summary>
    public string SelfTestTipText
    {
        get => _selfTestTipText;
        private set => SetProperty(ref _selfTestTipText, value);
    }

    /// <summary>是否正在自检。</summary>
    public bool IsSelfTestRunning => _selfTestRunning;

    /// <summary>
    /// 按钮的两种语义合一：空闲时启动、进行中时中断。
    /// </summary>
    public void ToggleSelfTest()
    {
        if (_selfTestRunning)
        {
            _selfTestCancellation?.Cancel();
            AppLog.Write("[SELFTEST] 用户请求中断");
            return;
        }

        StartSelfTest();
    }

    private static string BuildSelfTestIdleTip()
        => "逐核确定性自检：" + Environment.NewLine
           + "对每颗核依次绑核跑一段结果已知的计算（整数两条独立路径互证 + 浮点比特稳定性）。"
           + Environment.NewLine + Environment.NewLine
           + "**会真的把 CPU 跑满**（逐核依次，每核约 "
           + $"{CoreSelfTestArchive.DefaultDurationSeconds:0} 秒）。"
           + Environment.NewLine
           + "开始后本按钮变为「停止」，随时可中断。"
           + Environment.NewLine + Environment.NewLine
           + "结果是硬件级的事实记录（这颗核算错了没有），**不进健康度评分**；"
           + "结论落盘到 core-selftest.json，可在悬浮里看最近一轮结果。";

    private void StartSelfTest()
    {
        // 目标取当前快照里的物理核及其逻辑处理器索引。
        // **没有 LP 索引的核一律跳过**：绑不上核的自检只是"在整机某处跑了跑"，
        // 把结论记到某颗核头上是错的（那条约束由 CoreSelfTestRunner 再兜一层）。
        var targets = _monitor.GetCoreSnapshots()
            .Where(s => s.LogicalProcessors.Count > 0)
            .ToArray();

        if (targets.Length == 0)
        {
            SelfTestTipText = "自检无法开始：未拿到核心的逻辑处理器索引（采集层尚未就绪）。";
            AppLog.Write("[SELFTEST] 无法开始：无可用绑核目标");
            return;
        }

        _selfTestRunning = true;
        _selfTestCancellation = new CancellationTokenSource();
        var token = _selfTestCancellation.Token;

        SelfTestText = "停止";
        SelfTestTipText = $"自检进行中：0/{targets.Length} 核（每核约 {CoreSelfTestArchive.DefaultDurationSeconds:0} 秒）";
        AppLog.Write($"[SELFTEST] 开始：{targets.Length} 核 × {CoreSelfTestArchive.DefaultDurationSeconds:0} 秒，逐核绑核执行");

        var duration = TimeSpan.FromSeconds(CoreSelfTestArchive.DefaultDurationSeconds);

        Task.Run(() =>
        {
            var outcomes = new List<CoreSelfTestOutcome>(targets.Length);

            foreach (var target in targets)
            {
                if (token.IsCancellationRequested)
                    break;

                var outcome = CoreSelfTestRunner.RunOn(target.Id, target.LogicalProcessors, duration, token);
                outcomes.Add(outcome);

                var done = outcomes.Count;
                OnUi(() => SelfTestTipText =
                    $"自检进行中：{done}/{targets.Length} 核（每核约 {CoreSelfTestArchive.DefaultDurationSeconds:0} 秒）"
                    + $"{Environment.NewLine}最近：{outcome.CoreId} {CoreHealthFormatter.SelfTestVerdict(outcome)}");
            }

            OnUi(() => CompleteSelfTest(outcomes));
        });
    }

    /// <summary>自检收尾（UI 线程）：并档、落盘、刷新文案与日志。</summary>
    private void CompleteSelfTest(IReadOnlyList<CoreSelfTestOutcome> outcomes)
    {
        _selfTestRunning = false;
        _selfTestCancellation?.Dispose();
        _selfTestCancellation = null;
        _selfTestOutcomes = outcomes;
        SelfTestText = "自检";
        SelfTestTipText = BuildSelfTestIdleTip();

        CoreSelfTestStore.Merge(_selfTestArchive, outcomes);

        // 概览悬浮随之刷新：自检结论是"确定性证据"，与健康度那条推断并列展示但分开说明。
        HealthHintText = CoreHealthFormatter.BuildOverviewTooltip(_healthScores, SelfTestSummaryText);

        var passed = outcomes.Count(o => o.Passed);
        AppLog.Write($"[SELFTEST] 结束：{passed}/{outcomes.Count} 核通过"
                     + (outcomes.Count == 0 ? "（未执行任何核心）" : string.Empty));

        foreach (var o in outcomes)
        {
            AppLog.Write(
                $"[SELFTEST] {o.CoreId} {CoreHealthFormatter.SelfTestVerdict(o)}"
                + $" 轮次={o.Passes} 整数不一致={o.IntegerMismatches} 浮点失稳={o.FpBitInstabilities}"
                + $" 浮点最大相对误差={o.FpMaxRelativeError:E2}"
                + $" LP=[{string.Join(",", o.LogicalProcessors)}]"
                + (o.Error is null ? string.Empty : $" 错误={o.Error}"));
        }
    }

    /// <summary>把动作切回 UI 线程（后台自检线程不能直接碰绑定属性）。</summary>
    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.Invoke(action);
    }

    /// <summary>
    /// 实时视图：核心 Tile 按自然核序就地更新，并确保分区顺序已复位
    /// （从累积视图切回时列表仍是按名次排过的）。
    /// </summary>
    private void ApplyLiveCoreView(IReadOnlyList<CpuCoreSnapshot> snapshots)
    {
        for (var i = 0; i < snapshots.Count && i < _allCores.Count; i++)
            _allCores[i].ApplyLive(snapshots[i]);

        foreach (var group in CoreGroups)
        {
            if (!SameInstanceOrder(group.Cores, group.NaturalCores))
                group.Cores = group.NaturalCores;
        }

        RefreshGroupMode();
    }

    /// <summary>
    /// 累积视图：计算全局名次、刷新卡片，并把每段分区的卡片按名次降序排列。
    /// <para>
    /// 名次是**全局**的（跨分区比较所有物理核），符合"每个核的累积负载高低排名"的字面口径；
    /// 分区分组只影响排布的栏位，不影响名次编号。
    /// </para>
    /// </summary>
    private void ApplyCumulativeCoreView(IReadOnlyList<CpuCoreSnapshot> snapshots)
    {
        var order = BuildHystereticOrder();
        if (order.Count == 0)
            return;

        var rankById = new Dictionary<string, int>(order.Count, StringComparer.Ordinal);
        for (var i = 0; i < order.Count; i++)
            rankById[order[i].Id] = i + 1;

        for (var i = 0; i < snapshots.Count && i < _allCores.Count; i++)
        {
            var vm = _allCores[i];
            var rank = rankById.TryGetValue(vm.Id, out var value) ? value : i + 1;
            vm.ApplyCumulative(snapshots[i], _cumulative, rank, order.Count);
        }

        foreach (var group in CoreGroups)
        {
            var sorted = group.Cores
                .OrderBy(c => rankById.TryGetValue(c.Id, out var r) ? r : int.MaxValue)
                .ToArray();

            if (!SameInstanceOrder(group.Cores, sorted))
                group.Cores = sorted;
        }

        RefreshGroupMode();
        VerifyCumulativeRanking(order, rankById);
    }

    /// <summary>
    /// 累积排名不变量自检（回归守卫）。
    /// <para>
    /// 排名正确性依赖一个跨层约定：<c>DashboardCoreVm.Id</c> 必须与 <c>CpuCoreSnapshot.Id</c> 同串
    /// （累积积分器按该字符串索引，名次表也用同一把钥匙）。
    /// 一旦两个口径漂移，名次会静默退化成"自然顺序"——界面看起来完全正常，
    /// 数据却是错的，属于最难靠肉眼发现的失真。故在此固化为运行期断言。
    /// </para>
    /// 断言三件事：① 每颗核都拿到了名次；② 名次恰为 1..N 的排列（不重不漏）；
    /// ③ 每段分区内的显示顺序按名次非降。破坏时写日志（仅状态翻转时写一次，不随帧刷屏）。
    /// </summary>
    private void VerifyCumulativeRanking(
        IReadOnlyList<DashboardCoreVm> order, IReadOnlyDictionary<string, int> rankById)
    {
        string? problem = null;

        if (order.Count != _allCores.Count)
        {
            problem = $"order={order.Count} cores={_allCores.Count}";
        }
        else
        {
            var seen = new bool[order.Count + 1];
            foreach (var core in _allCores)
            {
                if (!rankById.TryGetValue(core.Id, out var rank) || rank < 1 || rank > order.Count)
                {
                    problem = $"id '{core.Id}' 无名次（Id 口径与快照不一致）";
                    break;
                }

                if (seen[rank])
                {
                    problem = $"名次 {rank} 重复";
                    break;
                }

                seen[rank] = true;
            }
        }

        if (problem is null)
        {
            foreach (var group in CoreGroups)
            {
                for (var i = 1; i < group.Cores.Count; i++)
                {
                    var previous = rankById[group.Cores[i - 1].Id];
                    var current = rankById[group.Cores[i].Id];
                    if (current < previous)
                    {
                        problem = $"{group.Title} 显示顺序未按名次降序（{previous} → {current}）";
                        break;
                    }
                }

                if (problem is not null)
                    break;
            }
        }

        if (problem is null)
        {
            _rankInvariantWarned = false;
            return;
        }

        if (_rankInvariantWarned)
            return;

        _rankInvariantWarned = true;
        AppLog.Write($"[INVARIANT] cumulative ranking: {problem}");
    }

    /// <summary>
    /// 按累积均值降序生成全局显示序，并在上一次顺序上做**滞回**：
    /// 相邻两核的累积均值差距不足 <see cref="RankHysteresis"/> 个百分点就不换位。
    /// <para>
    /// 换位会重建该分区的 ItemContainer（新 SegmentBar 从 0 动画到目标值），
    /// 若均值接近的核每帧互换，界面会持续闪动且白白消耗 CPU。
    /// 实现用相邻冒泡而非 OrderBy：冒泡天然以"上一帧顺序"为基序，滞回阈值直接作用于相邻比较。
    /// </para>
    /// </summary>
    private List<DashboardCoreVm> BuildHystereticOrder()
    {
        // 核数变化（首次进入 / 平台或线程数改变）→ 丢弃旧基序，退回自然序重新收敛。
        if (_displayOrder.Count != _allCores.Count)
        {
            _displayOrder.Clear();
            _displayOrder.AddRange(_allCores);
        }

        var list = _displayOrder;
        var swapped = true;

        while (swapped)
        {
            swapped = false;
            for (var i = 0; i + 1 < list.Count; i++)
            {
                var ahead = _cumulative.AveragePercent(list[i].Id);
                var behind = _cumulative.AveragePercent(list[i + 1].Id);

                if (behind - ahead <= RankHysteresis)
                    continue;

                (list[i], list[i + 1]) = (list[i + 1], list[i]);
                swapped = true;
            }
        }

        return list;
    }

    /// <summary>
    /// 把当前视图模式（含运行时长文案）下发给各分区标题。
    /// 副标题里的运行时长会随时间变化，故每帧下发；字符串未变时 SetProperty 不会触发通知。
    /// </summary>
    private void RefreshGroupMode()
    {
        foreach (var group in CoreGroups)
            group.ApplyLoadMode(IsCumulativeLoad, CumulativeWindowText);
    }

    /// <summary>
    /// 累积视图副标题所用的**统计窗口**文案。
    /// <para>
    /// 必须取 <see cref="CumulativeLoadTracker.ObservedSeconds"/>，不能用 <see cref="UptimeText"/>：
    /// 断点保护会跳过休眠间隔（既不进分子也不进分母），二者在发生挂起后不再相等。
    /// 用 UptimeText 会让界面声称统计了 1 小时、而实际归一化分母只有 55 分钟 ——
    /// 正是本项目反复踩到的「界面看着正常、数据是错的」那类静默偏差。
    /// </para>
    /// </summary>
    private string CumulativeWindowText
    {
        get
        {
            var window = TimeSpan.FromSeconds(_cumulative.ObservedSeconds);
            return $"{(int)window.TotalHours}:{window.Minutes:00}:{window.Seconds:00}";
        }
    }

    /// <summary>
    /// 刷新状态栏的累积能耗读数与悬浮说明。
    /// <para>
    /// 这里最容易出的错是把「没有数据」显示成「0」：0 Wh 与「读不到」在界面上完全不同义，
    /// 前者说这段时间的确没耗电，后者说不知道。非管理员会话下传感器返回 NaN 是常态
    /// （占多数机器上跑的情形），因此必须区分。参见 PeakTempText 同样的处理取舍。
    /// </para>
    /// <para>
    /// 有数据但并非全程有效时（例如中途关闭了传感器、或性能模式暂停轮询），
    /// 覆盖率会低于 100%，此时累计量只代表「观测到的那部分时间」。这句话必须出现在
    /// Tooltip 里，否则用户会把部分时段的结果当成全程结果来读。
    /// </para>
    /// </summary>
    private void UpdateEnergyReadout()
    {
        EnergyIsLive = _energy.HasLiveSample;

        if (_energy.ObservedSeconds <= 0)
        {
            EnergyText = "-";
            EnergyHintText = "运行期累计能耗\n功耗传感器不可用\n需以管理员身份运行方可读取封装功耗";
            return;
        }

        EnergyText = CumulativeEnergyTracker.FormatEnergy(_energy.Joules);

        var window = TimeSpan.FromSeconds(_energy.ObservedSeconds);
        var coverage = _cumulative.ObservedSeconds > 0
            ? Math.Min(100.0, _energy.ObservedSeconds / _cumulative.ObservedSeconds * 100.0)
            : 100.0;

        EnergyHintText =
            $"运行期累计能耗\n统计 {(int)window.TotalHours}:{window.Minutes:00}:{window.Seconds:00}"
            + $" · 覆盖 {coverage:0}%\n平均功率 {FmtValueUnit(_energy.AverageWatt, "0", "W")}";
    }

    /// <summary>
    /// 翻到上一个 / 下一个月（offsetMonths 为负向前翻）。
    /// <para>
    /// 向后只到当前月为止：未来没有数据，翻过去只能看到一片浅色的空格，
    /// 而浅色在无记录语义下极易被读成「这些天很省电」。历史向前不受限
    /// （账本保留 <see cref="EnergyHistoryStore.RetentionDays"/> 天）。
    /// </para>
    /// </summary>
    public void ShiftCalendarMonth(int offsetMonths)
    {
        var thisMonth = FirstOfMonth(DateOnly.FromDateTime(DateTime.Today));
        var target = _calendarMonth.AddMonths(offsetMonths);
        target = target > thisMonth ? thisMonth : target;

        if (target == _calendarMonth)
            return;

        _calendarMonth = target;
        AppLog.Write($"calendar month = {target:yyyy-MM}");
        RefreshCalendar();
    }

    private static DateOnly FirstOfMonth(DateOnly day) => new(day.Year, day.Month, 1);

    /// <summary>
    /// 日历的按需重算。今天那一格的数值会随时间增长，但没必要每帧重排：
    /// 能耗的量级变化以分钟计，10 秒一次肉眼察觉不到，却省掉了每帧 42 格的
    /// 属性比较与悬浮文案拼接（字符串构造在 UI 线程上是实打实的开销）。
    /// </summary>
    private void MaybeRefreshCalendar()
    {
        if ((DateTime.Now - _lastCalendarRefresh).TotalSeconds < CalendarRefreshSeconds)
            return;

        RefreshCalendar();
    }

    /// <summary>按当前显示月份重算全部格子、标题与汇总。</summary>
    private void RefreshCalendar()
    {
        if (CalendarDays.Count != CalendarCellCount)
        {
            CalendarDays.Clear();
            for (var i = 0; i < CalendarCellCount; i++)
                CalendarDays.Add(new PowerCalendarDayVm());
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        var first = _calendarMonth;
        var daysInMonth = DateTime.DaysInMonth(first.Year, first.Month);

        // 归一化基准取**当月最高的一天**：跨温度工况量级不同（轻薄本 15W、桌面 150W），
        // 用绝对阈值定档会让低压平台永远一片浅色，失去日历的比较意义。
        double peak = 0;
        double monthTotal = 0;
        var recordedDays = 0;

        for (var offset = 0; offset < daysInMonth; offset++)
        {
            var joules = _energyLedger.JoulesOf(first.AddDays(offset));
            if (joules <= 0)
                continue;

            recordedDays++;
            monthTotal += joules;
            if (joules > peak)
                peak = joules;
        }

        // 网格起点：周一为一周之始（大陆日历习惯），先回退到落在该周周一的那天。
        var leading = ((int)first.DayOfWeek - 1 + 7) % 7;
        var gridStart = first.AddDays(-leading);

        for (var index = 0; index < CalendarCellCount; index++)
        {
            var date = gridStart.AddDays(index);
            var inMonth = date.Year == first.Year && date.Month == first.Month;

            CalendarDays[index].Apply(
                date,
                inMonth ? _energyLedger.JoulesOf(date) : 0,
                inMonth ? _energyLedger.SecondsOf(date) : 0,
                inMonth && _energyLedger.HasRecord(date),
                peak,
                inMonth,
                today);
        }

        CalendarMonthText = $"{first.Year} 年 {first.Month} 月";
        // 空状态把「为什么空」直接写进这一行，而不是另起一行说明：
        // 多一行会让右列三段面板总高越过可视区，默认就冒出滚动条。
        // 且必须区分两种成因：传感器关着时「怎么等都不会有记录」，让用户直接看到原因。
        CalendarSummaryText = recordedDays == 0
            ? (_monitor.SensorsEnabled
                ? "无记录 · 尚无有效读数"
                : "无记录 · 硬件传感器未启用")
            : $"本月 {CumulativeEnergyTracker.FormatEnergy(monthTotal)} · 记录 {recordedDays} 天";
        CanGoNextMonth = first < FirstOfMonth(today);
        _lastCalendarRefresh = DateTime.Now;
    }

    /// <summary>两个列表是否逐项引用同一个对象（用于判断顺序是否真的需要重排）。</summary>
    private static bool SameInstanceOrder(
        IReadOnlyList<DashboardCoreVm> left, IReadOnlyList<DashboardCoreVm> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            if (!ReferenceEquals(left[i], right[i]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// 有界插入：把候选放进长度固定的榜单（<paramref name="descending"/> = true 为劳模榜，
    /// false 为摸鱼王）。榜内按"使用率 + 核心名"定序，超出末位者直接丢弃。
    /// <para>
    /// **为什么不用 LINQ**：<c>OrderByDescending(...).Take(5)</c> 每帧都要为全部核心建排序缓冲、
    /// 并分配比较器与委托；本实现只在固定长度数组上就地搬移，稳态零分配。
    /// 排序语义与 LINQ 版逐字对齐 —— 同率必须按核心名定序，否则两张榜会在同率核心之间
    /// 逐帧抖动（8 核满负载时所有核都是 100%，这个抖动不是理论问题）。
    /// </para>
    /// </summary>
    private static void InsertRanked(CpuCoreSnapshot?[] board, CpuCoreSnapshot candidate, bool descending)
    {
        // 榜单始终"前紧后空"：先数出已填长度，才知道该在哪一格插入、末位是否可比。
        var count = 0;
        while (count < board.Length && board[count] is not null)
            count++;

        if (count == board.Length && Rank(board[^1]!, candidate, descending) <= 0)
            return; // 榜已满且候选不优于末位

        var pos = count == board.Length ? board.Length - 1 : count;
        while (pos > 0 && Rank(board[pos - 1]!, candidate, descending) > 0)
        {
            board[pos] = board[pos - 1];
            pos--;
        }

        board[pos] = candidate;
    }

    /// <summary>榜单序：返回负值表示 <paramref name="a"/> 排在 <paramref name="b"/> 之前。</summary>
    private static int Rank(CpuCoreSnapshot a, CpuCoreSnapshot b, bool descending)
    {
        var byUsage = descending
            ? b.UsagePercent.CompareTo(a.UsagePercent)
            : a.UsagePercent.CompareTo(b.UsagePercent);

        return byUsage != 0 ? byUsage : string.CompareOrdinal(a.Id, b.Id);
    }

    /// <summary>追加一条包温采样，保留最近 60 条。</summary>
    private void AppendTemperatureHistory(double temperatureC)
    {
        _temperatureHistory.Add(temperatureC);
        if (_temperatureHistory.Count > HeatmapSeconds)
            _temperatureHistory.RemoveAt(0);
    }

    /// <summary>
    /// 发布 Sparkline 序列（v1.19.0 改为双缓冲，稳态零分配）。
    /// <para>
    /// 与热力图同一个约束：绑定是依赖属性（AffectsRender），**引用必须变**才会失效视觉，
    /// 所以这里在两个固定长度的数组之间交替发布，而不是复用同一个实例。
    /// 长度在历史未填满 60 条时会逐帧增长，那时重建一次双缓冲；填满后不再分配。
    /// </para>
    /// </summary>
    private void PublishPeakSeries()
    {
        var count = _temperatureHistory.Count;

        if (_peakSeriesBuffers.Length > 0 && _peakSeriesBuffers[0].Length != count)
            _peakSeriesBuffers = []; // 长度变化（历史还在长 / 窗口被改）→ 重建

        if (_peakSeriesBuffers.Length == 0)
            _peakSeriesBuffers = [new double[count], new double[count]];

        var buffer = _peakSeriesBuffers[_peakSeriesIndex];
        _peakSeriesIndex ^= 1;

        for (var i = 0; i < count; i++)
            buffer[i] = _temperatureHistory[i];

        PeakSeries = buffer;
    }

    /// <summary>
    /// 推进热力图并追加当前采样。
    /// <para>
    /// 两点关键：
    /// 1) 每次必须产出**新的数组实例**。Heatmap 绑定到 HeatMapControl.RowsData（依赖属性，AffectsRender）。
    ///    若原地改同一个数组再触发 PropertyChanged，WPF 按引用比较判定"值未变化"→ 不失效视觉，
    ///    于是 OnRender 只在首帧跑一次，整片热力图冻结不滚动。
    /// 2) 列推进按**墙钟秒**而非刷新次数计算，保证 60 列恒等于 60 秒：
    ///    刷新周期 500ms 时同一秒内只刷新最右一格（否则 60 列会缩成 30 秒）；
    ///    周期 2000ms 时一次补足跳过的秒（避免时间轴被拉长成 120 秒）。
    /// </para>
    /// </summary>
    private void PushHeatmap(IReadOnlyList<CpuCoreSnapshot> snapshots, bool prefill = false)
    {
        // 行数必须以**逻辑线程**为准（与 HeatmapLabels 同源），不能取 snapshots.Count（物理核数）：
        // 取核数时 8 核 16 线程只画 8 行，HeatMapControl 会把标签截到 8 条，C4~C7 在热力图里整体消失。
        var rowCount = UpdateHeatRows(snapshots);
        if (rowCount == 0)
            return;

        // 双缓冲（v1.19.0）：备用组的形状必须与当前组一致才谈得上"搬运历史"，
        // 因此 reuse 以**两组**都对齐为准。行数变化时两组一起重建，并让时间锚归零
        // → 下一帧走 advance = HeatmapSeconds 全量铺满，新矩阵不会掺进旧行的残留格子。
        var reuse = _heatmap.Length == rowCount && _heatmapSpare.Length == rowCount;
        var now = DateTime.UtcNow;

        // 需要新推入的列数（0 = 同一秒内，仅刷新最右一格）。
        int advance;
        if (prefill || !reuse || _heatmapLastUtc == DateTime.MinValue)
        {
            advance = HeatmapSeconds;
            _heatmapLastUtc = now;
        }
        else
        {
            var elapsed = (now - _heatmapLastUtc).TotalSeconds;
            if (elapsed >= HeatmapSeconds)
            {
                advance = HeatmapSeconds;
                _heatmapLastUtc = now;
            }
            else
            {
                advance = (int)elapsed;
                if (advance > 0)
                    _heatmapLastUtc = _heatmapLastUtc.AddSeconds(advance);
            }
        }

        var fill = advance > 0 ? advance : 1; // 未满一秒时至少覆盖最右一格

        double[][] next;
        if (reuse)
        {
            next = _heatmapSpare;
            for (var r = 0; r < rowCount; r++)
            {
                var row = next[r];
                // 重叠区间用 Array.Copy 是安全的（同数组内 memmove 语义）——这正是
                // "把历史整体左移 advance 列"的原地写法，不必每帧新建整块矩阵。
                if (advance < HeatmapSeconds)
                    Array.Copy(_heatmap[r], advance, row, 0, HeatmapSeconds - advance);

                var value = _heatRows[r].Usage;
                for (var c = HeatmapSeconds - fill; c < HeatmapSeconds; c++)
                    row[c] = value;
            }

            _heatmapSpare = _heatmap; // 旧的那组转为备用
        }
        else
        {
            next = AllocateHeatmapRows(rowCount);
            _heatmapSpare = AllocateHeatmapRows(rowCount);

            for (var r = 0; r < rowCount; r++)
            {
                var value = _heatRows[r].Usage;
                for (var c = HeatmapSeconds - fill; c < HeatmapSeconds; c++)
                    next[r][c] = value;
            }
        }

        // 必须是**新的数组实例**：RowsData 是依赖属性（AffectsRender），原地改写同一实例
        // 会被 WPF 按引用判定为"未变化"→ 视觉不失效 → 热力图冻结不滚动（v1.5.x 实测教训）。
        // 双缓冲两组交替发布，既满足这条约束又不产生每帧垃圾。
        //
        // **不要**在这里再写一次 `_heatmap = next;`（v1.19.0 实测踩过）：
        // 属性 setter 走的是 SetProperty(ref _heatmap, value)，它按**引用比较**决定要不要发
        // PropertyChanged。若先手动把字段赋成 next，setter 里就成了"新值 == 旧值"→ 永不通知
        // → HeatMapControl.RowsData 恒为 null → OnRender 开头的空值判断直接 return
        // → 热力图整片空白（连底板都不画）。
        // 阴险之处：VerifyHeatmapInvariants 校验的是 _heatmap/_heatRows/_heatmapLabels 三者行数，
        // 而字段是**真的**被更新了，所以不变量一路通过、日志一行不写 —— 只有截图能发现。
        Heatmap = next;

        VerifyHeatmapInvariants();
    }

    /// <summary>按行数申请一组行数组（每行 <see cref="HeatmapSeconds"/> 列）。仅形状变化时调用。</summary>
    private static double[][] AllocateHeatmapRows(int rowCount)
    {
        var rows = new double[rowCount][];
        for (var r = 0; r < rowCount; r++)
            rows[r] = new double[HeatmapSeconds];

        return rows;
    }

    /// <summary>
    /// 热力图不变量自检（回归守卫）：行数据、行标签、行源三者的行数必须同源一致。
    /// <para>
    /// 历史缺陷：PushHeatmap 取物理核数（8）铺行，而 HeatmapLabels 取逻辑线程数（16），
    /// 结果 HeatMapControl.EnsureLabelCache 的 Math.Min 把标签截到 8 条，C4~C7 在热力图里整体消失。
    /// 该缺陷属"两份口径打架"型，静态 review 难以长期挡住，故在此固化为运行期断言。
    /// 不变量被破坏时写日志（仅在状态翻转时写一次，不随帧刷屏），日志落在 exe 同级 logs/debug.log。
    /// </para>
    /// </summary>
    private void VerifyHeatmapInvariants()
    {
        var labelCount = string.IsNullOrEmpty(_heatmapLabels)
            ? 0
            : _heatmapLabels.Split(',', StringSplitOptions.RemoveEmptyEntries).Length;

        var consistent = _heatmap.Length == _heatRows.Length && labelCount == _heatRows.Length;

        if (consistent)
        {
            _heatmapInvariantWarned = false;
            return;
        }

        if (_heatmapInvariantWarned)
            return;

        _heatmapInvariantWarned = true;
        AppLog.Write(
            $"[INVARIANT] heatmap row mismatch: rows={_heatmap.Length} labels={labelCount} source={_heatRows.Length}");
    }

    /// <summary>刷新热力图行标签（逗号分隔）。行标签与 RowsData 行数同源，保证逐行对齐不被截断。</summary>
    private void RefreshHeatmapLabels()
        => HeatmapLabels = string.Join(",", _heatRows.Select(r => r.Id));

    /// <summary>
    /// 把最新快照写入热力图行源，返回行数。
    /// 行数只在平台数量变化时重建数组，稳态下纯就地赋值，无分配。
    /// </summary>
    private int UpdateHeatRows(IReadOnlyList<CpuCoreSnapshot> snapshots)
    {
        var needed = 0;
        foreach (var snapshot in snapshots)
            needed += snapshot.Threads.Count > 0 ? snapshot.Threads.Count : 1;

        if (needed == 0)
            return 0;

        if (_heatRows.Length != needed)
        {
            _heatRows = new (string, double)[needed];
            RefreshHeatmapLabels(); // 行数变化（平台 / 线程数变化）时标签同步重建，避免与数据行数错位。
        }

        var index = 0;
        foreach (var snapshot in snapshots)
        {
            if (snapshot.Threads.Count == 0)
            {
                _heatRows[index++] = (snapshot.Id, snapshot.UsagePercent);
                continue;
            }

            foreach (var thread in snapshot.Threads)
                _heatRows[index++] = (thread.Id, thread.UsagePercent);
        }

        return needed;
    }

    /// <summary>温度文本（不可用显示 "-"）。</summary>
    private static string FmtTempC(double value, string unit)
        => double.IsNaN(value) ? "-" : $"{value:0}{unit}";

    /// <summary>
    /// 带单位的数值文本（功耗 / 转速；不可用时**只留 "-"，不拼单位**）。
    /// <para>
    /// 为什么不写成 <c>值 + " W"</c>：读不到功耗时会渲染成 "- W" ——
    /// 一个挂在数字位上的单位，看上去像"数值是 0"的近亲。温度那一行一直是纯 "-"，
    /// 同一张卡上两种降级写法不该并存（v1.16.1）。
    /// </para>
    /// </summary>
    private static string FmtValueUnit(double value, string format, string unit)
        => double.IsNaN(value) ? "-" : $"{value.ToString(format)} {unit}";

    /// <summary>频率文本（不可用显示 "-"）。</summary>
    private static string FmtFreq(double ghz)
        => double.IsNaN(ghz) || ghz <= 0 ? "-" : $"{ghz:0.00} GHz";

    /// <summary>
    /// 状态栏整机硬件状态刷新（内存 / 显卡 / 磁盘，v1.11.0；v1.16.0 口径更新）。
    /// <para>
    /// 采集层已保证不抛异常，这里再包一层，避免任何意外打断刷新帧。
    /// 文本未变化时 SetProperty 不触发通知，因此每帧调用不会产生额外重绘；
    /// 磁盘等慢变量由采集层内部节流。占用 / 温度来自同帧传感器快照（NaN = 不可用）。
    /// </para>
    /// </summary>
    private void RefreshSystemHardware(CpuSnapshot snapshot)
    {
        if (_systemHardware is null)
            return;

        SystemHardwareInfo info;
        try
        {
            info = _systemHardware.Read();
        }
        catch (Exception ex)
        {
            AppLog.Write($"system hardware read FAILED: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        // 内存：已用 / 总量 + 占用率。口径为「可供 OS 使用的物理内存」，与任务管理器一致。
        // 内存条通常无独立温度传感器，不显示温度（主板 SuperIO 的"内存"温区并非内存条本体）。
        MemoryVisibility = info.HasMemory ? Visibility.Visible : Visibility.Collapsed;
        if (info.HasMemory)
        {
            MemoryText = $"{info.MemoryUsedGb:0.#}/{info.MemoryTotalGb:0.#}G {info.MemoryUsagePercent:0}%";
            MemoryHintText =
                $"物理内存\n已用 {info.MemoryUsedGb:0.0} GB / 共 {info.MemoryTotalGb:0.0} GB"
                + $"（{info.MemoryUsagePercent:0}%）";
        }

        // 显卡：独显优先（采集层保证）。显存小于 1 GB 不显示（核显共享显存多为固定小值，
        // 显示会误导）；占用 / 温度不可用（NaN）时不拼进文案，绝不拿 0 冒充读数。
        GpuVisibility = info.HasGpu ? Visibility.Visible : Visibility.Collapsed;
        if (info.HasGpu)
        {
            var name = info.GpuName.Length > 16 ? info.GpuName[..16].TrimEnd() : info.GpuName;
            var parts = new List<string> { name };
            if (info.GpuMemoryGb is >= 1.0)
                parts.Add($"{info.GpuMemoryGb.Value:0.#}G");
            if (!double.IsNaN(snapshot.GpuUtilizationPercent))
                parts.Add($"{snapshot.GpuUtilizationPercent:0}%");
            if (!double.IsNaN(snapshot.GpuTemperatureC))
                parts.Add($"{snapshot.GpuTemperatureC:0}℃");

            GpuText = string.Join(" ", parts);

            var vramLine = info.GpuMemoryGb is > 0
                ? $"显存 {info.GpuMemoryGb.Value:0.0} GB"
                : "显存 未报告";
            var usageLine = double.IsNaN(snapshot.GpuUtilizationPercent)
                ? string.Empty
                : $"\n占用 {snapshot.GpuUtilizationPercent:0}%";
            var tempLine = double.IsNaN(snapshot.GpuTemperatureC)
                ? string.Empty
                : $"\n温度 {snapshot.GpuTemperatureC:0} ℃";
            GpuHintText = $"显卡\n{info.GpuName}\n{vramLine}{usageLine}{tempLine}";
        }

        // 磁盘：物理总量 + 已用率 + 温度（v1.16.0：不再按盘符显示剩余空间）。
        // 总量是 Win32_DiskDrive 固定硬盘求和；已用率按固定逻辑卷聚合（资源管理器口径）。
        DriveVisibility = info.HasDrive ? Visibility.Visible : Visibility.Collapsed;
        if (info.HasDrive)
        {
            var totalText = info.DriveTotalGb >= 1000
                ? $"{info.DriveTotalGb / 1024.0:0.0#}T"
                : $"{info.DriveTotalGb:0}G";
            var tempSuffix = double.IsNaN(snapshot.DriveTemperatureC)
                ? string.Empty
                : $" {snapshot.DriveTemperatureC:0}℃";

            DriveText = $"{totalText} 已用{info.DriveVolumeUsedPercent:0}%{tempSuffix}";

            var diskLine = info.DriveCount > 0
                ? $"物理磁盘 {info.DriveCount} 块 · 共 {info.DriveTotalGb:0.0} GB"
                : "物理磁盘（枚举不可用，容量按逻辑卷）";
            DriveHintText =
                $"{diskLine}\n卷已用 {info.DriveVolumeUsedPercent:0}%（可用 {info.DriveVolumeFreeGb:0.0} GB）"
                + BuildDriveTemperatureHint(snapshot);
        }
    }

    /// <summary>
    /// 悬浮详情里的盘温段落（v1.16.2）。单块盘直接给"温度 46 ℃（型号）"；多块盘把每块盘
    /// 按温度降序逐行列出来。
    /// </summary>
    /// <remarks>
    /// 状态栏那一格只能挤下一个数字（多块盘取最热），但"是哪块盘"是排查坏读数必需的信息：
    /// v1.16.2 修的 114 ℃ 就是靠"逐块盘列出原始读数"定位到阈值传感器被当成实时温度的。
    /// </remarks>
    /// <summary>
    /// 风扇 Tooltip（v1.16.3）：列出 CPU / GPU 转速，并带上来源或读不到的原因。
    /// <para>
    /// 笔记本风扇既不在 LibreHardwareMonitor 的采集范围（ASUS 机型整树无 Fan 传感器），
    /// 又受 ASUS WMI 的权限限制，"没提权"与"机器真没有"在界面上的表现完全一样（都是一个 "-"）。
    /// 所以原因必须跟着数值一起呈现 —— 与 v1.16.1 温度/功耗、v1.16.2 盘温同一口径。
    /// </para>
    /// </summary>
    private static string BuildFanHint(CpuSnapshot snapshot)
    {
        var lines = new List<string>(3);

        if (!double.IsNaN(snapshot.FanRpm))
            lines.Add($"CPU 风扇 {snapshot.FanRpm:0} RPM");

        if (!double.IsNaN(snapshot.GpuFanRpm))
            lines.Add($"GPU 风扇 {snapshot.GpuFanRpm:0} RPM");

        if (lines.Count == 0)
            return string.IsNullOrWhiteSpace(snapshot.FanDetail) ? "本机未提供风扇转速" : snapshot.FanDetail;

        if (!string.IsNullOrWhiteSpace(snapshot.FanDetail))
            lines.Add(snapshot.FanDetail);

        return string.Join("\n", lines);
    }

    private static string BuildDriveTemperatureHint(CpuSnapshot snapshot)    {
        if (double.IsNaN(snapshot.DriveTemperatureC))
            return string.Empty;

        var drives = snapshot.DriveTemperatures;
        if (drives.Count == 0)
            return $"\n温度 {snapshot.DriveTemperatureC:0} ℃";

        if (drives.Count == 1)
            return $"\n温度 {drives[0].TemperatureC:0} ℃（{drives[0].Name}）";

        var lines = string.Concat(
            drives
                .OrderByDescending(d => d.TemperatureC)
                .Select(d => $"\n· {d.Name} {d.TemperatureC:0} ℃"));

        return $"\n温度取最热 {snapshot.DriveTemperatureC:0} ℃{lines}";
    }

    /// <summary>规格行一次性延迟重同步（v1.10.4）：MSR 基频修正发生在采样线程首帧（需等 LHM
    /// 内核驱动加载完成），晚于本 VM 构造时读取的 CpuInfo。启动约 10 秒后重查一次，
    /// 若基频被修正则更新「核 / 线程 | 基频」规格文本。</summary>
    private void ResyncSpecsOnce()
    {
        if (_specsResynced || ++_specResyncTicks < 10)
            return;

        _specsResynced = true;

        try
        {
            var specs = BuildSpecs(_monitor.GetCpuInfo());
            if (!string.Equals(specs, CpuSpecs, StringComparison.Ordinal))
                CpuSpecs = specs;
        }
        catch
        {
            // 监控服务已释放等场景：保持现有文本即可。
        }
    }

    /// <summary>由真实 CpuInfo 生成规格副标题。</summary>
    private static string BuildSpecs(CpuInfo info)
    {
        var parts = new List<string> { $"{info.PhysicalCores} 核 {info.LogicalProcessors} 线程" };
        if (info.BaseClockGHz > 0 && !double.IsNaN(info.BaseClockGHz))
            parts.Add($"基频 {info.BaseClockGHz:0.0} GHz");
        if (!string.IsNullOrWhiteSpace(info.Socket) && !info.Socket.StartsWith("U3E1", StringComparison.OrdinalIgnoreCase))
            parts.Add(info.Socket);
        return string.Join(" | ", parts);
    }

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
