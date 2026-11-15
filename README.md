# exvs2-launcher

An open, clean-room launcher for **Mobile Suit Gundam: Extreme Vs. 2 Infinite Boost** (Namco System BNA1), designed to work with the [exvs2-matchmaking](https://github.com/ServerOmega/exvs2-matchmaking) patched card server and VPS matchmaker.

This project is **not affiliated with**, endorsed by, or derived from Bandai Namco, SnowLauncher, TeknoParrot, or EXVS2-POC. It does not include game data, hook DLLs, or any third-party launcher binaries. You must supply your own legally obtained game installation.

The first milestone is documentation and design only. A license has not been chosen yet (see `LICENSE`).

## Why this exists

The common pack launcher, SnowLauncher, is useful but closed source. It also has behaviors that fight a self-hosted matchmaking setup (it starts the stock card server on `127.0.0.1:28080`, holds its INI in memory, and does not help with unique cabinet serials, fresh cards, battleMode, or the matchmaker tag secret).

This repo is a from-scratch replacement for the *launcher / orchestrator* layer: profiles, config writing, and a correct start order. It will not reimplement game hooks. Existing hook DLLs already in a user's game folder stay as they are.

## Status

Private preview. Docs and a proposed architecture are in:

- [docs/SNOWLAUNCHER-NOTES.md](docs/SNOWLAUNCHER-NOTES.md) — observed SnowLauncher behavior (clean-room notes, no source copied)
- [docs/DESIGN.md](docs/DESIGN.md) — proposed architecture, stack options, and feature list
- [ROADMAP.md](ROADMAP.md) — MVP milestones

No official binary is published yet.

## Related project

- Card server + VPS matchmaker: https://github.com/ServerOmega/exvs2-matchmaking

## License

License pending. See `LICENSE`. Until one is chosen, no permission is granted to redistribute this repository beyond the access already given to collaborators.
