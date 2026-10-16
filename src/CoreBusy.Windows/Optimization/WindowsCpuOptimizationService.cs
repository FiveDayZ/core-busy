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

    // 每项保存进程创建时间，PID 复用不能把旧进程的备份写到新进程。
    private sealed record ProcessState(long Created, UIntPtr? Affinity, uint? Priority, int? Io);
    private sealed class AppliedProcess(ProcessState original, string signature)
    {
        public ProcessState Original = original;
        public string Signature = signature;
        public IntPtr Job;
    }
    private readonly Dictionary<int, AppliedProcess> _rules = [];
    private readonly Dictionary<int, AppliedProcess> _games = [];
    private DateTime _lastScanUtc = DateTime.MinValue;
    private DateTime _lastGameScanUtc = DateTime.MinValue;
    private string _settingsSignature = "";
    private string _gameSignature = "";
    public string LastDetail { get; private set; } = "尚未套用进程规则";

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

    public int ApplyRulesNow(OptimizationSettings settings, bool gameModeActive)
    {
        var active = settings.Rules.Where(r => settings.Enabled && r.Enabled && HasAction(r)
            && !string.IsNullOrWhiteSpace(r.ProcessName) && (!r.GameOnly || gameModeActive))
            .GroupBy(r => SystemCriticalProcesses.Normalize(r.ProcessName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        var signature = $"{settings.Enabled}|{gameModeActive}|" + string.Join(";", active.Select(p => p.Key + ":" + Signature(p.Value)));
        var now = DateTime.UtcNow;
        if (signature == _settingsSignature && (now - _lastScanUtc).TotalSeconds < ScanIntervalSeconds)
            return 0;
        _lastScanUtc = now;
        _settingsSignature = signature;
        if (_gameSignature.Length == 0)
            foreach (var pid in _games.Keys.ToArray()) RestoreTracked(_games, pid);

        // 删除、停用、编辑规则都先撤销旧动作；恢复失败的备份保留到下一轮重试。
        foreach (var pair in _rules.ToArray())
        {
            try
            {
                using var process = FindProcess(pair.Key);
                if (process is null || !active.TryGetValue(process.ProcessName, out var rule)
                    || pair.Value.Signature != Signature(rule))
                    RestoreTracked(_rules, pair.Key);
            }
            catch (InvalidOperationException) { RestoreTracked(_rules, pair.Key); }
            catch (System.ComponentModel.Win32Exception) { RestoreTracked(_rules, pair.Key); }
        }
        if (!settings.Enabled || !IsSupported || active.Count == 0) return 0;

        var applied = 0;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (_games.ContainsKey(process.Id) || !active.TryGetValue(process.ProcessName, out var rule)) continue;
                    if (ApplyTracked(rule, process.Id, _rules, out var changed, out _) && changed) applied++;
                }
                catch (InvalidOperationException) { /* 枚举后已退出。 */ }
                catch (System.ComponentModel.Win32Exception) { /* 受保护进程不能读取名称。 */ }
            }
        }
        return applied;
    }

    public bool TryApplyRule(ProcessOptimizationRule rule, int processId, out string error)
        => ApplyTracked(rule, processId, _rules, out _, out error);

    private bool ApplyTracked(ProcessOptimizationRule rule, int pid, Dictionary<int, AppliedProcess> tracked,
        out bool changed, out string error)
    {
        changed = false;
        error = "";
        UIntPtr? affinity = null;
        var priority = AffinityMaskBuilder.ToPriorityClass(rule.Priority);
        if (string.IsNullOrWhiteSpace(rule.ProcessName) || SystemCriticalProcesses.IsProtected(rule.ProcessName))
            error = "目标为空或属于系统关键进程";
        else if (!HasAction(rule)) error = "规则未包含任何动作";
        else if (rule.Priority != ProcessPriorityLevel.None && priority is null) error = "优先级无效";
        else if (rule.GameOnly && rule.KernelEnforced) error = "仅游戏规则不支持内核强制亲和，请取消内核强制";
        else if (rule.KernelEnforced && rule.Affinity == CoreAffinityMode.None) error = "内核强制需要选择核心亲和性";
        else if (rule.Affinity != CoreAffinityMode.None)
        {
            if (_builder.TryBuild(rule.Affinity, rule.CustomAffinityMask, out var mask, out var reason)) affinity = mask;
            else error = reason;
        }
        if (error.Length > 0) { LastDetail = $"{rule.ProcessName}: {error}"; return false; }

        try
        {
            using var process = FindProcess(pid);
            if (process is null || !string.Equals(process.ProcessName, SystemCriticalProcesses.Normalize(rule.ProcessName), StringComparison.OrdinalIgnoreCase)
                || SystemCriticalProcesses.IsProtected(process.ProcessName))
            { error = "目标进程已退出或名称不匹配"; LastDetail = error; return false; }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        { error = "无法读取目标进程：" + ex.Message; LastDetail = error; return false; }
        var handle = ProcessOptimizationNative.OpenProcess(rule.KernelEnforced
            ? ProcessOptimizationNative.AccessRequiredForJob : ProcessOptimizationNative.AccessRequired, false, pid);
        if (handle == IntPtr.Zero)
        { error = $"无法打开进程（Win32={Marshal.GetLastWin32Error()}）"; LastDetail = $"{rule.ProcessName}: {error}"; return false; }
        try
        {
            var desired = new ProcessState(0, affinity, priority, rule.LowerIoPriority ? 0 : null);
            if (!TryRead(handle, desired, out var before))
            { error = "无法完整备份目标字段，未写入进程参数"; return false; }
            var signature = Signature(rule);
            if (tracked.TryGetValue(pid, out var previous))
            {
                if (previous.Original.Created != before.Created)
                {
                    // 旧 PID 已退出；只解除本工具持有的 Job 限制，不写新进程。
                    if (!ReleaseJob(previous)) { error = "旧作业限制未能解除"; return false; }
                    tracked.Remove(pid);
                    previous = null;
                }
                else if (previous.Signature != signature)
                {
                    if (!RestoreTracked(tracked, pid)) { error = "旧规则未能恢复，暂不套用新规则"; return false; }
                    previous = null;
                    if (!TryRead(handle, desired, out before)) { error = "无法备份新规则原值"; return false; }
                }
            }
            if (Matches(before, desired) && (!rule.KernelEnforced || previous is not null && previous.Job != IntPtr.Zero))
                return true;

            var entry = previous ?? new AppliedProcess(before, signature);
            // 整条规则事务化；捕获全部目标字段后才写，任一动作失败恢复本次调用前的值。
            var ok = WriteState(handle, desired);
            if (ok && rule.KernelEnforced && affinity is { } jobMask && entry.Job == IntPtr.Zero)
                ok = TryEnforceByJob(jobMask, handle, out entry.Job);
            ok = ok && TryRead(handle, desired, out var after) && Matches(after, desired);
            if (ok)
            {
                tracked[pid] = entry;
                changed = true;
                LastDetail = $"{rule.ProcessName}: 已套用并回读验证";
                return true;
            }
            var rolledBack = (previous is not null || ReleaseJob(entry)) & WriteState(handle, before);
            rolledBack = rolledBack && TryRead(handle, before, out var restored) && Matches(restored, before);
            if (!rolledBack)
            {
                entry.Signature = ""; // 保存恢复依据，下次先恢复，绝不把部分成功当成已完成。
                tracked[pid] = entry;
            }
            error = rolledBack ? "套用未完整成功，已回滚本次改动" : "套用和回滚未完整成功，已保留原值等待重试";
            LastDetail = $"{rule.ProcessName}: {error}";
            return false;
        }
        finally
        {
            if (error.Length > 0) LastDetail = $"{rule.ProcessName}: {error}";
            ProcessOptimizationNative.CloseHandle(handle);
        }
    }

    public void ApplyGameMode(OptimizationSettings settings, string? gameProcessName)
    {
        var rule = new ProcessOptimizationRule
        {
            ProcessName = gameProcessName ?? "", Affinity = settings.GameAffinity,
            CustomAffinityMask = settings.GameCustomAffinityMask, Priority = settings.GamePriority,
        };
        var enabled = settings.Enabled && settings.GameModeEnabled && !string.IsNullOrWhiteSpace(gameProcessName) && HasAction(rule);
        var signature = enabled ? gameProcessName!.ToUpperInvariant() + ":" + Signature(rule) : "";
        if (signature != _gameSignature || !enabled)
        {
            foreach (var pid in _games.Keys.ToArray()) RestoreTracked(_games, pid);
            _gameSignature = signature;
            _lastGameScanUtc = DateTime.MinValue;
        }
        if (!enabled) return;
        if ((DateTime.UtcNow - _lastGameScanUtc).TotalSeconds < ScanIntervalSeconds) return;
        _lastGameScanUtc = DateTime.UtcNow;
        foreach (var process in Process.GetProcessesByName(SystemCriticalProcesses.Normalize(gameProcessName!)))
        {
            using (process)
            {
                try
                {
                    // 游戏预设覆盖同进程的常驻规则，先恢复常驻原值，避免两套备份互相覆盖。
                    if (RestoreTracked(_rules, process.Id)) ApplyTracked(rule, process.Id, _games, out _, out _);
                }
                catch (InvalidOperationException) { /* 游戏恰好退出，下轮清理备份。 */ }
                catch (System.ComponentModel.Win32Exception ex) { LastDetail = ex.Message; }
            }
        }
    }

    private static Process? FindProcess(int pid)
    {
        try { return Process.GetProcessById(pid); }
        catch (ArgumentException) { return null; }
    }

    private static bool TryRead(IntPtr handle, ProcessState fields, out ProcessState state)
    {
        state = fields;
        if (!ProcessOptimizationNative.GetProcessTimes(handle, out var created, out _, out _, out _)) return false;
        UIntPtr? affinity = null; uint? priority = null; int? io = null;
        if (fields.Affinity is not null)
        {
            if (!ProcessOptimizationNative.GetProcessAffinityMask(handle, out var mask, out _)) return false;
            affinity = mask;
        }
        if (fields.Priority is not null)
        {
            var value = ProcessOptimizationNative.GetPriorityClass(handle);
            if (value == 0) return false;
            priority = value;
        }
        if (fields.Io is not null)
        {
            if (ProcessOptimizationNative.NtQueryInformationProcess(handle, ProcessOptimizationNative.ProcessIoPriority, out var value, sizeof(int), out _) != 0) return false;
            io = value;
        }
        state = new(created, affinity, priority, io);
        return true;
    }

    private static bool Matches(ProcessState current, ProcessState desired)
        => current.Affinity == desired.Affinity && current.Priority == desired.Priority && current.Io == desired.Io;

    private static bool WriteState(IntPtr handle, ProcessState state)
    {
        var ok = true;
        if (state.Affinity is { } mask) ok &= ProcessOptimizationNative.SetProcessAffinityMask(handle, mask);
        if (state.Priority is { } priority) ok &= ProcessOptimizationNative.SetPriorityClass(handle, priority);
        if (state.Io is { } io)
            ok &= ProcessOptimizationNative.NtSetInformationProcess(handle, ProcessOptimizationNative.ProcessIoPriority, ref io, sizeof(int)) == 0;
        return ok;
    }

    private bool RestoreTracked(Dictionary<int, AppliedProcess> tracked, int pid)
    {
        if (!tracked.TryGetValue(pid, out var entry)) return true;
        if (!ReleaseJob(entry)) { LastDetail = $"PID {pid}: 无法解除作业限制，保留备份"; return false; }
        var handle = ProcessOptimizationNative.OpenProcess(ProcessOptimizationNative.AccessRequired, false, pid);
        if (handle == IntPtr.Zero)
        {
            if (Marshal.GetLastWin32Error() == 87) { tracked.Remove(pid); return true; }
            LastDetail = $"PID {pid}: 无法打开进程恢复，保留备份";
            return false;
        }
        try
        {
            if (!ProcessOptimizationNative.GetProcessTimes(handle, out var created, out _, out _, out _)) return false;
            if (created != entry.Original.Created) { tracked.Remove(pid); return true; }
            if (!WriteState(handle, entry.Original) || !TryRead(handle, entry.Original, out var after) || !Matches(after, entry.Original))
            { LastDetail = $"PID {pid}: 恢复未完成，保留原值等待重试"; return false; }
            tracked.Remove(pid);
            LastDetail = $"PID {pid}: 已恢复改动前的进程参数";
            return true;
        }
        finally { ProcessOptimizationNative.CloseHandle(handle); }
    }

    private static bool SetJobAffinity(IntPtr job, UIntPtr? mask)
    {
        var limits = new ProcessOptimizationNative.JOBOBJECT_BASIC_LIMIT_INFORMATION
        { LimitFlags = mask is null ? 0 : ProcessOptimizationNative.JOB_OBJECT_LIMIT_AFFINITY, Affinity = mask ?? UIntPtr.Zero };
        var size = Marshal.SizeOf<ProcessOptimizationNative.JOBOBJECT_BASIC_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, false);
            return ProcessOptimizationNative.SetInformationJobObject(job, ProcessOptimizationNative.JobObjectBasicLimitInformation, buffer, (uint)size);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool TryEnforceByJob(UIntPtr mask, IntPtr handle, out IntPtr job)
    {
        job = ProcessOptimizationNative.CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return false;
        if (SetJobAffinity(job, mask) && ProcessOptimizationNative.AssignProcessToJobObject(job, handle)) return true;
        ProcessOptimizationNative.CloseHandle(job);
        job = IntPtr.Zero;
        return false;
    }

    private static bool ReleaseJob(AppliedProcess entry)
    {
        if (entry.Job == IntPtr.Zero) return true;
        // 关联仍存在时关闭最后一个句柄不会销毁 Job；必须先显式解除亲和性限制。
        if (!SetJobAffinity(entry.Job, null)) return false;
        ProcessOptimizationNative.CloseHandle(entry.Job);
        entry.Job = IntPtr.Zero;
        return true;
    }

    private static bool HasAction(ProcessOptimizationRule rule) =>
        rule.Affinity != CoreAffinityMode.None || rule.Priority != ProcessPriorityLevel.None || rule.LowerIoPriority;

    private static string Signature(ProcessOptimizationRule rule) =>
        $"{(int)rule.Affinity}|0x{rule.CustomAffinityMask:X}|{(int)rule.Priority}|{rule.KernelEnforced}|{rule.LowerIoPriority}|{rule.GameOnly}";

    public void Dispose()
    {
        foreach (var pid in _games.Keys.ToArray()) RestoreTracked(_games, pid);
        foreach (var pid in _rules.Keys.ToArray()) RestoreTracked(_rules, pid);
    }
}
