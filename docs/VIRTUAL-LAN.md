# Virtual LAN (n2n) mode

Optional, **off by default**, per cab profile. This is "mode A" from
[n2n-research-2026-11-16.md](n2n-research-2026-11-16.md): the whole cab (LM discovery and game UDP) lives on an
n2n TAP adapter, so cabs in other houses look like they are on the same arcade LAN. It is the classic
"LM over a VPN" setup (the role Radmin VPN or SnowLauncher's online tab play), done with tools you supply.

It is **not** the public matchmaker. Cabs on a virtual LAN are in their own realm and do not mix with the VPS
queue. Internet matchmaking mode is unchanged when this is off, and the launcher still never auto-picks a
virtual adapter (Radmin / TAP / n2n / ZeroTier / ...) outside this mode.

## What you need

1. **The OpenVPN TAP-Windows driver** (tap0901 / tap-windows6). Install it once, as administrator, from the
   official OpenVPN downloads: <https://build.openvpn.net/downloads/releases/>. The usual file for Windows 10/11
   (including IoT LTSC) is `tap-windows-9.24.7-I601-Win10.exe` (Microsoft attestation-signed, works with Secure
   Boot). See also <https://community.openvpn.net/Pages/Getting%20tap-windows>. The launcher detects the adapter
   and links that page; it never bundles or runs the installer.
2. **n2n `edge.exe`** that you download or build yourself (n2n is GPL-3.0, <https://github.com/ntop/n2n>).
   Official sources: build from a release tag (3.0 stable, or 3.1.1) with the MinGW steps in
   [Building.md](https://github.com/ntop/n2n/blob/dev/doc/Building.md), or use the binary collection linked
   from the n2n README ([lucktu/n2n](https://github.com/lucktu/n2n)). Everyone in the group **and** the
   supernode should run the same major version (3.x with 3.x). The launcher does not ship or download edge.exe.
3. From whoever runs your group: the **supernode** `host:port`, the **community** name, and the **key**.
   Use a private community. A public room is a world-wide Ethernet: every LM and every PC on it can see yours.

## Settings (Virtual LAN section of the main window)

| Setting | edge option | Default / notes |
| --- | --- | --- |
| Use an n2n virtual LAN | — | Off |
| edge.exe | — | Path to your edge.exe |
| Supernode | `-l host:port` | No built-in default |
| Community | `-c` | Up to 19 characters; masked, never logged; stored with the key, not in the profiles file |
| Key | `-k` | Masked, never shown again or logged; stored per profile for this Windows user only (DPAPI) in `exvs2-launcher.n2n-keys.dat` next to the profiles. Leave the box empty to keep the saved key; **Forget key** deletes it |
| Virtual IP | `-a ip/24`, none, or `-r -a dhcp:0.0.0.0` | Static (for example `10.77.0.11/24`, unique in your group), assigned by the supernode, or DHCP from a peer |
| MTU | `-M` | 1290 |
| metric | `-x` | 1, so Windows sends LAN broadcasts (LM discovery, UDP 49000) out of the TAP |
| TAP name | `-d` | Only if the PC has more than one TAP adapter |
| description | `-I` | Optional short name other edges see (15 characters) |
| Multicast | `-E` | On |
| Always relay | `-S1` | Off; for CGNAT / phone-hotspot tests |
| Header encryption | `-H` | Off; everyone must match, and the supernode must know the community |
| management port | `-t` | 5644 (localhost UDP), used for peers and a clean stop |

The key never appears on a command line. The launcher writes the options to a private
`%LocalAppData%\exvs2-launcher\n2n\edge.conf`, starts `edge.exe "<that file>"`, and deletes the file as soon as the
TAP is up (or the start fails). The log shows the options with the community and key replaced by `(hidden)`.
edge's own output goes to `%LocalAppData%\exvs2-launcher\n2n\edge.log` and is copied to the launcher log with the
same redaction.

## What Start does in this mode

1. Refuses to start while SnowLauncher or the game is open (as before).
2. Checks the settings and that a TAP-Windows adapter exists. Missing driver → a message and the download link.
3. Starts edge.exe **elevated** (one Windows administrator prompt; the launcher itself stays a normal app).
4. Waits up to 30 s for the TAP adapter to come up with its IPv4 (with a static address, that exact address).
   If edge exits or the time runs out, the start stops there with a clear error and edge is stopped.
5. Writes that TAP IP to `config.ini` and `lmnfig.ini` `IpAddress` (and SnowLauncher.ini if sync is on).
   The card server stays local: `Server=127.0.0.1:28080`. The profile's normal LAN IPs are not overwritten.
6. Starts the card server → LM → client as usual.

Start LM / Start Client on their own also bring edge up first and write the TAP IP to that program's ini.
Start card server alone does not need the virtual LAN. If edge is already running (started by this launcher,
or by hand on the same management port with the TAP already up), it is reused.

**Stop all** closes the client, LM and card server, then asks edge to exit through its management port
(`stop`), which lets the supernode release your MAC/IP at once. If edge ignores that for 5 s, the launcher
offers an elevated `taskkill` (another administrator prompt). **Disconnect** does the same on its own.
Closing the launcher also stops an edge it started (it asks first if the LM or game is still running).
An edge the launcher did not start is never stopped.

The status line shows the TAP adapter, the virtual IP, and peers from edge's management port: *direct*
peers have a P2P path, *via supernode* peers are relayed.

Turning virtual LAN off and starting again puts your normal LAN IPs back into `config.ini` / `lmnfig.ini`.

`DisableSocketHook` stays `False`: the launcher still never writes `True`, and in this mode it warns if
`lmnfig.ini` has `True`, because the LM must stay on the TAP address.

## TinyWall / Windows Firewall

TinyWall (allow-list mode) silently drops anything not allowed, so the first connect fails until you allow:

- **edge.exe** — outbound and inbound UDP (to the supernode port, for example 7654, and to peers on dynamic
  ports). Allowing the application is simplest.
- **The game and LM on the TAP adapter** — `vsac29_Release.exe` and `vsac29_Release_LM.exe` on UDP **49000**
  (LM discovery broadcast) and **48000–48020** (LM/game traffic). If you already allowed those exes for LAN
  play, check the rule is not limited to the LAN adapter or subnet.
- Localhost UDP **5644** (edge management) and the card server on `127.0.0.1:28080` are loopback; TinyWall
  normally allows loopback.

Windows Firewall may also ask about edge.exe and the game the first time; allow private networks. The TAP
adapter usually shows up as an "Unidentified network" (Public) — rules limited to Private will not apply to it.

## Known limitations (MVP)

- One cab per PC in this mode: the LM and client share the TAP IP. Multiple cabs on one PC would need
  secondary addresses on the TAP and is not handled yet.
- Untested against a real two-cab internet session. Other players have reported mid-battle LM drops on
  n2n / Radmin setups, so treat stability as unproven.
- No bundled edge.exe, no TAP installer, no supernode. A self-hosted supernode on the VPS is possible later
  but needs its own decision.
- Mode B (LM on n2n, matches on the internet) and "n2n as the matchmaker relay" are not implemented.
- EasyN2N / "duck" share links are not imported.
- Peer counts use edge's management port (JSON in n2n 3.x, text table in 3.0); other versions may show
  "peers unknown".
