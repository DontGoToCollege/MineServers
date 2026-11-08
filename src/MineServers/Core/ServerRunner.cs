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
        _proc!.StandardInput.WriteLine(command);
        _proc.StandardInput.Flush();
    }

    /// <summary>Asks the server to save and stop; forces it closed only if it hangs.</summary>
    public async Task Stop(TimeSpan? grace = null)
    {
        if (_proc is not { HasExited: false } p) return;
        SetState("Stopping");
        Send("stop");
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
