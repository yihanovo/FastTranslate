using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.RegularExpressions;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using SnapTranslate.Models;

namespace SnapTranslate.Services;

/// <summary>
/// WinRT OCR 封装（设计文档 §4.4）。
/// 系统 OCR 引擎：离线、免费、毫秒级；需系统装有英文语言包。
/// </summary>
internal sealed partial class OcrService
{
    private OcrEngine? _engine;

    /// <summary>初始化引擎；返回 false 表示系统缺少英文 OCR 语言包</summary>
    public bool EnsureEngine()
    {
        if (_engine != null) return true;
        try
        {
            _engine = OcrEngine.TryCreateFromLanguage(new Language("en"));
        }
        catch (Exception ex)
        {
            Logger.Error("创建 OCR 引擎失败", ex);
            _engine = null;
        }
        return _engine != null;
    }

    /// <summary>语言包是否缺失（用于启动时诊断）</summary>
    public static bool IsEnglishLanguagePackInstalled()
    {
        try
        {
            var installed = OcrEngine.AvailableRecognizerLanguages;
            return installed.Any(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>识别裁剪位图中的英文文本；返回 null=引擎不可用，Empty=无文本</summary>
    public async Task<OcrOutcome?> RecognizeAsync(Bitmap bitmap, CancellationToken ct = default)
    {
        if (_engine == null && !EnsureEngine()) return null;

        // Bitmap → PNG → WinRT 解码 → SoftwareBitmap（简单可靠的转换路径）
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        ms.Position = 0;

        using var winrtStream = ms.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(winrtStream).AsTask(ct);
        var softwareBitmap = await decoder.GetSoftwareBitmapAsync().AsTask(ct);
        if (softwareBitmap is null) return OcrOutcome.Empty;

        // OcrEngine 要求 Bgra8 + Premultiplied
        if (softwareBitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8
            || softwareBitmap.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
        {
            var converted = SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            softwareBitmap.Dispose();
            softwareBitmap = converted;
        }

        try
        {
            var engine = _engine ?? throw new InvalidOperationException("OCR 引擎未初始化");
            var result = await engine.RecognizeAsync(softwareBitmap).AsTask(ct);
            return Normalize(result);
        }
        finally
        {
            softwareBitmap.Dispose();
        }
    }

    private static OcrOutcome Normalize(OcrResult result)
    {
        if (result?.Lines is null || result.Lines.Count == 0) return OcrOutcome.Empty;

        var lines = result.Lines
            .Select(l => string.Join(" ", l.Words.Select(w => w.Text)).Trim())
            .Where(t => t.Length > 0)
            .ToList();

        if (lines.Count == 0) return OcrOutcome.Empty;

        // 去零宽字符、合并多余空白
        var text = string.Join("\n", lines.Select(CleanLine));
        var trimmed = text.Trim();
        var wordCount = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

        return new OcrOutcome
        {
            FullText = trimmed,
            WordCount = wordCount,
            IsSingleWord = SingleWordRegex().IsMatch(trimmed),
        };
    }

    private static string CleanLine(string line) =>
        line.Replace("\u200B", "").Replace("\u200C", "").Replace("\u200D", "").Trim();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z\-']*$")]
    private static partial Regex SingleWordRegex();
}
