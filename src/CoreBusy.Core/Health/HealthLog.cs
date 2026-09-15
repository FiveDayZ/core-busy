namespace CoreBusy.Core.Health;

/// <summary>
/// 核心健康度模块的取证日志出口（v1.17.0）。与 TopologyLog / OptimizationLog / SensorLog /
/// WmiLog 同一约定：宿主（App）启动时把 <c>AppLog.Write</c> 接到 <see cref="Sink"/>，
/// 写进 exe 同级 logs/debug.log；未接线时静默丢弃。
/// </summary>
/// <remarks>
/// 为什么健康度也需要一条日志出口：这一块的输出是**推导值**而非读数，
/// 一旦算出「某核只有 62 分」这种结论，必须能回答"依据是哪几个采样"。
/// 只把分数画在界面上、不留中间量，等于给出一个无法复核的数字。
/// </remarks>
public static class HealthLog
{
    /// <summary>宿主注入的日志接收器；未接线时静默丢弃。</summary>
    public static Action<string>? Sink { get; set; }

    public static void Write(string message) => Sink?.Invoke(message);
}
