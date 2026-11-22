# MineServers

Host Minecraft Java servers on your own Windows PC, and choose **how** your friends reach them.
SquidServers only lets you tunnel through playit.gg. MineServers gives you a choice of tunnels, and you can add your own.

## Download

Get `MineServers-<version>.exe` from the [Releases page](https://github.com/DontGoToCollege/MineServers/releases/latest).
There's nothing to install. Put it in any folder and run it. It keeps everything (servers, worlds, downloaded Java and tunnel programs)
in a `MineServers-Data` folder next to itself.

## How to use it

1. Click **+ New server**, give it a name, pick Vanilla, Paper or Fabric and a Minecraft version, and agree to the Minecraft EULA.
   MineServers downloads the server, plus the right Java if your PC doesn't have it.
2. In **Settings**, pick a tunnel (see below) and click **Save**.
3. Click **Start**. When the server is up, the **Join address** box shows what your friends type into Minecraft. Click **Copy**.
4. The **Console** tab shows the server's output. Type commands there (for example `op YourName`) and press Enter.
5. **Stop** saves the world and shuts the server down. Closing MineServers does the same for every running server.

### What's in each tab

- **Console**: the server's output, coloured (warnings yellow, errors red), with a **Find** box that shows only matching lines, a command box (Up/Down for earlier commands), Clear, and **Old logs** to read earlier log files. Select lines and press Ctrl+C to copy them.
- **Performance**: CPU, memory and ticks per second (TPS) with 10-minute graphs, players online, uptime, a lag check, and **Spark**: it watches the server for a minute and opens a report showing what causes lag.
- **Players**: who's online (kick, make operator, ban, ban their IP, set game mode, teleport to another player, send a private message), operators, the whitelist, bans and IP bans, and everyone who has joined with their playtime and when they were last seen.
- **World**: backups and world tools (see below).
- **Scheduler**: tasks that run every few minutes, daily, weekly or once on a date. Steps are server commands plus `!wait 30`, `!restart`, `!start`, `!stop` and `!backup`. A task can wait until nobody is online (or until someone is). There are ready-made tasks for a daily restart with a warning, a restart once nobody's on, backups, auto-saves and announcements.
- **Plugins** (Paper) or **Mods** (Fabric, Forge): search Modrinth for ones that fit your server's version, and install them with their required dependencies. Turn them on or off, remove them, or **Check for updates** and update them all at once. On Paper, **Let Bedrock players join** installs Geyser and Floodgate, so friends on phones, consoles and Windows Bedrock can play too (they join on port 19132).
- **Tunnels**: how friends outside your network join (see below).
- **Settings**: memory, port and Java; restart automatically after a crash; start the server when MineServers opens; recommended Java settings (Aikar's flags); the server icon; **Upgrade** to a newer Minecraft version (or the latest Paper build); and the server's game settings as a simple form (server message with colours and a preview, game mode, difficulty, max players, view and simulation distance, PvP, flying, hardcore, spawn protection, resource pack and more). Game rules like mob griefing and keep inventory change instantly while the server runs. Settings changed while it runs are applied at the next restart.
- **Server Files**: browse the server's folder and edit its config files right in the app.

Next to the join address, **Can friends join?** tests the address from the internet and says plainly whether it works.

**App settings** (bottom left): start MineServers with Windows, hide it by the clock when minimized, alerts when someone joins, and a Discord webhook that posts when a server starts (with its address), stops or crashes, and when players join or leave.

If a server crashes, MineServers restarts it (up to 3 times in 10 minutes) and shows an alert by the clock.

### Backups and the World tab

- **Back up now** copies the whole server (world, settings, plugins or mods) into `MineServers-Data\Backups`. It works while the server runs: the world is saved first. MineServers keeps the last 10 (you can change that). Add a Scheduler task with `!backup` to back up automatically.
- **Restore selected** puts a backup back. **Download world** saves the world as a zip.
- With the server stopped you can **replace the world** with a zip or a folder (for example a singleplayer world from `.minecraft\saves`), **reset the Nether or the End**, or **start a new world** with a seed you choose.
- Every restore, world change and upgrade makes a backup first, so you can always go back.

### New server from a modpack

In **+ New server**, **From a modpack...** sets up a server from a Modrinth modpack file (`.mrpack`): the right Fabric server, the pack's mods and its config files. Fabric packs only for now.

Already using SquidServers? Click **Import from SquidServers**. Your servers show up in the list and stay in their
existing folders, so nothing is copied or changed. Fabric servers that SquidServers calls "Vanilla" are recognised as Fabric. Don't run the same server in both apps at the same time.

## Tunnel choices

| Tunnel | Account needed | Address | Notes |
|---|---|---|---|
| None (LAN) | No | Your PC's local IP | Only people on your own Wi-Fi/network. |
| UPnP port forward | No | Your public IP | Your router must allow UPnP, and your internet must not be behind CGNAT. |
| playit.gg | Free playit account | Stays the same | The first time, a browser page opens so you can link MineServers to your playit account. |
| bore.pub | No | Changes each start | Free and simple. |
| Pinggy | No | Changes each start | Uses Windows' built-in SSH. Free tunnels last 60 minutes. |
| ngrok | Free ngrok account | Changes each start | Paste your authtoken in Settings. ngrok asks for a card on file before allowing TCP tunnels. |
| Custom command | Depends | Whatever it prints | Run any tunnel program. Put `{port}` where the server port goes. MineServers shows the first `host:port` it prints. |

## If friends get "Connection timed out"

With the **None (LAN)** or **UPnP** tunnel, other computers connect straight to your PC, and Windows Firewall blocks that by default.
When you start such a server, MineServers checks and offers to allow its port. Click **Yes**, then approve the Windows prompt.
Also make sure friends use the address shown in **Join address**. Your PC may have other `192.168…` addresses (from VirtualBox or Hyper-V) that don't work.

The other tunnels (playit.gg, bore, Pinggy, ngrok) don't need any firewall change.

## Building it yourself

Needs the .NET 8 SDK. Run `.\publish.ps1`: it runs the tests and builds `MineServers.exe`.
Raise `<Version>` in `src/MineServers/MineServers.csproj` before each release. The script refuses to rebuild a version that's already archived.

## License

MIT: free for anyone to use, change and share. See [LICENSE](LICENSE).
