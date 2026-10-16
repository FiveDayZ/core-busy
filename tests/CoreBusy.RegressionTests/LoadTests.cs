using System.Windows;
using System.Windows.Controls;
using CoreBusy.App.Controls;
using CoreBusy.App.Views;
using CoreBusy.App.ViewModels.Dashboard;
using CoreBusy.Core.Models;
using CoreBusy.Core.Interfaces;
using CoreBusy.Core.Progression;
using CoreBusy.App.Services;
using CoreBusy.Windows.Monitoring;
using System.Diagnostics;
using System.Reflection;
using System.IO;


internal static partial class Program
{
    private static CpuCoreSnapshot LoadSample(string id, double usage, params CpuThreadSnapshot[] threads)
        => new() { Id = id, UsagePercent = usage, Threads = threads };

    private static void CumulativeLoadRules()
    {
        var tracker = new CumulativeLoadTracker();
        var single = LoadSample("E0", 30, new CpuThreadSnapshot { Id = "E0", CoreId = "E0", UsagePercent = 30 });
        tracker.Accumulate([single], 10);
        Equal(3, tracker.BusySeconds("E0"), "single-thread core is integrated only once");
        Equal(30, tracker.AveragePercent("E0"), "single-thread cumulative average is not doubled");
        tracker.Reset();
        tracker.Accumulate([single with { UsagePercent = 100 }], 10);
        Equal(100, tracker.AveragePercent("E0"), "single-thread cumulative average never exceeds 100 percent");
        tracker.Reset();
        tracker.Accumulate([LoadSample("C0", 20)], 1);
        tracker.Accumulate([LoadSample("C0", 80)], 3);
        Equal(65, tracker.AveragePercent("C0"), "unequal intervals use time-weighted average");
        Equal(2.6, tracker.BusySeconds("C0"), "weighted full-load seconds are correct");
        tracker.Accumulate([LoadSample("C1", 100)], 10);
        Equal(65, tracker.AveragePercent("C0"), "missing core does not dilute existing observations");
        Equal(100, tracker.AveragePercent("C1"), "late core uses its own observation duration");
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1, 101 })
            tracker.Accumulate([LoadSample("C0", invalid)], 10);
        Equal(65, tracker.AveragePercent("C0"), "invalid percentages cannot poison or dilute cumulative load");
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1, 0 })
            tracker.Accumulate([LoadSample("C0", 100)], invalid);
        Equal(65, tracker.AveragePercent("C0"), "invalid intervals cannot poison cumulative load");
        tracker.Accumulate([LoadSample("C0", 0)], 4);
        Equal(32.5, tracker.AveragePercent("C0"), "valid idle zero is counted, unlike a missing sample");
        tracker.Reset();
        var smt = LoadSample("P0", 80, new() { Id = "P0·0", UsagePercent = 80 }, new() { Id = "P0·1", UsagePercent = 20 });
        tracker.Accumulate([smt], 10);
        Equal(8, tracker.BusySeconds("P0"), "SMT physical core retains its own busy time");
        Equal(80, tracker.AveragePercent("P0·0"), "first SMT thread is independent");
        Equal(20, tracker.AveragePercent("P0·1"), "second SMT thread is independent");
        tracker.Accumulate([smt with { UsagePercent = double.NaN, Threads = [smt.Threads[0] with { UsagePercent = 0 }, smt.Threads[1] with { UsagePercent = double.NaN }] }], 10);
        Equal(80, tracker.AveragePercent("P0"), "missing parent does not discard valid thread sample");
        Equal(40, tracker.AveragePercent("P0·0"), "valid thread zero is integrated independently");
        Equal(20, tracker.AveragePercent("P0·1"), "missing thread does not dilute its mean");
        tracker.Reset();
        Equal(0, tracker.ObservedSeconds, "reset clears session observation duration");
        Equal(0, tracker.BusySeconds("P0"), "reset clears busy time");
        Equal(0, tracker.AveragePercent("P0"), "reset clears per-core denominator");
        var roles = new CoreRoleAccumulator();
        roles.Accumulate([single], 60, DateTime.Now);
        var observed = roles.ObservedOf("E0");
        roles.Accumulate([single with { UsagePercent = double.NaN }], 60, DateTime.Now);
        Equal(observed, roles.ObservedOf("E0"), "missing usage cannot dilute role statistics either");
        SensorLifecycle();
        InDirectory(Path.Combine(Path.GetTempPath(), "corebusy-load-" + Guid.NewGuid().ToString("N")), "dashboard", LoadIntegration);
    }

    private sealed class LoadMonitor : ICpuMonitorService
    {
        private readonly MockCpuMonitorService _source = new(CoreLayoutMode.Spec, "amd-single");
        public CpuCoreSnapshot[] Samples;
        public double TotalUsage { get; set; } = 30;
        public long? SampleTimestamp;
        public CpuMonitorFrame GetFrame() => new(GetSnapshot(), GetCoreSnapshots())
            { CapturedTimestamp = SampleTimestamp ?? Stopwatch.GetTimestamp() };
        public LoadMonitor() => Samples = _source.GetCoreSnapshots().Select(s => s with { UsagePercent = 30 }).ToArray();
        public TimeSpan SampleInterval { get; set; } = TimeSpan.FromSeconds(1);
        public bool SensorsEnabled { get; set; }
        public CpuInfo GetCpuInfo() => _source.GetCpuInfo();
        public IReadOnlyList<CpuCoreGroup> GetCoreGroups() => _source.GetCoreGroups();
        public CpuSnapshot GetSnapshot() => new() { TotalUsagePercent = TotalUsage, PackagePowerW = 30 };
        public IReadOnlyList<CpuCoreSnapshot> GetCoreSnapshots() => Samples;
    }

    private static void LoadIntegration()
    {
        var monitor = new LoadMonitor();
        var vm = new DashboardViewModel(monitor);
        vm.Tick();
        var cores = Field<Dictionary<string, DashboardCoreVm>>(vm, "_allCoresById");
        monitor.Samples = monitor.Samples.Reverse().Select((s, i) => s with { UsagePercent = 10 + i * .3 }).ToArray();
        vm.Tick();
        foreach (var sample in monitor.Samples)
            Equal(sample.UsagePercent, cores[sample.Id].Usage, "live core lookup tolerates reordered snapshots");
        var tracker = Field<CumulativeLoadTracker>(vm, "_cumulative");
        tracker.Reset();
        tracker.Accumulate(monitor.Samples, 10);
        vm.SetCumulativeLoad(true);
        var expected = monitor.Samples.OrderByDescending(s => s.UsagePercent).ToArray();
        for (var i = 0; i < expected.Length; i++)
            Check(cores[expected[i].Id].StatusLabel == $"#{i + 1} / {expected.Length}", "close cumulative values have mathematically correct ranks");
        var exp = cores["C0"].Level.ExpCoreMinutes;
        vm.ResetCumulativeLoad();
        Check(cores["C0"].Level.ExpCoreMinutes >= exp, "resetting session load never resets EXP ledger");
        vm.SetCumulativeLoad(false);
        monitor.TotalUsage = double.NaN;
        monitor.Samples = monitor.Samples.Select(s => s with { UsagePercent = double.NaN, Threads = s.Threads.Select(t => t with { UsagePercent = double.NaN }).ToArray() }).ToArray();
        vm.Tick();
        Check(vm.TotalUsageText == "-" && double.IsFinite(vm.TotalUsage), "missing total usage is clearly unavailable with a safe numeric bar");
        foreach (var core in cores.Values)
            Check(core.UsageText == "-" && core.LiveUsageText == "-" && core.StatusLabel == "未采样" && core.Threads.All(t => t.UsageText == "-"), "missing core and thread samples do not display idle zero or NaN");
        vm.SetPresentationActive(false);
        Equal(2000, monitor.SampleInterval.TotalMilliseconds, "hidden window uses two-second sampling without disabling sensors");
        var before = tracker.BusySeconds("C0");
        var expBefore = Field<CoreExpAccumulator>(vm, "_exp").ExpCoreMinutes("C0");
        monitor.Samples = monitor.Samples.Select(s => s with { UsagePercent = 80 }).ToArray();
        Thread.Sleep(25);
        vm.Tick();
        Check(tracker.BusySeconds("C0") > before, "hidden window continues integrating load");
        Check(Field<CoreExpAccumulator>(vm, "_exp").ExpCoreMinutes("C0") > expBefore, "hidden window continues earning EXP");
        Check(cores["C0"].UsageText == "-", "hidden window skips card presentation refresh");
        vm.SetPresentationActive(true);
        Equal(1000, monitor.SampleInterval.TotalMilliseconds, "restoring window restores the configured interval");
        Check(cores["C0"].UsageText == "80%", "restoring window immediately shows current usage");
        vm.Stop();
        var ranker = new ProcessCpuRanker(Environment.ProcessorCount);
        ranker.SampleTop(5);
        ranker.Dispose();
        Check(((System.Collections.IDictionary)typeof(ProcessCpuRanker).GetField("_tracked", PrivateInstance)!.GetValue(ranker)!).Count == 0, "disposing process sampler releases its cached process handles");
        ranker.Dispose();
        var monitorType = typeof(WindowsCpuMonitorService);
        var coreUsage = monitorType.GetMethod("CoreUsage", BindingFlags.Static | BindingFlags.NonPublic)!;
        double Core(double[]? values, params int[] indices) => (double)coreUsage.Invoke(null, [values, indices])!;
        Equal(80, Core([20, 80], 0, 1), "Windows physical usage retains maximum SMT-thread convention");
        Equal(0, Core([0], 0), "Windows valid idle remains zero");
        foreach (var values in new double[]?[] { null, [], [double.NaN], [double.PositiveInfinity], [-1], [101] })
            Check(double.IsNaN(Core(values, 0)), "Windows missing or invalid counters cannot appear as idle or stale");
        Check(double.IsNaN(Core([80, double.NaN], 0, 1)), "partial thread loss marks physical usage unknown");
        var sampler = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(monitorType);
        monitorType.GetField("_ranker", PrivateInstance)!.SetValue(sampler, ranker);
        var sampleProcesses = monitorType.GetMethod("SampleProcesses", PrivateInstance)!;
        Check(sampleProcesses.Invoke(sampler, null) is List<ProcessLoadSample>, "first process sample is published, including empty results");
        Check(sampleProcesses.Invoke(sampler, null) is null, "process CPU sampling is throttled between five-second intervals");
        ranker.Dispose();
    }
    private static void LoadAndPerformanceUi(MainWindow owner, string evidence)
    {
        var monitor = new LoadMonitor();
        var vm = new DashboardViewModel(monitor);
        var window = new MainWindow(vm) { Owner = owner };
        window.Show(); Pump();
        var tiles = Visuals<CpuCoreTile>(window).ToArray();
        Check(tiles.Length == 8, "load regression runs against eight actual WPF cards");
        monitor.Samples = monitor.Samples.Select((s, i) => s with { UsagePercent = 20 + i * 5 }).ToArray();
        vm.Tick(); Pump();
        Visuals<Button>(window).First(b => Equals(b.Content, "累积")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        Check(vm.IsCumulativeLoad && tiles.All(t => ((DashboardCoreVm)t.DataContext).Usage is >= 0 and <= 100), "actual cumulative button displays bounded weighted load");
        Capture(window, Path.Combine(evidence, "load-cumulative.png"));
        Visuals<Button>(window).First(b => Equals(b.Content, "实时")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        monitor.TotalUsage = double.NaN;
        monitor.Samples = monitor.Samples.Select(s => s with { UsagePercent = double.NaN, Threads = s.Threads.Select(t => t with { UsagePercent = double.NaN }).ToArray() }).ToArray();
        vm.Tick(); Pump();
        Check(tiles.All(t => Visuals<TextBlock>(t).Any(b => b.IsVisible && b.Text == "未采样")), "actual cards visibly mark missing counter samples");
        Check(!Visuals<TextBlock>(window).Any(t => t.IsVisible && t.Text.Contains("NaN")), "no nonfinite text reaches rendered UI");
        Capture(window, Path.Combine(evidence, "load-unavailable.png"));
        monitor.Samples = monitor.Samples.Select(s => s with { UsagePercent = 60 }).ToArray();
        vm.Tick(); Pump();
        var tracker = Field<CumulativeLoadTracker>(vm, "_cumulative");
        var before = tracker.BusySeconds("C0");
        var exp = Field<CoreExpAccumulator>(vm, "_exp").ExpCoreMinutes("C0");
        window.Hide(); Pump(2200);
        Check(!Field<bool>(vm, "_isPresentationActive") && monitor.SampleInterval.TotalMilliseconds == 2000, "actual hide event enables low-overhead background sampling");
        Check(tracker.BusySeconds("C0") > before && Field<CoreExpAccumulator>(vm, "_exp").ExpCoreMinutes("C0") > exp, "actual hidden timer continues load and EXP integration");
        window.Show(); Pump();
        Check(Field<bool>(vm, "_isPresentationActive") && monitor.SampleInterval.TotalMilliseconds == 1000, "actual show event restores normal sampling");
        Check(tiles.All(t => ((DashboardCoreVm)t.DataContext).UsageText == "60%"), "restored cards immediately show fresh readings");
        window.WindowState = WindowState.Minimized; Pump();
        Check(!Field<bool>(vm, "_isPresentationActive"), "minimized window also pauses presentation");
        window.WindowState = WindowState.Normal; Pump();
        Check(Field<bool>(vm, "_isPresentationActive"), "unminimize resumes presentation");
        Capture(window, Path.Combine(evidence, "load-restored.png"));
        window.Close();
    }

    private sealed class CountingSensors : IHardwareSensorService
    {
        public int Starts, Reads, Probes, Disposals;
        public void Start() => Interlocked.Increment(ref Starts);
        public HardwareSensorSnapshot ReadSnapshot()
        {
            Interlocked.Increment(ref Reads);
            return new HardwareSensorSnapshot { PackageTemperatureC = 35, FanRpm = 1000 };
        }
        public IntelBaseClockProbe ProbeIntelBaseClock() { Interlocked.Increment(ref Probes); return new IntelBaseClockProbe { Note = "test" }; }
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }

    private static void SensorLifecycle()
    {
        var sensors = new CountingSensors();
        var type = typeof(WindowsCpuMonitorService);
        var sampler = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
        type.GetField("_sensors", PrivateInstance)!.SetValue(sampler, sensors);
        type.GetField("_info", PrivateInstance)!.SetValue(sampler, new CpuInfo { VendorKind = CpuVendor.Amd });
        Call(sampler, "EnsureSensorsStarted");
        Call(sampler, "EnsureSensorsStarted");
        Check(sensors.Starts == 1, "lazy sensor initialization runs once on the sampling thread");
    }

    private static void WindowsSampling()
    {
        var sensors = new CountingSensors();
        var monitor = new WindowsCpuMonitorService(sensors, CoreLayoutMode.Spec, sensorsEnabled: false);
        try
        {
            monitor.SampleInterval = TimeSpan.FromMilliseconds(200);
            bool Await(Func<bool> predicate)
            {
                var timer = Stopwatch.StartNew();
                while (!predicate() && timer.Elapsed.TotalSeconds < 5) Thread.Sleep(25);
                return predicate();
            }
            Check(Await(() => double.IsFinite(monitor.GetSnapshot().TotalUsagePercent)), "real Windows counters publish valid usage with sensors disabled");
            Check(sensors.Starts == 0 && sensors.Reads == 0 && sensors.Probes == 0, "disabled startup never initializes or polls hardware sensors");
            Check(monitor.GetCoreSnapshots().All(s => s.UsagePercent is >= 0 and <= 100), "real Windows physical samples remain in range");
            monitor.SensorsEnabled = true;
            Check(Await(() => sensors.Reads > 0), "enabling sensors starts lazy initialization and sampling");
            Equal(1, sensors.Starts, "sensor startup occurs exactly once");
            monitor.SensorsEnabled = false;
            Thread.Sleep(300);
            var reads = sensors.Reads;
            Thread.Sleep(300);
            Equal(reads, sensors.Reads, "disabled runtime pauses sensor polls");
            monitor.SensorsEnabled = true;
            Check(Await(() => sensors.Reads > reads) && sensors.Starts == 1, "re-enabling resumes polling without reopening hardware");
        }
        finally { monitor.Dispose(); }
        Equal(1, sensors.Disposals, "real sampler shutdown disposes its sensor service");
        Check(Field<System.Collections.IDictionary>(Field<ProcessCpuRanker>(monitor, "_ranker"), "_tracked").Count == 0, "real sampler shutdown releases process handles");
    }

}
