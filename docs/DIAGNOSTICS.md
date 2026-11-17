# Diagnostics bundle

**Diagnostics…** (profile bar) builds a zip you can send when something goes wrong. Nothing is uploaded: you pick
where the zip is saved and who gets it.

## What goes in

| Zip path | Content |
| --- | --- |
| `README.txt` | Launcher version and what was masked |
| `launcher/exvs2-launcher.log` | The launcher log (last 2 MB) |
| `launcher/profiles.json` | Launcher profiles and settings |
| `system/system.txt` | Windows version, CPU, RAM, GPU name, driver version and date, resolution / refresh rate, audio device (WMI) |
| `system/crash-events.txt` | System event log, last 30 days: Kernel-Power **41** (unexpected shutdown / power loss) and BugCheck **1001** (blue screen), up to 50 events |
| `cabs/<cab>/listing.txt` | Top level of each cab's game folder: file and folder names, sizes, dates, exe/dll file versions. **No hashes, no file contents** |
| `cabs/<cab>/config.ini`, `lmnfig.ini`, `card.ini`, `SnowLauncher.ini` | The cab's INI files, masked |
| `cardserver/<server>/server.json`, `kestrel.json` | Card server settings, masked |
| `cardserver/<server>/logs/log-*.txt` | The 3 newest card server logs (last 2 MB each), masked |
| `health/*.txt` | Up to 50 saved Health tab events with their surrounding log lines, masked |

Click each entry in the preview to see the exact text that will be saved. The zip contains exactly what was previewed.

## What never goes in

- Executables, DLLs, game assets, databases (`ServerInfinite.db`), images. The writer only accepts `.txt`, `.log`,
  `.json`, `.ini` and `.md` entries that it generated itself, and refuses `..` or absolute paths.
- Card `card.ini` backups (they hold card keys).
- Secrets. These are masked as `***`:
  - any setting whose name contains `secret`, `token`, `password`, `accesscode`, `chipid`, `n2n_community`,
    `apikey`, `privatekey`, `sessionid`, `cardid`, `pilotid`, `am_id` (plus `card`, `amid`, `amids`);
  - 20-character hex/digit card access codes and 32-character hex chip ids anywhere in the text;
  - public IPv4 addresses (LAN addresses such as `192.168.x.x`, `10.x.x.x` and `127.0.0.1` stay, because they are
    usually what is wrong);
  - your Windows user name in paths (`C:\Users\you\…` → `C:\Users\%USERNAME%\…`).

Masking is best-effort. **Look through the preview before posting the zip in a public channel.**

## Health events

The Health tab saves one small text file per problem event (unexpected exit, possible
disconnect, failed battle, error lines) under `health-events` next to `exvs2-launcher.profiles.json`. Each file has the event, and the
card server log lines before it (20 by default), already masked. The folder keeps the newest 500 files.
