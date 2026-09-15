namespace CoreBusy.Windows.Topology;

/// <summary>拓扑取证日志出口：由宿主（App）在启动时接到 AppLog，避免 Windows 程序集反向依赖 UI 层。</summary>
public static class TopologyLog
{
    /// <summary>宿主注入的日志接收器；未接线时静默丢弃。</summary>
    public static Action<string>? Sink { get; set; }

    public static void Write(string message) => Sink?.Invoke(message);
}
