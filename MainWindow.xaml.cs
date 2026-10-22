using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace KeyboardCounter;

public partial class MainWindow : Window
{
    private readonly KeyboardHook _keyboardHook;
    private Forms.NotifyIcon _notifyIcon = null!;
    private Forms.ContextMenuStrip _trayMenu = null!;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _weatherTimer;
    private readonly Stopwatch _stopwatch;
    private readonly IniConfig _config;

    private int _totalCount;
    private int _spaceCount;
    private int _enterCount;

    // 网络统计
    private long _lastBytesSent;
    private long _lastBytesReceived;
    private double _uploadSpeed;
    private double _downloadSpeed;
    private double _totalDownloadGB;

    // 天气
    private string _weatherIcon = "🌤";
    private string _weatherTemp = "--°";

    // 网络供应商
    private string _ispName = "-";

    // 布局方向
    private bool _isVertical;

    // 最小/最大纵向宽度
    private const double MinVerticalWidth = 150;
    private const double MaxVerticalWidth = 300;

    // 托盘菜单项
    private Forms.ToolStripMenuItem _horizontalItem = null!;
    private Forms.ToolStripMenuItem _verticalItem = null!;

    // 开机启动
    private const string RunRegistryKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string AppName = "KeyboardCounter";

    // 虚拟键码
    private const int VK_SPACE = 0x20;
    private const int VK_RETURN = 0x0D;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_T = 0x54;
    private const int VK_CONTROL = 0x11;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_MENU = 0x12;
    private const int VK_LMENU = 0xA4;
    private const int VK_RMENU = 0xA5;

    private bool _ctrlDown;
    private bool _altDown;

    // 划词翻译
    private TranslateSettings _translateSettings = null!;
    private SelectionWatcher? _selectionWatcher;
    private TranslatePopup? _translatePopup;
    // 发音功能暂时关闭（见 TranslateSelectionAsync 里注释掉的部分）
    // private PronunciationService? _speaker;
    private TranslationCache? _translationCache;
    private EcdictDictionary? _dictionary;
    private CancellationTokenSource? _translateCts;

    // 划词翻译托盘菜单项
    private Forms.ToolStripMenuItem _translateEnabledItem = null!;
    private Forms.ToolStripMenuItem _translateYoudaoItem = null!;
    private Forms.ToolStripMenuItem _translateBaiduItem = null!;
    private Forms.ToolStripMenuItem _selectionModeUiaItem = null!;
    private Forms.ToolStripMenuItem _selectionModeMixedItem = null!;
    private Forms.ToolStripMenuItem _selectionModeClipboardItem = null!;
    private int _lastUiaFailNoticeTick;
    private string _lastFailedProcessName = "";

    // 网络变化监听
    private DateTime _lastNetworkChangeTime = DateTime.MinValue;
    private bool _isFetchingWeather;
    private readonly object _networkChangeLock = new();

    // IP 变化检测
    private string _lastIpAddress = "";
    private int _ipCheckCounter = 0; // 每 30 秒检查一次 IP

    // 每日统计
    private readonly DailyStats _dailyStats;
    private int _saveCounter = 0; // 每分钟保存一次统计

    public MainWindow()
    {
        InitializeComponent();

        // 加载配置
        var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini");
        _config = new IniConfig(configPath);
        _isVertical = _config.GetBool("Display", "Vertical", false);

        // 加载划词翻译配置
        _translateSettings = TranslateSettings.Load(_config);
        DebugTrace.Enabled = _translateSettings.Debug;
        // 发音功能暂时关闭
        // _speaker = new PronunciationService();

        // 本地翻译缓存：程序目录\cache\translate_cache.db
        var cacheDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");
        _translationCache = new TranslationCache(cacheDirectory);

        // 离线词典：程序目录\dict\dictionary.db（由 tools\EcdictImporter 生成）
        _dictionary = EcdictDictionary.TryOpen(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dict", "dictionary.db"));

        // 预热一次：首次查询要加载索引页，先跑一次避免第一次划词多等几十毫秒
        _dictionary?.TryLookup("warmup", out _);

        // 应用布局
        ApplyLayout();

        // 加载每日统计数据
        var statsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "daily_stats.json");
        _dailyStats = new DailyStats(statsPath);
        var (total, space, enter) = _dailyStats.Get(DateTime.Today);
        _totalCount = total;
        _spaceCount = space;
        _enterCount = enter;

        // 初始化网络统计
        InitializeNetworkCounters();

        // 初始化计时器
        _stopwatch = Stopwatch.StartNew();
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += Timer_Tick;
        _timer.Start();

        // 天气更新定时器（每 5 分钟更新一次）
        _weatherTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(5)
        };
        _weatherTimer.Tick += WeatherTimer_Tick;
        _weatherTimer.Start();

        // 立即获取天气和ISP，并初始化 IP 地址
        _ = InitializeIpAndFetchWeatherAsync();

        UpdateDisplay();

        // 设置窗口位置：右下角
        PositionWindow();

        // 初始化系统托盘
        InitializeTrayIcon();

        // 初始化划词翻译（等窗口显示、消息循环就绪后再装鼠标钩子）
        Loaded += MainWindow_Loaded;

        // 初始化键盘钩子
        _keyboardHook = new KeyboardHook();
        _keyboardHook.OnKeyDown += OnKeyDown;
        _keyboardHook.OnKeyUp += OnKeyUp;
        _keyboardHook.Start();

        // 注册网络变化事件监听
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
    }

    // 初始化 IP 并获取天气和 ISP
    private async Task InitializeIpAndFetchWeatherAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            _lastIpAddress = await client.GetStringAsync("https://api.ipify.org");
        }
        catch
        {
            _lastIpAddress = "";
        }
        
        await FetchWeatherAndISPAsync();
    }

    // 网络变化事件处理（含防抖）
    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        lock (_networkChangeLock)
        {
            // 防抖：2 秒内只触发一次
            var now = DateTime.Now;
            if ((now - _lastNetworkChangeTime).TotalSeconds < 2)
                return;

            // 如果正在更新中，跳过
            if (_isFetchingWeather)
                return;

            _lastNetworkChangeTime = now;
        }

        // 在 UI 线程上触发更新（延迟等待网络稳定）
        Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                _isFetchingWeather = true;
                
                // 等待网络稳定（延迟 3 秒）
                await Task.Delay(3000);
                
                // 重试机制：最多尝试 3 次
                for (int i = 0; i < 3; i++)
                {
                    if (IsNetworkConnected())
                    {
                        await FetchWeatherAndISPAsync();
                        break;
                    }
                    // 网络未恢复，等待后重试
                    await Task.Delay(2000);
                }
            }
            finally
            {
                _isFetchingWeather = false;
            }
        });
    }

    // 应用布局
    private void ApplyLayout()
    {
        if (_isVertical)
        {
            Width = MinVerticalWidth; // 先设置最小宽度，后续会动态调整
            Height = 150;
            HorizontalLayout.Visibility = Visibility.Collapsed;
            VerticalLayout.Visibility = Visibility.Visible;
        }
        else
        {
            Width = 610;
            Height = 32;
            HorizontalLayout.Visibility = Visibility.Visible;
            VerticalLayout.Visibility = Visibility.Collapsed;
        }
    }

    // 切换布局
    private void ToggleLayout(bool vertical)
    {
        _isVertical = vertical;
        _config.SetBool("Display", "Vertical", vertical);
        _config.Save();

        // 更新托盘菜单勾选状态
        UpdateLayoutMenuCheckState();

        ApplyLayout();
        PositionWindow();
        UpdateDisplay();
    }

    private void UpdateLayoutMenuCheckState()
    {
        _horizontalItem.Checked = !_isVertical;
        _verticalItem.Checked = _isVertical;
    }

    private async void WeatherTimer_Tick(object? sender, EventArgs e)
    {
        await FetchWeatherAndISPAsync();
    }

    private async Task FetchWeatherAndISPAsync()
    {
        if (!IsNetworkConnected())
        {
            // 只有值变化时才更新
            if (_ispName != "-" || _weatherIcon != "🌤" || _weatherTemp != "--°")
            {
                _ispName = "-";
                _weatherIcon = "🌤";
                _weatherTemp = "--°";
                Dispatcher.Invoke(() => UpdateDisplay());
            }
            return;
        }

        // 保存旧值
        var oldIcon = _weatherIcon;
        var oldTemp = _weatherTemp;
        var oldIsp = _ispName;

        // 先获取 ISP（运营商）信息
        await FetchISPAsync();

        // 再尝试多个天气 API
        bool success = await TryWttrInAsync();

        if (!success)
        {
            success = await TryOpenMeteoAsync();
        }

        if (!success)
        {
            success = await TryWeatherApiAsync();
        }

        if (!success)
        {
            _weatherIcon = "🌤";
            _weatherTemp = "--°";
        }

        // 只有值变化时才更新显示
        if (_weatherIcon != oldIcon || _weatherTemp != oldTemp || _ispName != oldIsp)
        {
            Dispatcher.Invoke(() => UpdateDisplay());
        }
    }

    // 获取 ISP（运营商）信息
    private async Task FetchISPAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            
            // 使用 ip-api.com 获取真正的 ISP 信息
            var response = await client.GetStringAsync("http://ip-api.com/json/");
            var json = JsonDocument.Parse(response);
            var root = json.RootElement;

            if (root.TryGetProperty("isp", out var ispProp))
            {
                var isp = ispProp.GetString();
                if (!string.IsNullOrEmpty(isp))
                {
                    // 简化 ISP 名称
                    _ispName = SimplifyISPName(isp);
                    return;
                }
            }
        }
        catch { }

        // 备用：使用 ipapi.co
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await client.GetStringAsync("https://ipapi.co/json/");
            var json = JsonDocument.Parse(response);
            var root = json.RootElement;

            if (root.TryGetProperty("org", out var orgProp))
            {
                var org = orgProp.GetString();
                if (!string.IsNullOrEmpty(org))
                {
                    _ispName = SimplifyISPName(org);
                    return;
                }
            }
        }
        catch { }

        _ispName = "-";
    }

    // 简化 ISP 名称
    private static string SimplifyISPName(string isp)
    {
        var ispLower = isp.ToLower();
        
        // 常见中国运营商简化
        if (ispLower.Contains("china telecom") || ispLower.Contains("chinanet") || isp.Contains("中国电信") || isp.Contains("电信"))
            return "中国电信";
        if (ispLower.Contains("china unicom") || ispLower.Contains("unicom") || isp.Contains("中国联通") || isp.Contains("联通"))
            return "中国联通";
        if (ispLower.Contains("china mobile") || ispLower.Contains("cmcc") || isp.Contains("中国移动") || isp.Contains("移动"))
            return "中国移动";
        if (ispLower.Contains("cernet") || isp.Contains("教育网"))
            return "教育网";
        
        // 移除 AS 号（如 "AS4134 China Telecom..."）
        if (isp.StartsWith("AS"))
        {
            var spaceIndex = isp.IndexOf(' ');
            if (spaceIndex > 0)
                isp = isp.Substring(spaceIndex + 1);
        }
        
        // 截断过长的名称
        return isp.Length > 15 ? isp.Substring(0, 15) : isp;
    }

    // 主 API: wttr.in
    private async Task<bool> TryWttrInAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            client.DefaultRequestHeaders.Add("User-Agent", "curl/7.64.1");

            var response = await client.GetStringAsync("https://wttr.in/?format=j1");
            var json = JsonDocument.Parse(response);
            var root = json.RootElement;

            if (root.TryGetProperty("current_condition", out var currentCondition) && currentCondition.GetArrayLength() > 0)
            {
                var current = currentCondition[0];
                if (current.TryGetProperty("temp_C", out var tempProp))
                    _weatherTemp = $"{tempProp.GetString()}°";
                if (current.TryGetProperty("weatherCode", out var codeProp))
                    _weatherIcon = GetWeatherEmoji(codeProp.GetString());
            }

            return true;
        }
        catch { return false; }
    }

    // 备用 API 1: Open-Meteo (免费，无需 API Key)
    private async Task<bool> TryOpenMeteoAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

            // 先获取 IP 位置
            var ipResponse = await client.GetStringAsync("https://ipapi.co/json/");
            var ipJson = JsonDocument.Parse(ipResponse);
            var ipRoot = ipJson.RootElement;

            string lat = "0", lon = "0";
            if (ipRoot.TryGetProperty("latitude", out var latProp))
                lat = latProp.ToString();
            if (ipRoot.TryGetProperty("longitude", out var lonProp))
                lon = lonProp.ToString();

            // 获取天气
            var weatherUrl = $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current_weather=true";
            var weatherResponse = await client.GetStringAsync(weatherUrl);
            var weatherJson = JsonDocument.Parse(weatherResponse);
            var weatherRoot = weatherJson.RootElement;

            if (weatherRoot.TryGetProperty("current_weather", out var currentWeather))
            {
                if (currentWeather.TryGetProperty("temperature", out var tempProp))
                    _weatherTemp = $"{tempProp.GetDouble():F0}°";
                if (currentWeather.TryGetProperty("weathercode", out var codeProp))
                    _weatherIcon = GetOpenMeteoEmoji(codeProp.GetInt32());
            }

            return true;
        }
        catch { return false; }
    }

    // 备用 API 2: ip-api.com + 7Timer
    private async Task<bool> TryWeatherApiAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

            // 获取 IP 位置
            var ipResponse = await client.GetStringAsync("http://ip-api.com/json/");
            var ipJson = JsonDocument.Parse(ipResponse);
            var ipRoot = ipJson.RootElement;

            string lat = "0", lon = "0";
            if (ipRoot.TryGetProperty("lat", out var latProp))
                lat = latProp.ToString();
            if (ipRoot.TryGetProperty("lon", out var lonProp))
                lon = lonProp.ToString();

            // 使用 7Timer API 获取天气
            var weatherUrl = $"http://www.7timer.info/bin/api.pl?lon={lon}&lat={lat}&product=civil&output=json";
            var weatherResponse = await client.GetStringAsync(weatherUrl);
            var weatherJson = JsonDocument.Parse(weatherResponse);
            var weatherRoot = weatherJson.RootElement;

            if (weatherRoot.TryGetProperty("dataseries", out var dataSeries) && dataSeries.GetArrayLength() > 0)
            {
                var firstData = dataSeries[0];
                if (firstData.TryGetProperty("temp2m", out var tempProp))
                    _weatherTemp = $"{tempProp.GetInt32()}°";
                if (firstData.TryGetProperty("weather", out var weatherProp))
                    _weatherIcon = Get7TimerEmoji(weatherProp.GetString());
            }

            return true;
        }
        catch { return false; }
    }

    // Open-Meteo 天气代码转 Emoji
    private static string GetOpenMeteoEmoji(int code)
    {
        return code switch
        {
            0 => "☀️",
            1 or 2 or 3 => "⛅",
            45 or 48 => "🌫",
            51 or 53 or 55 => "🌦",
            61 or 63 or 65 => "🌧",
            71 or 73 or 75 => "🌨",
            80 or 81 or 82 => "🌧",
            95 or 96 or 99 => "⛈",
            _ => "🌤"
        };
    }

    // 7Timer 天气代码转 Emoji
    private static string Get7TimerEmoji(string? weather)
    {
        return weather?.ToLower() switch
        {
            "clear" => "☀️",
            "cloudy" or "pcloudy" => "⛅",
            "mcloudy" => "☁️",
            "fog" => "🌫",
            "rain" or "lrain" => "🌧",
            "snow" or "lsnow" => "🌨",
            "ts" or "tsrain" => "⛈",
            _ => "🌤"
        };
    }

    private static bool IsNetworkConnected()
    {
        var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
        return interfaces.Any(ni =>
            ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
            ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback);
    }

    private static string GetWeatherEmoji(string? code)
    {
        return code switch
        {
            "113" => "☀️",
            "116" => "🌤",
            "119" => "⛅",
            "122" => "☁️",
            "143" or "248" or "260" => "🌫",
            "176" or "263" or "266" => "🌦",
            "179" or "182" or "185" or "281" or "284" or "293" or "294" or "299" or "302" or "305" or "308" or "311" or "314" or "317" or "320" => "🌧",
            "323" or "326" or "329" or "332" or "335" or "338" or "350" or "353" or "356" or "359" => "⛈",
            "227" or "230" => "🌨",
            "200" or "386" or "389" or "392" or "395" => "⛈",
            _ => "🌤"
        };
    }

    private void InitializeNetworkCounters()
    {
        var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
        long totalSent = 0;
        long totalReceived = 0;

        foreach (var ni in interfaces)
        {
            if (ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            {
                var stats = ni.GetIPv4Statistics();
                totalSent += stats.BytesSent;
                totalReceived += stats.BytesReceived;
            }
        }

        _lastBytesSent = totalSent;
        _lastBytesReceived = totalReceived;
        _uploadSpeed = 0;
        _downloadSpeed = 0;
    }

    private void UpdateNetworkSpeed()
    {
        var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
        long totalSent = 0;
        long totalReceived = 0;

        foreach (var ni in interfaces)
        {
            if (ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            {
                var stats = ni.GetIPv4Statistics();
                totalSent += stats.BytesSent;
                totalReceived += stats.BytesReceived;
            }
        }

        _uploadSpeed = (totalSent - _lastBytesSent) / (1024.0 * 1024.0);
        _downloadSpeed = (totalReceived - _lastBytesReceived) / (1024.0 * 1024.0);
        _totalDownloadGB += _downloadSpeed / 1024.0;

        _lastBytesSent = totalSent;
        _lastBytesReceived = totalReceived;
    }

    private static string GetCountEmoji(int count)
    {
        return count switch
        {
            < 1000 => "😐",
            < 5000 => "🙂",
            < 10000 => "😊",
            < 20000 => "😃",
            < 50000 => "😄",
            < 100000 => "😆",
            _ => "🤯"
        };
    }

    #region 开机启动功能

    private bool IsAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, false);
        var value = key?.GetValue(AppName);
        return value != null;
    }

    private void SetAutoStart(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, true);
        if (key == null) return;

        if (enable)
        {
            var exePath = Environment.ProcessPath;
            key.SetValue(AppName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(AppName, false);
        }
    }

    #endregion

    private void InitializeTrayIcon()
    {
        // 注意：Icon 一定要在 Visible 之前赋值，否则 NIM_ADD 时图标是空的
        var icon = CreateIcon();
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "键盘计数器",
            Icon = icon,
            Visible = true
        };

        ScheduleTrayIconReRegister();

        var contextMenu = new Forms.ContextMenuStrip();
        _trayMenu = contextMenu;

        contextMenu.Items.Add("显示窗口", null, (s, e) =>
        {
            Show();
            WindowState = WindowState.Normal;
        });

        contextMenu.Items.Add("划词翻译设置...", null, (s, e) => ShowTranslateConfig());

        contextMenu.Items.Add(new Forms.ToolStripSeparator());

        contextMenu.Items.Add("重置计数", null, (s, e) =>
        {
            _totalCount = 0;
            _spaceCount = 0;
            _enterCount = 0;
            _stopwatch.Restart();
            // 同步清零当天 JSON 数据
            _dailyStats.Reset(DateTime.Today);
            UpdateDisplay();
        });

        contextMenu.Items.Add(new Forms.ToolStripSeparator());

        // 布局选择
        var layoutItem = new Forms.ToolStripMenuItem("布局");
        _horizontalItem = new Forms.ToolStripMenuItem("横向显示");
        _horizontalItem.Click += (s, e) => ToggleLayout(false);
        _verticalItem = new Forms.ToolStripMenuItem("纵向显示");
        _verticalItem.Click += (s, e) => ToggleLayout(true);

        // 设置初始勾选状态
        _horizontalItem.Checked = !_isVertical;
        _verticalItem.Checked = _isVertical;

        layoutItem.DropDownItems.Add(_horizontalItem);
        layoutItem.DropDownItems.Add(_verticalItem);
        contextMenu.Items.Add(layoutItem);

        contextMenu.Items.Add(new Forms.ToolStripSeparator());

        // 划词翻译
        var translateMenu = new Forms.ToolStripMenuItem("划词翻译");

        _translateEnabledItem = new Forms.ToolStripMenuItem("启用划词翻译");
        _translateEnabledItem.Click += (s, e) => ToggleTranslateEnabled();
        translateMenu.DropDownItems.Add(_translateEnabledItem);

        translateMenu.DropDownItems.Add(new Forms.ToolStripSeparator());

        var providerMenu = new Forms.ToolStripMenuItem("翻译接口");

        _translateYoudaoItem = new Forms.ToolStripMenuItem("有道翻译");
        _translateYoudaoItem.Click += (s, e) => SetTranslateProvider(TranslateProvider.Youdao);
        providerMenu.DropDownItems.Add(_translateYoudaoItem);

        _translateBaiduItem = new Forms.ToolStripMenuItem("百度翻译");
        _translateBaiduItem.Click += (s, e) => SetTranslateProvider(TranslateProvider.Baidu);
        providerMenu.DropDownItems.Add(_translateBaiduItem);

        translateMenu.DropDownItems.Add(providerMenu);

        // 取词方式
        var selectionModeMenu = new Forms.ToolStripMenuItem("取词方式");

        _selectionModeUiaItem = new Forms.ToolStripMenuItem("仅 UIA（不碰剪贴板）");
        _selectionModeUiaItem.Click += (s, e) => ApplySelectionMode(SelectionMode.UiaOnly);
        selectionModeMenu.DropDownItems.Add(_selectionModeUiaItem);

        _selectionModeMixedItem = new Forms.ToolStripMenuItem("UIA + 剪贴板兜底");
        _selectionModeMixedItem.Click += (s, e) => ApplySelectionMode(SelectionMode.UiaWithClipboard);
        selectionModeMenu.DropDownItems.Add(_selectionModeMixedItem);

        _selectionModeClipboardItem = new Forms.ToolStripMenuItem("仅剪贴板");
        _selectionModeClipboardItem.Click += (s, e) => ApplySelectionMode(SelectionMode.ClipboardOnly);
        selectionModeMenu.DropDownItems.Add(_selectionModeClipboardItem);

        translateMenu.DropDownItems.Add(selectionModeMenu);

        var whitelistItem = new Forms.ToolStripMenuItem("把上一个取词失败的程序加入白名单");
        whitelistItem.Click += (s, e) => AddLastFailedProcessToWhitelist();
        translateMenu.DropDownItems.Add(whitelistItem);

        var configItem = new Forms.ToolStripMenuItem("翻译接口配置...");
        configItem.Click += (s, e) => ShowTranslateConfig();
        translateMenu.DropDownItems.Add(configItem);

        var clipboardItem = new Forms.ToolStripMenuItem("翻译剪贴板 (Ctrl+Alt+T)");
        clipboardItem.Click += (s, e) => TranslateClipboardAsync();
        translateMenu.DropDownItems.Add(clipboardItem);

        contextMenu.Items.Add(translateMenu);

        contextMenu.Items.Add(new Forms.ToolStripSeparator());

        var autoStartItem = new Forms.ToolStripMenuItem("开机启动");
        autoStartItem.Click += (s, e) =>
        {
            var item = (Forms.ToolStripMenuItem)s!;
            var enable = !item.Checked;
            SetAutoStart(enable);
            item.Checked = enable;
        };
        autoStartItem.Checked = IsAutoStartEnabled();
        contextMenu.Items.Add(autoStartItem);

        contextMenu.Items.Add(new Forms.ToolStripSeparator());

        contextMenu.Items.Add("退出", null, (s, e) => Close());

        _notifyIcon.ContextMenuStrip = contextMenu;
        _notifyIcon.DoubleClick += (s, e) =>
        {
            Show();
            WindowState = WindowState.Normal;
        };
    }

    /// <summary>
    /// 托盘图标偶尔会被 Explorer 丢掉（尤其是程序被强杀后，同一个 exe 路径会留下失效的托盘记录）。
    /// 启动一段时间后重新注册一次，并记一条日志，便于排查"托盘看不到"。
    /// </summary>
    private void ScheduleTrayIconReRegister()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            TryReRegisterTrayIcon();
        };
        timer.Start();
    }

    private void TryReRegisterTrayIcon()
    {
        try
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Visible = true;
            DebugTrace.Log($"tray icon re-registered, text=[{_notifyIcon.Text}]");
        }
        catch (Exception ex)
        {
            DebugTrace.Log("tray icon re-register failed: " + ex.Message);
        }
    }

    private Drawing.Icon CreateIcon()
    {
        using var bitmap = new Drawing.Bitmap(32, 32);
        using var g = Drawing.Graphics.FromImage(bitmap);
        g.Clear(Drawing.Color.Transparent);
        g.FillEllipse(Drawing.Brushes.DeepSkyBlue, 2, 2, 28, 28);
        using var font = new Drawing.Font("Arial", 16, Drawing.FontStyle.Bold);
        g.DrawString("K", font, Drawing.Brushes.White, 5, 3);
        var hIcon = bitmap.GetHicon();
        return Drawing.Icon.FromHandle(hIcon);
    }

    #region 划词翻译

    private static readonly bool DebugEnabled = Environment.GetEnvironmentVariable("KC_DEBUG") == "1";
    private static void Dbg(string message)
    {
        DebugTrace.Log(message);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        InitializeTranslation();
        _ = ShowFirstRunSetupAsync();
    }

    /// <summary>按当前配置启动/停止划词监听</summary>
    private void InitializeTranslation()
    {
        Dbg($"InitializeTranslation enabled={_translateSettings.Enabled} provider={_translateSettings.Provider} configured={_translateSettings.IsConfigured} maxLen={_translateSettings.MaxLength}");

        if (_translateSettings.Enabled)
        {
            _selectionWatcher ??= CreateSelectionWatcher();
            _selectionWatcher.Start();

            // 预创建并预热气泡窗口，避免第一次划词时才解析 XAML / 建窗口 / 建 DWM 表面
            var popup = EnsurePopup();
            Dispatcher.BeginInvoke(new Action(() => popup.WarmUp()), DispatcherPriority.Background);
            Dbg($"watcher started running={_selectionWatcher.IsRunning}");
        }
        else
        {
            _selectionWatcher?.Stop();
            _translatePopup?.HidePopup();
        }

        UpdateTranslateMenuState();
    }

    private SelectionWatcher CreateSelectionWatcher()
    {
        var watcher = new SelectionWatcher(Dispatcher)
        {
            Mode = _translateSettings.SelectionMode,
            ClipboardFallbackProcesses = _translateSettings.GetClipboardFallbackProcessList()
        };
        watcher.SelectionCaptured += OnSelectionCaptured;
        watcher.LeftButtonPressed += OnGlobalLeftButtonDown;
        watcher.SelectionCapturePending += (x, y) => EnsurePopup().ShowPending(x, y);
        watcher.SelectionCaptureFailed += OnSelectionCaptureFailed;
        return watcher;
    }

    /// <summary>没取到文字时：关掉占位气泡，记下是哪个程序，并偶尔提示一次怎么绕过</summary>
    private void OnSelectionCaptureFailed(int screenX, int screenY)
    {
        _translatePopup?.HidePopup();

        var processName = SelectionCapture.GetProcessNameAt(screenX, screenY)
                          ?? SelectionCapture.GetForegroundProcessName();
        if (!string.IsNullOrEmpty(processName) &&
            !processName.Equals("KeyboardCounter.exe", StringComparison.OrdinalIgnoreCase))
        {
            _lastFailedProcessName = processName;
        }

        if (_translateSettings.SelectionMode != SelectionMode.UiaOnly)
            return;

        var now = Environment.TickCount;
        if (now - _lastUiaFailNoticeTick < 30000)
            return;

        _lastUiaFailNoticeTick = now;
        ShowTranslateBalloon(string.IsNullOrEmpty(_lastFailedProcessName)
            ? "这个窗口不支持直接取词（UIA）。可以在托盘菜单把该程序加入取词白名单，或把取词方式改成「UIA + 剪贴板兜底」。"
            : $"{_lastFailedProcessName} 不支持直接取词（UIA）。托盘菜单 → 划词翻译 → 把「{_lastFailedProcessName}」加入取词白名单 即可用剪贴板兜底（仅对该程序生效）。");
    }

    /// <summary>把上一次取词失败的程序加入白名单（仅它允许用剪贴板兜底）</summary>
    private void AddLastFailedProcessToWhitelist()
    {
        if (string.IsNullOrEmpty(_lastFailedProcessName))
        {
            ShowTranslateBalloon("请先在目标程序里划一次词，我再把它加入白名单。");
            return;
        }

        var list = new List<string>(_translateSettings.GetClipboardFallbackProcessList());
        if (list.Exists(n => n.Equals(_lastFailedProcessName, StringComparison.OrdinalIgnoreCase)))
        {
            ShowTranslateBalloon($"{_lastFailedProcessName} 已经在取词白名单里了。");
            return;
        }

        list.Add(_lastFailedProcessName);
        _translateSettings.ClipboardFallbackProcesses = string.Join(",", list);
        SaveTranslateSettings();

        _selectionWatcher?.Dispose();
        _selectionWatcher = null;
        InitializeTranslation();

        ShowTranslateBalloon($"已把 {_lastFailedProcessName} 加入取词白名单：只有该程序会用剪贴板兜底，其它程序仍然完全不碰剪贴板。");
    }

    private async void OnSelectionCaptured(string text, int screenX, int screenY)
    {
        Dbg($"OnSelectionCaptured at ({screenX},{screenY}) len={text.Length}");
        try
        {
            await TranslateSelectionAsync(text, screenX, screenY);
        }
        catch (Exception ex)
        {
            // 划词翻译不能影响主程序
            Dbg("OnSelectionCaptured exception: " + ex);
        }
    }

    private async Task TranslateSelectionAsync(string text, int screenX, int screenY)
    {
        if (!_translateSettings.Enabled)
            return;

        var started = Environment.TickCount;

        // 在自己的窗口里划词不处理
        if (ScreenGeometry.ContainsPixelPoint(this, screenX, screenY))
            return;
        if (_translatePopup != null && _translatePopup.ContainsPixelPoint(screenX, screenY))
            return;

        if (text.Length > _translateSettings.MaxLength)
        {
            Dbg("skipped: too long");
            return;
        }

        var popup = EnsurePopup();
        var provider = TranslationService.Create(_translateSettings);
        Dbg($"provider={(provider == null ? "null" : provider.Name)}");

        // 1) 离线词典优先：查到就直接显示（音标 + 多义项，不走网络、不需要密钥）
        var dictionaryWatch = System.Diagnostics.Stopwatch.StartNew();
        if (_dictionary is { IsAvailable: true } && _dictionary.TryLookup(text, out var dictionaryEntry))
        {
            dictionaryWatch.Stop();
            popup.ShowDictionary(text, dictionaryEntry, screenX, screenY);
            Dbg($"dictionary hit: {dictionaryEntry.Word}, lookup={dictionaryWatch.Elapsed.TotalMilliseconds:F2}ms, popup +{Environment.TickCount - started}ms");
            return;
        }

        dictionaryWatch.Stop();
        Dbg($"dictionary miss ({dictionaryWatch.Elapsed.TotalMilliseconds:F2}ms)");

        if (provider == null)
        {
            popup.ShowNotice(text,
                $"离线词典里没有这个词，而且还没有配置{_translateSettings.ProviderDisplayName}的密钥。\n右键托盘图标 → 划词翻译 → 翻译接口配置",
                screenX, screenY);
            return;
        }

        // 2) 本地缓存：之前翻译过就直接用
        if (_translationCache != null && _translationCache.TryGet(provider.Name, text, out var cached))
        {
            Dbg($"cache hit: {provider.Name} / {text.Length} chars");
            popup.ShowResult(cached, screenX, screenY);
            return;
        }

        Dbg("cache miss");

        _translateCts?.Cancel();
        _translateCts?.Dispose();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _translateCts = cts;

        popup.ShowLoading(text, screenX, screenY, provider.Name);

        try
        {
            var result = await provider.TranslateAsync(text, cts.Token);
            if (cts.IsCancellationRequested)
                return;

            popup.ShowResult(result, screenX, screenY);

            // 成功的结果写进本地缓存，下次同样的词/短语直接命中
            _translationCache?.Put(provider.Name, text, result);

            // 发音功能暂时关闭（保留代码，需要时取消注释即可）
            // if (_translateSettings.AutoSpeak)
            // {
            //     var sourceIsChinese = LanguageUtil.ContainsChinese(result.SourceText);
            //     var speakText = sourceIsChinese ? result.TranslatedText : result.SourceText;
            //     // 朗读的是译文时要用译文的发音地址
            //     var speakUrl = sourceIsChinese ? result.TranslatedSpeakUrl : result.SpeakUrl;
            //     _ = SpeakTextAsync(speakText, true, speakUrl);
            // }
        }
        catch (OperationCanceledException)
        {
            // 被新的划词取消，忽略
        }
        catch (TranslationException ex)
        {
            popup.ShowError(ex.Message);
        }
        catch (Exception ex)
        {
            popup.ShowError("翻译失败：" + ex.Message);
        }
    }

    // 发音功能暂时关闭
    // private async Task SpeakTextAsync(string text, bool american, string speakUrl)
    // {
    //     try
    //     {
    //         if (_speaker == null || string.IsNullOrWhiteSpace(text))
    //             return;
    //
    //         await _speaker.PlayAsync(text, speakUrl, american, CancellationToken.None);
    //     }
    //     catch
    //     {
    //         // 发音失败不影响翻译显示
    //     }
    // }

    private TranslatePopup EnsurePopup()
    {
        if (_translatePopup == null)
        {
            _translatePopup = new TranslatePopup();
            // 发音功能暂时关闭
            // _translatePopup.SpeakRequested += (text, american) =>
            //     _ = SpeakTextAsync(text, american, _translatePopup?.CurrentSpeakUrl ?? "");
        }

        return _translatePopup;
    }

    /// <summary>左键点在气泡外面就关掉气泡</summary>
    private void OnGlobalLeftButtonDown(int screenX, int screenY)
    {
        var popup = _translatePopup;
        if (popup == null || !popup.IsVisible)
            return;

        if (!popup.ContainsPixelPoint(screenX, screenY))
            popup.HidePopup();
    }

    private void ToggleTranslateEnabled()
    {
        _translateSettings.Enabled = !_translateSettings.Enabled;
        SaveTranslateSettings();
        InitializeTranslation();

        if (!_translateSettings.Enabled)
        {
            _translatePopup?.HidePopup();
            return;
        }

        ShowTranslateBalloon(_translateSettings.IsConfigured
            ? "划词翻译已开启：拖动划选或双击选词即可翻译。"
            : "划词翻译已开启，但还没有配置翻译接口。右键托盘图标 → 划词翻译 → 翻译接口配置。");
    }

    private void SetTranslateProvider(TranslateProvider provider)
    {
        _translateSettings.Provider = provider;
        SaveTranslateSettings();
        UpdateTranslateMenuState();

        ShowTranslateBalloon(_translateSettings.IsConfigured
            ? $"已切换到{_translateSettings.ProviderDisplayName}。"
            : $"已切换到{_translateSettings.ProviderDisplayName}，请先填写密钥。右键托盘图标 → 划词翻译 → 翻译接口配置。");
    }

    private void UpdateTranslateMenuState()
    {
        if (_translateEnabledItem == null || _translateYoudaoItem == null || _translateBaiduItem == null)
            return;

        _translateEnabledItem.Checked = _translateSettings.Enabled;
        _translateYoudaoItem.Checked = _translateSettings.Provider == TranslateProvider.Youdao;
        _translateBaiduItem.Checked = _translateSettings.Provider == TranslateProvider.Baidu;

        if (_selectionModeUiaItem != null)
        {
            _selectionModeUiaItem.Checked = _translateSettings.SelectionMode == SelectionMode.UiaOnly;
            _selectionModeMixedItem.Checked = _translateSettings.SelectionMode == SelectionMode.UiaWithClipboard;
            _selectionModeClipboardItem.Checked = _translateSettings.SelectionMode == SelectionMode.ClipboardOnly;
        }
    }

    /// <summary>切换取词方式：重建监听器（模式是在构造时传给它的）</summary>
    private void ApplySelectionMode(SelectionMode mode)
    {
        if (_translateSettings.SelectionMode == mode)
        {
            UpdateTranslateMenuState();
            return;
        }

        _translateSettings.SelectionMode = mode;
        SaveTranslateSettings();

        _selectionWatcher?.Dispose();
        _selectionWatcher = null;
        InitializeTranslation();

        ShowTranslateBalloon($"取词方式已切换为：{_translateSettings.SelectionModeDisplayName}");
    }

    private void ShowTranslateConfig()
    {
        _translatePopup?.HidePopup();

        var window = new ApiConfigWindow(_translateSettings);
        if (IsVisible)
            window.Owner = this;

        if (window.ShowDialog() == true)
        {
            _translateSettings = window.Settings;
            DebugTrace.Enabled = _translateSettings.Debug;
            SaveTranslateSettings();
            InitializeTranslation();

            ShowTranslateBalloon(_translateSettings.Enabled
                ? "配置已保存，划词翻译已启用。"
                : "配置已保存。");
        }
    }

    private void SaveTranslateSettings()
    {
        _translateSettings.Save(_config);
    }

    private void ShowTranslateBalloon(string message)
    {
        try
        {
            _notifyIcon.ShowBalloonTip(4000, "划词翻译", message, Forms.ToolTipIcon.Info);
        }
        catch
        {
            // 托盘气泡失败时忽略
        }
    }

    /// <summary>
    /// 首次运行引导：还没配置接口时自动弹出配置窗口（只弹一次），
    /// 之后每次启动只在悬浮窗托盘气泡里提醒。
    /// </summary>
    private async Task ShowFirstRunSetupAsync()
    {
        try
        {
            if (_translateSettings.IsConfigured)
                return;

            await Task.Delay(1200);

            if (!_translateSettings.Prompted)
            {
                _translateSettings.Prompted = true;
                SaveTranslateSettings();
                ShowTranslateConfig();
                return;
            }

            ShowTranslateBalloon(
                "划词翻译还没有配置接口。右键右下角的悬浮窗（或托盘图标）→ 划词翻译设置，填入有道/百度的密钥即可。");
        }
        catch
        {
            // 忽略
        }
    }

    #endregion

    private void PositionWindow()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 10;
        Top = workArea.Bottom - Height - 10;
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        UpdateNetworkSpeed();
        UpdateDisplay();
        
        // 每 30 秒检查一次 IP 是否变化
        _ipCheckCounter++;
        if (_ipCheckCounter >= 30)
        {
            _ipCheckCounter = 0;
            _ = CheckIpChangeAsync();
        }

        // 每分钟保存一次统计数据
        _saveCounter++;
        if (_saveCounter >= 60)
        {
            _saveCounter = 0;
            SaveDailyStats();
        }
    }

    // 保存每日统计数据
    private void SaveDailyStats()
    {
        _dailyStats.Save(DateTime.Today, _totalCount, _spaceCount, _enterCount);
    }

    // 检查 IP 是否变化
    private async Task CheckIpChangeAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var ip = await client.GetStringAsync("https://api.ipify.org");
            
            if (!string.IsNullOrEmpty(ip) && ip != _lastIpAddress)
            {
                _lastIpAddress = ip;
                // IP 变化，触发更新
                if (!_isFetchingWeather)
                {
                    await FetchWeatherAndISPAsync();
                }
            }
        }
        catch
        {
            // 忽略网络错误
        }
    }

    private void OnKeyDown(int vkCode)
    {
        var isCtrl = IsCtrlKey(vkCode);
        var isAlt = IsAltKey(vkCode);

        Dispatcher.Invoke(() =>
        {
            if (isCtrl)
                _ctrlDown = true;
            if (isAlt)
                _altDown = true;

            if (vkCode == VK_ESCAPE)
                _translatePopup?.HidePopup();

            // 全局热键：Ctrl + Alt + T 翻译剪贴板
            if (vkCode == VK_T && _ctrlDown && _altDown)
            {
                Dbg("hotkey ctrl+alt+t");
                _ctrlDown = false;
                _altDown = false;
                TranslateClipboardAsync();
                return;
            }

            _totalCount++;
            if (vkCode == VK_SPACE) _spaceCount++;
            else if (vkCode == VK_RETURN) _enterCount++;
            UpdateDisplay();
        });
    }

    private void OnKeyUp(int vkCode)
    {
        var isCtrl = IsCtrlKey(vkCode);
        var isAlt = IsAltKey(vkCode);
        if (!isCtrl && !isAlt)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            if (isCtrl)
                _ctrlDown = false;
            if (isAlt)
                _altDown = false;
        });
    }

    private static bool IsCtrlKey(int vkCode) => vkCode == VK_CONTROL || vkCode == VK_LCONTROL || vkCode == VK_RCONTROL;

    private static bool IsAltKey(int vkCode) => vkCode == VK_MENU || vkCode == VK_LMENU || vkCode == VK_RMENU;

    /// <summary>翻译剪贴板里的文字（Ctrl+Alt+T）</summary>
    private async void TranslateClipboardAsync()
    {
        try
        {
            Dbg("TranslateClipboardAsync start");

            var clipboardText = SelectionCapture.TryGetClipboardText();
            if (string.IsNullOrWhiteSpace(clipboardText))
            {
                Dbg("clipboard has no text");
                ShowTranslateBalloon("剪贴板里没有文字（或暂时被其它程序占用，稍后再试）。");
                return;
            }

            var text = LanguageUtil.Normalize(clipboardText);
            if (text.Length == 0)
            {
                ShowTranslateBalloon("剪贴板里没有文字。");
                return;
            }

            var cursor = Forms.Cursor.Position;
            await TranslateSelectionAsync(text, cursor.X, cursor.Y);
        }
        catch (Exception ex)
        {
            ShowTranslateBalloon("翻译剪贴板失败：" + ex.Message);
        }
    }

    private void UpdateDisplay()
    {
        var spacePercent = _totalCount > 0 ? (_spaceCount * 100.0 / _totalCount) : 0;
        var enterPercent = _totalCount > 0 ? (_enterCount * 100.0 / _totalCount) : 0;
        var elapsedMinutes = _stopwatch.Elapsed.TotalMinutes;
        var cpm = elapsedMinutes > 0 ? (int)(_totalCount / elapsedMinutes) : 0;

        var emoji = GetCountEmoji(_totalCount);
        var totalText = $"总计:{_totalCount}";
        var spaceText = $"空格:{_spaceCount}({spacePercent:F0}%)";
        var enterText = $"回车:{_enterCount}({enterPercent:F0}%)";
        var speedText = $"{cpm}/min";

        string uploadStr = _uploadSpeed >= 1.0 ? $"↑{_uploadSpeed:F1}MB" : $"↑{_uploadSpeed * 1024:F1}KB";
        string downloadStr = _downloadSpeed >= 1.0 ? $"↓{_downloadSpeed:F1}MB" : $"↓{_downloadSpeed * 1024:F1}KB";
        var networkText = $"{uploadStr} {downloadStr}";
        var totalDownloadText = FormatTotalDownload(_totalDownloadGB);

        // 纵向布局专用：更紧凑的网络显示（保留 1 位小数）
        string networkTextV = _uploadSpeed >= 1.0 || _downloadSpeed >= 1.0
            ? $"↑{_uploadSpeed:F1}M ↓{_downloadSpeed:F1}M"
            : $"↑{_uploadSpeed * 1024:F1}K ↓{_downloadSpeed * 1024:F1}K";

        if (_isVertical)
        {
            // 纵向布局 - 空格和回车分行显示
            WeatherIconV.Text = _weatherIcon;
            WeatherTempV.Text = _weatherTemp;
            EmojiTextV.Text = emoji;
            TotalTextV.Text = totalText;
            SpaceTextV.Text = $"空格:{_spaceCount}({spacePercent:F0}%)";
            EnterTextV.Text = $"回车:{_enterCount}({enterPercent:F0}%)";
            SpeedTextV.Text = speedText;
            NetworkTextV.Text = networkTextV;
            TotalDownloadTextV.Text = totalDownloadText;
            ISPTextV.Text = _ispName;

            // 动态调整宽度以适应ISP文字
            Dispatcher.BeginInvoke(() => AdjustVerticalWidth(), System.Windows.Threading.DispatcherPriority.Loaded);
        }
        else
        {
            // 横向布局
            WeatherIconH.Text = _weatherIcon;
            WeatherTempH.Text = _weatherTemp;
            EmojiTextH.Text = emoji;
            TotalTextH.Text = totalText;
            SpaceTextH.Text = spaceText;
            EnterTextH.Text = enterText;
            SpeedTextH.Text = speedText;
            NetworkTextH.Text = networkText;
            TotalDownloadTextH.Text = totalDownloadText;
            ISPTextH.Text = _ispName;
        }

        if (_notifyIcon != null)
            _notifyIcon.Text = $"键盘计数器 - {cpm}/min";
    }

    private void UpdateWeatherDisplay(string icon, string temp)
    {
        if (_isVertical)
        {
            WeatherIconV.Text = icon;
            WeatherTempV.Text = temp;
        }
        else
        {
            WeatherIconH.Text = icon;
            WeatherTempH.Text = temp;
        }
    }

    private void UpdateISPDisplay(string isp)
    {
        if (_isVertical)
            ISPTextV.Text = isp;
        else
            ISPTextH.Text = isp;
    }

    private static string FormatTotalDownload(double totalGB)
    {
        if (totalGB >= 1.0) return $"↓{totalGB:F2}GB";
        var totalMB = totalGB * 1024.0;
        if (totalMB >= 1.0) return $"↓{totalMB:F2}MB";
        var totalKB = totalMB * 1024.0;
        return $"↓{totalKB:F1}KB";
    }

    private void AdjustVerticalWidth()
    {
        // 使用FormattedText准确测量ISP文字宽度
        var typeface = new System.Windows.Media.Typeface("Microsoft YaHei UI");
        var fontSize = 11.0;
        var padding = 16.0; // 边框和内边距

        var formattedText = new System.Windows.Media.FormattedText(
            _ispName,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            System.Windows.Media.Brushes.Black,
            1.0);

        var newWidth = Math.Max(MinVerticalWidth, Math.Min(MaxVerticalWidth, formattedText.Width + padding));

        if (Math.Abs(Width - newWidth) > 1)
        {
            Width = newWidth;
            PositionWindow();
        }
    }

    protected override void OnStateChanged(EventArgs e)
    {
        if (WindowState == WindowState.Minimized) Hide();
        base.OnStateChanged(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _weatherTimer.Stop();
        _keyboardHook.Dispose();

        // 释放划词翻译相关资源
        _translateCts?.Cancel();
        _translateCts?.Dispose();
        _translateCts = null;
        _selectionWatcher?.Dispose();
        _selectionWatcher = null;
        _translatePopup?.ClosePopup();
        _translatePopup = null;
        // 发音功能暂时关闭
        // _speaker?.Dispose();
        // _speaker = null;
        _translationCache?.Dispose();
        _translationCache = null;
        _dictionary?.Dispose();
        _dictionary = null;

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        
        // 取消网络变化事件订阅
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        
        // 保存每日统计数据
        SaveDailyStats();
        
        base.OnClosed(e);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        DragMove();
    }

    /// <summary>在悬浮窗上右键，弹出和托盘相同的菜单（托盘图标被折叠时也能配置）</summary>
    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);

        try
        {
            _trayMenu?.Show(Forms.Cursor.Position);
            e.Handled = true;
        }
        catch
        {
            // 菜单弹出失败时忽略
        }
    }
}
