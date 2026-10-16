namespace CoreBusy.App.Services;

using System.IO;
using System.Text.Json;
using CoreBusy.App.Infrastructure;
using CoreBusy.Core.Models;

/// <summary>应用设置 JSON 持久化（%APPDATA%\CORE-BUSY\settings.json）。</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string DirectoryPath => EnergyHistoryStore.DataDirectory;

    private static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    /// <summary>加载设置；文件缺失或损坏时返回默认值（默认值不落盘，首次保存时写入）。</summary>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(JsonFile.ReadAllText(FilePath));
                if (settings is not null)
                    return settings;
            }
        }
        catch (Exception)
        {
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            JsonFile.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception ex)
        {
            AppLog.Write("settings save", ex);
        }
    }
}
