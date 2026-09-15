namespace CoreBusy.App.Infrastructure;

using System.IO;

/// <summary>轻量运行日志：写入 exe 同级 logs/debug.log，用于取证窗口生命周期与未处理异常。</summary>
public static class AppLog
{
    private static readonly object Lock = new();

    public static void Write(string message)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}";
            lock (Lock)
                File.AppendAllText(Path.Combine(dir, "debug.log"), line);
        }
        catch
        {
            // 日志失败不影响主流程。
        }
    }
}
