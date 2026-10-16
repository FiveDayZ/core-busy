namespace CoreBusy.Core.Models;

using System.Diagnostics;

/// <summary>同一轮采样的核级和整机读数；时间戳使用单调时钟，0 表示尚未取得采样。</summary>
public sealed record CpuMonitorFrame(CpuSnapshot Cpu, IReadOnlyList<CpuCoreSnapshot> Cores)
{
    public long CapturedTimestamp { get; init; } = Stopwatch.GetTimestamp();
    public double AgeSeconds => CapturedTimestamp == 0 ? double.PositiveInfinity
        : Stopwatch.GetElapsedTime(CapturedTimestamp).TotalSeconds;
    public bool IsFresh(TimeSpan interval) => AgeSeconds is >= 0 && AgeSeconds <= Math.Max(1, interval.TotalSeconds * 3);
}
