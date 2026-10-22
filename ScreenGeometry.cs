using System;
using System.Windows;
using System.Windows.Media;

namespace KeyboardCounter;

/// <summary>
/// 鼠标屏幕坐标（物理像素）与 WPF 坐标（DIP）之间的换算
/// </summary>
public static class ScreenGeometry
{
    /// <summary>获取元素所在屏幕的缩放比例（125% 时为 1.25）</summary>
    public static double GetScale(Visual? visual)
    {
        if (visual != null)
        {
            try
            {
                var source = PresentationSource.FromVisual(visual);
                if (source?.CompositionTarget != null)
                {
                    var matrix = source.CompositionTarget.TransformToDevice;
                    if (matrix.M11 > 0.1)
                        return matrix.M11;
                }
            }
            catch
            {
                // 忽略，走系统 DPI 兜底
            }
        }

        try
        {
            using var graphics = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
            return graphics.DpiX / 96.0;
        }
        catch
        {
            return 1.0;
        }
    }

    /// <summary>物理像素坐标转 DIP</summary>
    public static Point PixelsToDips(Visual? visual, int x, int y)
    {
        var scale = GetScale(visual);
        return new Point(x / scale, y / scale);
    }

    /// <summary>判断某个物理像素点是否落在窗口内（用于点击外部时关闭气泡）</summary>
    public static bool ContainsPixelPoint(Window? window, int x, int y)
    {
        if (window == null || !window.IsVisible)
            return false;

        try
        {
            var scale = GetScale(window);
            var dipX = x / scale;
            var dipY = y / scale;

            return dipX >= window.Left && dipX <= window.Left + window.ActualWidth &&
                   dipY >= window.Top && dipY <= window.Top + window.ActualHeight;
        }
        catch
        {
            return false;
        }
    }
}
