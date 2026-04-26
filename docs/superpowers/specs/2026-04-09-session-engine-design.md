# Session Engine: Design Spec

## Overview

Replace WatchDog's flat clip library with session-based organization. Clips are grouped under gaming sessions (defined by game process lifetime). Supported games (CS2, Valorant, R6, OW2) get per-match grouping within sessions. Unsupported games get a single session block. Desktop capture clips are grouped by calendar day under a virtual "Desktop" game.

## User Experience

### Session Lifecycle

1. **Game launches** -- WatchDog creates a `GameSession`, starts replay buffer. If auto-record is enabled for that game, full session recording also starts.
2. **During gameplay** -- hotkey clips and auto-highlights are tagged with the active session ID. For supported games, match boundaries are detected from game APIs, and clips are also tagged with match number.
3. **Game exits** -- session is closed with an end timestamp. Session recording stops (if running). Session appears in the library.
4. **Desktop capture** -- when no game is detected and desktop capture is enabled, clips are assigned to a daily "Desktop" session. A new Desktop session is created at midnight or on first desktop clip of the day.
5. **App crash / hard reboot** -- orphaned sessions (no EndedAt) are detected on next startup. They're closed using the timestamp of the last known recording segment or clip.

### Library View (replaces current flat grid)

The main clip library becomes session-grouped, sorted newest-first:

```
Counter-Strike 2  |  Apr 9, 2026
  3 matches  |  12 clips  |  1h 45m
  [Match 03 card] [Match 02 card] [Match 01 card]

Roblox  |  Apr 9, 2026
  Session  |  3 clips  |  45m
  [Session 01 card]

Desktop  |  Apr 9, 2026
  5 clips  |  scattered throughout the day

Counter-Strike 2  |  Apr 8, 2026
  ...

Unsorted
  Clips from before session tracking  |  24 clips
```

- Sessions with matches show match cards (thumbnail, match number, score, map when available)
- Sessions without match detection show a single session card
- Desktop sessions group all desktop capture clips from that calendar day
- Legacy clips (pre-session-tracking) appear in an "Unsorted" group at the bottom
- Game filter dropdown still works (filters to sessions of that game)

### Session Detail View

Clicking a session opens a detail view:

**For sessions with auto-record enabled:**
- Match cards across the top (for supported games)
- Full video player with the session recording
- Timeline with highlight markers (kills, deaths, round wins) at exact timestamps
- Clip thumbnails below timeline for quick navigation

**For sessions without auto-record:**
- Match cards across the top (for supported games)
- Clip grid showing all clips from that session, sorted by time
- Clicking a clip opens it in the existing editor panel

### Per-Game Settings

New section in Settings: "Game Profiles"
- List of detected games the user has played
- Per-game toggle: "Auto-record full sessions" (default: off)
- Falls back to global settings for quality, buffer size, hotkeys
- Keyed by executable name (not display name) for stability

## Data Model

### GameSession (new)

```csharp
public sealed record GameSession
{
    public required Guid Id { get; init; }
    public required string GameName { get; init; }
    public required string GameExecutableName { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public SessionStatus Status { get; init; } = SessionStatus.InProgress;
    public IReadOnlyList<SessionMatch> Matches { get; init; } = [];
    public IReadOnlyList<string> RecordingPaths { get; init; } = [];
    public bool IsAutoRecorded { get; init; }
}

public enum SessionStatus { InProgress, Completed, Crashed }
```

### SessionMatch (new, embedded in GameSession)

```csharp
public sealed record SessionMatch
{
    public required int MatchNumber { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public string? Map { get; init; }
    public string? Score { get; init; }
    public string? GameMode { get; init; }
    public MatchResult? Result { get; init; }
    public IReadOnlyList<string> ClipIds { get; init; } = [];
}

public enum MatchResult { Win, Loss, Draw, Abandoned, Unknown }
```

### ClipMetadata (updated)

Two new nullable fields added to the existing record:

```csharp
public Guid? SessionId { get; init; }
public int? MatchNumber { get; init; }
```

Null means legacy clip (pre-session-tracking). Both fields are optional for backward compatibility.

### Desktop Sessions

Desktop capture clips use a synthetic session:
- `GameName = "Desktop"`
- `GameExecutableName = "desktop"`
- `StartedAt` = start of the calendar day (midnight local time)
- `EndedAt` = end of the calendar day
- No matches (desktop sessions are always flat)
- A new Desktop session is created daily on the first desktop clip or at midnight if desktop capture is running

### GameRecordingProfile (new settings)

```csharp
public sealed record GameRecordingProfile
{
    public required string GameExecutableName { get; init; }
    public bool AutoRecord { get; init; }
}
```

Stored as `List<GameRecordingProfile>` in `AppSettings`. Looked up by exe name when a game starts. Missing profile = use global defaults (auto-record off).

## Services Architecture

### SessionManager (new)

Core orchestrator for session lifecycle.

**Responsibilities:**
- Creates `GameSession` on `GameDetectedEvent`
- Closes session on `GameExitedEvent` (sets EndedAt, Status = Completed)
- Manages Desktop sessions (create daily, close at midnight)
- Detects orphaned sessions on startup (Status = InProgress with no running game) and marks as Crashed
- Provides `CurrentSessionId` for clip tagging
- Publishes `SessionStartedEvent` and `SessionEndedEvent`

**Thread safety:** Uses `ConcurrentDictionary<Guid, GameSession>` for active sessions.

### MatchTracker (new)

Listens to highlight detector events for match boundaries.

**Responsibilities:**
- Subscribes to `HighlightDetectedEvent` on the event bus
- When `HighlightType.MatchStarted` fires: creates a new `SessionMatch` in the current session
- When `HighlightType.MatchWin` or `HighlightType.MatchLoss` fires: closes the current match (sets EndedAt, Result, Score)
- Updates the active session via `SessionManager`

**Match boundary detection by game:**
- **Valorant:** `MatchPhase` transitions in ValorantGameState (explicit start/end)
- **CS2:** Infer from GSI round/score state changes (new `MatchStarted` highlight type needed)
- **R6 Siege:** Round start/end from log parsing
- **OW2:** Similar log-based inference
- **Unsupported games:** No match tracking, session is one block

### HighlightType Changes

Add to existing enum:
```csharp
MatchStarted,  // new -- fired when a match begins
```

Existing `MatchWin` and `MatchLoss` already cover match end.

### SessionRecordingOrchestrator (updated)

The existing `SessionRecordingHostedService` gains per-game profile awareness:

- On `GameDetectedEvent`: check `GameRecordingProfile` for the game's exe name
- If `AutoRecord = true`: start full session recording (current behavior)
- If `AutoRecord = false` or no profile: skip session recording (replay buffer only)
- Recording paths are added to the `GameSession.RecordingPaths` via `SessionManager`

### Clip Tagging

When a clip is saved (hotkey or auto-highlight):
- `ClipStorageManager.IndexClipAsync` receives the current `SessionId` from `SessionManager.CurrentSessionId`
- If a match is in progress, `MatchNumber` is also set from `MatchTracker.CurrentMatchNumber`
- Both fields written to `ClipMetadata` and persisted to `clips-index.json`

## Persistence

### Storage Abstraction

```csharp
public interface ISessionRepository
{
    Task<GameSession?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<GameSession>> GetByGameAsync(string gameName, CancellationToken ct = default);
    Task<IReadOnlyList<GameSession>> GetRecentAsync(int count, CancellationToken ct = default);
    Task SaveAsync(GameSession session, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
```

Initial implementation: `JsonSessionRepository` backed by `sessions-index.json`. Uses atomic write-to-temp-then-rename to prevent corruption on crash.

### File Structure

```
/Videos/WatchDog/
  sessions-index.json          // all GameSession records
  clips-index.json             // existing, gains SessionId + MatchNumber
  Counter-Strike 2/
    2026-04-09_1800/            // session folder (timestamped)
      session.mp4               // full recording (if auto-record on)
      session_part2.mp4         // segment (if > 30min)
      clip_001.mp4              // replay buffer clips
      clip_002.mp4
      .thumbnails/
    2026-04-08_2100/
      clip_001.mp4              // no session.mp4 (auto-record was off)
  Roblox/
    2026-04-09_2030/
      session.mp4               // single session recording
      clip_001.mp4
  Desktop/
    2026-04-09/                 // daily folder
      clip_001.mp4
      clip_002.mp4
```

New clips go into session folders. Existing clips stay where they are (no file moves during migration).

### Schema Versioning

Both index files gain a `SchemaVersion` field. On load, if version is missing or older than current, default new nullable fields to null and re-save once. This is idempotent.

### Index File Safety

Both `clips-index.json` and `sessions-index.json` use atomic writes: write to a temp file, then `File.Move` with overwrite. Prevents corruption from mid-write crashes.

## Migration

- Existing clips get `SessionId = null` and `MatchNumber = null`
- No files are moved or renamed
- Legacy clips appear in an "Unsorted" group in the session library view
- `ScanAndIndexAsync` updated to handle both old flat structure and new session-folder structure (walks two directory levels)
- Game name inference: check index first, fall back to parent directory name

## Events

### New Events

```csharp
public sealed record SessionStartedEvent(Guid SessionId, GameInfo Game);
public sealed record SessionEndedEvent(Guid SessionId, GameInfo Game, TimeSpan Duration);
public sealed record MatchStartedEvent(Guid SessionId, int MatchNumber, GameInfo Game);
public sealed record MatchEndedEvent(Guid SessionId, int MatchNumber, MatchResult Result, string? Score);
```

### Updated Events

`ClipSavedEvent` gains `SessionId` and `MatchNumber` fields (nullable).

## UI Changes

### MainWindow

- Library view switches from flat clip grid to session-grouped list
- Each session is a row: game icon + name + date + match count + clip count + duration
- Click a session to expand or navigate to session detail
- Game filter dropdown filters sessions by game
- "Unsorted" group at the bottom for legacy clips
- Grid/list view toggle still works within the session detail view for clips

### Session Detail View (new)

- Top: match cards (for supported games) or session summary
- Middle: video player with session recording (if auto-recorded) + timeline with highlight markers
- Bottom: clip grid/list for the session's clips
- Back button to return to session list

### Settings

- New "Game Profiles" tab listing detected games
- Per-game auto-record toggle
- Accessible from existing Settings sidebar

## Out of Scope

- Editing session recordings (trim, export) -- use existing clip editor on individual clips
- Cross-session analytics (win rate, K/D trends) -- future feature
- Cloud sync of sessions -- future feature
- Real-time match stats overlay -- separate feature
- Retroactive session grouping of legacy clips by timestamp proximity -- too fragile, skip
