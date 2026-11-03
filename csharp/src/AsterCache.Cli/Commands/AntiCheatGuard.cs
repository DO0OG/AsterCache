using AsterCache.Core.Discovery;
using AsterCache.Core.Json;
using AsterCache.Core.Settings;
using AsterCache.Windows;

namespace AsterCache.Cli.Commands;

/// CLI 에서의 안티치트 규칙: 설정을 읽을 때 Riot 규칙을 채우고 (서비스와 같음), 실시간 트레이스·학습은 금지 종류의 게임이면 세션을 열기 전에 거절합니다. 시험이 바꿔 끼우는 바깥 세계는 아래 두 개.
internal static class AntiCheatGuard
{
    internal static Func<bool> VanguardInstalled { get; set; } = LauncherScan.IsVanguardInstalled;

    /// 폴더 이름으로 본 안티치트 종류. 폴더가 없으면 null, 열거에 실패하면 서비스처럼 "unknown" (모름 = 금지).
    internal static Func<string, string?> FolderKind { get; set; } = root =>
    {
        try
        {
            return Directory.Exists(root) ? AntiCheat.Detect(LauncherScan.ListNames(root, LauncherScan.AntiCheatDepth)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "unknown";
        }
    };

    /// 이 루트와 겹치는 등록 게임의 금지값 (기본 데이터 폴더의 설정, 못 읽으면 null).
    internal static Func<string, string?> RegisteredKind { get; set; } = root =>
    {
        try
        {
            string dir = SettingsStore.DefaultDataDir;
            return Load(Path.Combine(dir, "config.json"), Path.Combine(dir, "plans")).Games.Where(g => Overlaps(g.OriginRoot, root)).Select(g => g.AntiCheat).FirstOrDefault(AntiCheat.ForbidsTrace);
        }
        catch (Exception e) when (e is SettingsException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    };

    // 한쪽이 다른 쪽 폴더 아래이거나 같은 경로인가.
    private static bool Overlaps(string a, string b)
    {
        string x = a.TrimEnd('\\', '/') + "\\", y = b.TrimEnd('\\', '/') + "\\";
        return x.StartsWith(y, StringComparison.OrdinalIgnoreCase) || y.StartsWith(x, StringComparison.OrdinalIgnoreCase);
    }

    internal static AsterSettings Load(string config, string plans) => AntiCheat.WithRiotRule(SettingsStore.Load(config, plans), VanguardInstalled);

    /// 트레이스를 걸면 안 되는 게임이면 거절 결과 (종료 코드 1) 를 쓰고 그 코드를, 아니면 null.
    internal static int? RefuseTrace(TextWriter o, string root, IEnumerable<string> exes)
    {
        string? kind = RegisteredKind(root);
        if (!AntiCheat.ForbidsTrace(kind))
        {
            kind = FolderKind(root);
        }

        if (!AntiCheat.ForbidsTrace(kind))
        {
            kind = AntiCheat.RiotVanguard(root, exes, VanguardInstalled()) ?? kind;
        }

        return AntiCheat.ForbidsTrace(kind)
            ? AsterCli.Emit(o, new JObject { { "error", "anti_cheat" }, { "detail", $"anti-cheat protected game; tracing disabled ({kind})" } }, 1)
            : null;
    }
}
