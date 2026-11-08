using System.IO;

namespace MineServers.Core;

/// <summary>
/// Reads and writes files the Minecraft server also uses (server.properties, whitelist.json, ...).
/// The server, or antivirus scanning its fresh write, can hold a file for a moment, so wait and retry instead of failing.
/// </summary>
public static class SharedFile
{
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    /// <summary>Reads without locking the server out of the file.</summary>
    public static string ReadAllText(string path) => Retry(() =>
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    });

    public static void WriteAllText(string path, string text) => Retry(() =>
    {
        File.WriteAllText(path, text);
        return true;
    });

    static T Retry<T>(Func<T> action)
    {
        var until = DateTime.UtcNow + Patience;
        while (true)
        {
            try { return action(); }
            catch (IOException e) when (e is not FileNotFoundException and not DirectoryNotFoundException && DateTime.UtcNow < until)
            {
                Thread.Sleep(100);
            }
        }
    }
}
