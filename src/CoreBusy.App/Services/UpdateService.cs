namespace CoreBusy.App.Services;

using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using CoreBusy.App.Views;

/// <summary>更新检查结果分类。</summary>
public enum UpdateCheckOutcome
{
    /// <summary>检查不可用（网络失败 / 接口异常 / 资产缺失），Detail 说明原因。</summary>
    Unavailable,

    /// <summary>远端版本不高于当前，已是最新。</summary>
    UpToDate,

    /// <summary>发现更新的 Release。</summary>
    UpdateAvailable,
}

/// <summary>远端 Release 的资产描述。</summary>
public sealed record UpdateInfo(
    string Tag,
    Version Version,
    string AssetName,
    string AssetUrl,
    long AssetSize,
    string? AssetDigest);

/// <summary>检查结果（Unavailable 时 Detail 带原因；Info 仅在 UpdateAvailable 时非空）。</summary>
public readonly record struct UpdateCheckResult(
    UpdateCheckOutcome Outcome,
    UpdateInfo? Info,
    string Detail);

/// <summary>下载进度（Percent 0–100；Detail 是 "x.x MB / y.y MB" 形式的字节计数文本）。</summary>
public readonly record struct UpdateDownloadProgress(double Percent, string Detail);

/// <summary>
/// GitHub Release 更新服务（v1.31.0）。
/// <para>
/// 检查：匿名 GET <c>repos/FiveDayZ/core-busy/releases/latest</c>（仓库公开，无需令牌；
/// GitHub API 强制要求 User-Agent）。比较 <c>tag_name</c> 与当前程序集版本。
/// </para>
/// <para>
/// 安装：下载对应变体的 zip（完整版 / 精简版按当前 exe 体积判别），sha256 对账
/// （GitHub API 的 assets[].digest，与 make_release.ps1 发版后回读的 digest 同源），
/// 解出新 exe 暂存到 %TEMP%\CORE-BUSY-update\&lt;tag&gt;\，并写一份自换版脚本；
/// 用户确认后本进程退出、脚本接管：等 exe 锁释放 → 改名旧版 → 拷入新版 → 重启。
/// </para>
/// </summary>
public static class UpdateService
{
    private const string Repo = "FiveDayZ/core-busy";
    private const string LatestApiUrl = "https://api.github.com/repos/" + Repo + "/releases/latest";

    /// <summary>Releases 页地址（设置页「查看发布页」按钮与检查提示共用）。</summary>
    public const string ReleasesPageUrl = "https://github.com/" + Repo + "/releases";

    /// <summary>
    /// 调试预置（思路同 <c>MockCpuMonitorService.MockCpuEnvVar</c>）：设置该环境变量后
    /// 不访问网络，直接以指定 tag 报告"发现新版本"，便于在没有新 Release 的机器上验证
    /// 更新 UI 链路。发布版不做 UI 入口，对普通用户零影响。
    /// </summary>
    public const string SimulateTagEnvVar = "COREBUSY_UPDATE_SIM_TAG";

    /// <summary>当前运行的版本（单点来自 csproj &lt;Version&gt; → 程序集版本）。</summary>
    public static Version CurrentVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    /// <summary>当前版本的展示文本（三段式，如 "v1.31.0"）。</summary>
    public static string CurrentVersionText => "v" + CurrentVersion.ToString(3);

    /// <summary>
    /// 变体判别：完整版单文件 exe 自包含运行时（约 159 MB），精简版只有个位数 MB。
    /// 按当前 exe 体积判别比"读某标记文件"更可靠 —— 两个变体是同一份代码的两种发布形态。
    /// </summary>
    public static bool IsLiteVariant { get; } = DetectLiteVariant();

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = System.Net.DecompressionMethods.None,
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        // GitHub API 无 User-Agent 直接 403；Accept 按官方文档要求声明版本化媒体类型。
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CORE-BUSY-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static bool DetectLiteVariant()
    {
        try
        {
            var path = Environment.ProcessPath;
            return string.IsNullOrEmpty(path) || new FileInfo(path).Length < 64L * 1024 * 1024;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>检查 GitHub latest Release。任何失败都返回 Unavailable，绝不抛出到调用方。</summary>
    public static async Task<UpdateCheckResult> CheckLatestAsync(CancellationToken ct = default)
    {
        // 调试模拟：不做任何网络访问，直接构造"发现新版本"。
        var sim = Environment.GetEnvironmentVariable(SimulateTagEnvVar);
        if (!string.IsNullOrWhiteSpace(sim))
        {
            var simVersion = ParseVersion(sim);
            return simVersion is null
                ? new UpdateCheckResult(UpdateCheckOutcome.Unavailable, null, $"模拟 tag 无法解析：{sim}")
                : new UpdateCheckResult(
                    UpdateCheckOutcome.UpdateAvailable,
                    new UpdateInfo(sim, simVersion, AssetName(sim, IsLiteVariant),
                        $"https://github.com/{Repo}/releases/tag/{sim}", 0, null),
                    string.Empty);
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(12));

            using var resp = await Http.GetAsync(LatestApiUrl, timeoutCts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new UpdateCheckResult(
                    UpdateCheckOutcome.Unavailable, null, $"GitHub 返回 {(int)resp.StatusCode}（{resp.ReasonPhrase}）");

            await using var stream = await resp.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeoutCts.Token).ConfigureAwait(false);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? string.Empty : string.Empty;
            var remote = ParseVersion(tag);
            if (remote is null)
                return new UpdateCheckResult(UpdateCheckOutcome.Unavailable, null, $"无法解析版本号：{tag}");
            if (remote <= CurrentVersion)
                return new UpdateCheckResult(UpdateCheckOutcome.UpToDate, null, string.Empty);

            var wanted = AssetName(tag, IsLiteVariant);
            string? assetUrl = null, assetDigest = null, assetName = null;
            long assetSize = 0;

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    var isMatch = string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase);
                    var isZip = name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
                    if (!isMatch && !(isZip && assetUrl is null))
                        continue; // 首选变体优先；缺失时回落到任意一份 zip（变体可能变化，仍可升级）

                    assetName = name;
                    assetUrl = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    assetSize = asset.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number
                        ? s.GetInt64()
                        : 0;
                    assetDigest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;

                    if (isMatch)
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(assetUrl))
                return new UpdateCheckResult(
                    UpdateCheckOutcome.Unavailable, null, $"Release {tag} 未找到安装包（{wanted}）");

            return new UpdateCheckResult(
                UpdateCheckOutcome.UpdateAvailable,
                new UpdateInfo(tag, remote, assetName!, assetUrl, assetSize, assetDigest),
                string.Empty);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new UpdateCheckResult(UpdateCheckOutcome.Unavailable, null, "检查超时（12 秒无响应）");
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(UpdateCheckOutcome.Unavailable, null, ex.Message);
        }
    }

    /// <summary>对应变体的发布包名（与 scripts/build-release.py 的命名约定一致）。</summary>
    public static string AssetName(string tag, bool lite) =>
        lite ? $"CoreBusy-{tag}-win-x64-lite.zip" : $"CoreBusy-{tag}-win-x64.zip";

    /// <summary>
    /// 下载 → sha256 对账 → 解出新 exe 暂存 → 写自换版脚本。返回暂存目录。
    /// </summary>
    public static async Task<string> DownloadAndStageAsync(
        UpdateInfo info,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken ct)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "CORE-BUSY-update");
        var stageDir = Path.Combine(workDir, info.Tag);
        Directory.CreateDirectory(stageDir);

        var zipPath = Path.Combine(stageDir, info.AssetName);
        try
        {
            using var resp = await Http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength ?? info.AssetSize;
            long received = 0;
            var sinceReport = Stopwatch.StartNew();

            await using var source = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var target = new FileStream(
                zipPath, FileMode.Create, FileAccess.Write, FileShare.None,
                1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);

            var buffer = new byte[1 << 16];
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;

                // 100ms 节流：Progress<T> 每次回调都要排进 UI 消息队列，逐块上报没有意义。
                if (sinceReport.ElapsedMilliseconds >= 100)
                {
                    sinceReport.Restart();
                    Report(progress, received, total);
                }
            }

            Report(progress, received, total);
        }
        catch (Exception)
        {
            TryDelete(zipPath);
            throw;
        }

        // sha256 对账：digest 由 GitHub 服务端计算（assets[].digest），与发版脚本回读的判据同源。
        // 磁盘空间不足 / 代理劫持 / 下载中断都会在这里被拦下，绝不把坏包换进安装目录。
        if (!string.IsNullOrWhiteSpace(info.AssetDigest)
            && info.AssetDigest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            var expected = info.AssetDigest["sha256:".Length..].Trim();
            var actual = Convert.ToHexString(
                SHA256.HashData(await File.ReadAllBytesAsync(zipPath, ct).ConfigureAwait(false)));
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(zipPath);
                throw new IOException("安装包 sha256 校验失败（下载不完整或被篡改），已放弃安装。");
            }
        }

        // 发布包里只有一个文件：CoreBusy.App.exe（build-release.py 的打包约定）。
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var entry = zip.GetEntry("CoreBusy.App.exe")
                ?? throw new IOException("安装包内缺少 CoreBusy.App.exe。");
            entry.ExtractToFile(Path.Combine(stageDir, "CoreBusy.App.exe"), overwrite: true);
        }

        TryDelete(zipPath); // 校验与解压完成后立即释放（完整版 zip 有 60+ MB）
        WriteUpdateScript(Path.Combine(stageDir, "CORE-BUSY-update.cmd"));
        return stageDir;
    }

    /// <summary>启动暂存的自换版脚本；调用方随即退出本进程，脚本在锁释放后完成换版并重启。</summary>
    internal static bool CanInstall(string installDir, string stageDir)
    {
        if (!Directory.Exists(installDir) || !File.Exists(Path.Combine(installDir, "CoreBusy.App.exe"))
            || !File.Exists(Path.Combine(stageDir, "CoreBusy.App.exe"))) return false;
        var probe = Path.Combine(installDir, ".corebusy-update-" + Guid.NewGuid().ToString("N") + ".tmp");
        try { using (File.Create(probe)) { } return true; }
        catch (Exception ex) { AppLog.Write("update preflight", ex); return false; }
        finally { TryDelete(probe); }
    }

    public static bool LaunchStagedUpdate(string stageDir)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
            return false;

        var script = Path.Combine(stageDir, "CORE-BUSY-update.cmd");
        if (!File.Exists(script))
            return false;

        var installDir = Path.GetDirectoryName(exePath);
        if (string.IsNullOrWhiteSpace(installDir))
            return false;

        if (!CanInstall(installDir, stageDir)) return false;

        // cmd /c 接多段带引号参数的标准写法：整体再加一层引号。
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c \"\"" + script + "\" \"" + installDir + "\" \"" + stageDir + "\" " + Environment.ProcessId + "\"",
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi);
        return process is not null;
    }

    /// <summary>
    /// 带进度对话框的下载 + 校验，完成后询问是否立即重启安装。
    /// 返回 true 表示已启动换版脚本，调用方应退出本进程（脚本会等锁释放后换 exe 并重启）。
    /// </summary>
    public static async Task<bool> DownloadApplyWithUiAsync(UpdateInfo info, Window? owner)
    {
        var dialog = new UpdateProgressDialog(owner);
        var cts = new CancellationTokenSource();
        dialog.CancelRequested += (_, _) => cts.Cancel();

        // Progress<T> 在创建时捕获 UI 上下文：下载在后台流式进行，回调由 UI 消息泵消费。
        var progress = new Progress<UpdateDownloadProgress>(dialog.Report);

        string stageDir;
        try
        {
            stageDir = await DownloadAndStageAsync(info, progress, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            dialog.Close();
            return false; // 用户主动取消，不打扰
        }
        catch (Exception ex)
        {
            dialog.Close();
            MessageBox.Show(owner, $"更新下载失败：{ex.Message}", "CORE-BUSY 更新",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        dialog.Close();

        if (MessageBox.Show(
                owner,
                $"新版本 {info.Tag} 已下载并通过校验。\n\n是否立即重启并完成安装？",
                "CORE-BUSY 更新",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes)
        {
            return false;
        }

        if (!LaunchStagedUpdate(stageDir))
        {
            MessageBox.Show(owner, "安装目录不可写、安装包不完整或脚本启动失败，本次未退出程序。", "CORE-BUSY 更新",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        return true;
    }

    /// <summary>暂存脚本内容（纯 ASCII，路径一律经 %1/%2 传入，规避批处理的编码坑）。</summary>
    private static void WriteUpdateScript(string path, int maxAttempts = 30)
    {
        if (maxAttempts is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        const string script = """
@echo off
setlocal
set "INSTALL=%~1"
set "STAGED=%~2"
set "EXE=%INSTALL%\CoreBusy.App.exe"
set "BAK=%INSTALL%\CoreBusy.App.exe.old"
set "LOG=%STAGED%\update.log"
set /a ATTEMPTS=0
if not exist "%INSTALL%\" goto failed
if not exist "%EXE%" goto failed
if not exist "%STAGED%\CoreBusy.App.exe" goto failed
rem Wait for the original process to release both its files and the single-instance mutex.
if "%~3"=="" goto waitlock
powershell.exe -NoLogo -NoProfile -NonInteractive -Command "$p=Get-Process -Id %~3 -ErrorAction SilentlyContinue; if ($p -and -not $p.WaitForExit(__WAIT_MS__)) { exit 1 }; exit 0"
if errorlevel 1 goto failed
:waitlock
set /a ATTEMPTS+=1
if exist "%BAK%\" goto failed
if exist "%BAK%" del /q "%BAK%" >nul 2>&1
ren "%EXE%" "CoreBusy.App.exe.old" >nul 2>&1
if not errorlevel 1 goto install
if %ATTEMPTS% GEQ __MAX_ATTEMPTS__ goto failed
ping -n 2 127.0.0.1 >nul
goto waitlock
:install
copy /y "%STAGED%\CoreBusy.App.exe" "%EXE%" >nul 2>&1
if errorlevel 1 goto rollback
start "" /d "%INSTALL%" "%EXE%"
if errorlevel 1 goto rollback
del "%BAK%" >nul 2>&1
exit /b 0
:rollback
rem A failed copy can leave a partial destination; remove it before restoring the old EXE.
if exist "%EXE%" del "%EXE%" >nul 2>&1
ren "%BAK%" "CoreBusy.App.exe" >nul 2>&1
if errorlevel 1 goto failed
start "" /d "%INSTALL%" "%EXE%"
:failed
> "%LOG%" echo CORE-BUSY update failed. The original program has not been intentionally removed.
>> "%LOG%" echo Check folder permissions, running processes and free disk space. Retry or download the ZIP manually.
if not "%COREBUSY_UPDATE_SILENT%"=="1" start "" notepad.exe "%LOG%"
exit /b 1
""";
        File.WriteAllText(path, script.Replace("__MAX_ATTEMPTS__", maxAttempts.ToString())
            .Replace("__WAIT_MS__", (maxAttempts * 1000).ToString()));
    }

    private static void Report(IProgress<UpdateDownloadProgress>? progress, long received, long total)
    {
        if (progress is null)
            return;

        var percent = total > 0 ? Math.Clamp(received * 100.0 / total, 0, 100) : 0;
        progress.Report(new UpdateDownloadProgress(
            percent,
            $"{received / 1048576.0:0.0} MB / {(total > 0 ? total / 1048576.0 : 0):0.0} MB"));
    }

    private static Version? ParseVersion(string tag)
    {
        var text = tag.TrimStart('v', 'V');
        return Version.TryParse(text, out var version) ? version : null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
        }
    }
}
