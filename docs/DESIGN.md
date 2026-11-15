# Design

This is a proposed architecture for a clean-room EXVS2 Infinite Boost launcher that is compatible with [exvs2-matchmaking](https://github.com/ServerOmega/exvs2-matchmaking). Nothing here is locked until the owner picks a stack, a license, and an MVP scope. See also [SNOWLAUNCHER-NOTES.md](SNOWLAUNCHER-NOTES.md) and [../ROADMAP.md](../ROADMAP.md).

## Goals

- One desktop app that can manage several cabinet folders (main cab, Test cab, P2–P4, a friend's pack, and so on).
- Write the game's own config files correctly and start processes in the order a self-hosted matchmaker needs.
- Be obvious about what it will not do: no game data, no hook DLLs, no SnowLauncher code, no affiliation with Bandai Namco or other launchers.
- Keep secrets (card chip/access codes, tag secret, boot tokens) out of logs and out of git.

## Non-goals (v1)

- Reimplementing `bngrw.dll` / socket hooks / JVS / AM auth.
- Bundling `vsac29_Release.exe`, stock `Server.exe`, TAP drivers, or any pack files.
- Becoming a generic TeknoParrot-style front-end for other arcade games.
- Shipping a built-in n2n supernode. Virtual LAN can be a later helper that shells out to a tool the user already has.
- Online matchmaking itself — that stays in exvs2-matchmaking.

## Stack options

The game and the card server are Windows programs. The launcher can assume Windows 10/11 x64.

### Option A — C# / .NET 8 + WPF (recommended)

- Same language and runtime family as the exvs2-matchmaking card server (`Server-Infinite`). Shared knowledge, and later we can reference server config models if we want.
- Excellent Windows process control, job objects, NIC enumeration (`NetworkInterface`), TCP wait-for-port, and INI/JSON editing.
- WPF (or WinUI 3) gives a solid native UI for profiles, forms, and a log pane without fighting the OS.
- Shipping: single-folder or single-file `dotnet publish -r win-x64 --self-contained`.
- Downsides: Windows-only UI; looks dated if we stay on default WPF styling (Fluent/Win11 themes mitigate this).

### Option B — C# / .NET 8 + Avalonia

- Same backend advantages as option A, with a more modern, themeable UI.
- Could in theory run the *settings editor* on another OS, but the actual game start path is still Windows-only, so the portability win is small.
- Extra dependency and a slightly less "batteries included" story for some Windows-only APIs.

### Option C — C# / .NET 8 + WinForms

- Fastest possible MVP. Fine for a utility.
- Weakest long-term UI. Only worth it if we want a first clickable build in a day and plan to replace the shell.

### Option D — Rust + Tauri (or egui)

- Small, fast, easy to reason about at the process-isolation level.
- Weaker fit: the card server is already .NET, Windows service/job control and NIC quirks are more work, and we would maintain two languages for one hobby stack.
- Consider only if there is a strong desire to avoid .NET on the desktop.

**Recommendation:** Option A, C# / .NET 8 + WPF, with a Fluent-ish theme. Revisit Avalonia if the UI starts to feel boxed in. Keep process and profile logic in a net8.0 class library so the UI is replaceable.

## Proposed layout

```
exvs2-launcher/
  docs/
  src/
    Exvs2Launcher.Core/     # profiles, ini/json IO, process supervisor, serial/card gen
    Exvs2Launcher.App/      # WPF shell
  tests/
    Exvs2Launcher.Core.Tests/
```

No game files, ever. A profile stores a *path* to a game folder the user already has.

## Profile model

A profile is a named cabinet:

- Display name (for example "Test cab", "P2")
- Path to the game folder (must contain or be able to locate `vsac29_Release.exe`, `config.ini`, and optionally the LM exe)
- Path to the card server to use (patched `Server-Infinite.exe` plus its `server.json`, which may live outside the game folder)
- Roles to start: card server / LM / client (any combination)
- Network: PCB IP, LM IP, NIC names, optional "auto-detect on start"
- Identity: 4-character serial (auto-generate if empty), card access code + chip id (auto-generate if empty)
- Display: resolution, windowed, borderless, skip opening, framerate limit
- Input bindings (keyboard + up to N controllers), stored in our profile and written out to INI at start
- Card server: listen address (default `127.0.0.1:28080`), MatchUrl, tag secret path/value, other `server.json` keys we already document in the matchmaking repo
- LM isolation notes / optional firewall rule hooks
- Last log paths

On start, the app writes the game INI files from the profile (it does not leave a GUI process sitting on an in-memory copy that fights hand edits). After the processes exit, the user can still edit INI files; the next launch can either "reload from disk" or "overwrite from profile", chosen per profile.

### INI files we write

| File | Role |
| --- | --- |
| `config.ini` | Client mode, serial, Server, IP, window, keyboard/controller, display |
| `lmnfig.ini` | LM mode, Server, IP, window, keyboard, display |
| `card.ini` | `accessCode`, `chipId` |
| `29\testmode_pcb.json` | battleMode (and later other test-mode fields), only when the user asks |
| card server `server.json` | MatchUrl, tag secret, listen port — only the keys we own, with a backup |

We may optionally *read* a `SnowLauncher.ini` to import a profile. We will not write one unless there is a clear migration need, and we will not try to be byte-compatible with SnowLauncher.

## Process supervisor and start order

Correct order for a self-hosted matchmaking cab:

1. If this profile uses a patched card server, start it (or detect that one we own is already listening on the configured port). **Do not** start stock `server\Server.exe` in that case.
2. Wait until the listen port accepts a TCP connection, with a timeout and a log tail if it fails.
3. If LM is enabled, start `vsac29_Release_LM.exe` in the game folder, after `lmnfig.ini` is written.
4. If client is enabled, start `vsac29_Release.exe` after `config.ini` and `card.ini` are written.
5. Offer Stop / Kill (the existing F4 kill binding remains a game-side feature; the launcher should also be able to close the processes it started).

The supervisor should track PIDs, show stdout/log files, and avoid double-starting. If something else already bound 28080 and it is not our server, refuse and explain.

## Feature list

### Per-cab profiles

Multiple profiles, stored in an app data folder (not inside the game dump). Import from an existing game folder by reading `config.ini` / `card.ini` / `lmnfig.ini`.

### Auto unique serial and new card

- Serial: four random characters from a safe alphabet, checked against other local profiles so two cabs on the same PC are not identical. Write to `config.ini` `serial =`.
- Card: 20-digit access code (document the prefix convention we already use, for example a `99` prefix on some cabs) and 32 uppercase hex chip id. Write both to `card.ini`. Never log the raw values at default verbosity.
- A "new card for this profile" button, with a backup of the previous `card.ini`.

### Auto-detect PCB / LM IP

Enumerate up IPv4 interfaces, hide obvious virtual ones unless the user selects them (Radmin, TAP, n2n can be offered in an "include virtual NICs" list). Let PCB and LM use the same or different addresses. Offer a refresh on each start when the auto flag is on.

### battleMode editor

Read `<game>\29\testmode_pcb.json`. The game selects a `group[N]` of the active `settingFile` by time slot. For each relevant group, set `battleMode` `"value": 0` (leave `"type": 1`) when the user wants the mode we use for free matchmaking tests — or expose the value as a field. Backup the JSON before writing.

### MatchUrl and tag secret

Edit the *card server* configuration, not the game INI:

- MatchUrl should point at the VPS matchmaker (port 28080 in the current deployment).
- Tag secret: show where the VPS host finds it, and where it is pasted on each card server, without ever committing the value. Prefer "read from a local secret file" over putting the secret in the profile JSON.

This complements the setup guide in the matchmaking repo.

### LM isolation

Because LM discovery is a LAN broadcast, cabs in the same house can join the wrong LM. The launcher should:

- Show the LM bind IP clearly.
- Warn when two local profiles share an LM IP / subnet in a conflicting way.
- Optionally help create a Windows Firewall rule that blocks broadcast/discovery toward a named neighbor IP (for example a housemate's cab), documented as an advanced, reversible action. Do not flip firewall rules without an explicit click and a way to undo them.

### Logs viewer

Tail the card server log directory and the launcher's own log. Filter by level. Do not show chip ids / access codes / tag secrets (redact known keys).

### Input and video

A straightforward bindings screen and resolution/window options, writing the observed `config.ini` / `lmnfig.ini` keys. No new input driver.

## Security and repo hygiene

- `.gitignore` already excludes executables, DLLs, INI files, databases, and secrets.
- Profiles live under `%LocalAppData%\exvs2-launcher\` (or similar), with secrets marked and files ACL'd to the user when practical.
- The app should refuse to copy itself into a game folder as a "pack overlay" that might get redistributed with dumps.

## Open decisions

See the questions at the end of the README / in the first private-repo issue list: license, WPF vs Avalonia, whether v1 includes a fightstick mapper and battleMode editor or only start-order + identity + server config, and whether we import SnowLauncher.ini in v1.
