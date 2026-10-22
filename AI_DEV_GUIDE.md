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
├── MainWindow.xaml.cs        # Main logic (timers, hooks, API calls, translate wiring)
├── KeyboardHook.cs           # Low-level keyboard hook (key down/up)
├── MouseHook.cs              # Low-level mouse hook (drag / double-click gesture)
├── SelectionWatcher.cs       # Selection watcher (hook + polling fallback)
├── SelectionCapture.cs       # Read selected text (UIA first, Ctrl+C fallback)
├── UiaSelectionReader.cs     # Dedicated STA thread (with message pump) for UIA calls
├── TranslatePopup.xaml       # Selection-translation bubble window
├── TranslatePopup.xaml.cs
├── ApiConfigWindow.xaml      # Translation provider settings (Youdao/Baidu)
├── ApiConfigWindow.xaml.cs
├── TranslateSettings.cs      # config.ini [Translate] section
├── TranslationCache.cs       # SQLite translation cache (cache\translate_cache.db)
├── EcdictDictionary.cs       # Offline ECDICT dictionary (dict\dictionary.db)
├── tools/EcdictImporter/     # Console tool: ecdict.csv -> dictionary.db (excluded from the app build)
├── ITranslationProvider.cs   # Provider abstraction
├── TranslationResult.cs
├── TranslationService.cs     # Provider factory
├── TranslationException.cs   # User-facing translation errors
├── YoudaoTranslator.cs       # Youdao text translation (v3 signature)
├── BaiduTranslator.cs        # Baidu general text translation
├── PronunciationService.cs   # Pronunciation (free dict TTS + MediaPlayer)
├── LanguageUtil.cs           # Language detection / text cleanup
├── ScreenGeometry.cs         # Screen px <-> DIP conversion
├── DebugTrace.cs             # Opt-in debug log (kc_debug.log)
├── IniConfig.cs              # Simple INI file parser/writer
├── DailyStats.cs             # Per-day counters persisted to JSON
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
| `MainWindow` | Main window with timers, display logic, API calls, translate wiring |
| `KeyboardHook` | Global keyboard hook (`LowLevelKeyboardProc`), key down/up |
| `MouseHook` | Global mouse hook (`WH_MOUSE_LL`), detects drag / double-click gestures |
| `SelectionWatcher` | Owns the gesture sources, grabs the selection, raises `SelectionCaptured` |
| `SelectionCapture` | Simulates Ctrl+C, watches the clipboard sequence number, restores the clipboard |
| `TranslatePopup` | Borderless, non-activating, topmost bubble that follows the cursor |
| `ApiConfigWindow` | Provider picker + key fields + test button |
| `IniConfig` | Simple key-value INI file handling |
| `DailyStats` | Per-day counters persisted to JSON |

### Important Methods in MainWindow

| Method | Purpose |
|--------|---------|
| `ApplyLayout()` | Switch between horizontal/vertical layouts |
| `ToggleLayout(bool)` | Handle layout toggle from tray menu |
| `UpdateDisplay()` | Refresh all text values |
| `FetchWeatherAndISPAsync()` | Fetch weather and ISP from APIs |
| `UpdateNetworkSpeed()` | Calculate network speed |
| `InitializeTrayIcon()` | Setup system tray with context menu |
| `InitializeTranslation()` | Start/stop the selection watcher based on config |
| `TranslateSelectionAsync()` | Translate text and drive the popup (loading/result/error) |
| `TranslateClipboardAsync()` | `Ctrl+Alt+T` / tray entry: translate clipboard text |
| `ShowTranslateConfig()` | Open the provider settings dialog and persist the result |

### Translation Flow

1. `MouseHook` (or the 35 ms polling fallback in `SelectionWatcher`) detects a drag / double-click gesture
2. **The hook callback only enqueues work** (`Dispatcher.BeginInvoke`) and returns — it must never run UIA, clipboard or file I/O
3. `SelectionWatcher.CaptureSelectionTextCoreAsync()` branches on `SelectionMode` (see below) and returns the text plus a bubble anchor (the selection's last rectangle from UIA, falling back to the mouse point)
4. `UiaSelectionReader` runs every UIA call on its own STA thread (with a `Dispatcher.Run()` message pump); `SelectionCapture.ReadSelectionViaUia()` walks up to 5 ancestors looking for `TextPattern` and calls `GetSelection()`
5. `MainWindow.TranslateSelectionAsync()` resolves the text through three tiers:
   1. `EcdictDictionary.TryLookup()` — offline word/phrase, 0.1–3 ms, shows phonetic + multiple senses (`· 离线词典` in the header); works without API keys
   2. `TranslationCache.TryGet(provider.Name, text, out result)` — previously translated text (`· 缓存`)
   3. online provider via `TranslationService.Create(settings)`; a successful result is written back with `TranslationCache.Put(...)`
6. `YoudaoTranslator` / `BaiduTranslator` call their HTTP APIs and return `TranslationResult`
7. Pronunciation is currently disabled (see the commented block at the end of `TranslateSelectionAsync`; `PronunciationService.cs` is kept but unused)

### Selection Modes (`[Translate] SelectionMode`)

| Mode | Clipboard | Behaviour |
| --- | --- | --- |
| `UiaOnly` (**default**) | never read or written, except for whitelisted processes | up to 4 UIA attempts: cursor (800 ms) → focused (400 ms) → cursor (400 ms) → focused (400 ms), 60 ms apart; on failure raises `SelectionCaptureFailed` and `MainWindow` shows a throttled balloon (30 s) suggesting the whitelist or `Ctrl+Alt+T` |
| `UiaWithClipboard` | simulated Ctrl+C, then restored | UIA gets a 100 ms head start, then both run in parallel; clipboard restore is skipped when `GetClipboardSequenceNumber()` shows a third party wrote it after our copy |
| `ClipboardOnly` | simulated Ctrl+C, then restored | legacy fallback |

- `SelectionMode` is read at `SelectionWatcher` construction, so changing it disposes and recreates the watcher (`MainWindow.ApplySelectionMode`).
- **`ClipboardFallbackProcesses` whitelist** (`[Translate]`, e.g. `typora.exe,WeChat.exe`): when the gesture's process (window under cursor, else foreground) is listed, `SelectionWatcher` skips UIA entirely and captures through the clipboard path. Everything else stays clipboard-free. Tray menu → 「把上一个取词失败的程序加入白名单」 calls `MainWindow.AddLastFailedProcessToWhitelist()`, which appends `_lastFailedProcessName` recorded in `OnSelectionCaptureFailed`.
- Known limitation: Electron apps (Typora) expose only `Chrome Legacy Window` with no `TextPattern` in the tree, and Qt apps (WeChat) expose no text either — hence the whitelist. QQ / Chrome / Edge / VS / Office / Notepad / WPF / WinForms work through UIA; the ancestor walk now covers 12 levels because Chrome text nodes can sit at depth 5.
- `ClipboardSnapshot` snapshots text / image / file-drop-list; it is only used by the two clipboard modes and the whitelist path.
- The popup's 复制 button writes the clipboard on explicit user click — that is intentional and independent of `SelectionMode`.

### Offline Dictionary (ECDICT)

- Data: [ECDICT](https://github.com/skywind3000/ECDICT) (MIT) for en→zh + [CC-CEDICT](https://www.mdbg.net/chinese/dictionary?page=cedict) (CC BY-SA 4.0) for zh→en, in one SQLite file at `<exe dir>\dict\dictionary.db` — **768 739** English words + **196 374** inflected forms + **203 238** Chinese rows, 74.7 MB
- Generated by `tools/EcdictImporter` (console project, excluded via `<Compile Remove="tools/**" />`):
  `dotnet run -c Release -- <ecdict.csv> <out>\dictionary.db [maxFrq] [cedict.txt]`
- Schema: `words(word PK COLLATE NOCASE, phonetic, translation, pos, tag, bnc, frq)`, `forms(form PK COLLATE NOCASE, word)`, `zh(hanzi PK COLLATE NOCASE, traditional, pinyin, english)`; `translation` keeps `\n` → real newlines so multiple senses render on separate lines
- `forms` is built from ECDICT's `exchange` field: `0:lemma` marks a word as inflected, other types (`p/d/i/3/s/r/t`) map an inflected form back to its lemma
- `zh` rows are inserted twice (simplified and traditional keys) so both scripts hit
- `TryLookup` order: CJK → `zh` exact (≤8 chars, longer goes online); latin → exact (case-insensitive) → `forms` lemma → suffix heuristics (`s/es/ed/ing/er/est/ies`); latin queries >120 chars are skipped
- Measured: 0.09 ms per lookup (en and zh)
- Missing `dict\dictionary.db` is not fatal: `TryOpen` returns null and the app degrades to cache + online

### Translation Cache

- File: `<exe dir>\cache\translate_cache.db`, SQLite via `Microsoft.Data.Sqlite` (WAL, `synchronous=NORMAL`)
- Table `translations(provider, source, target, from_lang, to_lang, hits, created_at, used_at)`, primary key `(provider, source)` — the provider name is part of the key so Youdao/Baidu entries stay separate
- Key is the normalized selection text (`LanguageUtil.Normalize`), so byte-identical selections hit
- `hits` / `used_at` are bumped on every hit; `Prune()` removes the least-recently-used rows above `MaxEntries` (20000) at startup
- Any SQLite failure is caught: the cache degrades to "always miss" and translation keeps working
- Note: the native `e_sqlite3.dll` is part of the output, so deployments must copy the whole publish folder (a single-file copy of `KeyboardCounter.exe` is not enough)

**Why the STA worker thread matters**: `AutomationElement.FromPoint` on a thread-pool (MTA) thread or on a thread without a message pump took >800 ms and timed out in testing; on an STA thread that runs `Dispatcher.Run()` the same call takes 16–60 ms. `SelectionWatcher` also guards against overlapping UIA reads (`_pendingUiaRead`).

### Selection Gesture Rules

- Drag: press-to-release movement > 4 px
- Double-click: two clicks within 450 ms, movement ≤ 6 px
- Skipped windows: console/terminal classes (Ctrl+C would interrupt the process), Explorer / desktop / taskbar
- Skipped when Ctrl/Alt is held, or when the click lands on this app's own windows

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

### Adding a New Translation Provider

1. Implement `ITranslationProvider` (see `YoudaoTranslator` / `BaiduTranslator`)
2. Map provider-specific errors to `TranslationException` with a Chinese message
3. Add the enum value in `TranslateSettings` + a branch in `TranslationService.Create()`
4. Add menu entry in `InitializeTrayIcon()` and fields in `ApiConfigWindow.xaml`

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

### Selection Translation Not Popping Up
- Set `[Translate] Debug=true` in `config.ini`, restart, then read `kc_debug.log` next to the exe
- `uia selection len=N` / `clipboard capture len=N` → which path produced the text (UIA is preferred)
- `gesture at (...)` logged but both lengths are -1 → the app under the cursor does not expose UIA `TextPattern` and did not react to Ctrl+C (elevated window, or a control without text support)
- No `gesture at` line at all → the mouse hook receives nothing (security software blocking `WH_MOUSE_LL`, or a sandboxed/restricted process); the 35 ms `GetAsyncKeyState` polling fallback covers most of those cases, and `Ctrl+Alt+T` always works
- Note: `GetAsyncKeyState` returns 0 for processes with restricted input visibility, so the polling fallback is silently inert there

### Tray Icon Does Not Appear
- Windows silently refuses `Shell_NotifyIcon` for executables stored under some directories (verified: the same binary shows the icon when moved out of the directory). Move the exe to a normal folder.
- `MainWindow.ScheduleTrayIconReRegister()` re-adds the icon 2.5 s after startup as a workaround for stale shell state.
- The floating overlay's right-click menu (`OnMouseRightButtonUp` → `_trayMenu.Show`) exposes the same menu, so configuration is reachable without the tray.

### Whole Desktop Stutters / Mouse Lags After Selecting Text
- Low-level mouse hook callbacks are dispatched on the thread that installed the hook, and the OS blocks mouse input until the callback returns. `MouseHook` therefore runs on its **own STA thread with a message loop** and the callback only records state + raises events; `SelectionWatcher` always marshals with `Dispatcher.BeginInvoke`.
- Two metrics in `kc_debug.log` (needs `Debug=true`), logged as `hook cb=Xms inputDelay=Yms`:
  - `hook cb` — callback duration, must be < 10 ms (measured 0.0–0.8 ms)
  - `inputDelay` — event timestamp vs. now, i.e. how long Windows waited for our hook (measured 0–16 ms, which is TickCount granularity)
- If `inputDelay` grows, something blocked the hook thread or the hook was moved back onto the UI thread.

## Notes for AI Agents

1. **Always test changes**: Build and run the application after modifications.
2. **Maintain both layouts**: When adding UI elements, add to both `HorizontalLayout` and `VerticalLayout`.
3. **Update both XAML and code-behind**: UI changes typically need both files.
4. **Keep window dimensions updated**: If adding elements, adjust `Width` in `ApplyLayout()`.
5. **Use Dispatcher for UI updates**: Background threads must use `Dispatcher.Invoke()`.
6. **Never block hook callbacks**: hooks fire on the UI thread and are subject to the OS `LowLevelHooksTimeout`; keep callbacks to field updates + `BeginInvoke`, no file/network I/O.
7. **Clipboard is thread-affine**: all `Clipboard` access must happen on the WPF UI (STA) thread.

## Version History

| Version | Changes |
|---------|---------|
| v2.1 | SelectionMode (UiaOnly default, no clipboard I/O), UIA retries + focused-element fallback |
| v2.2 | Capture whitelist (`ClipboardFallbackProcesses`), UIA walk depth 5 → 12, tray item to whitelist the last failed app |
| v2.1 | SelectionMode (UiaOnly default, no clipboard I/O), UIA retries + focused-element fallback |
| v2.0 | CC-CEDICT zh→en, selection-rectangle bubble anchor, screenshot-overlay blocklist, clipboard sequence guard |
| v1.9 | Clipboard snapshot/restore, parallel capture, pending hint |
| v1.8 | Offline ECDICT dictionary (3-tier lookup), hook moved off the UI thread, popup warm-up |
| v1.7 | SQLite translation cache, UIA-first capture, hook latency fix, pronunciation disabled |
| v1.6 | Selection translation (Youdao/Baidu), pronunciation, clipboard translate hotkey |
| v1.5 | Daily stats, network change detection, IP change detection |
| v1.4 | Weather, ISP, download total, layout switching |
| v1.1 | Auto-start on boot |
| v1.0 | Initial release |

> v1.0 – v1.5 were built by **Claude Code**; v1.6 – v2.2 by **DeepSeek Harness**.

---

*This guide is maintained for AI-assisted development. Last updated: 2026 (DeepSeek Harness)*
