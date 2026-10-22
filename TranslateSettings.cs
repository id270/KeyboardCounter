using System;
using System.Collections.Generic;
using System.Globalization;

namespace KeyboardCounter;

/// <summary>
/// 划词翻译使用的翻译接口
/// </summary>
public enum TranslateProvider
{
    Youdao = 0,
    Baidu = 1
}

/// <summary>
/// 取词方式
/// </summary>
public enum SelectionMode
{
    /// <summary>只用 UI Automation 读选区，完全不碰剪贴板（默认）</summary>
    UiaOnly = 0,

    /// <summary>UIA 读不到时退回"模拟 Ctrl+C + 读剪贴板"（会短暂占用剪贴板，取完还原）</summary>
    UiaWithClipboard = 1,

    /// <summary>只用剪贴板（老式环境兜底）</summary>
    ClipboardOnly = 2
}

/// <summary>
/// 划词翻译配置（保存在 config.ini 的 [Translate] 段）
/// </summary>
public class TranslateSettings
{
    private const string Section = "Translate";

    /// <summary>是否启用划词翻译</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>当前使用的翻译接口</summary>
    public TranslateProvider Provider { get; set; } = TranslateProvider.Youdao;

    /// <summary>取词方式，默认只用 UI Automation（不碰剪贴板）</summary>
    public SelectionMode SelectionMode { get; set; } = SelectionMode.UiaOnly;

    /// <summary>
    /// 取词白名单：这些程序（如 typora.exe,WeChat.exe）在"仅 UIA"模式下也允许用剪贴板兜底。
    /// 逗号/分号分隔，留空表示不启用。
    /// </summary>
    public string ClipboardFallbackProcesses { get; set; } = "";

    /// <summary>有道智云 应用ID（appKey）</summary>
    public string YoudaoAppKey { get; set; } = "";

    /// <summary>有道智云 应用密钥（appSecret）</summary>
    public string YoudaoAppSecret { get; set; } = "";

    /// <summary>百度翻译  APP ID</summary>
    public string BaiduAppId { get; set; } = "";

    /// <summary>百度翻译 密钥</summary>
    public string BaiduAppSecret { get; set; } = "";

    /// <summary>翻译完成后自动朗读</summary>
    public bool AutoSpeak { get; set; } = true;

    /// <summary>单次翻译的最大字符数（超过则忽略）</summary>
    public int MaxLength { get; set; } = 600;

    /// <summary>是否输出调试日志（排查划词无反应时打开）</summary>
    public bool Debug { get; set; }

    /// <summary>是否已经自动弹过一次配置窗口（首次运行引导）</summary>
    public bool Prompted { get; set; }

    public string ProviderDisplayName =>
        Provider == TranslateProvider.Baidu ? "百度翻译" : "有道翻译";

    /// <summary>当前选择的接口是否已经填写密钥</summary>
    public bool IsConfigured =>
        Provider == TranslateProvider.Baidu
            ? !string.IsNullOrWhiteSpace(BaiduAppId) && !string.IsNullOrWhiteSpace(BaiduAppSecret)
            : !string.IsNullOrWhiteSpace(YoudaoAppKey) && !string.IsNullOrWhiteSpace(YoudaoAppSecret);

    public static TranslateSettings Load(IniConfig config)
    {
        var settings = new TranslateSettings
        {
            Enabled = config.GetBool(Section, "Enabled", true),
            Provider = string.Equals(config.GetString(Section, "Provider", "Youdao"), "Baidu", StringComparison.OrdinalIgnoreCase)
                ? TranslateProvider.Baidu
                : TranslateProvider.Youdao,
            YoudaoAppKey = config.GetString(Section, "YoudaoAppKey"),
            YoudaoAppSecret = config.GetString(Section, "YoudaoAppSecret"),
            BaiduAppId = config.GetString(Section, "BaiduAppId"),
            BaiduAppSecret = config.GetString(Section, "BaiduAppSecret"),
            AutoSpeak = config.GetBool(Section, "AutoSpeak", true),
            MaxLength = ParseInt(config.GetString(Section, "MaxLength", "600"), 600),
            Debug = config.GetBool(Section, "Debug", false),
            Prompted = config.GetBool(Section, "Prompted", false),
            SelectionMode = ParseSelectionMode(config.GetString(Section, "SelectionMode", "UiaOnly")),
            ClipboardFallbackProcesses = config.GetString(Section, "ClipboardFallbackProcesses", "")
        };

        if (settings.MaxLength < 10)
            settings.MaxLength = 10;

        return settings;
    }

    public void Save(IniConfig config)
    {
        config.SetBool(Section, "Enabled", Enabled);
        config.SetString(Section, "Provider", Provider == TranslateProvider.Baidu ? "Baidu" : "Youdao");
        config.SetString(Section, "YoudaoAppKey", YoudaoAppKey.Trim());
        config.SetString(Section, "YoudaoAppSecret", YoudaoAppSecret.Trim());
        config.SetString(Section, "BaiduAppId", BaiduAppId.Trim());
        config.SetString(Section, "BaiduAppSecret", BaiduAppSecret.Trim());
        config.SetBool(Section, "AutoSpeak", AutoSpeak);
        config.SetString(Section, "MaxLength", MaxLength.ToString(CultureInfo.InvariantCulture));
        config.SetBool(Section, "Debug", Debug);
        config.SetBool(Section, "Prompted", Prompted);
        config.SetString(Section, "SelectionMode", SelectionMode.ToString());
        config.SetString(Section, "ClipboardFallbackProcesses", ClipboardFallbackProcesses.Trim());
        config.Save();
    }

    /// <summary>取词白名单解析成数组（自动补 .exe、去重）</summary>
    public string[] GetClipboardFallbackProcessList()
    {
        if (string.IsNullOrWhiteSpace(ClipboardFallbackProcesses))
            return Array.Empty<string>();

        var seen = new List<string>();
        foreach (var part in ClipboardFallbackProcesses.Split(new[] { ',', ';', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var name = part.Trim();
            if (name.Length == 0)
                continue;
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                name += ".exe";
            if (!seen.Exists(existing => existing.Equals(name, StringComparison.OrdinalIgnoreCase)))
                seen.Add(name);
        }

        return seen.ToArray();
    }

    public bool IsClipboardFallbackProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return false;

        foreach (var name in GetClipboardFallbackProcessList())
        {
            if (string.Equals(name, processName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public string SelectionModeDisplayName => SelectionMode switch
    {
        SelectionMode.UiaWithClipboard => "UIA + 剪贴板兜底",
        SelectionMode.ClipboardOnly => "仅剪贴板",
        _ => "仅 UIA（不碰剪贴板）"
    };

    private static SelectionMode ParseSelectionMode(string? value)
    {
        if (string.Equals(value, "UiaWithClipboard", StringComparison.OrdinalIgnoreCase))
            return SelectionMode.UiaWithClipboard;
        if (string.Equals(value, "ClipboardOnly", StringComparison.OrdinalIgnoreCase))
            return SelectionMode.ClipboardOnly;
        return SelectionMode.UiaOnly;
    }

    public TranslateSettings Clone()
    {
        return new TranslateSettings
        {
            Enabled = Enabled,
            Provider = Provider,
            YoudaoAppKey = YoudaoAppKey,
            YoudaoAppSecret = YoudaoAppSecret,
            BaiduAppId = BaiduAppId,
            BaiduAppSecret = BaiduAppSecret,
            AutoSpeak = AutoSpeak,
            MaxLength = MaxLength,
            Debug = Debug,
            Prompted = Prompted,
            SelectionMode = SelectionMode,
            ClipboardFallbackProcesses = ClipboardFallbackProcesses
        };
    }

    private static int ParseInt(string? value, int defaultValue)
    {
        if (int.TryParse((value ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
            return result;
        return defaultValue;
    }
}
