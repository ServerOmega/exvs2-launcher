# n2n VPN support for the EXVS2 launcher (research, 2026-11-16)

Status: research (2026-11-16). Mode A was then built as an MVP on branch `n2n-vlan`; see
[VIRTUAL-LAN.md](VIRTUAL-LAN.md). No VPS supernode was set up.
Builds on `docs/SNOWLAUNCHER-NOTES.md`, `docs/DESIGN.md`, `ROADMAP.md` Milestone 5,
and `/workspace/projects/exvs2-lobbies/notes/relay-research-2026-11-16.md` plus
`/workspace/projects/exvs2-lobbies/analysis/lm-over-vpn-split.md`.
No SnowLauncher code was copied. No community keys, card ChipId/AccessCode/BootToken,
or tag secrets appear here.

## TL;DR

n2n (ntop/n2n, GPL-3.0) is a Layer-2 P2P VPN: each PC runs `edge`, which creates a
TAP Ethernet adapter, and a public `supernode` helps peers find each other and
**relays packets when UDP hole punching fails**. That is why the EXVS community
(and SnowLauncher's online tab) uses it: remote cabs look like they are on the
same arcade LAN, so LM discovery (broadcast to `255.255.255.255:49000`) works.

It is **not** a drop-in TURN/relay for the VPS matchmaker. The game has no TURN
path. Pinning LM/client to the TAP puts those cabs in a separate overlay realm
that cannot mix with the public internet queue. The in-matchmaker relay plan
remains the right fix for internet hole-punch failures. n2n is the right tool
for classic "link cabs over a virtual LAN" play, which our launcher does not
support yet.

License is compatible with our GPL-3.0-or-later launcher if we ship `edge.exe`
with source/license notices. Do **not** bundle the OpenVPN TAP driver (GPL-2.0,
not ours; DESIGN.md already forbids it). Do not copy SnowLauncher.

---

## 1. What n2n is

- Upstream: [ntop/n2n](https://github.com/ntop/n2n). License: **GPL-3.0**
  (`COPYING`). Compatible with this repo (GPL-3.0-or-later). Bundling `edge.exe`
  is allowed if we also offer Corresponding Source (or a written offer) and keep
  the license/copyright notices.
- Two programs:
  - **supernode**: public UDP rendezvous and packet relay. Default listen port
    **7654**. Cannot decrypt payload if edges use `-k`. Management UDP port
    defaults to **5645**.
  - **edge**: creates a TAP virtual Ethernet NIC, joins a **community** (`-c`,
    max 16–19 chars), optionally encrypts payload (`-k`, default cipher AES,
    `-A_`) and headers (`-H`). Management UDP port defaults to **5644**.
- A community is a virtual LAN. Edges try UDP P2P first; if NAT blocks that,
  the supernode relays. `-S1` forces always-relay (UDP); `-S2` is TCP-to-supernode
  and was **not available on Windows** as of the 3.0 notes.
- This is **TAP (L2)**, not Wintun (L3 TUN). Wintun will not work. L2 matters
  because LM discovery is a limited broadcast; Tailscale (no broadcast) is a
  poor fit, while n2n and Radmin can carry it.
  See [TapConfiguration.md](https://github.com/ntop/n2n/blob/dev/doc/TapConfiguration.md).

### Windows requirements

- **TAP-Windows** (OpenVPN tap-windows6, NDIS 6, hardware ID historically
  `tap0901`). Official builds: [build.openvpn.net/downloads/releases](http://build.openvpn.net/downloads/releases),
  wiki: [Getting tap-windows](https://community.openvpn.net/Pages/Getting%20tap-windows).
  The Win10 attestation-signed installer
  `tap-windows-9.24.7-I601-Win10.exe` is the one community packs already ship
  and is the usual choice for Windows 10/11, including IoT LTSC. Newer 9.24.8
  files on that index are mostly `.msm` merge modules, not a standalone exe.
  Driver install needs Administrator and a Microsoft-attestation-signed package
  (Secure Boot). We cannot sign a kernel driver ourselves.
- **Admin rights to run edge**: opening and configuring the TAP device typically
  requires elevation; "TAP open failed" is the usual symptom without it. The
  launcher today is `asInvoker` (`app.manifest`) and should stay that way;
  only elevate `edge.exe` (or a small helper), not the whole UI.
- If several TAP adapters exist, `edge -d <adapter name>` selects one.
  `edge --help` lists TAP names on Windows.
- Windows sends **broadcasts only on the lowest-metric interface**. n2n's
  `-x <metric>` exists specifically so games can see LAN broadcasts on the TAP.
  Enable multicast with `-E` if needed (off by default). Default TAP MTU is a
  conservative **~1290** (`-M` to override; `-D` re-enables PMTU discovery).
- IP assignment: static `-a 192.168.x.y/24`, supernode auto-IP if `-a` is
  omitted (default pools `10.128.0.0/24`–`10.255.255.0/24`), or
  `-r -a dhcp:0.0.0.0` if a peer runs DHCP.

### Where to get Windows binaries (legitimate)

Official project does **not** publish a packaged Windows installer
([FAQ](https://github.com/ntop/n2n/blob/dev/doc/Faq.md),
[issue #890](https://github.com/ntop/n2n/issues/890)):

1. **Best if we ship a binary:** build `edge.exe` / `supernode.exe` ourselves
   from a pinned official tag (last stable **3.0**, 2021-10-27; pre-release
   **3.1.1**, 2022-03-31; `dev` is not guaranteed backward-compatible).
   Follow [Building.md](https://github.com/ntop/n2n/blob/dev/doc/Building.md)
   (MinGW). Ship the exe with `COPYING` and a link to the exact commit/tag.
   All edges **and** the supernode must be the same build family; mixed
   2.x / 3.x / mid-`dev` packet formats break.
2. GitHub Actions **Testing** workflow artifacts:
   `binaries.zip` → `x86_64-pc-mingw64/usr/sbin/`. Official but awkward:
   requires a GitHub login and artifacts expire.
3. Community collection officially linked from the n2n README:
   [lucktu/n2n](https://github.com/lucktu/n2n). Fine to point users at;
   prefer (1) if we bundle.
4. User-supplied path to an `edge.exe` they already have (matches DESIGN.md:
   "shell out to a tool the user already has").

Do not scrape random pack copies of edge/TAP. Build from official source or
use the project's own pointers.

---

## 2. How the EXVS community / SnowLauncher uses n2n

From public guides and our clean-room notes only (INI *key names* already in
`docs/SNOWLAUNCHER-NOTES.md`; no UI or code was copied):

- SnowLauncher has an online / virtual-LAN tab. Packs often include
  `tap-windows-9.24.7-I601.exe`. Observed `SnowLauncher.ini` keys:
  `N2N_Host`, `N2N_DHCP`, `N2N_Community`, `N2N_Relay`, `N2N_NoPortForwarding`,
  `N2N_MulticastMAC`, `N2N_PlayerName`, `N2N_NoInterface`, `N2N_Priority`,
  `N2N_Conf`, `N2N_VirtualIp`. A later importer could map those keys; do not
  treat their meaning as fully verified.
- Typical flow in public writeups (GameKee SnowLauncher page for XBOOST, pack
  / forum posts): install the virtual NIC when prompted, paste a community/host
  share often called a **"duck" / 小黄鸭** link, set a player name, connect,
  then start the game. Another cab on that virtual LAN is already running LM.
  [GameKee SnowLauncher](https://www.gamekee.com/exvs2xb/619101.html)
  (fetch of that URL failed with HTTP 500 during this research; title and
  community descriptions were taken from search snippets and our existing notes).
- **"Duck" = EasyN2N / 小黄鸭**, a third-party n2n client and public supernode
  service ([Bug侠 EasyN2N guide](https://bugxia.com/525.html)). Share links look
  like `easyn2n://...` and encode server, group name, and IP mode. Community chat
  groups hand those links around.
  There is **no single official EXVS supernode or community name** we should
  hard-code. Do not publish or commit group names or keys.
- Purpose: **LM-to-cab linking over the internet as if it were LAN**, same role
  as Radmin VPN. One host runs LM; others join as clients (or bring their own
  LM) on the TAP subnet. Local card servers usually stay at `127.0.0.1:28080`.
  This predates / sits beside our VPS matchmaker.
- Stability is mixed. An Emuline thread reports SnowLauncher n2n/Radmin setups
  dropping mid-battle ("disconnected from LM once the battle started").
  Treat reliability as unproven and worth testing on our own supernode first.
  [Emuline EXVS2 guide, p.3](https://www.emuline.org/topic/3670-gundam-exvs2-xboost-standalone-terminal-emulation-1-pc-guide/page/3/).

---

## 3. How it fits our architecture

Current path: each cab runs a local card server (28080) + LM + client. The VPS
matchmaker (TCP 28090, STUN 3478) matches players; the game hole-punches UDP
P2P. There is no game-side TURN
([relay research](/workspace/projects/exvs2-lobbies/notes/relay-research-2026-11-16.md)).

### Three different modes (do not mix casually)

| Mode | What n2n does | Bind / hook | Mix with public queue? |
| --- | --- | --- | --- |
| **A. Overlay club (classic n2n/Radmin)** | Whole cab (LM discovery + game UDP) lives on the TAP LAN. Supernode relays if P2P fails. | Hook on (`DisableSocketHook=False`), `IpAddress` / `InterfaceName` = TAP. | **No.** STUN/P2P see overlay addresses; separate realm. This is the community norm. |
| **B. Split (LM on n2n, matches on the internet)** | Only the LM link uses the TAP; STUN and peer UDP use the real NIC. | Documented as *probably yes, untested* in `lm-over-vpn-split.md`. Needs `DisableSocketHook=true` so sockets stay on `0.0.0.0`, `InterfaceName`=TAP for the announced IP, **no default route** on the TAP, and broadcasts leaving on the TAP (lowest metric / n2n `-x`). | Aimed at yes, but **untested**, and `DisableSocketHook=true` is exactly what this launcher refuses to set because the LM then binds `0.0.0.0` and other local LMs fail with **21-32**. |
| **C. "Use n2n as the matchmaker relay"** | Does not work as a drop-in. Pinning to the real NIC ignores the TAP; pinning to the TAP becomes mode A. | — | The in-matchmaker UDP relay remains the right public-queue fix. |

n2n is already listed as option 4 in the relay research: "already the community
norm; everything inside the club; cannot mix with the public queue."

Adapter selection is mandatory. The launcher already lists Radmin/TAP/n2n/etc.
but **never auto-picks a virtual NIC** over a real one
(`LanAddressDetector`). For mode A it must *explicitly* write the TAP IPv4 into
`config.ini` / `lmnfig.ini` `IpAddress` (and interface names). Wrong NIC = no
LM, or the well-known "wrong LM on a shared network" problem, now on a
world-wide L2: **everyone in the same community can see every LM broadcast**.
Use a private community, not a public duck room, unless that is intentional.

The TAP must not become the default route, or internet MatchUrl/STUN/card-web
traffic will hairpin through the VPN.

Card server should stay **local 28080** in the usual layout. Sharing a card
server over the TAP is possible but is a different design and still should not
move the VPS matchmaker port.

---

## 4. Launcher design proposal

This revisits ROADMAP Milestone 5 ("Virtual LAN helpers … do not vendor those
tools") and DESIGN.md's non-goal ("Shipping a built-in n2n supernode").
Proposal: a first-class **Virtual LAN / n2n** optional mode that shells out to
n2n; we may later bundle a CI-built `edge.exe` under GPL, but we still do not
ship TAP or a supernode inside the launcher.

### Settings (store beside the profile; never log or commit secrets)

- Enable n2n (off by default)
- Supernode `host:port` (no built-in public default; optional placeholder only)
- Community name
- Encryption key (password box, same care as the tag secret: never shown after
  save, never written to the start log)
- Header encryption (`-H`) recommended on
- Virtual IP: static / supernode-DHCP (omit `-a`) / DHCP-from-peer
- MTU (default 1290), interface metric (`-x`, default 1 so broadcasts go to TAP)
- Multicast (`-E`) on by default for this game
- Optional always-relay (`-S1`) for CGNAT tests
- Optional TAP adapter name (`-d`) and edge description (`-I`, player/cab name)
- Path to `edge.exe`: user-supplied, or a bundled copy if we add one
- Optional import of a user-pasted EasyN2N share link *if* we later document
  that format ourselves; do not hard-code any group's server

### TAP driver

- Detect a TAP-Windows adapter (description/name hints already in
  `LanAddressDetector`: `tap-windows`, `tap adapter`, `openvpn`, `n2n`).
- If missing: show a short guide and a link to the official OpenVPN
  `tap-windows-9.24.7-I601-Win10.exe`. Do not bundle or silently run an
  installer. The user runs it elevated once.

### Process model

- Treat `edge.exe` as a fourth managed process, **before** the card server / LM /
  client when n2n mode is on.
- Start elevated (helper or `runas` / Windows "run this program as
  administrator" manifest on a tiny starter, not on the WPF app).
- Wait until the TAP is up and has the expected IPv4 (or a timeout with a clear
  error).
- Then Write IPs to the TAP address and start LM/client as today.
- Stop order: stop game processes first, then send edge a graceful stop
  (Ctrl+C / management-port `stop` / SIGTERM equivalent). A hard kill can leave
  the supernode holding the MAC/IP for ~2 minutes ("Authentication error. MAC
  or IP address already in use").
- Reuse the existing start-all / relaunch rules (v0.4.4): do not assume edge
  being up means the game is still running.

### Status and logs

- Poll edge management UDP **5644** (localhost only) for peers vs pending
  (relayed) peers, virtual IP, and community. Do not print the key.
- UI: TAP up/down, virtual IP, peer count, P2P vs relayed, last error.
- Capture edge stdout/stderr in the launcher log with the key redacted (same
  masking style as the tag secret).

### Elevation and TinyWall (user is on Win11 IoT LTSC with TinyWall allow-list)

- First connect will fail if TinyWall has not allowed `edge.exe`.
- Allow: `edge.exe` outbound/inbound UDP to the supernode port and to peers
  (dynamic P2P ports); localhost UDP 5644; and the TAP adapter itself so
  LM/client UDP **49000** and **48000–48020** (plus the usual card-server
  28080 on loopback) are not blocked.
- The troubleshooting probe already mentions TinyWall; add a specific "n2n /
  TAP not allowed" hint.
- Windows Firewall prompts may also appear for `edge.exe` and the game.

### Optional self-hosted supernode on our VPS

Feasible and recommended over public EasyN2N / `supernode.ntop.org:7777` if we
want a private club for testers.

- Install a **version-matched** `supernode` (apt from ntop, or the same tag we
  ship for edge).
- Open **one UDP port** that is not 28090 or 3478 (for example 7654).
- `community.list` allowlist; do not run an open supernode.
- Payload stays encrypted on the edges; still treat community name + key as
  secrets.
- Bandwidth if many sessions are relayed: same ballpark as the matchmaker
  relay estimate, about **0.5–1.2 Mbps per 4-human match**, plus n2n headers.
  Same-region +5–20 ms typical; a LA edge relayed through an EU supernode
  would be a full transatlantic hop and can trip the game's ~300 ms RTT limit.
  Prefer a supernode in the same region as the players, or a federation later.
- **Any VPS install, firewall port, or service start needs the user's
  approval, and should wait until cabs are idle.**

---

## 5. Effort, risks, recommended order

### Effort (once approved; research is done)

| Step | Estimate |
| --- | --- |
| TAP detect, settings UI, user-supplied `edge.exe`, elevated start/stop, wait for TAP IP, write TAP into `IpAddress`, status via :5644, redacted logs, tests | **2–4 days** |
| TinyWall / metric / multicast docs, elevation helper polish, optional EasyN2N-link paste | **+1–2 days** |
| Optional: CI-build and bundle official `edge.exe` + `COPYING` | **+~1 day** |
| Optional: VPS supernode (after approval, cabs idle) | **~0.5–1 day** ops |
| Real two-cab internet test (private community) | depends on testers |

A usable mode-A MVP is about **4–6 days**, not including a VPS supernode.

### Risks

- **Security:** community name + key are secrets. Never commit, log, or put them
  in a Page/README. A public duck room is a world-wide Ethernet: SMB, ARP, and
  every LM on the community are exposed. Prefer a private community, `-k` and
  `-H`, and optionally edge `-R` traffic filters later.
- **Driver signing on Win11 IoT LTSC:** only a signed official TAP installer;
  test once on the user's PC before telling everyone to install it. Unsigned or
  SHA-1-only old TAP packages will fail under Secure Boot.
- **TinyWall allow-list** will silently drop edge or TAP traffic until allowed.
- **Broadcast metric and hook policy:** wrong metric → LM not found; hook off
  on a multi-cab PC → 21-32. Mode B remains untested and fights our hook rule.
- **Version skew** between edge and supernode.
- **Stability:** community reports of mid-battle LM drops on n2n/Radmin.
- **License:** do not bundle TAP (GPL-2.0-only + "not ours"). n2n itself is fine.
- **Clean-room:** reimplement from n2n docs and observed INI keys; do not
  decompile or copy SnowLauncher.
- n2n does not replace the planned matchmaker relay for public internet play.

### Suggested first build (if the user says go)

1. Mode A only: private community, user-supplied `edge.exe`, official TAP
   installer link, bind LM/client to TAP, hook stays False, n2n off by default.
2. Test two cabs on a private community (our supernode only if approved).
3. Keep internet matchmaking as a separate mode with virtual NICs still not
   auto-selected.
4. Do not start mode B or a public-queue "n2n relay" without a new decision.

## Sources

- https://github.com/ntop/n2n
- https://github.com/ntop/n2n/blob/dev/COPYING
- https://github.com/ntop/n2n/blob/dev/doc/Faq.md
- https://github.com/ntop/n2n/blob/dev/doc/Building.md
- https://github.com/ntop/n2n/blob/dev/doc/TapConfiguration.md
- https://github.com/ntop/n2n/blob/dev/doc/Communities.md
- https://github.com/ntop/n2n/releases (3.0, 3.1.1)
- https://github.com/ntop/n2n/issues/890
- https://github.com/lucktu/n2n (linked from official README)
- http://build.openvpn.net/downloads/releases
- https://community.openvpn.net/Pages/Getting%20tap-windows
- https://bugxia.com/525.html (EasyN2N / 小黄鸭)
- https://www.gamekee.com/exvs2xb/619101.html (SnowLauncher public guide; HTTP 500 at fetch time)
- https://www.emuline.org/topic/3670-gundam-exvs2-xboost-standalone-terminal-emulation-1-pc-guide/page/3/
- Local: `docs/SNOWLAUNCHER-NOTES.md`, `docs/DESIGN.md`, `ROADMAP.md`,
  `src/Exvs2Launcher.Core/Net/LanAddressDetector.cs`,
  `src/Exvs2Launcher.Core/Net/SocketHookSetting.cs`,
  lobbies `notes/relay-research-2026-11-16.md`,
  `analysis/lm-over-vpn-split.md`
