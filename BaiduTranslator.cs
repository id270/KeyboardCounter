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
/// 百度翻译开放平台 通用文本翻译接口
/// 文档：https://fanyi-api.baidu.com/doc/21
/// </summary>
public class BaiduTranslator : ITranslationProvider
{
    private const string Endpoint = "https://fanyi-api.baidu.com/api/trans/vip/translate";

    private static readonly HttpClient Http = CreateHttpClient();

    private readonly string _appId;
    private readonly string _appSecret;

    public BaiduTranslator(string appId, string appSecret)
    {
        _appId = (appId ?? "").Trim();
        _appSecret = (appSecret ?? "").Trim();
    }

    public string Name => "百度翻译";

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
        if (string.IsNullOrWhiteSpace(_appId) || string.IsNullOrWhiteSpace(_appSecret))
            throw new TranslationException("还没有填写百度翻译的 APP ID / 密钥。");

        // 中文 -> 英文，其它 -> 中文
        var target = LanguageUtil.ContainsChinese(text) ? "en" : "zh";

        var salt = Guid.NewGuid().ToString("N");
        var sign = Md5Hex(_appId + text + salt + _appSecret);

        var form = new Dictionary<string, string>
        {
            ["q"] = text,
            ["from"] = "auto",
            ["to"] = target,
            ["appid"] = _appId,
            ["salt"] = salt,
            ["sign"] = sign
        };

        string body;
        try
        {
            using var content = new FormUrlEncodedContent(form);
            using var response = await Http.PostAsync(Endpoint, content, cancellationToken).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw new TranslationException($"百度接口返回 HTTP {(int)response.StatusCode}。");
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
            throw new TranslationException("无法连接百度翻译接口，请检查网络。", ex);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new TranslationException("百度接口返回了无法解析的内容。", ex);
        }

        using (document)
        {
            var root = document.RootElement;

            var errorCode = ReadString(root, "error_code");
            if (!string.IsNullOrEmpty(errorCode) && errorCode != "0" && errorCode != "52000")
                throw new TranslationException(DescribeError(errorCode));

            var translated = new StringBuilder();
            if (root.TryGetProperty("trans_result", out var resultElement) &&
                resultElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in resultElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;
                    var line = ReadString(item, "dst");
                    if (string.IsNullOrEmpty(line))
                        continue;
                    if (translated.Length > 0)
                        translated.Append('\n');
                    translated.Append(line);
                }
            }

            if (translated.Length == 0)
                throw new TranslationException("百度接口没有返回翻译结果。");

            var from = ReadString(root, "from");
            var to = ReadString(root, "to");

            return new TranslationResult
            {
                SourceText = text,
                TranslatedText = translated.ToString(),
                From = string.IsNullOrEmpty(from) ? "auto" : from,
                To = string.IsNullOrEmpty(to) ? target : to,
                SpeakUrl = "",
                ProviderName = Name
            };
        }
    }

    private static string Md5Hex(string value)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(value));
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
            "52001" => "百度接口：请求超时，请重试。",
            "52002" => "百度接口：系统错误，请重试。",
            "52003" => "百度接口：未授权用户，请检查 APP ID 是否正确。",
            "54000" => "百度接口：必填参数为空。",
            "54001" => "百度接口：签名错误，请检查密钥是否正确。",
            "54003" => "百度接口：访问频率受限（免费版每秒 1 次），请稍后重试。",
            "54004" => "百度接口：账户余额不足。",
            "54005" => "百度接口：长文本请求过于频繁，请稍后重试。",
            "58000" => "百度接口：客户端 IP 非法。",
            "58001" => "百度接口：译文语言方向不支持。",
            "58002" => "百度接口：服务当前已关闭，请到控制台开启。",
            "90107" => "百度接口：认证未通过或未生效。",
            _ => $"百度接口错误码 {errorCode}，请检查接口配置。"
        };
    }
}
