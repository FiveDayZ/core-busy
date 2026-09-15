namespace CoreBusy.Windows.Optimization;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using CoreBusy.Core.Interfaces;
using CoreBusy.Core.Models;
using CoreBusy.Windows.Topology;

/// <summary>
/// 进程级 CPU 优化服务（v1.12.0）的实现。
/// <para>
/// 策略：按进程名匹配 → 设置优先级类 → 设置处理器亲和性 → 可选内核强制（作业对象）
/// → 可选降低 I/O 优先级。**只改调度参数，绝不终止或挂起进程**。
/// </para>
/// <para>
/// 参考实现：<c>Prohect/ProcGovernor</c>（内核强制亲和、作业对象）、
/// <c>dmaitz/ProcessorAffinityMgr</c>（按进程名规则自动套用）。
/// </para>
/// </summary>
public sealed class WindowsCpuOptimizationService : ICpuOptimizationService, IDisposable
{
    /// <summary>
    /// 规则重扫周期（秒）。每条 Tick（默认 1 秒）都全量枚举进程代价偏高，
    /// 且亲和性一旦设好就是常驻状态，不需要秒级重复确认。5 秒足以覆盖
    /// "新启动的进程很快被规则接管"这一用户可感知的时效。
    /// </summary>
    private const int ScanIntervalSeconds = 5;

    private readonly CpuTopology _topology;
    private readonly AffinityMaskBuilder _builder;
    private readonly bool _elevated;

    /// <summary>pid → 上次套用结果的签名。签名相同即跳过，避免每秒重复调用系统 API。</summary>
    private readonly Dictionary<int, string> _appliedByPid = [];

    /// <summary>
    /// 已创建的作业对象句柄。**必须持有**：一旦关闭句柄，作业被销毁，
    /// 内核强制的亲和性限制会随之消失，规则等于白设。
    /// </summary>
    private readonly List<IntPtr> _jobs = [];

    /// <summary>已经被作业对象接管的 pid（重复 AssignProcessToJobObject 会失败）。</summary>
    private readonly HashSet<int> _jobAssigned = [];

    /// <summary>
    /// 因「仅游戏时生效」规则而被改过调度参数的 pid（v1.13.0）。
    /// 退出游戏时必须逐个恢复 —— 否则进程会带着被压低的优先级一直跑下去，
    /// 表现为"关掉游戏后 Docker 还是慢"，而界面上没有任何线索指向本工具。
    /// </summary>
    private readonly HashSet<int> _gameOnlyPids = [];

    /// <summary>上一次的「是否处于游戏模式」。用于识别边沿，并据此绕过节流立即响应。</summary>
    private bool _lastGameModeActive;

    private DateTime _lastScanUtc = DateTime.MinValue;
    private int? _gamePid;

    /// <summary>最近一次套用的可读摘要，供状态栏/日志取证。</summary>
    public string LastDetail { get; private set; } = string.Empty;

    public WindowsCpuOptimizationService()
    {
        _topology = new LogicalProcessorTopologyService().GetTopology();
        _builder = new AffinityMaskBuilder(_topology);
        _elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator);
    }

    public bool IsSupported => _builder.SupportsMask;

    public string StatusDetail => !_builder.SupportsMask
        ? $"逻辑处理器 {_builder.LogicalCount} 个，跨越多个处理器组，位掩码方式不可用"
        : $"{(_elevated ? "已提升权限 · 可优化全部进程" : "未提升权限 · 仅能优化当前用户进程")} · {DescribeTopology()}";

    public IReadOnlyList<CoreAffinityMode> AvailableAffinityModes => _builder.AvailableModes();

    /// <inheritdoc />
    public IReadOnlyList<LogicalProcessorTopology> TopologyView => _topology.LogicalProcessors;

    /// <inheritdoc />
    public string? ValidateCustomMask(ulong mask) => _builder.ValidateCustom(mask);

    /// <summary>
    /// 拓扑一句话摘要，直接放在状态里而不是藏进文档：用户问"为什么没有仅 P 核"时，
    /// 界面必须直说这台机器没有 P 核（如 N305 全为 E 核）或没有大小核分区（AMD 同构）。
    /// </summary>
    private string DescribeTopology()
    {
        var logical = _builder.LogicalCount;
        if (_topology.IsHybrid)
        {
            var p = _topology.LogicalProcessors.Count(c => c.CoreClass == CoreClass.Performance);
            return $"{logical} 线程 · P 核 {p} / E 核 {logical - p} 个线程";
        }

        return _topology.HasEfficiencyCores
            ? $"{logical} 线程 · 全部为能效核（无 P 核）"
            : $"{logical} 线程 · 无大小核分区";
    }

    public IReadOnlyList<string> ListRunningProcessNames()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var name = process.ProcessName + ".exe";
                if (!SystemCriticalProcesses.IsProtected(name))
                    names.Add(name);
            }
            catch
            {
                // 进程在枚举与取名之间退出，或权限不足——跳过即可。
            }
            finally
            {
                process.Dispose();
            }
        }

        return names.ToList();
    }

    /// <summary>
    /// 重扫节流后的规则套用；游戏模式等需要即时生效的场景请走 <see cref="ApplyGameMode"/>。
    /// <para>
    /// <paramref name="gameModeActive"/> 由调用方提供（见接口注释）。它发生翻转时
    /// <b>绕过节流</b>立即扫描一次：节流的存在意义是省掉"亲和性设好后反复确认"的开销，
    /// 而游戏进出恰恰是最需要立刻响应的时刻 —— 等一个节流周期，表现为"切回桌面后
    /// 后台任务还要再被压几秒才恢复正常"。
    /// </para>
    /// </summary>
    public int ApplyRulesNow(OptimizationSettings settings, bool gameModeActive)
    {
        var now = DateTime.UtcNow;
        var edge = gameModeActive != _lastGameModeActive;
        if (!edge && (now - _lastScanUtc).TotalSeconds < ScanIntervalSeconds)
            return 0;

        _lastScanUtc = now;

        if (!settings.Enabled || !IsSupported)
        {
            // 总开关被关掉时也要撤销仅游戏规则的残留：否则用户以为"关了就不影响"，
            // 实际上进程还带着被压低的优先级在跑，而界面上没有任何线索指向本工具。
            RevertGameOnlyRules("optimization disabled");
            _lastGameModeActive = gameModeActive;
            return 0;
        }

        if (edge && !gameModeActive)
            RevertGameOnlyRules("game mode exit");

        _lastGameModeActive = gameModeActive;

        var active = settings.Rules
            .Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.ProcessName) && HasAction(r))
            .Where(r => !r.GameOnly || gameModeActive)
            .ToList();

        if (active.Count == 0)
        {
            _appliedByPid.Clear();
            return 0;
        }

        var byName = new Dictionary<string, ProcessOptimizationRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in active)
            byName[SystemCriticalProcesses.Normalize(rule.ProcessName)] = rule;

        var appliedCount = 0;
        var seen = new HashSet<int>();

        foreach (var process in Process.GetProcesses())
        {
            // rule / pid 必须在 try 之外声明：try 块内的局部变量出了块就失效，
            // 而后续的套用逻辑要用到它们。
            ProcessOptimizationRule? rule = null;
            int pid;
            try
            {
                pid = process.Id;
                var name = SystemCriticalProcesses.Normalize(process.ProcessName);
                if (!byName.TryGetValue(name, out rule))
                    continue;
            }
            catch
            {
                continue;
            }
            finally
            {
                process.Dispose();
            }

            if (rule is null)
                continue;

            seen.Add(pid);
            var signature = Signature(rule);

            // 签名一致说明该进程当前状态就是规则要的状态，无需重复调用。
            if (_appliedByPid.TryGetValue(pid, out var previous) && previous == signature)
                continue;

            if (TryApplyRule(rule, pid, out var error))
            {
                _appliedByPid[pid] = signature;
                appliedCount++;

                // 记下"这次改动是仅游戏规则的产物"。规则由仅游戏改成常驻时要移除，
                // 否则退出游戏会把一条已经变成常驻的规则一起撤掉。
                if (rule.GameOnly)
                    _gameOnlyPids.Add(pid);
                else
                    _gameOnlyPids.Remove(pid);

                // 取证行：没有它，"规则到底有没有真的套上"就只能靠界面猜。
                // 重复套用已被上面的签名比对挡掉，因此这行不会刷屏 ——
                // 每个 (进程, 规则内容) 组合一生只写一次。
                OptimizationLog.Write(
                    $"applied pid={pid} {LastDetail}{(rule.GameOnly ? " [game-only]" : string.Empty)}");
            }
            else
            {
                // 失败也记签名：否则每个扫描周期都会重试同一条注定失败的规则并刷屏日志。
                // 代价是瞬时失败（进程刚创建、句柄尚未就绪）不会自动重试——
                // 对亲和性这类常驻设置，用户重新触发一次即可，比日志被淹没划算。
                _appliedByPid[pid] = "\u0001" + signature;
                if (previous != "\u0001" + signature)
                    LastDetail = $"{rule.ProcessName}: {error}";
            }
        }

        // 清理已退出进程的记录，避免 pid 复用后签名误判为"已套用"。
        // 仅游戏规则的 pid 一并摘掉：进程都没了，退出游戏时也没有恢复对象。
        foreach (var dead in _appliedByPid.Keys.Where(pid => !seen.Contains(pid)).ToList())
        {
            _appliedByPid.Remove(dead);
            _gameOnlyPids.Remove(dead);
        }

        return appliedCount;
    }

    public bool TryApplyRule(ProcessOptimizationRule rule, int processId, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(rule.ProcessName))
        {
            error = "规则未指定进程名";
            return false;
        }

        if (SystemCriticalProcesses.IsProtected(rule.ProcessName))
        {
            error = "属于系统关键进程，本工具拒绝套用规则";
            return false;
        }

        if (!HasAction(rule))
        {
            error = "规则未包含任何动作";
            return false;
        }

        var access = rule.KernelEnforced && !_jobAssigned.Contains(processId)
            ? ProcessOptimizationNative.AccessRequiredForJob
            : ProcessOptimizationNative.AccessRequired;

        var handle = ProcessOptimizationNative.OpenProcess(access, false, processId);
        if (handle == IntPtr.Zero)
        {
            error = _elevated
                ? $"无法打开进程（目标受保护或已退出，Win32={Marshal.GetLastWin32Error()}）"
                : $"无法打开进程（可能需管理员权限，Win32={Marshal.GetLastWin32Error()}）";
            return false;
        }

        var failures = new List<string>();
        var succeeded = new List<string>();

        try
        {
            // 1) 亲和性。
            if (rule.Affinity != CoreAffinityMode.None)
            {
                if (!_builder.TryBuild(rule.Affinity, rule.CustomAffinityMask, out var mask, out var reason))
                {
                    failures.Add(reason);
                }
                else if (!ProcessOptimizationNative.SetProcessAffinityMask(handle, mask))
                {
                    failures.Add($"设置亲和性失败（Win32={Marshal.GetLastWin32Error()}）");
                }
                else
                {
                    succeeded.Add(rule.Affinity == CoreAffinityMode.Custom
                        ? $"亲和性=自定义核心(0x{rule.CustomAffinityMask:X})"
                        : $"亲和性={OptimizationLabels.Describe(rule.Affinity)}");

                    // 2) 内核强制：作业对象限制由内核实施，进程自身无法改回。
                    if (rule.KernelEnforced && !_jobAssigned.Contains(processId)
                        && !TryEnforceByJob(mask, handle, processId, out var jobReason))
                    {
                        failures.Add(jobReason);
                    }
                }
            }

            // 3) 优先级。放在亲和性之后：万一优先级被拒，亲和性也已生效，规则不至于全无作用。
            if (AffinityMaskBuilder.ToPriorityClass(rule.Priority) is { } priorityClass)
            {
                if (ProcessOptimizationNative.SetPriorityClass(handle, priorityClass))
                    succeeded.Add($"优先级={OptimizationLabels.Describe(rule.Priority)}");
                else
                    failures.Add($"设置优先级失败（Win32={Marshal.GetLastWin32Error()}）");
            }

            // 4) I/O 优先级。走 ntdll 未文档化入口，失败只降级不影响其它动作。
            if (rule.LowerIoPriority)
            {
                var ioPriority = ProcessOptimizationNative.IoPriorityVeryLow;
                if (ProcessOptimizationNative.NtSetInformationProcess(
                        handle,
                        ProcessOptimizationNative.ProcessIoPriority,
                        ref ioPriority,
                        sizeof(int)) == 0)
                {
                    succeeded.Add("I/O=最低");
                }
                else
                {
                    failures.Add("设置 I/O 优先级失败");
                }
            }
        }
        finally
        {
            ProcessOptimizationNative.CloseHandle(handle);
        }

        LastDetail = $"{rule.ProcessName} → {string.Join("、", succeeded)}";

        if (failures.Count > 0)
        {
            error = string.Join("；", failures);
            return false;
        }

        return succeeded.Count > 0;
    }

    public void ApplyGameMode(OptimizationSettings settings, string? gameProcessName)
    {
        // 游戏退出：把之前钉住的进程放回全核心与标准优先级。
        if (string.IsNullOrWhiteSpace(gameProcessName))
        {
            if (_gamePid is { } pid)
            {
                RestoreDefaults(pid);
                _gamePid = null;
            }

            return;
        }

        if (!settings.Enabled || !settings.GameModeEnabled || !IsSupported)
            return;

        var rule = new ProcessOptimizationRule
        {
            ProcessName = gameProcessName,
            Affinity = settings.GameAffinity,
            CustomAffinityMask = settings.GameCustomAffinityMask,
            Priority = settings.GamePriority,
        };

        if (!HasAction(rule))
            return;

        foreach (var process in Process.GetProcessesByName(
                     gameProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                         ? gameProcessName[..^4]
                         : gameProcessName))
        {
            try
            {
                var pid = process.Id;
                if (TryApplyRule(rule, pid, out _))
                    _gamePid = pid;
            }
            catch
            {
                // 游戏可能正在退出，跳过。
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// 撤销「仅游戏时生效」规则留下的改动，把相关进程放回全核心 + 标准优先级。
    /// 无残留时是空操作，因此可以在每条可能的退出路径上放心调用。
    /// </summary>
    private void RevertGameOnlyRules(string reason)
    {
        if (_gameOnlyPids.Count == 0)
            return;

        foreach (var pid in _gameOnlyPids)
            RestoreDefaults(pid);

        OptimizationLog.Write($"game-only rules reverted: {_gameOnlyPids.Count} process(es) ({reason})");
        _gameOnlyPids.Clear();

        // 缓存一并清掉：下次进入游戏必须能重新套用，否则签名比对会把它们误判为"已套用"。
        _appliedByPid.Clear();
    }

    /// <summary>把进程恢复为全核心 + 标准优先级（尽力而为，失败静默）。</summary>
    private void RestoreDefaults(int processId)
    {
        if (!_builder.TryBuild(CoreAffinityMode.AllCores, out var mask, out _))
            return;

        var handle = ProcessOptimizationNative.OpenProcess(
            ProcessOptimizationNative.AccessRequired, false, processId);
        if (handle == IntPtr.Zero)
            return;

        try
        {
            ProcessOptimizationNative.SetProcessAffinityMask(handle, mask);
            ProcessOptimizationNative.SetPriorityClass(
                handle, ProcessOptimizationNative.NORMAL_PRIORITY_CLASS);
        }
        finally
        {
            ProcessOptimizationNative.CloseHandle(handle);
        }
    }

    /// <summary>用作业对象把亲和性升级为内核强制。句柄挂在服务上，进程退出前不能释放。</summary>
    private bool TryEnforceByJob(UIntPtr mask, IntPtr processHandle, int processId, out string reason)
    {
        reason = string.Empty;

        var job = ProcessOptimizationNative.CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            reason = $"创建作业对象失败（Win32={Marshal.GetLastWin32Error()}）";
            return false;
        }

        var limits = new ProcessOptimizationNative.JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            LimitFlags = ProcessOptimizationNative.JOB_OBJECT_LIMIT_AFFINITY,
            Affinity = mask,
        };

        var size = Marshal.SizeOf<ProcessOptimizationNative.JOBOBJECT_BASIC_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, false);
            if (!ProcessOptimizationNative.SetInformationJobObject(
                    job,
                    ProcessOptimizationNative.JobObjectBasicLimitInformation,
                    buffer,
                    (uint)size))
            {
                reason = $"配置作业对象失败（Win32={Marshal.GetLastWin32Error()}）";
                ProcessOptimizationNative.CloseHandle(job);
                return false;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        if (!ProcessOptimizationNative.AssignProcessToJobObject(job, processHandle))
        {
            // 最常见的原因是目标进程已隶属于某个作业（例如由调试器或启动器创建），
            // 此时内核拒绝再次归属。这属于环境限制，不是配置错误。
            reason = $"进程已在其它作业对象中，无法内核强制（Win32={Marshal.GetLastWin32Error()}）";
            ProcessOptimizationNative.CloseHandle(job);
            return false;
        }

        _jobs.Add(job);
        _jobAssigned.Add(processId);
        return true;
    }

    private static bool HasAction(ProcessOptimizationRule rule) =>
        (rule.Affinity == CoreAffinityMode.Custom
            ? rule.CustomAffinityMask != 0
            : rule.Affinity != CoreAffinityMode.None)
        || rule.Priority != ProcessPriorityLevel.None
        || rule.LowerIoPriority;

    private static string Signature(ProcessOptimizationRule rule) =>
        $"{(int)rule.Affinity}|0x{rule.CustomAffinityMask:X}|{(int)rule.Priority}|{rule.KernelEnforced}|{rule.LowerIoPriority}|{rule.GameOnly}";

    public void Dispose()
    {
        foreach (var job in _jobs)
            ProcessOptimizationNative.CloseHandle(job);

        _jobs.Clear();
        _jobAssigned.Clear();
    }
}
