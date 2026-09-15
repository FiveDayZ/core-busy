namespace CoreBusy.Sensor;

/// <summary>
/// 传感器层取证日志出口（v1.16.1）。与 <c>TopologyLog</c> 同一约定：
/// CoreBusy.Sensor 不反向依赖 App 层，由 App 启动时把 <c>AppLog.Write</c> 接到
/// <see cref="Sink"/> 上，写进 exe 同级 logs/debug.log；未接线时静默丢弃。
/// <para>行首前缀（<c>[SENSOR]</c>）由调用方给出，与 TopologyLog 口径一致。</para>
/// </summary>
public static class SensorLog
{
    /// <summary>日志接收端（由 App 层注入）。</summary>
    public static Action<string>? Sink { get; set; }

    public static void Write(string message) => Sink?.Invoke(message);
}
