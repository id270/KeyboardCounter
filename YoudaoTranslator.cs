using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KeyboardCounter;

/// <summary>
/// 有道智云 文本翻译接口（v3 签名）
/// 文档：https://ai.youdao.com/DOCSIRMA/html/trans/api/wbfy/index.html
/// </summary>
public class YoudaoTranslator : ITranslationProvider
{
    private const string Endpoint = "https://openapi.youdao.com/api";

    private static readonly HttpClient Http = CreateHttpClient();

    private readonly string _appKey;
    private readonly string _appSecret;

    public YoudaoTranslator(string appKey, string appSecret)
    {
        _appKey = (appKey ?? "").Trim();
        _appSecret = (appSecret ?? "").Trim();
    }

    public string Name => "有道翻译";

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "KeyboardCounter/1.5");
        return client;
    }

    public async Task<TranslationResult> TranslateAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new TranslationException("没有需要翻译的内容。");
        if (string.IsNullOrWhiteSpace(_appKey) || string.IsNullOrWhiteSpace(_appSecret))
            throw new TranslationException("还没有填写有道翻译的 应用ID / 应用密钥。");

        // 中文 -> 英文，其它 -> 中文
        var target = LanguageUtil.ContainsChinese(text) ? "en" : "zh-CHS";

        var salt = Guid.NewGuid().ToString("N");
        var curtime = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var sign = Sha256Hex(_appKey + BuildSignInput(text) + salt + curtime + _appSecret);

        var form = new Dictionary<string, string>
        {
            ["q"] = text,
            ["from"] = "auto",
            ["to"] = target,
            ["appKey"] = _appKey,
            ["salt"] = salt,
            ["sign"] = sign,
            ["signType"] = "v3",
            ["curtime"] = curtime
        };

        string body;
        try
        {
            using var content = new FormUrlEncodedContent(form);
            using var response = await Http.PostAsync(Endpoint, content, cancellationToken).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw new TranslationException($"有道接口返回 HTTP {(int)response.StatusCode}。");
        }
        catch (TranslationException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new TranslationException("无法连接有道翻译接口，请检查网络。", ex);
        }

        return ParseResponse(body, text);
    }

    private TranslationResult ParseResponse(string body, string text)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new TranslationException("有道接口返回了无法解析的内容。", ex);
        }

        using (document)
        {
            var root = document.RootElement;

            var errorCode = ReadString(root, "errorCode");
            if (!string.IsNullOrEmpty(errorCode) && errorCode != "0")
                throw new TranslationException(DescribeError(errorCode));

            var translated = new StringBuilder();
            if (root.TryGetProperty("translation", out var translationElement) &&
                translationElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in translationElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                        continue;
                    var line = item.GetString();
                    if (string.IsNullOrEmpty(line))
                        continue;
                    if (translated.Length > 0)
                        translated.Append('\n');
                    translated.Append(line);
                }
            }

            if (translated.Length == 0)
                throw new TranslationException("有道接口没有返回翻译结果。");

            // l 形如 "EN2zh-CHS"
            var language = ReadString(root, "l");
            var from = "auto";
            var to = LanguageUtil.ContainsChinese(text) ? "en" : "zh-CHS";
            if (!string.IsNullOrEmpty(language))
            {
                var parts = language.Split("2");
                if (parts.Length == 2)
                {
                    from = parts[0];
                    to = parts[1];
                }
            }

            return new TranslationResult
            {
                SourceText = text,
                TranslatedText = translated.ToString(),
                From = from,
                To = to,
                SpeakUrl = ReadString(root, "speakUrl"),
                TranslatedSpeakUrl = ReadString(root, "tSpeakUrl"),
                ProviderName = Name
            };
        }
    }

    /// <summary>有道 v3 签名里的 input：q 前10个字符 + q长度 + q后10个字符</summary>
    private static string BuildSignInput(string q)
    {
        if (q.Length <= 20)
            return q;
        return q.Substring(0, 10) + q.Length + q.Substring(q.Length - 10);
    }

    private static string Sha256Hex(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
            builder.Append(b.ToString("x2"));
        return builder.ToString();
    }

    private static string ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element))
            return "";

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.Number => element.GetRawText(),
            _ => ""
        };
    }

    private static string DescribeError(string errorCode)
    {
        return errorCode switch
        {
            "101" => "有道接口：缺少必填参数。",
            "102" => "有道接口：不支持的语言类型。",
            "103" => "有道接口：文本过长。",
            "108" => "有道接口：应用ID无效，请检查配置。",
            "110" => "有道接口：应用没有绑定文本翻译服务，请到控制台绑定。",
            "111" => "有道接口：开发者账号无效。",
            "113" => "有道接口：翻译内容为空。",
            "202" => "有道接口：签名校验失败，请检查应用密钥是否正确。",
            "203" => "有道接口：访问 IP 不在允许列表内。",
            "206" => "有道接口：时间戳无效，请检查系统时间。",
            "207" => "有道接口：重放请求，请重试。",
            "301" => "有道接口：辞典查询失败。",
            "302" => "有道接口：翻译查询失败，请稍后重试。",
            "401" => "有道接口：账户余额不足（新用户赠送的体验金可能已用完）。",
            "411" => "有道接口：访问频率受限，请稍后重试。",
            "1411" => "有道接口：访问频率受限，请稍后重试。",
            _ => $"有道接口错误码 {errorCode}，请检查接口配置。"
        };
    }
}
