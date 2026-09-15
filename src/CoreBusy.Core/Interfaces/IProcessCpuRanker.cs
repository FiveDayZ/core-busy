namespace CoreBusy.Core.Interfaces;

using CoreBusy.Core.Models;

/// <summary>
/// 进程 CPU 占用排行（方案 4.3 节"进程核心分析"数据源）。
/// 基于进程 CPU 时间增量计算；真实按逻辑核心归属需 ETW 内核跟踪，MVP 提供全系统排行。
/// </summary>
public interface IProcessCpuRanker
{
    /// <summary>
    /// 采样一次全系统进程 CPU 占用，返回占用最高的前 <paramref name="top"/> 个进程。
    /// 首次调用返回空列表（无增量基准），之后每次调用以上次调用为基准，按调用间隔折算百分比。
    /// </summary>
    List<ProcessCpuSample> SampleTop(int top);
}
