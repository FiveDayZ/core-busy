namespace CoreBusy.Core.Interfaces;

using CoreBusy.Core.Models;

/// <summary>CPU 拓扑识别服务（物理核心 / SMT / P-E 类别）。</summary>
public interface ICpuTopologyService
{
    /// <summary>获取逻辑处理器拓扑。实现必须与 PerformanceCounter 的逻辑处理器编号对齐。</summary>
    CpuTopology GetTopology();
}
