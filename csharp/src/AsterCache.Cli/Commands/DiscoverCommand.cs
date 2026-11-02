using AsterCache.Core.Json;
using AsterCache.Windows;

namespace AsterCache.Cli.Commands;

/// 런처 (Steam, Epic, GOG, Ubisoft) 에 설치된 게임 목록 (읽기 전용, 관리자 불필요). 아무것도 실행하거나 기록하지 않습니다.
internal static class DiscoverCommand
{
    public static readonly ArgOption[] Options = [new("json", ArgKind.Flag)];

    public static int Run(ParsedArgs a, TextWriter o) => Run(a, o, LauncherScan.Scan());

    internal static int Run(ParsedArgs a, TextWriter o, IReadOnlyList<DiscoveredGame> games)
    {
        if (a.Flag("json"))
        {
            return AsterCli.Emit(o, new JObject { { "games", games.Select(Row).ToList() } });
        }

        foreach (DiscoveredGame g in games)
        {
            o.WriteLine($"{g.Name}\t{g.Launcher}\t{(g.OnHdd is bool h ? (h ? "hdd" : "ssd") : "?")}\t{g.ExePath ?? g.InstallDir}\t{g.AntiCheat ?? "-"}");
        }

        return 0;
    }

    private static object? Row(DiscoveredGame g) => new JObject
    {
        { "name", g.Name },
        { "launcher", g.Launcher },
        { "install_dir", g.InstallDir },
        { "exe", g.ExePath },
        { "on_hdd", g.OnHdd },
        { "trigger_exe", g.TriggerExe },
        { "anticheat", g.AntiCheat },
    };
}
