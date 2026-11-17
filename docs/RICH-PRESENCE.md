# Discord Rich Presence

The launcher can show "EXVS2 Infinite Boost — In menus / In queue / In match" on your Discord profile while a cab's
client is running. It is **off by default** and needs a Discord application id that you create yourself (free, two
minutes). The launcher ships with the id blank on purpose.

## 1. Create a Discord application

1. Open <https://discord.com/developers/applications> and sign in.
2. **New Application**. The name is what Discord shows as "Playing …", so call it something like `EXVS2 Infinite Boost`.
3. On **General Information**, copy the **Application ID** (a long number). You do not need a bot, a token, a client
   secret or OAuth2. Never paste a token or client secret into the launcher.
4. Optional: **Rich Presence → Art Assets** to add an image. The launcher does not reference images yet, so this is
   cosmetic only.

## 2. Turn it on in the launcher

1. **Settings** tab → **Discord Rich Presence**.
2. Paste the Application ID into **Discord application id**, tick **Show EXVS2 IB status on my Discord profile**,
   and keep **Show a "Join the EXVS2 Discord" button** if you want the button (it links to
   <https://discord.gg/YENXAmjkYX>). **How to get an id** opens this page.
3. **Test** sends a sample status for a few seconds.

The Discord desktop app must be running on the same PC (the browser version has no local connection). In Discord,
**User Settings → Activity Privacy → Share your detected activities with others** must be on.

## What it shows and how it knows

| Shown | When |
| --- | --- |
| nothing | no cab client is running |
| In menus | a client is running |
| In queue | the local card server log shows a match search for one of this PC's cabs (held 3 minutes) |
| In match | the local card server log shows a match / battle result (held 12 minutes) |

With more than one cab running it adds "· N cabs". The timer counts from the last state change.

This is best-effort: the game itself does not report its state, so the launcher guesses from card server log lines.
If your card server does not log match lines (they appear where the matchmaker runs), you will mostly see
"In menus".

## Privacy

Only the text above is sent, over Discord's local named pipe (`\\.\pipe\discord-ipc-0`…`9`). No card names,
card ids, IPs or server addresses are sent. Turning the option off clears the status.
