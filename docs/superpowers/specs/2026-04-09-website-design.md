# WatchDog Website — Design Spec

**Date:** 2026-04-09
**Status:** Draft
**Approach:** Pure static HTML + CSS + vanilla JS, hosted on GitHub Pages

---

## Overview

A single-page landing site for WatchDog that lets users download the latest installer, understand what the app does, and troubleshoot common issues. Hosted via GitHub Pages from the `site/` directory in the main repo. No framework, no build step.

---

## File Structure

```
site/
├── index.html              # Single landing page
├── css/
│   └── style.css           # All styles
├── js/
│   └── app.js              # GitHub API fetch, FAQ accordion
├── assets/
│   ├── watchdog-icon.png   # Copied from src/WatchDog.App/Resources/WatchDog.png
│   └── og-image.png        # Social preview (WatchDog icon centered on #0A1114 bg, 1200x630)
└── CNAME                   # Only if custom domain later
```

**Deployment:** GitHub Pages configured to serve from `site/` directory on `main` branch. No GitHub Actions workflow needed — Pages serves static files directly.

**GitHub repo settings to update:**
- Enable Pages, source = `main`, directory = `/site`
- Set homepage URL to the Pages URL

---

## Visual Identity

### Color Palette (matched from WatchDogTheme.xaml)

| Token | Hex | Usage |
|-------|-----|-------|
| `--crust` | `#0A1114` | Page background |
| `--mantle` | `#0E171B` | Section alt background |
| `--base` | `#131D22` | Card backgrounds |
| `--surface` | `#1B2830` | Elevated cards, FAQ items |
| `--surface-1` | `#25353E` | Hover states |
| `--surface-2` | `#31434E` | Active/focus states |
| `--overlay` | `#4A5F6B` | Borders, dividers |
| `--text` | `#D1DCE2` | Primary text |
| `--muted` | `#8A9EAA` | Secondary text |
| `--subtext` | `#7E939E` | Tertiary text |
| `--accent` | `#2EC4B6` | Teal accent (buttons, links, highlights) |
| `--accent-muted` | `#1A7A70` | Hover/pressed accent |
| `--danger` | `#D95555` | Error states |
| `--success` | `#38BF7F` | Success states |
| `--warning` | `#D9B84C` | Warning states |

### Typography

- **Headings:** Archivo (Black, Bold, SemiBold) — loaded from Google Fonts
- **Body:** Barlow (Regular, Medium, SemiBold) — loaded from Google Fonts
- **Scale:** Hero title ~40px, section headings ~24px, body 16px, captions 13px
- **Letter-spacing:** Slight negative tracking on headings for density

### Effects

- Cards: `border: 1px solid var(--overlay)` with subtle `box-shadow` on hover
- Buttons: Solid teal fill, darken on hover to `--accent-muted`
- Smooth scroll between sections via `scroll-behavior: smooth`
- FAQ accordion: CSS transitions on max-height

---

## Page Sections

### 1. Hero

- WatchDog icon (80px) centered
- "WatchDog" in Archivo Black, ~40px
- Tagline: "Lightweight, open-source game clipping for Windows."
- Subtitle: "Capture highlights, trim clips, share to Discord — all from the system tray."
- **Download button:** Large teal CTA — "Download v{version}" with dynamic version
  - Href points to `WatchDog-Setup.exe` asset from latest GitHub release
  - Below: file size + "Windows 10/11 64-bit" + "Portable ZIP" secondary link
- Small badge row: .NET 9 | OBS | GPL-2.0

### 2. Features Grid

3-column grid (2 on tablet, 1 on mobile). 9 feature cards:

1. **Replay Buffer** — Press F9 to save the last 2 minutes instantly
2. **Session Recording** — Record entire sessions with auto-splitting
3. **Highlight Detection** — Auto-clip kills, aces, round wins (CS2, Valorant, OW2, R6S)
4. **Discord Sharing** — Upload clips via webhook with rich embeds
5. **Clip Editor** — Trim with visual timeline and lossless export
6. **Game Detection** — Auto-detects 100+ games, starts/stops capture
7. **GPU Encoding** — NVENC, AMD AMF, and x264 CPU fallback
8. **Storage Management** — Auto-delete old clips, configurable quota
9. **System Tray** — Runs silently in background, start with Windows

Each card: inline SVG icon or Unicode symbol, title in Archivo SemiBold, one-line description in Barlow.

### 3. Quick Start

Three numbered steps in a horizontal row (stacks on mobile):

1. **Download** — "Grab WatchDog-Setup.exe from the button above"
2. **Install** — "Run the installer. If SmartScreen appears, click More info > Run anyway"
3. **Done** — "WatchDog starts in your system tray. Press F9 to save clips."

Below: notes about portable ZIP option and auto-updates.

### 4. Game Setup

CS2 gets its own block with a copy-pasteable PowerShell command for the GSI config. Valorant, OW2, and R6S collapsed into: "Valorant, Overwatch 2, and Rainbow Six Siege work automatically — no setup required."

Discord setup: brief 3-step instruction (create webhook, paste in Settings, right-click clip to share).

### 5. FAQ

Collapsible accordion (pure CSS + JS). Entries:

- **Windows SmartScreen blocked the installer** — Click "More info" then "Run anyway." The app isn't code-signed yet.
- **My clips are black with audio** — Game capture may take 30-60s to hook. Try desktop capture mode as fallback.
- **My game isn't detected** — WatchDog recognizes 100+ games. Use desktop capture mode for unsupported titles.
- **CS2 highlights aren't working** — Verify GSI config placement, restart CS2, check recording mode.
- **F9 hotkey doesn't work** — Another app (Game Bar, Discord) may have registered F9. Change hotkey in Settings or use tray menu.
- **How do I share clips to Discord?** — Set up a webhook URL in Settings > Discord first.

### 6. Footer

Single row: GitHub icon + repo link | "GPL-2.0 License" | "Built with OBS Studio & FFmpeg"

---

## JavaScript Behavior (app.js)

### GitHub Release Fetch

```
On page load:
1. Fetch https://api.github.com/repos/thrtn70/WatchDog/releases/latest
2. Parse: tag_name → version, assets → find WatchDog-Setup.exe → url + size
3. Update:
   - Download button text: "Download v{version}"
   - Download button href: browser_download_url of Setup.exe asset
   - File size text: "{size} MB"
   - Portable link href: browser_download_url of .zip asset
4. Fallback on error:
   - Button text: "Download Latest"
   - Button href: https://github.com/thrtn70/WatchDog/releases/latest
```

### FAQ Accordion

Click handler on FAQ items toggles an `.open` class. CSS transitions `max-height` for smooth expand/collapse. Only one item open at a time (clicking a new one closes the previous).

### Smooth Scroll

Nav links (if any anchor links added) use `scroll-behavior: smooth` CSS property. No JS needed.

---

## Responsive Breakpoints

| Breakpoint | Layout |
|------------|--------|
| > 1024px | Desktop: 3-col features, horizontal quick-start steps |
| 768–1024px | Tablet: 2-col features, horizontal steps |
| < 768px | Mobile: 1-col everything, stacked steps |

Max content width: `1100px` centered with `margin: 0 auto`.

---

## SEO & Meta

```html
<title>WatchDog — Lightweight Game Clipping for Windows</title>
<meta name="description" content="Capture highlights, trim clips, and share to Discord. Open-source, GPU-accelerated, runs from the system tray.">
<meta property="og:title" content="WatchDog — Game Clipping for Windows">
<meta property="og:description" content="Lightweight, open-source replay buffer and highlight clipper. NVENC/AMF GPU encoding, auto game detection, Discord sharing.">
<meta property="og:image" content="og-image.png">
<meta name="theme-color" content="#2EC4B6">
```

---

## Out of Scope

- No screenshots section (deferred — add later when ready)
- No analytics or tracking
- No JavaScript framework
- No build step or bundler
- No custom domain (uses default github.io URL)
- No multi-page structure (single index.html only, expandable later)

---

## Verification

1. Open `site/index.html` locally in a browser — all sections render correctly
2. Download button fetches latest release version and links to correct asset
3. FAQ accordion expands/collapses smoothly
4. Responsive layout works at desktop, tablet, and mobile widths
5. Enable GitHub Pages in repo settings (source: main, dir: /site) and verify live URL
