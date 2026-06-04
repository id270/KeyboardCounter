using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
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

        // 初始化键盘钩子
        _keyboardHook = new KeyboardHook();
        _keyboardHook.OnKeyDown += OnKeyDown;
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
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "键盘计数器",
            Visible = true,
            Icon = CreateIcon()
        };

        var contextMenu = new Forms.ContextMenuStrip();

        contextMenu.Items.Add("显示窗口", null, (s, e) =>
        {
            Show();
            WindowState = WindowState.Normal;
        });

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
        Dispatcher.Invoke(() =>
        {
            _totalCount++;
            if (vkCode == VK_SPACE) _spaceCount++;
            else if (vkCode == VK_RETURN) _enterCount++;
            UpdateDisplay();
        });
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
}
