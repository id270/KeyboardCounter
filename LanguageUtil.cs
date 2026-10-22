using System;
using System.Text;

namespace KeyboardCounter;

/// <summary>
/// 文本语言判断与清洗的小工具
/// </summary>
public static class LanguageUtil
{
    /// <summary>是否包含中日韩统一表意文字</summary>
    public static bool ContainsChinese(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        foreach (var c in text)
        {
            if ((c >= 0x4E00 && c <= 0x9FFF) ||   // 基本汉字
                (c >= 0x3400 && c <= 0x4DBF) ||   // 扩展 A
                (c >= 0xF900 && c <= 0xFAFF))     // 兼容汉字
                return true;
        }

        return false;
    }

    /// <summary>是否纯 ASCII（用来判断是否适合显示"英式/美式"发音按钮）</summary>
    public static bool IsAscii(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        foreach (var c in text)
        {
            if (c > 127)
                return false;
        }

        return true;
    }

    /// <summary>
    /// 清洗剪贴板里拿到的文本：去掉零宽字符、统一换行、去掉首尾空白
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c == '\u200B' || c == '\uFEFF' || c == '\0')
                continue;
            if (c == '\r')
                continue;
            builder.Append(c);
        }

        // 去掉空行之外的多余空白行
        var lines = builder.ToString().Split('\n');
        var result = new StringBuilder();
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 && result.Length == 0)
                continue; // 跳过开头的空行
            if (result.Length > 0)
                result.Append('\n');
            result.Append(trimmed);
        }

        return result.ToString().Trim();
    }

    /// <summary>把文本压缩到指定长度，超出部分用省略号代替</summary>
    public static string Ellipsis(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        if (maxLength <= 3 || text.Length <= maxLength)
            return text;

        return text.Substring(0, maxLength - 1) + "…";
    }

    /// <summary>语言代码转显示名称</summary>
    public static string LanguageName(string? code)
    {
        return (code ?? "").ToLowerInvariant() switch
        {
            "en" => "英语",
            "zh" or "zh-chs" => "中文",
            "zh-cht" => "繁体中文",
            "ja" => "日语",
            "ko" => "韩语",
            "fr" => "法语",
            "de" => "德语",
            "ru" => "俄语",
            "" => "自动",
            _ => code ?? ""
        };
    }
}
