using System.IO;
using System.IO.Compression;

namespace MineServers.Core;

public record BackupInfo(string Path, long Size)
{
    /// <summary>"2026-11-22_03-00-00_before restore" shows as "2026-11-22 03:00:00  before restore  (120 MB)".</summary>
    public override string ToString()
    {
        var n = System.IO.Path.GetFileNameWithoutExtension(Path);
        if (n.Length < Backups.StampLength) return n;
        var label = n[Backups.StampLength..].TrimStart('_');
        return $"{n[..10]} {n[11..19].Replace('-', ':')}  {(label.Length > 0 ? label + "  " : "")}({Size / 1048576.0:0.#} MB)";
    }
}

/// <summary>Zipped copies of a server (world, settings, plugins), kept in MineServers-Data\Backups\&lt;server id&gt;.
/// Everything here expects the world to be saved (or the server stopped) first; MainWindow takes care of that.</summary>
public static class Backups
{
    // The server downloads or rebuilds these itself, so they'd only waste space.
    static readonly string[] SkipDirs = { "libraries", "versions", "cache", "logs", "crash-reports", ".fabric", "bundler", "debug", UploadDir };
    const string UploadDir = ".mineservers-upload";
    internal const int StampLength = 19; // yyyy-MM-dd_HH-mm-ss

    public static string DirFor(ServerEntry s) => Store.Dir(Path.Combine("Backups", s.Id));

    /// <summary>Newest first. Names start with the time, so name order is time order.</summary>
    public static List<BackupInfo> List(ServerEntry s) =>
        new DirectoryInfo(DirFor(s)).GetFiles("*.zip").OrderByDescending(f => f.Name).Select(f => new BackupInfo(f.FullName, f.Length)).ToList();

    /// <summary>Backs up the whole server folder. Regular backups (no label) also delete the oldest beyond <paramref name="keep"/>;
    /// safety backups ("before restore") don't, so they can't delete the backup that's about to be restored.
    /// Returns the zip's path and the files that couldn't be read.</summary>
    public static (string Path, List<string> Skipped) Create(ServerEntry s, string? label, int keep)
    {
        var dest = Path.Combine(DirFor(s), DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + (label != null ? "_" + label : "") + ".zip");
        var files = Directory.EnumerateFiles(s.Path).Concat(Directory.EnumerateDirectories(s.Path)
            .Where(d => !SkipDirs.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
            .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)));
        var skipped = Zip(s.Path, files, dest);
        if (label == null)
            foreach (var old in List(s).Where(b => Path.GetFileNameWithoutExtension(b.Path).Length == StampLength).Skip(Math.Max(keep, 1)))
                File.Delete(old.Path);
        return (dest, skipped);
    }

    /// <summary>Just the world folders, for sharing or moving to another server.</summary>
    public static List<string> ZipWorld(ServerEntry s, string dest) =>
        Zip(s.Path, WorldDirs(s).SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)), dest);

    /// <summary>Puts a backup back. The current world folders are removed first so chunks made since don't linger.</summary>
    public static void Restore(ServerEntry s, string zipPath)
    {
        foreach (var d in WorldDirs(s)) Directory.Delete(d, true);
        ZipFile.ExtractToDirectory(zipPath, s.Path, overwriteFiles: true); // refuses entries that point outside the folder
    }

    /// <summary>The world folder from server.properties, plus Paper/Spigot's separate Nether and End folders.</summary>
    public static List<string> WorldDirs(ServerEntry s)
    {
        var world = WorldDir(s);
        return new[] { world, world + "_nether", world + "_the_end" }.Where(Directory.Exists).ToList();
    }

    static string WorldDir(ServerEntry s)
    {
        var name = ServerProperties.Get(Path.Combine(s.Path, "server.properties"), "level-name");
        var dir = Path.GetFullPath(Path.Combine(s.Path, string.IsNullOrWhiteSpace(name) ? "world" : name));
        // A level-name like "." would make every world action delete the whole server folder.
        if (!dir.StartsWith(Path.GetFullPath(s.Path).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The world folder name in server.properties (level-name={name}) points outside the server folder.");
        return dir;
    }

    /// <summary>Replaces the world with one from a zip or a folder (another server's, or a singleplayer world from .minecraft\saves).</summary>
    public static void ReplaceWorld(ServerEntry s, string source)
    {
        var temp = Path.Combine(s.Path, UploadDir);
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        try
        {
            var root = source;
            if (File.Exists(source))
            {
                ZipFile.ExtractToDirectory(source, temp);
                root = temp;
            }
            var levelDat = Directory.EnumerateFiles(root, "level.dat", SearchOption.AllDirectories).OrderBy(p => p.Length).FirstOrDefault()
                           ?? throw new InvalidOperationException("There's no Minecraft world in it (no level.dat file).");
            var from = Path.GetDirectoryName(levelDat)!;
            var to = WorldDir(s);
            foreach (var d in WorldDirs(s)) Directory.Delete(d, true);
            foreach (var suffix in new[] { "", "_nether", "_the_end" }) // Paper worlds keep the Nether and End beside the world
            {
                if (!Directory.Exists(from + suffix)) continue;
                if (root == temp) Directory.Move(from + suffix, to + suffix);
                else CopyDir(from + suffix, to + suffix);
            }
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
        }
    }

    /// <summary>Deletes the Nether ("the_nether") or End ("the_end") so the server makes a fresh one. Returns how many folders went.</summary>
    public static int ResetDimension(ServerEntry s, string dimension)
    {
        var world = WorldDir(s);
        var (old, paper) = dimension == "the_nether" ? ("DIM-1", "_nether") : ("DIM1", "_the_end");
        // 26.x keeps dimensions in world\dimensions\minecraft; 1.x in world\DIM-1; Paper 1.x in world_nether.
        var dirs = new[] { Path.Combine(world, "dimensions", "minecraft", dimension), Path.Combine(world, old), world + paper }
            .Where(Directory.Exists).ToList();
        foreach (var d in dirs) Directory.Delete(d, true);
        return dirs.Count;
    }

    /// <summary>Removes the world and sets the seed, so the server makes a new world when it starts.</summary>
    public static void NewWorld(ServerEntry s, string seed)
    {
        var props = Path.Combine(s.Path, "server.properties");
        WorldDir(s); // checks level-name before anything is deleted
        foreach (var d in WorldDirs(s)) Directory.Delete(d, true);
        ServerProperties.Set(props, "level-seed", seed);
    }

    /// <summary>Reads with sharing, so a running server's open files can be copied. Returns files that couldn't be read.</summary>
    static List<string> Zip(string root, IEnumerable<string> files, string dest)
    {
        var skipped = new List<string>();
        var tmp = dest + ".part";
        try { ZipInto(tmp, root, files, skipped); }
        catch
        {
            File.Delete(tmp); // no half-made backup left behind
            throw;
        }
        File.Move(tmp, dest, true);
        return skipped;
    }

    static void ZipInto(string tmp, string root, IEnumerable<string> files, List<string> skipped)
    {
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            foreach (var file in files)
            {
                if (Path.GetFileName(file) == "session.lock") continue; // a running server locks it; it holds nothing
                // Read first, then add: a file locked part-way through must not leave a broken entry in the zip.
                // shortcut: files over 64 MB are streamed straight in, so a lock inside one would still fail the backup.
                Stream data;
                try
                {
                    var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (fs.Length > 64 * 1048576) data = fs;
                    else
                    {
                        using (fs) { data = new MemoryStream(); fs.CopyTo(data); }
                        data.Position = 0;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    skipped.Add(Path.GetRelativePath(root, file));
                    continue;
                }
                using (data)
                {
                    var entry = zip.CreateEntry(Path.GetRelativePath(root, file).Replace('\\', '/'), CompressionLevel.Fastest);
                    entry.LastWriteTime = File.GetLastWriteTime(file);
                    using var dst = entry.Open();
                    data.CopyTo(dst);
                }
            }
        }
    }

    static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            if (Path.GetFileName(file) == "session.lock") continue; // locked if the world is open in Minecraft
            using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var dst = File.Create(Path.Combine(to, Path.GetFileName(file)));
            src.CopyTo(dst);
        }
        foreach (var dir in Directory.EnumerateDirectories(from)) CopyDir(dir, Path.Combine(to, Path.GetFileName(dir)));
    }
}
