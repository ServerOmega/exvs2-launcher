# exvs2-launcher

A clean-room launcher for **Mobile Suit Gundam: Extreme Vs. 2 Infinite Boost** (Namco System BNA1), designed to work with the [exvs2-matchmaking](https://github.com/ServerOmega/exvs2-matchmaking) patched card server and VPS matchmaker.

This project is **not affiliated with**, endorsed by, or derived from Bandai Namco, SnowLauncher, TeknoParrot, or EXVS2-POC. It does not include game data, hook DLLs, or any third-party launcher binaries. You must supply your own legally obtained game installation.

Proprietary, all rights reserved (see `LICENSE`).

## Why this exists

The common pack launcher, SnowLauncher, is useful but closed source. It also has behaviors that fight a self-hosted matchmaking setup (it starts the stock card server on `127.0.0.1:28080`, holds its INI in memory, and does not help with unique cabinet serials, fresh cards, battleMode, or the matchmaker tag secret).

This repo is a from-scratch replacement for the *launcher / orchestrator* layer: profiles, config writing, and a correct start order. It will not reimplement game hooks. Existing hook DLLs already in a user's game folder stay as they are.

## Status

Private preview, **v0.1.1 (MVP, branch `mvp`)**: C# / .NET 8 + WPF. What it does today:

- **Correct start order.** Starts the patched card server you pick (`Server-Infinite.exe` or `3-Start-Server.bat`),
  waits until `127.0.0.1:28080` accepts connections, then starts the LM (`vsac29_Release_LM.exe`, optional) and the
  client (`vsac29_Release.exe`). It never starts the pack's stock `server\Server.exe`. If the port is already held it
  says by which program: the patched server is reused, the stock server or anything else is refused.
- **Unique serial and new card.** A new profile (and the first-run profile) gets a random unique 4-character serial
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
  separate LM address is recommended when more than one cab or LM runs on the same PC. Virtual adapters (Radmin,
  TAP/n2n, Hyper-V...) are listed but never auto-picked over a real one.
- **Card server version.** The path's folder name (for example `exvs2-matchmaking-v0.4.1-pre`) or a `BUILD_COMMIT`
  file is shown in the UI and the start log. A warning appears if it looks like a pre-release older than v0.4.0.
- **Per-component Start and Stop.** Each of the card server, LM, and client has its own Start and Stop, plus a
  Running/Stopped status. Stop tracks the PID this launcher started, then falls back to the exe in this profile's
  folder — never a process from another cab folder. **Stop all** still closes all three for this profile.
- **Hover tooltips.** Every setting and button has a short-delay tooltip in beginner-friendly English (text lives in
  `src/Exvs2Launcher.App/Resources/Tooltips.xaml` so it can be translated later).
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

No official binary is published yet; test builds are shared privately.

## Related project

- Card server + VPS matchmaker: https://github.com/ServerOmega/exvs2-matchmaking

## License

Copyright (c) 2026 ServerOmega. All rights reserved. See `LICENSE`.
