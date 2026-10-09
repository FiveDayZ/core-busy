namespace CoreBusy.App.Services;

using Microsoft.Win32;

/// <summary>
/// 开机自动启动（v1.26.5）：当前用户的注册表 Run 项
/// （<c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>）。
/// <para>
/// 选 HKCU Run 而非任务计划/服务：无需管理员权限、用户可在任务管理器"启动"页自行开关、
/// 卸载只需删一个注册表值。注册表是**唯一事实来源**（settings.json 不记这份状态）——
/// 用户在任务管理器里手动禁用/删除后，设置页读到的就是真实状态，不会出现
/// "界面说开机启动、实际没有"的两张皮。
/// </para>
/// </summary>
public static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CORE-BUSY";

    /// <summary>当前是否已注册开机启动（以注册表实况为准）。</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string path && path.Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 注册 / 取消开机启动。命令行带引号写入完整 exe 路径（路径含空格时必须带引号）。
    /// </summary>
    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null)
                return;

            if (enabled)
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                    return; // 单文件发布外异常宿主拿不到路径，宁可不注册也不写坏值。

                key.SetValue(ValueName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception)
        {
            // 注册表写入失败（权限/杀软拦截）静默：设置页下次打开按注册表实况回显，
            // 不出现"界面已勾选、实际未生效"的假状态。
        }
    }
}
