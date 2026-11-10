using AsterCache.Core.I18n;
using AsterCache.Tray;
using Xunit;

namespace AsterCache.Tests.Tray;

/// 상태 열의 "SSD 보조 N GB" 표시 (서비스는 영어 상수 "ssd tier N GB" 를 detail 에 쓰고, 관리 창이 번역).
public sealed class TierStatusTextTests
{
    [Fact]
    public void SsdTierDetailShowsTheAmountInBothLanguagesNextToOtherTags()
    {
        var done = new AutoGame("auto", "gog", "done", "relocated (profile), ssd tier 2.5 GB, prewarmed");

        Assert.Equal("완료 (재배치됨(프로필), SSD 보조 2.5 GB, 예열됨)", AutoStatusFile.StageText(done, "ko"));
        Assert.Equal("done (relocated (profile), SSD tier 2.5 GB, prewarmed)", AutoStatusFile.StageText(done, "en"));
        Assert.Equal("재배치 자동 진행 대기 (SSD 보조 8.0 GB)", AutoStatusFile.StageText(new AutoGame("auto", "gog", "waiting_idle", "ssd tier 8.0 GB"), "ko"));
        Assert.Equal("done", AutoStatusFile.StageText(done with { Detail = null }, "en"));
    }

    [Fact]
    public void BothLanguagesHaveTheKeysAndTheSamePlaceholders()
    {
        foreach (string key in new[] { "Stage_SsdTier", "Auto_SsdTier", "Auto_SsdTierBudget", "Err_TierBudget" })
        {
            Assert.True(L.Table.ContainsKey(key));
        }

        Assert.Equal("SSD 보조 1.0 GB", L.Get("ko", "Stage_SsdTier", "1.0"));
    }
}
