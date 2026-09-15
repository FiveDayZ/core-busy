namespace CoreBusy.Windows.PerformanceCounter;

using System.Diagnostics;
using CoreBusy.Core.Interfaces;

/// <summary>
/// 基于 Windows Performance Counter（Processor 对象 / % Processor Time 计数器）的
/// 每逻辑处理器占用率采集实现（方案 7.2 节）。
/// </summary>
public sealed class PerCoreUsageProvider : ICoreUsageProvider
{
    private const string CategoryName = "Processor";
    private const string CounterName = "% Processor Time";

    private readonly List<PerformanceCounter> _counters = new();

    public int LogicalProcessorCount { get; }

    /// <param name="logicalProcessorCount">逻辑处理器数量，来自 CPU 信息模块。</param>
    /// <exception cref="ArgumentOutOfRangeException">数量小于 1。</exception>
    /// <exception cref="InvalidOperationException">性能计数器实例与逻辑处理器数量不匹配。</exception>
    public PerCoreUsageProvider(int logicalProcessorCount)
    {
        if (logicalProcessorCount < 1)
            throw new ArgumentOutOfRangeException(nameof(logicalProcessorCount));

        LogicalProcessorCount = logicalProcessorCount;

        var instances = new PerformanceCounterCategory(CategoryName).GetInstanceNames();

        // 单处理器组内实例名为 "0".."N-1"；超过 64 逻辑处理器的多处理器组实例名形如 "0,0"。
        var matched = instances
            .Where(name => ushort.TryParse(name, out _))
            .Select(name => ushort.Parse(name))
            .OrderBy(index => index)
            .ToArray();

        if (matched.Length < logicalProcessorCount)
            throw new InvalidOperationException(
                $"性能计数器实例数({matched.Length})少于逻辑处理器数({logicalProcessorCount})。");

        for (ushort i = 0; i < logicalProcessorCount; i++)
        {
            _counters.Add(new PerformanceCounter(CategoryName, CounterName, i.ToString(), readOnly: true));
        }
    }

    public double[] SampleAll()
    {
        var values = new double[_counters.Count];
        for (int i = 0; i < _counters.Count; i++)
        {
            float value;
            try
            {
                value = _counters[i].NextValue();
            }
            catch (InvalidOperationException)
            {
                // 实例消失（如热插拔/计数器重建）时按 0 处理，避免采样线程崩溃。
                value = 0f;
            }

            values[i] = Math.Clamp(value, 0d, 100d);
        }

        return values;
    }
}
