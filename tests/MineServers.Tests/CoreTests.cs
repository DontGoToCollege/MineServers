using System.IO;
using System.Text.Json;
using MineServers.Core;
using Xunit;

namespace MineServers.Tests;

public class CoreTests
{
    [Theory]
    [InlineData("1.8.0_511", 8)]
    [InlineData("21.0.2", 21)]
    [InlineData("25", 25)]
    public void ParsesJavaMajor(string version, int major) => Assert.Equal(major, JavaFinder.ParseMajor(version));

    [Fact]
    public void PicksExactJavaThenClosestNewer()
    {
        var javas = new[] { new JavaInstall("a", 8), new JavaInstall("b", 21), new JavaInstall("c", 25) };
        Assert.Equal(21, JavaFinder.Pick(javas, 21)!.Major);
        Assert.Equal(21, JavaFinder.Pick(javas, 17)!.Major);
        Assert.Equal(25, JavaFinder.Pick(javas, 0)!.Major);
        Assert.Null(JavaFinder.Pick(javas, 26));
    }

    [Theory]
    [InlineData("\u001b[32m INFO\u001b[0m bore_cli::client: listening at bore.pub:41234", @"(bore\.pub:\d+)", "bore.pub:41234")]
    [InlineData("t=2026 lvl=info msg=\"started tunnel\" url=tcp://0.tcp.eu.ngrok.io:12345", @"url=tcp://([\w.-]+:\d+)", "0.tcp.eu.ngrok.io:12345")]
    [InlineData("   tcp://rnxyz-1-2-3-4.a.free.pinggy.link:40123", @"tcp://([\w.-]+:\d+)", "rnxyz-1-2-3-4.a.free.pinggy.link:40123")]
    [InlineData("Your server is at my-tunnel.example.net:25565 now", ProcessTunnel.AnyAddress, "my-tunnel.example.net:25565")]
    public void FindsTunnelAddress(string line, string pattern, string expected) =>
        Assert.Equal(expected, ProcessTunnel.FindAddress(line, pattern));

    [Fact]
    public void IgnoresLinesWithoutAddress() =>
        Assert.Null(ProcessTunnel.FindAddress("Connecting to bore.pub...", @"(bore\.pub:\d+)"));

    [Theory]
    [InlineData("25565", true)]
    [InlineData("80, 25565", true)]
    [InlineData("25560-25570", true)]
    [InlineData("25566", false)]
    [InlineData("*", false)]
    [InlineData(null, false)]
    public void ReadsFirewallPortLists(string? ports, bool has) => Assert.Equal(has, Firewall.PortListHas(ports, 25565));

    [Theory]
    [InlineData("[20:22:47] [Server thread/INFO]: Disconnecting DontGoToCollege (/192.168.1.226:49766): You are not whitelisted on this server!", "DontGoToCollege")]
    [InlineData("[12:00:00] [Server thread/INFO]: Disconnecting Steve_2 (/10.0.0.5:5000): You are not white-listed on this server!", "Steve_2")]
    [InlineData("[20:22:47] [Server thread/INFO]: DontGoToCollege (/192.168.1.226:49766) lost connection: You are not whitelisted on this server!", null)]
    [InlineData("[20:22:47] [Server thread/INFO]: Disconnecting Alex (/1.2.3.4:5): Timed out", null)]
    public void SpotsPlayersTurnedAwayByWhitelist(string line, string? name) => Assert.Equal(name, Players.RejectedName(line));

    [Fact]
    public void ReadsWhitelistNames()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "whitelist.json"), """[{"uuid":"1","name":"zed"},{"uuid":"2","name":"Amy"}]""");
        Assert.Equal(new[] { "Amy", "zed" }, Players.Allowed(dir));
        Assert.Empty(Players.Banned(dir));
        Directory.Delete(dir, true);
    }

    [Theory]
    [InlineData("DontGoToCollege", true)]
    [InlineData("ab", false)]
    [InlineData("bob; stop", false)]
    public void ChecksPlayerNames(string name, bool ok) => Assert.Equal(ok, Players.IsValidName(name));

    [Fact]
    public async Task WaitsForServerToReleaseLockedProperties()
    {
        var f = Path.GetTempFileName();
        File.WriteAllLines(f, new[] { "white-list=true", "server-port=25565" });
        var locked = new FileStream(f, FileMode.Open, FileAccess.ReadWrite, FileShare.None); // like a server mid-save
        _ = Task.Delay(700).ContinueWith(_ => locked.Dispose());

        await Task.Run(() => ServerProperties.Set(f, "white-list", "false"));

        Assert.Equal(new[] { "white-list=false", "server-port=25565" }, File.ReadAllLines(f));
        File.Delete(f);
    }

    [Fact]
    public void ReadsPropertiesWhileServerHasThemOpenForWriting()
    {
        var f = Path.GetTempFileName();
        File.WriteAllText(f, "white-list=true\n");
        using (new FileStream(f, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            Assert.Equal("true", ServerProperties.Get(f, "white-list"));
        File.Delete(f);
    }

    [Fact]
    public void SetsServerPropertyKeepingOtherLines()
    {
        var f = Path.GetTempFileName();
        File.WriteAllLines(f, new[] { "#comment", "motd=Hi", "server-port=25565" });
        ServerProperties.Set(f, "server-port", "25570");
        ServerProperties.Set(f, "online-mode", "true");
        Assert.Equal(new[] { "#comment", "motd=Hi", "server-port=25570", "online-mode=true" }, File.ReadAllLines(f));
        Assert.Equal("Hi", ServerProperties.Get(f, "motd"));
        File.Delete(f);
    }

    [Fact]
    public void ImportsSquidServersListSkippingKnownAndMissing()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var a = Directory.CreateDirectory(Path.Combine(root, "A")).FullName;
        var b = Directory.CreateDirectory(Path.Combine(root, "B")).FullName;
        File.WriteAllText(Path.Combine(a, "server.properties"), "server-port=25570\n");
        var list = Path.Combine(root, "servers.json");
        File.WriteAllText(list, JsonSerializer.Serialize(new object[]
        {
            new { name = "A", serverPath = a, serverType = "paper", version = "26.3", ram = 8192 },
            new { name = "B", serverPath = b, serverType = "vanilla", version = "26.2", ram = 4096 },
            new { name = "Gone", serverPath = Path.Combine(root, "missing"), serverType = "vanilla" },
        }));

        var found = SquidImport.Read(list, new[] { new ServerEntry { Path = b + "\\" } });

        var s = Assert.Single(found);
        Assert.Equal(("A", "paper", "26.3", 8192, 25570), (s.Name, s.Type, s.Version, s.RamMb, s.Port));
        Directory.Delete(root, true);
    }

    [Fact]
    public void MakesUniqueSafeFolderNames()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        Directory.CreateDirectory(Path.Combine(root, "My Server"));
        Assert.Equal(Path.Combine(root, "My Server (2)"), Store.UniqueFolder(root, "My Server"));
        Assert.Equal(Path.Combine(root, "ab"), Store.UniqueFolder(root, "a:b?"));
        Directory.Delete(root, true);
    }
}
