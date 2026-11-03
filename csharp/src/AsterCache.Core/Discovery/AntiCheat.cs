namespace AsterCache.Core.Discovery;

/// 설치 폴더에 남은 안티치트 흔적 이름으로 종류를 짐작합니다. 이름만 보고 (파일을 열지 않음) 모르면 null.
public static class AntiCheat
{
    /// 설치 폴더 아래 두 단계까지의 항목 이름 (폴더·파일 구분 없이, 경로가 섞여 있으면 마지막 조각만 봄). 규칙 순서의 첫 일치를 돌려줍니다.
    public static string? Detect(IEnumerable<string> names)
    {
        List<string> leaves = names.Select(n => n.TrimEnd('\\', '/')).Select(n => n[(n.LastIndexOfAny(['\\', '/']) + 1)..]).Where(n => n.Length > 0).ToList();
        if (leaves.Any(IsEac))
        {
            return "eac";
        }

        if (leaves.Any(IsBattlEye))
        {
            return "battleye";
        }

        if (leaves.Any(IsVanguard))
        {
            return "vanguard";
        }

        if (leaves.Any(n => Has(n, "ricochet") || Same(n, "randgrid.sys")))
        {
            return "ricochet";
        }

        return leaves.Any(n => new[] { "anticheat", "anti-cheat", "xigncode", "nprotect", "gameguard" }.Any(p => Has(n, p))) ? "unknown" : null;
    }

    private static readonly string[] RiotExes =
    [
        "RiotClientServices.exe", "Riot Client.exe", "LeagueClient.exe", "League of Legends.exe", "VALORANT.exe", "VALORANT-Win64-Shipping.exe", "TFTClient-Win64-Shipping.exe",
    ];

    /// Vanguard 는 게임 폴더 밖 (Program Files\Riot Vanguard) 에 있어 폴더 이름으로는 안 잡힘: Riot 게임 (설치 경로가 "Riot Games" 폴더 아래이거나 exe 가 Riot 클라이언트·리그·발로란트) 이고
    /// 시스템에 Vanguard (vgk/vgc 서비스 또는 폴더) 가 있으면 "vanguard", 아니면 null.
    public static string? RiotVanguard(string originRoot, IEnumerable<string> exeNames, bool vanguardInstalled) =>
        vanguardInstalled && (originRoot.Split('\\', '/').Any(s => Same(s, "Riot Games")) || exeNames.Any(e => RiotExes.Any(r => Same(e, r)))) ? "vanguard" : null;

    /// ETW 트레이스를 걸면 안 되는 종류인가: 커널 수준 안티치트 (eac·battleye·vanguard) 와 탐지 실패 (unknown). 등록 정보에 쓰인 값도 같은 규칙입니다.
    public static bool ForbidsTrace(string? kind) => kind is "eac" or "battleye" or "vanguard" or "unknown";

    private static bool IsEac(string n) =>
        Same(n, "EasyAntiCheat") || Starts(n, "EasyAntiCheat_EOS") || (Starts(n, "EasyAntiCheat") && Ends(n, ".exe")) || Same(n, "start_protected_game.exe");

    private static bool IsBattlEye(string n) =>
        Same(n, "BattlEye") || (Starts(n, "BEService") && Ends(n, ".exe")) || (Starts(n, "BEClient") && Ends(n, ".dll"));

    // vgc/vgk 는 짧아서 파일 이름이 그 글자로 시작하는 .exe/.sys 만 인정 (다른 이름 속 우연한 일치를 피함)
    private static bool IsVanguard(string n) =>
        ((Starts(n, "vgc") || Starts(n, "vgk")) && (Ends(n, ".exe") || Ends(n, ".sys"))) || Has(n, "Riot Vanguard");

    private static bool Same(string n, string v) => n.Equals(v, StringComparison.OrdinalIgnoreCase);

    private static bool Starts(string n, string v) => n.StartsWith(v, StringComparison.OrdinalIgnoreCase);

    private static bool Ends(string n, string v) => n.EndsWith(v, StringComparison.OrdinalIgnoreCase);

    private static bool Has(string n, string v) => n.Contains(v, StringComparison.OrdinalIgnoreCase);
}
