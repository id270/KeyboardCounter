# KeyboardCounter ⌨

一个轻量级的键盘统计工具，实时显示按键次数、打字速度和网络流量。

![Demo](https://img.shields.io/badge/.NET-8.0-blue) ![Platform](https://img.shields.io/badge/Platform-Windows-lightgrey) ![License](https://img.shields.io/badge/License-MIT-green) ![AI](https://img.shields.io/badge/Developed%20by-Claude%20Code%20%2B%20GLM5-purple)

> 🤖 本项目由 **Claude Code + GLM5** 完全自主开发，无人工参与编码

## 功能特性

- **按键统计** - 统计总按键次数
- **空格统计** - 单独统计空格键次数及占比
- **回车统计** - 单独统计回车键次数及占比
- **打字速度** - 实时计算每分钟按键数 (CPM)
- **网络流量** - 显示上传/下载速度 (MB/s)
- **Emoji 表情** - 根据打字速度动态变化，从平淡到愤怒
- **系统托盘** - 最小化到托盘，右键菜单操作
- **开机启动** - 支持设置开机自动启动

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

### 方式一：下载 Release

从 [Releases](https://github.com/id270/KeyboardCounter/releases) 页面下载最新版本（仅 630KB）

### 方式二：从源码运行

```bash
# 克隆仓库
git clone https://github.com/id270/KeyboardCounter.git

# 进入目录
cd KeyboardCounter

# 运行
dotnet run
```

## 使用说明

- **窗口位置** - 固定在屏幕右下角，可拖动
- **最小化** - 点击最小化按钮或关闭按钮，窗口隐藏到系统托盘
- **显示窗口** - 双击托盘图标或右键菜单选择"显示窗口"
- **重置计数** - 右键托盘图标，选择"重置计数"
- **开机启动** - 右键托盘图标，勾选"开机启动"
- **退出程序** - 右键托盘图标，选择"退出"

## 托盘菜单

```
显示窗口
重置计数
──────────
☑ 开机启动
──────────
退出
```

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

使用 Windows 低级键盘钩子 (LowLevelKeyboardProc) 监听所有按键事件

### 开机启动

通过注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 管理，无需管理员权限

### 网络流量统计

通过 `System.Net.NetworkInformation` 获取网络接口统计信息

## 系统要求

- Windows 10 或更高版本
- .NET 8.0 Runtime（Windows 11 通常已自带）

如果缺少运行时，可从 https://dotnet.microsoft.com/download/dotnet/8.0 下载

## 更新日志

### v1.1
- ✨ 新增开机启动功能
- 🔧 托盘菜单添加可勾选的启动选项

### v1.0
- 🎉 首次发布
- ⌨️ 键盘按键统计
- 📊 空格/回车键占比
- 🚀 打字速度计算
- 📡 网络上传/下载速度
- 😐→🤬 Emoji 表情随速度变化

## License

MIT License
