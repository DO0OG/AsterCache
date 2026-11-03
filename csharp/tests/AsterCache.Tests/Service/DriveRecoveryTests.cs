using AsterCache.Core.Volume;
using AsterCache.Service;
using AsterCache.Tests.Windows;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 쓰기 뒤 회복 판정 (DriveRecovery) 과 원시 읽기 표본의 순수 부분 (DiskProbe). 가짜 시계·가짜 표본만 쓰고 실제 볼륨은 읽지 않습니다.
public sealed class DriveRecoveryTests
{
    private const string Drive = @"E:\";
    private static readonly DriveBaseline Both = new(new ProbeSpot(10, 100), new ProbeSpot(20, 80));
    private readonly FakeWorld _w = new();
    private readonly ListLog _log = new();
    private readonly DriveRecovery _r;
    private readonly List<long> _probed = [];
    private double? _zone = 100;
    private double? _ref = 80;
    private readonly List<string> _gameRoots = [];

    public DriveRecoveryTests()
    {
        _w.ProbeMbps = (_, lcn, _) =>
        {
            _probed.Add(lcn);
            return lcn == 10 ? _zone : _ref;
        };
        AutoEnv env = _w.Env();
        env.GameRunningOnDisk = drive => _gameRoots.Any(root => _w.SameDisk(drive, root));
        _r = new DriveRecovery(env, _log);
    }

    private void Pass(int minutes)
    {
        _w.Now += TimeSpan.FromMinutes(minutes);
        _r.Poll();
    }

    [Fact]
    public void TwoConsecutiveGoodSamplesRecover()
    {
        _r.Begin(Drive, Both, TimeSpan.FromMinutes(60));
        Assert.True(_r.IsRecovering(Drive));

        Pass(2);
        Assert.True(_r.IsRecovering(Drive));
        Pass(2);

        Assert.False(_r.IsRecovering(Drive));
        Assert.Contains(_log.Lines, l => l.Contains("drive E: recovered after 4 min", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(90, 72, true)]
    [InlineData(89, 72, false)]
    [InlineData(90, 71, false)]
    public void NinetyPercentOfEverySpotIsTheLine(double zone, double reference, bool recovers)
    {
        (_zone, _ref) = (zone, reference);
        _r.Begin(Drive, Both, TimeSpan.FromMinutes(60));

        Pass(2);
        Pass(2);

        Assert.Equal(!recovers, _r.IsRecovering(Drive));
    }

    [Fact]
    public void ABadSampleBetweenGoodOnesRestartsTheStreak()
    {
        _r.Begin(Drive, Both, TimeSpan.FromMinutes(60));
        Pass(2);
        _zone = 50;
        Pass(2);
        _zone = 100;
        Pass(2);
        Assert.True(_r.IsRecovering(Drive)); // 좋음, 나쁨, 좋음: 연속이 아님

        Pass(2);
        Assert.False(_r.IsRecovering(Drive));
    }

    [Fact]
    public void FirstNotRecoveredSampleLogsOnceWithValuesAndBaselines()
    {
        _zone = 61;
        _ref = 70;
        _r.Begin(Drive, Both, TimeSpan.FromMinutes(60));

        Pass(2);
        Pass(2);

        Assert.Single(_log.Lines, l => l.Contains("drive E: recovering after relocation (zone 61 MB/s / 100 baseline, ref 70/80)", StringComparison.Ordinal));
    }

    [Fact]
    public void LimitEndsTheWaitEvenIfNeverRecovered()
    {
        _zone = 10;
        _r.Begin(Drive, Both, TimeSpan.FromMinutes(10));
        Pass(8);
        Assert.True(_r.IsRecovering(Drive));

        _w.Now += TimeSpan.FromMinutes(2);
        Assert.False(_r.IsRecovering(Drive)); // 상한 시각이면 Poll 전에도 끝
        _r.Poll();
        Assert.Contains(_log.Lines, l => l.Contains("recovery wait ended at the 10 min limit", StringComparison.Ordinal));
        Assert.False(_r.IsRecovering(Drive));
    }

    [Fact]
    public void NoBaselineIsAFixedWaitWithoutAnySample()
    {
        _r.Begin(Drive, null, TimeSpan.FromMinutes(15));

        Pass(14);
        Assert.True(_r.IsRecovering(Drive));
        Pass(1);

        Assert.False(_r.IsRecovering(Drive));
        Assert.Empty(_probed);
    }

    [Fact]
    public void ZeroLimitDoesNotWait()
    {
        _r.Begin(Drive, Both, TimeSpan.Zero);

        Assert.False(_r.IsRecovering(Drive));
    }

    [Fact]
    public void SamplesRotateBy64MibAndNeverReadTheBaselinePlaceAgain()
    {
        _w.ProbeStepLcn = _ => 64; // 가짜 클러스터 1 MiB: 64 MiB = 64 LCN
        _zone = 50; // 회복 못 함: 계속 잼
        _r.Begin(Drive, Both, TimeSpan.FromMinutes(120));

        for (int i = 0; i < 8; i++)
        {
            Pass(2);
        }

        long[] zoneLcns = [.. _probed.Where(l => l is >= 10 and < 10 + 512 && (l - 10) % 64 == 0)];
        Assert.Equal(8, zoneLcns.Length);
        Assert.Equal(3, zoneLcns.Distinct().Count());
        Assert.Equal(3, zoneLcns.Take(3).Distinct().Count()); // 3 번 안에는 같은 시작점이 없음
        Assert.DoesNotContain(10L, zoneLcns); // 기준을 쟀던 0 번 칸 (존 시작) 은 다시 읽지 않음
        Assert.All(zoneLcns, l => Assert.Equal(0, (l - 10) % 64));
        Assert.Equal(2 * 64, zoneLcns.Max() - zoneLcns.Min()); // 시작점이 64 MiB 씩 1..3 칸 (표본 안의 3 곳이 12 칸 안)
    }

    [Fact]
    public void SamplesAboveTheSequentialCeilingAreCacheHitsAndDoNotCount()
    {
        _zone = 400; // 고정 순차 상한 250 을 넘음
        _r.Begin(Drive, Both, TimeSpan.FromMinutes(60));

        Pass(2);
        Pass(2);
        Pass(2);
        Assert.True(_r.IsRecovering(Drive)); // 400 은 90% 이상이지만 캐시 적중이라 회복으로 세지 않음
        Assert.Single(_log.Lines, l => l.Contains("sequential ceiling (disk cache hit)", StringComparison.Ordinal));

        _zone = 100;
        Pass(2);
        Assert.True(_r.IsRecovering(Drive)); // 제외된 회차는 연속에 안 들어감: 좋음 1 회
        Pass(2);
        Assert.False(_r.IsRecovering(Drive));
    }

    [Theory]
    [InlineData(true, true)] // 같은 물리 디스크의 다른 볼륨 (조회 실패도 같은 취급)
    [InlineData(false, false)]
    public void ARecoveringVolumeMakesAnotherVolumeOnTheSameDiskRecoveringToo(bool sameDisk, bool recovering)
    {
        _w.SameDisk = (_, _) => sameDisk;
        _r.Begin(Drive, Both, TimeSpan.FromMinutes(60));

        Assert.True(_r.IsRecovering(Drive));
        Assert.Equal(recovering, _r.IsRecovering("F:"));
    }

    [Fact]
    public void BaselineAboveTheSequentialCeilingIsInvalidSoEverySampleIsUnknown()
    {
        _zone = 400; // 기준 400 은 디스크 순차 상한 250 을 넘는 캐시 적중 의심: 기준 무효
        _ref = 380;
        _r.Begin(Drive, new DriveBaseline(new ProbeSpot(10, 400), new ProbeSpot(20, 380)), TimeSpan.FromMinutes(60));

        Pass(2);
        Pass(2);
        Pass(2);

        Assert.True(_r.IsRecovering(Drive)); // 통과로 세지 않음: 상한 시각까지 계속
        Assert.DoesNotContain(_log.Lines, l => l.Contains("recovered after", StringComparison.Ordinal));
    }

    [Fact]
    public void WithoutAZoneBaselineOnlyTheReferenceIsSampledAndJudged()
    {
        _zone = 1; // 존 값은 무시됨
        _r.Begin(Drive, new DriveBaseline(null, new ProbeSpot(20, 80)), TimeSpan.FromMinutes(60));

        Pass(2);
        Pass(2);

        Assert.False(_r.IsRecovering(Drive));
        Assert.All(_probed, lcn => Assert.Equal(20, lcn));
    }

    [Theory]
    [InlineData(4.0 * (1 << 20))]
    [InlineData(100.0 * (1 << 20))]
    [InlineData(null)]
    public void BusyOrUnreadableDiskSkipsTheSampleAndKeepsTheStreak(double? busy)
    {
        _r.Begin(Drive, Both, TimeSpan.FromMinutes(60));
        Pass(2); // 좋음 1 회
        _w.Disk = busy;
        Pass(2);
        Pass(2);
        Assert.Equal(2, _probed.Count); // 바쁜 회차에는 재지 않음 (첫 회차의 두 곳뿐)

        _w.Disk = 0;
        Pass(2);

        Assert.False(_r.IsRecovering(Drive)); // 건너뛴 회차는 연속을 끊지 않음
    }

    [Fact]
    public void FailedProbeIsSkippedLikeABusyDisk()
    {
        _ref = null;
        _r.Begin(Drive, Both, TimeSpan.FromMinutes(60));

        Pass(2);
        Pass(2);
        Assert.True(_r.IsRecovering(Drive));

        _ref = 80;
        Pass(2);
        Pass(2);
        Assert.False(_r.IsRecovering(Drive));
    }

    [Fact]
    public void DriveKeyIgnoresCaseAndTrailingSeparator()
    {
        _r.Begin("e:", Both, TimeSpan.FromMinutes(60));

        Assert.True(_r.IsRecovering(@"E:\"));
        Assert.True(_r.IsRecovering("e:"));
        Assert.False(_r.IsRecovering(@"D:\"));
    }

    [Fact]
    public void UpdateWatchNeedsABaselineThenFollowsTheSameJudgement()
    {
        TimeSpan limit = TimeSpan.FromMinutes(60);
        Assert.Null(_r.WatchUpdate(Drive, _w.Now, limit)); // 쓰기 전 기준이 없으면 호출하는 쪽의 고정 분

        _r.Begin(Drive, Both, limit);
        Pass(2);
        Pass(2); // 회복 (Begin 의 추적)
        Assert.False(_r.IsRecovering(Drive));

        _w.Now += TimeSpan.FromMinutes(1);
        DateTimeOffset updatedAt = _w.Now;
        _zone = 50; // 업데이트가 흩어 쓴 뒤 느림
        Assert.True(_r.WatchUpdate(Drive, updatedAt, limit));
        _zone = 100;
        Pass(2);
        Pass(2);

        Assert.False(_r.WatchUpdate(Drive, updatedAt, limit)); // 회복 뒤 같은 업데이트로 다시 시작하지 않음
    }

    [Fact]
    public void UpdateWatchOlderThanTheLimitIsNotRecovering()
    {
        _r.Begin(Drive, Both, TimeSpan.FromMinutes(1));
        Pass(2);

        Assert.False(_r.WatchUpdate(Drive, _w.Now - TimeSpan.FromMinutes(90), TimeSpan.FromMinutes(60)));
    }

    // ---- 재시작 복원 (임시 폴더의 auto_status.json) ----

    private (DriveRecovery Recovery, AutoStatus Status) Open(string path)
    {
        var status = new AutoStatus(path, _log, () => _w.Now);
        return (new DriveRecovery(_w.Env(), _log, status), status);
    }

    private static string StatusPath(TempDir dir) => dir.Combine("auto_status.json");

    [Fact]
    public void ProbeTrackSurvivesARestartAndNeedsTwoFreshGoodSamples()
    {
        using var dir = new TempDir();
        (DriveRecovery first, _) = Open(StatusPath(dir));
        first.Begin(Drive, Both, TimeSpan.FromMinutes(60));
        _w.Now += TimeSpan.FromMinutes(2);
        first.Poll(); // 좋음 1 회 (연속 횟수는 저장하지 않음)

        (DriveRecovery second, AutoStatus status) = Open(StatusPath(dir));
        _probed.Clear();

        Assert.True(second.IsRecovering(Drive));
        DriveRecoveryRecord saved = Assert.Single(status.DriveRecoveries());
        Assert.Equal(("E:", "pass", 100.0, 80.0), (saved.Drive, saved.Verdict, saved.Zone!.Value.Mbps, saved.Ref!.Value.Mbps));
        second.Poll(); // 복원 직후 한 번 바로 잼
        Assert.Equal(2, _probed.Count);
        Assert.True(second.IsRecovering(Drive));
        _w.Now += TimeSpan.FromMinutes(2);
        second.Poll();
        Assert.False(second.IsRecovering(Drive));
        Assert.Contains(_log.Lines, l => l.Contains("recovery restored", StringComparison.Ordinal));
    }

    [Fact]
    public void ExpiredTrackIsDroppedOnRestartAndTheFileKeyDisappears()
    {
        using var dir = new TempDir();
        (DriveRecovery first, _) = Open(StatusPath(dir));
        first.Begin(Drive, Both, TimeSpan.FromMinutes(10));
        Assert.Contains("drive_recovery", File.ReadAllText(StatusPath(dir)), StringComparison.Ordinal);
        _w.Now += TimeSpan.FromMinutes(11);

        (DriveRecovery second, AutoStatus status) = Open(StatusPath(dir));

        Assert.False(second.IsRecovering(Drive));
        Assert.Empty(status.DriveRecoveries());
        Assert.DoesNotContain("drive_recovery", File.ReadAllText(StatusPath(dir)), StringComparison.Ordinal);
    }

    [Fact]
    public void FixedWaitWithoutBaselineIsRestoredForTheRemainingTime()
    {
        using var dir = new TempDir();
        (DriveRecovery first, _) = Open(StatusPath(dir));
        first.Begin(Drive, null, TimeSpan.FromMinutes(15));
        _w.Now += TimeSpan.FromMinutes(5);

        (DriveRecovery second, _) = Open(StatusPath(dir));
        _w.Now += TimeSpan.FromMinutes(9);
        second.Poll();
        Assert.True(second.IsRecovering(Drive));
        _w.Now += TimeSpan.FromMinutes(1);
        second.Poll();

        Assert.False(second.IsRecovering(Drive)); // 시작 15 분 뒤
        Assert.Empty(_probed);
    }

    [Fact]
    public void RestoreAfterTheClockJumpedTwoHoursForwardExpiresTheTrack()
    {
        using var dir = new TempDir();
        (DriveRecovery first, _) = Open(StatusPath(dir));
        first.Begin(Drive, Both, TimeSpan.FromMinutes(60));
        _w.Now += TimeSpan.FromHours(2);

        (DriveRecovery second, AutoStatus status) = Open(StatusPath(dir));

        Assert.False(second.IsRecovering(Drive));
        Assert.Empty(status.DriveRecoveries());
    }

    [Fact]
    public void RestoreAfterTheClockJumpedTwoHoursBackCapsTheRemainingTimeAtTheLimit()
    {
        using var dir = new TempDir();
        (DriveRecovery first, _) = Open(StatusPath(dir));
        first.Begin(Drive, Both, TimeSpan.FromMinutes(60));
        _w.Now -= TimeSpan.FromHours(2);

        (DriveRecovery second, AutoStatus status) = Open(StatusPath(dir));

        DriveRecoveryRecord saved = Assert.Single(status.DriveRecoveries());
        Assert.Equal(_w.Now, saved.Started);
        Assert.Equal(TimeSpan.FromMinutes(60), saved.Until - saved.Started);
        _w.Now += TimeSpan.FromMinutes(59);
        Assert.True(second.IsRecovering(Drive));
        _w.Now += TimeSpan.FromMinutes(1);
        Assert.False(second.IsRecovering(Drive));
    }

    [Fact]
    public void RestoreInsideTheWindowKeepsTheSavedUntil()
    {
        using var dir = new TempDir();
        (DriveRecovery first, _) = Open(StatusPath(dir));
        first.Begin(Drive, Both, TimeSpan.FromMinutes(60));
        DateTimeOffset until = _w.Now + TimeSpan.FromMinutes(60);
        _w.Now += TimeSpan.FromMinutes(10);

        (DriveRecovery second, AutoStatus status) = Open(StatusPath(dir));

        DriveRecoveryRecord saved = Assert.Single(status.DriveRecoveries());
        Assert.Equal(until, saved.Until);
        Assert.Equal(_w.Now, saved.Started);
        _w.Now += TimeSpan.FromMinutes(49);
        Assert.True(second.IsRecovering(Drive));
        _w.Now += TimeSpan.FromMinutes(1);
        Assert.False(second.IsRecovering(Drive));
    }

    [Fact]
    public void InvalidSavedBaselineFallsBackToAFixedWaitWithoutProbing()
    {
        using var dir = new TempDir();
        (_, AutoStatus status) = Open(StatusPath(dir));
        status.SetDriveRecovery(new DriveRecoveryRecord("E:", _w.Now, _w.Now + TimeSpan.FromMinutes(30), new ProbeSpot(10, 0), null, null));

        (DriveRecovery restored, _) = Open(StatusPath(dir));
        _w.Now += TimeSpan.FromMinutes(20);
        restored.Poll();

        Assert.True(restored.IsRecovering(Drive));
        Assert.Empty(_probed);
        _w.Now += TimeSpan.FromMinutes(10);
        Assert.False(restored.IsRecovering(Drive));
    }

    [Fact]
    public void RecoveredTrackIsRemovedFromTheFile()
    {
        using var dir = new TempDir();
        (DriveRecovery first, AutoStatus status) = Open(StatusPath(dir));
        first.Begin(Drive, Both, TimeSpan.FromMinutes(60));
        _w.Now += TimeSpan.FromMinutes(2);
        first.Poll();
        _w.Now += TimeSpan.FromMinutes(2);
        first.Poll();

        Assert.Empty(status.DriveRecoveries());
        (DriveRecovery second, _) = Open(StatusPath(dir));
        Assert.False(second.IsRecovering(Drive));
    }

    [Fact]
    public void UnknownSamplesAreNeverPassAndTheLimitStillEndsTheWait()
    {
        using var dir = new TempDir();
        (DriveRecovery r, AutoStatus status) = Open(StatusPath(dir));
        _w.Disk = 100.0 * (1 << 20); // 바쁨: 표본 못 잼
        r.Begin(Drive, Both, TimeSpan.FromMinutes(10));

        for (int i = 0; i < 4; i++)
        {
            _w.Now += TimeSpan.FromMinutes(2);
            r.Poll();
        }

        Assert.True(r.IsRecovering(Drive));
        Assert.Equal("unknown", Assert.Single(status.DriveRecoveries()).Verdict);
        _w.Now += TimeSpan.FromMinutes(2);
        r.Poll();
        Assert.False(r.IsRecovering(Drive)); // 상한 시간이 되면 고정 지연 규칙으로 끝
        Assert.Empty(_probed);
        Assert.Contains(_log.Lines, l => l.Contains("recovery wait ended at the 10 min limit", StringComparison.Ordinal));
    }

    [Fact]
    public void BadSampleIsRecordedAsBad()
    {
        using var dir = new TempDir();
        (DriveRecovery r, AutoStatus status) = Open(StatusPath(dir));
        _zone = 50;
        r.Begin(Drive, Both, TimeSpan.FromMinutes(60));
        _w.Now += TimeSpan.FromMinutes(2);
        r.Poll();

        Assert.Equal("bad", Assert.Single(status.DriveRecoveries()).Verdict);
    }

    [Fact]
    public void CeilingIsOnePointTwoFiveTimesTheLargestValidBaselineCappedAtTwoFifty()
    {
        Assert.Equal(250, _r.CeilingFor(Drive));
        _r.Begin(Drive, Both, TimeSpan.FromMinutes(60));
        _r.NoteWarmMbps(Drive, 1000); // 예열 속도는 상한에 영향이 없음

        Assert.Equal(100 * DriveRecovery.CeilingFactor, _r.CeilingFor("E:"), 3); // 참조 80·존 100 중 큰 쪽
        Assert.Equal(250, _r.CeilingFor(@"D:\"));
        _r.Begin("F:", new DriveBaseline(new ProbeSpot(10, 100), null), TimeSpan.FromMinutes(60));
        Assert.Equal(100 * DriveRecovery.CeilingFactor, _r.CeilingFor("F:"), 3); // 존만 있어도 참조만 있는 때와 같은 상한
    }

    [Theory]
    [InlineData(true)] // 존 기준만
    [InlineData(false)] // 참조 기준만
    public void ASingleBaselineBoundsTheCeilingSymmetricallyAndACacheHitIsUnknown(bool zoneOnly)
    {
        var baseline = zoneOnly ? new DriveBaseline(new ProbeSpot(10, 50), null) : new DriveBaseline(null, new ProbeSpot(20, 50));
        (_zone, _ref) = (180, 180); // 기준 50 인데 캐시 적중 180
        _r.Begin(Drive, baseline, TimeSpan.FromMinutes(60));
        Assert.Equal(62.5, _r.CeilingFor(Drive), 3);

        Pass(12); // SuspectWindow (10 분) 밖
        Pass(2);
        Pass(2);

        Assert.True(_r.IsRecovering(Drive)); // 상한 250 이었다면 PASS 2 회로 회복됐을 값
        Assert.Contains(_log.Lines, l => l.Contains("62 MB/s sequential ceiling (disk cache hit)", StringComparison.Ordinal));

        (_zone, _ref) = (50, 50);
        Pass(2);
        Pass(2);

        Assert.False(_r.IsRecovering(Drive));
    }

    [Fact]
    public void WithoutAnyValidBaselineThereIsNoEarlyRecoveryAndTheFixedWaitRuns()
    {
        _r.Begin(Drive, new DriveBaseline(null, null), TimeSpan.FromMinutes(60));
        Assert.Equal(DriveRecovery.CeilingMbps, _r.CeilingFor(Drive));

        for (int i = 0; i < 10; i++)
        {
            Pass(2);
        }

        Assert.True(_r.IsRecovering(Drive)); // 표본이 몇 번을 좋아도 판정하지 않음
        Pass(60);
        Assert.False(_r.IsRecovering(Drive)); // 상한 (고정 지연) 에서 끝남
    }

    [Fact]
    public void HighBaselineCeilingIsCappedAtTwoFiftyAndAboveItIsUnknown()
    {
        var high = new DriveBaseline(null, new ProbeSpot(20, 230));
        _r.Begin(Drive, high, TimeSpan.FromMinutes(60));
        Assert.Equal(250, _r.CeilingFor(Drive)); // 230 × 1.25 = 287.5 가 아니라 250

        _ref = 300;
        Pass(2);
        Pass(2);

        Assert.True(_r.IsRecovering(Drive)); // 300 은 상한 초과: UNKNOWN 이라 회복으로 세지 않음
    }

    [Fact]
    public void SameSpeedAsTheBaselinePasses()
    {
        _r.Begin(Drive, new DriveBaseline(null, new ProbeSpot(20, 100)), TimeSpan.FromMinutes(60));
        _ref = 100;
        Pass(2);
        Pass(2);

        Assert.False(_r.IsRecovering(Drive));
    }

    [Fact]
    public void SampleAboveOnePointOneTimesTheBaselineRightAfterTheWriteIsSuspectedCache()
    {
        _r.Begin(Drive, new DriveBaseline(null, new ProbeSpot(20, 100)), TimeSpan.FromMinutes(60));
        _ref = 120; // 상한 (125) 아래지만 쓰기 직후 10 분 안에 기준의 1.1 배 초과
        Pass(2);
        Pass(2);
        Assert.True(_r.IsRecovering(Drive)); // UNKNOWN 두 번: 회복 아님
        Assert.Contains(_log.Lines, l => l.Contains("disk cache suspected", StringComparison.Ordinal));

        _ref = 135; // 상한 (125) 초과도 UNKNOWN
        Pass(2);
        Assert.True(_r.IsRecovering(Drive));

        _w.Now += TimeSpan.FromMinutes(6); // 쓴 지 10 분이 지나면 1.1 배 규칙은 끝: 120 은 기준 이상으로 PASS
        _ref = 120;
        _r.Poll();
        _w.Now += TimeSpan.FromMinutes(2);
        _r.Poll();
        Assert.False(_r.IsRecovering(Drive));
    }

    [Fact]
    public void FastPrewarmDoesNotLetADiskCacheHitCountAsRecovered()
    {
        _r.NoteWarmMbps(Drive, 1000);
        _zone = 400; // 디스크 100 인데 표본 400: 기준 100 × 1.25 = 125 초과 → 캐시 적중, UNKNOWN
        _r.Begin(Drive, Both, TimeSpan.FromMinutes(60));

        Pass(2);
        Pass(2);

        Assert.True(_r.IsRecovering(Drive));
        Assert.Single(_log.Lines, l => l.Contains("125 MB/s sequential ceiling (disk cache hit)", StringComparison.Ordinal) && l.Contains("prewarm peak 1000 MB/s", StringComparison.Ordinal));
    }

    [Fact]
    public void RisingSamplesAgainstTheReferenceCeilingRecover()
    {
        _r.Begin(Drive, Both, TimeSpan.FromMinutes(60));
        _zone = 70;
        Pass(2);
        _zone = 95;
        Pass(2);
        Assert.True(_r.IsRecovering(Drive)); // 95 는 좋음 1 회
        _zone = 100;
        Pass(2);

        Assert.False(_r.IsRecovering(Drive));
    }

    // ---- DiskProbe 의 순수 부분 (실제 볼륨을 열지 않음) ----

    [Fact]
    public void SampleReadsThreeSeparatedSpotsOf64MibWithin192Mib()
    {
        const long mib = 1L << 20;
        long[] spots = DiskProbe.SpotOffsets(1000 * mib);

        Assert.Equal(3, spots.Distinct().Count());
        Assert.All(spots.Zip(spots.Skip(1)), p => Assert.True(p.Second - p.First - DiskProbe.Blocks * mib >= 128 * mib)); // 구간 사이 간격 128 MiB 이상
        Assert.Equal(192, DiskProbe.Spots * DiskProbe.Blocks * DiskProbe.BlockMib);
        foreach (int step in Enumerable.Range(0, DiskProbe.RotationSteps))
        {
            long end = DiskProbe.SpotOffsets(step * DiskProbe.Blocks * mib)[^1] + DiskProbe.Blocks * mib;
            Assert.True(end <= DiskProbe.RotationWindows * DiskProbe.Blocks * mib); // 할당·기록을 확인한 영역 안
        }
    }

    [Fact]
    public void RunReadsExactly192BlocksAndReturnsTheMedianRate()
    {
        long clock = 0;
        var reads = new List<long>();
        double? mbps = DiskProbe.Run(0, off =>
        {
            reads.Add(off);
            clock += System.Diagnostics.Stopwatch.Frequency / 640; // 블록마다 1/640 초
            return true;
        }, () => clock, DiskProbe.WallLimit);

        Assert.Equal(192, reads.Count);
        Assert.Equal(192, reads.Distinct().Count());
        Assert.Equal(1048576.0 * 640 / 1e6, mbps!.Value, 3);
    }

    [Fact]
    public void RunGivesUpPastTheWallClockLimitOrOnAFailedRead()
    {
        long clock = 0;
        Assert.Null(DiskProbe.Run(0, _ =>
        {
            clock += System.Diagnostics.Stopwatch.Frequency / 10; // 블록당 0.1 초: 192 블록이면 19 초
            return true;
        }, () => clock, DiskProbe.WallLimit));

        int n = 0;
        Assert.Null(DiskProbe.Run(0, _ => ++n < 70, () => 0, DiskProbe.WallLimit)); // 두 번째 구간 중간에서 읽기 실패
    }

    [Fact]
    public void CancelMidSampleGivesUpAfterTheBlockInFlight()
    {
        using var cts = new CancellationTokenSource();
        var round = new ProbeRound(cts.Token);
        int reads = 0;

        double? mbps = DiskProbe.Run(0, _ =>
        {
            if (++reads == 10)
            {
                cts.Cancel();
            }

            return true;
        }, () => 0, DiskProbe.WallLimit, round);

        Assert.Null(mbps);
        Assert.Equal((10, "cancelled"), (reads, round.Aborted));
        Assert.Equal(10L << 20, round.BytesRead);
    }

    [Fact]
    public void ARoundStopsAt192MibAndZoneAndRefShareIt()
    {
        var spent = new ProbeRound(default) { BytesRead = 190L << 20 };
        int reads = 0;
        Assert.Null(DiskProbe.Run(0, _ => ++reads > 0, () => 0, DiskProbe.WallLimit, spent));
        Assert.Equal((2, "byte limit"), (reads, spent.Aborted));

        var shared = new ProbeRound(default, 2); // 존 + 참조: 구간마다 32 블록씩
        Assert.NotNull(DiskProbe.Run(0, _ => true, () => 0, DiskProbe.WallLimit, shared));
        Assert.NotNull(DiskProbe.Run(0, _ => true, () => 0, DiskProbe.WallLimit, shared));
        Assert.Equal((192L << 20, null), (shared.BytesRead, shared.Aborted));
    }

    [Fact]
    public void RoundWallClockIsSharedAcrossSamples()
    {
        long clock = 0;
        var round = new ProbeRound(default, 2);
        Func<long, bool> read = _ =>
        {
            clock += System.Diagnostics.Stopwatch.Frequency / 1000 * 60; // 블록당 60 ms: 96 블록 = 5.76 s
            return true;
        };

        Assert.NotNull(DiskProbe.Run(0, read, () => clock, DiskProbe.WallLimit, round));
        Assert.Null(DiskProbe.Run(0, read, () => clock, DiskProbe.WallLimit, round)); // 두 번째는 합쳐 10 s 를 넘김 (11.5 s)
        Assert.Equal("wall limit", round.Aborted);
    }

    [Fact]
    public void DiskActivityBetweenZoneAndReferenceSamplesAbortsTheRound()
    {
        _w.ProbeMbps = (_, lcn, _) =>
        {
            _probed.Add(lcn);
            _w.Disk = 10L << 20; // 존을 재는 사이에 다른 읽기가 시작됨
            return _zone;
        };
        _r.Begin(Drive, Both, TimeSpan.FromMinutes(60));

        Pass(2);

        Assert.Equal([10L], _probed); // 참조 표본은 재지 않음
        Assert.True(_r.IsRecovering(Drive));
        Assert.Contains(_log.Lines, l => l.Contains("aborted (disk busy between samples)", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(@"E:\Games\X", true)]
    [InlineData(@"D:\Games\X", false)]
    public void GameStartCancelsOnlySamplesOnTheSameDisk(string gameRoot, bool cancelled)
    {
        _w.ProbeMbps = (_, lcn, _) =>
        {
            _probed.Add(lcn);
            _r.YieldTo(gameRoot); // 존을 재는 중에 게임이 시작됨
            return lcn == 10 ? _zone : _ref;
        };
        _r.Begin(Drive, Both, TimeSpan.FromMinutes(60));

        Pass(2);
        Pass(2);

        Assert.Equal(cancelled, !_probed.Contains(20));
        Assert.Equal(cancelled, _r.IsRecovering(Drive)); // 취소된 회차는 판정 불가라 회복으로 세지 않음
    }

    [Fact]
    public void ARunningGameOnTheDiskKeepsTheSampleFromStartingEvenWhenTheDiskIsIdle()
    {
        _gameRoots.Add(@"E:\Games\X"); // 메뉴·일시정지라 처리량은 0
        _r.Begin(Drive, Both, TimeSpan.FromMinutes(60));

        Pass(2);
        Pass(2);
        Pass(2);

        Assert.Empty(_probed); // 읽기 0 바이트
        Assert.True(_r.IsRecovering(Drive));
        Assert.Single(_log.Lines, l => l.Contains("aborted (game running)", StringComparison.Ordinal)); // 게임이 끝날 때까지 로그는 한 번

        _gameRoots.Clear();
        Pass(2);
        Pass(2);

        Assert.Contains(10L, _probed);
        Assert.False(_r.IsRecovering(Drive));
    }

    [Fact]
    public void AGameOnAnotherPhysicalDiskDoesNotBlockTheSample()
    {
        _gameRoots.Add(@"D:\Games\X");
        _r.Begin(Drive, Both, TimeSpan.FromMinutes(60));

        Pass(2);
        Pass(2);

        Assert.Contains(20L, _probed);
        Assert.False(_r.IsRecovering(Drive));
    }

    [Fact]
    public void AGameStartingAfterTheZoneKeepsTheReferenceUnread()
    {
        _w.ProbeMbps = (_, lcn, _) =>
        {
            _probed.Add(lcn);
            _gameRoots.Add(@"E:\Games\X"); // 존을 잰 직후 게임이 시작됨
            return _zone;
        };
        _r.Begin(Drive, Both, TimeSpan.FromMinutes(60));

        Pass(2);

        Assert.Equal([10L], _probed);
        Assert.True(_r.IsRecovering(Drive));
        Assert.Contains(_log.Lines, l => l.Contains("aborted (game running)", StringComparison.Ordinal));
    }

    private static VolumeBitmap Bits(params (bool Used, int Length)[] runs)
    {
        var bits = new List<bool>();
        foreach ((bool used, int length) in runs)
        {
            bits.AddRange(Enumerable.Repeat(used, length));
        }

        byte[] data = new byte[(bits.Count + 7) / 8];
        for (int i = 0; i < bits.Count; i++)
        {
            if (bits[i])
            {
                data[i >> 3] |= (byte)(1 << (i & 7));
            }
        }

        return new VolumeBitmap(0, bits.Count, data);
    }

    // 1 MiB 클러스터: 표본 영역 768 클러스터 (64 x 12), 존에서 4 GiB = 4096 클러스터 뒤.
    private static readonly VolumeBitmap Map = Bits((true, 800), (false, 2200), (true, 200), (false, 1800), (true, 5000));

    [Fact]
    public void ReferenceIsTheAllocatedRunNearestToFourGibAfterTheZone()
    {
        (bool zoneAllocated, long? reference) = DiskProbe.Locate(Map, 1 << 20, 0);

        Assert.True(zoneAllocated);
        Assert.Equal(5000, reference); // 3000..3200 은 4096 에서 960 칸, 5000.. 은 904 칸
    }

    [Fact]
    public void ZoneThatIsStillFreeIsNotAllocated()
    {
        (bool zoneAllocated, long? reference) = DiskProbe.Locate(Map, 1 << 20, 1000);

        Assert.False(zoneAllocated);
        Assert.Equal(5096, reference); // 목표 1000 + 4096 이 5000.. 구간 안
    }

    [Fact]
    public void ZoneAllocatedButNotWrittenIsNotABaseline()
    {
        // 할당만 되고 첫 4 KiB 가 전부 0 (SMR 이 즉시 0 을 돌려주는 영역): 존도 기준도 무효
        (bool zoneAllocated, long? reference) = DiskProbe.Locate(Map, 1 << 20, 0, _ => false);

        Assert.False(zoneAllocated);
        Assert.Null(reference);
    }

    [Fact]
    public void EveryRotationWindowMustHoldData()
    {
        // 존의 64 MiB 칸 중 하나라도 비어 있으면 (3 번째 칸 = LCN 128) 존 기준 없음, 기준 구간은 그 칸을 안 지나므로 유지
        (bool zoneAllocated, long? reference) = DiskProbe.Locate(Map, 1 << 20, 0, lcn => lcn != 128);

        Assert.False(zoneAllocated);
        Assert.Equal(5000, reference);
    }

    [Fact]
    public void ReferenceIsNullWhenNoRunIsLongEnough()
    {
        Assert.Null(DiskProbe.FindReference(Bits((true, 60), (false, 100), (true, 63)), 0, 64));
        Assert.Null(DiskProbe.FindReference(Bits((true, 1000)), 0, 64, _ => false));
    }

    [Fact]
    public void ReferenceInsideARunStaysAtTheTargetAndNeverCrossesItsEnd()
    {
        VolumeBitmap map = Bits((false, 100), (true, 1000), (false, 100));

        Assert.Equal(600, DiskProbe.FindReference(map, 600, 64));
        Assert.Equal(1036, DiskProbe.FindReference(map, 5000, 64)); // 1100 - 64
        Assert.Equal(100, DiskProbe.FindReference(map, 0, 64));
    }

    [Fact]
    public void MedianAndRate()
    {
        Assert.Equal(2, DiskProbe.Median([3, 1, 2]));
        Assert.Equal(100, DiskProbe.Rate(100_000_000, TimeSpan.FromSeconds(1)), 6);
    }

    // 64 클러스터 할당 구간 20 개가 100 클러스터 빈 구간으로 갈라진 맵 (구간 i 의 시작 = 164 x i).
    private static VolumeBitmap ManyRuns() => Bits([.. Enumerable.Range(0, 20).SelectMany(_ => new (bool, int)[] { (true, 64), (false, 100) })]);

    [Fact]
    public void ReferenceChecksAtMostEightCandidatesNearestFirst()
    {
        var asked = new List<long>();

        long? found = DiskProbe.FindReference(ManyRuns(), 164 * 10, 64, lcn =>
        {
            asked.Add(lcn);
            return false;
        });

        Assert.Null(found);
        Assert.Equal(DiskProbe.MaxReferenceCandidates, asked.Count);
        Assert.Equal(164 * 10, asked[0]); // 목표에 가장 가까운 후보부터
        Assert.True(asked.Zip(asked.Skip(1)).All(p => Math.Abs(p.First - 164 * 10) <= Math.Abs(p.Second - 164 * 10)));
    }

    [Fact]
    public void ReferenceSearchStopsOnCancelAndOnBusyDisk()
    {
        using var cts = new CancellationTokenSource();
        int reads = 0;

        Assert.Throws<OperationCanceledException>(() => DiskProbe.FindReference(ManyRuns(), 0, 64, _ =>
        {
            reads++;
            cts.Cancel();
            return false;
        }, cts.Token));
        Assert.Equal(1, reads); // 취소 뒤에는 다음 후보를 읽지 않음

        int busyReads = 0;
        Assert.Null(DiskProbe.FindReference(ManyRuns(), 0, 64, _ =>
        {
            busyReads++;
            return true;
        }, default, () => true));
        Assert.Equal(0, busyReads); // 바쁘면 읽지 않고 포기
    }

    [Fact]
    public void RecoverySampleRotationNeverReturnsToTheBaselineWindow()
    {
        int[] seq = [.. Enumerable.Range(1, 9).Select(DiskProbe.RotationIndex)];

        Assert.Equal([1, 2, 3, 1, 2, 3, 1, 2, 3], seq);
        Assert.All(seq, i => Assert.InRange(i, 1, DiskProbe.RotationSteps - 1)); // 0 번 칸 = 기준이 읽은 곳
    }
}
