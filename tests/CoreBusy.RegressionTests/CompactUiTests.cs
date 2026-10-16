using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CoreBusy.App.Services;
using CoreBusy.App.Views;
using CoreBusy.Core.Models;

internal static partial class Program
{
    private static void CompactDialogsUi(Window owner, string evidence)
    {
        var settings = new SettingsWindow(null, null, "布局模拟") { Owner = owner };
        settings.Show(); Pump();
        try
        {
            Check(settings.ActualWidth == 600 && settings.ActualHeight == 560, "settings use compact 600 by 560 DIPs");
            foreach (var (nav, pane, label) in new[] { ("NavGeneral", "PaneGeneral", "general"), ("NavAppearance", "PaneAppearance", "appearance"), ("NavOptimize", "PaneOptimize", "optimization") })
            {
                ((RadioButton)settings.FindName(nav)).IsChecked = true; Pump();
                var scroll = (ScrollViewer)settings.FindName(pane);
                scroll.ScrollToHome(); Pump();
                Check(scroll.ExtentWidth <= scroll.ViewportWidth + .1, label + " settings fit without horizontal scrolling");
                foreach (var text in Visuals<TextBlock>(scroll).Where(t => t.IsVisible))
                {
                    var bounds = text.TransformToAncestor(scroll).TransformBounds(new Rect(text.RenderSize));
                    Check(bounds.Left >= -1 && bounds.Right <= scroll.ActualWidth + 1, label + " setting labels stay inside the compact pane: " + text.Text);
                }
                Capture(settings, Path.Combine(evidence, "compact-settings-" + label + ".png"));
                scroll.ScrollToEnd(); Pump();
                Check(scroll.VerticalOffset + scroll.ViewportHeight >= scroll.ExtentHeight - 1, label + " last settings remain accessible by scrolling");
                var ok = Visuals<Button>(settings).Single(b => Equals(b.Content, "确定"));
                var rect = ok.TransformToAncestor(settings).TransformBounds(new Rect(ok.RenderSize));
                Check(rect.Bottom <= settings.ActualHeight && rect.Top > 0 && ok.ActualWidth >= 60, "settings confirm remains visible with a practical click target");
            }
            var combo = (ComboBox)settings.FindName("GamePriorityCombo");
            combo.IsDropDownOpen = true; Pump();
            Check(combo.IsDropDownOpen, "compact settings dropdown still opens");
            combo.SelectedItem = "低于标准"; combo.IsDropDownOpen = false;
            Check(Equals(combo.SelectedItem, "低于标准"), "compact dropdown selection works");
        }
        finally { settings.Close(); }

        var topology = Enumerable.Range(0, 16).Select(i => new LogicalProcessorTopology(i, i / 2, CoreClass.Standard, "C" + i / 2, true)).ToArray();
        var picker = new CorePickerWindow(topology, 0xFFFF) { Owner = owner };
        Exception? error = null;
        picker.Loaded += (_, _) => picker.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            try
            {
                Check(picker.ActualWidth == 480, "core picker uses compact width");
                Check(Visuals<CheckBox>(picker).All(c => Equals(c.Foreground, picker.FindResource("CoreBusyTextPrimaryBrush"))),
                    "core-picker thread labels retain readable foreground on dark cards");
                Visuals<Button>(picker).Single(b => Equals(b.Content, "全不选")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(!((Button)picker.FindName("OkButton")).IsEnabled, "compact picker still rejects empty selection");
                ((Button)picker.FindName("PhysicalButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Capture(picker, Path.Combine(evidence, "compact-core-picker.png"));
                ((Button)picker.FindName("OkButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception ex) { error = ex; picker.Close(); }
        }));
        var accepted = picker.ShowDialog();
        if (error is not null) throw error;
        Check(accepted == true && picker.SelectedMask == 0x5555, "compact core picker confirms physical-core selection");

        var progress = new UpdateProgressDialog(owner);
        var cancelled = false; progress.CancelRequested += (_, _) => cancelled = true;
        progress.Report(new UpdateDownloadProgress(37, "55.5 MB / 150 MB"));
        progress.Show(); Pump();
        try
        {
            Check(progress.ActualWidth == 360 && progress.ActualHeight == 146, "update progress uses compact footprint");
            Check(((ProgressBar)progress.FindName("Bar")).Value == 37, "compact update progress preserves real progress mapping");
            Capture(progress, Path.Combine(evidence, "compact-update-progress.png"));
            Visuals<Button>(progress).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(cancelled, "compact update close still requests cancellation");
        }
        finally { progress.Close(); }
    }
}
