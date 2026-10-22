using System;

namespace KeyboardCounter;

/// <summary>
/// 翻译过程中可以直接展示给用户的异常（消息已经是中文提示）
/// </summary>
public class TranslationException : Exception
{
    public TranslationException(string message) : base(message)
    {
    }

    public TranslationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
