namespace CoreBusy.App.Views;

using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Interop;
using CoreBusy.App.Infrastructure;
using CoreBusy.App.ViewModels.Dashboard;

/// <summary>
/// CORE-BUSY 主窗口：自定义无边框标题栏（WindowChrome）+ 高保真监控仪表盘。
/// 刷新节奏由 DashboardViewModel 内部 DispatcherTimer 驱动（800ms）。
/// 关闭行为由设置 CloseToTray 决定：启用时隐藏到托盘（双击恢复 / 右键退出），否则直接退出。
/// </summary>
public partial class MainWindow : Window
{
    private const int WmGetMinMaxInfo = 0x0024;
    private readonly DashboardViewModel _viewModel;
    private TrayIcon? _trayIcon;
    private bool _realExit;
    private bool _trayHintShown;

    public MainWindow(DashboardViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        // 弹卡动画（v1.27.0）：登场补间 + EXP 条生长都在 Opened/Closed 生命周期内，
        // 关闭即停 —— 不给软件渲染引擎留任何常驻动画负载。
        FocusCard.Opened += OnFocusCardOpened;
        FocusCard.Closed += OnFocusCardClosed;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _viewModel.Start();
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    /// <summary>标题栏"关闭"：交给 OnClosing 按设置决定隐藏到托盘还是真正退出。</summary>
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 关闭拦截：CloseToTray 启用且非显式退出时，取消关闭、隐藏窗口并惰性创建托盘图标；
    /// 首次隐藏弹一次气泡提示。显式退出（托盘"退出" / 重启流程 / 设置关闭托盘）走真正的关闭收尾。
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_realExit && Services.SettingsStore.Load().CloseToTray)
        {
            e.Cancel = true;
            Hide();
            EnsureTrayIcon();

            if (!_trayHintShown && _trayIcon is { IsAttached: true })
            {
                _trayHintShown = true;
                _trayIcon.ShowBalloon(
                    "CORE-BUSY", "已最小化到托盘：双击图标恢复显示，右键图标可退出。", 3000);
            }

            return;
        }

        DisposeTrayIcon();
        _viewModel.Stop();
        base.OnClosing(e);
    }

    /// <summary>
    /// 惰性创建托盘图标（首次隐藏到托盘时）：双击恢复窗口，右键菜单提供打开 / 退出。
    /// <para>
    /// v1.19.0 起改用 WPF 原生实现（<see cref="TrayIcon"/>），不再依赖
    /// <c>System.Windows.Forms.NotifyIcon</c> —— 体积/内存的账见该类注释。
    /// 菜单项沿用旧实现的文案与顺序（打开 → 分隔线 → 退出），避免用户可见的行为漂移。
    /// </para>
    /// </summary>
    private void EnsureTrayIcon()
    {
        if (_trayIcon is not null)
            return;

        try
        {
            var menu = new ContextMenu();

            var open = new MenuItem { Header = "打开 CORE-BUSY" };
            open.Click += (_, _) => RestoreFromTray();
            menu.Items.Add(open);
            menu.Items.Add(new Separator());

            var exit = new MenuItem { Header = "退出" };
            exit.Click += (_, _) => ExitFromTray();
            menu.Items.Add(exit);

            var tray = new TrayIcon("CORE-BUSY 核忙") { Menu = menu };
            tray.Activate += (_, _) => RestoreFromTray();
            _trayIcon = tray;
        }
        catch (Exception)
        {
        }
    }

    /// <summary>从托盘恢复主窗口显示。</summary>
    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>托盘"退出"：标记显式退出后走正常关闭流程。</summary>
    private void ExitFromTray()
    {
        _realExit = true;
        Close();
    }

    /// <summary>释放托盘图标资源（注销通知区域图标 → 销毁消息窗口 → 销毁 HICON）。</summary>
    private void DisposeTrayIcon()
    {
        if (_trayIcon is null)
            return;

        _trayIcon.Dispose();
        _trayIcon = null;
    }

    /// <summary>
    /// 标题栏"设置"：打开设置对话框并将结果应用到运行时配置。
    /// 主题 / 核心分区在窗口构建前生效，若这两项发生变化则询问是否立即重启。
    /// </summary>
    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_viewModel.OptimizationService, _viewModel.PowerPolicyService)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() == true)
        {
            var before = Services.SettingsStore.Load();
            _viewModel.ApplySettings(dialog.Result);

            if (dialog.Result.RequiresRestartComparedTo(before)
                && MessageBox.Show(
                    this,
                    "主题 / 核心分区已保存。\n\n这两项需要在窗口构建前生效，是否立即重启 CORE-BUSY？",
                    "CORE-BUSY",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                RestartApplication();
            }
        }
    }

    /// <summary>重启本程序（用于让主题 / 核心分区变更生效）。</summary>
    private void RestartApplication()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception)
        {
        }

        _realExit = true; // 重启流程不允许被"关闭到托盘"拦截
        _viewModel.Stop();
        Application.Current.Shutdown();
    }

    /// <summary>
    /// 以管理员身份重启本程序（v1.16.1，概览卡右上角的提示标触发）。
    /// <para>
    /// CPU 温度/功耗、风扇转速、硬盘温度都要经 LibreHardwareMonitor 的内核驱动（PawnIO）读取，
    /// 而该设备只对提升后的进程开放 —— 用户报告"读不到温度/功耗"九成是这个原因。
    /// 提权走 <c>runas</c>，是否放行由 UAC 决定：**用户点了"否"（Win32 1223）不是错误**，
    /// 此时留在原进程继续用（显卡温度等不依赖内核驱动的指标照常工作），不弹错误框、不退出。
    /// </para>
    /// </summary>
    public void RestartElevated()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
                return;

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas",
            });
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return;
        }
        catch (Exception)
        {
            return;
        }

        _realExit = true; // 重启流程不允许被"关闭到托盘"拦截
        _viewModel.Stop();
        Application.Current.Shutdown();
    }

    /// <summary>标题栏"性能模式"：切换低开销运行模式（按钮高亮 + 状态栏提示）。</summary>
    private void OnTogglePerformanceMode(object sender, RoutedEventArgs e)
        => _viewModel.TogglePerformanceMode();

    /// <summary>排行榜面板右上角 Tab：在劳模榜 / 摸鱼榜之间切换（分类集合恒为 P0-P4）。</summary>
    private void OnRankTabClick(object sender, RoutedEventArgs e)
        => _viewModel.ToggleRankTab();

    /// <summary>
    /// 核心分区的负载口径切换（实时 / 累积）。
    /// 两段按钮用 Tag 声明目标口径，处理逻辑只有一份；模式状态在 VM 里是全局唯一的。
    /// </summary>
    private void OnCoreLoadModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string mode })
            return;

        _viewModel.SetCumulativeLoad(string.Equals(mode, "cumulative", StringComparison.Ordinal));
    }

    /// <summary>
    /// 点击某张核卡 → 弹出该核的详情卡片（v1.23.0 反堆砌版）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 详情改用 <see cref="System.Windows.Controls.Primitives.Popup"/> 承载：
    /// 主界面保持 8 卡均等的简约布局，深度信息（健康度三分量/等级/角色/EXP）
    /// 全部进浮层 —— 浮层 <c>StaysOpen=False</c>，点击卡片外任何位置自动关闭，
    /// 「点空白收起」由控件机制天然保证，不再需要背景点击处理器与冒泡防护。
    /// </para>
    /// <para>
    /// 浮层独立于主窗口视觉树（有自己的 HWND），其重绘不影响 8 卡区域 ——
    /// 软件渲染下这也是一处卡顿收益。
    /// </para>
    /// </remarks>
    private void OnCoreTileClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: DashboardCoreVm core })
        {
            if (!ReferenceEquals(_focusedCore, core))
            {
                if (_focusedCore is not null)
                    _focusedCore.Level.PropertyChanged -= OnFocusedLevelPropertyChanged;

                _focusedCore = core;
                _focusedCore.Level.PropertyChanged += OnFocusedLevelPropertyChanged;
            }

            FocusCard.DataContext = core;
            FocusCard.PlacementTarget = (UIElement)sender;
            FocusCard.IsOpen = true;
        }
    }

    /// <summary>当前弹出卡片对应的核（其 Level 的 Progress 变化驱动 EXP 条补间）。</summary>
    private DashboardCoreVm? _focusedCore;

    /// <summary>弹卡打开（v1.27.0）：内容缩放 + 淡入登场；布局完成后把 EXP 条从 0 生长到当前值。</summary>
    private void OnFocusCardOpened(object? sender, EventArgs e)
    {
        var scale = new System.Windows.Media.ScaleTransform(0.94, 0.94);
        FocusCardRoot.RenderTransform = scale;

        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        scale.BeginAnimation(
            System.Windows.Media.ScaleTransform.ScaleXProperty,
            new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        scale.BeginAnimation(
            System.Windows.Media.ScaleTransform.ScaleYProperty,
            new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        FocusCardRoot.BeginAnimation(
            OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));

        // 等弹卡内容完成一次布局（ActualWidth 可用），再播 EXP 条生长动画。
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            () =>
            {
                if (FocusCard.IsOpen && _focusedCore is not null)
                    AnimateExpFill(_focusedCore.Level.Progress);
            });
    }

    /// <summary>弹卡关闭：解除 Progress 订阅，避免后台持续补间不可见元素。</summary>
    private void OnFocusCardClosed(object? sender, EventArgs e)
    {
        if (_focusedCore is not null)
        {
            _focusedCore.Level.PropertyChanged -= OnFocusedLevelPropertyChanged;
            _focusedCore = null;
        }

        ExpFill.BeginAnimation(WidthProperty, null);
    }

    /// <summary>聚焦核的等级进度变化 → EXP 条平滑补间到新宽度（软渲染下只动一个 Border 的 Width，代价极低）。</summary>
    private void OnFocusedLevelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CoreLevelVm.Progress) && FocusCard.IsOpen && _focusedCore is not null)
            AnimateExpFill(_focusedCore.Level.Progress);
    }

    /// <summary>EXP 条宽度补间（ease-out 700ms）。EXP 每个采样周期都在涨，
    /// 相邻两次补间首尾衔接，视觉上就是一条持续生长的经验条。</summary>
    private void AnimateExpFill(double progress)
    {
        var trackWidth = ExpTrack.ActualWidth;
        if (trackWidth <= 0)
            return;

        var target = Math.Clamp(progress, 0, 1) * trackWidth;
        ExpFill.BeginAnimation(
            WidthProperty,
            new System.Windows.Media.Animation.DoubleAnimation(target, TimeSpan.FromMilliseconds(700))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
            });
    }


    /// <summary>清零累积负载统计，从当前时刻重新开始积分。</summary>
    private void OnResetCumulativeClick(object sender, RoutedEventArgs e)
        => _viewModel.ResetCumulativeLoad();

    /// <summary>
    /// 标题栏「更多」：在资源管理器里打开数据文件夹。
    /// <para>
    /// v1.20.0 前这里打开的是 exe 同级的 logs —— 运行日志功能移除后，那已是一个
    /// 永远为空的目录（本方法还会顺手把它建出来）。改为指向真正的数据目录：
    /// settings.json / energy-history.json / core-health.json 都在那里，
    /// 而设置里的悬浮提示正让用户去那儿校准整机功耗系数，指过去才用得上。
    /// </para>
    /// <para>
    /// 路径复用 <see cref="CoreBusy.App.Services.EnergyHistoryStore.DataDirectory"/> 的解析，
    /// **不**自己拼 %APPDATA%：那个解析认 COREBUSY_DATA_DIR 覆盖，
    /// 自己拼会得到与写盘位置不一致的目录。
    /// </para>
    /// </summary>
    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = CoreBusy.App.Services.EnergyHistoryStore.DataDirectory;
            System.IO.Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
        }
    }
}
