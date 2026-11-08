using System.IO;
using System.Text.RegularExpressions;

namespace MineServers.Core;

public record JavaInstall(string Exe, int Major)
{
    public override string ToString() => $"Java {Major}  ({Exe})";
}

public static class JavaFinder
{
    /// <summary>Java installs on this PC, found by their "release" file (no need to run java to learn its version).</summary>
    public static List<JavaInstall> FindAll()
    {
        var homes = new List<string>();
        void AddChildren(string? dir)
        {
            if (dir != null && Directory.Exists(dir))
                foreach (var d in Directory.GetDirectories(dir)) { homes.Add(d); homes.AddRange(Directory.GetDirectories(d)); }
        }
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var vendor in new[] { "Java", "Eclipse Adoptium", "Microsoft", "Zulu", "Amazon Corretto", "BellSoft" })
            AddChildren(Path.Combine(pf, vendor));
        AddChildren(Store.JavaDir);
        // Java that SquidServers already downloaded, so imported servers work straight away.
        AddChildren(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "squidservers", "java"));
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (javaHome != null) homes.Add(javaHome);

        return homes.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(h => (exe: Path.Combine(h, "bin", "java.exe"), major: ReadMajor(h)))
            .Where(x => x.major > 0 && File.Exists(x.exe))
            .Select(x => new JavaInstall(x.exe, x.major))
            .OrderByDescending(j => j.Major)
            .ToList();
    }

    static int ReadMajor(string home)
    {
        var release = Path.Combine(home, "release");
        if (!File.Exists(release)) return 0;
        var m = Regex.Match(File.ReadAllText(release), "JAVA_VERSION=\"([0-9._]+)");
        return m.Success ? ParseMajor(m.Groups[1].Value) : 0;
    }

    /// <summary>"1.8.0_511" is Java 8; "21.0.2" is Java 21.</summary>
    internal static int ParseMajor(string version)
    {
        var parts = version.Split('.', '_');
        int.TryParse(parts[0] == "1" && parts.Length > 1 ? parts[1] : parts[0], out var major);
        return major;
    }

    /// <summary>The exact version if installed, else the closest newer one. Minecraft refuses older Java.</summary>
    internal static JavaInstall? Pick(IEnumerable<JavaInstall> installs, int required) =>
        required <= 0
            ? installs.MaxBy(j => j.Major)
            : installs.Where(j => j.Major >= required).MinBy(j => j.Major);

    public static string? JavaExeUnder(string dir) =>
        Directory.Exists(dir) ? Directory.GetFiles(dir, "java.exe", SearchOption.AllDirectories).FirstOrDefault(f => Path.GetFileName(Path.GetDirectoryName(f)) == "bin") : null;
}
