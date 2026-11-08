using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace MineServers.Core;

/// <summary>
/// playit.gg through its public API, linked to the owner's own playit account by playit's claim flow.
/// (SquidServers instead gets its playit key from its own backend, which is why it can't offer anything else.)
/// One playit agent runs for all servers; each server gets its own "MineServers &lt;id&gt;" tunnel, reused across starts
/// so its address stays the same.
/// </summary>
public class PlayitTunnel : Tunnel
{
    const string Api = "https://api.playit.gg";
    static readonly SemaphoreSlim Gate = new(1, 1);
    static Process? _agent;
    static string KeyPath => Path.Combine(Store.PlayitDir, "agent_key");

    public override async Task<string> Start(ServerEntry s, CancellationToken ct)
    {
        string key;
        await Gate.WaitAsync(ct);
        try
        {
            key = await EnsureKey(ct);
            await EnsureAgent();
        }
        finally { Gate.Release(); }

        var agentId = (await Call("/agents/rundata", new { }, key, ct)).GetProperty("agent_id").GetString();
        var name = $"MineServers {s.Id}";
        var list = await Call("/tunnels/list", new { tunnel_id = (string?)null, agent_id = (string?)null }, key, ct);
        var tunnelId = list.GetProperty("tunnels").EnumerateArray()
            .FirstOrDefault(t => t.GetProperty("name").GetString() == name) is { ValueKind: JsonValueKind.Object } existing
            ? existing.GetProperty("id").GetString()
            : null;

        if (tunnelId == null)
        {
            Say("Creating a playit.gg tunnel for this server...");
            var created = await Call("/tunnels/create", new
            {
                name,
                tunnel_type = "minecraft-java",
                port_type = "tcp",
                port_count = 1,
                origin = new { type = "agent", data = new { agent_id = agentId, local_ip = "127.0.0.1", local_port = s.Port } },
                enabled = true,
                alloc = new { type = "region", details = new { region = "global" } },
                firewall_id = (string?)null,
                proxy_protocol = (string?)null,
            }, key, ct);
            tunnelId = created.GetProperty("id").GetString();
        }
        else
        {
            // The port may have changed since last time; point the tunnel at the current one.
            await Call("/tunnels/update", new { tunnel_id = tunnelId, local_ip = "127.0.0.1", local_port = s.Port, enabled = true, agent_id = agentId }, key, ct);
        }

        Say("Waiting for playit.gg to give the tunnel an address...");
        for (int i = 0; i < 30; i++)
        {
            var t = (await Call("/tunnels/list", new { tunnel_id = tunnelId, agent_id = (string?)null }, key, ct))
                .GetProperty("tunnels").EnumerateArray().FirstOrDefault();
            if (t.ValueKind == JsonValueKind.Object && t.TryGetProperty("alloc", out var alloc)
                && alloc.GetProperty("status").GetString() == "allocated")
            {
                var d = alloc.GetProperty("data");
                if (d.TryGetProperty("assigned_domain", out var dom) && dom.GetString() is { Length: > 0 } domain) return domain;
                return $"{d.GetProperty("static_ip4").GetString()}:{d.GetProperty("port_start").GetInt32()}";
            }
            await Task.Delay(2000, ct);
        }
        throw new TimeoutException("playit.gg didn't assign an address in time. Check your tunnels at playit.gg.");
    }

    /// <summary>Links MineServers to the owner's playit account the first time (browser approval), then reuses the key.</summary>
    async Task<string> EnsureKey(CancellationToken ct)
    {
        if (File.Exists(KeyPath) && File.ReadAllText(KeyPath).Trim() is { Length: > 0 } saved) return saved;

        var code = RandomNumberGenerator.GetHexString(10, lowercase: true);
        var url = $"https://playit.gg/claim/{code}";
        Say($"Link MineServers to playit.gg: approve it in the browser page that just opened ({url}).");
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

        var until = DateTime.UtcNow.AddMinutes(10);
        while (DateTime.UtcNow < until)
        {
            var state = (await Call("/claim/setup", new { code, agent_type = "self-managed", version = "MineServers " + AppInfo.Version }, null, ct)).GetString();
            if (state == "UserAccepted") break;
            if (state == "UserRejected") throw new InvalidOperationException("The playit.gg link was declined.");
            await Task.Delay(2000, ct);
        }
        var secret = (await Call("/claim/exchange", new { code }, null, ct)).GetProperty("secret_key").GetString()
            ?? throw new InvalidOperationException("playit.gg didn't return a key.");
        await File.WriteAllTextAsync(KeyPath, secret, ct);
        Say("Linked to playit.gg.");
        return secret;
    }

    async Task EnsureAgent()
    {
        if (_agent is { HasExited: false }) return;
        var exe = await Downloads.EnsureTool("playit.exe", "playit-cloud/playit-agent", n => n == "playit-windows-x86_64-signed.exe", null, Progress);
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "--secret-path", KeyPath, "--socket-path", @"\\.\pipe\playitd-mineservers", "--log-path", Path.Combine(Store.PlayitDir, "playit.log") })
            psi.ArgumentList.Add(a);
        _agent = Process.Start(psi);
        Say("playit.gg agent started.");
    }

    /// <summary>Closes the shared playit agent (when MineServers exits).</summary>
    public static void StopAgent()
    {
        try { if (_agent is { HasExited: false }) _agent.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    static async Task<JsonElement> Call(string path, object body, string? key, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Api + path) { Content = JsonContent.Create(body) };
        if (key != null) req.Headers.TryAddWithoutValidation("Authorization", "Agent-Key " + key);
        using var resp = await Downloads.Http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(text.Length > 0 ? text : "{}");
        var root = doc.RootElement;
        bool authError = (int)resp.StatusCode is 401 or 403
            || (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object
                && d.TryGetProperty("type", out var ty) && ty.GetString() == "auth");
        if (key != null && authError)
        {
            File.Delete(KeyPath); // the link was removed on playit.gg; link again on the next start
            throw new InvalidOperationException("playit.gg no longer accepts MineServers' key. Start the server again to re-link.");
        }
        if (!root.TryGetProperty("status", out _)) throw new InvalidOperationException($"playit.gg {path} failed: HTTP {(int)resp.StatusCode}");
        if (root.GetProperty("status").GetString() != "success")
            throw new InvalidOperationException($"playit.gg {path} failed: {root.GetProperty("data")}");
        return root.GetProperty("data").Clone();
    }
}
