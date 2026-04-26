# Headless VM Workflow

Drive the WatchDog Windows VM from the Mac terminal **without stealing window focus or moving the cursor**. Three control layers stack from cheapest to most invasive — pick the right one for each operation.

## Layer 1 — SSH (cheapest, no GUI)

Already configured in `~/.ssh/config` as `Host win11`.

| Use for | Command |
|---|---|
| Build | `./scripts/build.sh debug` |
| Test | `./scripts/build.sh test` |
| Arbitrary command | `ssh win11 '<powershell-command>'` |
| Interactive shell | `./scripts/build.sh shell` |

**Limitation**: WPF apps launched via SSH run in a non-interactive Windows session and do **not** render on the user's interactive desktop. SSH is correct for builds / tests / scripts; do not use it to launch the visual WatchDog UI.

## Layer 2 — Parallels CLI (`prlctl`)

Lifecycle without leaving the terminal.

| Use for | Command |
|---|---|
| List VMs | `prlctl list --all` |
| Snapshot before risky test | `prlctl snapshot "Windows 11" -n "label"` |
| Roll back | `prlctl snapshot-switch "Windows 11" --id <uuid>` |
| Start / stop | `prlctl start "Windows 11"` / `prlctl stop "Windows 11"` |
| Run VM-side command | `prlctl exec "Windows 11" cmd /c "echo hi"` |

`prlctl exec` is an alternative to SSH that runs as `Local System` and bypasses the SSH session-isolation issue, but the agent has not been broadly tested for GUI launches.

## Layer 3 — Cua Driver (headless GUI, no focus steal)

The MCP tools at `mcp__cua-driver__*` deliver events to a specific pid via SkyLight's `SLEventPostToPid`. The target pid does **not** need to be frontmost — your physical cursor never moves and your active app keeps focus.

### Discovery

```
list_apps                              → pid of "Parallels Desktop"
list_windows({pid})                    → window_id of the VM display
                                         (also surfaces Coherence Win-app windows)
```

### Capture — never disrupts focus

```
screenshot({window_id})                → PNG of one window, even backgrounded / off-Space
get_window_state({pid, window_id})     → AX tree + screenshot, with [element_index N] tags
```

### Drive — pid-targeted, no cursor move

```
click({pid, x, y, window_id})          → pixel-mode click delivered to pid
press_key({pid, key: "return"})        → single key via SLEventPostToPid
hotkey({pid, keys: ["cmd","v"]})       → modifier combo
type_text({pid, text})                 → only on macOS-native text fields
```

> ⚠️ **Parallels VM input gotcha**: `click` / `press_key` / `hotkey` posted
> to the Parallels Desktop pid (84408) deliver to the macOS process, but
> Parallels' virtualization driver does NOT pick them up — they never reach
> the Windows OS inside. Verified: `press_key(pid=parallels, key="return")`
> on a focused PowerShell prompt did not advance the prompt.
>
> **What DOES work** for Parallels' own macOS-level UI: any keyboard
> shortcut that Parallels handles itself, not the VM. Verified working:
> `hotkey({pid: parallels, keys: ["ctrl","cmd","return"]})` switched the
> VM into Coherence view. Parallels' menu shortcut handler accepts CGEvent
> posts; only the VM input pipeline rejects them.
>
> **For input INTO the VM (Windows OS)**, the only reliable paths are:
> 1. SSH (Layer 1) — for command execution. ⚠️ But SSH-launched processes
>    run in a non-interactive Windows session and cannot spawn GUI apps
>    (verified: `Start-Process notepad` returned "Access is denied").
>    `prlctl exec` has the same session-isolation issue.
> 2. Standard `mcp__computer-use__*` — works but DOES steal focus.

## Coherence mode — verified

Tested 2026-04-25. Setup:

```bash
# Persist as default (optional — affects future VM starts)
prlctl set "Windows 11" --startup-view coherence

# Toggle a running VM into Coherence (no focus steal — Parallels'
# OWN macOS shortcut handler accepts CGEvent posts, even though
# the VM input driver doesn't)
mcp__cua-driver__hotkey({pid: 84408, keys: ["ctrl","cmd","return"]})

# Toggle back
mcp__cua-driver__hotkey({pid: 84408, keys: ["ctrl","cmd","return"]})
```

What works in Coherence:
- The Parallels VM display window disappears entirely — your screen real
  estate is freed, no big VM rectangle in the way
- Windows apps that **already had visible windows** before the switch may
  surface as separate macOS-level processes with bundle IDs like
  `com.parallels.winapp.<hash>.<vm-uuid>` — those CAN be screenshot,
  AX-inspected, and (in theory) input-driven via Cua Driver
- `mcp__cua-driver__launch_app({bundle_id: "com.parallels.winapp..."})`
  brings a Coherence-exposed app to running state

What does NOT work, even in Coherence:
- Spawning a fresh GUI Windows app from Mac-side tools is still blocked
  by Windows session isolation. SSH and `prlctl exec` both run in a
  non-interactive Windows session that cannot push windows to the user's
  desktop. Verified.
- The Coherence helper windows that surface (e.g. `Terminal (pid 21424)`
  with four 1512×33 stub windows at y=0) are top-of-screen menubar
  accessories, not real content windows. Until a Windows app actively
  creates a content window IN the user's interactive session, Coherence
  has nothing real to expose.

**Practical verdict**: Coherence is useful for *visual decluttering*
(no big VM window dominating the screen) and for *driving Parallels'
own macOS UI* (view modes, menus). It does not bridge the
session-isolation gap that prevents Mac-initiated GUI app launches.
For interactive WatchDog testing, the user must still launch the app
**from inside** the VM (via the visible PowerShell, manually) and only
then can Cua Driver see/screenshot the result.

### Quality-of-life knobs

```
set_agent_cursor_enabled({enabled:false})  → hide visual cursor overlay (default: on)
check_permissions({prompt:true})           → re-prompt for AX/Screen-Recording
```

## Practical patterns for WatchDog

### Verify VM state without leaving Cursor / Claude / wherever you are
```
list_windows(pid_parallels)            → find "Parallels Desktop" / "Installation Assistant"
screenshot(window_id)                  → see VM, no focus change
```

### Type a command into the visible PowerShell inside the VM
1. `click({pid: parallels_pid, x, y, window_id})` — pixel coords inside the VM screenshot, click on the terminal area
2. `pbcopy <<< "dotnet run --project src\WatchDog.App"` — set Mac clipboard
3. `hotkey({pid: parallels_pid, keys: ["cmd","v"]})` — Parallels translates Cmd+V → Windows Ctrl+V → paste
4. `press_key({pid: parallels_pid, key: "return"})` — execute

### Click a button in the running WatchDog window
WatchDog renders inside the Parallels window as a bitmap, so AX is unavailable.
1. `screenshot({window_id})` — capture
2. Read the screenshot, identify the target button's pixel coordinates
3. `click({pid: parallels_pid, x, y, window_id})` — fires inside the VM, no Mac focus change

### Test a global hotkey
1. `hotkey({pid: parallels_pid, keys: ["ctrl","shift","f11"]})` — Parallels forwards to Windows
2. `screenshot({window_id})` to verify the hotkey fired (e.g. clip-saved toast)

## Layer comparison (verified)

| Operation | SSH | prlctl | Cua Driver |
|---|---|---|---|
| Build / run script | ✅ best | ✅ alt | ❌ wrong tool |
| Take VM snapshot | ❌ | ✅ best | ❌ |
| Launch WPF app (visible) | ⚠️ runs in non-interactive session | ⚠️ untested | ❌ no input path into VM |
| Capture VM screen | ❌ | ❌ | ✅ best — no focus steal |
| Click button inside VM | ❌ | ❌ | ❌ — events don't propagate through Parallels |
| Click button in Coherence-mode app | ❌ | ❌ | ✅ — direct macOS pid |
| Verify visual rendering | ❌ | ❌ | ✅ |
| Steals focus? | n/a | n/a | **no, for screenshots** |
| Moves cursor? | n/a | n/a | **no** |

**The honest take**: for the WatchDog dev loop, Cua Driver's *observation* layer
(screenshots, window listing) is a major win — verify rendering and state
without ever interrupting your work. For *interactive GUI driving* of the VM,
fall back to `mcp__computer-use__*` (focus steal but reliable) or use Coherence
mode.

## What about Lume?

Lume manages **macOS / Linux** VMs natively via Apple Virtualization Framework. It does **not** apply to the Parallels Windows VM. Keep it installed for:

- Disposable Linux sandboxes for builds / package tests
- macOS-on-macOS test environments (e.g. testing the future macOS port of related tooling)
- CI-style runs without polluting the host

Currently no Lume VMs are configured (`lume_list_vms` returns `[]`). When the time comes:

```
mcp__lume__lume_create_vm(...)         → spin up a fresh test env
mcp__lume__lume_run_vm({name})         → start it
mcp__lume__lume_exec({name, cmd})      → run commands
mcp__lume__lume_stop_vm({name})        → tear down
mcp__lume__lume_delete_vm({name})      → clean up
```

## Permissions

Cua Driver needs both **Accessibility** and **Screen Recording** TCC grants. Verify with:

```
mcp__cua-driver__check_permissions
```

If either is missing, pass `{prompt: true}` to raise the system dialogs.
