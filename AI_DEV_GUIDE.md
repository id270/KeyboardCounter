# AI Development Guide for KeyboardCounter

> This document is intended for AI agents (Claude, ChatGPT, GitHub Copilot, etc.) to understand the project structure and development conventions.

## Project Overview

KeyboardCounter is a lightweight WPF application for Windows that tracks keyboard statistics and displays system information in a compact overlay window.

### Key Characteristics

- **No Solution File**: This project uses the simplified .NET SDK style `.csproj` file. You can open, build, and run it directly without a `.sln` file.
- **Single Project**: All code is in one project - no complex solution structure.
- **Minimal Dependencies**: Only one NuGet package - `Emoji.Wpf`.

## Project Structure

```
KeyboardCounter/
├── KeyboardCounter.csproj    # Project file (SDK style, no .sln needed)
├── App.xaml                  # WPF application definition
├── App.xaml.cs               # Entry point (creates MainWindow)
├── MainWindow.xaml           # UI layout (two layouts: horizontal/vertical)
├── MainWindow.xaml.cs        # Main logic (timers, hooks, API calls)
├── KeyboardHook.cs           # Low-level keyboard hook (LowLevelKeyboardProc)
├── IniConfig.cs              # Simple INI file parser/writer
├── config.ini                # Runtime config (auto-generated)
├── README.md                 # Chinese documentation
├── README_EN.md              # English documentation
└── AI_DEV_GUIDE.md           # This file
```

## Technical Details

### Build Commands

```bash
# Build
dotnet build

# Build Release
dotnet build -c Release

# Run
dotnet run

# Publish single file
dotnet publish -c Release -r win-x64 --self-contained false
```

### Key Classes

| Class | Purpose |
|-------|---------|
| `MainWindow` | Main window with timers, display logic, API calls |
| `KeyboardHook` | Global keyboard hook using Windows API |
| `IniConfig` | Simple key-value INI file handling |

### Important Methods in MainWindow

| Method | Purpose |
|--------|---------|
| `ApplyLayout()` | Switch between horizontal/vertical layouts |
| `ToggleLayout(bool)` | Handle layout toggle from tray menu |
| `UpdateDisplay()` | Refresh all text values |
| `FetchWeatherAndISPAsync()` | Fetch weather and ISP from APIs |
| `UpdateNetworkSpeed()` | Calculate network speed |
| `InitializeTrayIcon()` | Setup system tray with context menu |

### Data Flow

1. `KeyboardHook` captures key events → `OnKeyDown` increments counters
2. `DispatcherTimer` (1 second) → `Timer_Tick` → updates network speed and display
3. `DispatcherTimer` (10 minutes) → `WeatherTimer_Tick` → fetches weather/ISP

### Weather API Fallback Chain

1. **wttr.in** (primary) - Returns weather + location
2. **Open-Meteo** (fallback 1) - Uses ipapi.co for location
3. **7Timer** (fallback 2) - Uses ip-api.com for location

### Layout System

- **Horizontal Layout**: 610px × 32px, single row
- **Vertical Layout**: Dynamic width (150-300px) × 150px, stacked rows

Layout preference is saved to `config.ini`:
```ini
[Display]
Vertical=true
```

## Coding Conventions

### Naming
- Private fields: `_camelCase`
- Public properties: `PascalCase`
- Methods: `PascalCase`
- Constants: `UPPER_CASE` or `PascalCase`

### File Organization
- One class per file
- XAML and code-behind kept together

### Async Pattern
```csharp
private async Task DoSomethingAsync()
{
    // Use ConfigureAwait(false) if not needed on UI thread
    await SomeOperationAsync();
    
    // Update UI via Dispatcher if needed
    Dispatcher.Invoke(() => UpdateDisplay());
}
```

## Common Development Tasks

### Adding a New Display Field

1. Add `TextBlock` in `MainWindow.xaml` (both layouts)
2. Add field variable in `MainWindow.xaml.cs`
3. Update in `UpdateDisplay()` method

### Adding a New Configuration Option

1. Add property in `IniConfig.cs` if needed
2. Read/write in `MainWindow` constructor and appropriate methods

### Adding a New API

1. Create `TryXxxAsync()` method returning `Task<bool>`
2. Add to fallback chain in `FetchWeatherAndISPAsync()`

## Troubleshooting

### Build Error: File Locked
The application might be running. Kill the process:
```bash
taskkill //f //im KeyboardCounter.exe
```

### Weather/ISP Not Showing
- Check network connectivity
- APIs might be rate-limited (wait a few minutes)
- Check console output for exceptions

### Layout Not Persisting
- Ensure `config.ini` is writable
- Check `IniConfig.Save()` is called after changes

## Notes for AI Agents

1. **Always test changes**: Build and run the application after modifications.
2. **Maintain both layouts**: When adding UI elements, add to both `HorizontalLayout` and `VerticalLayout`.
3. **Update both XAML and code-behind**: UI changes typically need both files.
4. **Keep window dimensions updated**: If adding elements, adjust `Width` in `ApplyLayout()`.
5. **Use Dispatcher for UI updates**: Background threads must use `Dispatcher.Invoke()`.

## Version History

| Version | Changes |
|---------|---------|
| v1.4 | Weather, ISP, download total, layout switching |
| v1.1 | Auto-start on boot |
| v1.0 | Initial release |

---

*This guide is maintained for AI-assisted development. Last updated: 2024*
