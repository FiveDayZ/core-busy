namespace CoreBusy.Windows.Monitoring;

using System.Diagnostics;
using CoreBusy.Core.Interfaces;
using CoreBusy.Core.Models;

/// <summary>
/// 基于 Process.TotalProcessorTime 增量的进程 CPU 排行。
/// 缓存 Process 句柄避免反复枚举；进程列表每 30 秒刷新一次。
/// 百分比按"占全部逻辑处理器总容量的百分比"折算（与任务管理器口径一致）。
/// </summary>
public sealed class ProcessCpuRanker : IProcessCpuRanker
{
    private readonly int _logicalProcessorCount;
    private readonly Dictionary<int, (Process Process, TimeSpan LastCpuTime)> _tracked = new();
    private DateTime _lastSample = DateTime.UtcNow;
    private DateTime _lastRefresh = DateTime.MinValue;

    public ProcessCpuRanker(int logicalProcessorCount)
    {
        _logicalProcessorCount = Math.Max(1, logicalProcessorCount);
    }

    public List<ProcessCpuSample> SampleTop(int top)
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastSample).TotalSeconds;
        _lastSample = now;

        if (elapsed <= 0)
            return new List<ProcessCpuSample>();

        try
        {
            if ((now - _lastRefresh).TotalSeconds >= 30)
            {
                RefreshProcesses();
                _lastRefresh = now;
            }

            var samples = new List<ProcessCpuSample>();
            var dead = new List<int>();

            foreach (var (pid, entry) in _tracked)
            {
                TimeSpan cpuTime;
                try
                {
                    cpuTime = entry.Process.TotalProcessorTime;
                }
                catch
                {
                    // 进程已退出或权限不足，移除跟踪。
                    dead.Add(pid);
                    continue;
                }

                var delta = cpuTime - entry.LastCpuTime;
                if (delta < TimeSpan.Zero)
                    continue;

                _tracked[pid] = (entry.Process, cpuTime);

                var percent = delta.TotalSeconds / (elapsed * _logicalProcessorCount) * 100.0;
                if (percent >= 0.1)
                {
                    string name;
                    try
                    {
                        name = entry.Process.ProcessName + ".exe";
                    }
                    catch
                    {
                        name = $"pid:{pid}";
                    }

                    samples.Add(new ProcessCpuSample(name, percent));
                }
            }

            foreach (var pid in dead)
            {
                try { _tracked[pid].Process.Dispose(); } catch { }
                _tracked.Remove(pid);
            }

            return samples
                .OrderByDescending(x => x.CpuPercent)
                .Take(Math.Max(1, top))
                .ToList();
        }
        catch
        {
            // 系统调用失败按空样本处理，不打断主循环。
            return new List<ProcessCpuSample>();
        }
    }

    private void RefreshProcesses()
    {
        foreach (var entry in _tracked.Values)
        {
            try { entry.Process.Dispose(); } catch { }
        }

        _tracked.Clear();

        foreach (var process in Process.GetProcesses())
        {
            TimeSpan cpuTime;
            try
            {
                cpuTime = process.TotalProcessorTime;
            }
            catch
            {
                process.Dispose();
                continue;
            }

            _tracked[process.Id] = (process, cpuTime);
        }
    }
}
