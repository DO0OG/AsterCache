using System.ComponentModel;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 예약 드라이브 최적화 (defrag.exe) 실행 감지: 시작 → 도는 중 → 끝, 서비스 시작 때 이미 도는 경우, 한 번의 실행에 30분 상한, 연달아 두 번 실행. 프로세스 목록은 부르지 않습니다.
public sealed class DriveOptimizationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 23, 3, 0, 0, TimeSpan.Zero);
    private readonly ListLog _log = new();
    private DateTimeOffset _now = T0; // 로그용 벽시계
    private long _ms; // 단조 시계
    private bool _running;
    private bool _unreadable; // 프로세스 목록 조회 실패
    private int _lookups;

    private DriveOptimization Make() => new(() =>
    {
        _lookups++;
        return _unreadable ? throw new Win32Exception(5) : _running;
    }, _log, () => _now, () => _ms);

    private void Elapse(TimeSpan span)
    {
        _now += span;
        _ms += (long)span.TotalMilliseconds;
    }

    [Fact]
    public void NoRunMeansNoBlockAndNoLogLine()
    {
        DriveOptimization o = Make();

        Assert.False(o.Blocking);
        Assert.Null(o.LastEnded);
        Assert.Empty(_log.Lines);
    }

    [Fact]
    public void BlocksFromTheStartUntilTheEndAndLogsOneLineEachWithTheLength()
    {
        DriveOptimization o = Make();
        Assert.False(o.Blocking); // 서비스는 최적화가 돌지 않을 때 시작함

        _running = true;
        Elapse(TimeSpan.FromSeconds(30));
        Assert.True(o.Blocking);
        Elapse(TimeSpan.FromMinutes(8));
        Assert.True(o.Blocking);
        Assert.Null(o.LastEnded); // 도는 중에는 끝 시각이 없음

        _running = false;
        Elapse(TimeSpan.FromSeconds(30));
        Assert.False(o.Blocking);

        Assert.Equal(T0 + TimeSpan.FromSeconds(30) + TimeSpan.FromMinutes(8) + TimeSpan.FromSeconds(30), o.LastEnded);
        Assert.Collection(_log.Lines,
            l => Assert.Equal("Info: drive optimization started at 03:00:30 UTC (defrag.exe running), relocation waits for it up to 30 min", l),
            l => Assert.Equal("Info: drive optimization ended at 03:09:00 UTC after 510 s (started 03:00:30 UTC)", l));
    }

    [Fact]
    public void ARunAlreadyGoingWhenTheServiceStartsBlocksAndIsLoggedAsStartUnknown()
    {
        _running = true;
        DriveOptimization o = Make();

        Assert.True(o.Blocking);
        Assert.Equal("Info: drive optimization already running when the service started (seen at 03:00:00 UTC, start time unknown), relocation waits for it up to 30 min", Assert.Single(_log.Lines));

        _running = false;
        Elapse(TimeSpan.FromMinutes(5));
        Assert.False(o.Blocking);
        Assert.Contains(_log.Lines, l => l.Contains("ended at 03:05:00 UTC after at least 300 s", StringComparison.Ordinal)); // 길이는 본 때부터라 하한
    }

    [Fact]
    public void StopsBlockingThirtyMinutesAfterTheRunWasFirstSeenEvenIfItKeepsRunning()
    {
        _running = true;
        DriveOptimization o = Make();
        Assert.True(o.Blocking);

        Elapse(DriveOptimization.MaxDefer - TimeSpan.FromSeconds(10));
        Assert.True(o.Blocking); // 29 분 50 초

        Elapse(TimeSpan.FromSeconds(10));
        Assert.False(o.Blocking); // 30 분: 그 뒤에는 기존 조건만으로 판단
        Elapse(TimeSpan.FromMinutes(20));
        Assert.False(o.Blocking);
        Assert.Single(_log.Lines, l => l.Contains("still running after 30 min, relocation no longer waits for it", StringComparison.Ordinal)); // 한 번만

        _running = false;
        Elapse(TimeSpan.FromSeconds(10));
        Assert.False(o.Blocking);
        Assert.Contains(_log.Lines, l => l.Contains("ended at 03:50:10 UTC", StringComparison.Ordinal)); // 끝은 상한 뒤에도 기록
    }

    [Fact]
    public void TwoRunsInARowEachGetTheirOwnThirtyMinutesAndTheEndOfThePreviousOneIsKeptWhileTheNextRuns()
    {
        DriveOptimization o = Make();
        Assert.False(o.Blocking);
        Elapse(TimeSpan.FromSeconds(10));
        _running = true;
        Assert.True(o.Blocking);
        Elapse(TimeSpan.FromMinutes(9));
        _running = false;
        Assert.False(o.Blocking);
        DateTimeOffset firstEnd = _now;
        Assert.Equal(firstEnd, o.LastEnded);

        Elapse(TimeSpan.FromMinutes(2));
        _running = true;
        Assert.True(o.Blocking);
        Assert.Equal(firstEnd, o.LastEnded); // 둘째 실행이 도는 동안 경고에는 직전 실행의 끝이 남음

        Elapse(TimeSpan.FromMinutes(25));
        Assert.True(o.Blocking); // 첫 실행 시작에서는 36 분이지만 둘째 실행의 30 분 안
        Elapse(TimeSpan.FromMinutes(5));
        Assert.False(o.Blocking);

        Assert.Equal(2, _log.Lines.Count(l => l.Contains("drive optimization started", StringComparison.Ordinal)));
    }

    [Fact]
    public void LooksUpTheProcessListAtMostOncePerPollInterval()
    {
        DriveOptimization o = Make();

        Assert.False(o.Blocking);
        _running = true;
        Elapse(DriveOptimization.PollEvery - TimeSpan.FromSeconds(1));
        Assert.False(o.Blocking); // 간격 안: 목록을 다시 읽지 않아 시작을 아직 모름
        Assert.Equal(1, _lookups);

        Elapse(TimeSpan.FromSeconds(1));
        Assert.True(o.Blocking);
        Assert.Equal(2, _lookups);
    }

    [Fact]
    public void AWallClockStepBackDoesNotStallTheDetectionOrStretchTheDeferral()
    {
        DriveOptimization o = Make();
        Assert.False(o.Blocking);
        Elapse(TimeSpan.FromSeconds(10));
        _running = true;
        Assert.True(o.Blocking);

        _now -= TimeSpan.FromHours(1); // 도는 중에 벽시계가 1시간 뒤로 감 (단조 시계는 그대로 흐름)
        _ms += (long)TimeSpan.FromMinutes(9).TotalMilliseconds;
        _running = false;
        Assert.False(o.Blocking); // 간격이 지난 것으로 보고 끝을 알아챔
        Assert.Contains(_log.Lines, l => l.Contains("after 540 s", StringComparison.Ordinal)); // 길이는 단조 시계로

        _running = true;
        _now -= TimeSpan.FromHours(1);
        _ms += (long)TimeSpan.FromSeconds(10).TotalMilliseconds;
        Assert.True(o.Blocking);
        _ms += (long)DriveOptimization.MaxDefer.TotalMilliseconds;
        Assert.False(o.Blocking); // 상한도 단조 시계로 30 분
    }

    [Fact]
    public void AFailedFirstLookupStillCountsTheRunSeenNextAsAlreadyRunningAtServiceStart()
    {
        _running = true;
        _unreadable = true;
        DriveOptimization o = Make();
        Assert.False(o.Blocking);

        _unreadable = false;
        Elapse(TimeSpan.FromSeconds(10));
        Assert.True(o.Blocking);
        Assert.Contains(_log.Lines, l => l.Contains("already running when the service started", StringComparison.Ordinal));
    }

    [Fact]
    public void AFailedLookupKeepsTheLastKnownState()
    {
        _running = true;
        DriveOptimization o = Make();
        Assert.True(o.Blocking);

        _unreadable = true;
        Elapse(TimeSpan.FromMinutes(1));
        Assert.True(o.Blocking); // 못 읽은 것을 끝으로 보지 않음
        Assert.Single(_log.Lines);

        _unreadable = false;
        _running = false;
        Elapse(TimeSpan.FromMinutes(1));
        Assert.False(o.Blocking);
    }
}
