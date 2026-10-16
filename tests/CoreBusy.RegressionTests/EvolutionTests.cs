using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CoreBusy.App.Controls;
using CoreBusy.App.Themes;
using CoreBusy.App.ViewModels.Dashboard;
using CoreBusy.App.Views;
using CoreBusy.Core.Progression;
using CoreBusy.Core.Models;
using CoreBusy.App.Services;

internal static partial class Program
{
    private static void EvolutionRules()
    {
        foreach (var (level, stage) in new[] { (1, 0), (9, 0), (10, 1), (39, 1), (40, 2), (74, 2), (75, 3), (98, 3), (99, 4) })
        {
            var vm = new CoreLevelVm(CpuVendor.Amd);
            vm.Refresh(CoreLevelCurve.CumulativeExp(level), false);
            Check(vm.Level == level && vm.StageIndex == stage && vm.StageText == $"形态 {stage + 1}/5", $"stage boundary Lv {level}");
            Check(vm.EvolutionVersion == 0, $"loading Lv {level} does not celebrate historical evolution");
            Check(vm.HasNextEvolution == (stage < 4) && vm.EvolutionProgress is >= 0 and <= 1, $"valid preview at Lv {level}");
        }
        var current = new CoreLevelVm(CpuVendor.Amd);
        var notifications = new HashSet<string?>();
        current.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        current.Refresh(CoreLevelCurve.CumulativeExp(9), false);
        Check(current.NextEvolutionLevel == 10 && current.NextStageIndex == 1 && current.NextEvolutionText.Contains("Lv 10") && current.NextEvolutionText.Contains("新外观"), "first evolution preview matches next shell");
        current.Refresh(CoreLevelCurve.CumulativeExp(10), false);
        Check(current.EvolutionVersion == 1 && current.NextEvolutionLevel == 40, "crossing a boundary celebrates and advances preview");
        Check(new[] { "StageIndex", "StageText", "HasNextEvolution", "NextStageIndex", "NextEvolutionText", "EvolutionProgress", "EvolutionVersion" }.All(notifications.Contains), "evolution bindings receive change notifications");
        Equal(0, current.EvolutionProgress, "new stage progress resets at its threshold");
        current.Refresh((CoreLevelCurve.CumulativeExp(10) + CoreLevelCurve.CumulativeExp(40)) / 2, false);
        Equal(0.5, current.EvolutionProgress, "evolution progress uses EXP rather than level percentage");
        Check(current.EvolutionVersion == 1, "progress within a stage never repeats evolution");
        current.Refresh(CoreLevelCurve.CumulativeExp(99), false);
        Check(current.EvolutionVersion == 2 && !current.HasNextEvolution && current.NextEvolutionText == "已达最终形态", "multi-stage jump celebrates once and max level ends preview");
        current.Refresh(CoreLevelCurve.CumulativeExp(99) + 10, false);
        Check(current.EvolutionVersion == 2 && current.EvolutionProgress == 1, "max EXP accrual is static");
        current.Refresh(0, false);
        Check(current.EvolutionVersion == 2, "lower historical snapshot never celebrates");

        var core = new DashboardCoreVm("test", false, vendor: CpuVendor.Amd);
        core.Level.Refresh(CoreLevelCurve.CumulativeExp(99), false);
        var (_, samples) = Session();
        core.ApplyLive(samples[0]);
        Check(core.IsHighLoad && ReferenceEquals(core.TileBorderBrush, UiTheme.StageBorderFor(99)), "load warning preserves growth identity");
        BrandRules();
    }

    private static void BrandRules()
    {
        foreach (var vendor in new[] { CpuVendor.Intel, CpuVendor.Amd })
        {
            var models = new HashSet<string>();
            foreach (var (level, stage) in new[] { (1, 0), (9, 0), (10, 1), (39, 1), (40, 2), (74, 2), (75, 3), (98, 3), (99, 4) })
            {
                var vm = new CoreLevelVm(vendor);
                vm.Refresh(CoreLevelCurve.CumulativeExp(level), false);
                var form = CpuEvolutionCatalog.For(vendor, stage);
                Check(!vm.AppearanceText.Contains(form.Model) && !vm.NextEvolutionText.Contains(form.Name), "growth labels do not impersonate CPU model names");
                Check(vm.Appearance == form && vm.BrandText == (vendor == CpuVendor.Intel ? "INTEL" : "AMD") && form.Year > 2000, $"{vendor} Lv {level} selects real post-2000 form");
                Check(vm.AppearanceText.Contains(vm.BrandText) && !vm.AppearanceText.Contains(form.Name) && !vm.NextEvolutionTip.Contains(form.Model), "visible growth labels use only vendor and form, keeping reference SKUs internal");
                Check(vm.HasNextEvolution == (stage < 4), "both brands use existing evolution thresholds");
                models.Add(form.Model);
            }
            Check(models.Count == 5, vendor + " has five distinct models");
        }
        var unknown = new CoreLevelVm();
        unknown.Refresh(CoreLevelCurve.CumulativeExp(60), false);
        Check(unknown.Level == 60 && unknown.StageText == "CPU" && !unknown.HasNextEvolution, "unidentified manufacturer keeps EXP without guessing a brand");
        Check(unknown.EvolutionVersion == 0, "unknown hardware does not celebrate imaginary form changes");
        foreach (var profile in new[] { "amd-single", "amd-dual", "intel-standard", "intel-hybrid" })
        {
            var monitor = new MockCpuMonitorService(CoreLayoutMode.Spec, profile);
            var vm = new DashboardViewModel(monitor);
            Check(Field<Dictionary<string, DashboardCoreVm>>(vm, "_allCoresById").Values.All(c => c.Level.Vendor == monitor.GetCpuInfo().VendorKind), profile + " routes every physical core to hardware manufacturer");
            vm.Stop();
        }
    }

    private static void CheckPortraitLayout(CpuCoreTile tile)
    {
        var core = (DashboardCoreVm)tile.DataContext;
        var exp = (SegmentBar)tile.FindName("ExpBar");
        Equal(core.Level.ProgressPercent, exp.Value, "card EXP bar uses percentage units");
        var portrait = (CoreEvolutionVisual)tile.FindName("Portrait");
        var bounds = portrait.PortraitBounds;
        var image = CoreEvolutionVisual.PortraitFor(portrait.Vendor, portrait.Stage);
        Check(image.IsFrozen && ReferenceEquals(image, CoreEvolutionVisual.PortraitFor(portrait.Vendor, portrait.Stage)), "portrait geometry is frozen and shared");
        Check(!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= 0 && bounds.Right <= portrait.ActualWidth && bounds.Bottom <= portrait.ActualHeight, "portrait fits its reserved area");
        Check(Math.Abs(bounds.Width / bounds.Height - image.Width / image.Height) < 1e-8, "portrait preserves package aspect ratio");
        Check(Math.Abs(bounds.Left + bounds.Width / 2 - portrait.ActualWidth / 2) < .01 && Math.Abs(bounds.Top + bounds.Height / 2 - portrait.ActualHeight / 2) < .01, "portrait is centered horizontally and vertically");
        var texts = Visuals<TextBlock>(tile).Where(t => t.IsVisible).ToArray();
        foreach (var text in texts)
        {
            var rect = text.TransformToAncestor(tile).TransformBounds(new Rect(text.RenderSize));
            Check(rect.Left >= -1 && rect.Right <= tile.ActualWidth + 1 && rect.Bottom <= tile.ActualHeight + 1, "monitoring and model text stay inside the card");
        }
        Check(Math.Max(bounds.Width, bounds.Height) >= tile.ActualWidth * (tile.IsCondensed ? .40 : .46), "CPU portrait is prominent relative to card width");
        Check(portrait.ActualHeight >= (tile.IsCondensed ? 64 : 84) && tile.ActualHeight <= 210, "compact card gives CPU the main area without increasing card height");
        Check(Math.Max(bounds.Width, bounds.Height) >= Math.Min(tile.IsCondensed ? 64 : 84, portrait.ActualWidth) * .94, "CPU fills the available portrait area");
        Check((string)exp.ToolTip == core.Level.NextEvolutionTip, "next evolution remains accessible from the EXP bar");
        Check(!texts.Any(t => t.Text.Contains("形态")), "core tile does not display redundant form labels");
        var level = texts.Single(t => t.Text == core.Level.LevelText);
        var levelBounds = level.TransformToAncestor(tile).TransformBounds(new Rect(level.RenderSize));
        var expBounds = exp.TransformToAncestor(tile).TransformBounds(new Rect(exp.RenderSize));
        Check(levelBounds.Bottom <= expBounds.Top, "level stays above the EXP bar");
    }

    private static void CheckSocketGaps(FrameworkElement window)
    {
        foreach (var panel in Visuals<CoreTilePanel>(window))
        {
            var bounds = Visuals<CpuCoreTile>(panel).Select(t => t.TransformToAncestor(panel).TransformBounds(new Rect(t.RenderSize))).ToArray();
            foreach (var first in bounds)
                foreach (var second in bounds)
                {
                    if (Math.Abs(first.Top - second.Top) < 1 && second.Left >= first.Right)
                        Check(second.Left - first.Right >= 9.9, $"horizontal socket channel exposes at least 10 DIPs of PCB (actual={second.Left-first.Right:F2})");
                    if (Math.Abs(first.Left - second.Left) < 1 && second.Top >= first.Bottom)
                        Check(second.Top - first.Bottom >= 7.9, "vertical socket channel exposes at least 8 DIPs of PCB");
                }
        }
    }

    private static void CheckPopupPlacement(FrameworkElement detail)
    {
        var origin = detail.PointToScreen(new Point());
        var dpi = VisualTreeHelper.GetDpi(detail);
        var bottomRight = detail.PointToScreen(new Point(detail.ActualWidth, detail.ActualHeight));
        var source = (System.Windows.Interop.HwndSource)PresentationSource.FromVisual(detail);
        var info = new PopupMonitorInfo { Size = Marshal.SizeOf<PopupMonitorInfo>() };
        Check(PopupGetMonitorInfo(PopupMonitorFromWindow(source.Handle, 2), ref info), "read actual popup monitor work area");
        var area = info.Work;
        Check(PopupGetWindowRect(source.Handle, out var native) && native.Left >= area.Left - 1
            && native.Top >= area.Top - 1 && native.Right <= area.Right + 1 && native.Bottom <= area.Bottom + 1,
             $"native popup window also fits its monitor work area (rect={native.Left},{native.Top}..{native.Right},{native.Bottom}, root={origin}, size={detail.ActualWidth}x{detail.ActualHeight})");
        Check(origin.X >= area.Left - 1 && origin.Y >= area.Top - 1
            && bottomRight.X <= area.Right + 1
            && bottomRight.Y <= area.Bottom + 1,
            $"native popup placement stays inside its monitor work area (origin={origin}, size={detail.ActualWidth}×{detail.ActualHeight}, bottomRight={bottomRight}, dpi={dpi.DpiScaleX}×{dpi.DpiScaleY}, area={area.Left},{area.Top}..{area.Right},{area.Bottom})");
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    private static extern bool PopupGetWindowRect(nint window, out PopupRect rect);
    [DllImport("user32.dll", EntryPoint = "MonitorFromWindow")]
    private static extern nint PopupMonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    private static extern bool PopupGetMonitorInfo(nint monitor, ref PopupMonitorInfo info);
    [StructLayout(LayoutKind.Sequential)]
    private struct PopupRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PopupMonitorInfo { public int Size; public PopupRect Monitor, Work; public uint Flags; }

    private static void BrandUi(MainWindow owner, string evidence)
    {
        foreach (var vendor in new[] { CpuVendor.Intel, CpuVendor.Amd })
            foreach (var stage in Enumerable.Range(0, 5))
                Check(CoreEvolutionVisual.PortraitFor(vendor, stage).IsFrozen, "all ten portraits render as retained vector drawings");
        Check(CoreEvolutionVisual.PortraitFor(CpuVendor.Unknown, 3) == CoreEvolutionVisual.PortraitFor(CpuVendor.Unknown, 0), "unknown hardware uses one neutral image");
        foreach (var profile in new[] { "intel-standard", "intel-hybrid", "amd-dual" })
        {
            var monitor = new MockCpuMonitorService(CoreLayoutMode.Spec, profile);
            // 故意使用相反的配色，验证皮肤主题不会把品牌成长路线一起切换。
            BrandTheme.Apply(monitor.GetCpuInfo().Vendor, monitor.GetCpuInfo().VendorKind == CpuVendor.Intel ? ThemeMode.Amd : ThemeMode.Intel);
            var vm = new DashboardViewModel(monitor);
            var window = new MainWindow(vm) { Owner = owner };
            window.Show();
            vm.Stop();
            var levels = new[] { 1, 10, 40, 75, 99 };
            var tiles = Visuals<CpuCoreTile>(window).ToArray();
            for (var i = 0; i < tiles.Length; i++)
            {
                var level = ((DashboardCoreVm)tiles[i].DataContext).Level;
                level.Refresh(CoreLevelCurve.CumulativeExp(levels[i % 5]), false);
                level.UpdateEta(.01);
            }
            Pump(800);
            Check(tiles.All(t => ((CoreEvolutionVisual)t.FindName("Portrait")).Vendor == monitor.GetCpuInfo().VendorKind), "opposite color theme cannot change " + profile + " portraits");
            Capture(window, Path.Combine(evidence, profile + "-default.png"));
            foreach (var tile in tiles) CheckPortraitLayout(tile);
            CheckSocketGaps(window);
            if (profile == "intel-hybrid")
                Check(tiles.Any(t => t.IsCompact) && tiles.Any(t => !t.IsCompact), "Intel P/E cores use one brand with two card densities");
            window.Width = window.MinWidth; window.Height = window.MinHeight;
            Pump();
            Check(Visuals<CoreTilePanel>(window).All(p => p.Columns <= 4), "narrow multi-core layout limits columns");
            foreach (var tile in tiles) CheckPortraitLayout(tile);
            CheckSocketGaps(window);
            Capture(window, Path.Combine(evidence, profile + "-minimum.png"));
            var scroll = (ScrollViewer)window.FindName("CoreScroll");
            scroll.ScrollToEnd(); Pump();
            var lastBounds = tiles[^1].TransformToAncestor(window).TransformBounds(new Rect(tiles[^1].RenderSize));
            Check(lastBounds.Bottom <= window.ActualHeight - 20, "last core remains reachable by scrolling");
            Capture(window, Path.Combine(evidence, profile + "-scrolled.png"));
            window.Width = 1280; window.Height = 780; Pump();
            scroll.ScrollToHome(); Pump();
            Check(tiles.Max(t => t.ActualWidth) <= 224, "wide windows preserve maximum card width");
            foreach (var tile in tiles) CheckPortraitLayout(tile);
            CheckSocketGaps(window);
            Capture(window, Path.Combine(evidence, profile + "-wide.png"));
            var first = (DashboardCoreVm)tiles[0].DataContext;
            Click((Border)tiles[0].Parent, UIElement.MouseLeftButtonUpEvent); Pump(400);
            var popup = (Popup)window.FindName("FocusCard");
            var detail = (FrameworkElement)window.FindName("FocusCardRoot");
            Check(popup.IsOpen && Visuals<CoreEvolutionVisual>(detail).All(v => v.Vendor == first.Level.Vendor), "detail and preview follow actual core brand");
            var previewTrack = (MiniBar)window.FindName("EvolutionTrack");
            Check(previewTrack.Maximum == 1 && previewTrack.Value == first.Level.EvolutionProgress, "evolution preview uses ratio units");
            Check(detail.ActualHeight <= SystemParameters.WorkArea.Height && detail.ActualWidth == 288, "redesigned detail fits screen work area");
            CheckPopupPlacement(detail);
            Capture(detail, Path.Combine(evidence, profile + "-detail.png"));
            popup.IsOpen = false;
            scroll.ScrollToEnd(); Pump();
            Click((Border)tiles[^1].Parent, UIElement.MouseLeftButtonUpEvent); Pump(400);
            Check(popup.IsOpen, "last core opens detail after scrolling");
            CheckPopupPlacement(detail);
            Capture(detail, Path.Combine(evidence, profile + "-detail-edge.png"));
            popup.IsOpen = false;
            scroll.ScrollToHome(); Pump();
            first.Level.Refresh(CoreLevelCurve.CumulativeExp(9), false); Pump();
            first.Level.Refresh(CoreLevelCurve.CumulativeExp(10), false); Pump(100);
            Check(((CoreEvolutionVisual)tiles[0].FindName("Portrait")).HasAnimatedProperties == SystemParameters.ClientAreaAnimation, "both brand routes animate live evolution");
            window.Close();
        }
    }

    private static void Pump(int milliseconds = 50)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Click(UIElement element, RoutedEvent routedEvent)
        => element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = routedEvent });

    private static void EvolutionUi(MainWindow window, DashboardViewModel vm, string evidence)
    {
        var telemetry = new DashboardCoreVm("C0", false);
        var live = new CpuCoreSnapshot { Id = "C0", UsagePercent = 73, FrequencyGHz = 4.5, EffectiveFrequencyGHz = 2.25, CoreVoltageV = 1.125, FrequencySource = CoreFrequencySource.SensorPState };
        var tracker = new CumulativeLoadTracker();
        tracker.Accumulate([live with { UsagePercent = 10 }], 60);
        telemetry.ApplyCumulative(live, tracker, 1, 8);
        Check(telemetry.UsageText == "10%" && telemetry.LiveUsageText == "73%", "detail stays live when the board shows cumulative averages");
        Check(telemetry.LiveFrequencyText == "4.50 GHz" && telemetry.EffectiveFrequencyText == "2.25 GHz" && telemetry.VoltageText == "1.125 V", "detail keeps distinct clock, effective clock and VID readings");
        telemetry.ApplyLive(live with { FrequencyGHz = double.PositiveInfinity, EffectiveFrequencyGHz = double.NaN, CoreVoltageV = 0 });
        Check(telemetry.LiveFrequencyText == "-" && telemetry.EffectiveFrequencyText == "-" && telemetry.VoltageText == "-", "missing telemetry is never rendered as fabricated measurements");
        // 所有等级注入都在隔离的测试数据目录内；停止采样，避免下一帧覆盖确定的边界。
        vm.Stop();
        var levels = new[] { 1, 10, 40, 75, 99 };
        var mainTiles = Visuals<CpuCoreTile>(window).ToArray();
        Check(mainTiles.Length >= 5, "actual dashboard contains all five evolution samples");
        for (var i = 0; i < mainTiles.Length; i++)
        {
            var level = ((DashboardCoreVm)mainTiles[i].DataContext).Level;
            level.Refresh(CoreLevelCurve.CumulativeExp(levels[i % 5]), false);
            level.UpdateEta(0);
        }
        Pump(800);
        Check(mainTiles.Max(t => t.ActualWidth) - mainTiles.Min(t => t.ActualWidth) < 1, "all evolution stages fill equal dashboard columns");
        Check(((ScrollViewer)window.FindName("CoreScroll")).ScrollableHeight < 1, $"default eight-core dashboard fits both complete rows ({window.ActualWidth}×{window.ActualHeight}, scroll={((ScrollViewer)window.FindName("CoreScroll")).ScrollableHeight})");
        Check(window.ActualWidth == 720 && window.ActualHeight <= 500, "default dashboard is compact at 720 by 500 DIPs");
        foreach (var tile in mainTiles) CheckPortraitLayout(tile);
        CheckSocketGaps(window);
        Capture(window, Path.Combine(evidence, "evolution-main.png"));
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        Pump();
        Capture(window, Path.Combine(evidence, "evolution-main-minimum.png"));

        var gallery = new StackPanel { Margin = new Thickness(16) };
        var testCores = new List<DashboardCoreVm>();
        foreach (var (vendor, compact) in new[] { (CpuVendor.Intel, false), (CpuVendor.Amd, true) })
        {
            gallery.Children.Add(new TextBlock { Text = compact ? "紧凑卡 · AMD 路线" : "标准卡 · Intel 路线", Foreground = Brushes.White, Margin = new Thickness(0, 8, 0, 8) });
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var level in levels)
            {
                var core = new DashboardCoreVm("C" + testCores.Count, compact, vendor: vendor);
                core.Level.Refresh(CoreLevelCurve.CumulativeExp(level), false);
                core.Role.Refresh(CoreRole.Burster, null);
                testCores.Add(core);
                row.Children.Add(new CpuCoreTile { Width = 172, IsCompact = compact, DataContext = core, Margin = new Thickness(3) });
            }
            gallery.Children.Add(row);
        }
        var illustrations = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        for (var stage = 0; stage < 5; stage++)
        {
            var pair = new StackPanel { Width = 214 };
            pair.Children.Add(new CoreEvolutionVisual { Vendor = CpuVendor.Amd, Stage = stage, Width = 86, Height = 86 });
            pair.Children.Add(new CoreEvolutionVisual { Vendor = CpuVendor.Amd, Stage = stage, IsSilhouette = true, Width = 64, Height = 64 });
            illustrations.Children.Add(pair);
        }
        gallery.Children.Add(illustrations);
        var host = new Window { Content = gallery, SizeToContent = SizeToContent.WidthAndHeight, Background = new SolidColorBrush(Color.FromRgb(0x0C, 0x30, 0x28)), Owner = window, Title = "Evolution regression gallery" };
        host.Show();
        Pump();
        Check(Visuals<CoreEvolutionVisual>(host).All(v => !v.HasAnimatedProperties), "initial shells and silhouettes have no animation clocks");
        Capture(host, Path.Combine(evidence, "evolution-five-stages.png"));
        foreach (var tile in Visuals<CpuCoreTile>(host))
        {
            Check(tile.ActualWidth == 172 && tile.ActualHeight > 0, "normal and compact shells fit fixed tile width");
            Check((string)((SegmentBar)tile.FindName("ExpBar")).ToolTip == ((DashboardCoreVm)tile.DataContext).Level.NextEvolutionTip, "compact tile retains next evolution guidance in the EXP tooltip");
            var visibleText = Visuals<TextBlock>(tile).Where(t => t.IsVisible).ToArray();
            var core = (DashboardCoreVm)tile.DataContext;
            var levelText = visibleText.Single(t => t.Text == core.Level.LevelText);
            var roleText = visibleText.Single(t => t.Text == core.Role.Label);
            var levelBounds = levelText.TransformToAncestor(tile).TransformBounds(new Rect(levelText.RenderSize));
            var roleBounds = roleText.TransformToAncestor(tile).TransformBounds(new Rect(roleText.RenderSize));
            Check(!levelBounds.IntersectsWith(roleBounds), "role and level labels do not overlap");
        }
        var changing = Visuals<CoreEvolutionVisual>(host).First(v => !v.IsSilhouette);
        testCores[0].Level.Refresh(CoreLevelCurve.CumulativeExp(9), false);
        Pump();
        testCores[0].Level.Refresh(CoreLevelCurve.CumulativeExp(10), false);
        Pump(100);
        Check(changing.HasAnimatedProperties == SystemParameters.ClientAreaAnimation, "live stage crossing follows system animation preference");
        Capture(host, Path.Combine(evidence, "evolution-assembly.png"));
        Pump(800);
        Check(!changing.HasAnimatedProperties, "assembly animation releases its clock after completion");
        testCores[0].Level.Refresh(CoreLevelCurve.CumulativeExp(11), false);
        Pump();
        Check(!changing.HasAnimatedProperties, "ordinary level gain does not animate the shell");
        host.Close();

        var tileBorder = (Border)mainTiles[0].Parent;
        Click(tileBorder, UIElement.MouseLeftButtonUpEvent);
        Pump(400);
        var popup = (Popup)window.FindName("FocusCard");
        var detail = (FrameworkElement)window.FindName("FocusCardRoot");
        Check(popup.IsOpen && Visuals<CoreEvolutionVisual>(detail).Count() == 2, "actual core click opens current shell and next silhouette");
        Check(Visuals<CoreEvolutionVisual>(detail).All(v => !v.HasAnimatedProperties), "opening details does not replay evolution");
        Pump(400);
        var expFill = (Border)window.FindName("ExpFill");
        var expTrack = (FrameworkElement)window.FindName("ExpTrack");
        var tinyTarget = expFill.ActualWidth + .05;
        Call(window, "AnimateExpFill", tinyTarget / expTrack.ActualWidth);
        Pump(50);
        Check(!expFill.HasAnimatedProperties, "subpixel EXP increments never start continuous popup layout animation");
        Equal(tinyTarget, expFill.Width, "popup still synchronizes exact EXP width without the tween");
        Capture(detail, Path.Combine(evidence, "evolution-detail.png"));
        Check(!Visuals<TextBlock>(detail).Any(t => t.Text.Contains("/5")), "detail removes redundant numbered forms too");
        Check(!Visuals<TextBlock>(detail).Any(t => t.Text.Contains("戳") || t.Text.Contains("点我")), "detail removes poke instructions and response text");
        foreach (var label in new[] { "实时负载", "当前频率", "有效频率", "核心 VID", "升级进度" })
            Check(Visuals<TextBlock>(detail).Any(t => t.IsVisible && t.Text == label), "compact detail displays important metric: " + label);
        Check(detail.ActualWidth == 288 && detail.ActualHeight <= 410, "detail keeps a compact readable footprint");
        popup.IsOpen = false;
        Click((Border)mainTiles[4].Parent, UIElement.MouseLeftButtonUpEvent);
        Pump(400);
        Check(popup.IsOpen && Visuals<CoreEvolutionVisual>(detail).Single(v => v.IsSilhouette).IsVisible == false, "max-level detail hides next silhouette");
        Capture(detail, Path.Combine(evidence, "evolution-detail-max.png"));
        popup.IsOpen = false;
        Click(tileBorder, UIElement.MouseLeftButtonUpEvent);
        Pump(400);
        Check(!((CoreEvolutionVisual)window.FindName("CurrentCorePortrait")).HasAnimatedProperties, "reopening core details does not add decorative animations");
        popup.IsOpen = false;
        BrandUi(window, evidence);
        DenseLayoutUi(window, evidence);
    }
}
