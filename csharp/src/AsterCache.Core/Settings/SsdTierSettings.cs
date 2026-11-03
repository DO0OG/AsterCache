namespace AsterCache.Core.Settings;

/// SSD 보조 저장 (auto.ssd_tier, 선택 기능): 재배치한 HDD 게임의 핫 파일을 SSD 사본으로 두고 원래 자리에 심볼릭 링크를 둡니다. 기본 꺼짐.
/// budget_gib 는 게임마다 쓸 수 있는 SSD 용량의 상한 (GiB), root 는 "auto" (SSD 를 골라 `<드라이브>\AsterCache\tier`) 또는 SSD 위의 절대 경로.
public sealed record SsdTierSettings(bool Enabled, int BudgetGib, string Root)
{
    public const string AutoRoot = "auto";

    public static SsdTierSettings Default { get; } = new(false, 8, AutoRoot);
}
