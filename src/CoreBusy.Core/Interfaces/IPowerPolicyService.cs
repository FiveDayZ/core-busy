namespace CoreBusy.Core.Interfaces;

using CoreBusy.Core.Models;

/// <summary>
/// 电源策略服务（v1.12.0）：读写当前电源方案中的处理器相关设置。
/// <para>
/// 实现口径参考 <c>vadyaravadim/cpu-parking-disabler</c>：
/// 核心停放最小核数（CPMINCORES）、能源性能偏好（PERFEPP）、处理器最大状态
/// （PROCTHROTTLEMAX）。三个设置同属 <c>SUB_PROCESSOR</c> 子组
/// （54533251-82be-4824-96c1-47b60b740d00）。
/// </para>
/// <para>
/// **安全契约**：任何写操作前必须先备份当前三个值，且备份需落盘；
/// <see cref="Restore"/> 必须能把方案还原到本工具改动之前的状态。
/// 这是本功能的硬性要求 —— 改电源方案是系统级改动，没有回滚路径就不允许暴露给用户。
/// </para>
/// </summary>
public interface IPowerPolicyService
{
    /// <summary>读取当前电源策略状态。</summary>
    PowerPolicyState Read();

    /// <summary>应用预设。成功返回 true；失败原因写入 <paramref name="error"/>。</summary>
    bool Apply(PowerPreset preset, out string error);

    /// <summary>还原到最近一次备份的状态。</summary>
    bool Restore(out string error);

    /// <summary>是否存在可还原的备份。</summary>
    bool HasBackup { get; }
}
