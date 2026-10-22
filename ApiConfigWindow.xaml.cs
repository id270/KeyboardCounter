using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace KeyboardCounter;

/// <summary>
/// 翻译接口配置窗口：选择有道 / 百度，填写密钥，测试翻译
/// </summary>
public partial class ApiConfigWindow : Window
{
    private readonly TranslateSettings _settings;

    public ApiConfigWindow(TranslateSettings settings)
    {
        InitializeComponent();

        _settings = settings.Clone();
        LoadFields();
    }

    /// <summary>保存后的配置（仅在 DialogResult 为 true 时使用）</summary>
    public TranslateSettings Settings => _settings;

    private void LoadFields()
    {
        EnabledCheck.IsChecked = _settings.Enabled;
        // 发音功能暂时关闭
        // AutoSpeakCheck.IsChecked = _settings.AutoSpeak;
        MaxLengthBox.Text = _settings.MaxLength.ToString();

        YoudaoKeyBox.Text = _settings.YoudaoAppKey;
        YoudaoSecretBox.Text = _settings.YoudaoAppSecret;
        BaiduIdBox.Text = _settings.BaiduAppId;
        BaiduSecretBox.Text = _settings.BaiduAppSecret;

        ProviderCombo.SelectedIndex = _settings.Provider == TranslateProvider.Baidu ? 1 : 0;
        SelectionModeCombo.SelectedIndex = _settings.SelectionMode switch
        {
            SelectionMode.UiaWithClipboard => 1,
            SelectionMode.ClipboardOnly => 2,
            _ => 0
        };
        UpdateProviderVisibility();
    }

    private void ReadFields()
    {
        _settings.Enabled = EnabledCheck.IsChecked == true;
        // 发音功能暂时关闭
        // _settings.AutoSpeak = AutoSpeakCheck.IsChecked == true;
        _settings.Provider = ProviderCombo.SelectedIndex == 1 ? TranslateProvider.Baidu : TranslateProvider.Youdao;
        _settings.SelectionMode = SelectionModeCombo.SelectedIndex switch
        {
            1 => SelectionMode.UiaWithClipboard,
            2 => SelectionMode.ClipboardOnly,
            _ => SelectionMode.UiaOnly
        };

        _settings.YoudaoAppKey = YoudaoKeyBox.Text.Trim();
        _settings.YoudaoAppSecret = YoudaoSecretBox.Text.Trim();
        _settings.BaiduAppId = BaiduIdBox.Text.Trim();
        _settings.BaiduAppSecret = BaiduSecretBox.Text.Trim();

        _settings.MaxLength = int.TryParse(MaxLengthBox.Text.Trim(), out var maxLength) ? maxLength : 600;
        if (_settings.MaxLength < 10)
            _settings.MaxLength = 600;
    }

    private void UpdateProviderVisibility()
    {
        var isBaidu = ProviderCombo.SelectedIndex == 1;
        YoudaoGroup.Visibility = isBaidu ? Visibility.Collapsed : Visibility.Visible;
        BaiduGroup.Visibility = isBaidu ? Visibility.Visible : Visibility.Collapsed;
        TestResultText.Text = "";
    }

    private void ProviderCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsInitialized)
            return;

        UpdateProviderVisibility();
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        ReadFields();

        var provider = TranslationService.Create(_settings);
        if (provider == null)
        {
            TestResultText.Foreground = System.Windows.Media.Brushes.Firebrick;
            TestResultText.Text = "请先填写当前所选接口的密钥。";
            return;
        }

        var text = string.IsNullOrWhiteSpace(TestInputBox.Text) ? "hello" : TestInputBox.Text.Trim();

        TestButton.IsEnabled = false;
        TestResultText.Foreground = System.Windows.Media.Brushes.Gray;
        TestResultText.Text = "翻译中…";

        try
        {
            var result = await provider.TranslateAsync(text, System.Threading.CancellationToken.None);
            TestResultText.Foreground = System.Windows.Media.Brushes.SeaGreen;
            TestResultText.Text = $"✅ {result.ProviderName}：{result.TranslatedText}";
        }
        catch (Exception ex)
        {
            TestResultText.Foreground = System.Windows.Media.Brushes.Firebrick;
            TestResultText.Text = $"❌ {ex.Message}";
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        ReadFields();

        if (!_settings.IsConfigured)
        {
            MessageBox.Show(this,
                $"请填写{_settings.ProviderDisplayName}的密钥。\n\n" +
                (_settings.Provider == TranslateProvider.Baidu
                    ? "百度翻译需要 APP ID 和密钥，可在“百度翻译开放平台 → 我的服务”中查看。"
                    : "有道智云需要应用 ID 和密钥，可在“控制台 → 应用管理”中查看。"),
                "划词翻译", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.Enabled = true;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OpenApplyPage_Click(object sender, RoutedEventArgs e)
    {
        var url = ProviderCombo.SelectedIndex == 1
            ? "https://fanyi-api.baidu.com/"
            : "https://ai.youdao.com/";
        OpenUrl(url);
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        OpenUrl(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // 打不开浏览器时忽略
        }
    }
}
