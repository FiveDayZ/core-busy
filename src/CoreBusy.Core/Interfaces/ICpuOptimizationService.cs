namespace CoreBusy.Core.Interfaces;

using CoreBusy.Core.Models;

/// <summary>
/// CPU 核心优化服务（v1.12.0）：按规则套用进程核心亲和性与优先级，
/// 并与游戏模式联动。
/// <para>
/// 设计约束：
/// ① 只做**策略套用**，绝不终止/挂起进程 —— 与 Process Lasso 的 ProBalance 有本质区别，
///    本工具不介入进程生命周期，降低误伤风险；
/// ② 所有写操作都必须可逆：亲和性与优先级随进程退出自然消失，不做持久化写入注册表；
/// ③ 系统关键进程（见实现层黑名单）一律拒绝套用，避免把 csrss / winlogon 之类
///    限制到单核后导致整机假死。
/// </para>
/// </summary>
public interface ICpuOptimizationService
{
    /// <summary>当前环境是否支持进程优化（需要管理员权限）。</summary>
    bool IsSupported { get; }

    /// <summary>支持状态说明（不支持时直接展示原因，不静默降级）。</summary>
    string StatusDetail { get; }

    /// <summary>
    /// 按真实拓扑过滤后的可用亲和性模式。
    /// 例如 Intel 非混合架构与 AMD 不返回 P/E 两项，避免界面给出无法落地的选项。
    /// </summary>
    IReadOnlyList<CoreAffinityMode> AvailableAffinityModes { get; }

    /// <summary>
    /// 逻辑处理器拓扑（v1.15.0，供核心选择器按物理核分组、标注 P/E 类别）。
    /// 服务不可用或拓扑未解析时为空列表 —— 界面据此禁用「自定义核心」
    /// 而不是弹出一个空选择器。
    /// </summary>
    IReadOnlyList<LogicalProcessorTopology> TopologyView { get; }

    /// <summary>
    /// 校验自定义核心掩码。合法返回 null；否则返回可读原因（空掩码 / 含不存在的处理器）。
    /// 界面在「确定」前调用，服务端套用时再复核一次 —— 两处共用同一实现，避免口径漂移。
    /// </summary>
    string? ValidateCustomMask(ulong mask);

    /// <summary>枚举当前运行进程名（去重、按名称排序），供规则编辑器挑选目标。</summary>
    IReadOnlyList<string> ListRunningProcessNames();

    /// <summary>
    /// 套用一条规则到单个进程。返回是否成功；失败原因写入 <paramref name="error"/>。
    /// 实现必须对黑名单进程、已退出进程、权限不足分别给出可读原因。
    /// </summary>
    bool TryApplyRule(ProcessOptimizationRule rule, int processId, out string error);

    /// <summary>
    /// 立即扫描并套用全部启用的规则，返回本轮实际套用的进程数。
    /// <para>
    /// <paramref name="gameModeActive"/> 是 v1.13.0 新增的输入：标记为
    /// <see cref="ProcessOptimizationRule.GameOnly"/> 的规则只在该值为 true 时套用。
    /// 由调用方提供而不是服务自己去探测游戏状态 —— 检测器与规则套用是两个独立关注点，
    /// 服务内部再探测一次会出现"两处判定不一致"的经典问题（界面上说在游戏中、
    /// 服务认为不在，规则时灵时不灵且无从排查）。
    /// </para>
    /// <para>
    /// 该值由 false 变 true、或由 true 变 false 时，实现必须**绕过节流**立即扫一次，
    /// 否则游戏切进切出后要等一个节流周期才生效。
    /// </para>
    /// </summary>
    int ApplyRulesNow(OptimizationSettings settings, bool gameModeActive);

    /// <summary>
    /// 游戏模式联动：进入全屏游戏时套用游戏预设，退出时解除（优先级/亲和性恢复为默认）。
    /// 传入 null 表示游戏已退出。
    /// </summary>
    void ApplyGameMode(OptimizationSettings settings, string? gameProcessName);
}
