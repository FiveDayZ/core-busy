using System.Runtime.InteropServices;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using CoreBusy.App.Infrastructure;
using CoreBusy.App.Services;
using CoreBusy.App.ViewModels.Dashboard;
using CoreBusy.App.Views;
using CoreBusy.Core.Interfaces;
using CoreBusy.Core.Models;
using CoreBusy.Core.Progression;
using CoreBusy.Windows.Monitoring;
using CoreBusy.Windows.Optimization;

internal static partial class Program
{
    private static void RecoveryRules(string root)
    {
        InDirectory(root, "unavailable", () =>
        {
            var monitor = CoreBusy.App.App.InitializeMonitor(() => throw new InvalidOperationException("测试采集失败"));
            var vm = new DashboardViewModel(monitor); vm.Tick();
            Check(vm.CoreGroups.Count == 0 && vm.TotalUsageText == "-" && vm.MonitorErrorText.Contains("测试采集失败"), "failed monitor displays unavailable without invented CPU or load");
            vm.Stop();
            Check(CoreExpStore.Load().Cores.Count == 0 && EnergyHistoryStore.Load().Days.Count == 0, "failed monitor never generates EXP or energy history");
        });
        InDirectory(root, "stale-frame", () =>
        {
            var monitor = new LoadMonitor(); var vm = new DashboardViewModel(monitor); vm.Tick();
            var exp = Field<CoreExpAccumulator>(vm, "_exp").ExpCoreMinutes("C0");
            var energy = Field<CumulativeEnergyTracker>(vm, "_energy").Joules;
            monitor.SampleTimestamp = Stopwatch.GetTimestamp() - 10 * Stopwatch.Frequency;
            Thread.Sleep(30); vm.Tick();
            Equal(exp, Field<CoreExpAccumulator>(vm, "_exp").ExpCoreMinutes("C0"), "expired monitor frame cannot award EXP");
            Equal(energy, Field<CumulativeEnergyTracker>(vm, "_energy").Joules, "expired monitor frame cannot add energy");
            Check(!vm.EnergyIsLive && vm.MonitorErrorText.Length > 0, "expired readings are visibly unavailable");
            monitor.SampleTimestamp = null; vm.Tick();
            Check(vm.MonitorErrorText.Length == 0 && vm.EnergyIsLive, "new sampling recovers from stale state"); vm.Stop();
        });
        InDirectory(root, "shared-gap", () =>
        {
            var (vm, samples) = Session();
            var power = new CpuSnapshot { PackagePowerW = 60 };
            Call(vm, "AccumulateSample", samples, power, SystemHardwareInfo.Empty, 1.0);
            var energy = Field<CumulativeEnergyTracker>(vm, "_energy").Joules;
            Call(vm, "AccumulateSample", samples, power, SystemHardwareInfo.Empty, 7201.0);
            var dt = Field<double>(vm, "_validDeltaSeconds");
            var telemetry = Field<TelemetryLedger>(vm, "_telemetry"); telemetry.ObserveFrame(60, dt);
            Call(vm, "RefreshCoreLevels", (object)samples);
            Equal(0, dt, "two-hour sleep has zero accepted interval across all accumulators");
            Equal(energy, Field<CumulativeEnergyTracker>(vm, "_energy").Joules, "sleep adds no main energy");
            Check(double.IsNaN(telemetry.CurrentEnergyTotalWh), "sleep adds no phantom telemetry watt-hours");
            Check(Field<Dictionary<string, CoreExpEntry>>(vm, "_expLedger").Values.All(e => !(e.PeakSeconds > 0)), "sleep adds no peak duration");
            vm.Stop();
        });
        foreach (var mode in new[] { "exit", "detection-off", "optimization-off", "game-off", "game-ended", "fixed-preset", "cleanup-error" })
            InDirectory(root, "game-" + mode, () => GameCleanup(mode));
        InDirectory(root, "power-plans", PowerPlanRecovery);
        InDirectory(root, "updater", UpdateRecovery);
    }

    private static AppSettings GameSettings() => new()
    {
        CheckUpdatesOnStartup = false, CloseToTray = false, GameDetectionEnabled = true,
        Optimization = new() { Enabled = true, GameModeEnabled = true, GameAppliesPowerPreset = true },
    };

    private static void GameCleanup(string mode)
    {
        var settings = GameSettings(); SettingsStore.Save(settings);
        var power = new RecoveryPower(); var optimization = new RecoveryOptimization(); var game = new RecoveryGame();
        var vm = new DashboardViewModel(new MockCpuMonitorService(CoreLayoutMode.Spec, "amd-single"),
            optimization: optimization, powerPolicy: power, gameDetector: game);
        vm.Tick(); Equal(1, power.Applies, "game enters a temporary power preset");
        if (mode == "cleanup-error") optimization.ThrowOnExit = true;
        if (mode is "exit" or "cleanup-error") vm.Stop();
        else if (mode == "game-ended") { game.Process = null; vm.Tick(); }
        else
        {
            if (mode == "detection-off") settings.GameDetectionEnabled = false;
            if (mode == "optimization-off") settings.Optimization.Enabled = false;
            if (mode == "game-off") settings.Optimization.GameModeEnabled = false;
            if (mode == "fixed-preset") { settings.Optimization.PowerPreset = PowerPreset.Saver; settings.GameDetectionEnabled = false; }
            vm.ApplySettings(settings);
        }
        Equal(1, power.Restores, mode + " requests temporary restoration even when fixed preset is configured");
        Check(optimization.Exits == 1 && Field<string?>(vm, "_activeGameProcess") is null, mode + " clears game ownership");
        vm.Stop();
        Equal(1, power.Restores, "repeated stop does not repeat power restore");
    }

    private static void PowerPlanRecovery()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var current = a;
        var values = new Dictionary<(Guid, string, bool), int>();
        var writes = new List<Guid>(); var activations = new List<Guid>();
        var failOnce = false; var throwOnce = false; var unreadable = false;
        var service = new WindowsPowerPolicyService(() => current,
            (setting, dc, plan) => unreadable ? null : values.GetValueOrDefault((plan, setting, dc), plan == a ? 42 : 73),
            (setting, value, dc, plan) =>
            {
                writes.Add(plan);
                if (throwOnce) { throwOnce = false; throw new IOException("injected write fault"); }
                if (failOnce) { failOnce = false; return false; }
                values[(plan, setting, dc)] = value; return true;
            },
            plan => { activations.Add(plan); return true; });
        Check(service.Apply(PowerPreset.Performance, out _), "plan A applies with complete independent backup");
        current = b; writes.Clear(); activations.Clear();
        Check(service.Restore(out _), "plan A restores while plan B is active");
        Check(writes.Count == 6 && writes.All(p => p == a) && activations.Count == 0, "restore touches only backed-up plan and never switches user's current plan");
        Check(values.All(p => p.Value == 42), "all six original AC/DC values return");
        Check(!service.HasBackup, "successful restore consumes backup so next session captures current settings");
        Check(service.Apply(PowerPreset.Saver, out _) && service.ApplyTemporary(PowerPreset.Performance, out _), "fixed and game presets use separate backups");
        Check(service.RestoreTemporary(out _), "temporary game power can restore independently");
        Check(values.Where(p => p.Key.Item1 == b).All(p => p.Value is 10 or 100 or 80), "game exit preserves the user's previous fixed preset");
        Check(service.Restore(out _), "fixed preset still has its original backup");
        Check(values.Where(p => p.Key.Item1 == b).All(p => p.Value == 73), "fixed restoration returns plan B's own values");
        failOnce = true;
        Check(!service.Apply(PowerPreset.Performance, out var error) && error.Contains("撤销"), "partial application is rolled back instead of falsely succeeding");
        Check(values.Where(p => p.Key.Item1 == b).All(p => p.Value == 73), "partial apply rollback returns every value to its pre-call state");
        service.Restore(out _);
        throwOnce = true;
        Check(!service.Apply(PowerPreset.Performance, out error) && error.Contains("撤销"), "native write exception also rolls back before reporting failure");
        Check(values.Where(p => p.Key.Item1 == b).All(p => p.Value == 73), "exception rollback preserves pre-call plan values");
        service.Restore(out _); writes.Clear(); unreadable = true;
        Check(!service.Apply(PowerPreset.Performance, out _) && writes.Count == 0, "incomplete six-value backup refuses all writes");
        unreadable = false;
        failOnce = true;
        Check(!service.ApplyTemporary(PowerPreset.Performance, out _), "failed temporary preset rolls back");
        Check(!service.HasBackup, "rolled-back preset leaves no stale backup ownership");
        foreach (var key in values.Keys.Where(key => key.Item1 == b).ToArray()) values[key] = 66;
        writes.Clear();
        Check(service.RestoreTemporary(out _) && writes.Count == 0, "failed game preset cannot later overwrite manual power changes");
        Check(values.Where(p => p.Key.Item1 == b).All(p => p.Value == 66), "manual changes survive stale restore attempt");
        var backupPath = Path.Combine(EnergyHistoryStore.DataDirectory, "power-backup.json");
        var keys = (string[])typeof(WindowsPowerPolicyService).GetField("SettingGuids", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        File.WriteAllText(backupPath, System.Text.Json.JsonSerializer.Serialize(new
        {
            Scheme = a.ToString(), Ac = keys.ToDictionary(k => k, _ => 31), Dc = keys.ToDictionary(k => k, _ => 27),
        }));
        Check(service.Restore(out _) && writes.Count == 6 && writes.All(plan => plan == a), "valid legacy backup migrates and restores its original scheme");
        writes.Clear();
        File.WriteAllText(backupPath, "{\"Scheme\":\"bad-guid\",\"Ac\":{},\"Dc\":{}}");
        Check(!service.Restore(out _) && writes.Count == 0, "malformed legacy backup cannot write to any power plan");
        File.Delete(backupPath); Directory.CreateDirectory(backupPath);
        Check(!service.Apply(PowerPreset.Performance, out _) && writes.Count == 0, "backup commit failure refuses all native writes");
    }

    private static void UpdateRecovery()
    {
        var root = Path.Combine(EnergyHistoryStore.DataDirectory, "updater with spaces"); Directory.CreateDirectory(root);
        var stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage);
        var script = Path.Combine(stage, "update.cmd");
        typeof(UpdateService).GetMethod("WriteUpdateScript", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [script, 2]);
        var missing = Path.Combine(root, "missing");
        int Run(string install, string? pid = null)
        {
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
            start.Arguments = "/d /c \"\"" + script + "\" \"" + install + "\" \"" + stage + "\"" + (pid is null ? "" : " " + pid) + "\"";
            start.Environment["COREBUSY_UPDATE_SILENT"] = "1";
            start.Environment["COREBUSY_UPDATE_TEST_MARKER"] = Path.Combine(root, "started.txt");
            using var child = Process.Start(start)!;
            if (!child.WaitForExit(10000)) { child.Kill(true); throw new Exception("updater timeout"); }
            return child.ExitCode;
        }
        Check(!UpdateService.CanInstall(missing, stage) && Run(missing) == 1, "permanent missing-directory update error exits promptly");
        var install = Path.Combine(root, "install"); Directory.CreateDirectory(install);
        foreach (var f in Directory.GetFiles(AppContext.BaseDirectory)) File.Copy(f, Path.Combine(install, Path.GetFileName(f)), true);
        var host = Path.Combine(AppContext.BaseDirectory, "CoreBusy.RegressionTests.exe");
        var exe = Path.Combine(install, "CoreBusy.App.exe"); File.Copy(host, exe, true);
        File.Copy(host, Path.Combine(stage, "CoreBusy.App.exe"), true);
        Check(UpdateService.CanInstall(install, stage), "valid isolated update passes preflight");
        Check(Run(install) == 0, "real updater installs and starts isolated executable");
        var marker = Path.Combine(root, "started.txt");
        var wait = Stopwatch.StartNew(); while (!File.Exists(marker) && wait.Elapsed.TotalSeconds < 5) Thread.Sleep(25);
        Check(File.Exists(marker) && !File.Exists(exe + ".old"), "updated process actually starts and backup is cleaned after successful copy");
        File.Delete(marker);
        using (var previous = StartSelf("--hold", "500"))
            Check(Run(install, previous.Id.ToString()) == 0 && previous.HasExited, "updater waits for original process before replacement and restart");
        using (File.Open(exe, FileMode.Open, FileAccess.Read, FileShare.Read))
            Check(Run(install) == 1 && File.Exists(exe), "permanent sharing violation reaches bounded retry limit without deleting old EXE");
        File.Delete(marker);
        using (File.Open(Path.Combine(stage, "CoreBusy.App.exe"), FileMode.Open, FileAccess.Read, FileShare.None))
            Check(Run(install) == 1 && File.Exists(exe) && !File.Exists(exe + ".old"), "copy failure restores old EXE after rename");
        wait.Restart(); while (!File.Exists(marker) && wait.Elapsed.TotalSeconds < 5) Thread.Sleep(25);
        Check(File.Exists(marker), "rolled-back application actually restarts after copy failure");
        Directory.CreateDirectory(exe + ".old");
        Check(Run(install) == 1 && File.Exists(exe), "permanent rename error stops after bounded retries and preserves old EXE");
    }

    private static void RecoveryUi(MainWindow owner, string evidence)
    {
        var originalData = Environment.GetEnvironmentVariable(EnergyHistoryStore.DataDirEnvVar)!;
        Environment.SetEnvironmentVariable(EnergyHistoryStore.DataDirEnvVar, Path.Combine(originalData, "activation-window"));
        SettingsStore.Save(new AppSettings { CheckUpdatesOnStartup = false, CloseToTray = false });
        var monitor = new LoadMonitor(); var vm = new DashboardViewModel(monitor); var window = new MainWindow(vm) { Owner = owner };
        window.Show(); Pump(); window.Hide(); Pump();
        Check(InstanceActivation.Request(), "activation finds hidden window by isolated data-directory key"); Pump();
        Check(window.IsVisible && Field<bool>(vm, "_isPresentationActive") && monitor.SampleInterval.TotalMilliseconds == 1000, "activation restores WPF visibility and presentation sampling");
        window.WindowState = WindowState.Minimized; Pump(); InstanceActivation.Request(); Pump();
        Check(window.WindowState == WindowState.Normal && Field<bool>(vm, "_isPresentationActive"), "activation also restores minimized WPF window");
        monitor.SampleTimestamp = Stopwatch.GetTimestamp() - 10 * Stopwatch.Frequency;
        vm.Tick(); window.Width = window.MinWidth; window.Height = window.MinHeight; Pump();
        Check(((ScrollViewer)window.FindName("CoreScroll")).ScrollableHeight < .1, "expired-sample hint keeps all eight cores visible at minimum size");
        Capture(window, Path.Combine(evidence, "monitor-expired-eight-core.png"));
        window.Close();
        var failed = new MainWindow(new DashboardViewModel(new UnavailableCpuMonitorService("采集失败，请检查性能计数器"))) { Owner = owner };
        failed.Show(); Pump();
        Check(Visuals<TextBlock>(failed).Any(t => t.IsVisible && t.Text.Contains("采集失败")), "actual unavailable monitor window has visible actionable empty state");
        Capture(failed, Path.Combine(evidence, "monitor-unavailable.png")); failed.Close();
        Environment.SetEnvironmentVariable(EnergyHistoryStore.DataDirEnvVar, originalData);
    }

    private sealed class RecoveryPower : IPowerPolicyService
    {
        public int Applies, Restores, Discards;
        public bool HasBackup => true;
        public PowerPolicyState Read() => new(true, 10, 50, 100, true, "test");
        public bool Apply(PowerPreset p, out string error) { Applies++; error = ""; return true; }
        public bool Restore(out string error) { Restores++; error = ""; return true; }
        public void DiscardTemporaryBackup() => Discards++;
    }
    private sealed class RecoveryGame : IGameDetector { public string? Process = "test-game.exe"; public string? DetectForegroundGame() => Process; }
    private sealed class RecoveryOptimization : ICpuOptimizationService
    {
        public int Exits; public bool ThrowOnExit;
        public bool IsSupported => true; public string StatusDetail => "test";
        public IReadOnlyList<CoreAffinityMode> AvailableAffinityModes => []; public IReadOnlyList<LogicalProcessorTopology> TopologyView => [];
        public string? ValidateCustomMask(ulong mask) => null; public IReadOnlyList<string> ListRunningProcessNames() => [];
        public bool TryApplyRule(ProcessOptimizationRule rule, int pid, out string error) { error = ""; return true; }
        public int ApplyRulesNow(OptimizationSettings settings, bool active) => 0;
        public void ApplyGameMode(OptimizationSettings settings, string? process) { if (process is null) { Exits++; if (ThrowOnExit) throw new IOException("test cleanup failure"); } }
    }
    private static void NativePowerRecovery()
    {
        Guid Active()
        {
            if (PowerAuditGetActive(IntPtr.Zero, out var ptr) != 0) throw new IOException("Cannot read active power plan");
            try { return Marshal.PtrToStructure<Guid>(ptr); } finally { PowerAuditFree(ptr); }
        }
        var original = Active(); var source = original; var pointer = IntPtr.Zero;
        var result = PowerAuditDuplicate(IntPtr.Zero, ref source, ref pointer);
        if (result != 0) { Console.WriteLine("SKIP: isolated native power plan unavailable, code=" + result); return; }
        var clone = Marshal.PtrToStructure<Guid>(pointer); PowerAuditFree(pointer);
        try
        {
            var type = typeof(WindowsPowerPolicyService);
            var read = type.GetMethod("ReadValue", BindingFlags.NonPublic | BindingFlags.Static)!;
            var write = type.GetMethods(BindingFlags.NonPublic | BindingFlags.Static).Single(m => m.Name == "TryWrite" && m.GetParameters().Length == 4);
            var settings = (string[])type.GetField("SettingGuids", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            int? Read(string setting, bool dc, Guid plan) => (int?)read.Invoke(null, [setting, dc, plan]);
            var before = settings.SelectMany(setting => new[] { false, true }.Select(dc => (setting, dc, value: Read(setting, dc, clone)))).ToArray();
            if (before.Any(v => v.value is null)) { Console.WriteLine("SKIP: native clone has unsupported processor settings"); return; }
            var current = clone;
            var service = new WindowsPowerPolicyService(() => current, Read, (setting, value, dc, plan) =>
            {
                if (plan != clone) throw new Exception("Native test refused write outside its own clone");
                return (bool)write.Invoke(null, [setting, value, dc, plan])!;
            }, _ => true);
            Check(service.Apply(PowerPreset.Performance, out var error), "native writes to isolated cloned power plan: " + error);
            current = original;
            Check(service.Restore(out error), "native restore targets stored clone GUID rather than current original: " + error);
            foreach (var v in before) Equal(v.value!.Value, Read(v.setting, v.dc, clone)!.Value, "native AC/DC round-trip preserves clone originals");
            Check(Active() == original, "native power verification never switches the active system plan");
        }
        finally { Check(PowerAuditDelete(IntPtr.Zero, ref clone) == 0, "owned temporary power plan is removed after verification"); }
    }

    private sealed class BlockingSensors : IHardwareSensorService
    {
        public int Reads; public readonly ManualResetEventSlim Release = new();
        public void Start() { }
        public HardwareSensorSnapshot ReadSnapshot()
        {
            if (Interlocked.Increment(ref Reads) >= 2) Release.Wait();
            return new() { PackagePowerW = 60, PackageTemperatureC = 35, FanRpm = 1000 };
        }
        public IntelBaseClockProbe ProbeIntelBaseClock() => new();
        public void Dispose() { Release.Set(); }
    }

    private static void BlockedSamplingRecovery()
    {
        var sensors = new BlockingSensors();
        using var monitor = new WindowsCpuMonitorService(sensors);
        monitor.SampleInterval = TimeSpan.FromMilliseconds(200);
        try
        {
            var wait = Stopwatch.StartNew(); while (sensors.Reads < 2 && wait.Elapsed.TotalSeconds < 8) Thread.Sleep(25);
            Check(sensors.Reads == 2, "real sampler enters controlled sensor blockage after a valid frame");
            Thread.Sleep(1200);
            Check(double.IsNaN(monitor.GetSnapshot().PackagePowerW), "blocked native sampler expires its cached power automatically");
            Check(monitor.GetCoreSnapshots().All(c => double.IsNaN(c.UsagePercent)), "blocked native sampler expires cached core usage too");
            Check(!monitor.GetFrame().IsFresh(monitor.SampleInterval), "expired frame retains original monotonic capture timestamp");
            sensors.Release.Set();
            wait.Restart(); while (!double.IsFinite(monitor.GetSnapshot().PackagePowerW) && wait.Elapsed.TotalSeconds < 5) Thread.Sleep(25);
            Check(double.IsFinite(monitor.GetSnapshot().PackagePowerW), "native sampling recovers after sensor unblocks");
        }
        finally { sensors.Release.Set(); }
    }

    [DllImport("powrprof.dll", EntryPoint = "PowerGetActiveScheme")] private static extern uint PowerAuditGetActive(IntPtr key, out IntPtr guid);
    [DllImport("powrprof.dll", EntryPoint = "PowerDuplicateScheme")] private static extern uint PowerAuditDuplicate(IntPtr key, ref Guid source, ref IntPtr target);
    [DllImport("powrprof.dll", EntryPoint = "PowerDeleteScheme")] private static extern uint PowerAuditDelete(IntPtr key, ref Guid scheme);
    [DllImport("kernel32.dll", EntryPoint = "LocalFree")] private static extern IntPtr PowerAuditFree(IntPtr memory);

}
