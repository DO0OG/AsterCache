using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 물리 디스크 활동 판정의 순수 부분: 인스턴스 이름 매핑·합산·캐시. 실제 PDH 는 부르지 않습니다.
public sealed class DiskActivityTests
{
    private const double Mib = 1 << 20;

    private static readonly Dictionary<string, double> Instances = new()
    {
        ["0 C: D:"] = 80 * Mib,
        ["1 E:"] = 0,
        ["_Total"] = 80 * Mib,
    };

    private static bool Busy(string letter, IReadOnlyDictionary<string, double> instances) =>
        DiskActivity.Sum(letter, instances) is not { } bps || bps >= DiskActivity.BusyBytesPerSecond;

    [Fact]
    public void OtherVolumeActivityOnTheSameDiskHoldsBackTheIdleVolume()
    {
        Assert.True(Busy("D:", Instances)); // C: 가 읽는 중이면 같은 디스크의 D: 도 바쁨
        Assert.True(Busy("C:", Instances));
        Assert.False(Busy("E:", Instances)); // 다른 디스크는 진행
    }

    [Fact]
    public void UnmappedVolumeCountsAsBusy()
    {
        Assert.Null(DiskActivity.Sum("Z:", Instances));
        Assert.True(Busy("Z:", Instances));
        Assert.True(Busy("D:", new Dictionary<string, double> { ["0"] = 0, ["_Total"] = 0 })); // 문자 없는 인스턴스만 있으면 모름
    }

    [Fact]
    public void SpannedVolumeSumsEveryDisk()
    {
        var spanned = new Dictionary<string, double> { ["1 F:"] = 3 * Mib, ["2 F:"] = 3 * Mib, ["3 G:"] = 0 };
        Assert.Equal(6 * Mib, DiskActivity.Sum("f:", spanned));
        Assert.True(Busy("F:", spanned)); // 각각 4 MiB/s 미만이어도 합이 넘으면 바쁨
        Assert.False(Busy("G:", spanned));
    }

    // 수집 루프는 돌리지 않고 (start 가 아무것도 안 함) Refresh 를 직접 불러 표본 시점을 제어한다.
    private static DiskActivity.PhysicalSampler Manual(Func<IReadOnlyDictionary<string, double>?> sample, Func<long> now) => new(sample, now, _ => { });

    [Fact]
    public void ReadsTheLatestSampleUntilItGoesStaleThenIsUnknown()
    {
        long now = 0;
        int taken = 0;
        var sampler = Manual(() => { taken++; return Instances; }, () => now);
        Assert.Null(sampler.BytesPerSecond("C:")); // 표본이 아직 없음
        sampler.Refresh();
        Assert.Equal(80 * Mib, sampler.BytesPerSecond("C:"));
        Assert.Equal(0, sampler.BytesPerSecond("E:"));
        now = 3999;
        Assert.Equal(80 * Mib, sampler.BytesPerSecond("D:")); // 읽기만 하고 다시 잡지 않음
        Assert.Equal(1, taken);
        now = 4000;
        Assert.Null(sampler.BytesPerSecond("D:")); // 낡음
        sampler.Refresh();
        Assert.Equal(80 * Mib, sampler.BytesPerSecond("D:"));
    }

    [Fact]
    public void BeforeTheFirstSampleIsWarmingAndBusyUntilTheGiveUpLimitThenBroken()
    {
        long now = 0;
        var sampler = Manual(() => Instances, () => now);
        Assert.Equal(T(null, DiskActivity.SampleState.Warming), sampler.Read("E:"));
        Assert.True(sampler.BusyUnlessBroken("E:")); // 아직 모름 = 바쁨
        now = 59_999;
        Assert.Equal(DiskActivity.SampleState.Warming, sampler.Read("E:").State);
        now = 60_000; // 수집 스레드가 끝내 표본을 못 냄: 영구 보류하지 않음
        Assert.Equal(T(null, DiskActivity.SampleState.Broken), sampler.Read("E:"));
        Assert.False(sampler.BusyUnlessBroken("E:"));
    }

    [Fact]
    public void StaleIsBusyAndBecomesBrokenPastTheGiveUpLimit()
    {
        long now = 0;
        var sampler = Manual(() => Instances, () => now);
        sampler.Refresh();
        Assert.Equal(T(0.0, DiskActivity.SampleState.Ok), sampler.Read("E:"));
        now = 4000;
        Assert.Equal(T(null, DiskActivity.SampleState.Stale), sampler.Read("E:"));
        Assert.True(sampler.BusyUnlessBroken("E:")); // 낡음 = 보류
        now = 59_999;
        Assert.True(sampler.BusyUnlessBroken("E:"));
        now = 60_000;
        Assert.Equal(DiskActivity.SampleState.Broken, sampler.Read("E:").State);
        Assert.False(sampler.BusyUnlessBroken("E:")); // 수집이 죽었어도 영구히 막지 않음
        sampler.Refresh(); // 수집이 되살아나면 다시 값으로
        Assert.True(sampler.BusyUnlessBroken("C:"));
        Assert.Equal(DiskActivity.SampleState.Ok, sampler.Read("E:").State);
    }

    [Fact]
    public void GameProtectionKeepsAStalledSampleBusyPastTheGiveUpLimitWhileOtherCallersStillGiveUp()
    {
        long now = 0;
        var log = new ListLog();
        var sampler = new DiskActivity.PhysicalSampler(() => Instances, () => now, _ => { }, () => log);
        Assert.True(sampler.BusyUnlessBroken("E:", keepStalled: true)); // 첫 표본 전
        now = 60_000;
        Assert.False(sampler.BusyUnlessBroken("E:")); // 다른 호출자: 기존 그대로 Broken = 바쁘지 않음
        Assert.Equal(DiskActivity.SampleState.Broken, sampler.Read("E:").State);
        Assert.True(sampler.BusyUnlessBroken("E:", keepStalled: true)); // 게임 보호: 정체는 고장이 아님
        Assert.Equal(T(null, DiskActivity.SampleState.Warming), sampler.Read("E:", keepStalled: true));

        sampler.Refresh();
        now = 64_000;
        Assert.Equal(DiskActivity.SampleState.Stale, sampler.Read("E:", keepStalled: true).State);
        now = 125_000; // 마지막 표본에서 60 초 넘게 정체
        Assert.Equal(DiskActivity.SampleState.Broken, sampler.Read("E:").State);
        Assert.Equal(T(null, DiskActivity.SampleState.Stale), sampler.Read("E:", keepStalled: true));
        Assert.True(sampler.BusyUnlessBroken("E:", keepStalled: true));
        Assert.Equal(2, log.Lines.Count(l => l.Contains("samples stalled", StringComparison.Ordinal))); // 정체 진입마다 경고 한 번 (첫 표본 전 정체, 새 표본 뒤 다시 정체)
    }

    [Fact]
    public void AGenuinelyBrokenCounterIsNotKeptBusyEvenForGameProtection()
    {
        var sampler = Manual(() => null, () => 0);
        sampler.Refresh();
        Assert.False(sampler.BusyUnlessBroken("E:", keepStalled: true)); // 고장은 기존대로 영구히 막지 않음
    }

    [Fact]
    public void ABrokenCounterIsNotBusyButAValueIs()
    {
        bool fail = true;
        var sampler = Manual(() => fail ? null : Instances, () => 0);
        sampler.Refresh();
        Assert.Equal(T(null, DiskActivity.SampleState.Broken), sampler.Read("E:"));
        Assert.False(sampler.BusyUnlessBroken("E:")); // 고장은 기존대로 바쁘지 않음 (영구히 막지 않음)
        fail = false;
        sampler.Refresh();
        Assert.False(sampler.BusyUnlessBroken("E:"));
        Assert.True(sampler.BusyUnlessBroken("C:"));
        Assert.False(sampler.BusyUnlessBroken("Z:")); // 매핑 없음도 고장 (기존 동작)
    }

    [Fact]
    public async Task ValueAndStateComeFromOneSnapshotEvenWhenASampleIsPublishedConcurrently()
    {
        // 바쁜 첫 표본이 게시되는 순간과 겹쳐도 한가함 (바쁘지 않음) 으로 읽히는 호출이 없어야 한다.
        var sampler = Manual(() => Instances, () => 0);
        Task publish = Task.Run(sampler.Refresh);
        for (int i = 0; i < 100_000; i++)
        {
            Assert.True(sampler.BusyUnlessBroken("C:"));
        }

        await publish;
    }

    [Fact]
    public void TheNextWaitSubtractsTheTimeTheSampleTook()
    {
        long now = 1000;
        long took = 1000;
        var sampler = Manual(() => { now += took; return Instances; }, () => now);
        Assert.Equal(1000, sampler.Tick()); // 수집 1 초 + 대기 1 초 = 주기 2 초
        took = 0;
        Assert.Equal(2000, sampler.Tick());
        took = 2500; // 주기보다 오래 걸리면 바로 다음 수집
        Assert.Equal(0, sampler.Tick());
    }

    [Fact]
    public void AThrowingLogDoesNotStopTheSamplerAndTheNextSampleIsTaken()
    {
        int taken = 0;
        bool fail = true;
        var sampler = new DiskActivity.PhysicalSampler(() => { taken++; return fail ? throw new InvalidOperationException() : Instances; }, () => 0, _ => { }, () => new ThrowingLog());
        sampler.Tick(); // 수집 예외를 기록하다 로그가 던져도 밖으로 나가지 않음
        Assert.Equal(T(null, DiskActivity.SampleState.Broken), sampler.Read("D:"));
        fail = false;
        sampler.Tick();
        Assert.Equal(2, taken);
        Assert.Equal(DiskActivity.SampleState.Ok, sampler.Read("D:").State);
    }

    private static (double?, DiskActivity.SampleState) T(double? bps, DiskActivity.SampleState state) => (bps, state);

    private sealed class ThrowingLog : AsterCache.Core.Logging.ILog
    {
        public void Write(AsterCache.Core.Logging.LogLevel level, string message, Exception? exception = null) => throw new IOException("broken pipe");
    }

    [Fact]
    public void FailedSampleMeansUnknownAndTheNextRefreshRecovers()
    {
        bool fail = true;
        var sampler = Manual(() => fail ? null : Instances, () => 0);
        sampler.Refresh();
        Assert.Null(sampler.BytesPerSecond("D:"));
        fail = false;
        sampler.Refresh();
        Assert.Equal(80 * Mib, sampler.BytesPerSecond("D:"));
    }

    [Fact]
    public void ThrowingSampleIsAFailedSampleNotACrash()
    {
        var sampler = Manual(() => throw new InvalidOperationException(), () => 0);
        sampler.Refresh();
        Assert.Null(sampler.BytesPerSecond("D:"));
    }

    [Fact]
    public async Task SlowSamplingNeverBlocksTheCaller()
    {
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        Task? collector = null;
        DiskActivity.PhysicalSampler? sampler = null;
        // 수집을 한 번만 하는 작업을 주입: 끝나면 풀어 주어 스레드가 남지 않는다.
        sampler = new DiskActivity.PhysicalSampler(() => { entered.Set(); release.Wait(); return Instances; }, () => 0, _ => collector = Task.Run(() => sampler!.Refresh()));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(sampler.BytesPerSecond("C:")); // 첫 호출이 수집을 시작하지만 기다리지 않음
        Assert.Null(sampler.BytesPerSecond("C:"));
        Assert.True(sw.ElapsedMilliseconds < 500, $"{sw.ElapsedMilliseconds} ms");
        Assert.True(entered.Wait(5000)); // 수집은 백그라운드에서 실제로 시작됨
        release.Set();
        await collector!.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1.0)]
    public void InvalidValueMakesTheVolumeUnknownAndSaysSo(double value)
    {
        var bad = new Dictionary<string, double> { ["0 D:"] = value, ["1 E:"] = 0 };
        Assert.Null(DiskActivity.Sum("D:", bad, out string? reason));
        Assert.Equal(DiskActivity.ValueInvalid, reason);
        Assert.True(Busy("D:", bad));
    }

    [Fact]
    public void OnlyAnUnnumberedInstanceIsUnmapped()
    {
        Assert.Null(DiskActivity.Sum("D:", new Dictionary<string, double> { ["0"] = 0 }, out string? reason));
        Assert.Equal(DiskActivity.InstanceUnmapped, reason);
    }
}
