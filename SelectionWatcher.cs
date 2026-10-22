using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using Drawing = System.Drawing;

namespace KeyboardCounter;

/// <summary>
/// 划词监听器：全局鼠标钩子 + 轮询兜底，识别"拖动划选"和"双击选词"手势后读取选中文本。
/// </summary>
public class SelectionWatcher : IDisposable
{
    #region WinAPI（轮询兜底用）

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private const int VK_LBUTTON = 0x01;

    #endregion

    private const int DragThresholdPx = 4;
    private const int DoubleClickMs = 450;
    private const int DoubleClickSlopPx = 6;

    /// <summary>两次手势之间的最小间隔，避免钩子和轮询重复触发</summary>
    private const int GestureDedupeMs = 400;

    /// <summary>UIA 读选区的总超时（毫秒）</summary>
    private const int UiaTimeoutMs = 600;

    /// <summary>UIA 先跑的"独占时间片"，超过就开始并行走剪贴板</summary>
    private const int UiaHeadStartMs = 100;

    /// <summary>取词超过这个时间还没结果，就先显示"识别中…"占位气泡</summary>
    private const int PendingHintMs = 140;

    /// <summary>单次划词采集的总超时（毫秒），超过就放弃，避免用户干等</summary>
    private const int CaptureTimeoutMs = 2500;

    private readonly Dispatcher _dispatcher;
    private readonly MouseHook _mouseHook;
    private readonly DispatcherTimer _pollTimer;
    private readonly UiaSelectionReader _uiaReader = new();
    private bool _running;
    private bool _capturing;
    private int _lastGestureTick;

    private bool _leftWasDown;
    private Drawing.Point _downPoint;
    private int _downTick;
    private bool _lastUpWasClick;
    private int _lastUpTick;
    private Drawing.Point _lastUpPoint;
    private Task<UiaSelectionResult>? _pendingUiaRead;
    private int _pollTicks;

    /// <summary>捕获到划词（文本, 锚点屏幕X, 锚点屏幕Y），已在 UI 线程上触发</summary>
    public event Action<string, int, int>? SelectionCaptured;

    /// <summary>取词失败（没选中内容），用于关掉"识别中…"占位气泡</summary>
    public event Action<int, int>? SelectionCaptureFailed;

    /// <summary>取词超过 <see cref="PendingHintMs"/> 还没完成，用于先弹一个"识别中…"占位气泡</summary>
    public event Action<int, int>? SelectionCapturePending;

    /// <summary>任意位置左键按下（屏幕X, 屏幕Y），已在 UI 线程上触发</summary>
    public event Action<int, int>? LeftButtonPressed;

    public SelectionWatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _mouseHook = new MouseHook();
        _mouseHook.SelectionGesture += OnGestureDetectedFromHook;
        _mouseHook.LeftButtonDown += OnLeftDownFromHook;

        _pollTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(35)
        };
        _pollTimer.Tick += PollTimer_Tick;
    }

    public bool IsRunning => _running;

    /// <summary>取词方式（默认只用 UIA，不碰剪贴板）</summary>
    public SelectionMode Mode { get; set; } = SelectionMode.UiaOnly;

    /// <summary>
    /// 取词白名单：这些程序在"仅 UIA"模式取不到词时，也允许用剪贴板兜底
    /// （例如 typora.exe、WeChat.exe 这类不暴露可访问性文本的程序）。
    /// </summary>
    public string[] ClipboardFallbackProcesses { get; set; } = Array.Empty<string>();

    public void Start()
    {
        if (_running)
            return;

        _leftWasDown = false;
        _lastUpWasClick = false;
        _running = true;

        _mouseHook.Start();
        _pollTimer.Start();

        DebugTrace.Log($"SelectionWatcher started, hookInstalled={_mouseHook.IsInstalled}");
    }

    public void Stop()
    {
        if (!_running)
            return;

        _running = false;
        _pollTimer.Stop();
        _mouseHook.Stop();
        DebugTrace.Log("SelectionWatcher stopped");
    }

    #region 钩子回调（在钩子线程触发，只投递）

    private void OnLeftDownFromHook(Drawing.Point point)
    {
        RaiseLeftButtonPressed(point);
    }

    private void OnGestureDetectedFromHook(Drawing.Point point)
    {
        OnGestureDetected(point);
    }

    #endregion

    #region 轮询兜底

    private void PollTimer_Tick(object? sender, EventArgs e)
    {
        if (!_running)
            return;

        try
        {
            _pollTicks++;
            if (_pollTicks % 120 == 0)
                DebugTrace.Log(
                    $"hook cb={_mouseHook.MaxCallbackMs:F2}ms inputDelay={_mouseHook.MaxInputDelayMs}ms events={_mouseHook.EventCount}");

            Poll();
        }
        catch (Exception ex)
        {
            DebugTrace.Log("poll exception: " + ex);
        }
    }

    private void Poll()
    {
        var isDown = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;

        if (isDown && !_leftWasDown)
        {
            _leftWasDown = true;
            if (!TryGetCursor(out _downPoint))
                _downPoint = default;
            _downTick = Environment.TickCount;
            RaiseLeftButtonPressed(_downPoint);
            return;
        }

        if (isDown || !_leftWasDown)
            return;

        _leftWasDown = false;

        if (!TryGetCursor(out var upPoint))
            return;

        var now = Environment.TickCount;
        var moved = Math.Abs(upPoint.X - _downPoint.X) > DragThresholdPx ||
                    Math.Abs(upPoint.Y - _downPoint.Y) > DragThresholdPx;

        var isDoubleClick = !moved && _lastUpWasClick &&
                            (now - _lastUpTick) <= DoubleClickMs &&
                            Math.Abs(upPoint.X - _lastUpPoint.X) <= DoubleClickSlopPx &&
                            Math.Abs(upPoint.Y - _lastUpPoint.Y) <= DoubleClickSlopPx;

        _lastUpWasClick = !moved;
        _lastUpTick = now;
        _lastUpPoint = upPoint;

        if (moved || isDoubleClick)
            OnGestureDetected(upPoint);
    }

    private static bool TryGetCursor(out Drawing.Point point)
    {
        if (GetCursorPos(out var native))
        {
            point = new Drawing.Point(native.X, native.Y);
            return true;
        }

        point = default;
        return false;
    }

    #endregion

    private void OnGestureDetected(Drawing.Point point)
    {
        if (!_running)
            return;

        var now = Environment.TickCount;
        if (now - _lastGestureTick < GestureDedupeMs)
            return;
        _lastGestureTick = now;

        // 关键：这里可能就运行在鼠标钩子回调里。绝对不能同步做事，
        // 否则钩子不返回，系统就会暂停派发鼠标输入，表现为鼠标掉帧/卡顿。
        try
        {
            _dispatcher.BeginInvoke(
                new Action(() => { _ = HandleGestureAsync(point); }),
                DispatcherPriority.Input);
        }
        catch
        {
            // 程序退出过程中 Dispatcher 可能已关闭
        }
    }

    private void RaiseLeftButtonPressed(Drawing.Point point)
    {
        if (!_running)
            return;

        try
        {
            _dispatcher.BeginInvoke(new Action(() => InvokeLeftButtonPressed(point)));
        }
        catch
        {
            // 忽略
        }
    }

    private void InvokeLeftButtonPressed(Drawing.Point point)
    {
        try
        {
            LeftButtonPressed?.Invoke(point.X, point.Y);
        }
        catch
        {
            // 忽略
        }
    }

    private async Task HandleGestureAsync(Drawing.Point point)
    {
        var started = Environment.TickCount;

        if (_capturing || !_running)
            return;

        _capturing = true;
        try
        {
            // 终端类窗口 Ctrl+C 会打断进程；截图浮层会把刚截的图冲掉；本程序自己的窗口也不处理
            if (SelectionCapture.IsForegroundBlocked(point.X, point.Y))
            {
                DebugTrace.Log("skipped: blocked window (terminal/screenshot/explorer)");
                return;
            }

            if (SelectionCapture.IsOwnProcessWindow(point.X, point.Y))
                return;

            if (IsModifierDown())
            {
                DebugTrace.Log("skipped: modifier held");
                return;
            }

            DebugTrace.Log($"gesture at ({point.X},{point.Y})");

            var (text, anchorX, anchorY) = await CaptureSelectionTextAsync(point, started).ConfigureAwait(true);

            if (string.IsNullOrWhiteSpace(text))
            {
                DebugTrace.Log("nothing captured");
                try
                {
                    SelectionCaptureFailed?.Invoke(point.X, point.Y);
                }
                catch
                {
                    // 忽略
                }

                return;
            }

            var normalized = LanguageUtil.Normalize(text);
            if (normalized.Length == 0)
                return;

            DebugTrace.Log($"selection ready ({normalized.Length} chars) after {Environment.TickCount - started}ms, anchor=({anchorX},{anchorY})");
            SelectionCaptured?.Invoke(normalized, anchorX, anchorY);
        }
        catch (Exception ex)
        {
            DebugTrace.Log("HandleGestureAsync exception: " + ex);
        }
        finally
        {
            _capturing = false;
        }
    }

    private async Task<(string? Text, int AnchorX, int AnchorY)> CaptureSelectionTextAsync(Drawing.Point point, int started)
    {
        using var pendingCts = new CancellationTokenSource();
        _ = ShowPendingHintAsync(point, pendingCts.Token);

        try
        {
            return await CaptureSelectionTextCoreAsync(point, started).ConfigureAwait(true);
        }
        finally
        {
            pendingCts.Cancel();
        }
    }

    /// <summary>取词偏慢时先给用户一个"识别中…"的反馈，避免看起来像没反应</summary>
    private async Task ShowPendingHintAsync(Drawing.Point point, CancellationToken token)
    {
        try
        {
            await Task.Delay(PendingHintMs, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested)
            return;

        DebugTrace.Log($"capture still running after {PendingHintMs}ms, show pending hint");

        try
        {
            SelectionCapturePending?.Invoke(point.X, point.Y);
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>
    /// 取选区文本：UIA 先跑，给它 UiaHeadStartMs 的时间；
    /// 没按时拿到就同时启动"模拟 Ctrl+C + 读剪贴板"，谁先成功用谁。
    /// 返回文本 + 气泡锚点（UIA 给的是选区末尾，剪贴板路径用鼠标位置）。
    /// </summary>
    private async Task<(string? Text, int AnchorX, int AnchorY)> CaptureSelectionTextCoreAsync(Drawing.Point point, int started)
    {
        if (Mode == SelectionMode.ClipboardOnly)
        {
            using var clipboardCts = new CancellationTokenSource(CaptureTimeoutMs);
            var clipboardOnly = await CaptureFromClipboardAsync(clipboardCts.Token, started).ConfigureAwait(true);
            return (clipboardOnly, point.X, point.Y);
        }

        if (Mode == SelectionMode.UiaOnly)
        {
            // 白名单里的程序（Typora、微信这类不暴露可访问性文本的）直接用剪贴板取词，
            // 省掉几秒毫无意义的 UIA 尝试；其它程序依旧完全不碰剪贴板。
            if (IsClipboardFallbackAllowed(point))
            {
                DebugTrace.Log("uia-only: whitelisted process, capture via clipboard");
                using var directCts = new CancellationTokenSource(CaptureTimeoutMs);
                var directText = await CaptureFromClipboardAsync(directCts.Token, started).ConfigureAwait(true);
                return (directText, point.X, point.Y);
            }

            // 只走 UIA：鼠标位置 → 焦点控件 → 再重试，全程不碰剪贴板。
            // 第一次给足 800ms（浏览器/Office 首次要建可访问性树），后面几次复用同一批调用。
            var attempts = new (bool UseFocused, int TimeoutMs)[]
            {
                (false, 800),
                (true, 400),
                (false, 400),
                (true, 400)
            };

            for (var i = 0; i < attempts.Length; i++)
            {
                var (useFocused, timeoutMs) = attempts[i];
                var result = useFocused
                    ? await ReadFocusedSelectionAsync(timeoutMs).ConfigureAwait(true)
                    : await ReadCursorSelectionAsync(point, timeoutMs).ConfigureAwait(true);

                DebugTrace.Log($"uia-only attempt {i + 1} ({(useFocused ? "focused" : "cursor")}): len={(result.Text == null ? -1 : result.Text.Length)} at {Environment.TickCount - started}ms");

                if (!string.IsNullOrWhiteSpace(result.Text))
                {
                    DebugTrace.Log($"uia-only hit on attempt {i + 1} ({Environment.TickCount - started}ms)");
                    return Anchor(result, point);
                }

                if (i < attempts.Length - 1)
                    await Task.Delay(60).ConfigureAwait(true);
            }

            DebugTrace.Log($"uia-only: nothing captured after {Environment.TickCount - started}ms");
            return (null, point.X, point.Y);
        }

        var uiaTask = TryReadSelectionViaUiaAsync(point);

        var first = await Task.WhenAny(uiaTask, Task.Delay(UiaHeadStartMs)).ConfigureAwait(true);
        if (first == uiaTask)
        {
            var uiaResult = await uiaTask.ConfigureAwait(true);
            DebugTrace.Log($"uia selection len={(uiaResult.Text == null ? -1 : uiaResult.Text.Length)} anchor={uiaResult.HasAnchor} after {Environment.TickCount - started}ms");
            if (!string.IsNullOrWhiteSpace(uiaResult.Text))
                return Anchor(uiaResult, point);
        }
        else
        {
            DebugTrace.Log($"uia still running after {UiaHeadStartMs}ms, start clipboard capture in parallel");
        }

        using var cts = new CancellationTokenSource(CaptureTimeoutMs);
        var clipboardTask = CaptureFromClipboardAsync(cts.Token, started);

        var winner = await Task.WhenAny(uiaTask, clipboardTask).ConfigureAwait(true);
        if (winner == clipboardTask)
        {
            var clipboardText = await clipboardTask.ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(clipboardText))
                return (clipboardText, point.X, point.Y);

            // 剪贴板没拿到，再给 UIA 一点时间收尾
            if (!uiaTask.IsCompleted)
                await Task.WhenAny(uiaTask, Task.Delay(UiaTimeoutMs)).ConfigureAwait(true);

            if (!uiaTask.IsCompletedSuccessfully)
                return (null, point.X, point.Y);

            var lateUia = await uiaTask.ConfigureAwait(true);
            return string.IsNullOrWhiteSpace(lateUia.Text)
                ? (null, point.X, point.Y)
                : Anchor(lateUia, point);
        }

        var finalUia = await uiaTask.ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(finalUia.Text))
            return Anchor(finalUia, point);

        var text = await clipboardTask.ConfigureAwait(true);
        return (text, point.X, point.Y);
    }

    /// <summary>当前取词位置所属程序是否在白名单里</summary>
    private bool IsClipboardFallbackAllowed(Drawing.Point point)
    {
        if (ClipboardFallbackProcesses.Length == 0)
            return false;

        var name = SelectionCapture.GetProcessNameAt(point.X, point.Y)
                   ?? SelectionCapture.GetForegroundProcessName();
        if (string.IsNullOrEmpty(name))
            return false;

        foreach (var allowed in ClipboardFallbackProcesses)
        {
            if (string.Equals(allowed, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        DebugTrace.Log($"uia-only: {name} not in clipboard fallback whitelist");
        return false;
    }

    /// <summary>从当前焦点控件读选区（给"仅 UIA"模式做兜底）</summary>
    private async Task<UiaSelectionResult> ReadFocusedSelectionAsync(int timeoutMs)
    {
        try
        {
            var task = _uiaReader.ReadFocusedAsync();
            var done = await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(true);
            if (done != task)
            {
                DebugTrace.Log($"uia focused timeout (>{timeoutMs}ms)");
                return UiaSelectionResult.Empty;
            }

            return await task.ConfigureAwait(true);
        }
        catch
        {
            return UiaSelectionResult.Empty;
        }
    }

    /// <summary>读光标处的选区；如果上一次调用还没返回就复用它，避免堆积跨进程调用</summary>
    private async Task<UiaSelectionResult> ReadCursorSelectionAsync(Drawing.Point point, int timeoutMs)
    {
        var task = _pendingUiaRead is { IsCompleted: false }
            ? _pendingUiaRead
            : _uiaReader.ReadAsync(point.X, point.Y);

        _pendingUiaRead = task;

        var done = await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(true);
        if (done != task)
        {
            DebugTrace.Log($"uia cursor timeout (>{timeoutMs}ms)");
            return UiaSelectionResult.Empty;
        }

        try
        {
            return await task.ConfigureAwait(true);
        }
        catch
        {
            return UiaSelectionResult.Empty;
        }
    }

    /// <summary>UIA 有选区矩形就用选区末尾做锚点，否则用鼠标位置</summary>
    private static (string? Text, int AnchorX, int AnchorY) Anchor(UiaSelectionResult result, Drawing.Point fallback)
    {
        if (result.HasAnchor && result.AnchorX > 0 && result.AnchorY > 0)
            return (result.Text, result.AnchorX, result.AnchorY);

        return (result.Text, fallback.X, fallback.Y);
    }

    private static async Task<string?> CaptureFromClipboardAsync(CancellationToken cancellationToken, int started)
    {
        try
        {
            var text = await SelectionCapture.TryReadAsync(cancellationToken).ConfigureAwait(true);
            DebugTrace.Log($"clipboard capture len={(text == null ? -1 : text.Length)} after {Environment.TickCount - started}ms");
            return text;
        }
        catch (OperationCanceledException)
        {
            DebugTrace.Log("clipboard capture timed out");
            return null;
        }
    }

    /// <summary>UIA 读选区放到专门的 STA 后台线程，最多等 <paramref name="timeoutMs"/> 毫秒</summary>
    private async Task<UiaSelectionResult> TryReadSelectionViaUiaAsync(Drawing.Point point, int timeoutMs = UiaTimeoutMs)
    {
        // 上一次 UIA 还没返回（可能卡在无响应的目标程序上），不要再叠加一次跨进程调用
        if (_pendingUiaRead is { IsCompleted: false })
        {
            DebugTrace.Log("uia read still pending, skip");
            return UiaSelectionResult.Empty;
        }

        var readTask = _uiaReader.ReadAsync(point.X, point.Y);
        _pendingUiaRead = readTask;

        var finished = await Task.WhenAny(readTask, Task.Delay(timeoutMs)).ConfigureAwait(true);

        if (finished != readTask)
        {
            DebugTrace.Log($"uia timeout (>{timeoutMs}ms)");
            return UiaSelectionResult.Empty;
        }

        try
        {
            return await readTask.ConfigureAwait(true);
        }
        catch
        {
            return UiaSelectionResult.Empty;
        }
    }

    /// <summary>按住 Ctrl / Alt 时用户多半在做别的操作，跳过</summary>
    private static bool IsModifierDown()
    {
        return Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl) ||
               Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt);
    }

    public void Dispose()
    {
        SelectionCaptured = null;
        SelectionCaptureFailed = null;
        SelectionCapturePending = null;
        LeftButtonPressed = null;
        _running = false;
        _pollTimer.Stop();
        _mouseHook.Dispose();
        _uiaReader.Dispose();
        GC.SuppressFinalize(this);
    }
}
