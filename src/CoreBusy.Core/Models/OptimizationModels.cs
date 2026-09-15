namespace CoreBusy.Core.Models;

/// <summary>
/// 进程核心亲和性模式（v1.12.0）。
/// <para>
/// 语义与实现口径：<see cref="AllCores"/> / <see cref="PhysicalOnly"/> /
/// <see cref="PerformanceOnly"/> / <see cref="EfficiencyOnly"/> 都按**真实拓扑**折算成
/// 逻辑处理器集合，再由实现层转成处理器掩码。非混合架构上 P/E 两项不可用，
/// 由 <c>IOptimizationService.AvailableAffinityModes</c> 按硬件过滤后交给界面，
/// 避免出现"选了 P 核但机器上根本没有 P 核"的静默空操作。
/// </para>
/// </summary>
public enum CoreAffinityMode
{
    /// <summary>不干预，保留系统默认调度。</summary>
    None = 0,

    /// <summary>全部逻辑处理器（解除既有绑定）。</summary>
    AllCores = 1,

    /// <summary>
    /// 仅物理核：每个物理核只保留首个逻辑处理器，屏蔽 SMT 兄弟线程。
    /// 面向"单线程敏感型负载"——同一物理核的两个超线程会争抢执行单元与 L1/L2。
    /// </summary>
    PhysicalOnly = 2,

    /// <summary>仅性能核 P-Core（Intel 混合架构）。</summary>
    PerformanceOnly = 3,

    /// <summary>仅能效核 E-Core（Intel 混合架构）。</summary>
    EfficiencyOnly = 4,

    /// <summary>
    /// 自定义核心子集（v1.15.0）：掩码存于 <see cref="ProcessOptimizationRule.CustomAffinityMask"/>
    /// 或 <see cref="OptimizationSettings.GameCustomAffinityMask"/>，第 n 位对应逻辑处理器 n。
    /// 界面核心选择器负责生成，服务端 <c>AffinityMaskBuilder.ValidateCustom</c> 复核 ——
    /// 即使 settings.json 被手改绕过界面，落点也是「拒绝并给出原因」而不是错误调度。
    /// </summary>
    Custom = 5,
}

/// <summary>
/// 进程优先级档位（映射 Windows 优先级类）。
/// 刻意不暴露 RealTime：一旦把普通进程提到实时优先级，它可能饿死输入线程导致整机假死，
/// 这是 Process Lasso 类工具公认的事故点，本工具不提供该档位。
/// </summary>
public enum ProcessPriorityLevel
{
    /// <summary>不干预。</summary>
    None = 0,

    /// <summary>低（IDLE_PRIORITY_CLASS）。</summary>
    Idle = 1,

    /// <summary>低于标准（BELOW_NORMAL_PRIORITY_CLASS）。</summary>
    BelowNormal = 2,

    /// <summary>标准（NORMAL_PRIORITY_CLASS）。</summary>
    Normal = 3,

    /// <summary>高于标准（ABOVE_NORMAL_PRIORITY_CLASS）。</summary>
    AboveNormal = 4,

    /// <summary>高（HIGH_PRIORITY_CLASS）。</summary>
    High = 5,
}

/// <summary>
/// 单条进程优化规则。以**进程名**（如 <c>chrome.exe</c>）为键，
/// 对新启动的同名进程同样生效——这是与"只改当前进程"的即时操作的本质区别。
/// </summary>
public sealed class ProcessOptimizationRule
{
    /// <summary>目标进程名（含 .exe，大小写不敏感）。</summary>
    public string ProcessName { get; set; } = string.Empty;

    /// <summary>是否启用该规则。停用后不再套用，但不会回滚已生效的设置。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>核心亲和性。</summary>
    public CoreAffinityMode Affinity { get; set; } = CoreAffinityMode.None;

    /// <summary>
    /// 自定义核心掩码（<see cref="CoreAffinityMode.Custom"/> 时生效）：第 n 位 = 逻辑处理器 n，
    /// 与 <see cref="LogicalProcessorTopology.OsIndex"/> 同口径。0 视为未配置 ——
    /// 服务端会拒绝并给出原因；界面核心选择器强制至少勾选 1 个。
    /// </summary>
    public ulong CustomAffinityMask { get; set; }

    /// <summary>优先级档位。</summary>
    public ProcessPriorityLevel Priority { get; set; } = ProcessPriorityLevel.None;

    /// <summary>
    /// 用 Job Object 在内核层强制亲和。
    /// 普通 <c>SetProcessAffinityMask</c> 是进程级设置，进程自己（或其子进程）可以再改回去；
    /// 作业对象限制由内核强制，目标进程无法逃逸。代价是无法撤销（进程退出才释放），
    /// 因此界面上需要显式提示。
    /// </summary>
    public bool KernelEnforced { get; set; }

    /// <summary>把该进程的磁盘 I/O 优先级降为 VeryLow，避免后台任务抢占前台读写。</summary>
    public bool LowerIoPriority { get; set; }

    /// <summary>
    /// 仅在游戏模式激活期间套用（v1.13.0）。
    /// <para>
    /// 存在意义：后台负载降级对开发机是有代价的 —— Docker 与编译器被压到「低于标准」
    /// 会明显拖慢构建。勾上此项后，规则只在检测到全屏游戏时生效，退出游戏即恢复默认
    /// 优先级，日常开发完全不受影响；不勾则是常驻生效。
    /// </para>
    /// <para>
    /// 依赖游戏检测：若「常规」页的「启用游戏模式」被关掉，或游戏检测器不可用，
    /// 本项视为永不满足 —— 规则静默不套用，而不是退化成常驻规则（那等于
    /// 用户以为自己关掉了优化，实际影响还在）。
    /// </para>
    /// </summary>
    public bool GameOnly { get; set; }
}

/// <summary>电源策略预设。</summary>
public enum PowerPreset
{
    /// <summary>不干预（保持系统当前方案）。</summary>
    None = 0,

    /// <summary>
    /// 性能优先：解除核心停放（CPMINCORES=100）、EPP 拉到 0（最大性能）、
    /// 处理器最大状态 100%。参考 <c>cpu-parking-disabler</c> 的口径。
    /// </summary>
    Performance = 1,

    /// <summary>节能优先：恢复动态停放、EPP 拉到 100（最大节能）、处理器最大状态 80%。</summary>
    Saver = 2,
}

/// <summary>
/// 电源策略当前状态（只读快照）。
/// 未知项一律用 <c>null</c> 而非 0：0 在 EPP 语义里是"最大性能"，拿 0 冒充"读不到"
/// 会让界面把未知状态显示成满血状态。
/// </summary>
/// <param name="Available">是否成功读到当前电源方案（false 时其余字段无意义）。</param>
/// <param name="CoreParkingMinPercent">核心停放最小核数百分比（100 表示不停放）。</param>
/// <param name="EppPercent">能源性能偏好 EPP（0=最大性能，100=最大节能）。</param>
/// <param name="ProcessorMaxPercent">处理器最大状态百分比。</param>
/// <param name="BackupAvailable">是否已存在可还原的备份。</param>
/// <param name="Detail">面向用户的说明文本（含失败原因）。</param>
public sealed record PowerPolicyState(
    bool Available,
    int? CoreParkingMinPercent,
    int? EppPercent,
    int? ProcessorMaxPercent,
    bool BackupAvailable,
    string Detail);

/// <summary>
/// CPU 核心优化总设置，随 <see cref="AppSettings"/> 一起持久化到 settings.json。
/// </summary>
public sealed class OptimizationSettings
{
    /// <summary>总开关。关闭后停止新增套用（已生效的亲和性不主动回滚，避免扰动正在跑的进程）。</summary>
    public bool Enabled { get; set; }

    /// <summary>进程规则表。</summary>
    public List<ProcessOptimizationRule> Rules { get; set; } = [];

    /// <summary>进入全屏游戏时自动套用下面的游戏预设。</summary>
    public bool GameModeEnabled { get; set; }

    /// <summary>游戏预设：亲和性。</summary>
    public CoreAffinityMode GameAffinity { get; set; } = CoreAffinityMode.None;

    /// <summary>游戏预设的自定义核心掩码（<see cref="GameAffinity"/> = Custom 时生效）。</summary>
    public ulong GameCustomAffinityMask { get; set; }

    /// <summary>游戏预设：优先级。</summary>
    public ProcessPriorityLevel GamePriority { get; set; } = ProcessPriorityLevel.AboveNormal;

    /// <summary>游戏启动时同时应用"性能优先"电源策略（退出游戏后自动还原）。</summary>
    public bool GameAppliesPowerPreset { get; set; }

    /// <summary>
    /// 点击「确定」时要应用的电源预设。
    /// <see cref="PowerPreset.None"/> 表示**不改动**当前电源方案（不是"还原"）——
    /// 还原是设置窗口里那个独立按钮的职责，两者语义不同，混在一起会让
    /// "我只想把预设清掉"变成一次意料之外的系统改动。
    /// </summary>
    public PowerPreset PowerPreset { get; set; } = PowerPreset.None;
}
