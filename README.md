# KeyboardCounter ⌨

一个轻量级的键盘统计工具，实时显示按键次数、打字速度、网络流量和天气信息。

![Demo](https://img.shields.io/badge/.NET-8.0-blue) ![Platform](https://img.shields.io/badge/Platform-Windows-lightgrey) ![License](https://img.shields.io/badge/License-MIT-green) ![AI](https://img.shields.io/badge/Developed%20by-Claude%20Code-purple)

> 🤖 本项目由 **Claude Code** 完全自主开发
> 
> ⚠️ **二次开发提示**: 本项目没有 `.sln` 解决方案文件，建议让 AI 智能体阅读 [AI_DEV_GUIDE.md](AI_DEV_GUIDE.md) 后进行开发。

## 功能特性

- **按键统计** - 统计总按键次数
- **空格/回车统计** - 单独统计空格键和回车键次数及占比
- **打字速度** - 实时计算每分钟按键数 (CPM)
- **网络流量** - 显示上传/下载速度 (KB/s 或 MB/s)
- **下载总量** - 累计下载流量统计 (KB/MB/GB)
- **天气显示** - 根据IP定位显示当前天气图标和温度
- **网络供应商** - 显示当前ISP名称
- **Emoji 表情** - 根据打字总量动态变化
- **布局切换** - 支持横向和纵向两种显示模式
- **系统托盘** - 最小化到托盘，右键菜单操作
- **开机启动** - 支持设置开机自动启动

## 显示效果

### 横向模式 (默认)

![screenshot](images/screenshot.png)

### 纵向模式

![screenshot2](images/screenshot2.png)

## Emoji 等级

根据打字总量变化：

| 按键次数 | 表情 | 状态 |
|---------|------|------|
| 0-999 | 😐 | 平淡 |
| 1000-4999 | 🙂 | 微笑 |
| 5000-9999 | 😊 | 开心 |
| 10000-19999 | 😃 | 兴奋 |
| 20000-49999 | 😄 | 愉悦 |
| 50000-99999 | 😆 | 高兴 |
| 100000+ | 🤯 | 震惊 |

## 技术栈

- .NET 8.0
- WPF (Windows Presentation Foundation)
- [Emoji.Wpf](https://github.com/samhocevar/emoji.wpf) - 彩色 Emoji 支持

## 安装运行

### 方式一：下载 Release

从 [Releases](../../releases) 页面下载最新版本

### 方式二：从源码运行

```bash
git clone https://github.com/id270/KeyboardCounter.git
cd KeyboardCounter
dotnet run
```

## 使用说明

- **窗口位置** - 固定在屏幕右下角，可拖动
- **切换布局** - 右键托盘图标 → 布局 → 选择横向/纵向
- **重置计数** - 右键托盘图标，选择"重置计数"
- **开机启动** - 右键托盘图标，勾选"开机启动"
- **退出程序** - 右键托盘图标，选择"退出"

## 托盘菜单

```
显示窗口
重置计数
──────────
布局
  ├─ ☑ 横向显示
  └─ ☐ 纵向显示
──────────
☑ 开机启动
──────────
退出
```

## 配置文件

程序运行时会在同目录下生成 `config.ini` 文件：

```ini
[Display]
Vertical=false
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
├── IniConfig.cs              # INI配置文件处理
├── README.md                 # 中文说明文档
├── README_EN.md              # English README
├── AI_DEV_GUIDE.md           # AI Development Guide
└── config.ini                # 运行时配置文件
```

## 系统要求

- Windows 10 或更高版本
- .NET 8.0 Runtime

## 更新日志

### v1.4
- ✨ 新增天气显示（图标+温度）
- ✨ 新增网络供应商显示
- ✨ 新增下载总量统计
- ✨ 新增横向/纵向布局切换
- ✨ 新增INI配置文件持久化
- 🔧 纵向布局动态宽度适配

### v1.1
- ✨ 新增开机启动功能

### v1.0
- 🎉 首次发布

## License

MIT License

---

**English**: [README_EN.md](README_EN.md)
