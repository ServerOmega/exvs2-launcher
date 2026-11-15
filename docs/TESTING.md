# Testing the v0.2.0 launcher

Do these on a cab nobody is playing on. Close SnowLauncher and the game first (the launcher refuses to write files
or start-all while they are open). Per-component Start/Stop can be used when only part of the cab is running.

## 0. Put the exe somewhere

Use `Exvs2Launcher.exe` from the self-contained build (no .NET install needed) or the small framework-dependent one
(needs the .NET 8 Desktop Runtime). Put it in its own folder, **not** inside a game folder. It saves
`exvs2-launcher.profiles.json` and `exvs2-launcher.log` next to itself. Hover any setting or button for a short
delay to read what it does.

## 1. First-run wizard (or New)

1. Open a fresh copy (no `exvs2-launcher.profiles.json` next to it), or click **New**. The setup wizard should open.
   Cancel leaves no profile; click **New** to open it again.
2. **Game folder** → Browse → the cab folder (for example
   `...\Mobile_Suit_Gundam_Extreme_Vs._2_Infinite_Boost\Test`). The page checks for `vsac29_Release.exe`,
   `vsac29_Release_LM.exe`, `bngrw.dll`, `config.ini`, and `lmnfig.ini`. A folder already used by another profile
   shows a warning, not a hard stop.
3. **Card** — a valid existing `card.ini` is kept (values stay masked). Otherwise generate a new one with one click.
   You should see a "never share this" note, never the raw numbers.
4. **Serial** — generated automatically, or adopted from the folder if it is unique. Another profile using the same
   serial should force a new one.
5. **Network** — local IPv4 addresses are listed and the PCB IP is filled in. "Only cab on this PC?" Yes: the LM may
   share the cab IP. No: pick a second address; if the adapter has only one IPv4, a one-line elevated PowerShell
   command (`New-NetIPAddress ... -SkipAsSource $true`) is shown, and is not run for you. Typing `127.0.0.1` for the
   LM is rejected.
6. **Card server** — pick `3-Start-Server.bat` or `Server-Infinite.exe` from v0.4.0-pre or later. The version is
   shown; an early package such as v0.1.0-pre warns. The releases link and setup guide section A8 are offered. Enter
   the matchmaker address and (if needed) the online tag secret. The secret is masked and must never appear in the
   log; it is written only to `Configurations/server.json`.
7. **Checks** — Run checks. An already-running patched card server is left running; a test-started process is
   stopped afterwards. The matchmaker HTTP check should report a status code if the host answers. Firewall advice
   lists the exact exes to allow and does not change any rules.
8. **Summary** — every file that will change is listed, with backups. **Save** writes and returns to the main
   window; **Save and Start** also starts in the usual order.

Expected: the profiles JSON appears next to the exe and has no card values or tag secret in it. Game files that
already had a unique serial and a valid card are not rewritten unless you asked for a new card/serial or a new IP.

The main window still has Browse / Save for an existing profile. **Troubleshooting** explains 21-01, card in use /
このカードは使用中です, a missing card server, and firewall blocks.

## 2. Check port

Click **Check port** with the card server closed: "Port 28080 is free". With the patched server open: "patched card
server Server-Infinite (pid ...)". With SnowLauncher's stock server open: "STOCK card server".

## 3. New serial / New card

1. A new profile already has a serial — you do not have to click **New serial**. Use **New serial** only when you
   really want to change this cab's identity. Confirm; the log lists `config.ini` (and `SnowLauncher.ini`) as
   updated, plus `.bak-...` backups. Open `config.ini`: only the `serial =` line changed.
2. **New card** → confirm. `card.ini` gets new values; `card.ini.bak-...` keeps the old card. The log never prints the
   values. A working card.ini with `accessCode` / `AccessCode` and `chipId` / `ChipId` (20 digits or 20 hex, plus 32
   uppercase hex) is treated as valid; you should not see a false "missing or not in the expected format" warning.
3. To undo, close the launcher and copy the `.bak-...` file back over the original.

## 4. Network (PCB vs LM)

1. The PCB (client) IP is `config.ini` `IpAddress`. The LM IP is `lmnfig.ini` `IpAddress`. They are independent.
2. Tick "Detect this PC's LAN IP for the PCB" (or click **Detect now**): only the PCB box should change. The LM box
   and `lmnfig.ini` stay as they are. The log should say `lmnfig.ini IpAddress left as ...` and must not show
   `lmnfig.ini IpAddress: <old> → <detected PCB IP>`.
3. The LM dropdown lists every local IPv4 address, including secondary ones on the same adapter (for example both
   `.69` and `.245` on Ethernet). To change the LM IP, pick one and click **Write IPs** (or turn auto-detect off and
   Start). The log should show `lmnfig.ini IpAddress: <old> → <new>`. If SnowLauncher sync is on, `LM_IpAddress`
   follows the LM IP, not the PCB IP. `lmnfig.ini` is authoritative: if SnowLauncher.ini still has an older LM
   address, a Start/Write IPs will sync it to the lmnfig value.
4. Sharing one address is fine for a single cab. A note appears if they match. Use a separate LM address when more
   than one cab or LM runs on this PC. If the LM is pinned to the same specific address the game uses, you can get
   error 21-01 (live monitor disconnect).
5. `127.0.0.1` (or any loopback address) is rejected as an LM IP in both the main window and the wizard, including
   Write IPs and Start. If `lmnfig.ini` already has `127.0.0.1`, a red warning appears and that value is not kept as
   authoritative — pick a LAN address instead.

## 5. Start and stop

1. Close any card-server window for this cab if you want the launcher to start a new one. Click **Start all**.
2. Expected log order: (PCB IP written, LM IP left alone) → Starting Card server → Waiting for 127.0.0.1:28080 →
   Card server is listening → Starting LM (if ticked) → Starting Client. The stock `server\Server.exe` never appears.
3. With the patched server already running, Start all says "already listening ... Using it" and only starts the
   remaining parts.
4. Each of Card server, LM, and Client has its own **Start** and **Stop**, plus a Running/Stopped status (with pid).
   Stop only closes the copy that belongs to this profile: the process this launcher started, or the exe in this
   profile's folder. A game or LM in another folder (P2, Test, and so on) is left alone. **Stop all** does all three
   for this profile, in reverse order (client, LM, card server).

Please report: anything in the log that looks wrong, whether the game boots and reaches the card screen, whether
LM/client need a longer "LM → client delay", and whether the PCB and LM IPs it used are the ones you expected.

## Known gaps in v0.2.0

- The game and LM are started with no command-line arguments and the game folder as working directory (same as a
  double-click). If your pack needs something else, tell us.
- The launcher cannot add a second LAN IP for you; the wizard shows the PowerShell command, but you still have to
  run it in an elevated window.
- No battleMode editor, input bindings, or resolution helpers yet.
- If "Server" is the card server's process name, the launcher treats it as the stock server.
- The launcher log is plain text next to the exe; it never contains card values or secrets.
