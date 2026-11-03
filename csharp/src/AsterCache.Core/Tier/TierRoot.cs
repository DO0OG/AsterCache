using AsterCache.Core.Plan;
using AsterCache.Core.Settings;

namespace AsterCache.Core.Tier;

/// SSD 보조 저장의 사본 폴더 선택. Root 가 null 이면 기능을 건너뛰고 Why 가 이유입니다.
public sealed record TierRootChoice(string? Root, string? Why);

public static class TierRoot
{
    public const string AutoFolder = @"AsterCache\tier";

    /// "auto": 시스템 드라이브가 SSD 면 `<시스템드라이브>\AsterCache\tier`, 아니면 다른 SSD 고정 디스크 중 여유가 가장 큰 볼륨, 없으면 건너뜀.
    /// 사용자 지정 경로: SSD 위일 때만 (HDD·알 수 없는 드라이브면 건너뜀). isHdd 는 'X:' 가 HDD 면 true, SSD 면 false, 모르면 null.
    public static TierRootChoice Resolve(SsdTierSettings s, string systemDrive, IEnumerable<string> fixedDrives, Func<string, bool?> isHdd, Func<string, long> freeBytes)
    {
        if (s.Root != SsdTierSettings.AutoRoot)
        {
            string letter = PlanFile.SplitDrive(s.Root).ToUpperInvariant();
            return isHdd(letter) switch
            {
                false => new TierRootChoice(s.Root, null),
                true => new TierRootChoice(null, "custom root is not on an SSD"),
                null => new TierRootChoice(null, "custom root drive type unknown"),
            };
        }

        if (isHdd(systemDrive.ToUpperInvariant()) == false)
        {
            return new TierRootChoice(Combine(systemDrive), null);
        }

        string? best = fixedDrives.Select(d => d.ToUpperInvariant()).Where(d => !string.Equals(d, systemDrive, StringComparison.OrdinalIgnoreCase) && isHdd(d) == false)
            .OrderByDescending(freeBytes).ThenBy(d => d, StringComparer.Ordinal).FirstOrDefault();
        return best is null ? new TierRootChoice(null, "no SSD found") : new TierRootChoice(Combine(best), null);
    }

    private static string Combine(string drive) => drive.ToUpperInvariant() + @"\" + AutoFolder;
}
