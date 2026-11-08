using AsterCache.Core.I18n;
using AsterCache.Core.Metrics;
using AsterCache.Tray;
using Xunit;

namespace AsterCache.Tests.Tray;

/// 성과 판정 열 (V2-09, 읽기 전용): 기록 없음, 실측 안 됨, 거부를 구분합니다.
[Collection("Language")]
public sealed class BenchmarkGateTests
{
    private static LoadSample S(string game, Arm arm, long? ready, long? probe = null) =>
        new(game, Scenario.S0, arm, ElapsedBucket.Sec0, ready, GameBuild: "b1", CacheState: CacheState.Warm, ProbeReadyMs: probe, ElapsedBasis: "service_uptime");

    [Fact]
    public void NoRecordWhenTheGameHasNoSample()
    {
        List<LoadSample> samples = [S("Bar", Arm.Baseline, null, 5)];

        Assert.Equal(L.T("Gate_NoRecord"), ManageViewModel.GateText(samples, "Foo"));
        Assert.Equal(L.T("Gate_NoRecord"), ManageViewModel.GateText([], "Foo"));
    }

    [Fact]
    public void ProbeOnlyRecordsAreUnverified()
    {
        List<LoadSample> samples = [.. Enumerable.Range(0, 6).Select(_ => S("Foo", Arm.Baseline, null, 5))];

        Assert.StartsWith(L.T("Gate_Unverified", string.Empty), ManageViewModel.GateText(samples, "Foo"));
    }

    [Fact]
    public void WorseCandidateIsRejected()
    {
        List<LoadSample> samples = [.. Enumerable.Range(0, 5).Select(_ => S("Foo", Arm.Baseline, 10)), .. Enumerable.Range(0, 5).Select(_ => S("Foo", Arm.Candidate, 100))];

        Assert.StartsWith(L.T("Gate_Reject", string.Empty), ManageViewModel.GateText(samples, "Foo"));
    }

    [Fact]
    public void EnglishGateTextHasNoHangul()
    {
        string saved = L.Current;
        L.Current = L.En;
        try
        {
            List<LoadSample> samples = [.. Enumerable.Range(0, 5).Select(_ => S("Foo", Arm.Baseline, 10)), .. Enumerable.Range(0, 5).Select(_ => S("Foo", Arm.Candidate, 100))];
            string rejected = ManageViewModel.GateText(samples, "Foo");
            string few = ManageViewModel.GateText([S("Foo", Arm.Baseline, 10)], "Foo");

            Assert.StartsWith("rejected: ", rejected);
            Assert.StartsWith("not measured: too few samples", few);
            Assert.DoesNotMatch(@"[가-힣]", rejected);
            Assert.DoesNotMatch(@"[가-힣]", few);
        }
        finally
        {
            L.Current = saved;
        }
    }

    [Fact]
    public void UnknownScenarioIsUnverifiedNotRejected()
    {
        List<LoadSample> baseline = [.. Enumerable.Range(0, 5).Select(_ => new LoadSample("Foo", Scenario.Unknown, Arm.Baseline, ElapsedBucket.Sec0, 10, GameBuild: "b1", CacheState: CacheState.Warm))];
        List<LoadSample> candidate = [.. Enumerable.Range(0, 5).Select(_ => new LoadSample("Foo", Scenario.Unknown, Arm.Candidate, ElapsedBucket.Sec0, 100, GameBuild: "b1", CacheState: CacheState.Warm))];

        Assert.Equal("mixed_conditions", ResultGate.Evaluate(baseline, candidate).ReasonCode);
        Assert.StartsWith(L.T("Gate_Unverified", string.Empty), ManageViewModel.GateText([.. baseline, .. candidate], "Foo"));
    }

    [Fact]
    public void BrokenSampleFileReadsAsEmpty()
    {
        string path = Path.Combine(Path.GetTempPath(), "ac-gate-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "[broken");
        try
        {
            Assert.Empty(ManageViewModel.LoadSamples(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SampleFileLockedWhileReadingThrowsSoCallerKeepsOldSamples()
    {
        string path = Path.Combine(Path.GetTempPath(), "ac-gate-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{"samples": [{"game_key": "Foo", "scenario": "S0", "arm": "Baseline", "elapsed": "Sec0", "cache_state": "Warm", "elapsed_basis": "service_uptime"}]}""");
        try
        {
            using (var hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.ThrowsAny<IOException>(() => ManageViewModel.LoadSamples(path)); // 쓰는 쪽이 잠그는 동안은 읽기 실패 (ReloadAuto 가 직전 값을 유지)
            }

            Assert.Single(ManageViewModel.LoadSamples(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
