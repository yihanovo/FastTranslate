using SnapTranslate.Models;

namespace SnapTranslate.Services;

/// <summary>
/// 翻译引擎路由（设计文档 §5.3 "供应商可替换"的落地）：
/// - Auto：百度凭据已配置 → 百度（质量优先）；否则 → MyMemory（开箱即用）
/// - 显式指定引擎时直接使用
/// </summary>
internal sealed class TranslationRouter
{
    private readonly ConfigService _config;
    private readonly BaiduTranslationProvider _baidu;
    private readonly MyMemoryTranslationProvider _myMemory;

    public TranslationRouter(
        ConfigService config,
        BaiduTranslationProvider baidu,
        MyMemoryTranslationProvider myMemory)
    {
        _config = config;
        _baidu = baidu;
        _myMemory = myMemory;
    }

    /// <summary>当前生效引擎名（诊断/设置界面显示用）</summary>
    public string ActiveEngineName => ResolveEngine() switch
    {
        TranslationEngine.Baidu => _baidu.Name,
        _ => _myMemory.Name,
    };

    private TranslationEngine ResolveEngine()
    {
        var configured = _config.GetEngine();
        if (configured == TranslationEngine.Auto)
        {
            return _config.GetBaidu().IsConfigured
                ? TranslationEngine.Baidu
                : TranslationEngine.MyMemory;
        }
        return configured;
    }

    public Task<TranslationResult> TranslateAsync(
        string text, string from = "en", string to = "zh", CancellationToken ct = default)
    {
        return ResolveEngine() switch
        {
            TranslationEngine.Baidu => _baidu.TranslateAsync(text, from, to, ct),
            _ => _myMemory.TranslateAsync(text, from, to, ct),
        };
    }
}
