using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;

namespace KeyboardCounter;

/// <summary>
/// 读取鼠标划选的文本。
/// 做法：记录剪贴板序列号 → 模拟 Ctrl+C → 序列号变化说明确实有选中内容 → 读取后还原剪贴板。
/// </summary>
public static class SelectionCapture
{
    #region WinAPI

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;

        public POINT(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    private const byte VK_CONTROL = 0x11;
    private const byte VK_C = 0x43;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    #endregion

    /// <summary>这些窗口里 Ctrl+C 会中断进程或复制文件，绝对不能发 Ctrl+C</summary>
    private static readonly string[] BlockedWindowClasses =
    {
        "ConsoleWindowClass",
        "CASCADIA_HOSTING_WINDOW_CLASS",
        "mintty",
        "PuTTY",
        "KiTTY",
        "VirtualConsoleClass",
        "CabinetWClass",   // 资源管理器（拖动是复制文件，不是选文字）
        "Progman",         // 桌面
        "WorkerW",
        "Shell_TrayWnd",   // 任务栏

        // 截图/录屏类浮层：拖框是截屏区域，不是选中文字；
        // 在它们上面发 Ctrl+C 会把刚截的图冲掉
        "ScreenClippingHost",
        "XamlExplorerHostIslandWindow",
        "Snipaste",
        "PixPin",
        "ScreenSketch",
        "FastStoneCapture",
        "ShareX"
    };

    /// <summary>
    /// 当前是不是"不能发 Ctrl+C"的场合：终端、资源管理器、桌面、截图浮层等。
    /// 同时检查前台窗口和光标下的窗口——截图浮层不一定抢前台焦点。
    /// </summary>
    public static bool IsForegroundBlocked(int screenX, int screenY)
    {
        if (IsBlockedClass(GetWindowClassName(GetForegroundWindow())))
            return true;

        try
        {
            var under = WindowFromPoint(new POINT(screenX, screenY));
            if (under != IntPtr.Zero && IsBlockedClass(GetWindowClassName(under)))
                return true;
        }
        catch
        {
            // 忽略
        }

        return false;
    }

    private static bool IsBlockedClass(string className)
    {
        if (string.IsNullOrEmpty(className))
            return false;

        foreach (var blocked in BlockedWindowClasses)
        {
            if (string.Equals(className, blocked, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>鼠标位置下的窗口是否属于本程序（避免对自己的气泡窗口划词）</summary>
    public static bool IsOwnProcessWindow(int x, int y)
    {
        try
        {
            var hwnd = WindowFromPoint(new POINT(x, y));
            if (hwnd == IntPtr.Zero)
                return false;

            GetWindowThreadProcessId(hwnd, out var processId);
            return processId == (uint)Environment.ProcessId;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>鼠标位置下的窗口属于哪个进程（返回 "xxx.exe"，取不到返回 null）</summary>
    public static string? GetProcessNameAt(int x, int y)
    {
        try
        {
            var hwnd = WindowFromPoint(new POINT(x, y));
            return GetProcessNameOfWindow(hwnd);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>前台窗口属于哪个进程（返回 "xxx.exe"，取不到返回 null）</summary>
    public static string? GetForegroundProcessName()
    {
        try
        {
            return GetProcessNameOfWindow(GetForegroundWindow());
        }
        catch
        {
            return null;
        }
    }

    private static string? GetProcessNameOfWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return null;

        GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0)
            return null;

        using var process = System.Diagnostics.Process.GetProcessById((int)processId);
        var name = process.ProcessName;
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
    }

    /// <summary>
    /// 用 UI Automation 直接读光标处的选区（不碰剪贴板，最稳、也最不打扰用户）。
    /// 同时给出选区最后一行的右下角坐标，用来把气泡贴在文字旁边。
    /// 读不到时返回 <see cref="UiaSelectionResult.Empty"/>，由调用方决定是否退回剪贴板。
    /// </summary>
    public static UiaSelectionResult ReadSelectionViaUia(int screenX, int screenY)
    {
        try
        {
            // Chrome/Electron 的文本节点常常要在第 5~8 层才能拿到 Document 的选区，
            // 所以这里给足层数；一旦拿到非空选区就立刻返回。
            return ExtractSelection(AutomationElement.FromPoint(new Point(screenX, screenY)), 12);
        }
        catch
        {
            // UIA 在目标程序无响应时会抛异常
            return UiaSelectionResult.Empty;
        }
    }

    /// <summary>
    /// 从"当前获得焦点的控件"再试着读一次选区。
    /// 有些程序的鼠标位置下拿不到 TextPattern（例如浮层/装饰层），但焦点控件可以。
    /// </summary>
    public static UiaSelectionResult ReadSelectionFromFocusedElement()
    {
        try
        {
            return ExtractSelection(AutomationElement.FocusedElement, 6);
        }
        catch
        {
            return UiaSelectionResult.Empty;
        }
    }

    /// <summary>
    /// 从指定元素开始逐层向上找 TextPattern，读出选区文本与末尾矩形。
    /// 某一层虽然有 TextPattern 但选区为空时继续向上找（Chrome 的文本节点就是这样）。
    /// </summary>
    private static UiaSelectionResult ExtractSelection(AutomationElement? start, int maxLevels)
    {
        var element = start;
        var walker = TreeWalker.ControlViewWalker;

        for (var level = 0; level < maxLevels && element != null; level++)
        {
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject) &&
                patternObject is TextPattern textPattern)
            {
                var result = ReadSelectionFromRange(textPattern);
                if (result != null)
                    return result;
            }

            element = walker.GetParent(element);
        }

        return UiaSelectionResult.Empty;
    }

    /// <summary>把 TextPattern 的选区读成文本 + 末尾矩形；没有选区返回 null</summary>
    private static UiaSelectionResult? ReadSelectionFromRange(TextPattern textPattern)
    {
        var builder = new StringBuilder();
        double anchorX = 0;
        double anchorY = 0;
        var hasAnchor = false;

        foreach (var range in textPattern.GetSelection())
        {
            var text = range.GetText(MaxSelectionLength);
            if (string.IsNullOrEmpty(text))
                continue;

            if (builder.Length > 0)
                builder.Append('\n');
            builder.Append(text);

            // GetBoundingRectangles 返回每行一个矩形，取最后一个作为"选区末尾"
            try
            {
                var rectangles = range.GetBoundingRectangles();
                if (rectangles.Length > 0)
                {
                    var last = rectangles[rectangles.Length - 1];
                    anchorX = last.Right;
                    anchorY = last.Bottom;
                    hasAnchor = true;
                }
            }
            catch
            {
                // 某些控件不支持取矩形，退化为鼠标位置
            }
        }

        if (builder.Length == 0)
            return null;

        return new UiaSelectionResult
        {
            Text = builder.ToString(),
            HasAnchor = hasAnchor,
            AnchorX = (int)Math.Round(anchorX),
            AnchorY = (int)Math.Round(anchorY)
        };
    }

    /// <summary>单次划词允许的最大字符数</summary>
    private const int MaxSelectionLength = 5000;

    /// <summary>
    /// 尝试读取当前选中的文本，没有选中内容时返回 null。
    /// 走这条路一定会覆盖剪贴板，所以取完无论成功与否都把用户的剪贴板原样放回去
    /// （包括刚截的图、复制的文件）。
    /// </summary>
    public static async Task<string?> TryReadAsync(CancellationToken cancellationToken)
    {
        var snapshot = ClipboardSnapshot.Capture();
        var before = GetClipboardSequenceNumber();

        SendCopyShortcut();

        // 等待剪贴板发生变化（最多约 400ms）
        var changed = false;
        var afterCopy = before;
        for (var i = 0; i < 12; i++)
        {
            await Task.Delay(35, cancellationToken).ConfigureAwait(true);
            var sequence = GetClipboardSequenceNumber();
            if (sequence != before)
            {
                afterCopy = sequence;
                changed = true;
                break;
            }
        }

        if (!changed)
            return null; // 没有选中任何内容，剪贴板也没被动过

        string? captured = null;
        for (var i = 0; i < 8; i++)
        {
            captured = TryGetClipboardText();
            if (!string.IsNullOrWhiteSpace(captured))
                break;
            await Task.Delay(30, cancellationToken).ConfigureAwait(true);
        }

        // 关键：只有"我们这次 Ctrl+C 之后没人再改过剪贴板"才还原。
        // 否则会把别人刚写进去的内容（比如微信截图刚生成的图片）冲掉。
        var afterCapture = GetClipboardSequenceNumber();
        if (afterCapture == afterCopy)
        {
            snapshot.Restore();
        }
        else
        {
            DebugTrace.Log("clipboard changed by someone else after capture, skip restore");
        }

        if (string.IsNullOrWhiteSpace(captured))
            return null;

        return captured;
    }

    private static void SendCopyShortcut()
    {
        try
        {
            keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
            Thread.Sleep(8);
            keybd_event(VK_C, 0, 0, UIntPtr.Zero);
            Thread.Sleep(12);
            keybd_event(VK_C, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            Thread.Sleep(4);
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
        catch
        {
            // 某些环境下模拟按键会失败，直接忽略
        }
    }

    private static string GetWindowClassName(IntPtr hwnd)
    {
        var builder = new StringBuilder(256);
        var length = GetClassName(hwnd, builder, builder.Capacity);
        return length > 0 ? builder.ToString(0, length) : "";
    }

    /// <summary>读取剪贴板文本；剪贴板被其它程序占用时会重试几次</summary>
    public static string? TryGetClipboardText()
    {
        for (var i = 0; i < 3; i++)
        {
            try
            {
                if (Clipboard.ContainsText(TextDataFormat.UnicodeText))
                    return Clipboard.GetText(TextDataFormat.UnicodeText);
                return null;
            }
            catch
            {
                Thread.Sleep(20);
            }
        }

        return null;
    }

    private static void TrySetClipboardText(string text)
    {
        for (var i = 0; i < 3; i++)
        {
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch
            {
                Thread.Sleep(25);
            }
        }
    }
}
