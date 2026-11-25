namespace SnapTranslate.Models;

/// <summary>OCR 归一化结果（设计文档 §5.3）</summary>
public sealed record OcrOutcome
{
    /// <summary>按行拼接、已清洗的文本</summary>
    public string FullText { get; init; } = string.Empty;

    /// <summary>清洗后仅一个英文单词（M4 词典卡片模式触发条件）</summary>
    public bool IsSingleWord { get; init; }

    /// <summary>词数（粗略，按空白切分）</summary>
    public int WordCount { get; init; }

    public static readonly OcrOutcome Empty = new();
}

/// <summary>翻译结果（设计文档 §5.3）</summary>
public sealed record TranslationResult
{
    public bool Success { get; init; }
    public string? Translated { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public static TranslationResult Fail(string code, string message) =>
        new() { Success = false, ErrorCode = code, ErrorMessage = message };
}
