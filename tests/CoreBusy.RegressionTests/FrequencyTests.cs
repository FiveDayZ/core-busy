using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using CoreBusy.App.Controls;
using CoreBusy.App.Services;
using CoreBusy.App.Themes;
using CoreBusy.App.ViewModels.Dashboard;
using CoreBusy.App.Views;
using CoreBusy.Core.Interfaces;
using CoreBusy.Core.Models;

internal static partial class Program
{
    private static void FrequencyRules()
    {
        foreach (var kind in new[] { CoreClass.Performance, CoreClass.Efficiency })
        {
            var sample = new CpuCoreSnapshot { Id = "test", Class = kind, UsagePercent = 30, FrequencyGHz = 2.29 };
            var core = new DashboardCoreVm(sample.Id, kind == CoreClass.Efficiency);
            var tracker = new CumulativeLoadTracker();
            tracker.Accumulate([sample], 60);
            core.ApplyCumulative(sample, tracker, 1, 1);
            Check(core.FreqText == "2.29 GHz" && core.FreqText == core.LiveFrequencyText, "cumulative " + kind + " card and popup share current GHz");
            Check(core.LoadTipText.Contains("18.0 核·秒") && core.UsageText == "30%", "cumulative workload remains accessible with correct units");
            var changes = new HashSet<string?>();
            core.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
            core.ApplyCumulative(sample with { FrequencyGHz = 1.64 }, tracker, 1, 1);
            Check(core.FreqText == "1.64 GHz" && changes.Contains(nameof(core.FreqText)), "frequency change notifies cumulative " + kind + " card binding");
            foreach (var missing in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0, -1 })
            {
                core.ApplyLive(sample with { FrequencyGHz = missing });
                Check(core.FreqText == "-" && core.LiveFrequencyText == "-", "invalid frequency is unavailable on both displays");
            }
        }
    }

    // Controlled Intel hybrid frames exercise both sensor and counter fallback paths in the real WPF UI.
    private sealed class FrequencyMonitor : ICpuMonitorService
    {
        private readonly CpuInfo _info;
        private readonly CpuCoreGroup[] _groups;
        public CpuCoreSnapshot[] Samples;
        public FrequencyMonitor(string name, int p, int e)
        {
            _info = new() { Name = name + " test topology", Vendor = "Intel", VendorKind = CpuVendor.Intel,
                PhysicalCores = p + e, LogicalProcessors = p * 2 + e };
            _groups = [new(CoreGroupKind.Performance, "P-Core", Enumerable.Range(0, p).Select(i => "P" + i).ToArray(), p, p * 2),
                new(CoreGroupKind.Efficiency, "E-Core", Enumerable.Range(0, e).Select(i => "E" + i).ToArray(), e, e)];
            Samples = _groups.SelectMany(g => g.CoreIds.Select(id => new CpuCoreSnapshot
            {
                Id = id, Class = g.Kind == CoreGroupKind.Performance ? CoreClass.Performance : CoreClass.Efficiency,
                UsagePercent = 30, FrequencyGHz = g.Kind == CoreGroupKind.Performance ? 2.29 : 1.64,
                FrequencySource = p == 1 ? CoreFrequencySource.PerfCounterRatio : CoreFrequencySource.SensorPState,
                EffectiveFrequencyGHz = p == 1 ? double.NaN : 1.1,
            })).ToArray();
        }
        public TimeSpan SampleInterval { get; set; } = TimeSpan.FromSeconds(1);
        public bool SensorsEnabled { get; set; }
        public CpuInfo GetCpuInfo() => _info;
        public IReadOnlyList<CpuCoreGroup> GetCoreGroups() => _groups;
        public CpuSnapshot GetSnapshot() => new() { TotalUsagePercent = 30, AverageFrequencyGHz = Samples.Average(s => s.FrequencyGHz), PackagePowerW = 15 };
        public IReadOnlyList<CpuCoreSnapshot> GetCoreSnapshots() => Samples;
    }

    private static void FrequencyUi(MainWindow owner, string evidence)
    {
        foreach (var (name, p, e) in new[] { ("U300", 1, 4), ("14600KF", 6, 8) })
        {
            BrandTheme.Apply("Intel");
            var monitor = new FrequencyMonitor(name, p, e);
            var vm = new DashboardViewModel(monitor);
            var window = new MainWindow(vm) { Owner = owner };
            window.Show(); Pump();
            var tracker = Field<CumulativeLoadTracker>(vm, "_cumulative");
            tracker.Accumulate(monitor.Samples, 60);
            vm.SetCumulativeLoad(true); Pump();
            foreach (var id in new[] { "P0", "E0" })
            {
                CpuCoreTile Tile() => Visuals<CpuCoreTile>(window).Single(t => ((DashboardCoreVm)t.DataContext).Id == id);
                var tile = Tile();
                var core = (DashboardCoreVm)tile.DataContext;
                Click((Border)tile.Parent, UIElement.MouseLeftButtonUpEvent); Pump();
                var popup = (Popup)window.FindName("FocusCard");
                var detail = (FrameworkElement)window.FindName("FocusCardRoot");
                Check(popup.IsOpen && ReferenceEquals(detail.DataContext, core), name + " opens the matching " + id + " popup");
                foreach (var cumulative in new[] { true, false, true })
                {
                    vm.SetCumulativeLoad(cumulative);
                    var current = monitor.Samples.Single(s => s.Id == id).FrequencyGHz;
                    monitor.Samples = monitor.Samples.Select(s => s with { FrequencyGHz = s.FrequencyGHz + .17 }).ToArray();
                    vm.Tick(); Pump();
                    var cardClock = Visuals<TextBlock>(Tile()).Single(t => BindingOperations.GetBinding(t, TextBlock.TextProperty)?.Path.Path == "FreqText");
                    var popupClock = Visuals<TextBlock>(detail).Single(t => BindingOperations.GetBinding(t, TextBlock.TextProperty)?.Path.Path == "LiveFrequencyText");
                    Check(cardClock.Text == $"{current + .17:0.00} GHz" && cardClock.Text == popupClock.Text,
                        name + " " + id + " current frequency updates on card and open popup, cumulative=" + cumulative);
                }
                Capture(window, Path.Combine(evidence, "frequency-" + name + "-" + id + ".png"));
                Capture(detail, Path.Combine(evidence, "frequency-" + name + "-" + id + "-detail.png"));
                popup.IsOpen = false;
            }
            Check(tracker.BusySeconds("P0") >= 18 && vm.IsCumulativeLoad, name + " frequency refresh preserves cumulative workload");
            window.Close();
        }
    }
}
