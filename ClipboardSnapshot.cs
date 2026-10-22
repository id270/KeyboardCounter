using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media.Imaging;

namespace KeyboardCounter;

/// <summary>
/// 剪贴板快照。
///
/// 划词取词在 UIA 失败时会模拟 Ctrl+C，这一步会把用户原来的剪贴板内容冲掉
/// （比如刚截的图）。所以取词前先快照，取完再放回去，尽量做到"零打扰"。
/// 只保留最常见的三类：图片、文件列表、文本。
/// </summary>
public sealed class ClipboardSnapshot
{
    private BitmapSource? _image;
    private StringCollection? _files;
    private string? _text;
    private bool _hasImage;
    private bool _hasFiles;
    private bool _hasText;

    /// <summary>快照里是否有任何内容</summary>
    public bool HasContent => _hasImage || _hasFiles || _hasText;

    /// <summary>快照里是否有图片（截图场景）</summary>
    public bool HasImage => _hasImage;

    public static ClipboardSnapshot Capture()
    {
        var snapshot = new ClipboardSnapshot();

        try
        {
            if (Clipboard.ContainsImage())
            {
                snapshot._image = Clipboard.GetImage();
                snapshot._hasImage = snapshot._image != null;
            }

            if (!snapshot._hasImage && Clipboard.ContainsFileDropList())
            {
                snapshot._files = Clipboard.GetFileDropList();
                snapshot._hasFiles = snapshot._files is { Count: > 0 };
            }

            if (!snapshot._hasImage && !snapshot._hasFiles && Clipboard.ContainsText(TextDataFormat.UnicodeText))
            {
                snapshot._text = Clipboard.GetText(TextDataFormat.UnicodeText);
                snapshot._hasText = !string.IsNullOrEmpty(snapshot._text);
            }
        }
        catch
        {
            // 剪贴板被占用时快照失败，退化为"不具备还原能力"
        }

        return snapshot;
    }

    /// <summary>把快照写回剪贴板（尽力而为）</summary>
    public void Restore()
    {
        if (!HasContent)
            return;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (_hasImage && _image != null)
                {
                    Clipboard.SetImage(_image);
                    return;
                }

                if (_hasFiles && _files is { Count: > 0 })
                {
                    Clipboard.SetFileDropList(_files);
                    return;
                }

                if (_hasText && _text != null)
                {
                    Clipboard.SetText(_text);
                    return;
                }
            }
            catch
            {
                System.Threading.Thread.Sleep(30);
            }
        }
    }
}
