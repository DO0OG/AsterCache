using AsterCache.Core.Settings;

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

        if (leaves.Any(IsKernel))
        {
            return "kernel";
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

    /// 설정을 읽을 때: anticheat 가 비어 있는 항목 (수동 등록한 리그 등) 에 Riot 규칙을 적용해 채웁니다. 트레이스·고정·티어가 모두 이 값을 봅니다. Vanguard 확인은 한 번만 합니다.
    public static AsterSettings WithRiotRule(AsterSettings s, Func<bool> vanguardInstalled)
    {
        bool? installed = null;
        return s with { Games = [.. s.Games.Select(g => g.AntiCheat is null && RiotVanguard(g.OriginRoot, g.ExeNames, installed ??= vanguardInstalled()) is { } kind ? g with { AntiCheat = kind } : g)] };
    }

    /// ETW 트레이스를 걸면 안 되는 종류인가: 커널 수준 안티치트 (eac·battleye·vanguard·kernel) 와 탐지 실패 (unknown). 등록 정보에 쓰인 값도 같은 규칙입니다.
    public static bool ForbidsTrace(string? kind) => kind is "eac" or "battleye" or "vanguard" or "kernel" or "unknown";

    /// 파일 고정 (pin) 을 하면 안 되는 종류: 트레이스 금지 종류 전부와 리코셰 (보호 드라이버가 열린 핸들을 문제 삼을 수 있음). 리코셰는 실측에서 트레이스가 문제없어 트레이스는 허용합니다. SSD 티어는 안티치트가 있으면 종류와 상관없이 거절.
    public static bool ForbidsPin(string? kind) => ForbidsTrace(kind) || kind == "ricochet";

    private static bool IsEac(string n) =>
        Same(n, "EasyAntiCheat") || Starts(n, "EasyAntiCheat_EOS") || (Starts(n, "EasyAntiCheat") && Ends(n, ".exe")) || Same(n, "start_protected_game.exe");

    private static bool IsBattlEye(string n) =>
        Same(n, "BattlEye") || (Starts(n, "BEService") && Ends(n, ".exe")) || (Starts(n, "BEClient") && Ends(n, ".dll"));

    // vgc/vgk 는 짧아서 파일 이름이 그 글자로 시작하는 .exe/.sys 만 인정 (다른 이름 속 우연한 일치를 피함)
    private static bool IsVanguard(string n) =>
        ((Starts(n, "vgc") || Starts(n, "vgk")) && (Ends(n, ".exe") || Ends(n, ".sys"))) || Has(n, "Riot Vanguard");

    // 그 밖의 커널·상시 보호 안티치트 (BlackCipher, GameGuard, XIGNCODE3, Wellbia, HackShield, mhyprot, Denuvo, FACEIT): 종류를 따로 가리지 않고 "kernel" 하나로 묶음. 정확한 이름만 인정해 우연한 일치를 피함
    private static bool IsKernel(string n) =>
        Same(n, "BlackCipher") || (Starts(n, "BlackCall") && Ends(n, ".aes"))
        || Same(n, "GameGuard") || (Starts(n, "npgg") && Ends(n, ".dll")) || Same(n, "GameMon.des")
        || Same(n, "xigncode") || Same(n, "XIGNCODE3") || Same(n, "x3.xem") || Starts(n, "xcorona")
        || Same(n, "uncheater.exe") || Same(n, "wellbia")
        || Same(n, "HShield")
        || (Starts(n, "mhyprot") && Ends(n, ".sys"))
        || Starts(n, "denuvo-anti-cheat") || Starts(n, "faceit");

    private static bool Same(string n, string v) => n.Equals(v, StringComparison.OrdinalIgnoreCase);

    private static bool Starts(string n, string v) => n.StartsWith(v, StringComparison.OrdinalIgnoreCase);

    private static bool Ends(string n, string v) => n.EndsWith(v, StringComparison.OrdinalIgnoreCase);

    private static bool Has(string n, string v) => n.Contains(v, StringComparison.OrdinalIgnoreCase);
}
