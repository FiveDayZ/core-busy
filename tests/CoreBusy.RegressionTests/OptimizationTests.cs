using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CoreBusy.App.Views;
using CoreBusy.App.Services;
using CoreBusy.App.ViewModels.Dashboard;
using CoreBusy.Core.Models;
using CoreBusy.Windows.Optimization;

internal static partial class Program
{
    // 独立命名的休眠进程；规则扫描不能匹配测试宿主或任何用户进程。
    private sealed class OptimizationWorker : IDisposable
    {
        private readonly string _path;
        private readonly bool _ownsPath;
        public Process Process { get; }
        public string Name => Path.GetFileName(_path);
        public OptimizationWorker(string? path = null)
        {
            _ownsPath = path is null;
            _path = path ?? Path.Combine(AppContext.BaseDirectory, "cb-opt-" + Guid.NewGuid().ToString("N") + ".exe");
            if (!File.Exists(_path)) File.Copy(Environment.ProcessPath!, _path);
            Process = Process.Start(new ProcessStartInfo(_path, "--optimization-worker") { UseShellExecute = false, CreateNoWindow = true })!;
            // Windows 可从低优先级宿主继承初值；显式建立本组恢复断言的标准基线。
            Process.PriorityClass = ProcessPriorityClass.Normal;
        }
        public OptimizationWorker SameName() => new(_path);
        public void Dispose()
        {
            if (!Process.HasExited) Process.Kill();
            Process.WaitForExit(); Process.Dispose();
            if (_ownsPath) File.Delete(_path);
        }
    }

    private static int IoPriority(Process process)
    {
        Check(ProcessOptimizationNative.NtQueryInformationProcess(process.Handle, 33, out var io, sizeof(int), out _) == 0, "native process I/O priority can be read");
        return io;
    }

    private static void OptimizationRules(string root)
    {
        InDirectory(root, "optimization-native", () =>
        {
            using var worker = new OptimizationWorker();
            var p = worker.Process;
            using var service = new WindowsCpuOptimizationService();
            var full = p.ProcessorAffinity;
            var one = new IntPtr(full.ToInt64() & -full.ToInt64());
            p.PriorityClass = ProcessPriorityClass.AboveNormal;
            p.ProcessorAffinity = one;
            var io = IoPriority(p);
            var rule = new ProcessOptimizationRule { ProcessName = worker.Name, GameOnly = true, Priority = ProcessPriorityLevel.BelowNormal, LowerIoPriority = true };
            var settings = new OptimizationSettings { Enabled = true, Rules = [rule] };
            Check(service.ApplyRulesNow(settings, true) == 1, "game-only rule actually changes native process parameters");
            p.Refresh(); Check(p.PriorityClass == ProcessPriorityClass.BelowNormal && IoPriority(p) == 0, "native CPU and I/O priorities reflect requested values");
            service.ApplyRulesNow(settings, false);
            p.Refresh(); Check(p.PriorityClass == ProcessPriorityClass.AboveNormal && p.ProcessorAffinity == one && IoPriority(p) == io, "game exit restores original priority and I/O without widening untouched affinity");

            rule.Affinity = CoreAffinityMode.Custom; rule.CustomAffinityMask = 0;
            Check(!service.TryApplyRule(rule, p.Id, out _), "invalid affinity rejects the entire rule before priority mutation");
            p.Refresh(); Check(p.PriorityClass == ProcessPriorityClass.AboveNormal && IoPriority(p) == io, "failed validation leaves all native fields unchanged");
            rule.CustomAffinityMask = 1UL << 63;
            if (service.TopologyView.Count < 64) Check(!service.TryApplyRule(rule, p.Id, out _), "service rejects custom affinity bits outside real topology");
            rule.Affinity = CoreAffinityMode.None; rule.KernelEnforced = true;
            Check(!service.TryApplyRule(rule, p.Id, out _), "temporary rule cannot acquire persistent job restrictions");
            rule.KernelEnforced = false; rule.GameOnly = false; rule.LowerIoPriority = false;
            Check(service.ApplyRulesNow(settings, false) == 1, "rule edit bypasses scan throttle immediately");
            p.PriorityClass = ProcessPriorityClass.Normal;
            typeof(WindowsCpuOptimizationService).GetField("_lastScanUtc", PrivateInstance)!.SetValue(service, DateTime.MinValue);
            Check(service.ApplyRulesNow(settings, false) == 1, "same-signature rule detects and repairs external changes");
            p.Refresh(); Check(p.PriorityClass == ProcessPriorityClass.BelowNormal, "native readback confirms rule enforcement after external change");
            settings.Rules.Clear(); service.ApplyRulesNow(settings, false);
            p.Refresh(); Check(p.PriorityClass == ProcessPriorityClass.AboveNormal, "rule deletion restores original priority without waiting for throttle");

            rule.Affinity = CoreAffinityMode.Custom; rule.CustomAffinityMask = (ulong)one.ToInt64(); rule.KernelEnforced = true;
            p.ProcessorAffinity = full;
            var enforced = service.TryApplyRule(rule, p.Id, out var error);
            if (!enforced)
            {
                p.Refresh(); Check(p.PriorityClass == ProcessPriorityClass.AboveNormal && p.ProcessorAffinity == full, "native job failure rolls back already-written priority and affinity: " + error);
            }
            else
            {
                service.Dispose(); p.Refresh();
                Check(p.PriorityClass == ProcessPriorityClass.AboveNormal && p.ProcessorAffinity == full, "job success releases limits before restoring process originals");
            }
        });
        InDirectory(root, "optimization-games", () =>
        {
            using var a = new OptimizationWorker(); using var a2 = a.SameName(); using var b = new OptimizationWorker();
            using var service = new WindowsCpuOptimizationService();
            a.Process.PriorityClass = ProcessPriorityClass.BelowNormal;
            var settings = new OptimizationSettings { Enabled = true, GameModeEnabled = true, GamePriority = ProcessPriorityLevel.AboveNormal };
            service.ApplyGameMode(settings, a.Name);
            a.Process.Refresh(); a2.Process.Refresh();
            Check(a.Process.PriorityClass == ProcessPriorityClass.AboveNormal && a2.Process.PriorityClass == ProcessPriorityClass.AboveNormal, "game preset applies to all matching instances");
            service.ApplyGameMode(settings, b.Name);
            a.Process.Refresh(); a2.Process.Refresh(); b.Process.Refresh();
            Check(a.Process.PriorityClass == ProcessPriorityClass.BelowNormal && a2.Process.PriorityClass == ProcessPriorityClass.Normal, "direct game switch restores every old instance's own original priority");
            Check(b.Process.PriorityClass == ProcessPriorityClass.AboveNormal, "direct game switch applies new target");
            settings.GamePriority = ProcessPriorityLevel.BelowNormal; service.ApplyGameMode(settings, b.Name);
            b.Process.Refresh(); Check(b.Process.PriorityClass == ProcessPriorityClass.BelowNormal, "same game receives preset edits immediately");
            service.ApplyGameMode(settings, null); b.Process.Refresh();
            Check(b.Process.PriorityClass == ProcessPriorityClass.Normal, "game exit restores original across multiple preset edits");
            var rule = new ProcessOptimizationRule { ProcessName = b.Name, Priority = ProcessPriorityLevel.BelowNormal };
            settings.Rules = [rule]; service.ApplyRulesNow(settings, false);
            settings.GamePriority = ProcessPriorityLevel.AboveNormal; service.ApplyGameMode(settings, b.Name);
            service.ApplyRulesNow(settings, true); b.Process.Refresh();
            Check(b.Process.PriorityClass == ProcessPriorityClass.AboveNormal, "game preset owns target while matching regular rule remains configured");
            service.ApplyGameMode(settings, null); service.ApplyRulesNow(settings, false); b.Process.Refresh();
            Check(b.Process.PriorityClass == ProcessPriorityClass.BelowNormal, "regular rule resumes after game overlay exits");
            settings.Enabled = false; service.ApplyRulesNow(settings, false); b.Process.Refresh();
            Check(b.Process.PriorityClass == ProcessPriorityClass.Normal, "disabling optimization restores original despite prior game and rule overlap");
        });
        InDirectory(root, "optimization-power-session", OptimizationPowerSession);
    }

    private static void OptimizationPowerSession()
    {
        var scheme = Guid.NewGuid(); var values = new Dictionary<(string, bool), int>(); var refuseWrites = false;
        var power = new WindowsPowerPolicyService(() => scheme,
            (key, dc, _) => values.GetValueOrDefault((key, dc), 42),
            (key, value, dc, _) => { if (refuseWrites) return false; values[(key, dc)] = value; return true; }, _ => true);
        Check(power.Apply(PowerPreset.Saver, out _), "fixed saver pre-exists game session");
        var saver = values.ToDictionary(p => p.Key, p => p.Value);
        var settings = GameSettings(); settings.Optimization.PowerPreset = PowerPreset.Saver;
        SettingsStore.Save(settings);
        var game = new RecoveryGame(); var optimization = new RecoveryOptimization();
        var vm = new DashboardViewModel(new MockCpuMonitorService(CoreLayoutMode.Spec, "amd-single"), optimization: optimization, powerPolicy: power, gameDetector: game);
        vm.Tick(); game.Process = null; vm.Tick();
        Check(saver.All(p => values[p.Key] == p.Value), "game exit restores fixed saver instead of discarding its temporary backup");
        vm.Stop(); Check(power.Restore(out _) && values.Values.All(v => v == 42), "fixed original backup remains available after temporary game session");

        // 游戏内首次应用固定预设，也必须保留进入游戏前的真实原值。
        Check(power.ApplyTemporary(PowerPreset.Performance, out _) && power.Apply(PowerPreset.Saver, out _), "explicit fixed preset can supersede active temporary preset");
        Check(power.RestoreTemporary(out _) && saver.All(p => values[p.Key] == p.Value), "old temporary restore cannot overwrite explicitly applied fixed preset");
        Check(power.Restore(out _) && values.Values.All(v => v == 42), "first fixed preset applied during game preserves pre-game originals");

        settings = GameSettings(); settings.Optimization.GameAppliesPowerPreset = false; SettingsStore.Save(settings);
        game.Process = "test-game.exe"; var fakePower = new RecoveryPower(); optimization = new RecoveryOptimization();
        vm = new DashboardViewModel(new MockCpuMonitorService(CoreLayoutMode.Spec, "amd-single"), optimization: optimization, powerPolicy: fakePower, gameDetector: game);
        vm.Tick(); settings.Optimization.GamePriority = ProcessPriorityLevel.High; settings.Optimization.GameAppliesPowerPreset = true;
        vm.ApplySettings(settings);
        Check(fakePower.Applies == 1 && optimization.Exits == 1, "saving same-game settings restarts session and immediately enables power linkage");
        settings.Optimization.GameAppliesPowerPreset = false; vm.ApplySettings(settings);
        Check(fakePower.Restores == 1, "disabling power linkage while game stays active restores temporary power immediately");
        vm.Stop();

        settings = GameSettings(); SettingsStore.Save(settings); game.Process = "test-game.exe";
        vm = new DashboardViewModel(new MockCpuMonitorService(CoreLayoutMode.Spec, "amd-single"), optimization: new RecoveryOptimization(), powerPolicy: power, gameDetector: game);
        refuseWrites = true; vm.Tick();
        Check(power.HasTemporaryBackup && Field<bool>(vm, "_gamePowerApplied"), "failed temporary apply and rollback retain VM restoration ownership");
        refuseWrites = false; game.Process = null; vm.Tick();
        Check(!power.HasTemporaryBackup && values.Values.All(v => v == 42), "game exit retries failed power rollback and consumes backup only after successful restore");
        vm.Stop();
    }

    private static void OptimizationUi(Window owner, string evidence)
    {
        var oldDirectory = Environment.GetEnvironmentVariable(EnergyHistoryStore.DataDirEnvVar);
        try
        {
            var dir = Path.Combine(oldDirectory!, "optimization-ui"); Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable(EnergyHistoryStore.DataDirEnvVar, dir);
            using var worker = new OptimizationWorker();
            using var optimization = new WindowsCpuOptimizationService();
            var plan = Guid.NewGuid(); var values = new Dictionary<(string, bool), int>();
            var power = new WindowsPowerPolicyService(() => plan, (key, dc, _) => values.GetValueOrDefault((key, dc), 42),
                (key, value, dc, _) => { values[(key, dc)] = value; return true; }, _ => true);
            var settings = GameSettings(); settings.Optimization.GameAppliesPowerPreset = false;
            SettingsStore.Save(settings);
            var game = new RecoveryGame { Process = worker.Name };
            var vm = new DashboardViewModel(new MockCpuMonitorService(CoreLayoutMode.Spec, "amd-single"),
                optimization: optimization, powerPolicy: power, gameDetector: game);
            try
            {
                vm.Tick();
                var dialog = new SettingsWindow(optimization, power, "Regression CPU") { Owner = owner };
                Exception? failure = null;
                dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    try
                    {
                        ((RadioButton)dialog.FindName("NavOptimize")).IsChecked = true;
                        ((RadioButton)dialog.FindName("PowerSaver")).IsChecked = true;
                        ((CheckBox)dialog.FindName("GamePowerCheck")).IsChecked = true;
                        ((ComboBox)dialog.FindName("GamePriorityCombo")).SelectedItem = "低于标准";
                        dialog.UpdateLayout();
                        Check(((TextBlock)dialog.FindName("OptimizeStatusText")).Text.Contains("回读"), "actual settings page displays verified optimization result");
                        Capture(dialog, Path.Combine(evidence, "settings-optimization-fixed.png"));
                        ((ScrollViewer)dialog.FindName("PaneOptimize")).ScrollToBottom(); dialog.UpdateLayout();
                        Capture(dialog, Path.Combine(evidence, "settings-optimization-game-preset.png"));
                        Visuals<Button>(dialog).Single(b => Equals(b.Content, "确定")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    catch (Exception ex) { failure = ex; dialog.Close(); }
                }));
                var accepted = dialog.ShowDialog();
                if (failure is not null) throw failure;
                Check(accepted == true, "actual optimization settings confirmation completes");
                vm.ApplySettings(dialog.Result); worker.Process.Refresh();
                Check(worker.Process.PriorityClass == ProcessPriorityClass.BelowNormal, "saving game preset through WPF immediately updates real target process");
                var saved = SettingsStore.Load().Optimization;
                Check(saved.PowerPreset == PowerPreset.Saver && saved.GameAppliesPowerPreset && saved.GamePriority == ProcessPriorityLevel.BelowNormal,
                    "optimization settings survive UI confirmation and persistence");
                game.Process = null; vm.Tick(); worker.Process.Refresh();
                Check(worker.Process.PriorityClass == ProcessPriorityClass.Normal, "UI-driven game session restores original process priority");
                Check(values.Values.Contains(10) && values.Values.Contains(80) && !values.Values.Contains(0), "UI-driven game exit restores explicitly selected fixed saver");
            }
            finally { vm.Stop(); }
        }
        finally { Environment.SetEnvironmentVariable(EnergyHistoryStore.DataDirEnvVar, oldDirectory); }
    }
}
