# Testing the v0.3.0 launcher

Do these on a cab nobody is playing on. Close SnowLauncher and the game first (the launcher refuses to write files
or start-all while they are open). Per-component Start/Stop can be used when only part of the cab is running.

## 0. Put the exe somewhere

Use `Exvs2Launcher.exe` from the self-contained build (no .NET install needed) or the small framework-dependent one
(needs the .NET 8 Desktop Runtime). Put it in its own folder, **not** inside a game folder. It saves
`exvs2-launcher.profiles.json` and `exvs2-launcher.log` next to itself. Hover any setting or button for a short
delay to read what it does. On a high-DPI display (for example 4K at 175%), the tooltip should wrap instead of
being cut off as a single long line, and the gray "In the files now" note should wrap too.

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

The main window still has Browse / Save for an existing profile. **Troubleshooting** explains 21-01, 21-32, card in use /
このカードは使用中です, a missing card server, and firewall blocks. The network status line shows `Server=` from both
`config.ini` and `lmnfig.ini`. If `lmnfig.ini` has `Server=192.168.1.245` (no port), the line turns red and Save
or Write IPs offers to set `Server=127.0.0.1:28080`. A missing card server also adds a "not answering" warning.
The same line shows `lmnfig.ini DisableSocketHook`. If it is `True` and the LM IP is not the PCB IP (or you have more than one cab profile), it warns that the LM will grab every address and other LMs fail with 21-32, and Save or Write IPs offers to set it to `False`. Syncing SnowLauncher.ini must not write `DisableSocketHook=True`.

## 2. Check port

Click **Check port** with the card server closed: "Port 28080 is free". With the patched server open: "patched card
server ... (pid ...)". If the hidden build is a renamed Server.exe next to a BUILD_COMMIT file (for example
`mm-server2`), it should say it is the patched card server at that path, not stock. With the pack's stock
`server\Server.exe` open: "stock card server". Start all should reuse the patched server. Clicking Start all
again should not repeat the same failure line. The status label next to Check port should show the reason, not
just "Failed".

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
   authoritative — pick a LAN address instead. The same applies to `0.0.0.0`, `169.254.x`, and any address not
currently assigned to a local adapter (those make the LM fail with "failed to find interface"). The IP
dropdowns only list real adapter addresses.

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

## 6. Cab identity, controllers, server settings, and the web page

1. Set the cab name (for example "Cab 1") and Save. The window title should become `EXVS2 Launcher — Cab 1`. Start
   all / Stop lines in the log should use that name. The identity section should show the game folder name
   (for example `EXVS2IB_V29`). After the client is running, it should also show the current game window title
   (often `PCB_03` or similar). The launcher cannot rename that window.
2. Click **Controllers...**. The window should list the device name and button ids already in `config.ini`
   (`[controller]`, `[controller-1]`, `[xinput]`). If a Razer Panthera (or another stick) is plugged in, **Refresh**
   should list it (XInput or DirectInput / WinMM). **Use this device** copies the name; **Bind** next to a button
   waits for a press and fills the 1-based button id. **Write bindings** backs up `config.ini` (and SnowLauncher.ini
   if that option is on) and writes only the known keys — arcade bitmasks and comments stay as they were. Close
   SnowLauncher and the game first.
3. Under **Server settings**, the card server host/port should match this cab. `Server=` from `config.ini` and
   `lmnfig.ini` is shown, along with `DisableSocketHook`. If a `Configurations/server.json` sits next to the card
   server, MatchUrl is shown. The tag secret box stays empty; the note should say whether a secret is already set,
   never the value. **Write server.json** updates MatchUrl (and StunHost) and, if you pasted a secret, the secret —
   the log must not print the secret. Leave the box empty to keep an existing secret.
4. With the card server running, click **Open web page**. The default browser should open `http://127.0.0.1:28080/`
   (or this cab's host/port). That is the card server WebUI (`/` or `/Cards`).

## Known gaps in v0.3.0

- The game and LM are started with no command-line arguments and the game folder as working directory (same as a
  double-click). If your pack needs something else, tell us.
- The launcher cannot add a second LAN IP for you; the wizard shows the PowerShell command, but you still have to
  run it in an elevated window.
- No battleMode editor or resolution helpers yet. `29\testmode_pcb.json` is an svd1-wrapped test-mode store (not
  plain JSON). Besides `battleMode` it also holds `cpuMode`, `lanBattleTimeLimit`, coin costs, close times, and
  many other operator settings. A battleMode editor is still optional and not in this build.
- Controller detection uses XInput and WinMM only. A stick that appears only as a raw HID device may need its
  name and button ids typed by hand.
- If "Server" is the card server's process name, the launcher treats it as the stock server.
- The launcher log is plain text next to the exe; it never contains card values or secrets.
