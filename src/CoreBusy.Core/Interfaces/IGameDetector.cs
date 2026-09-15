namespace CoreBusy.Core.Interfaces;

/// <summary>前台全屏应用检测（游戏模式入口，方案 4.1 节）。</summary>
public interface IGameDetector
{
    /// <summary>
    /// 返回当前前台全屏应用的进程名（如 "Cyberpunk2077.exe"）；
    /// 非全屏或全屏应用属于桌面外壳时返回 null。
    /// </summary>
    string? DetectForegroundGame();
}
