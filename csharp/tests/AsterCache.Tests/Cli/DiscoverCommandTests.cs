using AsterCache.Cli;
using AsterCache.Cli.Commands;
using AsterCache.Core.Json;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Cli;

/// 이 PC 의 런처 상태에 기대지 않도록 실제 열거는 부르지 않고 인자 해석과 출력 형식만 봅니다.
public sealed class DiscoverCommandTests
{
    private static readonly DiscoveredGame[] Games =
    [
        new("Fast_Game", "steam", @"C:\Steam\steamapps\common\FastGame", @"C:\Steam\steamapps\common\FastGame\fast.exe", false, "steam.exe") { AntiCheat = "eac" },
        new("Unknown", "gog", @"D:\GOG\Unknown", null, null, "GalaxyClient.exe"),
    ];

    private static (int Code, string Out) Run(string[] args)
    {
        var o = new StringWriter();
        int code = DiscoverCommand.Run(ArgParser.Parse(DiscoverCommand.Options, args), o, Games);
        return (code, o.ToString());
    }

    [Fact]
    public void JsonHasTheGamesArrayWithFixedKeys()
    {
        (int code, string text) = Run(["--json"]);

        Assert.Equal(0, code);
        var rows = (List<object?>)((JObject)JsonRead.Parse(text)!)["games"]!;
        Assert.Equal(2, rows.Count);
        var first = (JObject)rows[0]!;
        Assert.Equal(["name", "launcher", "install_dir", "exe", "on_hdd", "trigger_exe", "anticheat"], first.Select(kv => kv.Key));
        Assert.Equal(false, first["on_hdd"]);
        var second = (JObject)rows[1]!;
        Assert.Null(second["exe"]);
        Assert.Null(second["on_hdd"]);
        Assert.Equal("eac", first["anticheat"]);
        Assert.Null(second["anticheat"]);
        Assert.Single(text.TrimEnd().Split('\n')); // 한 줄 JSON
    }

    [Fact]
    public void RiotUninstallAndXboxLaunchersAreWrittenAsIs()
    {
        DiscoveredGame[] games =
        [
            new("League_of_Legends", "riot", @"D:\Riot Games\League of Legends", @"D:\Riot Games\League of Legends\LeagueClient.exe", true, "RiotClientServices.exe") { AntiCheat = "vanguard" },
            new("Big_Game", "uninstall", @"D:\Apps\Big", @"D:\Apps\Big\big.exe", true, null),
            new("Forza", "xbox", @"D:\XboxGames\Forza\Content", @"D:\XboxGames\Forza\Content\f.exe", false, null),
        ];
        var json = new StringWriter();
        DiscoverCommand.Run(ArgParser.Parse(DiscoverCommand.Options, ["--json"]), json, games);
        var table = new StringWriter();
        DiscoverCommand.Run(ArgParser.Parse(DiscoverCommand.Options, []), table, games);

        var rows = (List<object?>)((JObject)JsonRead.Parse(json.ToString())!)["games"]!;
        Assert.Equal(["riot", "uninstall", "xbox"], rows.Select(r => ((JObject)r!)["launcher"]));
        Assert.Equal(["name", "launcher", "install_dir", "exe", "on_hdd", "trigger_exe", "anticheat"], ((JObject)rows[1]!).Select(kv => kv.Key));
        Assert.Null(((JObject)rows[2]!)["trigger_exe"]);
        string[] lines = table.ToString().TrimEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Equal("League_of_Legends\triot\thdd\t" + games[0].ExePath + "\tvanguard", lines[0]);
        Assert.Equal("Forza\txbox\tssd\t" + games[2].ExePath + "\t-", lines[2]);
    }

    [Fact]
    public void EmptyListGivesEmptyArray()
    {
        var o = new StringWriter();
        DiscoverCommand.Run(ArgParser.Parse(DiscoverCommand.Options, ["--json"]), o, []);

        Assert.Equal("{\"games\":[]}", o.ToString().Trim());
    }

    [Fact]
    public void TableHasOneLinePerGame()
    {
        (int code, string text) = Run([]);

        Assert.Equal(0, code);
        string[] lines = text.TrimEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Equal(2, lines.Length);
        Assert.Equal("Fast_Game\tsteam\tssd\t" + Games[0].ExePath + "\teac", lines[0]);
        Assert.Equal("Unknown\tgog\t?\t" + Games[1].InstallDir + "\t-", lines[1]);
    }

    [Fact]
    public void UnknownOptionIsAnArgumentError()
    {
        (int code, _, string err) = CliTests.Run("discover", "--bogus");

        Assert.Equal(2, code);
        Assert.Contains("--bogus", err);
    }

    [Fact]
    public void HelpListsTheJsonOption()
    {
        (int code, string text, _) = CliTests.Run("discover", "--help");

        Assert.Equal(0, code);
        Assert.Contains("--json", text);
    }
}
