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
