using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace MineServers.Core;

public record TunnelKind(string Id, string Label, string Help)
{
    public override string ToString() => Label;
}

public static class TunnelKinds
{
    public const string None = "none", Upnp = "upnp", Playit = "playit", Bore = "bore", Pinggy = "pinggy", Ngrok = "ngrok", Custom = "custom";

    public static readonly TunnelKind[] All =
    {
        new(None, "None (same Wi-Fi / LAN only)", "Only people on your own network can join, using your LAN address."),
        new(Upnp, "UPnP port forward (router)", "Asks your router to open the port. Free and fast, but only works if your router allows UPnP and you have a real public IP (not CGNAT)."),
        new(Playit, "playit.gg", "Free tunnel with a fixed address. The first time, your browser opens so you can link MineServers to a playit.gg account."),
        new(Bore, "bore.pub", "Free, no account. The address changes every time the server starts."),
        new(Pinggy, "Pinggy", "Free, no account, uses Windows' built-in SSH. Free tunnels close after 60 minutes and the address changes each time."),
        new(Ngrok, "ngrok", "Needs a free ngrok account and authtoken (Settings below). ngrok asks for a card on file before it allows TCP tunnels."),
        new(Custom, "Custom command", "Run any tunnel program you like. Use {port} where the server port goes."),
    };
}

/// <summary>Makes a server reachable from outside and reports the address players should use.</summary>
public abstract class Tunnel
{
    public event Action<string>? Log;
    protected void Say(string line) => Log?.Invoke("[Tunnel] " + line);
    protected IProgress<string> Progress => new SyncProgress(Say);

    /// <summary>Starts the tunnel and returns the address to share.</summary>
    public abstract Task<string> Start(ServerEntry s, CancellationToken ct);
    public virtual Task Stop() => Task.CompletedTask;

    public static Tunnel Create(ServerEntry s, AppConfig cfg) => s.Tunnel switch
    {
        TunnelKinds.Upnp => new UpnpTunnel(),
        TunnelKinds.Playit => new PlayitTunnel(),
        TunnelKinds.Bore => new ProcessTunnel(
            log => Downloads.EnsureTool("bore.exe", "ekzhang/bore", n => n.Contains("x86_64-pc-windows") && n.EndsWith(".zip"), null, log),
            port => new[] { "local", port.ToString(), "--to", "bore.pub" },
            @"(bore\.pub:\d+)"),
        TunnelKinds.Pinggy => new ProcessTunnel(
            _ => Task.FromResult(FindSsh()),
            port => new[]
            {
                "-p", "443", "-o", "StrictHostKeyChecking=no", "-o", "ServerAliveInterval=30",
                "-o", "UserKnownHostsFile=" + Path.Combine(Store.ToolsDir, "pinggy_known_hosts"),
                $"-R0:localhost:{port}", "tcp@a.pinggy.io",
            },
            @"tcp://([\w.-]+:\d+)"),
        TunnelKinds.Ngrok => new ProcessTunnel(
            log => Downloads.EnsureTool("ngrok.exe", null, null, "https://bin.equinox.io/c/bNyj1mQVY4c/ngrok-v3-stable-windows-amd64.zip", log),
            port => string.IsNullOrWhiteSpace(cfg.NgrokAuthToken)
                ? throw new InvalidOperationException("ngrok needs your authtoken. Paste it in Settings (get it free at dashboard.ngrok.com).")
                : new[] { "tcp", port.ToString(), "--authtoken", cfg.NgrokAuthToken.Trim(), "--log", "stdout", "--log-format", "logfmt" },
            @"url=tcp://([\w.-]+:\d+)"),
        TunnelKinds.Custom => new ProcessTunnel(
            _ => Task.FromResult("cmd.exe"),
            port => string.IsNullOrWhiteSpace(s.CustomTunnelCommand)
                ? throw new InvalidOperationException("Type the tunnel command in Settings first.")
                : new[] { "/c", s.CustomTunnelCommand.Replace("{port}", port.ToString()) },
            string.IsNullOrWhiteSpace(s.CustomTunnelPattern) ? ProcessTunnel.AnyAddress : s.CustomTunnelPattern),
        _ => new LanTunnel(),
    };

    static string FindSsh()
    {
        var ssh = Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe");
        return File.Exists(ssh) ? ssh
            : throw new InvalidOperationException("Windows' SSH client isn't installed. Add it in Settings > Apps > Optional features > OpenSSH Client.");
    }

    public static string LanIp()
    {
        // Connecting a UDP socket sends nothing; it just makes Windows pick the network card it would use.
        using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try { sock.Connect("8.8.8.8", 53); return ((IPEndPoint)sock.LocalEndPoint!).Address.ToString(); }
        catch (SocketException) { return "127.0.0.1"; }
    }
}

/// <summary>Reports progress on the calling thread (Progress&lt;T&gt; would post it later, out of order).</summary>
sealed class SyncProgress(Action<string> act) : IProgress<string>
{
    public void Report(string value) => act(value);
}

public class LanTunnel : Tunnel
{
    public override Task<string> Start(ServerEntry s, CancellationToken ct) => Task.FromResult($"{LanIp()}:{s.Port}");
}

public class UpnpTunnel : Tunnel
{
    int _port;

    public override Task<string> Start(ServerEntry s, CancellationToken ct) => Task.Run(async () =>
    {
        Say("Asking your router to open the port (UPnP)...");
        dynamic nat = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.NATUPnP")!)!;
        dynamic? maps = nat.StaticPortMappingCollection;
        if (maps == null)
            throw new InvalidOperationException("Your router didn't answer the UPnP request. Turn UPnP on in the router, or pick another tunnel.");
        maps.Add(s.Port, "TCP", s.Port, LanIp(), true, "MineServers");
        _port = s.Port;
        var ip = (await Downloads.Http.GetStringAsync("https://api.ipify.org", ct)).Trim();
        Say("Port opened. If friends still can't join, your internet provider may use CGNAT; try playit.gg instead.");
        return $"{ip}:{s.Port}";
    }, ct);

    public override Task Stop() => Task.Run(() =>
    {
        if (_port == 0) return;
        try
        {
            dynamic nat = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.NATUPnP")!)!;
            nat.StaticPortMappingCollection?.Remove(_port, "TCP");
        }
        catch (Exception e) { Say("Couldn't close the router port: " + e.Message); }
        _port = 0;
    });
}

/// <summary>Any tunnel that is a program printing its public address: bore, Pinggy, ngrok, or a custom command.</summary>
public class ProcessTunnel(Func<IProgress<string>, Task<string>> getExe, Func<int, string[]> args, string addressPattern) : Tunnel
{
    public const string AnyAddress = @"([\w-]+(?:\.[\w-]+)+:\d{2,5})";
    static readonly Regex Ansi = new(@"\x1B\[[0-9;?]*[A-Za-z]");
    Process? _proc;

    /// <summary>The first address in a line of tunnel output (colour codes removed), or null.</summary>
    internal static string? FindAddress(string line, string pattern)
    {
        var m = Regex.Match(Ansi.Replace(line, ""), pattern);
        return !m.Success ? null : m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Value : m.Value;
    }

    public override async Task<string> Start(ServerEntry s, CancellationToken ct)
    {
        var exe = await getExe(Progress);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args(s.Port)) psi.ArgumentList.Add(a);

        var found = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            Say(Ansi.Replace(line, ""));
            var addr = FindAddress(line, addressPattern);
            if (addr != null) found.TrySetResult(addr);
        }
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => OnLine(e.Data);
        p.ErrorDataReceived += (_, e) => OnLine(e.Data);
        p.Exited += (_, _) => found.TrySetException(new InvalidOperationException($"The tunnel program closed (exit code {p.ExitCode}). See the console for why."));
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _proc = p;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try { return await found.Task.WaitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            await Stop();
            throw new TimeoutException("The tunnel didn't report an address within 60 seconds.");
        }
    }

    public override Task Stop()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        _proc = null;
        return Task.CompletedTask;
    }
}
