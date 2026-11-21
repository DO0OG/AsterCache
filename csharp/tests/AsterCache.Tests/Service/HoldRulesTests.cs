using AsterCache.Core.Json;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 붙들기에서 뺄 파일의 판정 (PrewarmEnv.HoldRules) 과 업데이트 판정 (GameUpdated): 실제 임시 폴더의 표시 파일·크기 기록·계획 파일로
/// 게임이 쓰는 파일이 조건 (크기 기록 · 업데이트 아님 · 순위 목록 뒤 안 바뀜) 을 모두 채울 때만 붙들 수 있고, 모르면 붙들지 않는지 확인합니다.
public sealed class HoldRulesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_holdrules_" + Guid.NewGuid().ToString("N"));
    private readonly string _games;
    private readonly string _written;
    private readonly string _static;
    private readonly GameSettings _game;

    public HoldRulesTests()
    {
        _games = Path.Combine(_dir, "games");
        string plans = Path.Combine(_dir, "plans");
        Directory.CreateDirectory(Path.Combine(_games, "xpak_cache"));
        Directory.CreateDirectory(plans);
        _written = Path.Combine(_games, "xpak_cache", "cache_0_2.xpakdata");
        _static = Path.Combine(_games, "data.bin");
        File.WriteAllBytes(_written, new byte[4096]);
        File.WriteAllBytes(_static, new byte[4096]);
        _game = new("cod", ["cod.exe"], Path.Combine(plans, "cod.prefetch.json"), Path.Combine(plans, "cod.plan.json"), _games, [_games], "GAMES", 60);
        File.WriteAllText(AutoTracer.RawTracePath(_game), "[]");
        File.WriteAllText(AutoTracer.VolatilePath(_game), $"{{\"{Esc(_written)}\": \"opened for write\", \"{Esc(Path.Combine(_games, "updated.bin"))}\": \"changed between runs\"}}");
        File.SetLastWriteTimeUtc(_written, DateTime.UtcNow.AddHours(-1)); // 순위 목록·원시 트레이스보다 이른 수정 시각
        File.SetLastWriteTimeUtc(_static, DateTime.UtcNow.AddHours(-1));
        File.SetLastWriteTimeUtc(AutoTracer.RawTracePath(_game), DateTime.UtcNow.AddMinutes(-10));
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private static string Esc(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);

    // 플레이마다의 크기 (바이트) 기록: 시험 파일이 작아 최소 크기 제한은 0 으로 둠
    private void History(params long[] sizes)
    {
        var history = new WrittenFileHistory();
        for (int i = 0; i < sizes.Length; i++)
        {
            history.Record("s" + i, [(_written, sizes[i], 7)], 0);
        }

        history.Write(WrittenFileHistory.PathFor(AutoTracer.RawTracePath(_game)));
    }

    [Fact]
    public void WithoutARawTraceThereIsNoInformationAndNothingIsHeld()
    {
        File.Delete(AutoTracer.RawTracePath(_game));

        Assert.Null(PrewarmEnv.HoldRules(_game, true, _ => false));
    }

    [Fact]
    public void VolatileFilesStayExcludedWhenTheSettingIsOffAndNonWrittenVolatileFilesAlwaysDo()
    {
        History(4096, 4096, 4096);
        HoldFileRules off = PrewarmEnv.HoldRules(_game, false, _ => false)!;
        HoldFileRules on = PrewarmEnv.HoldRules(_game, true, _ => false)!;

        Assert.Equal("volatile file name", off.Reason(_written)); // 끄면 예전처럼 가변 파일은 붙들지 않음 (cache 로 시작하는 이름 규칙도 걸림)
        Assert.Null(off.IsWritten);
        Assert.Equal("changed between runs", off.Reason(Path.Combine(_games, "updated.bin")));
        Assert.Equal(Prewarmer.HoldWrittenNoHistory, on.Reason(Path.Combine(_games, "updated.bin"))); // 플레이 사이에 바뀐 것으로 표시된 파일도 쓰는 파일: 기록이 없으면 붙들지 않음
        Assert.Equal("volatile file name", on.Reason(Path.Combine(_games, "settings.ini"))); // 이름 규칙만 걸리고 트레이스 표시가 없는 파일은 쓰는 파일이 아님
        Assert.Null(on.Reason(_static)); // 가변이 아닌 파일은 그대로 붙들 수 있음
        Assert.True(on.IsWritten!(_written));
        Assert.False(on.IsWritten(_static));
    }

    [Fact]
    public void AWrittenFileIsHeldOnlyWithEnoughNonShrinkingHistoryAndNoUpdate()
    {
        Assert.Equal(4096, new FileInfo(_written).Length);
        History(4096, 4096); // 기록 둘
        Assert.Equal(Prewarmer.HoldWrittenNoHistory, PrewarmEnv.HoldRules(_game, true, _ => false)!.Reason(_written)); // 세 번 미만

        History(1024, 2048, 4096);
        Assert.Null(PrewarmEnv.HoldRules(_game, true, _ => false)!.Reason(_written)); // 조건 충족

        History(4096, 4096, 8192); // 마지막 기록이 지금 크기보다 큼: 그 뒤 줄어든 것
        Assert.Equal(Prewarmer.HoldWrittenShrank, PrewarmEnv.HoldRules(_game, true, _ => false)!.Reason(_written));
    }

    [Fact]
    public void AFileThatShrankBeforeIsExcludedPermanentlyEvenWhenItGrewAgain()
    {
        History(4096, 2048, 4096, 4096, 4096); // 두 번째 기록에서 줄어듦

        Assert.Equal(Prewarmer.HoldWrittenShrank, PrewarmEnv.HoldRules(_game, true, _ => false)!.Reason(_written));
    }

    [Fact]
    public void AnUpdatedOrUndeterminableGameBlocksTheWrittenFilesAndTheUpdateIsOnlyAskedOnceWhenTheHistoryIsGood()
    {
        History(4096, 4096, 4096);
        int asked = 0;
        Func<GameSettings, bool?> Updated(bool? answer) => _ =>
        {
            asked++;
            return answer;
        };

        Assert.Equal(Prewarmer.HoldWrittenUpdated, PrewarmEnv.HoldRules(_game, true, Updated(true))!.Reason(_written));
        Assert.Equal(Prewarmer.HoldWrittenUpdated, PrewarmEnv.HoldRules(_game, true, Updated(null))!.Reason(_written)); // 판단하지 못하면 업데이트 직후로 봄
        Assert.Null(PrewarmEnv.HoldRules(_game, true, Updated(false))!.Reason(_written));

        asked = 0;
        HoldFileRules rules = PrewarmEnv.HoldRules(_game, true, Updated(false))!;
        _ = rules.Reason(_written);
        _ = rules.Reason(_written);
        Assert.Equal(1, asked); // 파일마다 다시 묻지 않음

        History(); // 기록이 없으면 업데이트 여부는 묻지도 않음
        asked = 0;
        Assert.Equal(Prewarmer.HoldWrittenNoHistory, PrewarmEnv.HoldRules(_game, true, Updated(false))!.Reason(_written));
        Assert.Equal(0, asked);
    }

    [Fact]
    public void AWrittenFileChangedAfterTheRankingWasMadeIsStillExcludedEvenWhenTheConditionsHold()
    {
        History(4096, 4096, 4096);
        string ranked = LoadRefillPlans.RankedPathOf(_game);
        File.WriteAllText(ranked, "{}");
        File.SetLastWriteTimeUtc(ranked, DateTime.UtcNow.AddMinutes(-30));
        File.SetLastWriteTimeUtc(_written, DateTime.UtcNow.AddMinutes(-1)); // 순위 목록을 만든 뒤 게임이 씀

        Assert.Equal("changed since the ranking was made", PrewarmEnv.HoldRules(_game, true, _ => false)!.Reason(_written));
    }

    [Fact]
    public void GameUpdatedComparesThePlanFingerprintsAndSaysUnknownWithoutAPlanOrWithAnUnreadableFile()
    {
        Assert.Null(PrewarmEnv.GameUpdated(_game)); // 계획 없음 = 모름

        Fingerprint Of(string path) => PlanFile.FingerprintOf(path);
        PlanFile Plan(params Fingerprint[] prints) => new(1, _games, "C:", 4096, 65536, new PlanZone(null, 0), [], [], prints, new JObject());
        Plan().Save(_game.Plan);
        Assert.Null(PrewarmEnv.GameUpdated(_game)); // 지문이 없음 = 모름

        Plan(Of(_static), Of(_written)).Save(_game.Plan);
        Assert.False(PrewarmEnv.GameUpdated(_game)); // 모두 같음 (쓰는 파일은 가변 표시라 비교에서 빠짐)

        File.WriteAllBytes(_written, new byte[8192]); // 가변 파일이 바뀌어도 업데이트가 아님
        Assert.False(PrewarmEnv.GameUpdated(_game));

        File.WriteAllBytes(_static, new byte[8192]); // 정적 파일이 바뀜
        Assert.True(PrewarmEnv.GameUpdated(_game));

        File.Delete(_static); // 없어짐도 바뀜
        Assert.True(PrewarmEnv.GameUpdated(_game));
    }
}
