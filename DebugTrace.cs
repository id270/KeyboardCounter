using System;
using System.IO;

namespace KeyboardCounter;

/// <summary>
/// 划词翻译的调试日志。默认关闭，在 config.ini 里设置 [Translate] Debug=true 后开启，
/// 日志写入程序目录的 kc_debug.log，用于排查"划词没反应"之类的问题。
/// </summary>
internal static class DebugTrace
{
    private static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "kc_debug.log");

    public static bool Enabled { get; set; }

    public static void Log(string message)
    {
        if (!Enabled)
            return;

        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}\r\n");
        }
        catch
        {
            // 日志失败不影响功能
        }
    }
}
