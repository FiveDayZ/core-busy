namespace CoreBusy.App.Services;

using System.IO;

/// <summary>只记录故障；不记录设置、Token 或遥测载荷。</summary>
internal static class AppLog
{
    private static readonly object Gate = new();

    internal static void Write(string area, object error)
    {
        try
        {
            lock (Gate)
            {
                var directory = Path.Combine(EnergyHistoryStore.DataDirectory, "logs");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "errors.log");
                // ponytail: 仅保留当前及上一份 1 MiB 日志；需要集中诊断时再引入日志服务。
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                    File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} [{area}] {error}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // 日志目录也可能不可写，诊断失败不能替换原异常。
        }
    }
}
