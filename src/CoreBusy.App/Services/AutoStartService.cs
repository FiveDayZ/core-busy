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
/// <para>
/// v1.38.0 起写入的命令行带 <see cref="SilentArgument"/>（--minimized）：
/// 开机后直接静默运行在托盘，不弹出主界面 —— 开机自启要的是"采着数就行"，
/// 弹窗反而打断用户（托盘图标保留，双击随时打开；点一次图标即可确认它活着）。
/// 手动双击 exe 启动不受影响，照常显示主界面。
/// </para>
/// </summary>
public static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CORE-BUSY";

    /// <summary>静默启动开关参数：App.OnStartup 识别后把主窗口藏进托盘。</summary>
    public const string SilentArgument = "--minimized";

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
    /// 注册 / 取消开机启动。命令行带引号写入完整 exe 路径（路径含空格时必须带引号）+ 静默启动参数。
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

                key.SetValue(ValueName, $"\"{exePath}\" {SilentArgument}");
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

    /// <summary>
    /// 旧版 Run 值原地迁移（v1.38.0）：已注册开机启动的值不带静默参数（老格式只有 exe 路径），
    /// 启动时顺手补上 —— 已注册用户重启一次应用即获得静默启动，不必关掉再重新勾选。
    /// 值缺失或已带参数时不动。
    /// </summary>
    public static void EnsureSilentArg()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(ValueName) is not string path || path.Length == 0)
                return;
            if (path.Contains(SilentArgument, StringComparison.Ordinal))
                return;

            key.SetValue(ValueName, $"{path} {SilentArgument}");
        }
        catch (Exception)
        {
            // 迁移失败保持旧值：开机照常弹出主窗口，下次启动再补，不影响数据正确性。
        }
    }
}
