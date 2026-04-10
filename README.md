<p align="center">
  <img src="src/WatchDog.App/Resources/WatchDog.png" width="80" alt="WatchDog icon"/>
</p>

<h1 align="center">WatchDog</h1>

<p align="center">
  <em>(formerly known as TikrClpr)</em><br/><br/>
  <strong>Lightweight, open-source game clipping software for Windows.</strong><br/>
  Capture highlights, trim clips, and share to Discord — all from the system tray.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-9.0-blue?logo=dotnet" alt=".NET 9"/>
  <img src="https://img.shields.io/badge/WPF-Windows-blue?logo=windows" alt="WPF"/>
  <img src="https://img.shields.io/badge/OBS-31.0-purple?logo=obsstudio" alt="OBS"/>
  <img src="https://img.shields.io/badge/license-GPL--2.0-green" alt="License"/>
  <img src="https://img.shields.io/github/v/release/thrtn70/WatchDog?label=latest" alt="Release"/>
</p>

---

## What is WatchDog?

WatchDog embeds OBS Studio's recording engine into a native WPF app. It runs in the background, detects when you launch a game, and keeps a rolling replay buffer so you can save the last 2 minutes of gameplay with a single hotkey. It also auto-clips highlight moments (kills, aces, round wins) for supported games.

Think of it as **Outplayed / Medal without the bloat** — no Overwolf, no Electron, no account required.

---

## Features

| Category | Details |
|----------|---------|
| **Replay Buffer** | 120-second rolling buffer. Press **F9** to save the last 2 minutes instantly. |
| **Session Recording** | Record entire game sessions with automatic segment splitting (30 min default). |
| **Highlight Detection** | Auto-clip kills, deaths, aces, round wins for **CS2**, **Valorant**, **Overwatch 2**, and **Rainbow Six Siege X**. |
| **Discord Sharing** | Upload clips via webhook with rich embeds — no bot required. |
| **Clip Editor** | Trim clips with a visual timeline, thumbnail strip, and lossless FFmpeg export. |
| **Session Engine** | Clips organized by gaming session (like Outplayed). Drill into sessions to see clips, matches, and stats. Desktop capture clips grouped by day. |
| **Clip Library** | Session-grouped library with grid/list views, favorites, game filter, sort, and hover-to-scrub thumbnails. |
| **Game Detection** | Auto-detects 100+ games by process name. Auto-starts/stops capture. |
| **GPU Encoding** | NVENC H.264/HEVC/AV1, AMD AMF H.264/HEVC, and x264 CPU fallback. |
| **Audio Mixing** | Live volume sliders, mute toggles, device selection, and separate audio tracks. |
| **Floating Panels** | Draggable, resizable Performance and Audio overlays. |
| **System Tray** | Runs minimized. Right-click for quick actions. Start with Windows. |
| **Storage Management** | Auto-delete old clips, configurable quota, per-game folders, storage dashboard. |
| **Toast Notifications** | Visual popup near the tray when a clip is saved. |

---

## Installation

### Download (Recommended)

1. Go to [**Releases**](https://github.com/thrtn70/WatchDog/releases/latest)
2. Download **`WatchDog-Setup.exe`**
3. Run the installer — no prerequisites needed (everything is bundled)

The installer places WatchDog in Program Files, creates Desktop and Start Menu shortcuts, and registers with Add/Remove Programs for clean uninstall.

**System Requirements:** Windows 10/11 (64-bit). GPU with NVENC (NVIDIA GTX 600+) or AMF (AMD RX 400+) for hardware encoding, or CPU for x264 fallback.

> **Auto-Update:** WatchDog checks for updates on launch. When a new version is available, a banner appears with a one-click install button.
>
> **Portable:** Prefer no installer? Download `WatchDog-win-x64.zip` from the same releases page and run `WatchDog.App.exe` directly.

---

### Development Setup (Contributors)

<details>
<summary>Click to expand build instructions</summary>

#### Prerequisites

- **[.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)** or later
- **[Inno Setup 6](https://jrsoftware.org/isdl.php)** (optional, for building the installer)

#### 1. Clone the repo

```bash
git clone https://github.com/thrtn70/WatchDog.git
cd WatchDog
```

#### 2. Download OBS and FFmpeg runtimes

```powershell
.\tools\setup-obs-runtime.ps1
.\tools\setup-ffmpeg.ps1
```

These scripts download the OBS Studio binaries and FFmpeg into `obs-runtime/` and `ffmpeg-runtime/` (gitignored).

#### 3. Build and run

```bash
dotnet build
dotnet run --project src/WatchDog.App
```

The app starts minimized to the system tray. Right-click the tray icon to open the main window.

#### 4. Package for distribution

```powershell
.\tools\package.ps1
```

This produces both `WatchDog-win-x64.zip` (portable) and `installer/Output/WatchDog-Setup.exe` (installer, requires Inno Setup 6).

</details>

---

## Game-Specific Setup

### CS2 (Counter-Strike 2)

CS2 requires a Game State Integration config file to send events to WatchDog.

```powershell
# Copy the template to your CS2 cfg directory:
Copy-Item cfg\gamestate_integration_watchdog.cfg `
  "C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo\cfg\"
```

Restart CS2 after placing the file. WatchDog will auto-detect the game and start capturing highlights.

### Valorant

No setup needed. The app reads the Riot Client lockfile automatically at:
```
%LocalAppData%\Riot Games\Riot Client\Config\lockfile
```

### Overwatch 2 / Rainbow Six Siege X

No setup needed. The app tails game log files for event detection.

---

## Discord Sharing Setup

1. In Discord: **Server Settings > Integrations > Webhooks > New Webhook > Copy URL**
2. In WatchDog: **Settings > Discord > paste webhook URL > Save**
3. Right-click any clip > **"Share to Discord"**

Clips are uploaded with a rich embed showing game name, highlight type, duration, and file size.

---

## Hotkeys

| Action | Default Key | Configurable |
|--------|-------------|:------------:|
| Save clip (last 120s) | `F9` | Yes |
| Toggle recording | `F10` | Yes |

Change hotkeys in **Settings > Hotkeys** using the interactive key recorder.

---

## Settings Overview

| Section | What you can configure |
|---------|----------------------|
| **Capture** | Resolution, FPS, encoder (NVENC/AMF/x264), quality, bitrate, monitor selection |
| **Audio** | Output/input device, volume, mute, separate audio tracks |
| **Replay Buffer** | Duration (seconds), max size (MB) |
| **Storage** | Save path, max storage (GB), auto-delete age (days) |
| **Highlights** | Post-event delay, cooldown, which event types trigger clips |
| **Recording** | Mode (ReplayBuffer / Session / Both / Highlights), segment duration |
| **Discord** | Webhook URL, bot username, message template, embed toggle |
| **General** | Start with Windows, start minimized |

---

## Project Structure

```
WatchDog/
├── src/
│   ├── WatchDog.App/              # WPF application (Views, ViewModels, Controls, Themes)
│   ├── WatchDog.Core/             # Business logic (Capture, Highlights, Discord, Storage, Audio)
│   └── WatchDog.Native/           # Win32 interop (Hotkeys, OBS runtime loading)
├── tests/
│   └── WatchDog.Core.Tests/       # xUnit unit tests
├── cfg/
│   └── gamestate_integration_watchdog.cfg   # CS2 GSI config template
├── tools/
│   ├── setup-obs-runtime.ps1      # Downloads OBS binaries
│   ├── setup-ffmpeg.ps1           # Downloads FFmpeg binaries
│   └── package.ps1                # Build + package script
├── obs-runtime/                   # OBS binaries (gitignored)
└── ffmpeg-runtime/                # FFmpeg binaries (gitignored)
```

---

## Tech Stack

| Component | Technology |
|-----------|-----------|
| Runtime | .NET 9.0 / C# 13 |
| UI Framework | WPF (Windows Presentation Foundation) |
| Recording Engine | OBS Studio via [ObsKit.NET](https://github.com/niceguy135/ObsKit.NET) |
| Clip Processing | FFmpeg (lossless stream copy) |
| MVVM | [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) |
| System Tray | [H.NotifyIcon.Wpf](https://github.com/HavenDV/H.NotifyIcon) |
| Testing | xUnit |
| Theme | Custom WatchDog dark theme (Archivo + Barlow typography, teal-tinted palette) |

---

## Running Tests

```bash
dotnet test
```

Tests cover highlight detection (CS2, Valorant, OW2, R6), event parsing, settings serialization, storage analytics, session repository CRUD, session lifecycle, and match tracking.

---

## Troubleshooting

<details>
<summary><strong>"Missing OBS components" error on startup</strong></summary>

Run the setup script:
```powershell
.\tools\setup-obs-runtime.ps1
```
If the download fails, manually download [OBS Studio](https://github.com/obsproject/obs-studio/releases) and extract `bin/64bit/`, `data/`, and `obs-plugins/` into `obs-runtime/`.
</details>

<details>
<summary><strong>"FFmpeg not found" warning</strong></summary>

Run the setup script:
```powershell
.\tools\setup-ffmpeg.ps1
```
If it fails, download FFmpeg from [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) (essentials build) and extract `ffmpeg.exe` + `ffprobe.exe` into `ffmpeg-runtime/`.
</details>

<details>
<summary><strong>Clips are black with audio</strong></summary>

Game capture failed to hook the process. Wait 30-60 seconds for the hook to attach, or switch to **desktop capture mode** in Settings. Anti-cheat games may take longer.
</details>

<details>
<summary><strong>Game not detected</strong></summary>

WatchDog recognizes 100+ games by process name. If yours isn't detected, use **desktop capture mode** (always-on). Custom game entries are planned for a future release.
</details>

<details>
<summary><strong>CS2 highlights not working</strong></summary>

1. Verify `gamestate_integration_watchdog.cfg` is in `...\Counter-Strike Global Offensive\game\csgo\cfg\`
2. Restart CS2 after placing the file
3. Check that recording mode is set to **Highlights** in Settings
</details>

<details>
<summary><strong>"Share to Discord" does nothing</strong></summary>

Configure a webhook URL in **Settings > Discord** first. The dialog requires the URL to be set before sharing.
</details>

<details>
<summary><strong>Sessions not appearing in the library</strong></summary>

Sessions are created automatically when a game is detected. If the library shows "No clips yet":
1. Make sure at least one game has been launched and detected by WatchDog
2. Check that the replay buffer or session recording mode is enabled in Settings > Capture
3. Desktop capture sessions are grouped by calendar day under "Desktop"
4. Legacy clips saved before v1.2.0 appear in an "Unsorted" section at the bottom
</details>

<details>
<summary><strong>High CPU usage</strong></summary>

1. Check the Performance overlay (drag it from the top-right corner)
2. Switch to **NVENC encoder** (GPU) instead of x264 (CPU) in Settings > Capture
3. Lower output resolution or FPS
</details>

<details>
<summary><strong>OBS plugin warnings in logs</strong></summary>

Warnings about AJA, DeckLink, or NVIDIA Video FX are harmless. These are optional OBS plugins for professional capture hardware.
</details>

---

## License

WatchDog is released under the [GNU General Public License v2.0](LICENSE).

WatchDog bundles OBS Studio (GPL-2.0) and FFmpeg (LGPL-2.1+) runtimes as part of its build output. This requires the combined distribution to comply with GPL-2.0. See [NOTICE](NOTICE) for full third-party attribution.
