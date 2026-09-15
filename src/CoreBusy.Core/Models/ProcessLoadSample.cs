namespace CoreBusy.Core.Models;

/// <summary>主要负载进程采样。</summary>
public sealed record ProcessLoadSample
{
    /// <summary>进程名，例如 "Game.exe"。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>CPU 占用（%，全机口径）。</summary>
    public double UsagePercent { get; init; }

    /// <summary>图标类别键（game/browser/system/chat/steam），用于选择展示图标。</summary>
    public string IconKind { get; init; } = "app";
}
