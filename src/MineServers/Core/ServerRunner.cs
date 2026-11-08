using System.Diagnostics;
using System.IO;
using System.Text;

namespace MineServers.Core;

/// <summary>Runs one Minecraft server's Java process and passes its console through.</summary>
public class ServerRunner
{
    public event Action<string>? Output;
    public event Action? StateChanged;

    Process? _proc;
    public string State { get; private set; } = "Stopped"; // Stopped | Starting | Running | Stopping
    public bool IsRunning => _proc is { HasExited: false };

    public async Task Start(ServerEntry s, IProgress<string> log)
    {
        if (IsRunning) return;
        SetState("Starting");
        try
        {
            var java = await ResolveJava(s, log);
            ServerProperties.Set(Path.Combine(s.Path, "server.properties"), "server-port", s.Port.ToString());

            var psi = new ProcessStartInfo
            {
                WorkingDirectory = s.Path,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            var jar = Path.Combine(s.Path, s.JarFile);
            if (File.Exists(jar))
            {
                psi.FileName = java;
                foreach (var a in new[] { $"-Xms{s.RamMb}M", $"-Xmx{s.RamMb}M", "-jar", s.JarFile, "nogui" }) psi.ArgumentList.Add(a);
            }
            else if (File.Exists(Path.Combine(s.Path, "run.bat")))
            {
                // Forge/NeoForge servers start through run.bat, which reads RAM from user_jvm_args.txt.
                psi.FileName = "cmd.exe";
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add("run.bat nogui");
                psi.Environment["PATH"] = Path.GetDirectoryName(java) + ";" + Environment.GetEnvironmentVariable("PATH");
            }
            else
            {
                throw new FileNotFoundException($"No {s.JarFile} or run.bat in {s.Path}.");
            }

            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => OnLine(e.Data);
            p.ErrorDataReceived += (_, e) => OnLine(e.Data);
            p.Exited += (_, _) =>
            {
                Output?.Invoke($"[MineServers] Server stopped (exit code {p.ExitCode}).");
                SetState("Stopped");
            };
            Output?.Invoke($"[MineServers] Starting with {java}");
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _proc = p;
        }
        catch
        {
            SetState("Stopped");
            throw;
        }
    }

    void OnLine(string? line)
    {
        if (line == null) return;
        Output?.Invoke(line);
        if (State == "Starting" && line.Contains("Done (")) SetState("Running");
    }

    static async Task<string> ResolveJava(ServerEntry s, IProgress<string> log)
    {
        if (s.JavaPath != null && File.Exists(s.JavaPath)) return s.JavaPath;
        var found = JavaFinder.Pick(JavaFinder.FindAll(), s.JavaMajor);
        if (found != null) return found.Exe;
        return await Downloads.InstallJava(s.JavaMajor > 0 ? s.JavaMajor : 21, log);
    }

    public void Send(string command)
    {
        if (!IsRunning) return;
        _proc!.StandardInput.WriteLine(command);
        _proc.StandardInput.Flush();
    }

    /// <summary>Asks the server to save and stop; forces it closed only if it hangs.</summary>
    public async Task Stop(TimeSpan? grace = null)
    {
        if (!IsRunning) return;
        SetState("Stopping");
        Send("stop");
        using var cts = new CancellationTokenSource(grace ?? TimeSpan.FromSeconds(30));
        try { await _proc!.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            Output?.Invoke("[MineServers] Server didn't stop in time, forcing it closed.");
            try { _proc!.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }

    void SetState(string s)
    {
        State = s;
        StateChanged?.Invoke();
    }
}

public static class ServerProperties
{
    /// <summary>Sets one key in server.properties, keeping every other line as it was.</summary>
    public static void Set(string file, string key, string value)
    {
        var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : new List<string>();
        var i = lines.FindIndex(l => l.StartsWith(key + "=", StringComparison.Ordinal));
        if (i >= 0) lines[i] = $"{key}={value}"; else lines.Add($"{key}={value}");
        File.WriteAllLines(file, lines);
    }

    public static string? Get(string file, string key) =>
        File.Exists(file)
            ? File.ReadLines(file).FirstOrDefault(l => l.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..]
            : null;
}
