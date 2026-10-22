namespace KeyboardCounter;

/// <summary>
/// 一次翻译的结果
/// </summary>
public class TranslationResult
{
    /// <summary>原文</summary>
    public string SourceText { get; set; } = "";

    /// <summary>译文</summary>
    public string TranslatedText { get; set; } = "";

    /// <summary>源语言代码</summary>
    public string From { get; set; } = "auto";

    /// <summary>目标语言代码</summary>
    public string To { get; set; } = "";

    /// <summary>接口返回的发音地址（可能为空）</summary>
    public string SpeakUrl { get; set; } = "";

    /// <summary>接口返回的译文发音地址（可能为空）</summary>
    public string TranslatedSpeakUrl { get; set; } = "";

    /// <summary>接口显示名称</summary>
    public string ProviderName { get; set; } = "";

    /// <summary>是否来自本地缓存</summary>
    public bool IsFromCache { get; set; }
}
