using System.Net.Sockets;
using System.Text.Json;

namespace MineServers.Core;

/// <summary>"Can friends join?": asks mcstatus.io (a free server-status service) to connect to the join address from the internet.</summary>
public static class Reach
{
    public static async Task<string> Check(ServerEntry s, string address, bool running)
    {
        if (!running) return "Start the server first, then check again.";
        if (s.Tunnel == TunnelKinds.None)
        {
            var lan = $"{Tunnel.LanIp()}:{s.Port}";
            return await CanConnect(Tunnel.LanIp(), s.Port)
                ? $"People on your Wi-Fi can join at {lan}.\n\nFriends elsewhere can't, because the tunnel is set to None. Pick one in the Tunnels tab (playit.gg gives a fixed address)."
                : $"Even this PC can't connect to {lan}. The server may still be starting; wait for \"Done\" in the console.";
        }
        if (address.Length == 0 || address.StartsWith("Starting") || address.StartsWith("Tunnel failed"))
            return "The tunnel isn't up, so friends outside your Wi-Fi can't join yet. The console says what went wrong.";

        bool online;
        string details = "";
        try
        {
            using var doc = JsonDocument.Parse(await Downloads.Http.GetStringAsync("https://api.mcstatus.io/v2/status/java/" + Uri.EscapeDataString(address)));
            var root = doc.RootElement;
            online = root.GetProperty("online").GetBoolean();
            if (online && root.TryGetProperty("players", out var p) && p.ValueKind == JsonValueKind.Object)
                details = $" It sees {p.GetProperty("online").GetInt32()} of {p.GetProperty("max").GetInt32()} players online.";
        }
        catch (Exception e)
        {
            return "Couldn't run the check (is this PC online?): " + e.Message;
        }
        if (online)
            return $"Yes! The internet can reach {address}.{details}\n\nFriends join with Multiplayer > Add Server and that address.";
        var why = s.Tunnel == TunnelKinds.Upnp
            ? "Your router may not allow UPnP, or your internet provider shares one public address between homes (CGNAT), which blocks port forwarding. playit.gg works around both."
            : "The tunnel may have only just started: wait a minute and check again. If it still fails, restart the server or try another tunnel.";
        return $"No, the internet can't reach {address} right now.\n\n{why}\n\n(The check result can be up to a minute old.)";
    }

    static async Task<bool> CanConnect(string host, int port)
    {
        using var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port).WaitAsync(TimeSpan.FromSeconds(3));
            return true;
        }
        catch (Exception e) when (e is SocketException or TimeoutException) { return false; }
    }
}
