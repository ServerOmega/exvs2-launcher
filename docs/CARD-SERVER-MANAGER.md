# Card server manager and matchmaking server list (v0.5.0)

## Card server... window

Works on one card server folder, by default the one in the current cab profile.

| Folder you pick | What the launcher treats as |
|---|---|
| `...\server\Server-Infinite.exe` or the `server` folder | the server folder; its parent is the package folder |
| a folder with `server\Server-Infinite.exe` (an extracted release package) | the package folder |
| an empty / new folder | a new install: the server goes into `<folder>\server` |

Status shows Running (pid) / Stopped, whether it listens on its own port (from `Configurations\kestrel.json`,
default 28080) and the installed version (the launcher's `exvs2-launcher-cardserver.json`, else the package's
`files\VERSION`). **Start** opens the server in its own console window and waits until the port answers. **Stop** asks
the window to close (graceful shutdown), then ends it after 15 seconds.

### Install and update

1. **Check for updates** reads `https://api.github.com/repos/ServerOmega/exvs2-matchmaking/releases` (no login).
   "Include pre-releases" is on by default because every release so far is a pre-release. Drafts are ignored.
2. Asset choice per release: a prebuilt Windows zip (name contains `win-x64` / `windows`) if there is one, otherwise
   `exvs2-matchmaking-<tag>.zip`. A release is skipped when there is no SHA-256 for that zip (no `.sha256` asset and
   no GitHub digest).
3. Download (https only) and SHA-256 check. When both the `.sha256` asset and GitHub's digest exist they must agree.
   A mismatch deletes the download and changes nothing.
4. Build. The releases contain scripts and a patch only: the original card server has no license, so a compiled
   server can't be redistributed. The launcher runs the package's `scripts\build-server.ps1 -NoPrompt` in a visible
   PowerShell window (it downloads the pinned card-server source, a private .NET 9 SDK and MinGit into the package's
   `tools` folder, applies the patch and builds). The old server keeps running meanwhile. `tools\` is kept next to
   the server so later updates don't download .NET again.
5. Update only: you must tick "All cabs that use this card server are idle". Then the server is stopped, and the
   port is checked: if another program still holds it, the update stops here and the old server is restarted.
6. Swap: the old `server` folder is moved whole to `exvs2-launcher-work\backups\server-<yyyyMMdd-HHmmss>` and the new
   build moved in. Kept from the old folder:
   * `Configurations\*` — your files win; the new build's defaults are saved as `*.new-default` beside them,
   * `ServerInfinite.db`, `ServerInfinite.db-wal`, `ServerInfinite.db-shm` — copied, so the backup still has the
     pre-update database if the new server migrates it,
   * `wwwroot\images` — moved (or, if the new build has its own, only the missing pictures are copied),
   * any other top-level file the new build doesn't ship, except programs (`.exe .dll .pdb .so .bat .cmd`) — e.g.
     `leaderboard_salt.txt`, `leaderboard_hidden.json`, `leaderboard.json`, `mms_nodeids.json`.
7. Start and health check: the new server must stay running and listen on its port within 90 seconds (and, when
   Windows can tell, the listener must be that process). Otherwise it is stopped, the failed folder is kept as
   `backups\failed-server-<stamp>`, the old folder is moved back and restarted if it was running before.
   If the server was stopped before the update it is started for the check and stopped again.
8. The three newest backups are kept. For a package install the package's scripts and text files are refreshed in
   the package folder (`1-Build-Server.bat`, `2-Configure.bat`, `3-Start-Server.bat`, `scripts\`, docs).

A fresh install does not configure the server: use **Use for this card server** in the main window (MatchUrl /
StunHost), the online tag secret box, or the package's `2-Configure.bat`. **Import pictures...** runs the package's
`Import-Images.bat` (the WebUI pictures are not part of the source code).

## Matchmaking server dropdown

* Saved in `exvs2-launcher.profiles.json` as `MatchmakingServers` (name, host, port) and
  `SelectedMatchmakingServer`. No secrets.
* Defaults: "LA (Los Angeles)" and "EU (Europe)" on port 28090 with **empty hosts**. The public repository does not
  carry the server addresses; fill them in with **Edit list...** (host name or IP, or paste `http://host:28090/match`).
* Every 15 seconds each server with an address is probed: `GET http://host:port/status` (4 s timeout). Any HTTP
  answer counts as the ping; a valid status body adds the totals ("6 online (2 searching, 4 in match)"). When HTTP
  gets no answer at all the launcher times a TCP connect to the same port and shows "(TCP)".
* **Use for this card server** writes `CardServerConfig.OnlineMatching.MatchUrl = http://host:port/match` and
  `StunHost = host` into the profile card server's `Configurations\server.json` after a timestamped backup
  (`server.json.bak-yyyyMMdd-HHmmss`). Other keys (TagSecret, StunPort, …) are untouched. Restart the card server so it
  uses the new server. The dropdown selects the entry that matches the current MatchUrl when a profile is opened.

Fix in this version: older launcher builds wrote MatchUrl / TagSecret at the top level of server.json, but the card
server reads them from the `CardServerConfig` section; they are now written inside `CardServerConfig` (files without
that section, e.g. hand-made test files, are still edited in place).

### Matchmaker `/status` (exvs2-matchmaking branch `status-endpoint`)

```json
{"service":"exvs2-matchmaking","version":"0.6.0-dev","online":16,"searching":14,"inMatch":2,"matches":1,
 "updatedAt":"2026-11-17T00:06:39Z","cacheSeconds":5}
```

Totals only (no card ids, access codes, chip ids, names, serials, node ids or IPs), cached 5 s, rate-limited per IP.
Until a matchmaker runs that build, the dropdown shows its ping with "player count not available".
