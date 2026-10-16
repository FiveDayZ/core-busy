namespace CoreBusy.App.Services;

using System.Diagnostics;
using System.Globalization;

internal static class AppRestart
{
    private const string WaitArgument = "--restart-from";

    internal static ProcessStartInfo StartInfo(bool elevated)
    {
        var start = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? throw new InvalidOperationException("无法获取程序路径。"),
            UseShellExecute = true,
            Verb = elevated ? "runas" : string.Empty,
        };
        start.ArgumentList.Add(WaitArgument);
        start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
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
