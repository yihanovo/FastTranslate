using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using SnapTranslate.Models;

namespace SnapTranslate.Services;

/// <summary>MyMemory 响应模型（官方免费 API，免注册）</summary>
internal sealed record MyMemoryResponse(
    [property: JsonPropertyName("responseData")] MyMemoryData? ResponseData,
    [property: JsonPropertyName("responseStatus")] int? ResponseStatus,
    [property: JsonPropertyName("responseDetails")] string? ResponseDetails);

internal sealed record MyMemoryData(
    [property: JsonPropertyName("translatedText")] string? TranslatedText,
    [property: JsonPropertyName("match")] double? Match);

/// <summary>
/// MyMemory 翻译 Provider（默认引擎，开箱即用）。
/// - 官方免费 API：https://mymemory.translated.net/doc/spec.php
/// - 免注册：匿名 5000 词/天/IP（截图翻译场景足够）
/// - 无签名无 QPS 限制，失败自动重试 2 次
/// </summary>
internal sealed class MyMemoryTranslationProvider
{
    private const string Endpoint = "https://api.mymemory.translated.net/get";
    private const int MaxRetries = 2;

    private readonly HttpClient _http;

    public string Name => "MyMemory";

    public MyMemoryTranslationProvider()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SnapTranslate/0.1 (screenshot translator)");
    }

    public async Task<TranslationResult> TranslateAsync(
        string text, string from = "en", string to = "zh-CN", CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return TranslationResult.Fail("EMPTY", "没有可翻译的文本");

        // MyMemory 语言代码：zh 用 zh-CN
        var pair = $"{from}|{(to == "zh" ? "zh-CN" : to)}";

        Exception? lastError = null;
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var url = $"{Endpoint}?q={Uri.EscapeDataString(text)}&langpair={Uri.EscapeDataString(pair)}";
                using var response = await _http.GetAsync(url, ct);
                var json = await response.Content.ReadAsStringAsync(ct);

                MyMemoryResponse? parsed;
                try { parsed = JsonSerializer.Deserialize<MyMemoryResponse>(json); }
                catch (JsonException) { return TranslationResult.Fail("BAD_JSON", "响应解析失败"); }

                if (parsed is null)
                    return TranslationResult.Fail("BAD_JSON", "响应为空");

                // 配额超限等：responseStatus=429 且 responseDetails 有说明
                if (parsed.ResponseStatus is int status && status != 200)
                {
                    return TranslationResult.Fail("MYMEMORY_" + status,
                        parsed.ResponseDetails is { Length: > 0 } d ? d : $"服务返回状态 {status}");
                }

                var translated = parsed.ResponseData?.TranslatedText;
                if (!string.IsNullOrWhiteSpace(translated))
                {
                    // HTML 实体解码（MyMemory 偶尔返回 &amp; 等）
                    translated = System.Net.WebUtility.HtmlDecode(translated);
                    return new TranslationResult { Success = true, Translated = translated };
                }

                return TranslationResult.Fail("EMPTY_RESULT", "翻译结果为空");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                Logger.Error($"MyMemory 请求异常(第{attempt + 1}次)", ex);
                if (attempt < MaxRetries) await Task.Delay(500 * (attempt + 1), ct);
            }
        }
        return TranslationResult.Fail("NETWORK", lastError?.Message ?? "网络错误，重试失败");
    }
}
