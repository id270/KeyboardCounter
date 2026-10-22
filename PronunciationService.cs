using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace KeyboardCounter;

/// <summary>
/// 发音服务：优先使用接口返回的发音地址，否则使用有道词典的免费发音接口。
/// </summary>
public class PronunciationService : IDisposable
{
    /// <summary>有道词典免费发音接口（英美音：type=1 英式，type=2 美式）</summary>
    private const string YoudaodictVoiceEndpoint = "https://dict.youdao.com/dictvoice";

    /// <summary>百度翻译网页版的免费发音接口（兜底用，无需密钥）</summary>
    private const string BaiduTtsEndpoint = "https://fanyi.baidu.com/gettts";

    /// <summary>单次发音的最大字符数，太长会被接口拒绝</summary>
    private const int MaxSpeechLength = 240;

    private static readonly HttpClient Http = CreateHttpClient();

    private readonly Queue<string> _tempFiles = new();
    private MediaPlayer? _player;
    private int _playVersion;

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "KeyboardCounter/1.5");
        return client;
    }

    /// <summary>
    /// 朗读一段文本
    /// </summary>
    /// <param name="text">要朗读的文本</param>
    /// <param name="speakUrl">接口返回的发音地址，可为空</param>
    /// <param name="american">true 美式发音，false 英式发音（仅英文有效）</param>
    public async Task<bool> PlayAsync(string? text, string? speakUrl, bool american, CancellationToken cancellationToken)
    {
        var speech = BuildSpeechText(text);
        if (speech.Length == 0)
            return false;

        var version = ++_playVersion;

        foreach (var url in BuildCandidates(speech, american, speakUrl))
        {
            var data = await DownloadAsync(url, cancellationToken);
            if (data == null || version != _playVersion)
                continue;

            if (Play(data))
                return true;
        }

        return false;
    }

    /// <summary>停止当前发音</summary>
    public void Stop()
    {
        _playVersion++;
        try
        {
            _player?.Stop();
        }
        catch
        {
            // 忽略
        }
    }

    private static string BuildSpeechText(string? text)
    {
        var normalized = LanguageUtil.Normalize(text).Replace("\n", " ");
        if (normalized.Length > MaxSpeechLength)
            normalized = normalized.Substring(0, MaxSpeechLength);
        return normalized;
    }

    /// <summary>
    /// 依次尝试的发音地址：接口返回的地址 → 有道词典 → 百度网页版（后两个都不需要额外配置）
    /// </summary>
    private static IEnumerable<string> BuildCandidates(string speech, bool american, string? speakUrl)
    {
        if (!string.IsNullOrWhiteSpace(speakUrl))
            yield return speakUrl;

        var encoded = Uri.EscapeDataString(speech);
        var isChinese = LanguageUtil.ContainsChinese(speech);

        if (isChinese)
        {
            // 中文：有道词典接口不稳定，优先用百度
            yield return $"{BaiduTtsEndpoint}?lan=zh&text={encoded}&spd=3&source=web";
            yield return $"{YoudaodictVoiceEndpoint}?audio={encoded}&le=zh";
        }
        else
        {
            yield return $"{YoudaodictVoiceEndpoint}?audio={encoded}&type={(american ? 2 : 1)}";
            yield return $"{BaiduTtsEndpoint}?lan=en&text={encoded}&spd=3&source=web";
        }
    }

    private static async Task<byte[]?> DownloadAsync(string? url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        try
        {
            using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            var isAudio = mediaType.StartsWith("audio", StringComparison.OrdinalIgnoreCase) ||
                          mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase);
            if (!isAudio)
                return null;

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return bytes.Length > 512 ? bytes : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    private bool Play(byte[] data)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => Play(data)));
            return true;
        }

        try
        {
            var path = Path.Combine(Path.GetTempPath(), $"kc_tts_{Guid.NewGuid():N}.mp3");
            File.WriteAllBytes(path, data);

            if (_player == null)
            {
                _player = new MediaPlayer { Volume = 1.0 };
            }

            _player.Close();
            _player.Open(new Uri(path, UriKind.Absolute));
            _player.Play();

            _tempFiles.Enqueue(path);
            while (_tempFiles.Count > 4)
                TryDelete(_tempFiles.Dequeue());

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 文件可能仍在播放，交给系统临时目录清理
        }
    }

    public void Dispose()
    {
        _playVersion++;
        try
        {
            _player?.Close();
        }
        catch
        {
            // 忽略
        }

        _player = null;

        while (_tempFiles.Count > 0)
            TryDelete(_tempFiles.Dequeue());

        GC.SuppressFinalize(this);
    }
}
