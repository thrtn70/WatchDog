# WatchDog

## Mandatory Pre-Push Audit

**NEVER push code without completing BOTH audits. No exceptions.**

Before every `git push`, you MUST run:
1. **code-reviewer agent** — review all changed files for bugs, logic errors, security issues, and quality
2. **Codex CLI** — `codex exec review --uncommitted --full-auto` as a second independent auditor

If either audit finds P0/CRITICAL issues, fix them before pushing. This applies to ALL code changes including frontend/XAML, backend, tests, and config files.

If Codex CLI is unavailable, explicitly tell the user and do NOT push until the second audit is resolved.

## Design Context

### Users
PC gamers across the spectrum — competitive ranked players who need reliable auto-clipping during intense sessions, casual players saving funny moments for Discord, and content creators building montages. They use WatchDog during or immediately after gaming sessions on Windows desktops, often in dimly lit rooms, often alt-tabbing between a fullscreen game and the app. They value speed, reliability, and low resource usage above all. The app lives in the system tray and should feel like a trusted background tool that surfaces exactly when needed.

### Brand Personality
**Precise, capable, understated.** WatchDog is a watchful tool — it operates silently, captures what matters, and presents it cleanly. Think of a well-engineered instrument panel: dense with useful information, zero decoration for its own sake, every element earns its space. It respects the user's time and attention.

Three words: **sharp, efficient, confident.**

### Aesthetic Direction
- **Theme:** Dark mode, custom WatchDog-specific palette (replacing Catppuccin Mocha). The palette should feel like a purpose-built tool — not a community color scheme applied generically.
- **Tone:** Tactical utility meets refined craftsmanship. Dense information display without feeling cluttered. Clean lines and clear hierarchy without being sterile or empty.
- **Anti-references:** Should NOT feel like Overwolf/Outplayed (bloated, Electron-y, ad-laden). Should NOT feel like a generic "gamer" aesthetic (neon, RGB, aggressive angles). Should NOT feel like a generic dark admin dashboard.
- **Font direction:** The current Segoe UI is functional but generic. A typeface with more character — something that reads well at small sizes in dense UI but has a distinct personality — would differentiate WatchDog.
- **Color direction:** Build a custom dark palette with a distinct identity. Warm or cool neutrals (not generic gray), a signature accent color that becomes synonymous with WatchDog, and semantic colors for states (recording, idle, error, success).

### Design Principles
1. **Information density over whitespace** — Gamers want to see their clips, stats, and status at a glance. Don't waste space on decorative padding. Pack useful information tightly but legibly.
2. **Invisible until needed** — The app should feel like it disappears when you're gaming and appears instantly when you want it. Transitions should be fast, not fancy. No loading spinners where instant feedback is possible.
3. **Every pixel earns its place** — No decorative elements that don't convey information. Icons, colors, and spacing all communicate state or hierarchy. If an element doesn't help the user act faster, remove it.
4. **Confidence through consistency** — Uniform spacing rhythms, predictable interaction patterns, and a cohesive color language build trust. The app should feel like one person designed every screen.
5. **Tool-grade, not toy-grade** — WatchDog competes with ShadowPlay and Medal, not with casual mobile apps. The UI should communicate competence and reliability — the kind of app a serious player trusts to never miss a clip.
