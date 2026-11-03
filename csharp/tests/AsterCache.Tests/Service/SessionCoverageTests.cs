using AsterCache.Core.Json;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 게임 종료 때 남기는 존 커버리지 진단: 계산, pid 별 읽기량, auto_status.json 의 coverage_percent 왕복.
public sealed class SessionCoverageTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);
    private readonly AutoRig _rig = new();
    private readonly string _path = Path.Combine(Path.GetTempPath(), "aster_cov_" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        _rig.Dispose();
        File.Delete(_path);
    }

    [Theory]
    [InlineData(1000L, 250L, 25)]
    [InlineData(100L, 500L, 100)]
    [InlineData(300L, 0L, 0)]
    public void PercentIsHotShareCappedAt100(long read, long hot, int expected) =>
        Assert.Equal(expected, SessionCoverage.Percent(read, hot));

    [Theory]
    [InlineData(null, 5L)]
    [InlineData(0L, 5L)]
    public void NoReadsOrZeroReadsOmitThePercent(long? read, long hot) =>
        Assert.Null(SessionCoverage.Percent(read, hot));

    [Fact]
    public void LineUsesWholeMebibytes()
    {
        Assert.Equal("foo: session read 1024 MiB, zone covers 256 MiB (25%)", SessionCoverage.Line("foo", 1024L << 20, 256L << 20, 25));
    }

    [Fact]
    public void SessionReadsAreThePerProcessLastValueSummed()
    {
        var reads = new SessionReads();
        reads.Observe("foo", 1, 100);
        reads.Observe("foo", 1, 300); // 같은 pid 는 최신 값으로 덮어씀
        reads.Observe("foo", 2, 50);
        reads.Observe("bar", 9, 7);

        Assert.Equal(350L, reads.Take("foo"));
        Assert.Null(reads.Take("foo")); // 꺼낸 뒤에는 잊음
        Assert.Null(reads.Take("none"));
        Assert.Equal(7L, reads.Take("bar"));
    }

    [Fact]
    public void RecordedCoverageIsSerializedAndOldFilesStillLoad()
    {
        var s = new AutoStatus(_path, _rig.Log, () => T0);
        s.Set("foo", "auto", null, AutoStage.Done, null);
        s.RecordCoverage("foo", 40);

        var game = (JObject)((JObject)((JObject)JsonRead.ReadFile(_path)!)["games"]!)["foo"]!;
        Assert.Equal(40L, game["coverage_percent"]);
        Assert.Equal(40, new AutoStatus(_path, _rig.Log).Get("foo")!.CoveragePercent);

        File.WriteAllText(_path, "{\"games\": {\"foo\": {\"stage\": \"done\", \"at\": \"2026-11-04T09:00:00+00:00\"}}}");
        Assert.Null(new AutoStatus(_path, _rig.Log).Get("foo")!.CoveragePercent);
    }

    [Fact]
    public void LowCoverageShowsPartialInTheDetailOfADoneGameAndHigherCoverageClearsIt()
    {
        var s = new AutoStatus(_path, _rig.Log, () => T0);
        s.Set("foo", "auto", null, AutoStage.Done, AutoRelocator.RelocatedProfile);
        s.Set("bar", "auto", null, AutoStage.WaitingIdle, "trace 1/3");

        s.RecordCoverage("foo", 12);
        s.RecordCoverage("bar", 12);

        Assert.Equal("partial (12%)", s.Get("foo")!.Detail);
        Assert.Equal(AutoStage.Done, s.Get("foo")!.Stage);
        Assert.Equal("trace 1/3", s.Get("bar")!.Detail); // 완료 전 게임의 표시는 그대로

        s.RecordCoverage("foo", AutoStatus.PartialBelowPercent);

        Assert.Null(s.Get("foo")!.Detail);
    }
}
