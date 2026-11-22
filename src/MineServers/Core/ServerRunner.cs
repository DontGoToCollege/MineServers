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
    public DateTime StartedAt { get; private set; }
    /// <summary>The last run ended without anyone asking it to stop.</summary>
    public bool CrashedWhileRunning { get; private set; }
    // shortcut: for run.bat servers this is cmd.exe, so CPU/memory miss the java child; read the child process if Forge users need it.
    public Process? Process => IsRunning ? _proc : null;

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
                psi.ArgumentList.Add($"-Xms{s.RamMb}M");
                psi.ArgumentList.Add($"-Xmx{s.RamMb}M");
                if (s.AikarFlags) foreach (var a in AikarFlags(s.RamMb)) psi.ArgumentList.Add(a);
                foreach (var a in new[] { "-jar", s.JarFile, "nogui" }) psi.ArgumentList.Add(a);
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
            p.Exited += (_, _) => Finished(p);
            Output?.Invoke($"[MineServers] Starting with {java}");
            _proc = p;
            StartedAt = DateTime.Now;
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }
        catch
        {
            _proc = null; // a process that never started must not count as running
            SetState("Stopped");
            throw;
        }
    }

    /// <summary>Aikar's G1 garbage-collector flags (mcflags.emc.gs), with the bigger-heap values above 12 GB.
    /// G1RSetUpdatingPauseIntervalMillis is left out: Java 8 and 25 refuse to start with it.</summary>
    internal static string[] AikarFlags(int ramMb)
    {
        bool big = ramMb >= 12 * 1024;
        return new[]
        {
            "-XX:+UseG1GC", "-XX:+ParallelRefProcEnabled", "-XX:MaxGCPauseMillis=200", "-XX:+UnlockExperimentalVMOptions",
            "-XX:+DisableExplicitGC", "-XX:+AlwaysPreTouch", $"-XX:G1NewSizePercent={(big ? 40 : 30)}", $"-XX:G1MaxNewSizePercent={(big ? 50 : 40)}",
            $"-XX:G1HeapRegionSize={(big ? 16 : 8)}M", $"-XX:G1ReservePercent={(big ? 15 : 20)}", "-XX:G1HeapWastePercent=5",
            "-XX:G1MixedGCCountTarget=4", $"-XX:InitiatingHeapOccupancyPercent={(big ? 20 : 15)}", "-XX:G1MixedGCLiveThresholdPercent=90",
            "-XX:SurvivorRatio=32", "-XX:+PerfDisableSharedMem", "-XX:MaxTenuringThreshold=1",
            "-Dusing.aikars.flags=https://mcflags.emc.gs", "-Daikars.new.flags=true",
        };
    }

    readonly object _gate = new();

    /// <summary>Reports a run's end exactly once, whether Stop() or the exit event gets here first.
    /// An older run finishing late can't mark a newer one as stopped.</summary>
    void Finished(Process p)
    {
        lock (_gate)
        {
            if (_proc != p) return;
            _proc = null;
        }
        CrashedWhileRunning = State == "Running"; // a stop request sets Stopping first
        Output?.Invoke($"[MineServers] Server stopped (exit code {p.ExitCode}).");
        SetState("Stopped");
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
        if (command.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase) && State != "Stopping") SetState("Stopping");
        _proc!.StandardInput.WriteLine(command);
        _proc.StandardInput.Flush();
    }

    /// <summary>Asks the server to save and stop; forces it closed only if it hangs.</summary>
    public async Task Stop(TimeSpan? grace = null)
    {
        if (_proc is not { HasExited: false } p) return;
        Send("stop"); // also marks it Stopping
        using var cts = new CancellationTokenSource(grace ?? TimeSpan.FromSeconds(30));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            Output?.Invoke("[MineServers] Server didn't stop in time, forcing it closed.");
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await p.WaitForExitAsync();
        }
        Finished(p); // so a restart can begin straight away
    }

    void SetState(string s)
    {
        State = s;
        StateChanged?.Invoke();
    }
}

public static class ServerProperties
{
    /// <summary>Sets several keys in one write.</summary>
    public static void SetMany(string file, IReadOnlyDictionary<string, string> values)
    {
        var lines = File.Exists(file) ? Lines(file) : new List<string>();
        foreach (var (key, value) in values)
        {
            var i = lines.FindIndex(l => l.StartsWith(key + "=", StringComparison.Ordinal));
            var line = $"{key}={Escape(value)}";
            if (i >= 0) lines[i] = line; else lines.Add(line);
        }
        SharedFile.WriteAllText(file, string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }

    // Java .properties escaping, which the server uses: "minecraft\:normal", "§" for non-ASCII.
    internal static string Escape(string value) => string.Concat(value.Select(c => c switch
    {
        '\\' => @"\\",
        ':' => @"\:",
        '=' => @"\=",
        > '~' or < ' ' => $"\\u{(int)c:x4}",
        _ => c.ToString(),
    }));

    internal static string Unescape(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, @"\\(u[0-9a-fA-F]{4}|.)", m =>
            m.Groups[1].Value.Length == 5 ? ((char)Convert.ToInt32(m.Groups[1].Value[1..], 16)).ToString() : m.Groups[1].Value);

    public static void Set(string file, string key, string value) => SetMany(file, new Dictionary<string, string> { [key] = value });

    public static string? Get(string file, string key) => ReadAll(file).GetValueOrDefault(key);

    /// <summary>Every key=value line (comments skipped). Empty if the server hasn't made the file yet.</summary>
    public static Dictionary<string, string> ReadAll(string file) =>
        File.Exists(file)
            ? Lines(file).Where(l => !l.StartsWith('#') && l.Contains('='))
                .GroupBy(l => l[..l.IndexOf('=')]).ToDictionary(g => g.Key, g => Unescape(g.Last()[(g.Key.Length + 1)..]))
            : new();

    static List<string> Lines(string file)
    {
        var text = SharedFile.ReadAllText(file).ReplaceLineEndings("\n").TrimEnd('\n');
        return text.Length == 0 ? new() : text.Split('\n').ToList();
    }
}
