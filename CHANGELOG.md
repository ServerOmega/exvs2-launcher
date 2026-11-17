# Changelog

## v0.6.0 — 2026-11-16

Feature batch (planned in ROADMAP.md). A GPU / VRAM monitor was left out on purpose.

### Added
- **Start everything / Stop everything** and an **All cabs** tab: starts every included cab profile in order —
  each distinct card server once (waits for its port), then the LMs, then the clients with a configurable delay and
  stagger. Per-cab lights for card server / LM / client, Cancel, and a per-profile "include" tick. A failed card
  server skips the LM and client of the cabs that use it. CPU affinity / priority are never changed and the stock
  `server\Server.exe` is never started.
- **Health** tab: watches every profile's card server, LM and client by folder-scoped process (read-only), records
  starts, exits and unexpected exits, tails card server logs for errors, failed battles, room ends, network
  warnings and match lines, and saves masked events with surrounding log lines to `health-events`.
- **Diagnostics…**: a zip with logs, settings and system info, secrets masked, with a preview of every entry before
  saving. See docs/DIAGNOSTICS.md.
- **Cards** tab: card list from the card server API (names, masked id hints), this cab's card name / battles /
  last played, and `card.ini` backup / restore (refused while the cab runs; the current file is backed up first).
- **Stats** tab: Class solo / Class team / Mix leaderboards (card server, falling back to the selected matchmaker)
  and this cab's recent matches.
- **Export / Import** profiles as JSON without secrets, IPs or window titles, through a review screen. An existing
  profile is replaced only after confirmation.
- **Self-update** (Settings tab): checks GitHub releases (pre-release toggle), SHA-256-verified download, swap by
  rename, previous exe kept for **Roll back**. The startup check only shows a notice; installing needs a click.
- **Discord Rich Presence** (off by default, blank application id). See docs/RICH-PRESENCE.md.
- **Notifications** (off by default): sound and/or tray pop-up when the selected matchmaker shows players searching
  or a new match, or the local card server log shows a match.

### Changed
- `exvs2-launcher.profiles.json` gains a `Settings` block and a per-profile `IncludeInStartEverything` flag. Older
  files load with defaults.
- The single-profile Start all now writes network settings through the same `Prepare` step that Start everything
  uses (no behavior change).

## v0.5.1
- Tabbed main window.

## v0.5.0
- Card server manager and matchmaking server list with ping / player counts.
