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

- **Console**: the server's output, a command box (Up/Down for earlier commands) and Clear.
- **Performance**: CPU and memory with a 10-minute graph, players online, uptime, and a lag check (tick time).
- **Players**: who's online (kick, make operator, ban), operators, the whitelist and bans.
- **Scheduler**: tasks that run every few minutes, daily or weekly. Steps are server commands plus `!wait 30`, `!restart`, `!start` and `!stop`. There are ready-made tasks for a daily restart with a warning, auto-saves and announcements.
- **Plugins** (Paper) or **Mods** (Fabric, Forge): search Modrinth for ones that fit your server's version, and install them with their required dependencies. You can also turn them on or off, or remove them.
- **Tunnels**: how friends outside your network join (see below).
- **Settings**: memory, port and Java, plus the server's game settings as a simple form (game mode, difficulty, max players, view and simulation distance, PvP, flying, hardcore, spawn protection and more). Game rules like mob griefing and keep inventory change instantly while the server runs. Settings changed while it runs are applied at the next restart.
- **Server Files**: browse the server's folder and edit its config files right in the app.

Already using SquidServers? Click **Import from SquidServers**. Your servers show up in the list and stay in their
existing folders, so nothing is copied or changed. Don't run the same server in both apps at the same time.

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
