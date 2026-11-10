# Unified Capture Source Model — Detection & Session Redesign

**Date:** 2026-04-11
**Status:** Approved
**Approach:** B — Unified Capture Source Model

## Context

WatchDog has three interconnected problems with its game detection and session systems:

1. **Duplicate sessions** — Game launchers (Riot Client, Epic, Steam) trigger separate sessions before the actual game starts. Game crashes/restarts and WatchDog restarts mid-game also create duplicate session entries for what the user perceives as one play session.
2. **Empty sessions** — Sessions with zero clips clutter the dashboard. These should be auto-deleted when the session ends.
3. **No manual capture** — Users who launch games before WatchDog, or play unsupported games, have no way to manually select what to capture. A manual window capture mode with AI audio highlight detection as fallback solves this.

## Design

### CaptureSource Record
A `CaptureSource` abstraction treats auto-detected games, manual window selections, and desktop capture as first-class capture sources with a shared lifecycle. Three kinds: `Auto`, `Manual`, `Desktop`.

### Session Deduplication
Three rules: (1) launcher executable blacklist prevents launcher processes from creating sessions, (2) same-executable merge window of 120 seconds prevents rapid restart duplicates, (3) WatchDog restart resumes orphaned sessions whose game is still running instead of marking them crashed.

### Empty Session Auto-Delete
When a session ends, if it has zero clips and no recording paths, it gets deleted from storage. Sessions with recordings survive.

### Manual Window Capture
Users select a running window from a picker (available in both the dashboard status bar and tray context menu). Capture starts with OBS window capture mode, AI audio highlight detection runs as fallback, and the replay buffer enables hotkey clipping. Manual capture suppresses auto-detection; auto resumes when manual ends.

### CaptureSourceManager
Replaces `GameDetectorHostedService` as the orchestration layer. Owns the active capture source lifecycle, enforces mutual exclusion between manual and auto modes, and implements the deduplication merge window.

### OBS Window Capture
New `StartWindowCaptureAsync` method on `ObsCaptureEngine` using OBS `window_capture` source. Falls back to monitor capture if window capture fails.

## Future: Approach C
Full state machine rewrite (`Idle → Detecting → Capturing → PostSession → Cleanup`) deferred. The `CaptureSource` model is forward-compatible with that approach.
