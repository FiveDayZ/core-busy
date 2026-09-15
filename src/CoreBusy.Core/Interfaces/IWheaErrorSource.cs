namespace CoreBusy.Core.Interfaces;

using CoreBusy.Core.Models;

/// <summary>
/// WHEA 硬件错误事件源（v1.17.0）。实现放在具备桌面框架的程序集里（App 层），
/// 评分器只认这个契约，因此核心逻辑不依赖 <c>System.Diagnostics.EventLog</c>。
/// </summary>
public interface IWheaErrorSource
{
    /// <summary>
    /// 读取统计。实现**必须节流** —— 事件日志查询虽走服务端过滤，仍不该每帧发起；
    /// 同一进程内的重复调用应返回缓存值。
    /// </summary>
    WheaErrorCounts Read();
}
