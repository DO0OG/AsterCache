using AsterCache.Core.Apply;
using AsterCache.Core.Plan;
using AsterCache.Service;
using AsterCache.Tests.Core;
using Xunit;

namespace AsterCache.Tests.Service;

/// 다른 프로그램의 작은 읽기를 바쁨으로 보는 부하 환산과, 재배치가 쓰기 조각 사이에서 쉬었다가 이어 가는 규칙. 실제 PDH 는 부르지 않습니다.
public sealed class RelocationRestTests : ApplyTestBase
{
    private const double Mib = 1 << 20;
    private readonly ListLog _log = new();
    private readonly List<string> _events = [];
    private DateTimeOffset _now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
    private DiskActivity.SampleState _state = DiskActivity.SampleState.Ok;
    private int _busyObservations; // 앞으로 이만큼의 관측은 바쁨

    // 시계는 sleep 으로만 흐르고, 관측 한 번은 멈춤 1 초.
    private RelocationRest Rest() => new("g", _log,
        () =>
        {
            _now += TimeSpan.FromSeconds(1);
            bool busy = _busyObservations-- > 0;
            _events.Add(busy ? "observe busy" : "observe idle");
            return (busy ? 100 * Mib : 0, _state); // 바쁨은 100 MiB/s 로, 아니면 한가함
        },
        (span, _) =>
        {
            _now += span;
            _events.Add("rest");
        },
        () => _now);

    [Fact]
    public void SmallScatteredReadsOnAHardDiskCountAsBusyEvenAtLowThroughput()
    {
        // 64 KiB 를 초당 30회 (약 2 MiB/s) 읽는 다른 프로세스: 쉬는 시간 비율이 23~64 로 나옴 (실측)
        double load = DiskActivity.Load(2 * Mib, 50, seeks: true);
        Assert.True(DiskActivity.IsBusy(load, DiskActivity.SampleState.Ok));
        Assert.True(DiskActivity.IsBusy(DiskActivity.Load(0.3 * Mib, 0.3, true), DiskActivity.SampleState.Ok)); // 흩어진 읽기만 하는 프로세스 하나 (실측 3.8 MiB/s, 쉼 0.3%)
    }

    [Fact]
    public void AnIdleDiskOrAnSsdOrAMissingIdleRatioKeepsTheThroughputRule()
    {
        Assert.False(DiskActivity.IsBusy(DiskActivity.Load(0.1 * Mib, 98, true), DiskActivity.SampleState.Ok)); // 한가한 하드디스크
        Assert.False(DiskActivity.IsBusy(DiskActivity.Load(3 * Mib, 4, seeks: false), DiskActivity.SampleState.Ok)); // SSD 는 쉬는 시간 비율을 쓰지 않음
        Assert.False(DiskActivity.IsBusy(DiskActivity.Load(2 * Mib, null, true), DiskActivity.SampleState.Ok)); // 비율을 못 읽으면 처리량만
        Assert.Equal(80 * Mib, DiskActivity.Load(80 * Mib, 0, true)); // 처리량이 이미 높으면 값은 그대로
        Assert.True(DiskActivity.IsBusy(DiskActivity.Load(4 * Mib, 100, true), DiskActivity.SampleState.Ok)); // 처리량 기준은 그대로
    }

    [Fact]
    public void DoesNotObserveWhileTheDriveHasBeenIdleWithinTheObserveInterval()
    {
        RelocationRest rest = Rest();

        Assert.True(rest.MayIssue(CancellationToken.None));
        _now += TimeSpan.FromSeconds(29);
        Assert.True(rest.MayIssue(CancellationToken.None));

        Assert.Empty(_events); // 한가할 때의 속도를 늦추지 않음: 간격 안에서는 멈추지 않음
        _now += TimeSpan.FromSeconds(1);
        Assert.True(rest.MayIssue(CancellationToken.None));
        Assert.Equal(["observe idle"], _events);
        Assert.Empty(_log.Lines);
    }

    [Fact]
    public void RestsWhileOtherActivityShowsThenResumesWhenTheDriveIsIdleAndLogsEachChangeOnce()
    {
        RelocationRest rest = Rest();
        _now += RelocationRest.ObserveEvery;
        _busyObservations = 2;

        Assert.True(rest.MayIssue(CancellationToken.None));

        Assert.Equal(["observe busy", "rest", "observe busy", "rest", "observe idle"], _events);
        Assert.Collection(_log.Lines,
            l => Assert.Equal("Info: g: relocation yields to other disk activity (resting between pieces)", l),
            l => Assert.Equal("Info: g: relocation resumes after 10.0 s of rest", l));
        _events.Clear();
        Assert.True(rest.MayIssue(CancellationToken.None)); // 방금 한가함을 봤으니 다음 간격까지 관측 없음
        Assert.Empty(_events);
    }

    [Fact]
    public void StopsTheRoundAfterRestingLongerThanTheLimit()
    {
        RelocationRest rest = Rest();
        _now += RelocationRest.ObserveEvery;
        _busyObservations = int.MaxValue;

        Assert.False(rest.MayIssue(CancellationToken.None));

        Assert.Contains(_log.Lines, l => l.Contains("relocation stops this round after", StringComparison.Ordinal));
    }

    [Fact]
    public void StopsRightAwayWhenCancelledWhileRestingOrBeforeLikeAGameStart()
    {
        using var cts = new CancellationTokenSource();
        int rests = 0;
        var rest = new RelocationRest("g", _log, () => (100 * Mib, DiskActivity.SampleState.Ok), (_, _) => { if (++rests == 2) { cts.Cancel(); } }, () => _now);
        _now += RelocationRest.ObserveEvery;

        Assert.False(rest.MayIssue(cts.Token)); // 쉬던 중 취소: 다음 관측을 기다리지 않고 멈춤
        Assert.Equal(2, rests);
        Assert.False(Rest().MayIssue(cts.Token)); // 이미 취소됐으면 관측도 하지 않음
        Assert.Empty(_events);
    }

    [Fact]
    public void AnUnknownMeasurementDoesNotBlockTheRelocation()
    {
        RelocationRest rest = Rest();
        _now += RelocationRest.ObserveEvery;
        _state = DiskActivity.SampleState.Warming; // 카운터를 못 읽음: 고장 때문에 재배치를 영구히 막지 않음
        _busyObservations = int.MaxValue;

        Assert.True(rest.MayIssue(CancellationToken.None));
        Assert.Empty(_log.Lines);
    }

    [Fact]
    public void TheApplierRestsBetweenPiecesAndContinuesWhenOtherActivityEndsOnlyObservingWhileItIsNotWriting()
    {
        PlanFile plan = Make().Resolve(20000);
        RelocationRest rest = Rest();
        _busyObservations = 2;
        Vol.AfterMove = n =>
        {
            _events.Add("move");
            _now += n == 1 ? RelocationRest.ObserveEvery : TimeSpan.FromSeconds(1); // 첫 조각 뒤에 관측 간격이 지남
        };

        ApplyReport report = Apply(plan, new ApplyOptions(Execute: true, ChunkBytes: 8 * Cb) { CanIssue = () => rest.MayIssue(CancellationToken.None) });

        Assert.Null(report.Stopped);
        Assert.Equal(["move", "observe busy", "rest", "observe busy", "rest", "observe idle"], _events.Take(6).ToList());
        Assert.True(Vol.Moves.Count > 1);
        Assert.False(SameExtents(Vol.Ext, Files)); // 끝까지 옮김
    }

    [Fact]
    public void WhenTheRestEndsTheRoundTheJournalIsTheResumePointAndItContinuesLater()
    {
        PlanFile plan = Make().Resolve(20000);
        RelocationRest rest = Rest();
        _busyObservations = int.MaxValue;
        Vol.AfterMove = n => _now += n == 1 ? RelocationRest.ObserveEvery : TimeSpan.Zero;

        ApplyReport report = Apply(plan, new ApplyOptions(Execute: true, ChunkBytes: 8 * Cb) { CanIssue = () => rest.MayIssue(CancellationToken.None) });

        Assert.Equal("cancelled", report.Stopped);
        Assert.Single(Vol.Moves); // 한 조각을 끝낸 뒤 멈춤: 게임 시작 때와 같은 중단
        _busyObservations = 0;
        ApplyReport again = Apply(plan, new ApplyOptions(Execute: true, ChunkBytes: 8 * Cb) { CanIssue = () => Rest().MayIssue(CancellationToken.None) });
        Assert.Null(again.Stopped); // 저널이 이어 감
        Assert.False(SameExtents(Vol.Ext, Files));
    }
}
