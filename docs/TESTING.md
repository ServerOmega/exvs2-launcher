# Testing the v0.1.0 MVP

Do these on a cab nobody is playing on. Close SnowLauncher and the game first (the launcher refuses to write files
or start while they are open).

## 0. Put the exe somewhere

Use `Exvs2Launcher.exe` from the self-contained build (no .NET install needed) or the small framework-dependent one
(needs the .NET 8 Desktop Runtime). Put it in its own folder, **not** inside a game folder. It saves
`exvs2-launcher.profiles.json` and `exvs2-launcher.log` next to itself.

## 1. Profile

1. Open it. A "Test cab" profile is there.
2. Game folder → Browse → the cab folder (for example `...\Mobile_Suit_Gundam_Extreme_Vs._2_Infinite_Boost\Test`).
   The Identity box shows the current serial and "Card present (values hidden)".
3. Patched card server → Browse → that cab's `3-Start-Server.bat` (or `server\Server-Infinite.exe`). Picking the
   pack's `server\Server.exe` is refused.
4. Tick Card server and Client (and LM if this cab hosts the LM). Save.

Expected: no game file changes yet; the profiles JSON appears next to the exe and has no card values in it.

## 2. Check port

Click **Check port** with the card server closed: "Port 28080 is free". With the patched server open: "patched card
server Server-Infinite (pid ...)". With SnowLauncher's stock server open: "STOCK card server".

## 3. New serial / New card

1. **New serial** → confirm. The log lists `config.ini` (and `SnowLauncher.ini`) as updated, plus `.bak-...` backups.
   Open `config.ini`: only the `serial =` line changed; comments and other keys are the same.
2. **New card** → confirm. `card.ini` gets new values; `card.ini.bak-...` keeps the old card. The log never prints the
   values. On the next card insert the card server registers a new player.
3. To undo, close the launcher and copy the `.bak-...` file back over the original.

## 4. Start

1. Close any card-server window for this cab. Click **Start**.
2. Expected log order: (IP written) → Starting Card server → Waiting for 127.0.0.1:28080 → Card server is listening →
   Starting LM (if ticked) → Starting Client. The stock `server\Server.exe` never appears in Task Manager.
3. With the patched server already running, Start says "already listening ... Using it" and only starts the game.
4. **Stop game** closes LM + client the launcher started; **Stop all** also closes the card server it started.

Please report: anything in the log that looks wrong, whether the game boots and reaches the card screen, whether
LM/client need a longer "LM → client delay", and whether the IP it picked is the right one.

## Known gaps in v0.1.0

- The game and LM are started with no command-line arguments and the game folder as working directory (same as a
  double-click). If your pack needs something else, tell us.
- Stop only closes processes this launcher started in this session.
- No battleMode editor, input bindings, resolution, MatchUrl / tag-secret editing, or LM isolation yet.
- If "Server" is the card server's process name, the launcher treats it as the stock server.
- The launcher log is plain text next to the exe; it never contains card values or secrets.
