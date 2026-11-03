using AsterCache.Core.Discovery;
using AsterCache.Core.Json;
using AsterCache.Core.Settings;
using AsterCache.Windows;

namespace AsterCache.Cli.Commands;

/// CLI 에서의 안티치트 규칙: 설정을 읽을 때 Riot 규칙을 채우고 (서비스와 같음), 실시간 트레이스·학습은 금지 종류의 게임이면 세션을 열기 전에 거절합니다. 시험이 바꿔 끼우는 바깥 세계는 아래 두 개.
internal static class AntiCheatGuard
{
    internal static Func<bool> VanguardInstalled { get; set; } = LauncherScan.IsVanguardInstalled;

    /// 폴더 이름으로 본 안티치트 종류. 폴더가 없으면 null (shortcut: 열거 실패는 거절하지 않음, 직접 지정한 CLI 라 서비스처럼 모름 = 금지로 보지 않음).
    internal static Func<string, string?> FolderKind { get; set; } = root =>
    {
        try
        {
            return Directory.Exists(root) ? AntiCheat.Detect(LauncherScan.ListNames(root, LauncherScan.AntiCheatDepth)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    };

    internal static AsterSettings Load(string config, string plans) => AntiCheat.WithRiotRule(SettingsStore.Load(config, plans), VanguardInstalled);

    /// 트레이스를 걸면 안 되는 게임이면 거절 결과 (종료 코드 1) 를 쓰고 그 코드를, 아니면 null.
    internal static int? RefuseTrace(TextWriter o, string root, IEnumerable<string> exes)
    {
        string? kind = FolderKind(root);
        if (!AntiCheat.ForbidsTrace(kind))
        {
            kind = AntiCheat.RiotVanguard(root, exes, VanguardInstalled()) ?? kind;
        }

        return AntiCheat.ForbidsTrace(kind)
            ? AsterCli.Emit(o, new JObject { { "error", "anti_cheat" }, { "detail", $"anti-cheat protected game; tracing disabled ({kind})" } }, 1)
            : null;
    }
}
