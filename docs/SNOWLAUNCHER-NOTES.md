# SnowLauncher — clean-room notes

These notes describe **observed behavior** of the third-party SnowLauncher used with EXVS2 Infinite Boost packs, plus public documentation. They are a specification of features a clean-room launcher would need to cover. They are **not** a source listing.

This project does not contain, and must not receive, any SnowLauncher binaries, assets, decompiled code, or copied UI. Do not run unknown DLLs or launch game/LM processes from this repo.

## License and source availability

Conclusion: **SnowLauncher does not appear to be open source.**

What was checked (November 2026):

- No public GitHub/GitLab repository publishes SnowLauncher source or a license for it. Repos with a similar name belong to unrelated programs (Minecraft, Snowbreak, ServiceNow, and so on).
- Community guides (GameKee, Chinese arcade forums, Emuline threads, pack README-style posts) treat it as a closed binary that you unzip over a game folder. Typical wording is that it is a convenient third-party launcher. No license text is provided in those guides.
- The copy observed on a local game install has Windows version resources:
  - Product / company / description: `SnowLauncher`
  - File version: `2.8.8.7`
  - Original filename: `SnowLauncher.dll` (consistent with a .NET single-file publish)
  - Copyright and trademark fields: empty
  - No LICENSE or README next to the executable
  - Two builds in the same folder: `SnowLauncher.exe` and a later `SnowLauncher_English.exe`, same file version
- Informational version on that binary included a git commit SHA that also exists on the public [asesidaa/EXVS2-POC](https://github.com/asesidaa/EXVS2-POC) history (a June 2024 merge about skip-opening / JPN mode). That is a hint of a historical relationship to the open EXVS2-POC stack. It is **not** a source release of SnowLauncher. EXVS2-POC itself currently has no GitHub license metadata. Treat SnowLauncher as a closed binary and do not copy it. Do not assume POC code can be pasted into this repo either; reimplement from observed behavior and our own design.

Related, but different, public tools:

| Tool | What it is | Open source? |
| --- | --- | --- |
| SnowLauncher | Pack GUI that writes INI files, can start LM + client, stock card server, and optional n2n | No public source found |
| EXVS2-POC | Hook DLL, card server, AutoIt launcher (`xboost_launcher` / `Launcher.au3`) for older EXVS2 / XBoost / OverBoost | Public repo; license unclear |
| TeknoParrot | Separate arcade front-end with its own OverBoost support | Closed (free account for some features) |
| EXVSIB Mod Manager | Fan mod tool; some packs have SnowLauncher call it before boot | Public releases exist; not this project |

This repo should be a **new orchestrator**, not a fork of any of the above.

## Where it lives

In a typical Infinite Boost folder, next to the game:

- `SnowLauncher.exe` / `SnowLauncher_English.exe`
- `SnowLauncher.ini` (its settings file)
- Game client `vsac29_Release.exe` and LM `vsac29_Release_LM.exe`
- `config.ini` (client), `lmnfig.ini` (LM), `card.ini` (emulated card)
- `server\Server.exe` (stock card server)
- Hook / IO DLLs in the same folder (see below)
- `tap-windows-9.24.7-I601.exe` (OpenVPN TAP driver, used with the built-in n2n/virtual-LAN tab)
- Optional: `EXVSIB_Mod_Manager.exe`, older `Run_iboost_CLIENT_mode_v4.exe` / `Run_iboost_LM_mode_v4.exe`

Some extra cabinet copies (for example P2/P3/P4 folders) may have the game executables and INI files **without** a copy of SnowLauncher. A replacement launcher should be able to point at any cab folder, not assume the launcher exe lives inside it.

No SnowLauncher settings were found under `%APPDATA%` or `%LOCALAPPDATA%`; configuration appears to be entirely beside the game.

## Files it reads and writes

SnowLauncher's own file is `SnowLauncher.ini`. Observed sections and keys (values omitted or generalized):

### `[AppSettings]`

- `StartClient`, `StartLM` — which processes to launch
- `ClientWindow`, `LMWindow` — integer window placement / size presets (exact mapping not fully documented)
- `ImagePath` — cosmetic (`LauncherImage.jpg`); the file is not always present
- n2n / virtual-LAN: `N2N_Host`, `N2N_DHCP`, `N2N_Community`, `N2N_Relay`, `N2N_NoPortForwarding`, `N2N_MulticastMAC`, `N2N_PlayerName`, `N2N_NoInterface`, `N2N_Priority`, `N2N_Conf`, `N2N_VirtualIp`

Public guides describe a second tab for online play: install a virtual NIC when prompted, paste a community/host (often called a "duck" / n2n server link), set a player name, connect, then start the game. Another cab on that virtual LAN is expected to already be running LM.

### `[Game_Config]`

- `Serial` — cabinet serial written into `config.ini` as `serial =`
- `Server` — card-server host:port also mirrored to `config.ini` / `lmnfig.ini`
- `PCB_IpAddress`, `LM_IpAddress` — LAN addresses for the client and LM
- `PCB_InterfaceName`, `LM_InterfaceName` — NIC names (for example `Ethernet` or `Radmin VPN`)
- `PCB_IpAddressRun`, `LM_IpAddressRun` — whether those auto-fill / apply flags are on
- `PCB_Resolution`, `LM_Resolution` — integer indexes that map into `[display] Resolution=` (for example a client value of `2` was observed to correspond to `1080p`; do not assume a complete table without more samples)
- `ChipId`, `AccessCode` — copied to `card.ini` under `[card]`
- `CardReaderComPort`, `DisableSocketHook`, `OpeningScreenSkip`, `Region`, `BootToken`, `FramerateLimit`

### Input sections

- `[Game_Client_Keyboard]`, `[Game_LM_Keyboard]` — WASD / arrow keys, face buttons, combo buttons (`ButtonAB`, `ButtonBC`, `ButtonAC`, `ButtonABC`), Coin, Card, Start, Test, Kill
- `[Game_Controller]`, `[Game_Controller_1]` — fightstick / pad bindings by button number, device `Name`, and flags for Kill/Test/Service

These are synced into `config.ini` (`[keyboard]`, `[controller]`, `[controller-1]`) and `lmnfig.ini`. Combo keys on the client keyboard become comma-separated bindings on the `A`/`B`/`C` keys in `config.ini`.

### Sync and in-memory copy

When SnowLauncher applies settings it writes **all** of:

- `SnowLauncher.ini`
- `config.ini` (client: `mode =Client`, serial, Server, window, IP, input, display)
- `lmnfig.ini` (LM: `Mode=LM`, Server, IP, input, display)
- `card.ini` (`accessCode`, `chipId`)

Observed limitation: **SnowLauncher keeps the INI in memory.** External edits while it is open are ignored or overwritten. Close it before changing files by hand or with another tool.

`OpeningScreenSkip=3` in `SnowLauncher.ini` was observed as `OpeningScreenSkip=SkipAll` in `config.ini`. `Region` was not always identical between the two files, so a replacement should treat that mapping as unverified.

## Card server start behavior (important for matchmaking)

Observed with the Infinite Boost pack and our matchmaking work:

1. SnowLauncher **ignores** a custom `Server=` value for the purpose of which process it starts. It always talks to **`127.0.0.1:28080`**.
2. It **starts the stock** `server\Server.exe` itself if it can bind that port.
3. To use a patched card server (`Server-Infinite.exe` from exvs2-matchmaking), the patched process must be started **first** so it already holds `127.0.0.1:28080`. Then start SnowLauncher / the game.
4. The game also reads `Server` from `config.ini` / `lmnfig.ini`. Those should still be set correctly, but they are not sufficient if SnowLauncher launches the stock server over the patched one.

A replacement launcher should start the **configured** card server (patched, with the right `server.json` / MatchUrl / tag secret), wait until the port is listening, and only then start LM and/or the client. It should not launch stock `Server.exe` unless the user explicitly asks for it.

## How the game is actually launched

From public guides and pack layout, a "start" typically means:

1. Optional: virtual LAN / n2n connect (online tab).
2. Optional: pack-specific steps such as running EXVSIB Mod Manager to sync mods (documented by the mod manager project: it can be invoked by SnowLauncher before the game).
3. Start LM (`vsac29_Release_LM.exe`) if `StartLM` is on, and/or the client (`vsac29_Release.exe`) if `StartClient` is on, after writing the INI files.
4. Start (or reuse) a card server on port 28080.

The injection / hook layer is **not** implemented inside the launcher GUI. The game processes load DLLs from the game folder in the usual way.

## Hooks, DLLs, and loaders (folder inventory only)

Do not copy or reverse these. This list is so the new launcher knows what it should *leave alone* versus *configure*.

| File | Observed role (from names, versions, and public docs) |
| --- | --- |
| `bngrw.dll` | Replacement Banapassport / card-reader library used by unofficial PC setups (same role as in EXVS2-POC writeups). No version resource on the observed copy. |
| `bngrw_orig.dll` | Official `BanaPassRW Lib` 1.6.1.0, company BandaiNamcoStudios. Keep as a backup; do not redistribute. |
| `lmgrw.dll` | Same size as the replacement `bngrw.dll` on the observed install; likely the LM-side counterpart. Leave in place. |
| `amactivator.dll` | Official "Mucha activation module" (BANDAI NAMCO Amusement Lab). Game/auth component, not a launcher feature. |
| `wajvio.dll` | Official "WinArc JV I/O Library" (BANDAI NAMCO Studios) — JVS I/O. |
| `vsac29_Release.exe` / `_LM.exe` | Game client and Live Monitor. |
| `server\Server.exe` | Stock local card server shipped with the pack. |

This project will **not** ship or reimplement hook DLLs. The launcher only writes config and starts processes. Users keep whatever hook DLLs their game folder already has.

`config.ini` also exposes `UseRealCardReader`, `CardReaderComPort`, `DisableSocketHook`, `Windowed`, `BorderlessWindow`, and display options. Those belong in our UI as editors of the game's INI, not as a new injector.

## Card emulation

- `card.ini` holds a 20-digit `accessCode` and a 32-character uppercase-hex `chipId`.
- The same two values are stored in `SnowLauncher.ini` `[Game_Config]`.
- A physical reader can be selected with `CardReaderComPort` / `UseRealCardReader`; the common pack setup leaves this empty and uses the INI card.
- Official card-insert is a Banapassport-style flow; unofficial setups emulate it through `bngrw.dll`.

For matchmaking, every cabinet needs a **unique** serial (four random characters are what we use today) in both `SnowLauncher.ini` (`Serial=`) and `config.ini` (`serial =`). A reused serial + public IP is rejected by the matchmaker as "same cabinet entered again". A fresh card should be generated per cab, not copied.

## Input and fightstick mapping

SnowLauncher is a mapping editor, not the input driver. It writes keyboard and controller bindings into the INI files that the hook/game already understand.

Observed client keyboard defaults (pack default, not something to copy as a requirement): WASD movement, J/I/K/L face buttons, combo keys on U/O/Y/T, Enter = Start, `5` = Coin, `4` = Card, F3 = Test, F4 = Kill.

Controller section stores a device name (Xbox pads, "Razer Panthera PS4", and similar), numeric button ids, and optional combo lists. `config.ini` may also have `[xinput]` / `Mode=xinput-native` and a `DeviceId` field (0–15, or 16 = all devices).

A replacement should edit the same INI keys and let the existing hook/game handle raw input.

## Resolution and window options

- Client and LM each have a resolution setting (`PCB_Resolution` / `LM_Resolution` in SnowLauncher; `[display] Resolution=` in the game INI). Observed or documented Resolution tokens: `144p`, `240p`, `480p`, `720p`, `1080p`, `2k`, `4k`, `8k`. The SnowLauncher integer indexes do not have a complete observed table (a client value of `2` has been seen next to both `720p` and, in earlier notes, `1080p`), so a replacement should write the game INI string and leave those indexes alone.
- `Windowed` and `BorderlessWindow` live in `config.ini` / `lmnfig.ini`.
- `FramerateLimit` is present in both places.
- `OpeningScreenSkip` in the game INI uses `None`, `SkipReminder`, `SkipBrand`, or `SkipAll`. `OpeningScreenSkip=3` in SnowLauncher.ini was observed as `SkipAll` in `config.ini`; other integer mappings are unverified.
- `ClientWindow` / `LMWindow` appear to be placement presets; the exact enum is not documented here.

`29\testmode_pcb.json` (battleMode and other test-mode settings) is **not** part of SnowLauncher.ini. The game picks a time-slot group from that JSON. A replacement launcher should edit it directly; SnowLauncher does not appear to.

## LM discovery and isolation

LM discovery on this game is a **LAN broadcast**. Two cabinets on the same home network can see each other's LM even when they intended to use different matches. SnowLauncher does not provide an isolation feature. A replacement should, at least, help set distinct IPs / NIC bindings and document a firewall or bind strategy so a test cab does not pick up a housemate's LM (or vice versa).

## What a clean-room launcher should reimplement

Reimplement as our own UI and process manager, reading and writing the **game's** documented/observed files:

1. Per-folder (per-cab) profiles, including folders that have no SnowLauncher copy.
2. Editors for `config.ini`, `lmnfig.ini`, `card.ini` (and, optionally, import/export of a SnowLauncher.ini *as data* so people can migrate — implement as our own parser, do not bundle the exe).
3. Unique serial generation and new card (`accessCode` + `chipId`) generation.
4. Auto-detect local IPv4 addresses and NIC names for PCB and LM.
5. Keyboard / fightstick binding UI that writes the existing INI keys.
6. Resolution, windowed/borderless, skip-opening, framerate options.
7. Start order: patched card server first (honoring MatchUrl and tag secret in the card server's config), wait for the listen port, then LM, then client — or any subset the profile asks for.
8. Do **not** start stock `Server.exe` when a patched server is configured.
9. Optional battleMode editor for `29\testmode_pcb.json`.
10. Log viewer for the card server and, if we write our own, the launcher log.
11. Optional later: virtual-LAN helpers, mod-manager handoff, LM isolation helpers.

Do **not** reimplement, bundle, or ship: hook DLLs, official game files, TAP drivers as if they were ours, or any SnowLauncher code/assets.

## Sources for these notes

- File names, PE version resources, and INI *keys* observed in a local Infinite Boost install (secret values such as chip id, access code, and tokens were not recorded).
- Public web guides (GameKee SnowLauncher writeup; pack/forum posts describing LM+client checkboxes, local IP fields, and an n2n/virtual-LAN tab; EXVSIB Mod Manager user guide mentioning `Server=` in `config.ini` / `lmnfig.ini` / `SnowLauncher.ini`).
- Behavior already confirmed while running exvs2-matchmaking: ignored `Server=`, forced `127.0.0.1:28080`, stock `Server.exe` start, in-memory INI, unique serial requirement.

No disassembly, decompilation, string dumps, or resource extraction was used.
