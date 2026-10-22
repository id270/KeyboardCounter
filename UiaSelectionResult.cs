namespace KeyboardCounter;

/// <summary>
/// UIA 读到的选区：文本 + 选区右下的屏幕坐标（用来把气泡贴在文字旁边，
/// 而不是跟着鼠标跑到空白处）。
/// </summary>
public sealed class UiaSelectionResult
{
    public string? Text { get; init; }

    public bool HasAnchor { get; init; }

    public int AnchorX { get; init; }

    public int AnchorY { get; init; }

    public static UiaSelectionResult Empty { get; } = new();
}
