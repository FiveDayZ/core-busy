namespace CoreBusy.Windows.Optimization;

/// <summary>
/// 优化模块日志出口（v1.12.0）。
/// 与 <c>TopologyLog</c> 同一约定：CoreBusy.Windows 层不直接依赖 App 层，
/// 由 App 启动时把 <c>AppLog.Write</c> 接到 <see cref="Sink"/> 上，写进 logs/debug.log。
/// 未接线时静默丢弃，不影响功能。
/// </summary>
public static class OptimizationLog
{
    /// <summary>日志接收端（由 App 层注入）。</summary>
    public static Action<string>? Sink { get; set; }

    public static void Write(string message) => Sink?.Invoke($"[OPT] {message}");
}
