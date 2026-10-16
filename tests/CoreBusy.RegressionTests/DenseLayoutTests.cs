using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CoreBusy.App.Controls;
using CoreBusy.App.Services;
using CoreBusy.App.Themes;
using CoreBusy.App.ViewModels.Dashboard;
using CoreBusy.App.Views;
using CoreBusy.Core.Interfaces;
using CoreBusy.Core.Models;

internal static partial class Program
{
    // 仅为布局构造的拓扑，不冒充实际 SKU；采样接口与生产窗口完全相同。
    private sealed class LayoutMonitor : ICpuMonitorService
    {
        private readonly MockCpuMonitorService _source = new();
        private readonly CpuInfo _info;
        private readonly List<CpuCoreGroup> _groups = [];
        private readonly List<CpuCoreSnapshot> _cores = [];
        public LayoutMonitor(int count, bool splitEight = false, bool hybridEight = false)
        {
            var hybrid = count == 24 || hybridEight;
            var groups = splitEight ? new[] { 4, 4 } : count == 32 ? new[] { 8, 8, 8, 8 } : hybrid ? new[] { 8, 16 } : new[] { count };
            foreach (var (size, group) in groups.Select((size, index) => (size, index)))
            {
                var kind = hybrid ? group == 0 ? CoreGroupKind.Performance : CoreGroupKind.Efficiency : CoreGroupKind.Standard;
                var ids = new List<string>();
                for (var i = 0; i < size; i++)
                {
                    var id = hybrid ? (group == 0 ? "P" : "E") + i : "C" + _cores.Count;
                    var threads = kind == CoreGroupKind.Efficiency ? 1 : 2;
                    ids.Add(id);
                    _cores.Add(new CpuCoreSnapshot
                    {
                        Id = id, UsagePercent = 30, FrequencyGHz = 4,
                        Class = kind == CoreGroupKind.Efficiency ? CoreClass.Efficiency : hybrid ? CoreClass.Performance : CoreClass.Standard,
                        Threads = Enumerable.Range(0, threads).Select(t => new CpuThreadSnapshot { Id = id + "·" + t, CoreId = id, Index = t, UsagePercent = 30 }).ToArray(),
                    });
                }
                _groups.Add(new CpuCoreGroup(kind, hybrid ? group == 0 ? "P-Core" : "E-Core" : groups.Length > 1 ? "CCD " + group : "CPU Core", ids, size, size * (kind == CoreGroupKind.Efficiency ? 1 : 2)));
            }
            _info = new CpuInfo { Name = $"布局模拟 · {count} 核", Vendor = hybrid ? "Intel" : "AMD", VendorKind = hybrid ? CpuVendor.Intel : CpuVendor.Amd,
                PhysicalCores = count, LogicalProcessors = _cores.Sum(c => c.Threads.Count), CcdCount = hybrid ? 1 : groups.Length };
        }
        public TimeSpan SampleInterval { get; set; } = TimeSpan.FromSeconds(1);
        public bool SensorsEnabled { get; set; }
        public CpuInfo GetCpuInfo() => _info;
        public CpuSnapshot GetSnapshot() => _source.GetSnapshot();
        public IReadOnlyList<CpuCoreSnapshot> GetCoreSnapshots() => _cores;
        public IReadOnlyList<CpuCoreGroup> GetCoreGroups() => _groups;
    }

    private static void DenseLayoutUi(MainWindow owner, string evidence)
    {
        foreach (var count in new[] { 8, 12, 24, 32 })
        {
            var monitor = new LayoutMonitor(count);
            BrandTheme.Apply(monitor.GetCpuInfo().Vendor);
            var vm = new DashboardViewModel(monitor);
            var window = new MainWindow(vm) { Owner = owner };
            window.Show(); vm.Stop(); Pump();
            var tiles = Visuals<CpuCoreTile>(window).ToArray();
            Check(tiles.Length == count && tiles.Select(t => ((DashboardCoreVm)t.DataContext).Id).Distinct().Count() == count, count + " physical cores all have distinct cards");
            if (count == 24) Check(vm.CoreGroups.Count == 2, "24-core hybrid retains separate P/E banks");
            if (count == 32) Check(vm.CoreGroups.Count == 4, "32-core multi-CCD retains four banks");
            foreach (var (width, height, label) in new[] { (720, 500, "default"), (640, 460, "minimum"), (1280, 780, "wide") })
            {
                window.Width = width; window.Height = height; Pump();
                var scroll = (ScrollViewer)window.FindName("CoreScroll");
                scroll.ScrollToHome(); Pump();
                if (count == 8)
                {
                    Check(scroll.ScrollableHeight < .1, $"eight-core board fits one screen at {label} ({window.ActualWidth}x{window.ActualHeight}, overflow={scroll.ScrollableHeight})");
                    Check(scroll.ComputedVerticalScrollBarVisibility != Visibility.Visible, "eight-core board has no vertical scrollbar");
                    var lastCore = tiles[^1].TransformToAncestor(scroll).TransformBounds(new Rect(tiles[^1].RenderSize));
                    Check(lastCore.Bottom <= scroll.ViewportHeight + .1, "eighth core and all readouts fit without scrolling");
                }
                var panels = Visuals<CoreTilePanel>(window).ToArray();
                Check(panels.Select(p => p.Columns).Distinct().Count() == 1, "all banks share columns at " + count + "/" + label);
                Check(tiles.Min(t => t.ActualWidth) >= (count == 8 ? 130 : 124), "dense cards retain readable minimum width");
                if (count == 12 && width == 1280) Check(panels.All(p => p.Columns == 6), "wide twelve-core bank uses six columns");
                foreach (var tile in tiles) CheckPortraitLayout(tile);
                CheckSocketGaps(window);
                Capture(window, Path.Combine(evidence, $"cores-{count}-{label}.png"));
                if (count == 24 && label == "minimum")
                {
                    var scrollbar = Visuals<ScrollBar>(scroll).Single(b => b.Orientation == Orientation.Vertical);
                    Check(scrollbar.ActualWidth == 8, $"multi-core board uses a quiet narrow scrollbar (width={scrollbar.ActualWidth})");
                    var thumb = Visuals<Thumb>(scrollbar).Single();
                    thumb.RaiseEvent(new DragDeltaEventArgs(0, 24) { RoutedEvent = Thumb.DragDeltaEvent });
                    Pump();
                    Check(scroll.VerticalOffset > 0, "compact scrollbar thumb still scrolls the real board");
                }
                scroll.ScrollToEnd(); Pump();
                var last = tiles[^1].TransformToAncestor(window).TransformBounds(new Rect(tiles[^1].RenderSize));
                Check(last.Top >= 38 && last.Bottom <= window.ActualHeight - 20, "last dense card is fully accessible");
            }
            Click((Border)tiles[^1].Parent, UIElement.MouseLeftButtonUpEvent); Pump(400);
            var popup = (Popup)window.FindName("FocusCard");
            Check(popup.IsOpen, "last dense core opens detail");
            CheckPopupPlacement((FrameworkElement)window.FindName("FocusCardRoot"));
            popup.IsOpen = false;
            Capture(window, Path.Combine(evidence, $"cores-{count}-scrolled.png"));
            window.Close();
        }
        foreach (var hybrid in new[] { false, true })
        {
            var monitor = new LayoutMonitor(8, splitEight: true, hybridEight: hybrid);
            var vm = new DashboardViewModel(monitor);
            var window = new MainWindow(vm) { Owner = owner };
            window.Show(); vm.Stop(); Pump();
            foreach (var minimum in new[] { false, true })
            {
                if (minimum) { window.Width = window.MinWidth; window.Height = window.MinHeight; }
                Pump();
                var scroll = (ScrollViewer)window.FindName("CoreScroll");
                Check(scroll.ScrollableHeight < .1 && scroll.ComputedVerticalScrollBarVisibility != Visibility.Visible,
                    $"eight-core {(hybrid ? "Intel P/E" : "AMD CCD")} banks fit one screen at {window.ActualWidth}x{window.ActualHeight} (overflow={scroll.ScrollableHeight})");
                CheckSocketGaps(window);
                Capture(window, Path.Combine(evidence, $"cores-8-{(hybrid ? "hybrid" : "ccd")}-{(minimum ? "minimum" : "default")}.png"));
            }
            window.Close();
        }
        // 当前桌面不足以打开八列宽窗，直接对实际布局控件执行相同的测量/排列流程。
        var panel = new CoreTilePanel { MaxColumns = 8 };
        for (var i = 0; i < 32; i++) panel.Children.Add(new Border { Height = 240 });
        panel.Measure(new Size(1600, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, 1600, panel.DesiredSize.Height));
        Check(panel.Columns == 8 && panel.Children.Count == 32, "wide board lays out thirty-two cores in eight columns");
    }
}
