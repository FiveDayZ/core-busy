namespace CoreBusy.App.Services;

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>小型 JSON 账本：同目录原子替换、保留上一份副本，并按路径串行化合并写入。</summary>
internal static class JsonFile
{
    internal static string MutexName(string prefix, string path)
    {
        var canonical = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        return @"Local\CORE-BUSY." + prefix + "." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    internal static void WithLock(string path, Action action)
    {
        using var mutex = new Mutex(false, MutexName("Json", path));
        bool owned;
        try { owned = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
            throw new TimeoutException("账本正被另一进程写入：" + Path.GetFileName(path));
        try { action(); }
        finally { mutex.ReleaseMutex(); }
    }

    internal static string ReadAllText(string path)
    {
        try { return ReadValidJson(path); }
        catch (Exception ex) when ((ex is IOException or JsonException) && File.Exists(path + ".bak"))
        {
            AppLog.Write("JSON recovery", $"{Path.GetFileName(path)}: {ex.GetType().Name}; using backup");
            return ReadValidJson(path + ".bak");
        }
    }

    private static string ReadValidJson(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        using var document = JsonDocument.Parse(text);
        return text;
    }

    internal static void WriteAllText(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path))
                File.Replace(temporary, path, path + ".bak");
            else
                File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
