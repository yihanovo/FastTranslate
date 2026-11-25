using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SnapTranslate.Models;

namespace SnapTranslate.Services;

/// <summary>百度通用翻译 API 响应模型（设计文档 §5.1）</summary>
internal sealed record BaiduResponse(
    [property: JsonPropertyName("from")] string? From,
    [property: JsonPropertyName("to")] string? To,
    [property: JsonPropertyName("trans_result")] IReadOnlyList<BaiduTransItem>? TransResult,
    [property: JsonPropertyName("error_code")] string? ErrorCode,
    [property: JsonPropertyName("error_msg")] string? ErrorMsg);

internal sealed record BaiduTransItem(
    [property: JsonPropertyName("src")] string Src,
    [property: JsonPropertyName("dst")] string Dst);

/// <summary>
/// 百度翻译 Provider（设计文档 §4.5）。
/// - sign = MD5(appid + q + salt + secret)，UTF-8，32 位小写
/// - 标准版 1 QPS：客户端排队节流 ≥ 1.05s
/// - 52001/52002/54003 指数退避重试，最多 2 次
/// - 注意：错误响应 HTTP 状态码仍为 200，以 error_code 判定成败
/// </summary>
internal sealed class BaiduTranslationProvider
{
    private const string Endpoint = "https://fanyi-api.baidu.com/api/trans/vip/translate";
    private const int MinIntervalMs = 1050;     // 1 QPS 节流
    private const int MaxRetries = 2;

    private readonly HttpClient _http;
    private readonly ConfigService _config;
    private readonly SemaphoreSlim _throttleLock = new(1, 1);
    private DateTimeOffset _lastRequestUtc = DateTimeOffset.MinValue;

    public BaiduTranslationProvider(ConfigService config)
    {
        _config = config;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    }

    public string Name => "Baidu";

    public async Task<TranslationResult> TranslateAsync(
        string text, string from = "en", string to = "zh", CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return TranslationResult.Fail("EMPTY", "没有可翻译的文本");

        var credentials = _config.GetBaidu();
        if (!credentials.IsConfigured)
            return TranslationResult.Fail("NO_CONF",
                "未配置百度翻译密钥。请将 AppID 与密钥写入 %APPDATA%\\SnapTranslate\\config.json 或稍后在设置界面填写");

        // 1 QPS 客户端节流：串行排队，间隔不足则补足等待
        await _throttleLock.WaitAsync(ct);
        try
        {
            Exception? lastError = null;
            for (int attempt = 0; attempt <= MaxRetries; attempt++)
            {
                var waitMs = MinIntervalMs - (int)(DateTimeOffset.UtcNow - _lastRequestUtc).TotalMilliseconds;
                if (waitMs > 0) await Task.Delay(waitMs, ct);

                try
                {
                    var result = await TranslateOnceAsync(text, from, to, credentials, ct);
                    _lastRequestUtc = DateTimeOffset.UtcNow;
                    if (result.Success) return result;

                    // 明确不可重试的错误：直接返回
                    if (result.ErrorCode is "54001" or "54003" or "54004" or "54005" or "58003" or "52003" or "58002" or "90107" or "20003")
                    {
                        // 54003（QPS 超限）特殊：退避后仍值得重试一次
                        if (result.ErrorCode == "54003" && attempt < MaxRetries)
                        {
                            await Task.Delay(1200 * (attempt + 1), ct);
                            continue;
                        }
                        return result;
                    }

                    // 52001 超时 / 52002 系统错误等：退避重试
                    if (attempt < MaxRetries)
                    {
                        await Task.Delay(600 * (int)Math.Pow(2, attempt), ct);
                        continue;
                    }
                    return result;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 网络异常：退避重试
                    lastError = ex;
                    Logger.Error($"百度翻译请求异常(第{attempt + 1}次)", ex);
                    if (attempt < MaxRetries)
                    {
                        await Task.Delay(600 * (int)Math.Pow(2, attempt), ct);
                        continue;
                    }
                }
            }
            return TranslationResult.Fail("NETWORK", lastError?.Message ?? "网络错误，重试失败");
        }
        finally
        {
            _throttleLock.Release();
        }
    }

    private async Task<TranslationResult> TranslateOnceAsync(
        string text, string from, string to, BaiduCredentials credentials, CancellationToken ct)
    {
        var salt = Guid.NewGuid().ToString("N");

        // 签名拼接时 q 不做 URL encode（官方文档强调的 54001 陷阱）
        var signSource = $"{credentials.AppId}{text}{salt}{credentials.Secret}";
        var sign = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(signSource))).ToLowerInvariant();

        var form = new Dictionary<string, string>
        {
            ["q"] = text,
            ["from"] = from,
            ["to"] = to,
            ["appid"] = credentials.AppId,
            ["salt"] = salt,
            ["sign"] = sign,
        };

        using var content = new FormUrlEncodedContent(form);
        using var response = await _http.PostAsync(Endpoint, content, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        BaiduResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<BaiduResponse>(json);
        }
        catch (JsonException)
        {
            return TranslationResult.Fail("BAD_JSON", "响应解析失败");
        }

        if (parsed is null)
            return TranslationResult.Fail("BAD_JSON", "响应为空");

        if (!string.IsNullOrEmpty(parsed.ErrorCode))
            return TranslationResult.Fail(parsed.ErrorCode, DescribeError(parsed.ErrorCode, parsed.ErrorMsg));

        if (parsed.TransResult is { Count: > 0 })
        {
            // 多行 q 按行返回；按 \n 拼接保留段落结构
            var translated = string.Join("\n", parsed.TransResult.Select(i => i.Dst));
            return new TranslationResult { Success = true, Translated = translated };
        }

        return TranslationResult.Fail("EMPTY_RESULT", "翻译结果为空");
    }

    private static string DescribeError(string code, string? raw) => code switch
    {
        "52001" => "请求超时，请重试",
        "52002" => "百度系统错误，请重试",
        "52003" => "未授权用户：请检查 AppID 是否正确、服务是否开通",
        "54000" => "请求参数为空",
        "54001" => "签名错误：请检查 AppID/密钥是否匹配",
        "54003" => "访问过于频繁（1 QPS 限制），请稍候",
        "54004" => "账户余额不足",
        "54005" => "长文本发送过于频繁，请 3 秒后重试",
        "58000" => "客户端 IP 被限制",
        "58001" => "不支持该翻译方向",
        "58002" => "服务已关闭：请到百度控制台开启通用翻译服务",
        "58003" => "此 IP 当日请求受限（多 AppID 共用 IP 会触发）",
        "90107" => "认证未通过",
        _ => $"百度翻译错误 {code}：{raw}",
    };
}
