# exvs2-launcher

A clean-room launcher for **Mobile Suit Gundam: Extreme Vs. 2 Infinite Boost** (Namco System BNA1), designed to work with the [exvs2-matchmaking](https://github.com/ServerOmega/exvs2-matchmaking) patched card server and VPS matchmaker.

This project is **not affiliated with**, endorsed by, or derived from Bandai Namco, SnowLauncher, TeknoParrot, or EXVS2-POC. It does not include game data, hook DLLs, or any third-party launcher binaries. You must supply your own legally obtained game installation.

Licensed under the GNU General Public License v3.0 (see `LICENSE`).

## Why this exists

The common pack launcher, SnowLauncher, is useful but closed source. It also has behaviors that fight a self-hosted matchmaking setup (it starts the stock card server on `127.0.0.1:28080`, holds its INI in memory, and does not help with unique cabinet serials, fresh cards, battleMode, or the matchmaker tag secret).

This repo is a from-scratch replacement for the *launcher / orchestrator* layer: profiles, config writing, and a correct start order. It will not reimplement game hooks. Existing hook DLLs already in a user's game folder stay as they are.

## Status

Private preview, **v0.6.0 (branch `mvp`)**: C# / .NET 8 + WPF. What it does today:

- **v0.6.0: run several cabs from one place.** **Start everything** starts every included cab profile in order
  (each card server once and waits for its port, then the LMs, then the clients with a configurable stagger) with
  per-cab lights, and **Stop everything** closes them. A **Health** tab watches every cab's processes and card server
  log and saves problem events; **Diagnostics…** builds a masked zip with a preview
  ([docs/DIAGNOSTICS.md](docs/DIAGNOSTICS.md)); **Cards** and **Stats** tabs show the card server's cards, this
  cab's stats and `card.ini` backup / restore, global leaderboards and recent matches; profiles can be **exported /
  imported** without secrets; the launcher can **update itself** from GitHub releases (SHA-256 verified, rollback,
  never automatic); optional **Discord Rich Presence** ([docs/RICH-PRESENCE.md](docs/RICH-PRESENCE.md)) and
  **queue / match notifications**, both off by default. See [CHANGELOG.md](CHANGELOG.md).

- **Correct start order.** Starts the patched card server you pick (`Server-Infinite.exe` or `3-Start-Server.bat`),
  waits until `127.0.0.1:28080` accepts connections, then starts the LM (`vsac29_Release_LM.exe`, optional) and the
  client (`vsac29_Release.exe`). It never starts the pack's stock `server\Server.exe`. If the port is already held it
  says by which program: the patched server is reused, the stock server or anything else is refused. After Start all, stopping or closing the LM and client no longer leaves the launcher stuck: Start on LM or Client, and Start all, work again.
- **Tabbed main window.** Settings are grouped into Play, Card Server, Matchmaking, and Cab tabs so the window is
  less cluttered. The cab profile bar, Start all / Cancel / Stop all / Check port, and the card server / LM / client
  running status stay at the top, and the log stays at the bottom. **Card server...**, **Open web page**,
  **Controllers...**, and **Keyboard...** live on the tab for that feature (same buttons as before). The last selected
  tab is remembered in `exvs2-launcher.profiles.json`.
- **Cab name in the title and log.** Each profile has a cab name (for example "Cab 1") stored in the launcher profile. It appears in the window title and in start/stop log lines, so you do not have to tell the game's own `PCB_xx` window titles apart unaided. The identity section also shows the game-folder name and, once the client is running, the current game window title. The launcher cannot rename the game window.
- **Graphics and resolution.** The Cab tab edits the observed display keys in `config.ini` and `lmnfig.ini` separately: `[display] Resolution` (`144p`, `240p`, `480p`, `720p`, `1080p`, `2k`, `4k`, `8k`, plus any token already in the file), `FramerateLimit`, and `[config] Windowed`, `BorderlessWindow`, `OpeningScreenSkip` (`None`, `SkipReminder`, `SkipBrand`, `SkipAll`). A typical 4-cab layout keeps the LM at 144p and each client at 480p. Files are backed up before they change. SnowLauncher.ini is updated only for `FramerateLimit` and the observed `OpeningScreenSkip=SkipAll` → `3` pairing; its `PCB_Resolution` / `LM_Resolution` indexes are left alone because that table is incomplete. Close the game and LM so they reread the values.
- **Controller mapper.** A bindings window with a controller selector and a rebind list (one row per game action, Rebind captures the next button press, plus Clear). Combo keys, raw `Name` / `Mode` / `DeviceId`, and `[xinput]` settings are under Advanced. Connected pads and fight sticks are listed through HID / SetupAPI product strings (and bus-reported / friendly names), Raw Input, XInput slots 0–3, and the WinMM joystick API. The UI can show a short product name such as `Razer Panthera` while still writing the game-compatible `Name=` value (observed examples: `Razer Panthera PS4`, `Controller (Xbox One For Windows)`). Rebind captures the next press through Raw Input (HID button usages mapped to the game's 1-based ids), then WinMM, then XInput, and a Last pressed line shows the button number as you press it. This is not a new input driver. Unknown keys such as arcade bitmasks are left alone. Files are backed up before they change, and SnowLauncher.ini is updated when that option is on.
- **Keyboard bindings.** A separate window (Keyboard...) with Client and LM sections. Each action shows its current hook key name, with Rebind (captures the next key press, 8 second timeout) and Reset to the Infinite Boost pack default. The client is written to `config.ini` `[keyboard]` and the LM to `lmnfig.ini` `[keyboard]` — those are the files `bngrw.dll` / `lmgrw.dll` read. Combo keys are stored the way SnowLauncher does (`ButtonAB` and so on) and expanded into the comma-separated `A=`/`B=`/`C=` lists the hook actually reads (for example `A=J,U,Y,T`). Enter is written as `Return`, the name observed in the pack; `Enter` is accepted when reading. Only names in the hook's key table can be written (letters, digits, arrows, function keys, Enter, Esc, Space, NumPad, and the rest of that list). Shift, Ctrl, Alt, and punctuation are not in that table. Leftover unknown keys such as `ButtonR1` are left alone. Files are backed up before they change, and SnowLauncher.ini is updated when that option is on.
- **Card server manager (Card Server tab → Card server...).** Installs, updates, starts and stops a card server from the
  [exvs2-matchmaking releases](https://github.com/ServerOmega/exvs2-matchmaking/releases) (pre-releases optional).
  Releases hold build scripts and a patch, not a compiled server (the original card server has no license, so its
  program can't be handed out), so the launcher downloads the release zip, checks its SHA-256 (the `.sha256` asset and
  GitHub's digest must agree) and runs the release's own build script in a PowerShell window. A ready-made Windows zip
  (`*win-x64*.zip`) would be used directly if a release ever has one. **Update** needs you to tick "all cabs are idle";
  it builds first while the old server keeps running, then stops it, moves the old `server` folder to
  `exvs2-launcher-work\backups`, swaps in the new build keeping `Configurations\` (server.json, kestrel.json),
  `ServerInfinite.db` (+ `-wal` / `-shm`), `wwwroot\images` and other data files (leaderboard / friends / node-id
  state), restarts it and waits until it listens on its port. If it doesn't start or listen, the old folder is put back
  and restarted automatically. Details: [docs/CARD-SERVER-MANAGER.md](docs/CARD-SERVER-MANAGER.md).
- **Matchmaking server dropdown.** On the Matchmaking tab, a saved list of matchmaking servers (name, host, port; add / edit / remove in
  **Edit list...**, stored in the profile file). You can add **any** matchmaking server, including ones run by other
  people or communities. LA and EU are only empty-host starter placeholders for servers you may already use (ask in the
  Discord for those addresses; this repo does not carry them). Each entry shows its ping and how many cabinets are
  online, searching and in a match, refreshed every 15
  seconds (HTTP `GET /status` on the matchmaker, falling back to a TCP connect; player counts need a matchmaker with
  the `status-endpoint` patch, older ones show the ping only). **Use for this card server** writes the server into
  `Configurations\server.json` (`CardServerConfig.OnlineMatching.MatchUrl` and `StunHost`) after backing it up.
  Each server has its own tag secret, given by that operator; set it on the Card Server tab when you switch, so the
  previous secret is not left paired with the new URL. Switching does not move card data. Do it with the cabs idle.
- **Server settings and Open web page.** On the Card Server tab, the card-server host/port, `Server=` in `config.ini` / `lmnfig.ini`, LM `DisableSocketHook`, and the card server's `Configurations/server.json` MatchUrl live in one section. A remote MatchUrl defaults to port **28090** (the public / VPS matchmaker); the local card server and its web page stay on **28080**. The launcher warns if a non-local MatchUrl uses 28080. The online tag secret can be written there but is never shown or logged (leave the box empty to keep an existing secret). **Open web page** launches the card server WebUI (`http://{host}:{port}/`, or localhost when the server binds `0.0.0.0`) in the default browser.
- **Unique serial and new card.** A new profile (and the first-run wizard) gets a random unique 4-character serial
  automatically, so you don't have to click a button. If the game folder already has a unique serial in `config.ini`,
  the profile adopts it and the folder is not changed. A new serial is written only when the folder has no serial, or
  when that serial is already used by another profile (logged). "New serial" is the only way to replace a unique
  existing serial. "New card" writes a new 20-digit access code and 32-hex chip id (cryptographic RNG) to `card.ini`.
  Existing cards are accepted if the access code is 20 digits or 20 hex characters and the chip id is 32 uppercase hex;
  key names `accessCode`/`AccessCode` and `chipId`/`ChipId` are treated the same, and the original spelling is kept
  when writing. Both can also update `SnowLauncher.ini` so SnowLauncher doesn't put the old values back. Every file is
  backed up as `<name>.bak-yyyyMMdd-HHmmss` before it changes. Card values are never shown or logged.
- **Independent PCB and LM IPs.** Auto-detect and "Detect now" fill only the PCB (client) IP, written to `config.ini`
  `IpAddress`. The LM IP comes from `lmnfig.ini` `IpAddress`, which is authoritative: auto-detect never overwrites it.
  The LM IP dropdown lists every local IPv4 address, including secondary addresses on the same adapter. "Write IPs" or
  an explicit LM IP (with auto-detect off) can change it, and the log shows a before/after diff. If SnowLauncher.ini is
  synced, `LM_IpAddress` is updated to match lmnfig, not the PCB IP. Sharing one address is fine for a single cab; a
  separate LM address is recommended when more than one cab or LM runs on the same PC.
  `127.0.0.1`, `0.0.0.0`, link-local `169.254.x`, and addresses not on a local adapter are rejected. Virtual adapters (Radmin,
  TAP/n2n, Hyper-V...) are listed but never auto-picked over a real one.
- **Card server version.** The path's folder name (for example `exvs2-matchmaking-v0.4.1-pre`) or a `BUILD_COMMIT`
  file is shown in the UI and the start log. A warning appears if it looks like a pre-release older than v0.4.0.
- **Per-component Start and Stop.** Each of the card server, LM, and client has its own Start and Stop, plus a
  Running/Stopped status. Stop tracks the PID this launcher started, then falls back to the exe in this profile's
  folder — never a process from another cab folder. **Stop all** still closes all three for this profile.
- **Hover tooltips.** Every setting and button has a short-delay tooltip in beginner-friendly English (text lives in
  `src/Exvs2Launcher.App/Resources/Tooltips.xaml` so it can be translated later).
- **High-DPI tooltips and notes wrap.** Tooltips wrap at 420 DIP, stay open for 30 seconds, and the app is
  per-monitor DPI aware (PerMonitorV2) so text stays crisp at scales like 175% on a 4K display.
- **Renamed patched card servers are recognized.** A process named `Server` is treated as patched when its folder
  has a `BUILD_COMMIT` (or is named like `mm-server2`), and is reused on Start all. Only the pack's
  `server\Server.exe` is called stock. The same message is not logged twice on repeated clicks. The status label
  shows why a start or Check port failed.
- **First-run wizard.** Opens when there are no profiles, or from **New**. Eight pages: game folder (checks the
  game exe, LM exe, `bngrw.dll`, and ini files), card (keep a valid existing one or generate a new one, values
  masked), unique serial, network (only-cab vs multi-cab, secondary-IP PowerShell with `-SkipAsSource`, never offers
  `127.0.0.1` for the LM), card server (version check, release download link, matchmaker address and online tag
  secret written only to `Configurations/server.json`, never logged; link to setup guide section A8), online battle
  mode (sets every `battleMode` field in `29\testmode_pcb.json` to 0 for WAN matchmaking; other test-mode fields stay
  byte-for-byte intact; the svd1 container is backed up, rewritten with a fresh SHA-1, and read back; if the version
  or checksum cannot be rewritten safely the file is left unchanged), checks (test
  start of the card server, HTTP reachability of the matchmaker, TinyWall / Windows Firewall presence and the exact
  exes to allow — rules are never changed), and a summary of every file that will change, with backups, then
  **Save** or **Save and Start**.
- **Troubleshooting.** A button with plain-English notes for 21-01, 21-32, card in use / このカードは使用中です, a missing
  card server, and firewall blocks.
- **Card-server `Server=` is checked.** The main UI and the wizard show `Server=` from both `config.ini` and
  `lmnfig.ini`, and warn if it is missing a port, does not match the profile card server address, or that address
  is not answering. Write IPs and Save offer to write the correct `host:port` (error 21-32 otherwise).
- **LM `DisableSocketHook` is shown and checked.** The main UI and the wizard display `lmnfig.ini DisableSocketHook`. If it is `True` while the LM IP differs from the PCB IP or more than one cab is on this PC, they warn that the LM will grab every address and other LMs fail with 21-32, and Write IPs / Save offer to set it back to `False`. The launcher never turns the hook off, and SnowLauncher.ini sync will not push `True`.
- **Unusable LM and PCB IPs are rejected.** The main UI and the wizard refuse `127.0.0.1`, `0.0.0.0`, link-local
  `169.254.x`, and any address not assigned to a local adapter. The LM hook needs an address that exists on an
  adapter or it fails with "failed to find interface". Dropdowns only list real adapter addresses. A bad value
  already in `lmnfig.ini` is not kept as authoritative.
- **One profile per game folder**, saved as `exvs2-launcher.profiles.json` next to the exe (no card values in it).
- Refuses to write files or start while SnowLauncher or the game is running.

How to test it: [docs/TESTING.md](docs/TESTING.md). Build: see below.

## Build

```
dotnet test tests/Exvs2Launcher.Core.Tests
dotnet publish src/Exvs2Launcher.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

On Linux/macOS the WPF project builds thanks to `EnableWindowsTargeting=true` (set in `Directory.Build.props`); the
app itself only runs on Windows 10/11 x64. Logic lives in `src/Exvs2Launcher.Core` (cross-platform, unit-tested);
`src/Exvs2Launcher.App` is a thin WPF window.

## Docs

Docs and the architecture are in:

- [docs/SNOWLAUNCHER-NOTES.md](docs/SNOWLAUNCHER-NOTES.md) — observed SnowLauncher behavior (clean-room notes, no source copied)
- [docs/DESIGN.md](docs/DESIGN.md) — proposed architecture, stack options, and feature list
- [ROADMAP.md](ROADMAP.md) — MVP milestones
- [CHANGELOG.md](CHANGELOG.md) — what changed in each version
- [docs/TESTING.md](docs/TESTING.md) — manual test steps for each feature
- [docs/DIAGNOSTICS.md](docs/DIAGNOSTICS.md) — what the diagnostics zip contains and masks
- [docs/RICH-PRESENCE.md](docs/RICH-PRESENCE.md) — setting up Discord Rich Presence

No official binary is published yet; test builds are shared privately.

## Community

Join the [EXVS2 matchmaking Discord](https://discord.gg/YENXAmjkYX) for guides, troubleshooting and updates.

## Related project

- Card server + VPS matchmaker: https://github.com/ServerOmega/exvs2-matchmaking

## License

Copyright (c) 2026 ServerOmega.

This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version. It is distributed WITHOUT ANY WARRANTY. See `LICENSE` for the full text.
