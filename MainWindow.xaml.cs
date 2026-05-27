using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace KeyboardCounter;

public partial class MainWindow : Window
{
    private readonly KeyboardHook _keyboardHook;
    private Forms.NotifyIcon _notifyIcon = null!;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _stopwatch;

    private int _totalCount;
    private int _spaceCount;
    private int _enterCount;

    // 网络统计
    private long _lastBytesSent;
    private long _lastBytesReceived;
    private double _uploadSpeed;
    private double _downloadSpeed;

    // 虚拟键码
    private const int VK_SPACE = 0x20;
    private const int VK_RETURN = 0x0D;

    public MainWindow()
    {
        InitializeComponent();

        // 初始化计数
        _totalCount = 0;
        _spaceCount = 0;
        _enterCount = 0;

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

        UpdateDisplay();

        // 设置窗口位置：右下角
        PositionWindow();

        // 初始化系统托盘
        InitializeTrayIcon();

        // 初始化键盘钩子
        _keyboardHook = new KeyboardHook();
        _keyboardHook.OnKeyDown += OnKeyDown;
        _keyboardHook.Start();
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

        _lastBytesSent = totalSent;
        _lastBytesReceived = totalReceived;
    }

    // 根据打字速度返回 emoji 表情
    private string GetSpeedEmoji(int cpm)
    {
        return cpm switch
        {
            < 50 => "😐",
            < 100 => "🙂",
            < 150 => "😊",
            < 200 => "😃",
            < 250 => "😤",
            < 300 => "😠",
            _ => "🤬"
        };
    }

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
            UpdateDisplay();
        });

        contextMenu.Items.Add(new Forms.ToolStripSeparator());

        contextMenu.Items.Add("退出", null, (s, e) =>
        {
            Close();
        });

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
    }

    private void OnKeyDown(int vkCode)
    {
        Dispatcher.Invoke(() =>
        {
            _totalCount++;

            if (vkCode == VK_SPACE)
            {
                _spaceCount++;
            }
            else if (vkCode == VK_RETURN)
            {
                _enterCount++;
            }

            UpdateDisplay();
        });
    }

    private void UpdateDisplay()
    {
        var spacePercent = _totalCount > 0 ? (_spaceCount * 100.0 / _totalCount) : 0;
        var enterPercent = _totalCount > 0 ? (_enterCount * 100.0 / _totalCount) : 0;

        var elapsedMinutes = _stopwatch.Elapsed.TotalMinutes;
        var cpm = elapsedMinutes > 0 ? (int)(_totalCount / elapsedMinutes) : 0;

        // Emoji 表情 - 直接设置 Text
        EmojiText.Text = GetSpeedEmoji(cpm);

        // 显示内容 - 完整两个字
        TotalText.Text = $"总计:{_totalCount}";
        SpaceText.Text = $"空格:{_spaceCount}({spacePercent:F0}%)";
        EnterText.Text = $"回车:{_enterCount}({enterPercent:F0}%)";
        SpeedText.Text = $"{cpm}/m";
        NetworkText.Text = $"↑{_uploadSpeed:F1} ↓{_downloadSpeed:F1}MB";

        if (_notifyIcon != null)
        {
            _notifyIcon.Text = $"键盘计数器 - {cpm}/min";
        }
    }

    protected override void OnStateChanged(EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            Hide();
        }
        base.OnStateChanged(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _keyboardHook.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        base.OnClosed(e);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        DragMove();
    }
}
