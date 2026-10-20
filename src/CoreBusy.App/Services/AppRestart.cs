namespace CoreBusy.App.Services;

using System.Diagnostics;
using System.Globalization;

internal static class AppRestart
{
    internal const string WaitArgument = "--restart-from";

    /// <param name="elevated">true = 走 UAC（runas）提权启动。</param>
    /// <param name="extraArgs">
    /// 追加的命令行参数（v1.38.0）：提权重启来自开机自启时需要把 --minimized 带过去，
    /// 否则用户确认 UAC 后反而弹出主窗口，与静默启动的预期相悖。
    /// </param>
    internal static ProcessStartInfo StartInfo(bool elevated, IReadOnlyList<string>? extraArgs = null)
    {
        var start = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? throw new InvalidOperationException("无法获取程序路径。"),
            UseShellExecute = true,
            Verb = elevated ? "runas" : string.Empty,
        };
        start.ArgumentList.Add(WaitArgument);
        start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        if (extraArgs is not null)
        {
            foreach (var arg in extraArgs)
                start.ArgumentList.Add(arg);
        }
        return start;
    }

    internal static bool WaitForPreviousProcess(string[] args, int timeoutMilliseconds = 30000)
    {
        var index = Array.IndexOf(args, WaitArgument);
        if (index < 0)
            return true;
        if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out var pid) || pid <= 0 || pid == Environment.ProcessId)
            return false;
        try
        {
            using var previous = Process.GetProcessById(pid);
            return previous.WaitForExit(timeoutMilliseconds);
        }
        catch (ArgumentException)
        {
            return true; // 旧进程已经退出。
        }
        catch (Exception ex)
        {
            AppLog.Write("restart wait", ex);
            return false;
        }
    }
}
