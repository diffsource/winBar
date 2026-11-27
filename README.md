# winBar — a lightweight Windows 11 taskbar customizer

[![Build](https://github.com/diffsource/winBar/actions/workflows/build.yml/badge.svg)](https://github.com/diffsource/winBar/actions/workflows/build.yml) [![Latest release](https://img.shields.io/github/v/release/diffsource/winBar?label=release)](https://github.com/diffsource/winBar/releases/latest)

**Make the Windows 11 taskbar feel quieter.** winBar gives you a transparent, blurred, or solid taskbar; reveals pinned apps and tray icons only when you need them; adds a compact clock; and hides selected app buttons. It runs locally in the notification area with no account or telemetry.

[Download the latest release](https://github.com/diffsource/winBar/releases/latest/download/TaskbarTweaker.zip) · [See all releases](https://github.com/diffsource/winBar/releases) · [Report an issue](https://github.com/diffsource/winBar/issues)

## Get started

1. Install the [.NET 10 Desktop Runtime for Windows x64](https://dotnet.microsoft.com/download/dotnet/10.0) if you do not already have it.
2. Download [TaskbarTweaker.zip](https://github.com/diffsource/winBar/releases/latest/download/TaskbarTweaker.zip) and extract it completely.
3. Run `TaskbarTweaker.exe`. Keep `TaskbarTap.dll` next to it.

A short welcome tour appears on first use. To reopen settings later, **hold Alt and move your pointer onto the taskbar**, press **Ctrl + Alt + S**, or use the **winBar tray icon**. Closing Settings leaves winBar running. Choose **Exit** from the tray icon menu to stop it and restore the taskbar.

## What you can customize

| Feature | What it does |
| --- | --- |
| **Clear, Frost, and Solid looks** | Switch between transparent, blurred, and tinted taskbar styles. Adjust color and opacity, or adapt the tint to the Windows accent color and light/dark mode. |
| **On-demand pinned apps** | Keep up to five favorite pinned apps visible. Hold Alt to reveal the rest; running apps remain visible. |
| **On-demand tray** | Reveal hidden notification icons when holding Alt or moving near the right edge of the taskbar. |
| **Clean preset** | Hide Search, Widgets, Task View, badges, and tray clutter together. Click again to restore the previous values. |
| **Compact clock** | Show a styled time display, with optional date and a primary-monitor-only setting. |
| **Hide app buttons** | Choose running programs or EXE files whose taskbar buttons should be hidden. Their windows remain open and accessible with Alt + Tab. |
| **Work and Personal profiles** | Save two layouts, switch with **Ctrl + Alt + 1/2**, and import or export profiles as JSON. |
| **Automatic updates** | Check stable GitHub releases in the background, verify the download, and use the new version on the next normal launch. No forced restart. |

You can also set taskbar alignment, grouping, auto-hide, Search, Widgets, Copilot, Chat/Teams, the desktop corner, and other Windows taskbar options. Some Windows settings need an Explorer restart before they appear.

## A few practical details

- **Windows 11 x64** is the target. The Explorer helper uses Windows 11 XAML internals, so a Windows update may temporarily break some effects. If a new version changes the helper, restart Explorer when convenient to load the new DLL.
- The app's current interface and first-use tour are in German; this README is in English for the wider Windows community.
- **On-demand pinned apps** is experimental. Favorites are the first pinned apps without a running window, in their existing Windows order.
- **Hiding an app** affects its running taskbar buttons by full EXE path. It does not unpin the app, and some programs manage their own buttons.
- **The clock** sits over the Windows clock and lets clicks reach Windows. It hides during full-screen use.
- **Automatic updates** download from this repository's stable GitHub Releases. They are enabled by default and can be switched off in Settings. The first release with this feature is v1.0.2; earlier versions need one manual update. A staged update starts next time you launch winBar, so the current session is uninterrupted.
- Settings are stored in `%AppData%\TaskbarTweaker\settings.json`. Exported profiles can include full EXE paths; review a profile before sharing it.

## Build from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and Visual Studio with **Desktop development with C++** (MSVC x64). From the repository root:

```powershell
dotnet publish -c Release -o publish -p:PublishSingleFile=true --self-contained false
```

This builds `TaskbarTap.dll` from `tap/TaskbarTap.cpp`. Distribute `publish/TaskbarTweaker.exe` together with `publish/TaskbarTap.dll`. The [Windows build workflow](https://github.com/diffsource/winBar/actions/workflows/build.yml) also produces both files as an artifact.

## How it works

winBar uses Windows taskbar settings and `SetWindowCompositionAttribute` for appearance. A small native DLL runs inside Explorer to adjust the Windows 11 taskbar's XAML layer. Selected running app buttons use `ITaskbarList`. The Alt interaction is checked every 100 ms; heavier visual and window checks run less often. Updates are checked every 12 hours, with no background service or installer. The Explorer integration uses undocumented Windows interfaces and may be flagged by security software.

## Help shape winBar

Have a bug report or a focused idea for a cleaner taskbar? [Open an issue](https://github.com/diffsource/winBar/issues). If winBar is useful to you, starring the repository helps other Windows users find it.

Built by [diffsource](https://github.com/diffsource).
