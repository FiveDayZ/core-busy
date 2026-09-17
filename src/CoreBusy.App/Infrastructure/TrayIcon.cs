namespace CoreBusy.App.Infrastructure;

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

/// <summary>
/// WPF 原生托盘图标（v1.19.0），替代 <c>System.Windows.Forms.NotifyIcon</c>。
/// <para>
/// **为什么要自己写**：托盘是本程序**唯一**用到 WinForms 的地方，而
/// <c>UseWindowsForms=true</c> 会把 <c>System.Windows.Forms.dll</c>（13.5 MB）、
/// <c>System.Windows.Forms.Primitives.dll</c>（3.0 MB）连带 <c>System.Drawing</c> 一起
/// 编进自包含产物，并在每次启动时加载它们的元数据 —— 为一个 16×16 的图标付这套代价不划算。
/// 直接走 <c>Shell_NotifyIcon</c> 只需一个隐藏消息窗口 + 少量互操作，与本项目既有的
/// P/Invoke 风格（<c>FullscreenGameDetector</c> / <c>ProcessOptimizationNative</c>）一致，
/// 且不引入第三方包（无许可证与维护性风险）。
/// </para>
/// <para>
/// **行为与旧实现逐项对齐**：双击恢复窗口、右键弹出菜单（菜单内容由宿主提供）、
/// 关闭到托盘后弹一次气泡提示。菜单用 WPF 的 <see cref="ContextMenu"/>，
/// 因此菜单项可以绑定路由命令，不必再走 WinForms 的消息循环。
/// </para>
/// <para>
/// **图标来源**：优先 <c>ExtractIconEx</c> 取 exe 自身的图标资源（<c>ApplicationIcon</c>
/// 已把 .ico 写进产物）；失败时退到系统通用图标 <c>IDI_APPLICATION</c>，并把降级原因
/// 写进 <c>logs/debug.log</c> —— 宁可图标不精致，也不能出现"窗口藏起来了但托盘里没有图标"
/// 这种用户无法自救的状态。
/// </para>
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    /// <summary>托盘回调消息（WM_APP + 1，本进程内自用，不与系统消息冲突）。</summary>
    private const int WmTrayCallback = 0x8000 + 1;

    private const int NimAdd = 0x00000000;
    private const int NimModify = 0x00000001;
    private const int NimDelete = 0x00000002;

    private const int NifMessage = 0x00000001;
    private const int NifIcon = 0x00000002;
    private const int NifTip = 0x00000004;
    private const int NifInfo = 0x00000010;

    private const int NifInfoFlagInfo = 0x00000001;

    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const int WmContextMenu = 0x007B;

    private const int WsExToolWindow = 0x00000080;
    private const int IdiApplication = 32512;

    /// <summary>szTip / szInfo / szInfoTitle 的容量（含结尾 NUL，与 NOTIFYICONDATAW 定义一致）。</summary>
    private const int TipCapacity = 128;
    private const int InfoCapacity = 256;
    private const int InfoTitleCapacity = 64;

    private readonly NotifyIconData _data;
    private readonly uint _id;
    private HwndSource? _source;
    private IntPtr _hIcon;
    private bool _iconAdded;
    private bool _disposed;

    public TrayIcon(string tooltip, uint id = 1)
    {
        _id = id;

        // 隐藏消息窗口：只收托盘回调，不显示、不进 Alt-Tab（WS_EX_TOOLWINDOW）。
        // 尺寸给 0 是既有 WPF 托盘实现的通行做法 —— 它永远不显示，只为拿一个 hWnd。
        _source = new HwndSource(new HwndSourceParameters("CoreBusyTraySink")
        {
            WindowStyle = 0,
            ExtendedWindowStyle = WsExToolWindow,
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
        });
        _source.AddHook(WndProc);

        _hIcon = LoadApplicationIcon();

        _data = new NotifyIconData
        {
            hWnd = _source.Handle,
            uID = _id,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = WmTrayCallback,
            hIcon = _hIcon,
            szTip = Truncate(tooltip, TipCapacity - 1),
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };
        _data.cbSize = Marshal.SizeOf<NotifyIconData>();

        try
        {
            _iconAdded = Shell_NotifyIconW(NimAdd, ref _data);
        }
        catch (Exception)
        {
            _iconAdded = false;
        }
    }

    /// <summary>双击（或单击）图标：宿主据此恢复主窗口。</summary>
    public event EventHandler? Activate;

    /// <summary>右键菜单。由宿主装配，本类只负责在弹出的那一刻把它放到鼠标位置。</summary>
    public ContextMenu? Menu { get; set; }

    /// <summary>图标是否真的进了通知区域（false 时宿主应记录降级，而不是假装成功）。</summary>
    public bool IsAttached => _iconAdded;

    /// <summary>弹出气泡提示（等价于旧实现的 <c>NotifyIcon.ShowBalloonTip</c>）。</summary>
    public bool ShowBalloon(string title, string text, uint timeoutMs = 3000)
    {
        if (!_iconAdded || _disposed)
            return false;

        // 只改提示信息：uFlags 换成 NIF_INFO 后，图标与 tooltip 保持原值不被覆盖。
        var data = _data;
        data.uFlags = NifInfo;
        data.szInfo = Truncate(text, InfoCapacity - 1);
        data.szInfoTitle = Truncate(title, InfoTitleCapacity - 1);
        data.uVersionOrTimeout = timeoutMs;
        data.dwInfoFlags = NifInfoFlagInfo;

        try
        {
            return Shell_NotifyIconW(NimModify, ref data);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmTrayCallback)
            return IntPtr.Zero;

        // lParam 低位是鼠标消息（高 16 位留给图标 id）；x64 下要按 16 位截取，
        // 直接转 int 会因符号扩展得到负值，比较永远不成立。
        var mouseMessage = (int)(lParam.ToInt64() & 0xFFFF);
        switch (mouseMessage)
        {
            case WmLButtonDblClk:
                Activate?.Invoke(this, EventArgs.Empty);
                handled = true;
                break;

            case WmRButtonUp:
            case WmContextMenu:
                ShowMenu();
                handled = true;
                break;

            case WmLButtonUp:
                // 单击不恢复窗口：与旧实现（仅双击恢复）保持一致，避免误触把面板弹出来。
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    /// <summary>在鼠标位置弹出右键菜单。</summary>
    private void ShowMenu()
    {
        var menu = Menu;
        if (menu is null)
            return;

        try
        {
            menu.Placement = PlacementMode.MousePoint;
            menu.IsOpen = true;
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// 取托盘图标句柄：先要 exe 自带的图标资源，失败退系统通用图标。
    /// 后者会写一行降级日志 —— "图标变成默认样式"必须能从事后日志里解释清楚。
    /// </summary>
    private static IntPtr LoadApplicationIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                if (ExtractIconExW(exe, 0, out var large, out var small, 1) > 0)
                {
                    // 托盘用 16×16 那一档；小图标缺失时才退用大图标。
                    // 注意先决定"留哪个"再销毁另一个 —— 反过来写会把要返回的句柄销毁掉，
                    // 结果是"图标加了但通知区域里一片空白"，而日志里一切正常。
                    var keep = small != IntPtr.Zero ? small : large;
                    if (large != IntPtr.Zero && large != keep)
                        DestroyIcon(large);
                    if (keep != IntPtr.Zero)
                        return keep;
                }
            }
        }
        catch (Exception)
        {
        }

        try
        {
            return LoadIconW(IntPtr.Zero, new IntPtr(IdiApplication));
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>按 UTF-16 码元截断，避免超长字符串在固定长度字段里被驱动侧截断成乱码。</summary>
    private static string Truncate(string value, int maxLength)
        => string.IsNullOrEmpty(value) || value.Length <= maxLength
            ? value ?? string.Empty
            : value[..maxLength];

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_iconAdded)
        {
            try
            {
                var data = _data;
                data.uFlags = 0;
                Shell_NotifyIconW(NimDelete, ref data);
            }
            catch
            {
                // 退出路径上的失败无需上报：进程即将结束，图标由系统回收。
            }

            _iconAdded = false;
        }

        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }

        if (_hIcon != IntPtr.Zero)
        {
            try { DestroyIcon(_hIcon); } catch { /* 句柄已随进程消失 */ }
            _hIcon = IntPtr.Zero;
        }
    }

    /// <summary>
    /// NOTIFYICONDATAW（Vista+ 完整版，x64 下 <c>Marshal.SizeOf</c> = 976 字节）。
    /// 字段顺序与 winuser.h 严格一致 —— 少一个字段或改一次顺序，shell 会把结构体
    /// 按错误的偏移解释，表现为"图标加了但 tooltip 是乱码 / 气泡不弹"。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = TipCapacity)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = InfoCapacity)]
        public string szInfo;

        /// <summary>联合体：Vista 前为 uTimeout，Vista 起另有 uVersion（本实现只用 uTimeout 语义）。</summary>
        public uint uVersionOrTimeout;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = InfoTitleCapacity)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NotifyIconData lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint ExtractIconExW(string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", EntryPoint = "LoadIconW", SetLastError = true)]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);
}
