namespace CoreBusy.App.Services;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// 机器指纹：本机稳定特征的单向哈希（v1.34.0）。
/// </summary>
/// <remarks>
/// <para>
/// 种子 = Windows 安装标识（HKLM\Cryptography\MachineGuid，装系统时生成、重启不变）
/// + 机器名。SHA-256 后截 16 hex —— <b>只上报哈希，不上报明文</b>：
/// 后台只需要"同一台机器"这个等价关系，不需要知道机器叫什么。
/// </para>
/// <para>
/// MachineGuid 读不到（极端的权限/精简系统）时退化为纯机器名哈希；
/// 两台同名且同硬件的机器会共用一个指纹 —— 对个人机群的用途可接受，且宁可退化也不引入 WMI 依赖。
/// </para>
/// </remarks>
public static class MachineFingerprint
{
    private const string MachineGuidPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography";
    private const string MachineGuidValue = "MachineGuid";

    public static string Compute()
    {
        var guid = Microsoft.Win32.Registry.GetValue(MachineGuidPath, MachineGuidValue, null) as string ?? string.Empty;
        var seed = $"{guid}|{Environment.MachineName}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        Span<char> hex = stackalloc char[16];
        for (var i = 0; i < 8; i++)
            hash[i].TryFormat(hex[(i * 2)..], out _, "x2");
        return new string(hex);
    }
}

/// <summary>单核等级快照（心跳载荷的 coresGrade 数组元素）。</summary>
public sealed record TelemetryCoreGrade
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>P = 性能核，E = 能效核，S = 单一架构（非混合平台）。</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "S";

    /// <summary>养成等级（EXP 曲线，跨进程账本口径）。</summary>
    [JsonPropertyName("level")]
    public int? Level { get; init; }

    /// <summary>养成阶段（UiTheme.StageIndexOf 同一阈值折算，0–4）。服务端只存不判，边界单源在客户端。</summary>
    [JsonPropertyName("stage")]
    public int? Stage { get; init; }

    /// <summary>健康度综合分（0–100）；null = 尚不能判定（沿用全线"不拿 0 冒充"约定）。</summary>
    [JsonPropertyName("score")]
    public double? Score { get; init; }
}

/// <summary>心跳载荷。非有限值在上报前已转 null（JSON 不接受 NaN）。</summary>
public sealed class TelemetryPayload
{
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; init; } = string.Empty;

    [JsonPropertyName("alias")]
    public string Alias { get; init; } = string.Empty;

    [JsonPropertyName("cpuModel")]
    public string CpuModel { get; init; } = string.Empty;

    [JsonPropertyName("coresPhysical")]
    public int CoresPhysical { get; init; }

    [JsonPropertyName("coresLogical")]
    public int CoresLogical { get; init; }

    [JsonPropertyName("hybrid")]
    public bool Hybrid { get; init; }

    [JsonPropertyName("os")]
    public string Os { get; init; } = string.Empty;

    [JsonPropertyName("appVersion")]
    public string AppVersion { get; init; } = string.Empty;

    [JsonPropertyName("runtimeTotalSeconds")]
    public double? RuntimeTotalSeconds { get; init; }

    [JsonPropertyName("energyTotalWh")]
    public double? EnergyTotalWh { get; init; }

    [JsonPropertyName("powerLastW")]
    public double? PowerLastW { get; init; }

    [JsonPropertyName("powerPeakW")]
    public double? PowerPeakW { get; init; }

    [JsonPropertyName("coresGrade")]
    public List<TelemetryCoreGrade>? CoresGrade { get; set; }
}

/// <summary>注册载荷（v1.35.0）：注册码兑换设备 Token 时上报的最小信息集。</summary>
public sealed class TelemetryEnrollPayload
{
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; init; } = string.Empty;

    [JsonPropertyName("alias")]
    public string? Alias { get; init; }

    [JsonPropertyName("cpuModel")]
    public string? CpuModel { get; init; }
}

/// <summary>
/// 遥测心跳上报（v1.34.0）。任何失败都静默 —— 遥测绝不允许影响监控主流程，
/// 丢一次心跳无害（下个 10 分钟周期带最新累计总量覆盖，总量幂等无需补报队列）。
/// </summary>
public static class TelemetryReporter
{
    private static readonly System.Net.Http.HttpClient Http = CreateClient();

    private static System.Net.Http.HttpClient CreateClient()
    {
        var handler = new System.Net.Http.SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        var client = new System.Net.Http.HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CORE-BUSY-Telemetry");
        return client;
    }

    /// <summary>最近一次失败的摘要（取证用；发布版无界面消费，但保留比彻底静默有价值）。</summary>
    public static string? LastError { get; private set; }

    /// <summary>
    /// 发送一次心跳。成功 = 2xx；网络失败 / 非法地址 / 服务器拒绝一律返回 false 且不抛出。
    /// 12 秒硬超时：上报线程在后台，但退出时的 best-effort 补发要等它，不能无限挂。
    /// </summary>
    public static async Task<bool> ReportAsync(TelemetryPayload payload, string url, string token)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, url)
            {
                Content = new System.Net.Http.StringContent(json, Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrEmpty(token))
                request.Headers.TryAddWithoutValidation("X-Token", token);

            using var response = await Http.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return true;

            LastError = $"HTTP {(int)response.StatusCode}";
            return false;
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 注册制（v1.35.0）：用一次性注册码（X-Invite 头）向 hub 兑换设备专属 Token。
    /// 成功返回 Token 文本；失败返回 null 并记 LastError（与心跳同一静默纪律，绝不抛出）。
    /// enrollUrl 形如 http://host:8400/api/enroll。
    /// </summary>
    public static async Task<string?> EnrollAsync(string enrollUrl, string invite, TelemetryEnrollPayload payload)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, enrollUrl)
            {
                Content = new System.Net.Http.StringContent(json, Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrEmpty(invite))
                request.Headers.TryAddWithoutValidation("X-Invite", invite);

            using var response = await Http.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LastError = $"HTTP {(int)response.StatusCode}";
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);
            var token = doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                        && doc.RootElement.TryGetProperty("token", out var t)
                        && t.ValueKind == System.Text.Json.JsonValueKind.String
                ? t.GetString()
                : null;
            if (string.IsNullOrEmpty(token))
            {
                LastError = "response missing token";
                return null;
            }
            return token;
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
