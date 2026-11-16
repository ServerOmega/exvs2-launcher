# exvs2-launcher

An open, clean-room launcher for **Mobile Suit Gundam: Extreme Vs. 2 Infinite Boost** (Namco System BNA1), designed to work with the [exvs2-matchmaking](https://github.com/ServerOmega/exvs2-matchmaking) patched card server and VPS matchmaker.

This project is **not affiliated with**, endorsed by, or derived from Bandai Namco, SnowLauncher, TeknoParrot, or EXVS2-POC. It does not include game data, hook DLLs, or any third-party launcher binaries. You must supply your own legally obtained game installation.

The first milestone is documentation and design only. Licensed under GPL-3.0-or-later (see `LICENSE`). Active development is on the `mvp` branch.

## Why this exists

The common pack launcher, SnowLauncher, is useful but closed source. It also has behaviors that fight a self-hosted matchmaking setup (it starts the stock card server on `127.0.0.1:28080`, holds its INI in memory, and does not help with unique cabinet serials, fresh cards, battleMode, or the matchmaker tag secret).

This repo is a from-scratch replacement for the *launcher / orchestrator* layer: profiles, config writing, and a correct start order. It will not reimplement game hooks. Existing hook DLLs already in a user's game folder stay as they are.

## Status

Private preview. Docs and a proposed architecture are in:

- [docs/SNOWLAUNCHER-NOTES.md](docs/SNOWLAUNCHER-NOTES.md) — observed SnowLauncher behavior (clean-room notes, no source copied)
- [docs/DESIGN.md](docs/DESIGN.md) — proposed architecture, stack options, and feature list
- [ROADMAP.md](ROADMAP.md) — MVP milestones

No official binary is published yet.

## Community

Join the [EXVS2 matchmaking Discord](https://discord.gg/YENXAmjkYX) for guides, troubleshooting and updates.

## Related project

- Card server + VPS matchmaker: https://github.com/ServerOmega/exvs2-matchmaking

## License

Copyright (c) 2026 ServerOmega. Licensed under the GNU General Public License v3.0 or later. See `LICENSE`.
