using System;
using System.Runtime.InteropServices;
using System.Threading;
using Drawing = System.Drawing;

namespace KeyboardCounter;

/// <summary>
/// 全局鼠标钩子（WH_MOUSE_LL）。
///
/// 关键点：低级钩子的回调是在"安装钩子的那个线程"的消息循环里派发的，回调返回之前
/// 系统不会继续派发鼠标输入。所以这里把钩子装在一个独立的 STA 线程上（自带消息循环），
/// 回调里只做状态记录 + 投递事件，绝不接触 UIA / 剪贴板 / 数据库 / 文件。
/// 这样即使主界面线程正在做耗时工作，鼠标也不会卡。
/// </summary>
public class MouseHook : IDisposable
{
    #region WinAPI

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool PostThreadMessage(int threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool PeekMessage(out MSG msg, IntPtr hWnd, uint filterMin, uint filterMax, uint removeMsg);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const uint WM_QUIT = 0x0012;

    /// <summary>按下到抬起移动超过该像素视为"拖动划选"</summary>
    private const int DragThresholdPx = 4;

    /// <summary>两次点击间隔小于该值视为双击</summary>
    private const uint DoubleClickMs = 450;

    /// <summary>双击时允许的位置偏差</summary>
    private const int DoubleClickSlopPx = 6;

    #endregion

    private readonly LowLevelMouseProc _proc;
    private Thread? _thread;
    private int _threadId;
    private IntPtr _hookId = IntPtr.Zero;
    private volatile bool _running;

    private bool _hasDown;
    private Drawing.Point _downPoint;
    private bool _lastUpWasClick;
    private uint _lastUpTime;
    private Drawing.Point _lastUpPoint;

    private int _eventCount;
    private long _maxCallbackTicks;
    private uint _maxInputDelayMs;

    /// <summary>收到过的鼠标消息数量（诊断用）</summary>
    public int EventCount => _eventCount;

    /// <summary>钩子回调耗时峰值（毫秒）。必须很小。</summary>
    public double MaxCallbackMs => _maxCallbackTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>
    /// 输入延迟峰值（毫秒）：鼠标事件发生到钩子真正被调用的间隔。
    /// 这个值大说明系统在等我们的钩子，也就是用户感觉到的"鼠标卡顿"。
    /// </summary>
    public uint MaxInputDelayMs => _maxInputDelayMs;

    /// <summary>钩子是否安装成功</summary>
    public bool IsInstalled => _hookId != IntPtr.Zero;

    /// <summary>识别到划选手势（参数为鼠标抬起时的屏幕坐标，在钩子线程上触发）</summary>
    public event Action<Drawing.Point>? SelectionGesture;

    /// <summary>任意位置左键按下（参数为屏幕坐标，在钩子线程上触发）</summary>
    public event Action<Drawing.Point>? LeftButtonDown;

    public MouseHook()
    {
        _proc = HookCallback;
    }

    public void Start()
    {
        if (_thread != null)
            return;

        _running = true;
        _thread = new Thread(HookThreadProc)
        {
            IsBackground = true,
            Name = "KeyboardCounter.MouseHook"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void Stop()
    {
        if (_thread == null)
            return;

        _running = false;

        try
        {
            if (_threadId != 0)
                PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // 忽略
        }

        try
        {
            _thread.Join(500);
        }
        catch
        {
            // 忽略
        }

        _thread = null;
        _threadId = 0;
        _hookId = IntPtr.Zero;
    }

    private void HookThreadProc()
    {
        _threadId = GetCurrentThreadId();

        // 先建消息队列，再装钩子（低级钩子要求安装线程有消息队列）
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0);

        _hookId = SetWindowsHookEx(WH_MOUSE_LL, _proc, IntPtr.Zero, 0);
        DebugTrace.Log($"MouseHook installed thread={_threadId} hook={_hookId} err={Marshal.GetLastWin32Error()}");

        while (_running)
        {
            if (GetMessage(out var msg, IntPtr.Zero, 0, 0) <= 0)
                break;

            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }

        DebugTrace.Log("MouseHook thread exit");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // 这个函数在钩子线程上执行，必须极快返回：只记录状态并投递事件
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        try
        {
            if (nCode >= 0)
            {
                var message = wParam.ToInt32();

                if (message == WM_LBUTTONDOWN || message == WM_LBUTTONUP)
                {
                    var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    _eventCount++;

                    // 事件发生到现在过了多久——大了就说明鼠标被我们拖住了
                    var delay = unchecked((uint)Environment.TickCount - info.time);
                    if (delay < 10000 && delay > _maxInputDelayMs)
                        _maxInputDelayMs = delay;

                    if (message == WM_LBUTTONDOWN)
                    {
                        _hasDown = true;
                        _downPoint = new Drawing.Point(info.pt.X, info.pt.Y);

                        try
                        {
                            LeftButtonDown?.Invoke(_downPoint);
                        }
                        catch
                        {
                            // 忽略
                        }
                    }
                    else if (_hasDown)
                    {
                        _hasDown = false;

                        var upPoint = new Drawing.Point(info.pt.X, info.pt.Y);
                        var moved = Math.Abs(upPoint.X - _downPoint.X) > DragThresholdPx ||
                                    Math.Abs(upPoint.Y - _downPoint.Y) > DragThresholdPx;

                        // 双击选词：两次点击之间没有明显位移，且间隔足够短
                        var isDoubleClick = !moved && _lastUpWasClick &&
                                            (info.time - _lastUpTime) <= DoubleClickMs &&
                                            Math.Abs(upPoint.X - _lastUpPoint.X) <= DoubleClickSlopPx &&
                                            Math.Abs(upPoint.Y - _lastUpPoint.Y) <= DoubleClickSlopPx;

                        _lastUpWasClick = !moved;
                        _lastUpTime = info.time;
                        _lastUpPoint = upPoint;

                        if (moved || isDoubleClick)
                        {
                            try
                            {
                                SelectionGesture?.Invoke(upPoint);
                            }
                            catch
                            {
                                // 忽略
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            var elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - started;
            if (elapsed > _maxCallbackTicks)
                _maxCallbackTicks = elapsed;
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
