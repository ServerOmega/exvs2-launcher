# Testing the v0.6.0 launcher

Do these on a cab nobody is playing on. Close SnowLauncher and the game first (the launcher refuses to write files
or start-all while they are open). Per-component Start/Stop can be used when only part of the cab is running.

The main window keeps the cab profile, Start all / Cancel / Stop all / Check port, and the card server / LM / client
status at the top, and the log at the bottom. Other settings are on four tabs: **Play** (cab name, game folder, start
checkboxes), **Card Server** (path, Card server... manager, server settings, Open web page), **Matchmaking** (server
dropdown, ping and player counts, Edit list...), and **Cab** (serial and card, Controllers... / Keyboard..., graphics,
PCB / LM IPs). v0.6.0 adds **All cabs**, **Health**, **Cards**, **Stats** and **Settings** tabs, Start everything /
Stop everything in the top bar, and Diagnostics… / Export… / Import… in the profile bar (sections 9–17). The last
tab you had open is restored the next time the launcher starts.

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

The main window still has Browse / Save for an existing profile (Play tab for the folder, then Save). **Troubleshooting** explains 21-01, 21-32, card in use /
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
2. Under **Graphics and resolution**, the Client column should show `config.ini` (often 480p) and the LM column
   should show `lmnfig.ini` (often 144p), plus Windowed, Borderless, FramerateLimit, and Opening skip. Changing
   only the client resolution and clicking **Write graphics** should back up `config.ini` and leave `lmnfig.ini`
   alone. Close the game, LM, and SnowLauncher first, then restart them to pick up the change. `PCB_Resolution` /
   `LM_Resolution` in SnowLauncher.ini should not change.
3. Click **Controllers...**. The window should show a controller selector and a rebind list for Controller 1 / 2.
   Combo keys, raw ids, and `[xinput]` stay under **Advanced**. If a Razer Panthera (or another stick) is plugged
   in, **Refresh** should list a product name when Windows provides one (not just "XInput Controller 1"). **Use
   selected controller** copies the game-compatible `Name=` (for example `Razer Panthera PS4`); **Rebind** next to
   an action waits for a press and fills the 1-based button id; **Clear** empties it. **Write bindings** backs up
   `config.ini` (and SnowLauncher.ini if that option is on) and writes only the known keys — arcade bitmasks and
   comments stay as they were. Close SnowLauncher and the game first.
4. Click **Keyboard...**. The window should show two lists, Client (`config.ini`) and LM (`lmnfig.ini`).
   Each action should show its current key (the usual pack client layout is WASD, J/I/K/L, combos on U/O/Y/T,
   Enter stored as `Return`, `5` coin, `4` card, F3 test, F4 kill; the LM uses the arrow keys, Z/X/C/V, Q start,
   `5` coin, P card, F3 test, F4 kill). **Rebind** next to an action waits up to 8 seconds for a key press and
   fills the hook name (`Return` for Enter, `UpArr` for the up arrow, `1 (NumPad)` for numpad 1). A key the hook
   has no name for (Shift, Ctrl, semicolon, and similar) should be refused, not written. **Reset** restores that
   row to the pack default. **Write bindings** backs up `config.ini` and `lmnfig.ini` (and SnowLauncher.ini if
   that option is on) and writes only the known hook keys — leftover lines such as `ButtonR1` stay as they were.
   Close SnowLauncher, the game, and the LM first, then restart them to pick up the change.
5. Under **Server settings**, the card server host/port should match this cab. `Server=` from `config.ini` and
   `lmnfig.ini` is shown, along with `DisableSocketHook`. If a `Configurations/server.json` sits next to the card
   server, MatchUrl is shown. The tag secret box stays empty; the note should say whether a secret is already set,
   never the value. **Write server.json** updates MatchUrl (and StunHost) and, if you pasted a secret, the secret —
   the log must not print the secret. Leave the box empty to keep an existing secret.
6. With the card server running, click **Open web page**. The default browser should open `http://127.0.0.1:28080/`
   (or this cab's host/port). That is the card server WebUI (`/` or `/Cards`).

## 7. Wizard sets battleMode to 0

Close the game and LM first. Click **New** and walk through the setup wizard for a cab folder that has
`29\testmode_pcb.json`.

1. The **Online battle mode** page should be checked by default ("Set battleMode to 0 (online matching)"). It should
   say how many setting groups will change, or that they are already 0. If the file is missing or the svd1 checksum
   / version cannot be rewritten safely, it should say so and must not offer a write that it cannot complete.
2. On **Save**, if a change was needed, a timestamped `testmode_pcb.json.bak-...` should appear next to the file, the
   log should say which battleMode values went to 0, and no other test-mode fields should change. Leave the box
   checked unless you really want to keep an in-store battleMode.
3. Do not use EXIT & SAVE in the test menu to make this change. If a cab was already in a session, restart that cab
   so it rereads the file.

## 8. Card server manager and matchmaking servers (v0.5.0)

Do this with the cabs idle. Nothing here touches the game folders.

1. **Card Server** tab → **Card server...** with a cab profile whose card server is an existing package folder: Status shows Running /
   Stopped, the port and the version. Stop, then Start: the console window opens and the status says it listens.
2. Pick a new empty folder (e.g. `C:\exvs2-test-server`), Check for updates (pre-releases on), **Install v0.5.0**:
   the log shows the download, "SHA-256 verified", a PowerShell build window, then "Installed v0.5.0". The folder has
   `server\Server-Infinite.exe`, the package scripts and `exvs2-launcher-cardserver.json`. If it was started, it must
   not clash with a running card server on 28080 (the launcher refuses and says which program has the port).
3. Update / reinstall on the test folder: without the idle tick the launcher refuses. With it: old server stops, the
   new one starts and listens, `Configurations\server.json`, `ServerInfinite.db` and `wwwroot\images` are the old
   ones, and `exvs2-launcher-work\backups\server-<stamp>` has the previous folder.
4. Port clash / rollback: start a second card server on the test folder's port (or any program listening there),
   then run Update: the launcher says which program holds the port, does not swap, and restarts the old server.
   (Automatic rollback after a failed start is covered by the unit tests.)
5. Main window → **Matchmaking** tab: LA / EU show "no address yet". **Edit list...** → fill in the LA host → the
   dropdown shows a ping within 15 s ("player count not available" until the matchmaker has `/status`).
   **Use for this card server** → server.json gets `CardServerConfig.OnlineMatching.MatchUrl` and `StunHost`, and a
   `server.json.bak-*` file appears. Restart the card server.

## 9. Start everything / Stop everything (v0.6.0)

Needs two or more cab profiles (e.g. P1 and P2) that each have a working single-cab Start all. All cabs idle.

1. **All cabs** tab: every profile is listed with an **Include** tick, three lights (card server / LM / client), the
   game window title and folder. Untick a profile → it stays ticked/unticked after a restart.
2. Set **LM → first client** to 5 and **Between clients** to 8. Click **Start everything** (top bar or tab).
   Expected order in the log: each distinct card server once ("waiting for port…" then "listening"), then each LM,
   then wait 5 s, then client 1, wait 8 s, client 2. Lights go amber (Waiting/Starting) → green (Running).
3. Two profiles that share one card server (same exe and port) start it only once.
4. Make one profile's card server path wrong: its lights go red ("Failed" / "Skipped"), its LM and client are not
   started, and the other cabs still start. The status line lists the problem.
5. **Cancel** during the stagger: nothing more starts; what already runs keeps running.
6. With everything running, **Start everything** again: running parts are left alone (no second copy, no config
   writes for running cabs).
7. **Stop everything** → confirm: clients close, then LMs, then card servers. Task Manager: no process priority or
   affinity was changed at any point. The stock `server\Server.exe` never appears.
8. The single-profile **Start all** / **Stop all** still behave as in v0.5.1.

## 10. Health tab (v0.6.0)

1. Start a cab. **Health** shows "started" rows for its card server / LM / client within ~2 s.
2. Close the client from its own window (or Task Manager): an important "Stopped unexpectedly" row appears with
   the cab name. **Open saved event** opens a text file with the event and the card server log lines before it.
3. Stop the client from the launcher instead: the row says "closed by the launcher" and is not marked as a
   problem.
4. Play a match (or wait for card server log activity): match / room-end / failed-battle (`STUB SaveFailedBattleLogOn`)
   / network warning rows appear when the card server log contains them. MATCH and ROOM-END lines only exist in the
   log of the card server that runs the matchmaker.
5. **Problems only** hides normal rows. **Open events folder** opens `health-events` next to the profiles file.
6. Saved files contain no access codes, chip ids, tag secret or public IPs.

## 11. Diagnostics bundle (v0.6.0)

1. Profile bar → **Diagnostics…**. The left list shows every file that will be in the zip; clicking one shows its
   exact text.
2. Check: `system/system.txt` has Windows, CPU, RAM, GPU and driver; `system/crash-events.txt` lists Kernel-Power 41
   / BugCheck 1001 events or "(none)"; `cabs/<cab>/listing.txt` has names, sizes and versions only.
3. Search the previews for your access code, chip id, tag secret, n2n community and public IP: all show `***`.
   Your Windows user name in paths is `%USERNAME%`.
4. **Save zip…** → open the zip: only `.txt` / `.log` / `.json` / `.ini` / `.md` files, no exe / dll / db / images.

## 12. Cards tab (v0.6.0)

Card server running.

1. **Cards on this card server → Refresh**: lists card numbers and names, chip id column shows only a masked hint.
   The search box filters by name.
2. **Load card stats**: shows this cab's card name, battles (W / L), and last played. On an older card server
   without the Stats page it falls back to the recent battle count.
3. **Back up card.ini** → a new entry appears in the backups dropdown; **Open backups folder** shows it under
   `card-backups/<cab>`.
4. With the cab's game running, **Restore selected...** refuses. With it closed, confirm → `card.ini` is replaced,
   and the replaced file is kept as a backup first. Nothing is written to the card server database.

## 13. Stats tab (v0.6.0)

1. **Global leaderboard**: pick Class solo / Class team / Mix → **Refresh**. Rows show rank, name ((hidden) for
   hidden players), class (Pilot / Valiant / Ace / Extreme), grade, rate, W / L, matches, win %. The status line
   says whether it came from the card server or the matchmaker and the minimum matches.
2. If the cab's card is on the board its row is selected and the rank is shown.
3. Stop the card server and Refresh: with a matchmaker selected on the Matchmaking tab, the board still loads from
   the matchmaker's `/leaderboard/data`; otherwise "not available" with a reason.
4. **Load recent matches**: this cab's last battles with result, suit id, time, score and other players.

## 14. Export / import profiles (v0.6.0)

1. Profile bar → **Export…**. When the profile has a serial you are asked whether to include it (choose No).
   Open the `.exvs2profile.json`: no IPs, window title, card values, tag secret or serial.
2. **Import…** the same file: the review screen lists every setting and warnings. You already have that name, so it
   offers **Add as a new profile** ("P1 (imported)") and **Replace**. Add as new → a second profile appears and the
   original is unchanged.
3. Import again, choose **Replace** → a second confirmation appears. No → nothing changes. Yes → the profile is
   replaced and keeps its serial.
4. Typing a name that already exists while adding as new is refused. A file that is not a launcher profile (e.g.
   `{}`) is refused with a message. A profile pointing at stock `server\Server.exe` is not accepted as card server.

## 15. Self-update (v0.6.0)

Use a copy of an older launcher exe in a test folder.

1. **Settings → Launcher updates**: shows the installed version. **Check now** says up to date or which release is
   available (pre-releases only with the tick).
2. With "Check for updates when the launcher starts" on, an older exe shows an **Update … available** button in the
   profile bar a few seconds after start. Nothing is downloaded until you click **Download and install**.
3. **Download and install** → confirm: the log shows the download, "SHA-256 verified", and the launcher restarts as
   the new version. `Exvs2Launcher.exe.previous` is next to it.
4. **Go back to previous version** restores the old exe and restarts.
5. A tampered download (hash mismatch) is refused and nothing is replaced (covered by unit tests).
6. **Skip this version** hides the startup notice until a newer release appears.

## 16. Discord Rich Presence (v0.6.0)

Follow docs/RICH-PRESENCE.md to create an application id. Discord desktop app running.

1. Off by default and the id box is empty.
2. Paste the id → **Test**: your Discord profile shows "EXVS2 Infinite Boost — In menus" for a few seconds, with the
   "Join the EXVS2 Discord" button if ticked.
3. Tick **Show EXVS2 IB status…** and start a cab: "In menus" appears; queue / match states follow card server log
   lines when present. Close all clients: the status clears. Untick: the status clears.
4. With Discord closed nothing breaks; the launcher retries every 30 s.

## 17. Queue / match notifications (v0.6.0)

1. Off by default. Tick **Play a sound** and/or **Show a Windows notification**, then **Test**.
2. With the selected matchmaker showing player counts, a player starting to search plays the sound / shows the
   pop-up (at most one every 45 s). A new match does too when "When a match starts" is ticked.
3. A match line in this PC's card server log also notifies.
4. Nothing fires right after the launcher starts (the first reading is only a baseline).

## Known gaps in v0.6.0

- Health only knows what process exits and card server log lines tell it. A 21-01-style disconnect is only visible
  when the card server logs it; MATCH / ROOM-END lines exist only where the matchmaker runs.
- Card stats (battles, last played) are shown for each cab's own card only; the card list shows names and masked
  ids.
- Rich Presence and queue / match states are best-effort guesses from logs and `/status`.
- Pop-ups are tray balloon notifications (Windows shows them as notifications); there is no Action Center history
  entry or click action.
- Self-update never installs by itself.

## Known gaps in v0.4.2

- The game and LM are started with no command-line arguments and the game folder as working directory (same as a
  double-click). If your pack needs something else, tell us.
- The launcher cannot add a second LAN IP for you; the wizard shows the PowerShell command, but you still have to
  run it in an elevated window.
- No full test-mode editor yet. The wizard only sets `battleMode` to 0 in
  `29\testmode_pcb.json` (an svd1-wrapped store, not plain JSON). Other operator settings (`cpuMode`, time limits,
  coin costs, close times, and the rest) are left alone. If the file uses an svd1 version other than 1, or the SHA-1
  checksum does not match, the launcher will not write it.
- Controller product names come from HID / SetupAPI and Raw Input when Windows exposes them, then WinMM and XInput.
  Matching an XInput slot to a HID device is best-effort (IG_ interface index, then pairing leftover IG_ devices in
  order). A stick that never reports a product string may still show a generic label; you can type the name under
  Advanced. Rebind and the Last pressed line use Raw Input (HID usages mapped to the game's 1-based button ids) with WinMM and XInput as fallbacks. A PS4-mode Razer Panthera should show buttons such as 1 (Square) or 4 (Triangle) when pressed.
- Keyboard rebind only writes names from the hook's confirmed key table (the public VirtualKeyNames list the hook
  comments point at, plus `Enter` as an alias of `Return`). Shift, Ctrl, Alt, Win, Caps Lock, and punctuation have
  no hook name, so they cannot be bound. Extra keys observed in some `config.ini` files (`ButtonA=1 (NumPad)`,
  `ButtonR1`, and similar) are not hook keys and are left alone.
- SnowLauncher.ini `PCB_Resolution` / `LM_Resolution` indexes are not written; only the game INI `Resolution=`
  strings are. `OpeningScreenSkip` is synced to SnowLauncher only for the observed `SkipAll` = `3` pairing.
- If "Server" is the card server's process name, the launcher treats it as the stock server.
- The launcher log is plain text next to the exe; it never contains card values or secrets.
