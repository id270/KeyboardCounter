# KeyboardCounter ⌨

A lightweight keyboard statistics tool that displays keystrokes, typing speed, network traffic, and weather in real-time.

![Demo](https://img.shields.io/badge/.NET-8.0-blue) ![Platform](https://img.shields.io/badge/Platform-Windows-lightgrey) ![License](https://img.shields.io/badge/License-MIT-green) ![AI](https://img.shields.io/badge/Developed%20by-Claude%20Code-purple)

> 🤖 This project is fully developed by **Claude Code**
>
> ⚠️ **Note for Developers**: This project does not have a `.sln` solution file. It is recommended to let AI agents read [AI_DEV_GUIDE.md](AI_DEV_GUIDE.md) for further development.

## Features

- **Keystroke Statistics** - Total key count
- **Space/Enter Statistics** - Individual counts with percentages
- **Typing Speed** - Characters per minute (CPM)
- **Network Traffic** - Upload/download speed (KB/s or MB/s)
- **Total Download** - Cumulative download traffic (KB/MB/GB)
- **Weather Display** - Current weather icon and temperature based on IP geolocation
- **ISP Name** - Current network provider
- **Emoji Expression** - Changes based on total keystrokes
- **Layout Switching** - Horizontal and vertical display modes
- **System Tray** - Minimize to tray with context menu
- **Auto Start** - Launch on Windows startup

## Screenshots

### Horizontal Mode (Default)

![screenshot](images/screenshot.png)

### Vertical Mode

![screenshot2](images/screenshot2.png)

## Emoji Levels

Based on total keystrokes:

| Keystrokes | Emoji | Status |
|------------|-------|--------|
| 0-999 | 😐 | Neutral |
| 1000-4999 | 🙂 | Smile |
| 5000-9999 | 😊 | Happy |
| 10000-19999 | 😃 | Excited |
| 20000-49999 | 😄 | Joyful |
| 50000-99999 | 😆 | Grinning |
| 100000+ | 🤯 | Mind Blown |

## Tech Stack

- .NET 8.0
- WPF (Windows Presentation Foundation)
- [Emoji.Wpf](https://github.com/samhocevar/emoji.wpf) - Color Emoji support

## Installation

### Option 1: Download Release

Download from [Releases](../../releases) page

### Option 2: Run from Source

```bash
git clone https://github.com/id270/KeyboardCounter.git
cd KeyboardCounter
dotnet run
```

## Usage

- **Window Position** - Fixed at bottom-right corner, draggable
- **Switch Layout** - Right-click tray icon → Layout → Select Horizontal/Vertical
- **Reset Count** - Right-click tray icon → Reset Count
- **Auto Start** - Right-click tray icon → Check "Auto Start"
- **Exit** - Right-click tray icon → Exit

## Tray Menu

```
Show Window
Reset Count
──────────
Layout
  ├─ ☑ Horizontal
  └─ ☐ Vertical
──────────
☑ Auto Start
──────────
Exit
```

## Configuration

The program generates a `config.ini` file at runtime:

```ini
[Display]
Vertical=false
```

## Project Structure

```
KeyboardCounter/
├── KeyboardCounter.csproj    # Project file
├── App.xaml                  # Application definition
├── App.xaml.cs               # Application entry point
├── MainWindow.xaml           # Main window UI
├── MainWindow.xaml.cs        # Main window logic
├── KeyboardHook.cs           # Global keyboard hook
├── IniConfig.cs              # INI config handler
├── README.md                 # Chinese documentation
├── README_EN.md              # English documentation
├── AI_DEV_GUIDE.md           # AI Development Guide
└── config.ini                # Runtime config file
```

## Requirements

- Windows 10 or later
- .NET 8.0 Runtime

## Changelog

### v1.4
- ✨ Added weather display (icon + temperature)
- ✨ Added ISP name display
- ✨ Added total download statistics
- ✨ Added horizontal/vertical layout switching
- ✨ Added INI config persistence
- 🔧 Dynamic width for vertical layout

### v1.1
- ✨ Added auto-start on boot

### v1.0
- 🎉 Initial release

## License

MIT License

---

**中文**: [README.md](README.md)
