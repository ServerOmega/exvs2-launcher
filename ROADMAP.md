# Roadmap

Dates are not committed. Work stays in this private repo until the owner is ready to open it. Do not commit game files or third-party binaries at any milestone.

## Milestone 0 — Repo and design (current)

- [x] Private GitHub repository `ServerOmega/exvs2-launcher`
- [x] README, license placeholder, `.gitignore`
- [x] Clean-room SnowLauncher notes
- [x] Architecture proposal and stack recommendation
- [x] Owner picks a license (GPL-3.0-or-later, 2026-11-16)
- [x] Owner confirms stack: C# / .NET 8 + WPF; v1 = start order + unique serial/card

## v0.1.0 MVP (branch `mvp`, awaiting the owner's test)

- [x] `Exvs2Launcher.Core` + xUnit tests: line-preserving INI editor (comments, spacing, encoding, line endings),
      serial/card generator, backups, NIC auto-detect, profile store, start-order state machine with fake processes
- [x] WPF window: profiles, New serial, New card, Detect/Write IPs, Start / Cancel / Stop game / Stop all / Check port
- [x] Start order: patched card server → wait for 127.0.0.1:28080 → LM → client; never the stock Server.exe;
      reports which process holds the port
- [ ] Owner tests on a real cab (see docs/TESTING.md)

## v0.6.0 Feature batch (branch `feature/v0.6-batch` → `mvp`)

Plan agreed with the owner on 2026-11-16. A GPU / VRAM monitor was considered and **left out on purpose**.
Everything below stays read-only toward running programs: no CPU affinity or priority changes, no injected code,
and the stock `server\Server.exe` is never started.

1. **Start everything (multi-cab)**: one button starts every included cab profile in order. Card servers first
   (each distinct server once, wait for its port), then the LM, then the clients with a configurable stagger.
   Per-cab status lights for card server / LM / client, and Stop everything (clients → LM → servers).
2. **Health tab**: watches the card server, LM and client of every profile by folder-scoped PID, records starts,
   exits and crashes (exit code when known), tails the card server log for errors, failed battles and room ends
   (21-01-style drops when the log shows them), and saves the surrounding log lines (masked) per event.
3. **Diagnostics bundle**: one click builds a zip with launcher and card server logs, profiles and INI / JSON
   settings with secrets masked, OS / GPU / driver info, a summary of recent crash events (System 41 / 1001),
   and a top-level file listing of each cab folder (names, sizes, versions; no hashes). A preview lists every
   entry before saving. Executables, game assets and secrets are never included.
4. ~~GPU / VRAM monitor~~ (excluded by the owner).
5. **Card manager**: lists cards from the card server's web API (`/ui/card/getAll`, names + masked id hints) and
   shows play count / last played for each cab's own card (`/ui/card-stats`). Back up and restore each cab's
   `card.ini` (current file backed up first; refused while that cab is running). No database writes.
6. **Leaderboards and match history**: global Class solo / Class team / Mix boards through the card server
   (`/ui/leaderboard/board/{board}`, falls back to the matchmaker's public `/leaderboard/data`), the rank of the
   cab's card, and its recent battles (`/ui/battle-history`). Card keys are only ever sent to a local card server.
7. **Shareable profiles**: export a cab profile to `.json` without secrets (no tag secret, card values, IPs or
   window titles; serial optional) and import it through a review screen; an existing profile is never
   overwritten without confirmation.
8. **Launcher self-update**: checks ServerOmega/exvs2-launcher releases (pre-release toggle), downloads the exe,
   verifies SHA-256 (`.sha256` asset, GitHub digest or release notes), swaps it by rename and keeps the previous
   exe for one-click rollback. "Check on startup" only notifies; installing always needs a click.
9. **Discord Rich Presence** (off by default): minimal IPC client over Discord's local named pipe; shows
   EXVS2 IB in menus / in queue / in match as far as the launcher can tell, with a button to the Discord server.
   The Discord application id is configurable and blank by default (docs/RICH-PRESENCE.md).
10. **Queue / match notifications** (off by default): a sound and/or Windows notification when the selected
    matchmaker's `/status` shows players searching or a new match, or when the local card server log shows a
    match for one of this PC's cabs.

Tests cover masking, profile export / import, update hash checks, svd1 files staying untouched, start-everything
ordering with fakes, log classification, notification rules and the Rich Presence frame format.

## v0.5.1 Tabbed main window (branch `mvp`)

- [x] Main window settings grouped into Play, Card Server, Matchmaking, and Cab tabs; launch status and log stay visible
- [x] Last selected tab remembered in the profile file
- [ ] Owner tests on a real cab

## v0.5.0 Card server manager and matchmaking servers (branch `mvp`)

- [x] Install / update the card server from exvs2-matchmaking releases (SHA-256 verified, release build script,
      idle confirmation, backup + swap keeping config / DB / pictures, health check, automatic rollback)
- [x] Saved matchmaking server list with ping and player counts (`/status` on the matchmaker, TCP fallback)
- [ ] Owner tests on a real cab (docs/TESTING.md section 8); matchmaker `/status` deployed to LA / EU

## Milestone 1 — Core library, no game start required

- Profile store (named cabs, game folder path, card server path)
- Read/write `config.ini`, `lmnfig.ini`, `card.ini` with tests
- Unique serial generator and new-card generator, plus backup of replaced files
- NIC / IPv4 auto-detect
- `server.json` helpers for listen address, MatchUrl, and tag secret (secret not logged)
- Optional reader that imports settings from an existing game folder (and, if easy, from a `SnowLauncher.ini` the user points at)

Deliverable: unit tests and a small CLI or smoke tool that can print and rewrite a fixture folder.

## Milestone 2 — Process supervisor (MVP launcher)

- WPF (or chosen UI) shell: pick a profile, edit identity / IPs / server URL, Save
- Start order: patched card server → wait for port → LM → client
- Stop / kill processes the launcher started
- Refuse to launch stock `Server.exe` when a patched server is configured
- Basic log tail for the card server
- Manual test on a real Infinite Boost folder (user present; do not start other people's cabs mid-match)

This is the first build that can replace SnowLauncher for local + VPS matchmaking use.

## Milestone 3 — Cab quality-of-life

- [x] Wizard sets `battleMode` to 0 in `29\testmode_pcb.json` (v0.3.1, backup first, svd1-safe rewrite only); a full test-mode editor is still later
- [x] Fightstick / controller binding UI (v0.3.0; simplified selector + rebind and HID product names in v0.4.0); keyboard editor for client and LM (v0.4.2)
- [x] Resolution / windowed / borderless / skip-opening / framerate (v0.4.0; client and LM kept separate)
- "New card" and "new serial" buttons in the UI
- Redaction of secrets in logs
- Warn when two profiles share a serial

## Milestone 4 — Multi-cab and LM isolation

- Several profiles running from one UI (Test, P2–P4, and so on)
- Detection of port conflicts and already-running servers
- LM isolation helpers (bind IP clarity, optional documented firewall rule, undo)
- Better handling of card servers that live outside the game folder (the current Test layout)

## Milestone 5 — Later / optional

- Virtual LAN helpers (detect Radmin / TAP / n2n, do not vendor those tools)
- Optional handoff to a user-provided mod manager
- Friend-request / online-tag status once exvs2-matchmaking exposes it
- Installer / auto-update
- [x] Public release of the repo (2026-11-16, after a secrets/binaries pass)

## Out of scope unless revisited

- Hook DLL development
- A full n2n implementation
- TeknoParrot integration
- Redistributing any official or pack files
