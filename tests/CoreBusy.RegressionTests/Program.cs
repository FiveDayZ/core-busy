using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CoreBusy.App.Services;
using CoreBusy.App.ViewModels.Dashboard;
using CoreBusy.App.Views;
using CoreBusy.Core.Models;
using CoreBusy.Core.Progression;
using CoreBusy.Windows.Topology;

internal static partial class Program
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _passed;

    [STAThread]
    private static int Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable("COREBUSY_UPDATE_TEST_MARKER") is { Length: > 0 } marker)
        {
            File.WriteAllText(marker, "started");
            return 0;
        }
        if (args is ["--optimization-worker"]) { Thread.Sleep(120000); return 0; }
        if (args is ["--hold", var milliseconds])
        {
            Thread.Sleep(int.Parse(milliseconds));
            return 0;
        }
        if (args is ["--save-exp", var directory, var amount])
        {
            Environment.SetEnvironmentVariable(EnergyHistoryStore.DataDirEnvVar, directory);
            CoreExpStore.Save(new Dictionary<string, CoreExpEntry> { ["C0"] = new() { LoadCoreMinutes = double.Parse(amount) } });
            return CoreExpStoreTrace.LastSaveError is null ? 0 : 1;
        }
        if (args is ["--restart-app"] || args is ["--restart-from", _])
            return RunRestartApp(args);

        var root = Path.Combine(Path.GetTempPath(), "corebusy-regression-" + Guid.NewGuid().ToString("N"));
        try
        {
            RecoveryRules(root);
            OptimizationRules(root);
            FrequencyRules();
            CumulativeLoadRules();
            ProgressionRules();
            InDirectory(root, "fresh-exp", FreshExp);
            InDirectory(root, "restart-exp", RestartExp);
            InDirectory(root, "mixed-exp", MixedExp);
            InDirectory(root, "awarded-exp", AwardedExpPersistence);
            InDirectory(root, "repair-exp", RepairExp);
            InDirectory(root, "fresh-role", FreshRoles);
            InDirectory(root, "restart-role", RestartRoles);
            InDirectory(root, "peak", PeakDuration);
            InDirectory(root, "settings", SettingsAndIsolation);
            InDirectory(root, "atomic", AtomicFiles);
            InDirectory(root, "concurrent", ConcurrentExp);
            InDirectory(root, "handoff", RestartHandoff);
            Topology();
            EvolutionRules();
            PowerRules();
            if (args is ["--ui", var evidence])
            {
                InDirectory(root, "native-power", NativePowerRecovery);
                InDirectory(root, "blocked-sampling", BlockedSamplingRecovery);
                InDirectory(root, "windows-sampling", WindowsSampling);
                InDirectory(root, "restart-ui", ActualAppRestart);
                InDirectory(root, "ui", () => UiSmoke(evidence));
            }
            if (args is ["--layout", var layoutEvidence])
                InDirectory(root, "ui-layout", () => UiSmoke(layoutEvidence, true));
            Console.WriteLine($"PASSED: {_passed} assertions");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            Console.Error.WriteLine("Isolated evidence: " + root);
            return 1;
        }
    }

    private static void InDirectory(string root, string name, Action test)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable(EnergyHistoryStore.DataDirEnvVar, directory);
        SettingsStore.Save(new AppSettings { CheckUpdatesOnStartup = false, CloseToTray = false });
        test();
    }

    private static void Check(bool ok, string name)
    {
        if (!ok) throw new InvalidOperationException("FAIL: " + name);
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static void Equal(double expected, double actual, string name) =>
        Check(Math.Abs(expected - actual) < 1e-7, $"{name}: expected={expected}, actual={actual}");

    private static T Field<T>(object value, string name) =>
        (T)value.GetType().GetField(name, PrivateInstance)!.GetValue(value)!;

    private static void Call(object value, string name, params object[] args) =>
        value.GetType().GetMethod(name, PrivateInstance)!.Invoke(value, args);

    private static (DashboardViewModel Vm, CpuCoreSnapshot[] Samples) Session()
    {
        var monitor = new MockCpuMonitorService(CoreLayoutMode.Spec, "amd-single");
        var samples = monitor.GetCoreSnapshots().Take(2)
            .Select(s => s with { UsagePercent = 100, EffectiveFrequencyGHz = 4 }).ToArray();
        return (new DashboardViewModel(monitor), samples);
    }

    private static void Accumulate(DashboardViewModel vm, CpuCoreSnapshot[] samples, double seconds)
    {
        Field<CoreExpAccumulator>(vm, "_exp").Accumulate(samples, seconds);
        Call(vm, "RefreshCoreLevels", (object)samples);
    }

    private static double DisplayedExp(DashboardViewModel vm, string id) =>
        Field<Dictionary<string, DashboardCoreVm>>(vm, "_allCoresById")[id].Level.ExpCoreMinutes;

    private static void SaveExp(DashboardViewModel vm) => Call(vm, "SaveExpIfNeeded", 60.0, true);

    private static void FreshExp()
    {
        var (vm, samples) = Session();
        var id = samples[0].Id;
        Accumulate(vm, samples, 60);
        Equal(1, DisplayedExp(vm, id), "first minute EXP");
        SaveExp(vm);
        Call(vm, "RefreshCoreLevels", (object)samples);
        Equal(1, DisplayedExp(vm, id), "saving must not duplicate displayed EXP");
        SaveExp(vm);
        Equal(1, CoreExpStore.Load().Cores[id].ExpCoreMinutes, "repeated save is idempotent");
        vm.Stop();
        vm.Stop();
        Equal(1, CoreExpStore.Load().Cores[id].ExpCoreMinutes, "repeated shutdown is idempotent");
    }

    private static void RestartExp()
    {
        CoreExpStore.Save(new Dictionary<string, CoreExpEntry> { ["C0"] = new() { LoadCoreMinutes = 1000 } });
        for (var i = 1; i <= 2; i++)
        {
            var (vm, samples) = Session();
            Accumulate(vm, samples, 60);
            Equal(1000 + i, DisplayedExp(vm, "C0"), "historical plus session EXP");
            SaveExp(vm);
            vm.Stop();
            Equal(1000 + i, CoreExpStore.Load().Cores["C0"].ExpCoreMinutes, "EXP survives restart");
        }
    }

    private static void MixedExp()
    {
        CoreExpStore.Save(new Dictionary<string, CoreExpEntry>
        {
            ["C0"] = new() { LoadCoreMinutes = 100, FloorCoreMinutes = 200 },
        });
        var (vm, samples) = Session();
        Accumulate(vm, samples, 12000);
        Equal(400, DisplayedExp(vm, "C0"), "legacy awarded balance plus busy interval");
        SaveExp(vm);
        Equal(400, CoreExpStore.Load().Cores["C0"].ExpCoreMinutes, "mixed EXP roundtrip");
        vm.Stop();
    }

    private static void RepairExp()
    {
        CoreExpStore.Save(new Dictionary<string, CoreExpEntry>
        {
            ["C0"] = new() { LoadCoreMinutes = 65, TotalLevels = 8, MaxGhz = 4.5, PeakSeconds = 20 },
        });
        var entry = CoreExpStore.Load().Cores["C0"];
        Equal(CoreLevelCurve.CumulativeExp(8), entry.ExpCoreMinutes, "legacy level high-water repair is preserved");
        CoreExpStore.Save(new Dictionary<string, CoreExpEntry> { ["C0"] = entry });
        Equal(entry.ExpCoreMinutes, CoreExpStore.Load().Cores["C0"].ExpCoreMinutes, "legacy repair is idempotent");
        CoreExpStore.Save(new Dictionary<string, CoreExpEntry> { ["C0"] = new() { LoadCoreMinutes = 1 } });
        var merged = CoreExpStore.Load().Cores["C0"];
        Equal(entry.ExpCoreMinutes, merged.ExpCoreMinutes, "older snapshot cannot reduce disk EXP");
        Equal(4.5, merged.MaxGhz, "NaN in old snapshot cannot erase frequency peak");
        Equal(20, merged.PeakSeconds, "NaN in old snapshot cannot erase peak duration");
    }

    private static void ObserveRoles(DashboardViewModel vm, CpuCoreSnapshot[] samples)
    {
        Field<CoreRoleAccumulator>(vm, "_role").Accumulate(samples, 60, new DateTime(2026, 10, 15, 12, 0, 0));
        Call(vm, "RefreshCoreRoles", 60.0, false);
    }

    private static void FreshRoles()
    {
        var (vm, samples) = Session();
        ObserveRoles(vm, samples);
        var ledger = Field<Dictionary<string, CoreRoleEntry>>(vm, "_roleLedger");
        Check(ledger.Count == 2, "fresh role ledger creates observed cores");
        Equal(1, ledger["C0"].ObservedCoreMinutes, "fresh role observation");
        Call(vm, "RefreshCoreRoles", 61.0, false);
        Equal(1, ledger["C0"].ObservedCoreMinutes, "role refresh is idempotent");
        ObserveRoles(vm, samples); // 未到 15 分钟定时保存点，退出必须保存。
        vm.Stop();
        Equal(2, CoreRoleStore.Load().Cores["C0"].ObservedCoreMinutes, "shutdown flushes role increment");
    }

    private static void RestartRoles()
    {
        CoreRoleStore.Save(new Dictionary<string, CoreRoleEntry>
        {
            ["C0"] = new() { ObservedCoreMinutes = 100, LoadCoreMinutes = 100 },
        });
        for (var i = 1; i <= 2; i++)
        {
            var (vm, samples) = Session();
            ObserveRoles(vm, samples);
            vm.Stop();
            Equal(100 + i, CoreRoleStore.Load().Cores["C0"].ObservedCoreMinutes, "role stats survive restart");
        }
    }

    private static void PeakDuration()
    {
        var (vm, samples) = Session();
        Call(vm, "RefreshCoreLevels", (object)samples);
        var ledger = Field<Dictionary<string, CoreExpEntry>>(vm, "_expLedger");
        foreach (var s in samples) ledger[s.Id].PeakSeconds = 0;
        Call(vm, "AccumulateSample", samples, new CpuSnapshot { PackagePowerW = 60 }, SystemHardwareInfo.Empty, 4.0);
        Call(vm, "RefreshCoreLevels", (object)samples);
        var first = ledger[samples[0].Id].PeakSeconds;
        var second = ledger[samples[1].Id].PeakSeconds;
        Equal(4, first, "peak duration uses the same accepted frame interval as energy and EXP");
        Equal(first, second, "all cores receive identical frame duration");
        vm.Stop();
    }

    private static void SettingsAndIsolation()
    {
        var directory = EnergyHistoryStore.DataDirectory;
        var (vm, _) = Session();
        vm.ApplySettings(new AppSettings { CheckUpdatesOnStartup = true, CloseToTray = false });
        Check(SettingsStore.Load().CheckUpdatesOnStartup, "enable startup update persists");
        vm.ApplySettings(new AppSettings { CheckUpdatesOnStartup = false, CloseToTray = false });
        Check(!SettingsStore.Load().CheckUpdatesOnStartup, "disable startup update persists");
        vm.Stop();
        RuntimeStatsStore.Save(42, 7);
        Equal(42, RuntimeStatsStore.Load().TotalSeconds, "isolated runtime roundtrip");
        Check(File.Exists(Path.Combine(directory, "stats", "runtime.json")), "runtime uses overridden directory");
        foreach (var type in new[] { typeof(SettingsStore), typeof(RuntimeStatsStore), typeof(StatsStore), typeof(GameReportStore) })
        {
            var path = (string)type.GetProperty("DirectoryPath", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            Check(Path.GetFullPath(path).StartsWith(Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase), type.Name + " uses isolated root");
        }
    }

    private static void AtomicFiles()
    {
        var path = Path.Combine(EnergyHistoryStore.DataDirectory, "atomic.json");
        JsonFile.WriteAllText(path, "{\"value\":1}");
        JsonFile.WriteAllText(path, "{\"value\":2}");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var failed = false;
            try { JsonFile.WriteAllText(path, "{\"value\":3}"); }
            catch (IOException) { failed = true; }
            Check(failed, "replacement failure is reported");
        }
        Check(JsonFile.ReadAllText(path).Contains(":2"), "failed replacement preserves previous data");
        Check(Directory.GetFiles(EnergyHistoryStore.DataDirectory, "*.tmp").Length == 0, "failed write removes temporary file");
        File.WriteAllText(path, "{truncated");
        Check(JsonFile.ReadAllText(path).Contains(":1"), "corrupt primary falls back to last backup");
        AppLog.Write("regression", "diagnostic marker");
        Check(File.ReadAllText(Path.Combine(EnergyHistoryStore.DataDirectory, "logs", "errors.log")).Contains("diagnostic marker"), "diagnostics use isolated directory");
    }

    private static Process StartSelf(params string[] args)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return Process.Start(start)!;
    }

    private static void ConcurrentExp()
    {
        var directory = EnergyHistoryStore.DataDirectory;
        var amounts = new[] { 100, 900, 200, 700, 300, 500 };
        var children = amounts.Select(n => StartSelf("--save-exp", directory, n.ToString())).ToArray();
        try
        {
            foreach (var child in children)
                Check(child.WaitForExit(15000) && child.ExitCode == 0, "concurrent writer completed");
            Equal(900, CoreExpStore.Load().Cores["C0"].ExpCoreMinutes, "concurrent snapshots cannot overwrite higher EXP");
        }
        finally { foreach (var child in children) child.Dispose(); }
    }

    private static void RestartHandoff()
    {
        using var child = StartSelf("--hold", "300");
        var args = new[] { "--restart-from", child.Id.ToString() };
        Check(!AppRestart.WaitForPreviousProcess(args, 0), "new process waits while previous process is alive");
        Check(AppRestart.WaitForPreviousProcess(args, 15000), "handoff completes after previous process exits");
        Check(AppRestart.WaitForPreviousProcess(args, 0), "already-exited previous process is accepted");
        Check(!AppRestart.WaitForPreviousProcess(["--restart-from"]), "malformed restart arguments rejected");
        Check(!AppRestart.WaitForPreviousProcess(["--restart-from", Environment.ProcessId.ToString()]), "cannot wait on self");
        Check(AppRestart.StartInfo(false).ArgumentList.SequenceEqual(new[] { "--restart-from", Environment.ProcessId.ToString() }), "restart carries old PID");
        var path = Path.Combine(EnergyHistoryStore.DataDirectory, "sub", "..");
        Check(JsonFile.MutexName("Instance", path) == JsonFile.MutexName("Instance", EnergyHistoryStore.DataDirectory), "equivalent data paths share instance lock");
        Check(JsonFile.MutexName("Instance", path + "other") != JsonFile.MutexName("Instance", path), "different data paths use independent locks");
    }

    private static int RunRestartApp(string[] args)
    {
        Environment.SetEnvironmentVariable(MockCpuMonitorService.MockCpuEnvVar, "amd-single");
        var app = new CoreBusy.App.App();
        app.InitializeComponent();
        app.Startup += (_, _) => app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            if (app.MainWindow is not MainWindow window)
                throw new InvalidOperationException("App startup did not create a main window");
            if (args[0] == "--restart-app")
            {
                File.WriteAllText(Path.Combine(EnergyHistoryStore.DataDirectory, "old-pid.txt"), Environment.ProcessId.ToString());
                typeof(MainWindow).GetMethod("RestartApplication", PrivateInstance, null, Type.EmptyTypes, null)!
                    .Invoke(window, null);
            }
            else
            {
                File.WriteAllText(Path.Combine(EnergyHistoryStore.DataDirectory, "new-pid.txt"), Environment.ProcessId.ToString());
                window.ExitForUpdate(); // 仅正常关闭窗口，不下载或安装更新。
            }
        }));
        return app.Run();
    }

    private static void ActualAppRestart()
    {
        using var old = StartSelf("--restart-app");
        Check(old.WaitForExit(30000) && old.ExitCode == 0, "actual App closes old process during restart");
        var ready = Path.Combine(EnergyHistoryStore.DataDirectory, "new-pid.txt");
        var wait = Stopwatch.StartNew();
        while (!File.Exists(ready) && wait.Elapsed.TotalSeconds < 30) Thread.Sleep(50);
        Check(File.Exists(ready), "replacement App starts after old instance releases its mutex");
        var oldPid = File.ReadAllText(Path.Combine(EnergyHistoryStore.DataDirectory, "old-pid.txt"));
        var newPid = File.ReadAllText(ready);
        Check(newPid != oldPid, "restart creates a new App process");
        try
        {
            using var replacement = Process.GetProcessById(int.Parse(newPid));
            Check(replacement.WaitForExit(15000), "replacement App completes normal shutdown");
        }
        catch (ArgumentException) { /* 替代实例已完成关闭。 */ }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);

    private static void Topology()
    {
        uint size = 0;
        GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref size);
        var buffer = Marshal.AllocHGlobal((int)size);
        int expected = 0;
        try
        {
            Check(GetLogicalProcessorInformationEx(0, buffer, ref size), "native core query succeeds");
            for (var offset = 0; offset < size; offset += Marshal.ReadInt32(buffer, offset + 4)) expected++;
        }
        finally { Marshal.FreeHGlobal(buffer); }
        var raw = (ICollection)typeof(LogicalProcessorTopologyService)
            .GetMethod("QueryProcessorCores", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
        Check(raw.Count == expected, $"query returns cores, not packages: OS={expected}, project={raw.Count}");
        using var process = Process.GetCurrentProcess();
        var original = process.ProcessorAffinity;
        try
        {
            var mask = original.ToInt64();
            process.ProcessorAffinity = new IntPtr(mask & -mask);
            var topology = new LogicalProcessorTopologyService().GetTopology();
            var actual = topology.LogicalProcessors.Select(p => p.PhysicalCoreIndex).Distinct().Count();
            Check(actual == expected, $"topology survives restricted affinity: OS={expected}, project={actual}");
        }
        finally { process.ProcessorAffinity = original; }
    }

    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static void Capture(FrameworkElement window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        // VisualBrush 以元素自身的可见边界取景，避免 Popup 外边距让截图右下角被裁掉。
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
            dc.DrawRectangle(new VisualBrush(window), null, new Rect(window.RenderSize));
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void UiSmoke(string evidence, bool layoutOnly = false)
    {
        Directory.CreateDirectory(evidence);
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var bindingLog = new StringWriter();
        var listener = new TextWriterTraceListener(bindingLog);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        foreach (var file in new[] { "Colors", "Brushes", "Icons", "Styles" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/CoreBusy.App;component/Themes/{file}.xaml"),
            });
        CoreBusy.App.Themes.BrandTheme.Apply("AMD");
        SettingsStore.Save(new AppSettings { CheckUpdatesOnStartup = true, CloseToTray = false });
        var (vm, _) = Session();
        var window = new MainWindow(vm);
        window.Show();
        var beforeAutostart = AutoStartService.IsEnabled();
        Exception? callbackError = null;
        var settings = new SettingsWindow(null, null, "Regression CPU") { Owner = window };
        settings.Loaded += (_, _) => settings.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            try
            {
                Check(window.IsVisible && window.ActualWidth > 0, "real WPF main window renders");
                var checkbox = (CheckBox)settings.FindName("CheckUpdatesCheck");
                Check(checkbox.IsChecked == true, "settings UI loads saved update preference");
                checkbox.IsChecked = false;
                Capture(window, Path.Combine(evidence, "main-window.png"));
                Capture(settings, Path.Combine(evidence, "settings-updates-disabled.png"));
                Visuals<Button>(settings).Single(b => Equals(b.Content, "确定")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception ex) { callbackError = ex; settings.Close(); }
        }));
        var accepted = settings.ShowDialog();
        if (callbackError is not null) throw callbackError;
        Check(accepted == true, "settings confirm event completes");
        vm.ApplySettings(settings.Result);
        Check(!SettingsStore.Load().CheckUpdatesOnStartup, "UI update preference survives persistence");
        Check(AutoStartService.IsEnabled() == beforeAutostart, "saving unrelated settings preserves autostart");
        var reopened = new SettingsWindow(null, null, "Regression CPU") { Owner = window };
        reopened.Show();
        Check(((CheckBox)reopened.FindName("CheckUpdatesCheck")).IsChecked == false, "reopened UI reflects disabled update preference");
        reopened.Close();
        CompactDialogsUi(window, evidence);
        OptimizationUi(window, evidence);
        RecoveryUi(window, evidence);
        FrequencyUi(window, evidence);
        LoadAndPerformanceUi(window, evidence);
        if (layoutOnly) DenseLayoutUi(window, evidence);
        else EvolutionUi(window, vm, evidence);
        window.Close();
        app.Shutdown();
        listener.Flush();
        PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        Check(string.IsNullOrWhiteSpace(bindingLog.ToString()), "WPF binding errors: " + bindingLog);
    }
}
