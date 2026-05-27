# KeyboardCounter ⌨

一个轻量级的键盘统计工具，实时显示按键次数、打字速度和网络流量。

![Demo](https://img.shields.io/badge/.NET-8.0-blue) ![Platform](https://img.shields.io/badge/Platform-Windows-lightgrey) ![License](https://img.shields.io/badge/License-MIT-green)

## 功能特性

- **按键统计** - 统计总按键次数
- **空格统计** - 单独统计空格键次数及占比
- **回车统计** - 单独统计回车键次数及占比
- **打字速度** - 实时计算每分钟按键数 (CPM)
- **网络流量** - 显示上传/下载速度 (MB/s)
- **Emoji 表情** - 根据打字速度动态变化，从平淡到愤怒
- **系统托盘** - 最小化到托盘，右键菜单操作

## 显示效果

```
😐 总计:123 | 空格:15(12%) | 回车:8(6%) | 180/m | ↑0.5 ↓1.2MB
```

## Emoji 速度等级

| 速度 (CPM) | 表情 | 状态 |
|-----------|------|------|
| 0-49 | 😐 | 平淡 |
| 50-99 | 🙂 | 微笑 |
| 100-149 | 😊 | 开心 |
| 150-199 | 😃 | 兴奋 |
| 200-249 | 😤 | 生气 |
| 250-299 | 😠 | 愤怒 |
| 300+ | 🤬 | 暴怒 |

## 技术栈

- .NET 8.0
- WPF (Windows Presentation Foundation)
- [Emoji.Wpf](https://github.com/samhocevar/emoji.wpf) - 彩色 Emoji 支持

## 安装运行

### 方式一：直接运行

```bash
# 克隆仓库
git clone https://github.com/your-username/KeyboardCounter.git

# 进入目录
cd KeyboardCounter

# 运行
dotnet run
```

### 方式二：编译后运行

```bash
# 编译
dotnet build -c Release

# 运行
./bin/Release/net8.0-windows/KeyboardCounter.exe
```

## 使用说明

- **窗口位置** - 固定在屏幕右下角，可拖动
- **最小化** - 点击最小化按钮或关闭按钮，窗口隐藏到系统托盘
- **显示窗口** - 双击托盘图标或右键菜单选择"显示窗口"
- **重置计数** - 右键托盘图标，选择"重置计数"
- **退出程序** - 右键托盘图标，选择"退出"

## 项目结构

```
KeyboardCounter/
├── KeyboardCounter.csproj    # 项目文件
├── App.xaml                  # 应用程序定义
├── App.xaml.cs               # 应用程序入口
├── MainWindow.xaml           # 主窗口 UI
├── MainWindow.xaml.cs        # 主窗口逻辑
├── KeyboardHook.cs           # 全局键盘钩子
└── README.md                 # 说明文档
```

## 核心实现

### 全局键盘钩子

使用 Windows 低级键盘钩子 (LowLevelKeyboardProc) 监听所有按键事件：

```csharp
private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
{
    if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
    {
        var vkCode = Marshal.ReadInt32(lParam);
        OnKeyDown?.Invoke(vkCode);
    }
    return CallNextHookEx(_hookId, nCode, wParam, lParam);
}
```

### 网络流量统计

通过 `System.Net.NetworkInformation` 获取网络接口统计信息：

```csharp
var stats = ni.GetIPv4Statistics();
totalSent += stats.BytesSent;
totalReceived += stats.BytesReceived;
```

## 系统要求

- Windows 10 或更高版本
- .NET 8.0 Runtime

## License

MIT License
