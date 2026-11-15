# Testing the v0.1.1 MVP

Do these on a cab nobody is playing on. Close SnowLauncher and the game first (the launcher refuses to write files
or start-all while they are open). Per-component Start/Stop can be used when only part of the cab is running.

## 0. Put the exe somewhere

Use `Exvs2Launcher.exe` from the self-contained build (no .NET install needed) or the small framework-dependent one
(needs the .NET 8 Desktop Runtime). Put it in its own folder, **not** inside a game folder. It saves
`exvs2-launcher.profiles.json` and `exvs2-launcher.log` next to itself. Hover any setting or button for a short
delay to read what it does.

## 1. Profile

1. Open it. A first-run "Test cab" profile is created and given a random unique serial automatically (nothing is
   written to a game folder until you pick one).
2. Game folder → Browse → the cab folder (for example `...\Mobile_Suit_Gundam_Extreme_Vs._2_Infinite_Boost\Test`).
   The Identity box shows the current serial and "Card present (values hidden)". If the folder already has a unique
   serial, the profile adopts it and the folder is not rewritten. A new serial is written only when the folder has
   no serial, or when that serial is already used by another profile (the log says so).
3. Patched card server → Browse → a **current** matchmaking package (`3-Start-Server.bat` or `Server-Infinite.exe`
   from v0.4.0-pre or later). The version is shown under the path. Picking the pack's `server\Server.exe` is refused,
   and an early package such as v0.1.0-pre shows a warning.
4. Tick Card server and Client (and LM if this cab hosts the LM). Save.

Expected: no game file changes when the folder already has a unique serial; the profiles JSON appears next to the
exe and has no card values in it.

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

## Known gaps in v0.1.1

- The game and LM are started with no command-line arguments and the game folder as working directory (same as a
  double-click). If your pack needs something else, tell us.
- The launcher cannot add a second LAN IP for you; pick one that already exists on the adapter.
- No first-run wizard, battleMode editor, input bindings, resolution, MatchUrl / tag-secret editing, or firewall
  helpers yet.
- If "Server" is the card server's process name, the launcher treats it as the stock server.
- The launcher log is plain text next to the exe; it never contains card values or secrets.
