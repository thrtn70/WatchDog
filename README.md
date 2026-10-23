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
  <a href="https://thrtn70.github.io/WatchDog/"><img src="https://img.shields.io/badge/Website-thrtn70.github.io%2FWatchDog-2EC4B6?logo=github" alt="Website"/></a>
  <img src="https://img.shields.io/badge/.NET-10.0-blue?logo=dotnet" alt=".NET 10"/>
  <img src="https://img.shields.io/badge/OBS-31.0-purple?logo=obsstudio" alt="OBS"/>
  <img src="https://img.shields.io/badge/license-GPL--2.0-green" alt="License"/>
  <img src="https://img.shields.io/github/v/release/thrtn70/WatchDog?label=latest" alt="Release"/>
</p>

---

## What is WatchDog?

WatchDog embeds OBS Studio's recording engine into a native WPF app. It runs in the background, detects when you launch a game, and keeps a rolling replay buffer so you can save the last 2 minutes of gameplay with a single hotkey. It also auto-clips highlight moments (kills, aces, round wins) for supported games.

Think of it as **Outplayed / Medal without the bloat** — and also without the blood of innocent children.

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
| **Desktop Capture** | Optional: keep a replay buffer of the desktop when no game is running. **Off by default for new installs**; turn on **Always-on desktop capture** in Settings. Existing users keep their setting. |
| **Encoding** | Replay buffer: **x264** (software) or **NVENC** H.264/HEVC/AV1 (NVIDIA). Session recordings and separate webcam files use x264. AMD AMF and Intel QSV are not supported. |
| **Audio Mixing** | Live volume sliders, mute toggles, device selection, and separate audio tracks. |
| **Floating Panels** | Draggable, resizable Performance and Audio overlays. |
| **System Tray** | Runs minimized. Right-click for quick actions. Start with Windows. |
| **Storage Management** | Auto-delete old clips, configurable quota, per-game folders, storage dashboard. |
| **Toast Notifications** | Visual popup near the tray when a clip is saved. |

---

## Known Limitations

- **Tutorial recording is not available yet.** The **Tutorial (planning only)** workspace can plan a window, display or region with an optional camera, but **Start recording stays disabled** in this release. Region capture, pause/resume, redaction/crop/zoom/camera layers and tutorial editing are deferred to a later release.
- **Encoders: x264 and NVENC only.** x264 runs on any CPU. NVENC needs an NVIDIA GPU and driver that support it. AMD AMF and Intel QSV are not supported. There is no automatic fallback: if NVENC fails to start, select x264 in **Settings > Capture**.
- **OBS runs in-process.** WatchDog loads its bundled OBS Studio 31 engine (libobs) inside the WatchDog process; no separate OBS install is needed or used. A crash in libobs, an OBS plugin or a GPU/capture driver also closes WatchDog.
- **Hardware qualification is per release candidate.** Automated tests do not exercise real GPUs, displays or audio devices. NVENC availability and performance, multi-monitor and mixed DPI, HDR and sleep/resume are checked on real hardware for each release candidate, not by CI.
- **The installer is not code-signed.** Windows SmartScreen may warn on first run. In-app updates are authenticated separately (see Auto-Update below).
- **Windows 10/11 64-bit only.**

---

## Installation

> **See the [WatchDog website](https://thrtn70.github.io/WatchDog/) for screenshots, feature overview, and setup guides.**

### Download (Recommended)

1. Go to [**Releases**](https://github.com/thrtn70/WatchDog/releases/latest)
2. Download **`WatchDog-Setup.exe`**
3. Run the installer — no prerequisites needed (everything is bundled)

The installer places WatchDog in Program Files, creates Desktop and Start Menu shortcuts, and registers with Add/Remove Programs for clean uninstall.

**System Requirements:** Windows 10/11 (64-bit). Select x264 for software encoding, or an implemented NVENC option whose availability depends on your hardware/runtime and remains unverified. There is no automatic encoder fallback. The engine prepares the selected encoder even for session-only starts; AMF and Intel/QSV selections are unsupported.

> **Auto-Update:** WatchDog checks for updates on launch. When a new version is available, a banner appears with a one-click install button. The updater runs an installer only when its signed update manifest verifies against the key built into the app and matches the downloaded bytes. WatchDog reopens by itself once the update is installed.
>
> **Updating from v1.4.0 or older on a slow connection:** those versions stop an update download after 5 minutes. If the in-app update doesn't finish, download `WatchDog-Setup.exe` from [Releases](https://github.com/thrtn70/WatchDog/releases) and run it. Your settings and clips are kept.
>
> **Portable:** Prefer no installer? Download `WatchDog-win-x64.zip` from the same releases page and run `WatchDog.App.exe` directly.

---

### Development Setup (Contributors)

<details>
<summary>Click to expand build instructions</summary>

#### Prerequisites

- **[.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)** (see `global.json`)
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

This produces both `WatchDog-win-x64.zip` (portable) and `installer/Output/WatchDog-Setup.exe` (installer, requires Inno Setup 6). Both include `LICENSE` and `NOTICE`.

To also write the signed update manifest for a release, set `WATCHDOG_UPDATE_SIGNING_KEY` to the signing key and pass the tag (`.\tools\package.ps1 -Tag v1.4.0`). Without it, the in-app updater refuses the installer. Tagged releases are signed by `.github/workflows/release.yml` from the repository secret of the same name.

#### 5. Install git hooks (one-time)

```bash
./scripts/install-hooks.sh
```

Installs the pre-push gate at `.git/hooks/pre-push` (a symlink to `scripts/pre-push.sh`). The gate runs `dotnet format --verify-no-changes` and a lightweight build via SSH to a Windows dev VM (`win11` host alias). Heavy checks (full `dotnet build` with warnings-as-errors plus `dotnet test`) run in CI on push/PR. Bypass for emergencies with `SKIP_PREPUSH=1 git push`.

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
| **Capture** | Resolution, FPS, replay encoder (NVENC hardware unverified / explicit x264), quality, bitrate, monitor selection. Unsupported saved encoders remain visible until explicitly replaced. |
| **Audio** | Output/input device, volume, mute, separate audio tracks |
| **Replay Buffer** | Duration (seconds), max size (MB) |
| **Storage** | Save path, max storage (GB), auto-delete age (days) |
| **Highlights** | Post-event delay, cooldown, which event types trigger clips |
| **Recording** | Mode (ReplayBuffer / Session / Both / Highlights), segment duration |
| **Discord** | Webhook URL, bot username, message template, embed toggle |
| **General** | Start with Windows, start minimized |

Saved game profiles apply to the next automatic start for the exact executable. Auto Record can disable that start; Replay Buffer and Session Recording select their respective outputs. Supported bitrate overrides and replay duration are frozen before window acquisition. Null overrides inherit sanitized defaults without changing global settings. Session/webcam encoders remain x264. Bitrate overrides for CQP/CRF replay, session-only buffer overrides, Highlights, sensitivity overrides and per-game canvas changes report an actionable unsupported reason. Change global resolution/FPS and restart to change the canvas. Editing a profile never reconfigures an active recording. Hardware/device behavior remains unqualified by managed tests.

---

## Project Structure

```
WatchDog/
├── src/
│   ├── WatchDog.App/              # WPF application (Views, ViewModels, Controls, Themes)
│   ├── WatchDog.Core/             # Business logic (Capture, Highlights, Discord, Storage, Audio)
│   └── WatchDog.Native/           # Win32 interop (Hotkeys, OBS runtime loading)
├── tests/
│   ├── WatchDog.Core.Tests/       # xUnit unit tests
│   └── WatchDog.Integration.Tests/  # App-level integration tests (fake capture backend)
├── cfg/
│   └── gamestate_integration_watchdog.cfg   # CS2 GSI config template
├── tools/
│   ├── setup-obs-runtime.ps1      # Downloads OBS binaries
│   ├── setup-ffmpeg.ps1           # Downloads FFmpeg binaries
│   ├── package.ps1                # Build + package script
│   └── sign-update-manifest.ps1   # Signs the update manifest the in-app updater requires
├── obs-runtime/                   # OBS binaries (gitignored)
└── ffmpeg-runtime/                # FFmpeg binaries (gitignored)
```

---

## Tech Stack

| Component | Technology |
|-----------|-----------|
| Runtime | .NET 10 / C# 14 |
| UI Framework | WPF (Windows Presentation Foundation) |
| Recording Engine | OBS Studio (libobs, in-process) via [ObsKit.NET](https://github.com/niceguy135/ObsKit.NET) |
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
2. An implemented **NVENC encoder** in Settings > Capture may reduce CPU work if your hardware/runtime supports it. Availability and performance are unverified; explicitly choose x264 if startup fails.
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
