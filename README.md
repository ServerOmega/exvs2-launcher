# exvs2-launcher

A clean-room launcher for **Mobile Suit Gundam: Extreme Vs. 2 Infinite Boost** (Namco System BNA1), designed to work with the [exvs2-matchmaking](https://github.com/ServerOmega/exvs2-matchmaking) patched card server and VPS matchmaker.

This project is **not affiliated with**, endorsed by, or derived from Bandai Namco, SnowLauncher, TeknoParrot, or EXVS2-POC. It does not include game data, hook DLLs, or any third-party launcher binaries. You must supply your own legally obtained game installation.

Proprietary, all rights reserved (see `LICENSE`).

## Why this exists

The common pack launcher, SnowLauncher, is useful but closed source. It also has behaviors that fight a self-hosted matchmaking setup (it starts the stock card server on `127.0.0.1:28080`, holds its INI in memory, and does not help with unique cabinet serials, fresh cards, battleMode, or the matchmaker tag secret).

This repo is a from-scratch replacement for the *launcher / orchestrator* layer: profiles, config writing, and a correct start order. It will not reimplement game hooks. Existing hook DLLs already in a user's game folder stay as they are.

## Status

Private preview, **v0.1.0 (MVP, branch `mvp`)**: C# / .NET 8 + WPF. What it does today:

- **Correct start order.** Starts the patched card server you pick (`Server-Infinite.exe` or `3-Start-Server.bat`),
  waits until `127.0.0.1:28080` accepts connections, then starts the LM (`vsac29_Release_LM.exe`, optional) and the
  client (`vsac29_Release.exe`). It never starts the pack's stock `server\Server.exe`. If the port is already held it
  says by which program: the patched server is reused, the stock server or anything else is refused.
- **Unique serial and new card.** "New serial" writes a random 4-character serial (unique among your profiles) to
  `config.ini` `serial =`. "New card" writes a new 20-digit access code and 32-hex chip id (cryptographic RNG) to
  `card.ini`. Both can also update `SnowLauncher.ini` so SnowLauncher doesn't put the old values back. Every file is
  backed up as `<name>.bak-yyyyMMdd-HHmmss` before it changes. Card values are never shown or logged.
- **LAN IP auto-detect.** On Start (or "Detect now" + "Write IPs") it writes this PC's LAN IP to `config.ini` and
  `lmnfig.ini` `IpAddress` (and `PCB_IpAddress` / `LM_IpAddress` in `SnowLauncher.ini`). Virtual adapters (Radmin,
  TAP/n2n, Hyper-V...) are listed but never auto-picked over a real one.
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
