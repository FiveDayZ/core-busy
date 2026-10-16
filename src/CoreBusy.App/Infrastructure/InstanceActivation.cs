namespace CoreBusy.App.Infrastructure;

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using CoreBusy.App.Services;

/// <summary>按数据目录定位窗口；由接收实例的 WPF Dispatcher 恢复显示与采样状态。</summary>
internal static class InstanceActivation
{
    private static readonly uint Message = RegisterWindowMessage("CORE-BUSY.Activate.v1");
    private static string Key => JsonFile.MutexName("Window", EnergyHistoryStore.DataDirectory);

    internal static void Attach(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var key = Key;
        if (!SetProp(handle, key, new IntPtr(1))) throw new InvalidOperationException("无法注册实例窗口");
        ChangeWindowMessageFilterEx(handle, Message, 1, IntPtr.Zero); // 只允许无参数的“显示窗口”消息跨完整性级别。
        var source = HwndSource.FromHwnd(handle);
        IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if ((uint)message == Message)
            {
                handled = true;
                window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    window.Show();
                    if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                    window.Activate();
                }));
            }
            return IntPtr.Zero;
        }
        source.AddHook(Hook);
        window.Closed += (_, _) => { source.RemoveHook(Hook); RemoveProp(handle, key); };
    }

    internal static bool Request()
    {
        var key = Key;
        var sent = false;
        EnumWindows((handle, _) =>
        {
            if (GetProp(handle, key) == IntPtr.Zero) return true;
            sent = PostMessage(handle, Message, IntPtr.Zero, IntPtr.Zero);
            return !sent;
        }, IntPtr.Zero);
        return sent;
    }

    private delegate bool EnumWindowProc(IntPtr handle, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetProp(IntPtr handle, string name, IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetProp(IntPtr handle, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr RemoveProp(IntPtr handle, string name);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool ChangeWindowMessageFilterEx(IntPtr handle, uint message, uint action, IntPtr filter);
}
