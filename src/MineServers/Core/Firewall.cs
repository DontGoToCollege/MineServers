using System.ComponentModel;
using System.Diagnostics;

namespace MineServers.Core;

/// <summary>
/// Windows Firewall blocks incoming connections by default, so LAN and UPnP players time out until the port is allowed.
/// (Tunnels like playit.gg or bore connect from inside the PC and don't need this.)
/// </summary>
public static class Firewall
{
    public static string RuleName(int port) => $"MineServers (Minecraft TCP {port})";

    /// <summary>True if an enabled inbound "allow" rule already covers this TCP port. Reading rules needs no admin rights.</summary>
    public static bool IsAllowed(int port)
    {
        dynamic fw = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
        foreach (dynamic r in fw.Rules)
        {
            // Direction 1 = inbound, Action 1 = allow, Protocol 6 = TCP.
            if (r.Direction == 1 && r.Action == 1 && r.Enabled && r.Protocol == 6
                && string.IsNullOrEmpty((string?)r.ApplicationName) && PortListHas((string?)r.LocalPorts, port))
                return true;
        }
        return false;
    }

    /// <summary>"25565", "25565,25570" or "25560-25570" style port lists.</summary>
    internal static bool PortListHas(string? ports, int port) =>
        ports != null && ports.Split(',').Any(p =>
        {
            var range = p.Trim().Split('-');
            return int.TryParse(range[0], out var lo)
                && (range.Length == 1 ? lo == port : int.TryParse(range[1], out var hi) && port >= lo && port <= hi);
        });

    /// <summary>Adds an inbound allow rule for the port. Windows shows its own permission prompt; returns false if the owner declines.</summary>
    public static async Task<bool> Allow(int port)
    {
        var psi = new ProcessStartInfo("netsh",
            $"advfirewall firewall add rule name=\"{RuleName(port)}\" dir=in action=allow protocol=TCP localport={port} profile=any")
        {
            UseShellExecute = true, // needed for "runas", which triggers the Windows permission prompt
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) // the owner clicked No on the prompt
        {
            return false;
        }
    }
}
