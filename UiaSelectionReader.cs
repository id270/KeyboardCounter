using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace KeyboardCounter;

/// <summary>
/// 专门跑 UI Automation 读取的后台线程（STA + 自带消息泵）。
/// UIA 是跨进程 COM 调用：放在线程池（MTA）或没有消息泵的线程上会明显变慢甚至超时；
/// 放在带消息泵的 STA 线程上既快，又不会阻塞界面线程或鼠标钩子。
/// </summary>
public sealed class UiaSelectionReader : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private Dispatcher? _dispatcher;
    private bool _disposed;

    public UiaSelectionReader()
    {
        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "KeyboardCounter.UIA"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        // 等消息泵就绪（最多 2 秒，超时就退回空实现，不影响主功能）
        _ready.Wait(2000);
    }

    private void ThreadMain()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _ready.Set();
        Dispatcher.Run();
    }

    /// <summary>在后台线程上读取光标处的选区</summary>
    public Task<UiaSelectionResult> ReadAsync(int screenX, int screenY)
    {
        return InvokeAsync(() => SelectionCapture.ReadSelectionViaUia(screenX, screenY));
    }

    /// <summary>在后台线程上从当前焦点控件读选区</summary>
    public Task<UiaSelectionResult> ReadFocusedAsync()
    {
        return InvokeAsync(SelectionCapture.ReadSelectionFromFocusedElement);
    }

    private Task<UiaSelectionResult> InvokeAsync(Func<UiaSelectionResult> read)
    {
        var dispatcher = _dispatcher;
        if (_disposed || dispatcher == null)
            return Task.FromResult(UiaSelectionResult.Empty);

        var completion = new TaskCompletionSource<UiaSelectionResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    completion.TrySetResult(read());
                }
                catch (Exception ex)
                {
                    DebugTrace.Log("uia read failed: " + ex.Message);
                    completion.TrySetResult(UiaSelectionResult.Empty);
                }
            }));
        }
        catch (Exception ex)
        {
            DebugTrace.Log("uia dispatch failed: " + ex.Message);
            return Task.FromResult(UiaSelectionResult.Empty);
        }

        return completion.Task;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            _dispatcher?.InvokeShutdown();
        }
        catch
        {
            // 忽略
        }

        _ready.Dispose();
        GC.SuppressFinalize(this);
    }
}
