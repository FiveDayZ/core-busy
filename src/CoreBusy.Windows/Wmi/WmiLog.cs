namespace CoreBusy.Windows.Wmi;

/// <summary>WMI 通道取证日志出口（v1.16.3）。与 TopologyLog / OptimizationLog 同一约定：
/// 由宿主（App）在启动时把 <c>AppLog.Write</c> 接到 <see cref="Sink"/>，写进 exe 同级
/// logs/debug.log；未接线时静默丢弃，Windows 程序集不反向依赖 UI 层。</summary>
public static class WmiLog
{
    /// <summary>宿主注入的日志接收器；未接线时静默丢弃。</summary>
    public static Action<string>? Sink { get; set; }

    public static void Write(string message) => Sink?.Invoke(message);
}
