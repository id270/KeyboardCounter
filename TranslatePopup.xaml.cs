using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace KeyboardCounter;

/// <summary>
/// 跟随鼠标的划词翻译气泡窗口。
/// 不抢焦点（WS_EX_NOACTIVATE），鼠标移开/点击别处就隐藏。
/// </summary>
public partial class TranslatePopup : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    private int _anchorX;
    private int _anchorY;
    private bool _closed;

    // 发音功能暂时关闭
    // private string _speechText = "";
    // private string _speakUrl = "";

    /*
    /// <summary>用户点击发音按钮（文本, 是否美式）</summary>
    public event Action<string, bool>? SpeakRequested;

    /// <summary>当前条目接口返回的发音地址（可能为空）</summary>
    public string CurrentSpeakUrl => _speakUrl;
    */

    public TranslatePopup()
    {
        InitializeComponent();
        SizeChanged += (s, e) => ClampPosition();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = GetWindowLong32(handle, GWL_EXSTYLE);
            style |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            SetWindowLong32(handle, GWL_EXSTYLE, style);
        }
        catch
        {
            // 设置失败只影响"不抢焦点"，可以忽略
        }
    }

    /// <summary>
    /// 预热：提前创建窗口句柄和 DWM 表面，避免第一次划词时才付出这块开销。
    /// 窗口以 0 透明显示一帧后立刻隐藏，用户看不到。
    /// </summary>
    public void WarmUp()
    {
        if (_closed || IsVisible)
            return;

        var oldOpacity = Opacity;

        try
        {
            Opacity = 0;
            Show();
            UpdateLayout();
            Hide();
        }
        catch
        {
            // 预热失败不影响正常使用
        }
        finally
        {
            Opacity = oldOpacity;
        }
    }

    /// <summary>取词偏慢时先显示的占位状态</summary>
    public void ShowPending(int screenX, int screenY)
    {
        ProviderText.Text = "识别中…";
        SourceText.Text = "";
        PhoneticText.Visibility = Visibility.Collapsed;
        ResultText.Text = "正在读取选区…";
        ResultText.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
        StatusText.Visibility = Visibility.Collapsed;
        ShowAt(screenX, screenY);
    }

    /// <summary>显示"翻译中…"</summary>
    public void ShowLoading(string source, int screenX, int screenY, string providerName)
    {
        ProviderText.Text = providerName;
        SourceText.Text = LanguageUtil.Ellipsis(source, 200);
        ResultText.Text = "翻译中…";
        ResultText.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
        StatusText.Visibility = Visibility.Collapsed;
        PhoneticText.Visibility = Visibility.Collapsed;
        // 发音功能暂时关闭
        // _speechText = "";
        // _speakUrl = "";
        // SetSpeakButtons(false);
        ShowAt(screenX, screenY);
    }

    /// <summary>显示翻译结果</summary>
    public void ShowResult(TranslationResult result, int screenX, int screenY)
    {
        var suffix = result.IsFromCache ? " · 缓存" : "";
        ProviderText.Text = $"{result.ProviderName} · {LanguageUtil.LanguageName(result.From)} → {LanguageUtil.LanguageName(result.To)}{suffix}";
        SourceText.Text = LanguageUtil.Ellipsis(result.SourceText, 300);
        ResultText.Text = result.TranslatedText;
        ResultText.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromArgb(0xF2, 0xF5, 0xFA, 0xFF));
        StatusText.Visibility = Visibility.Collapsed;
        PhoneticText.Visibility = Visibility.Collapsed;

        // 发音功能暂时关闭
        // var sourceIsChinese = LanguageUtil.ContainsChinese(result.SourceText);
        // _speechText = sourceIsChinese ? result.TranslatedText : result.SourceText;
        // _speakUrl = sourceIsChinese ? result.TranslatedSpeakUrl : result.SpeakUrl;
        // SetSpeakButtons(!string.IsNullOrWhiteSpace(_speechText));
        ShowAt(screenX, screenY);
    }

    /// <summary>显示离线词典结果（音标/拼音 + 多义项）</summary>
    public void ShowDictionary(string source, DictionaryEntry entry, int screenX, int screenY)
    {
        var header = entry.IsChinese ? "离线词典 · 中 → 英" : "离线词典 · 英 → 中";
        if (!string.IsNullOrEmpty(entry.MatchedForm) &&
            !string.Equals(entry.MatchedForm, entry.Word, StringComparison.OrdinalIgnoreCase))
        {
            header += $" · {entry.MatchedForm} → {entry.Word}";
        }

        if (!string.IsNullOrEmpty(entry.Tags))
            header += $" · {entry.Tags}";

        ProviderText.Text = header;
        SourceText.Text = LanguageUtil.Ellipsis(source, 300);

        if (string.IsNullOrWhiteSpace(entry.Phonetic))
        {
            PhoneticText.Visibility = Visibility.Collapsed;
        }
        else
        {
            // 中文词条这里是拼音
            PhoneticText.Text = entry.IsChinese
                ? $"[{entry.Phonetic}]"
                : $"/{entry.Phonetic.Trim('/')}/";
            PhoneticText.Visibility = Visibility.Visible;
        }

        ResultText.Text = entry.Translation;
        ResultText.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromArgb(0xF2, 0xF5, 0xFA, 0xFF));
        StatusText.Visibility = Visibility.Collapsed;
        ShowAt(screenX, screenY);
    }

    /// <summary>显示一条提示（例如还没有配置接口）</summary>
    public void ShowNotice(string source, string message, int screenX, int screenY)
    {
        ProviderText.Text = "划词翻译";
        SourceText.Text = LanguageUtil.Ellipsis(source, 200);
        ResultText.Text = message;
        ResultText.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromArgb(0xF0, 0xFF, 0xD5, 0x66));
        StatusText.Visibility = Visibility.Collapsed;
        PhoneticText.Visibility = Visibility.Collapsed;
        // 发音功能暂时关闭
        // _speechText = "";
        // _speakUrl = "";
        // SetSpeakButtons(false);
        ShowAt(screenX, screenY);
    }

    /// <summary>在当前气泡上显示错误信息（保留原文）</summary>
    public void ShowError(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
        // 发音功能暂时关闭
        // SetSpeakButtons(false);
        ClampPosition();
    }

    /// <summary>隐藏气泡</summary>
    public void HidePopup()
    {
        if (!IsVisible)
            return;

        try
        {
            Hide();
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>退出程序时真正关闭窗口</summary>
    public void ClosePopup()
    {
        _closed = true;
        try
        {
            Close();
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>鼠标物理像素坐标是否落在气泡上</summary>
    public bool ContainsPixelPoint(int x, int y) => ScreenGeometry.ContainsPixelPoint(this, x, y);

    // 发音功能暂时关闭
    /*
    private void SetSpeakButtons(bool visible)
    {
        if (!visible)
        {
            SpeakUsButton.Visibility = Visibility.Collapsed;
            SpeakUkButton.Visibility = Visibility.Collapsed;
            SpeakButton.Visibility = Visibility.Collapsed;
            return;
        }

        var isAscii = LanguageUtil.IsAscii(_speechText);
        // 英文提供英式 / 美式两个按钮，中文只提供一个
        SpeakUsButton.Visibility = isAscii ? Visibility.Visible : Visibility.Collapsed;
        SpeakUkButton.Visibility = isAscii ? Visibility.Visible : Visibility.Collapsed;
        SpeakButton.Visibility = isAscii ? Visibility.Collapsed : Visibility.Visible;
    }
    */

    private void ShowAt(int screenX, int screenY)
    {
        if (_closed)
            return;

        _anchorX = screenX;
        _anchorY = screenY;

        var scale = ScreenGeometry.GetScale(this);
        Left = screenX / scale + 16;
        Top = screenY / scale + 20;

        if (!IsVisible)
            Show();

        UpdateLayout();
        ClampPosition();

        DebugTrace.Log($"popup px=({screenX},{screenY}) scale={scale:F3} dip=({Left:F1},{Top:F1}) size=({ActualWidth:F0}x{ActualHeight:F0})");
    }

    private void ClampPosition()
    {
        if (_closed || !IsVisible || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        try
        {
            var scale = ScreenGeometry.GetScale(this);
            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(_anchorX, _anchorY));
            var area = screen.WorkingArea;

            var minLeft = area.Left / scale;
            var minTop = area.Top / scale;
            var maxLeft = area.Right / scale - ActualWidth - 2;
            var maxTop = area.Bottom / scale - ActualHeight - 2;

            if (maxLeft < minLeft)
                maxLeft = minLeft;
            if (maxTop < minTop)
                maxTop = minTop;

            var left = Left;
            var top = Top;

            if (left > maxLeft)
                left = maxLeft;
            if (left < minLeft)
                left = minLeft;

            if (top > maxTop)
            {
                // 下方放不下就翻到鼠标上方
                top = _anchorY / scale - ActualHeight - 12;
                if (top < minTop)
                    top = maxTop;
            }
            if (top < minTop)
                top = minTop;

            Left = left;
            Top = top;
        }
        catch
        {
            // 屏幕信息获取失败时保持原位
        }
    }

    // 发音功能暂时关闭
    /*
    private void SpeakUsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_speechText))
            SpeakRequested?.Invoke(_speechText, true);
    }

    private void SpeakUkButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_speechText))
            SpeakRequested?.Invoke(_speechText, false);
    }

    private void SpeakButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_speechText))
            SpeakRequested?.Invoke(_speechText, true);
    }
    */

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = ResultText.Text;
            if (!string.IsNullOrWhiteSpace(text))
                Clipboard.SetText(text);

            CopyButton.Content = "已复制";

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
            timer.Tick += (s, args) =>
            {
                timer.Stop();
                CopyButton.Content = "复制";
            };
            timer.Start();
        }
        catch
        {
            // 剪贴板被占用时忽略
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        HidePopup();
    }
}
