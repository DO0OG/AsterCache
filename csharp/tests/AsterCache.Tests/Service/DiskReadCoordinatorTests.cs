using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

public sealed class DiskReadCoordinatorTests
{
    private long _now;

    private DiskReadCoordinator Coordinator(ListLog log) => new(new VolumeDisks(letter => letter switch
    {
        "C:" or "D:" => new HashSet<uint> { 1 },
        "E:" => new HashSet<uint> { 2 },
        _ => null,
    }, () => _now), log, () => _now);

    [Fact]
    public void YieldBlocksNewLeasesAcrossMappedVolumesButLeavesOtherDisksAvailable()
    {
        var log = new ListLog();
        DiskReadCoordinator coordinator = Coordinator(log);
        using DiskReadCoordinator.Lease? active = coordinator.TryBegin(@"C:\Game", "warm files");
        using DiskReadCoordinator.Lease? other = coordinator.TryBegin(@"E:\Game", "warm files");

        coordinator.Yield(@"D:\Game", "game started");

        Assert.False(active!.IsValid);
        Assert.Null(coordinator.TryBegin(@"C:\Other", "keep awake"));
        Assert.NotNull(coordinator.TryBegin(@"E:\Other", "keep awake"));
    }

    [Fact]
    public void YieldCancelsExistingLeaseAndLogsWhenSynchronousWorkDrains()
    {
        var log = new ListLog();
        DiskReadCoordinator coordinator = Coordinator(log);
        DiskReadCoordinator.Lease lease = coordinator.TryBegin(@"C:\Game", "drive recovery sample")!;
        _now = 100;

        coordinator.Yield(@"C:\Game", "game started");
        Assert.True(lease.Token.IsCancellationRequested);
        Assert.False(lease.IsValid);
        _now = 475;
        lease.Dispose();

        Assert.Contains(log.Lines, line => line.Contains("disk yield: 1 leases drained in 375 ms (drive recovery sample)", StringComparison.Ordinal));
    }

    [Fact]
    public void ConcurrentGamesKeepTheDiskYieldedUntilTheLastGameResumes()
    {
        var log = new ListLog();
        DiskReadCoordinator coordinator = Coordinator(log);
        coordinator.Yield(@"C:\GameA", "game started");
        coordinator.Yield(@"D:\GameB", "game started");

        coordinator.Resume(@"C:\GameA");
        Assert.Null(coordinator.TryBegin(@"D:\GameB", "warm files"));

        coordinator.Resume(@"D:\GameB");
        Assert.NotNull(coordinator.TryBegin(@"C:\GameA", "warm files"));
    }

    [Fact]
    public void GamesWithTheSameInstallRootKeepSeparateDiskRegistrations()
    {
        var log = new ListLog();
        DiskReadCoordinator coordinator = Coordinator(log);

        coordinator.Yield(@"C:\Games\Shared", "game started", "Game A");
        coordinator.Yield(@"C:\Games\Shared", "game started", "Game B");
        coordinator.Resume(@"C:\Games\Shared", "Game A");

        Assert.Null(coordinator.TryBegin(@"C:\Games\Shared", "warm files"));

        coordinator.Resume(@"C:\Games\Shared", "Game B");

        using (DiskReadCoordinator.Lease? resumed = coordinator.TryBegin(@"C:\Games\Shared", "warm files"))
        {
            Assert.NotNull(resumed);
        }
    }

    [Fact]
    public void RemovedOrMovedGameRegistrationIsReleasedAndLogged()
    {
        var log = new ListLog();
        DiskReadCoordinator coordinator = Coordinator(log);
        coordinator.Yield(@"C:\Games\Old", "registered game running", "Game A");

        coordinator.ReconcileRunningGames(new Dictionary<string, string>());

        using (DiskReadCoordinator.Lease? oldAccess = coordinator.TryBegin(@"C:\Games\Old", "warm files"))
        {
            Assert.NotNull(oldAccess);
        }
        Assert.Contains(log.Lines, line => line.Contains("released stale game registration Game A", StringComparison.Ordinal));

        coordinator.Yield(@"C:\Games\Old", "registered game running", "Game A");
        coordinator.ReconcileRunningGames(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Game A"] = @"E:\Games\New",
        });
        coordinator.Yield(@"E:\Games\New", "registered game running", "Game A");

        using (DiskReadCoordinator.Lease? oldAccess = coordinator.TryBegin(@"C:\Games\Old", "warm files"))
        {
            Assert.NotNull(oldAccess);
        }

        Assert.Null(coordinator.TryBegin(@"E:\Games\New", "warm files"));
        Assert.Contains(log.Lines, line => line.Contains("released stale game registration Game A", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownVolumeMappingDoesNotPermanentlyMergeKnownDisks()
    {
        Dictionary<string, IReadOnlySet<uint>?> mapped = new(StringComparer.OrdinalIgnoreCase)
        {
            ["D:"] = new HashSet<uint> { 1 },
            ["E:"] = new HashSet<uint> { 2 },
            ["C:"] = null,
        };
        var volumes = new VolumeDisks(letter => mapped.GetValueOrDefault(letter), () => _now);
        var coordinator = new DiskReadCoordinator(volumes, new ListLog(), () => _now);
        using (coordinator.TryBegin(@"D:\Game", "warm files")) { }
        using (coordinator.TryBegin(@"E:\Game", "warm files")) { }

        coordinator.Yield(@"C:\Game", "game started", "Unknown");
        mapped["C:"] = new HashSet<uint> { 1 };
        coordinator.Yield(@"C:\Game", "game started", "Unknown");

        Assert.NotNull(coordinator.TryBegin(@"E:\Game", "warm files"));
        Assert.Null(coordinator.TryBegin(@"D:\Other", "warm files"));

        coordinator.Resume(@"C:\Game", "Unknown");

        using (DiskReadCoordinator.Lease? resumed = coordinator.TryBegin(@"D:\Other", "warm files"))
        {
            Assert.NotNull(resumed);
        }
    }

    [Fact]
    public void YieldWithAnUnmappedGameVolumeStillYieldsItAfterItMapsToANewDisk()
    {
        Dictionary<string, IReadOnlySet<uint>?> mapped = new(StringComparer.OrdinalIgnoreCase)
        {
            ["D:"] = new HashSet<uint> { 1 },
            ["C:"] = null,
        };
        var coordinator = new DiskReadCoordinator(new VolumeDisks(letter => mapped.GetValueOrDefault(letter), () => _now), new ListLog(), () => _now);
        using (coordinator.TryBegin(@"D:\Game", "warm files")) { }

        coordinator.Yield(@"C:\Game", "game started", "Unmapped"); // 이 순간에만 매핑을 모름
        mapped["C:"] = new HashSet<uint> { 3 }; // 뒤에 다른 새 디스크로 매핑됨

        Assert.Null(coordinator.TryBegin(@"C:\Other", "warm files")); // 양보가 이어져 같은 디스크 대여를 허용하지 않음
        coordinator.Resume(@"C:\Game", "Unmapped");
        using DiskReadCoordinator.Lease? resumed = coordinator.TryBegin(@"C:\Other", "warm files");
        Assert.NotNull(resumed);
    }

    [Fact]
    public void PreservedLeaseWithoutAServiceTokenIsNotTiedToTheCallersToken()
    {
        DiskReadCoordinator coordinator = Coordinator(new ListLog());
        coordinator.SetServiceToken(CancellationToken.None);
        using var caller = new CancellationTokenSource();

        using DiskReadCoordinator.Lease lease = coordinator.TryBegin(@"C:\Game", "startup stream", caller.Token, @"C:\Game", preserveGameKey: "game")!;
        caller.Cancel(); // 예열 취소 (게임 시작) 는 같은 게임의 보존 스트림을 끊지 않음

        Assert.True(lease.IsValid);
    }

    [Fact]
    public void CancellationCallbackFailureDoesNotEscapeYieldOrSkipOtherCancellations()
    {
        var log = new ListLog();
        DiskReadCoordinator coordinator = Coordinator(log);
        using DiskReadCoordinator.Lease first = coordinator.TryBegin(@"C:\Games\A", "first")!;
        using DiskReadCoordinator.Lease second = coordinator.TryBegin(@"C:\Games\B", "second")!;
        using CancellationTokenRegistration registration = first.Token.Register(() => throw new InvalidOperationException("callback failed"));

        Exception? error = Record.Exception(() => coordinator.Yield(@"C:\Games\A", "game started"));

        Assert.Null(error);
        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(second.IsValid);
        Assert.Contains(log.Lines, line => line.Contains("disk yield cancellation callback failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TryBeginRacingWithYieldNeverLeavesAValidBackgroundLease()
    {
        for (int i = 0; i < 100; i++)
        {
            var barrier = new Barrier(2);
            DiskReadCoordinator coordinator = Coordinator(new ListLog());
            Task<DiskReadCoordinator.Lease?> begin = Task.Run(() =>
            {
                barrier.SignalAndWait();
                return coordinator.TryBegin(@"C:\Games\A", "warm files");
            });
            Task yield = Task.Run(() =>
            {
                barrier.SignalAndWait();
                coordinator.Yield(@"C:\Games\A", "game started");
            });

            await Task.WhenAll(begin, yield);

            if (await begin is { } lease)
            {
                Assert.False(lease.IsValid);
                lease.Dispose();
            }

            Assert.Null(coordinator.TryBegin(@"C:\Games\A", "warm files"));
        }
    }

    [Fact]
    public void DrainLogIgnoresTheYieldingGamesOwnPreservedStreamLease()
    {
        var log = new ListLog();
        DiskReadCoordinator coordinator = Coordinator(log);
        using DiskReadCoordinator.Lease own = coordinator.TryBegin(@"C:\Game", "prewarm stream", default, @"C:\Game", preserveGameKey: "game")!;
        DiskReadCoordinator.Lease other = coordinator.TryBegin(@"C:\Other", "warm files")!;
        _now = 10;

        coordinator.Yield(@"C:\Game", "game started", "game");
        _now = 50;
        other.Dispose();

        Assert.True(own.IsValid); // 양보하는 게임의 보존 스트림은 계속 읽고 양보 시간 계측에도 넣지 않음
        Assert.Contains(log.Lines, line => line.Contains("disk yield: 1 leases drained in 40 ms (warm files)", StringComparison.Ordinal));
    }

    private static DiskReadCoordinator.Lease? BeginStart(DiskReadCoordinator coordinator, string root, string key) =>
        coordinator.TryBegin(root, "start stream", default, root, allowWhileYielded: true, preserveGameKey: key);

    [Fact]
    public void StartStreamIsRejectedWhileAnotherGameReallyRuns()
    {
        DiskReadCoordinator coordinator = Coordinator(new ListLog());
        coordinator.Yield(@"C:\Games\A", "game started", "a");
        coordinator.Yield(@"C:\Games\B", "game started", "b");

        Assert.Null(BeginStart(coordinator, @"C:\Games\B", "b"));
    }

    [Fact]
    public void StartStreamIsAllowedWhileTheOtherGameIsExitingButBackgroundWorkStaysYielded()
    {
        DiskReadCoordinator coordinator = Coordinator(new ListLog());
        coordinator.Yield(@"C:\Games\A", "game started", "a");
        coordinator.Yield(@"C:\Games\B", "game started", "b");

        coordinator.MarkExiting(new HashSet<string> { "a" });

        using DiskReadCoordinator.Lease? lease = BeginStart(coordinator, @"C:\Games\B", "b");
        Assert.NotNull(lease);
        Assert.True(lease!.IsValid);
        Assert.Null(coordinator.TryBegin(@"C:\Games\Other", "warm files"));
    }

    [Fact]
    public void ExitingGameThatComesBackRejectsStartStreamsAgainAndCancelsTheRunningOne()
    {
        DiskReadCoordinator coordinator = Coordinator(new ListLog());
        coordinator.Yield(@"C:\Games\A", "game started", "a");
        coordinator.Yield(@"C:\Games\B", "game started", "b");
        coordinator.MarkExiting(new HashSet<string> { "a" });
        using DiskReadCoordinator.Lease lease = BeginStart(coordinator, @"C:\Games\B", "b")!;

        coordinator.Yield(@"C:\Games\A", "game started", "a"); // A 가 다시 뜸: 유예 해제

        Assert.False(lease.IsValid);
        Assert.Null(BeginStart(coordinator, @"C:\Games\B", "b"));
    }

    [Fact]
    public void GameReRegisteredAfterResumeIsNotExiting()
    {
        DiskReadCoordinator coordinator = Coordinator(new ListLog());
        coordinator.Yield(@"C:\Games\A", "game started", "a");
        coordinator.Yield(@"C:\Games\B", "game started", "b");
        coordinator.MarkExiting(new HashSet<string> { "a" });

        coordinator.Resume(@"C:\Games\A", "a");
        coordinator.Yield(@"C:\Games\A", "game started", "a"); // Resume 뒤 재등록은 유예 상태가 아님

        Assert.Null(BeginStart(coordinator, @"C:\Games\B", "b"));
    }

    [Fact]
    public void MarkExitingWithAnEmptySetReleasesTheGrace()
    {
        DiskReadCoordinator coordinator = Coordinator(new ListLog());
        coordinator.Yield(@"C:\Games\A", "game started", "a");
        coordinator.Yield(@"C:\Games\B", "game started", "b");
        coordinator.MarkExiting(new HashSet<string> { "a" });
        using DiskReadCoordinator.Lease? allowed = BeginStart(coordinator, @"C:\Games\B", "b");
        Assert.NotNull(allowed);

        coordinator.MarkExiting(new HashSet<string>());

        Assert.Null(BeginStart(coordinator, @"C:\Games\B", "b"));
    }

    [Fact]
    public void RunningGameOnAnotherDiskNeverBlocksAndOneOnTheSameDiskStillBlocks()
    {
        DiskReadCoordinator coordinator = Coordinator(new ListLog());
        coordinator.Yield(@"C:\Games\A", "game started", "a"); // 디스크 1
        coordinator.Yield(@"E:\Games\B", "game started", "b"); // 디스크 2
        using (DiskReadCoordinator.Lease? other = BeginStart(coordinator, @"E:\Games\B", "b"))
        {
            Assert.NotNull(other); // 다른 디스크의 실행 중 게임은 B 의 시작 스트림에 무관
        }

        coordinator.Yield(@"D:\Games\C", "game started", "c"); // 디스크 1 에 C 도 실행 중
        coordinator.Yield(@"D:\Games\D", "game started", "d");
        coordinator.MarkExiting(new HashSet<string> { "a" });
        Assert.Null(BeginStart(coordinator, @"D:\Games\D", "d")); // C 가 실제로 실행 중이면 A 만 유예여도 거절

        coordinator.MarkExiting(new HashSet<string> { "a", "c" });
        using DiskReadCoordinator.Lease? allowed = BeginStart(coordinator, @"D:\Games\D", "d");
        Assert.NotNull(allowed);
    }

    [Fact]
    public void UnresolvedVolumeMappingUsesAConservativeSharedYield()
    {
        var log = new ListLog();
        var disks = new VolumeDisks(_ => null, () => _now);
        var coordinator = new DiskReadCoordinator(disks, log, () => _now);

        coordinator.Yield(@"C:\Game", "game started");

        Assert.Null(coordinator.TryBegin(@"E:\Game", "warm files"));
    }
}

public sealed class DiskReadCoordinatorPathTests
{
    [Fact]
    public void StartStreamDoesNotIssueAnotherBatchAfterSameDiskYield()
    {
        GameSettings game = Games.Make("game", "game.exe");
        var log = new ListLog();
        var coordinator = new DiskReadCoordinator(new VolumeDisks(letter => letter is "C:" or "D:" ? new HashSet<uint> { 1 } : new HashSet<uint> { 2 }, () => 0), log);
        var streamer = new BatchStreamer(n =>
        {
            if (n == 1)
            {
                coordinator.Yield(@"C:\Games\other", "another game started");
            }
        });
        var dispatcher = new GameDispatcher([game], streamer, log, coordinator: coordinator);

        Assert.False(dispatcher.Handle(new ProcessStart(42, "game.exe", 0)));
        Assert.Equal(1, streamer.Batches);
    }

    [Fact]
    public void SameGamesStartupStreamKeepsTheExistingYieldException()
    {
        GameSettings game = Games.Make("game", "game.exe");
        var log = new ListLog();
        var coordinator = new DiskReadCoordinator(new VolumeDisks(_ => new HashSet<uint> { 1 }, () => 0), log);
        coordinator.Yield(game.OriginRoot, "game started", game.Name);
        var streamer = new BatchStreamer(_ => { });
        var dispatcher = new GameDispatcher([game], streamer, log, coordinator: coordinator);

        Assert.True(dispatcher.Handle(new ProcessStart(43, "game.exe", 0)));
        Assert.Equal(3, streamer.Batches);
    }

    [Fact]
    public void StartupStreamIsDeniedWhenAnotherRegisteredGameAlreadyRunsOnTheDisk()
    {
        GameSettings game = Games.Make("game", "game.exe");
        var log = new ListLog();
        var coordinator = new DiskReadCoordinator(new VolumeDisks(_ => new HashSet<uint> { 1 }, () => 0), log);
        coordinator.Yield(@"C:\Games\other", "game started");
        coordinator.Yield(game.OriginRoot, "game started", game.Name);
        var streamer = new BatchStreamer(_ => { });
        var dispatcher = new GameDispatcher([game], streamer, log, coordinator: coordinator);

        Assert.False(dispatcher.Handle(new ProcessStart(44, "game.exe", 0)));
        Assert.Equal(0, streamer.Batches);
    }

    private sealed class BatchStreamer(Action<int> afterBatch) : IGameStreamer
    {
        public int Batches { get; private set; }

        public StreamStats Stream(GameSettings game) => throw new InvalidOperationException();

        public StreamStats Stream(GameSettings game, CancellationToken stop) => Stream(game, stop, null).Stats;

        public (StreamStats Stats, bool Complete) Stream(GameSettings game, CancellationToken stop, Action<long, long>? progress)
        {
            while (Batches < 3)
            {
                if (stop.IsCancellationRequested)
                {
                    return (new StreamStats(Batches, Batches, 0, 0, Aborted: true), false);
                }

                Batches++;
                afterBatch(Batches);
                progress?.Invoke(Batches, 3);
            }

            return (new StreamStats(Batches, Batches, 0, 0), true);
        }
    }
}
