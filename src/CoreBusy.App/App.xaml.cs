namespace CoreBusy.App;

using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CoreBusy.App.Infrastructure;
using CoreBusy.App.Services;
using CoreBusy.App.Themes;
using CoreBusy.App.ViewModels.Dashboard;
using CoreBusy.App.Views;
using CoreBusy.Core.Health;
using CoreBusy.Core.Interfaces;
using CoreBusy.Core.Models;
using CoreBusy.Sensor;
using CoreBusy.Windows.Hardware;
using CoreBusy.Windows.Monitoring;
using CoreBusy.Windows.Optimization;
using CoreBusy.Windows.Topology;
using CoreBusy.Windows.Wmi;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

        // 渲染模式（v1.19.0）：默认软件渲染（实测比硬件加速更省内存且无 CPU 代价，
        // 同时规避远程/沙箱会话的黑屏问题）。详见 ApplyRenderMode 的注释与实测表。
        ApplyRenderMode();

        AppLog.Write($"startup pid={Environment.ProcessId} version={typeof(App).Assembly.GetName().Version} elevated={new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)}");

        // 拓扑取证日志接线：CPUID 交叉校验（CoreBusy.Windows 层）经此写入 logs/debug.log。
        TopologyLog.Sink = AppLog.Write;

        // 优化模块日志接线（v1.12.0）：电源策略与规则套用的取证行同样进 logs/debug.log。
        OptimizationLog.Sink = AppLog.Write;

        // 传感器层日志接线（v1.16.1）：内核驱动状态、CPU 传感器清单、温度/功耗可用性变化
        // 都经此写入 logs/debug.log（含"没提权"这类降级原因，见 ProbeKernelAccess）。
        SensorLog.Sink = AppLog.Write;

        // WMI 通道日志接线（v1.16.3）：ASUS 风扇回退（AsusAtkWmi_WMNB.DSTS）的读数与失败原因
        // 走这里落盘 —— 笔记本风扇是唯一一条绕开 LHM 的采集路径，取证口径必须同样完整。
        WmiLog.Sink = AppLog.Write;

        // 健康度取证（v1.17.0）：评分依据（各分项、峰值、参照、基线、样本数）与 WHEA 计数
        // 都走这里落盘。这一块输出的是**推导值**，没有中间量就无法复核。
        HealthLog.Sink = AppLog.Write;

        // 载入用户设置（规范 §6/§7）：主题模式与核心分区口径需在窗口构建前决定。
        var settings = SettingsStore.Load();

        // 接入真实设备采集：拓扑(WMI/GetLogicalProcessorInformationEx) + 占用率(Performance Counter)
        // + 温度/功耗/频率/风扇(LibreHardwareMonitorLib) + 进程排行(CPU 时间增量)。
        // 初始化失败时回退 Mock，保证界面可用。
        ICpuMonitorService monitor = CreateMonitor(settings);

        // 传感器内核访问状态（v1.16.1）：CPU 温度/功耗、风扇转速、硬盘温度全部经
        // LibreHardwareMonitor 的内核驱动读取，而 PawnIO 设备只对提升后的进程开放。
        // 未提权时这些数值必然读不到，界面此前只显示 "-"/"0 W" 而不说原因，用户无从下手。
        // Mock 模式（调试 CPU 布局）的温度/功耗是编排出来的，不拿真实机器的权限状态去提示。
        var sensorAccess = monitor is MockCpuMonitorService
            ? null
            : LibreHardwareMonitorSensorService.ProbeKernelAccess();

        // 品牌主题：Auto 按 CPU 厂商（Intel/AMD/未知→Neutral），也可手动锁定；
        // 必须在主窗口构建前应用（StaticResource 解析时机）。
        BrandTheme.Apply(monitor.GetCpuInfo().Vendor, settings.Theme);
        AppLog.Write($"theme={BrandTheme.ThemeName} edition={BrandTheme.EditionText} coreLayout={settings.CoreLayout}");

        // 整机硬件状态（内存/显卡/系统盘）：供状态栏展示，采集失败不影响主链路。
        // CPU 核心优化（v1.12.0）：进程调度 + 电源策略 + 游戏检测，三项相互独立，
        // 任一不可用只让对应能力失效，不影响监控本身。
        var viewModel = new DashboardViewModel(
            monitor,
            TryCreateSystemHardware(),
            TryCreateOptimization(),
            TryCreatePowerPolicy(),
            TryCreateGameDetector(),
            sensorAccess,
            CreateWheaSource(monitor));
        var window = new MainWindow(viewModel);
        window.Closed += (_, _) => AppLog.Write("mainwindow closed");

        MainWindow = window;
        window.Show();
        AppLog.Write("dashboard initialize completed");
    }

    /// <summary>
    /// 渲染模式决策（v1.19.0）。
    /// <para>
    /// **结论来自实测，且与最初的假设相反。** v1.14.0 起无条件 <c>SoftwareOnly</c>
    /// 是为了规避"远程/沙箱会话下 D3D 渲染面在 Hide/Show 循环后易丢失（客户区变黑）"。
    /// v1.19.0 本想改成"只在远程会话强制软件渲染"，理由是本地桌面走硬件加速更省 CPU ——
    /// 但同机重复测量（每档 2 轮，同一份产物只改环境变量）把这条理由推翻了：
    /// </para>
    /// <list type="table">
    /// <item><term>软件渲染</term><description>工作集中位 157.4 MB，提交中位 86.0 MB，CPU 0.05 核</description></item>
    /// <item><term>硬件渲染</term><description>工作集中位 172.5 MB，提交中位 120.7 MB，CPU 0.05 核</description></item>
    /// </list>
    /// <para>
    /// 即硬件加速在本机（Console 会话 + AMD 核显）**多占 15 MB 工作集 / 35 MB 提交，
    /// 却换不到任何可测的 CPU 收益**（渲染路径不是本工具的瓶颈：画面 1 秒才动一次，
    /// 自绘控件全部走冻结画刷 + 零分配）。既然省内存和"远程会话不黑屏"指向同一个选择，
    /// 就**保持软件渲染为默认**，不再做"按会话类型自动切换"。
    /// </para>
    /// <para>
    /// 保留环境变量 <c>COREBUSY_SOFTWARE_RENDER</c> 作为逃生开关：<c>0</c> = 试硬件加速
    /// （换机器 / 换显卡后可复测这条路），<c>1</c> = 显式锁软件渲染，不设 = 默认软件渲染。
    /// 决策结果落盘到 <c>logs/debug.log</c>，便于把"界面表现"与"当时的渲染模式"对上。
    /// </para>
    /// </summary>
    private static void ApplyRenderMode()
    {
        var forced = Environment.GetEnvironmentVariable("COREBUSY_SOFTWARE_RENDER");
        var software = forced != "0";

        RenderOptions.ProcessRenderMode = software ? RenderMode.SoftwareOnly : RenderMode.Default;
        AppLog.Write(
            $"render mode: {(software ? "SoftwareOnly" : "Hardware")} remoteSession={GetSystemMetrics(SmRemoteSession) != 0} override={forced ?? "default"}");
    }

    private const int SmRemoteSession = 0x1000;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    /// <summary>
    /// WHEA 硬件错误事件源（v1.17.0）。Mock 模式（调试 CPU 布局）下**不接线**：
    /// 那套温度/频率本就是编排出来的，把真实机器的硬件错误混进合成健康度里，
    /// 只会得到一个既不是真机、也不是模拟的两不像。
    /// </summary>
    private static IWheaErrorSource? CreateWheaSource(ICpuMonitorService monitor)
        => monitor is MockCpuMonitorService ? null : new WheaEventLogReader();

    private static ICpuMonitorService CreateMonitor(AppSettings settings)
    {
        var elevated = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

        // Debug CPU 预置（规范 §55）：显式指定环境变量时直接走 Mock，
        // 便于在没有对应硬件的机器上验证 P/E、CPU Core、CCD 三种布局。发布版不做 UI 入口。
        var mockKey = Environment.GetEnvironmentVariable(MockCpuMonitorService.MockCpuEnvVar);
        if (!string.IsNullOrWhiteSpace(mockKey))
        {
            var mock = new MockCpuMonitorService(settings.CoreLayout, mockKey);
            AppLog.Write($"debug cpu profile active: {mock.ProfileKey} | {mock.GetCpuInfo()}");
            return mock;
        }

        try
        {
            var monitor = new WindowsCpuMonitorService(new LibreHardwareMonitorSensorService(), settings.CoreLayout);
            AppLog.Write($"real cpu monitor ready: {monitor.GetCpuInfo()} elevated={elevated}");
            return monitor;
        }
        catch (Exception ex)
        {
            AppLog.Write($"real cpu monitor init FAILED, fallback to mock. elevated={elevated} {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return new MockCpuMonitorService(settings.CoreLayout);
        }
    }

    /// <summary>
    /// 整机硬件状态采集（内存 / 显卡 / 系统盘）。不需要特殊权限，也不需要内核驱动，
    /// 因此 Mock 模式下同样接真实设备 —— 调试 CPU 布局时不该把整机信息也一起变成假的。
    /// </summary>
    private static ISystemHardwareService? TryCreateSystemHardware()
    {
        try
        {
            return new WindowsSystemHardwareService();
        }
        catch (Exception ex)
        {
            AppLog.Write($"system hardware service init FAILED: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// CPU 核心优化服务（进程亲和性 / 优先级 / I/O 优先级，v1.12.0）。
    /// 只依赖 Win32 调用，不需要内核驱动；未提升权限时仍可用，只是无法触碰系统进程。
    /// </summary>
    private static ICpuOptimizationService? TryCreateOptimization()
    {
        try
        {
            var service = new WindowsCpuOptimizationService();
            AppLog.Write($"[OPT] scheduling ready supported={service.IsSupported} {service.StatusDetail}");
            return service;
        }
        catch (Exception ex)
        {
            AppLog.Write($"[OPT] scheduling init FAILED: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 电源策略服务（powercfg，v1.12.0）。构造时即读一次当前状态并落取证行，
    /// 便于事后核对"用户看到的状态"与"机器真实状态"是否一致。
    /// </summary>
    private static IPowerPolicyService? TryCreatePowerPolicy()
    {
        try
        {
            var service = new WindowsPowerPolicyService();
            var state = service.Read();
            AppLog.Write(
                $"[OPT] power policy ready available={state.Available} backup={state.BackupAvailable} {state.Detail}");
            return service;
        }
        catch (Exception ex)
        {
            AppLog.Write($"[OPT] power policy init FAILED: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>前台全屏应用检测（游戏模式联动的输入源）。</summary>
    private static IGameDetector? TryCreateGameDetector()
    {
        try
        {
            return new FullscreenGameDetector();
        }
        catch (Exception ex)
        {
            AppLog.Write($"[OPT] game detector init FAILED: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // 用 ToString() 而不是 Message + StackTrace：XamlParseException 这类
        // 包装异常的内层（真正缺哪个资源、哪一行）只在 InnerException 链里，
        // 只记外层会把取证最关键的信息丢掉（v1.15.0 BoolToVis 前向引用实测教训）。
        AppLog.Write($"DISPATCHER UNHANDLED: {e.Exception}");
        e.Handled = true;
    }

    private void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        AppLog.Write($"APPDOMAIN UNHANDLED: {e.ExceptionObject}");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Write($"exit code={e.ApplicationExitCode}");
        base.OnExit(e);
    }
}
