namespace CoreBusy.App.Views;

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using CoreBusy.Core.Interfaces;
using CoreBusy.Core.Models;

/// <summary>
/// 设置对话框（v1.12.0 起改为「左侧分类导航 + 右侧内容区」三段式布局）。
/// <para>
/// 页面划分：常规（采样周期 / 传感器 / 托盘）、外观（主题 / 核心分区）、
/// 性能优化（电源策略 / 进程规则 / 游戏模式联动）。
/// </para>
/// <para>
/// 语义约定：导航、外观、规则列表都属于**配置**，点「确定」才落盘生效；
/// 而「还原原始值」是**动作**，点击即执行 —— 它要恢复的是系统电源方案，
/// 不是本程序的配置项，推迟到确定才执行反而不符合预期。
/// </para>
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly ICpuOptimizationService? _optimization;
    private readonly IPowerPolicyService? _powerPolicy;

    /// <summary>CPU 型号（注册建档用，主窗口传入；拿不到时空 = 注册行先不出 CPU，首条心跳补上）。</summary>
    private readonly string? _cpuName;

    /// <summary>
    /// 注册成功回调（v1.35.1）：注册即生效的关键 —— 设置窗口只负责落盘，运行中实例的
    /// 内存配置由此回调同步给 DashboardViewModel，免去「必须点确定/重启才开始上报」。
    /// 参数 (url, token, alias)；在 UI 线程触发（注册按钮回调链路）。
    /// </summary>
    public Action<string, string, string>? TelemetryActivated;

    /// <summary>按硬件能力过滤后的亲和性模式（与下拉框下标一一对应）。</summary>
    private readonly IReadOnlyList<CoreAffinityMode> _affinityModes;

    /// <summary>优先级档位全集（含"不干预"，与下拉框下标一一对应）。</summary>
    private static readonly IReadOnlyList<ProcessPriorityLevel> PriorityLevels =
    [
        ProcessPriorityLevel.None,
        ProcessPriorityLevel.Idle,
        ProcessPriorityLevel.BelowNormal,
        ProcessPriorityLevel.Normal,
        ProcessPriorityLevel.AboveNormal,
        ProcessPriorityLevel.High,
    ];

    private readonly ObservableCollection<ProcessRuleRow> _rules = [];

    /// <summary>「一键降级后台负载」的类别勾选行（v1.13.0）。纯界面状态，不落盘。</summary>
    private readonly ObservableCollection<BackgroundPresetRow> _presets = [];

    /// <summary>游戏预设「自定义核心」的掩码（选择器写回，「确定」时随 OptimizationSettings 落盘）。</summary>
    private ulong _gameCustomMask;

    /// <summary>确定后的新设置；取消时保持默认实例（调用方不会使用）。</summary>
    public AppSettings Result { get; private set; } = new();

    /// <summary>设计器/无服务场景的兜底构造函数。</summary>
    public SettingsWindow()
        : this(null, null)
    {
    }

    public SettingsWindow(ICpuOptimizationService? optimization, IPowerPolicyService? powerPolicy, string? cpuName = null)
    {
        _optimization = optimization;
        _powerPolicy = powerPolicy;
        _cpuName = cpuName;

        // 下拉选项必须在 InitializeComponent 之后才能塞给控件，但模式表本身要先算好。
        _affinityModes = optimization?.AvailableAffinityModes is { Count: > 0 } modes
            ? modes
            : [CoreAffinityMode.None, CoreAffinityMode.AllCores];

        InitializeComponent();

        LoadFromSettings(Services.SettingsStore.Load());
        PopulateOptimization();
    }

    private void LoadFromSettings(AppSettings settings)
    {
        IntervalFast.IsChecked = settings.IntervalMilliseconds == 500;
        IntervalSlow.IsChecked = settings.IntervalMilliseconds == 2000;
        IntervalNormal.IsChecked = settings.IntervalMilliseconds is not (500 or 2000);

        ThemeAuto.IsChecked = settings.Theme == ThemeMode.Auto;
        ThemeIntel.IsChecked = settings.Theme == ThemeMode.Intel;
        ThemeAmd.IsChecked = settings.Theme == ThemeMode.Amd;

        LayoutSpec.IsChecked = settings.CoreLayout == CoreLayoutMode.Spec;
        LayoutDraft.IsChecked = settings.CoreLayout == CoreLayoutMode.Draft;

        SensorsCheck.IsChecked = settings.SensorsEnabled;
        GameCheck.IsChecked = settings.GameDetectionEnabled;
        CloseToTrayCheck.IsChecked = settings.CloseToTray;

        // 开机启动以注册表实况回显（v1.26.5）：settings.json 不记这份状态，
        // 用户在任务管理器里手动禁用后这里必须如实显示未启用。
        AutoStartCheck.IsChecked = Services.AutoStartService.IsEnabled();

        // 软件更新（v1.31.0）：自动检查开关 + 当前版本回显。
        CheckUpdatesCheck.IsChecked = settings.CheckUpdatesOnStartup;
        UpdateStatusText.Text = $"当前版本 {Services.UpdateService.CurrentVersionText}。";

        // 遥测上报（v1.34.0）：默认关闭；回显服务器地址 / Token / 别名。
        TelemetryCheck.IsChecked = settings.TelemetryEnabled;
        TelemetryUrlBox.Text = settings.TelemetryServerUrl;
        TelemetryTokenBox.Text = settings.TelemetryToken;
        TelemetryAliasBox.Text = settings.TelemetryAlias;
        TelemetryInviteBox.Clear();
        TelemetryEnrollStatus.Text = string.IsNullOrWhiteSpace(settings.TelemetryToken)
            ? "尚未注册：填服务器地址与注册码后点「注册」。"
            : "已注册（设备 Token 就绪）。";
    }

    /// <summary>填充「性能优化」页：能力状态、电源预设、规则列表、游戏模式。</summary>
    private void PopulateOptimization()
    {
        var optimization = Result.Optimization;
        var settings = Services.SettingsStore.Load();
        optimization = settings.Optimization;

        OptimizeCheck.IsChecked = optimization.Enabled;
        OptimizeStatusText.Text = _optimization is null ? "优化服务不可用"
            : _optimization.StatusDetail + "\n" + _optimization.LastDetail;

        // 规则列表。
        foreach (var rule in optimization.Rules)
            _rules.Add(new ProcessRuleRow(_affinityModes, PriorityLevels, rule));

        RulesList.ItemsSource = _rules;
        UpdateRulesEmptyState();

        // 后台负载降级预设（v1.13.0）。默认一条都不勾：降级是有代价的（Docker 与编译器
        // 会变慢），替用户预勾等于把"提升前台"偷换成"削弱后台"，边界必须由用户自己划。
        _presets.Clear();
        foreach (var category in BackgroundLoadPresets.Categories)
            _presets.Add(new BackgroundPresetRow(category));

        PresetList.ItemsSource = _presets;

        ProcessPicker.ItemsSource = _optimization?.ListRunningProcessNames() ?? [];
        ProcessPicker.IsEnabled = _optimization is not null;

        // 亲和性下拉：规则行与游戏预设共用同一份模式表。
        GameAffinityCombo.ItemsSource = OptimizationLabels.AffinityOptions(_affinityModes);
        GamePriorityCombo.ItemsSource = OptimizationLabels.PriorityOptions(PriorityLevels);

        PowerNone.IsChecked = optimization.PowerPreset == PowerPreset.None;
        PowerPerformance.IsChecked = optimization.PowerPreset == PowerPreset.Performance;
        PowerSaver.IsChecked = optimization.PowerPreset == PowerPreset.Saver;

        GameOptimizeCheck.IsChecked = optimization.GameModeEnabled;
        _gameCustomMask = optimization.GameCustomAffinityMask;
        GameAffinityCombo.SelectedIndex = IndexOf(_affinityModes, optimization.GameAffinity);
        GamePriorityCombo.SelectedIndex = IndexOf(PriorityLevels, optimization.GamePriority);
        GamePowerCheck.IsChecked = optimization.GameAppliesPowerPreset;

        RefreshPowerStatus();
    }

    private void RefreshPowerStatus()
    {
        if (_powerPolicy is null)
        {
            PowerStatusText.Text = "电源策略服务不可用";
            PowerRestoreButton.IsEnabled = false;
            return;
        }

        var state = _powerPolicy.Read();
        PowerStatusText.Text = state.Detail;
        PowerRestoreButton.IsEnabled = state.BackupAvailable;
    }

    /// <summary>左侧导航切换：三个页面互斥显示。</summary>
    private void OnNavChanged(object sender, RoutedEventArgs e)
    {
        if (PaneGeneral is null)
            return; // 构造过程中由 XAML 触发的早期 Checked 事件，此时字段尚未就绪。

        PaneGeneral.Visibility = NavGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PaneAppearance.Visibility = NavAppearance.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PaneOptimize.Visibility = NavOptimize.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAddRule(object sender, RoutedEventArgs e)
    {
        if (ProcessPicker.SelectedItem is not string processName || string.IsNullOrWhiteSpace(processName))
            return;

        // 同一进程不重复添加：重复规则在扫描时后者覆盖前者，行为不直观。
        if (_rules.Any(r => string.Equals(r.ProcessName, processName, StringComparison.OrdinalIgnoreCase)))
            return;

        _rules.Add(new ProcessRuleRow(_affinityModes, PriorityLevels)
        {
            ProcessName = processName,
        });

        UpdateRulesEmptyState();
    }

    /// <summary>
    /// 一键把选中类别的后台重负载加进规则列表（v1.13.0）。
    /// 只增不改：已存在的进程名一律跳过 —— 用户可能已经手工调过那条规则，
    /// 覆盖掉就是一次静默的配置丢失。
    /// </summary>
    private void OnAddPresets(object sender, RoutedEventArgs e)
    {
        var keys = _presets.Where(p => p.Selected).Select(p => p.Category.Key).ToList();
        if (keys.Count == 0)
        {
            PresetStatusText.Text = "请先勾选要降级的类别。";
            return;
        }

        var added = 0;
        var skipped = 0;

        foreach (var rule in BackgroundLoadPresets.BuildRules(keys))
        {
            if (_rules.Any(r => string.Equals(r.ProcessName, rule.ProcessName, StringComparison.OrdinalIgnoreCase)))
            {
                skipped++;
                continue;
            }

            _rules.Add(new ProcessRuleRow(_affinityModes, PriorityLevels, rule));
            added++;
        }

        UpdateRulesEmptyState();

        // 报出「当前正在运行」的条数：规则按进程名常驻匹配，未运行的进程要等它下次启动
        // 才被接管，用户需要知道这一次点击里有多少是立刻见效的。
        var running = new HashSet<string>(
            _optimization?.ListRunningProcessNames() ?? [], StringComparer.OrdinalIgnoreCase);
        var live = _rules.Count(r => r.Enabled && running.Contains(r.ProcessName));

        PresetStatusText.Text = added == 0
            ? $"选中类别的 {skipped} 条规则都已存在，未新增。"
            : $"已新增 {added} 条规则" +
              (skipped > 0 ? $"（{skipped} 条已存在，已跳过）" : string.Empty) +
              $"；其中 {live} 条对应的进程正在运行，会立即生效。";
    }

    private void OnRemoveRule(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ProcessRuleRow row })
        {
            _rules.Remove(row);
            UpdateRulesEmptyState();
        }
    }

    private void OnRestorePower(object sender, RoutedEventArgs e)
    {
        if (_powerPolicy is null)
            return;

        // 动作类按钮：立即执行并回显结果，不等到「确定」。
        var ok = _powerPolicy.Restore(out var error);
        RefreshPowerStatus();

        MessageBox.Show(
            this,
            ok ? "已还原到本工具改动之前的电源方案。" : $"还原失败：{error}",
            "CORE-BUSY",
            MessageBoxButton.OK,
            ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void UpdateRulesEmptyState() =>
        RulesEmptyText.Visibility = _rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var previous = Services.SettingsStore.Load();

        // 「自定义核心」的落盘前复核：服务端套用时还会再校验一次，但在这里拦下
        // 能让用户当场修正，而不是关掉窗口后规则静默不生效（至少保留 1 个核心）。
        if (_rules.Any(r => r.GameOnly && r.KernelEnforced))
        {
            MessageBox.Show(this, "「仅游戏时生效」不能与「内核强制亲和」同时启用，请取消其中一项。", "CORE-BUSY",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var brokenRule = _rules.FirstOrDefault(r =>
            r.Affinity == CoreAffinityMode.Custom && (_optimization?.ValidateCustomMask(r.CustomAffinityMask) is not null || r.CustomAffinityMask == 0));
        if (brokenRule is not null)
        {
            MessageBox.Show(
                this,
                $"规则 {brokenRule.ProcessName} 选了「自定义核心」但没有勾选任何核心（至少保留 1 个）。",
                "CORE-BUSY",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var gameAffinity = _affinityModes[Math.Clamp(GameAffinityCombo.SelectedIndex, 0, _affinityModes.Count - 1)];
        if (gameAffinity == CoreAffinityMode.Custom && (_optimization?.ValidateCustomMask(_gameCustomMask) is not null || _gameCustomMask == 0))
        {
            MessageBox.Show(
                this,
                "游戏预设选了「自定义核心」但没有勾选任何核心（至少保留 1 个）。",
                "CORE-BUSY",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var optimization = new OptimizationSettings
        {
            Enabled = OptimizeCheck.IsChecked == true,
            Rules = _rules.Select(r => r.ToRule()).ToList(),
            GameModeEnabled = GameOptimizeCheck.IsChecked == true,
            GameAffinity = gameAffinity,
            GameCustomAffinityMask = gameAffinity == CoreAffinityMode.Custom ? _gameCustomMask : 0,
            GamePriority = PriorityLevels[Math.Clamp(GamePriorityCombo.SelectedIndex, 0, PriorityLevels.Count - 1)],
            GameAppliesPowerPreset = GamePowerCheck.IsChecked == true,
            PowerPreset = PowerPerformance.IsChecked == true ? PowerPreset.Performance
                : PowerSaver.IsChecked == true ? PowerPreset.Saver
                : PowerPreset.None,
        };

        // 电源策略是系统级改动，单独走一次显式应用；失败时保留对话框让用户看到原因，
        // 而不是关掉窗口后留下一台状态不明的机器。
        if (optimization.PowerPreset != PowerPreset.None && _powerPolicy is not null)
        {
            if (!_powerPolicy.Apply(optimization.PowerPreset, out var error))
            {
                RefreshPowerStatus();
                MessageBox.Show(this, $"电源策略未能完全应用：{error}", "CORE-BUSY",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        Result = new AppSettings
        {
            IntervalMilliseconds = IntervalFast.IsChecked == true ? 500
                : IntervalSlow.IsChecked == true ? 2000
                : 1000,
            Theme = ThemeIntel.IsChecked == true ? ThemeMode.Intel
                : ThemeAmd.IsChecked == true ? ThemeMode.Amd
                : ThemeMode.Auto,
            CoreLayout = LayoutDraft.IsChecked == true ? CoreLayoutMode.Draft : CoreLayoutMode.Spec,
            SensorsEnabled = SensorsCheck.IsChecked == true,
            GameDetectionEnabled = GameCheck.IsChecked == true,
            CloseToTray = CloseToTrayCheck.IsChecked == true,
            CheckUpdatesOnStartup = CheckUpdatesCheck.IsChecked == true,
            TelemetryEnabled = TelemetryCheck.IsChecked == true,
            TelemetryServerUrl = TelemetryUrlBox.Text.Trim(),
            TelemetryToken = TelemetryTokenBox.Text.Trim(),
            TelemetryAlias = TelemetryAliasBox.Text.Trim(),
            Optimization = optimization,
        };

        // 开机启动（v1.26.5）：在保存设置的同一动作里落注册表，勾选状态即生效。
        var autoStart = AutoStartCheck.IsChecked == true;
        if (autoStart != Services.AutoStartService.IsEnabled())
            Services.AutoStartService.Set(autoStart);

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    // ── 遥测注册（v1.35.0）────────────────────────────────────────────
    // 注册是动作不是配置：点了立即向 hub 兑换设备 Token 并持久化，不等「确定」。
    // 与软件更新检查同一模式 —— 注册码一次性，失败重填重试即可，无中间状态。

    private async void OnTelemetryEnroll(object sender, RoutedEventArgs e)
    {
        var url = TelemetryUrlBox.Text.Trim();
        var invite = TelemetryInviteBox.Text.Trim().ToUpperInvariant();
        var alias = TelemetryAliasBox.Text.Trim();

        if (url.Length == 0 || invite.Length == 0)
        {
            TelemetryEnrollStatus.Text = "请先填服务器地址与注册码。";
            return;
        }
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            TelemetryEnrollStatus.Text = "服务器地址需以 http:// 或 https:// 开头。";
            return;
        }

        // 心跳地址（…/api/heartbeat）派生注册端点；也接受直接填根地址。
        var root = url.EndsWith("/api/heartbeat", StringComparison.OrdinalIgnoreCase)
            ? url[..^"/api/heartbeat".Length]
            : url.TrimEnd('/');

        TelemetryEnrollButton.IsEnabled = false;
        TelemetryEnrollStatus.Text = "注册中…";
        var token = await Services.TelemetryReporter.EnrollAsync(
            root + "/api/enroll",
            invite,
            new Services.TelemetryEnrollPayload
            {
                DeviceId = Services.MachineFingerprint.Compute(),
                Alias = string.IsNullOrEmpty(alias) ? null : alias,
                CpuModel = string.IsNullOrEmpty(_cpuName) ? null : _cpuName,
            }).ConfigureAwait(true);

        TelemetryEnrollButton.IsEnabled = true;
        if (token is null)
        {
            var reason = Services.TelemetryReporter.LastError;
            TelemetryEnrollStatus.Text = reason is "HTTP 410" ? "注册码已使用或已过期，请取新码。"
                : reason is "HTTP 401" ? "注册码无效。"
                : $"注册失败：{reason}";
            return;
        }

        // 成功：填入 Token、勾选启用并立即落盘（注册动作自持，不依赖用户点确定）；
        // 再同步运行中实例的内存配置 —— 否则本进程要等「确定」/重启才开始上报（v1.35.1）。
        TelemetryTokenBox.Text = token;
        TelemetryCheck.IsChecked = true;
        TelemetryEnrollStatus.Text = "注册成功，30 秒内首报，之后每 10 分钟一次。";
        var settings = Services.SettingsStore.Load();
        settings.TelemetryEnabled = true;
        settings.TelemetryServerUrl = url;
        settings.TelemetryToken = token;
        settings.TelemetryAlias = alias;
        Services.SettingsStore.Save(settings);
        TelemetryActivated?.Invoke(url, token, alias);
    }


    // ── 软件更新（v1.31.0）────────────────────────────────────────────
    // 检查是动作不是配置：点了立即执行并回显，不等到「确定」。发现新版本时
    // 先弹询问框，用户同意才进入下载（带进度对话框 + sha256 校验）。

    /// <summary>最近一次检查发现的新版本；null = 尚未发现（「查看发布页」按钮隐藏）。</summary>
    private Services.UpdateInfo? _latestUpdate;

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        OpenReleaseButton.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text = "正在检查 GitHub Releases…";

        var result = await Services.UpdateService.CheckLatestAsync().ConfigureAwait(true);

        CheckUpdateButton.IsEnabled = true;
        switch (result.Outcome)
        {
            case Services.UpdateCheckOutcome.UpToDate:
                UpdateStatusText.Text = $"已是最新版本（{Services.UpdateService.CurrentVersionText}）。";
                break;

            case Services.UpdateCheckOutcome.UpdateAvailable when result.Info is { } info:
                _latestUpdate = info;
                UpdateStatusText.Text = info.AssetSize > 0
                    ? $"发现新版本 {info.Tag}（当前 {Services.UpdateService.CurrentVersionText}），安装包 {info.AssetSize / 1048576.0:0.0} MB。"
                    : $"发现新版本 {info.Tag}（当前 {Services.UpdateService.CurrentVersionText}）。";
                OpenReleaseButton.Visibility = Visibility.Visible;

                if (MessageBox.Show(
                        this,
                        $"发现 CORE-BUSY 新版本 {info.Tag}（当前 {Services.UpdateService.CurrentVersionText}）。\n\n是否下载并安装？",
                        "CORE-BUSY 更新",
                        MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
                {
                    await StartUpdateDownloadAsync(info).ConfigureAwait(true);
                }

                break;

            default:
                UpdateStatusText.Text = "检查失败：" + result.Detail;
                break;
        }
    }

    private void OnOpenReleasePage(object sender, RoutedEventArgs e)
    {
        if (_latestUpdate is null)
            return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = $"{Services.UpdateService.ReleasesPageUrl}/tag/{_latestUpdate.Tag}",
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
        }
    }

    /// <summary>下载并询问安装；返回 true 表示已启动换版脚本，此时应关窗退出主程序。</summary>
    private async Task<bool> StartUpdateDownloadAsync(Services.UpdateInfo info)
    {
        var applied = await Services.UpdateService.DownloadApplyWithUiAsync(info, this).ConfigureAwait(true);
        if (!applied)
            return false;

        // 先关掉本模态对话框，再走显式退出（跳过"关闭到托盘"拦截）。
        DialogResult = true;
        if (Application.Current.MainWindow is MainWindow main)
        {
            main.ExitForUpdate();
        }
        else
        {
            Application.Current.Shutdown();
        }

        return true;
    }

    /// <summary>打开核心选择器，返回选中的掩码；取消或拓扑不可用时返回 null。</summary>
    private ulong? OpenCorePicker(ulong initialMask)
    {
        var topology = _optimization?.TopologyView;
        if (topology is not { Count: > 0 })
        {
            MessageBox.Show(this, "核心拓扑不可用，无法自定义核心。", "CORE-BUSY",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        var picker = new CorePickerWindow(topology, initialMask) { Owner = this };
        return picker.ShowDialog() == true ? picker.SelectedMask : null;
    }

    private void OnPickCores(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ProcessRuleRow row }
            && OpenCorePicker(row.CustomAffinityMask) is { } mask)
        {
            row.CustomAffinityMask = mask;
        }
    }

    private void OnPickGameCores(object sender, RoutedEventArgs e)
    {
        if (OpenCorePicker(_gameCustomMask) is { } mask)
        {
            _gameCustomMask = mask;
            UpdateGameCustomRow();
        }
    }

    private void OnGameAffinityChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateGameCustomRow();

    /// <summary>游戏预设的「自定义核心」行：只在亲和性选中 Custom 时出现。</summary>
    private void UpdateGameCustomRow()
    {
        if (GameAffinityCombo is null || GameCustomPanel is null)
            return; // 构造过程中的早期事件，元素尚未就绪。

        var isCustom =
            _affinityModes[Math.Clamp(GameAffinityCombo.SelectedIndex, 0, _affinityModes.Count - 1)]
            == CoreAffinityMode.Custom;
        GameCustomPanel.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
        GameCustomSummary.Text = OptimizationLabels.DescribeCustomMask(_gameCustomMask);
    }

    private static int IndexOf<T>(IReadOnlyList<T> list, T value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (EqualityComparer<T>.Default.Equals(list[i], value))
                return i;
        }

        return 0;
    }
}
