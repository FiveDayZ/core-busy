namespace CoreBusy.Windows.Monitoring;

using System.Diagnostics;
using System.Runtime.InteropServices;
using CoreBusy.Core.Interfaces;

/// <summary>
/// 前台游戏检测：覆盖所在显示器 ≥98% 面积的全屏应用，或
/// 无边框样式（无 WS_CAPTION/WS_THICKFRAME，即 WS_POPUP 类）且覆盖 ≥90% 面积的窗口化全屏应用（Phase 5）。
/// 排除桌面外壳进程、子窗口与工具窗口；DWM cloaked 窗口（如挂起的 UWP）不视为前台。
/// 用于游戏模式识别（方案 4.1 节）。
/// </summary>
public sealed class FullscreenGameDetector : IGameDetector
{
    /// <summary>桌面外壳与系统组件（全屏时不应误判为游戏）。</summary>
    private static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer",
        "applicationframehost",
        "textinputhost",
        "searchhost",
        "shellexperiencehost",
        "startmenuexperiencehost",
        "systemsettings",
        "msedgewebview2",
        "taskmgr",
        "workbuddy",
        "codebuddy",
        "corebusy.app",
    };

    private static string OwnProcessName { get; } =
        Process.GetCurrentProcess().ProcessName;

    public string? DetectForegroundGame()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == nint.Zero)
                return null;

            // DWM cloaked 窗口（挂起的 UWP 等）在视觉上不可见，不应判为前台游戏。
            if (IsCloaked(hwnd))
                return null;

            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref monitorInfo))
                return null;

            if (!GetWindowRect(hwnd, out var windowRect))
                return null;

            var windowArea = (long)(windowRect.Right - windowRect.Left) * (windowRect.Bottom - windowRect.Top);
            var monitorArea = (long)(monitorInfo.rcMonitor.Right - monitorInfo.rcMonitor.Left)
                * (monitorInfo.rcMonitor.Bottom - monitorInfo.rcMonitor.Top);
            if (monitorArea <= 0 || windowArea <= 0)
                return null;

            var style = GetWindowLong(hwnd, GWL_STYLE);
            var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);

            // 子窗口与工具窗口不属于游戏画面。
            if ((style & WS_CHILD) != 0 || (exStyle & WS_EX_TOOLWINDOW) != 0)
                return null;

            var isFullscreen = windowArea >= monitorArea * 98 / 100;

            // Phase 5：无边框窗口化全屏（无标题栏/可调边框样式的"无边框"模式）。
            var isBorderless = (style & WS_CAPTION) == 0 && (style & WS_THICKFRAME) == 0;
            var isBorderlessFullscreen = !isFullscreen
                && windowArea >= monitorArea * 90 / 100
                && isBorderless;

            if (!isFullscreen && !isBorderlessFullscreen)
                return null;

            _ = GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0)
                return null;

            using var process = Process.GetProcessById((int)pid);
            var name = process.ProcessName;
            return IsShell(name) ? null : $"{name}.exe";
        }
        catch
        {
            // 进程可能刚退出（如游戏崩溃/退出瞬间），检测失败按"无游戏"处理。
            return null;
        }
    }

    private static bool IsShell(string processName)
        => ShellProcesses.Contains(processName)
           || string.Equals(processName, OwnProcessName, StringComparison.OrdinalIgnoreCase);

    /// <summary>DWMWA_CLOAKED 检测：窗口被 DWM 遮蔽（不可见）返回 true。检测失败按可见处理。</summary>
    private static bool IsCloaked(nint hwnd)
    {
        try
        {
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) != 0)
                return false;

            return cloaked != 0;
        }
        catch
        {
            return false;
        }
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const long WS_CHILD = 0x40000000;
    private const long WS_CAPTION = 0x00C00000;
    private const long WS_THICKFRAME = 0x00040000;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const int DWMWA_CLOAKED = 14;

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern long GetWindowLong(nint hwnd, int index);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}
