using System.Threading;
using System.Threading.Tasks;

namespace KeyboardCounter;

/// <summary>
/// 翻译接口的统一抽象（有道 / 百度）
/// </summary>
public interface ITranslationProvider
{
    /// <summary>接口显示名称</summary>
    string Name { get; }

    /// <summary>翻译一段文本，失败时抛出 <see cref="TranslationException"/></summary>
    Task<TranslationResult> TranslateAsync(string text, CancellationToken cancellationToken);
}
