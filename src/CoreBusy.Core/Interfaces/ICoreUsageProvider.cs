namespace CoreBusy.Core.Interfaces;

/// <summary>每逻辑处理器占用率采集器。</summary>
public interface ICoreUsageProvider
{
    /// <summary>逻辑处理器数量。</summary>
    int LogicalProcessorCount { get; }

    /// <summary>
    /// 采样一次全部逻辑处理器占用率（0-100），读取失败的元素为 NaN。
    /// 返回数组长度等于 <see cref="LogicalProcessorCount"/>，下标即逻辑处理器编号。
    /// </summary>
    double[] SampleAll();
}
