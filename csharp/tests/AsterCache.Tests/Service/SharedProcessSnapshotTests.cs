using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 한 번의 점검 안의 판정들은 스냅샷 한 번을 나눠 쓰고, 게임이 방금 시작했거나 끝났으면 낡은 목록을 쓰지 않는가.
public sealed class SharedProcessSnapshotTests
{
    private int _takes;
    private long _now = 10_000;
    private List<int> _pids = [4, 100, 200];

    private SharedProcessSnapshot Make() => new(() => [.. _pids], () =>
    {
        _takes++;
        return [.. _pids.Select(p => (p, $"p{p}.exe"))];
    }, () => _now);

    [Fact]
    public void ManyCallsInOneCheckTakeOneSnapshot()
    {
        SharedProcessSnapshot snap = Make();
        for (int i = 0; i < 300; i++)
        {
            _now += 2; // 17 개 게임 x 후보 게임 수만큼의 판정이 0.6 초 안에 몰리는 경우
            _ = snap.Get();
        }

        Assert.Equal(1, _takes);
    }

    [Fact]
    public void GameStartedJustNowIsSeenImmediately()
    {
        SharedProcessSnapshot snap = Make();
        Assert.DoesNotContain(snap.Get(), p => p.Exe == "p300.exe");
        _pids.Add(300); // 게임 프로세스가 방금 뜸: 시간은 거의 안 지남
        _now += 1;

        Assert.Contains(snap.Get(), p => p.Exe == "p300.exe");
        Assert.Equal(2, _takes);
    }

    [Fact]
    public void GameEndedJustNowIsSeenImmediately()
    {
        SharedProcessSnapshot snap = Make();
        _ = snap.Get();
        _pids.Remove(200);
        _now += 1;

        Assert.DoesNotContain(snap.Get(), p => p.Pid == 200);
        Assert.Equal(2, _takes);
    }

    [Fact]
    public void SnapshotOlderThanTheLimitIsTakenAgainEvenWithSamePids()
    {
        SharedProcessSnapshot snap = Make();
        _ = snap.Get();
        _now += SharedProcessSnapshot.MaxAgeMs - 1;
        _ = snap.Get();
        Assert.Equal(1, _takes);

        _now += 1;
        _ = snap.Get();
        Assert.Equal(2, _takes);
    }
}
