using System.Diagnostics;
using AsterCache.Core.Logging;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;

namespace AsterCache.Service;

/// Prewarmer 의 같은 디스크 게임·재배치와의 양보: 실행 중 게임 감지, 외부 부하 대기, 게임 시작 때 스트림 취소.
public sealed partial class Prewarmer
{
    /// 부팅 조용 구간의 다른 작업 (재배치·새 트레이스 예열, 배치 검사, 유지 예열) 과 대기 예열은 부팅 뒤 이만큼 지나고, 게임 드라이브가 IdleNeeded 동안 한가해진 뒤에 시작합니다 (부팅 직후 디스크 경쟁 방지).
    public static readonly TimeSpan BootDelay = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan IdleNeeded = TimeSpan.FromSeconds(60);

    /// 서비스 시작 예열과 런처 계기 예열 (두 경로만) 의 부팅 대기와 유휴: 읽기 자체가 다른 읽기에 양보하므로 (PrewarmYield) 위 BootDelay·IdleNeeded 보다 짧게 기다립니다.
    public static readonly TimeSpan PrewarmBootDelay = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan PrewarmIdleNeeded = TimeSpan.FromSeconds(20);

    /// 트리거로 켠 게임이 보충·재예열 예산 순위에서 먼저 받는 기간 (그 뒤에는 기존 순위만).
    public static readonly TimeSpan TriggerPriorityWindow = TimeSpan.FromHours(6);
    private static readonly TimeSpan GateStep = TimeSpan.FromSeconds(5);

    /// 기록 없는 예열의 통째 상한 기본값 (MiB, full_prewarm_max_mib 가 0 일 때).
    public const int DefaultFullPrewarmMib = 1024;

    /// 이 게임을 플레이한 적이 있는가 (자동 모드의 last_played). 재예열 점검은 플레이한 게임만 봅니다. 기본은 모두 참 (서비스가 자동 상태를 보고 정함).
    public Func<string, bool> HasPlayed { get; set; } = _ => true;

    /// 다음에 실행될 가능성이 높은 순서로 게임을 돌려주는 순위 (자동 모드의 예열 점수 + 런처 트리거). 기본 null = 순위 없음: 재예열·보충 예열이 모든 대상을 처리하고 예산을 나누지 않음.
    public Func<IReadOnlyList<GameSettings>, IEnumerable<GameSettings>>? WarmRank { get; set; }

    /// 게임 업데이트 직후의 지연 (auto.post_update_delay_min): 그 게임이 아직 기다려야 하면 남은 시간, 아니면 null. 기본은 지연 없음 (서비스가 자동 상태를 보고 정함).
    public Func<GameSettings, TimeSpan?> PostUpdateWait { get; set; } = _ => null;

    /// 업데이트를 감지해 재계획 상태가 된 때 (없으면 null): probe 모드에서 이 드라이브의 회복 판정 (Recovery) 의 기준 시각. 기본 null (고정 분만 씀).
    public Func<GameSettings, DateTimeOffset?> ReplanStart { get; set; } = _ => null;

    /// 업데이트 뒤 트레이스를 유지한 채 곧 다시 계획하는 상태인가 (바로 재계획 가능). 다음 플레이에서 다시 트레이스하는 상태는 거짓: 그 사이 새 예열을 막으면 플레이 전까지 영영 걸리므로 보류하지 않음. 기본 거짓.
    public Func<GameSettings, bool> ReplanKept { get; set; } = _ => false;

    /// 재배치 단계 (auto_status 의 relocating) 인 게임인가. 하나라도 있으면 서비스 시작 예열을 보류합니다.
    public Func<string, bool> Relocating { get; set; } = _ => false;

    /// 업데이트 지연으로 미뤄야 하는 게임이면 true (지연 한 번에 로그는 한 줄). 지연이 끝난 게임은 다음 지연 때 다시 로그를 남깁니다.
    private bool Deferred(GameSettings game)
    {
        // AutoRelocator.PostUpdateDelayed 와 같은 판정: probe 모드이고 이 드라이브에 회복 추적이 있으면 그 판정, 없으면 고정 분.
        bool? probed = _auto.PostRelocateRecovery == AutoSettings.RecoveryProbe && Recovery is { } recovery && ReplanStart(game) is { } at
            ? recovery.WatchUpdate(PlanFile.SplitDrive(game.OriginRoot).ToUpperInvariant(), at, TimeSpan.FromMinutes(_auto.RecoveryMaxMin)) : null;
        bool delayed = probed ?? PostUpdateWait(game) is not null;
        bool replan = !delayed && ReplanKept(game); // 지연이 끝나도 곧 있을 재계획이 끝나 상태가 바뀔 때까지 (성공·실패 모두 상태가 바뀜) 옛 목록으로 새 예열을 시작하지 않음. 다시 트레이스할 때까지 기다리는 상태는 제외
        bool wait = delayed || replan;
        lock (_gate)
        {
            if (!wait)
            {
                _delayLogged.Remove(game.Name);
            }
            else if (!_delayLogged.TryGetValue(game.Name, out bool logged) || logged != replan)
            {
                _delayLogged[game.Name] = replan;
                _log.Info(replan ? $"{game.Name}: new prewarm held until the re-plan after the update finishes"
                    : probed is null ? AutoRelocator.PostUpdateLine(game.Name, _auto.PostUpdateDelayMin) : AutoRelocator.PostUpdateLine(game.Name, _auto.RecoveryMaxMin, probed: true));
            }
        }

        return wait;
    }

    /// 이 게임이나 같은 디스크의 게임을 읽는 예열 (핫 집합 스트림, 기록 없는 통째·프로필 읽기) 이 지금 도는가. 재배치가 시작하기 전에 보고 양보합니다 (같은 디스크에서 읽기와 쓰기가 겹치지 않게).
    public bool StreamingOnDisk(string game) => ReadingOnDisk(_streaming, game) || ReadingOnDisk(_coldReading, game);

    private bool ReadingOnDisk(string? reading, string game) => reading is not null && (string.Equals(reading, game, StringComparison.OrdinalIgnoreCase) || SameDisk(reading, game));

    // 이 게임과 같은 디스크의 등록 게임 이름들 (자신 포함). 디스크 조회는 잠금 밖에서 하고 잠금 안에서는 이름만 비교합니다.
    private HashSet<string> DiskMates(string game) =>
        new([game, .. _games.Where(g => !string.Equals(g.Name, game, StringComparison.OrdinalIgnoreCase) && SameDisk(g.Name, game)).Select(g => g.Name)], StringComparer.OrdinalIgnoreCase);

    /// 재배치 쪽: 같은 디스크의 예열 읽기가 없을 때만 재배치 시작 표시를 세우고 true (읽는 중이면 표시 없이 false). 확인과 표시가 _gate 아래에서 한 번에 이뤄져,
    /// 이 호출이 true 를 돌려준 뒤에는 같은 디스크의 예열이 읽기 표시를 세우지 못합니다 (TryMarkReading). 끝나면 (성공·실패·예외 모두) EndRelocation 을 불러야 합니다.
    public bool TryBeginRelocation(string game)
    {
        HashSet<string> mates = DiskMates(game);
        lock (_gate)
        {
            if ((_streaming is { } streaming && mates.Contains(streaming)) || (_coldReading is { } cold && mates.Contains(cold)))
            {
                return false;
            }

            _relocStarted.Add(game);
            return true;
        }
    }

    /// 재배치 시작 표시를 내립니다.
    public void EndRelocation(string game)
    {
        lock (_gate)
        {
            _relocStarted.Remove(game);
            PruneRemovedGameLocked(game); // 재배치 중에 설정에서 빠져 보류된 정리를 끝나는 때 마저 함
        }
    }

    // 예열 쪽: 같은 디스크에서 재배치가 시작했으면 false (표시를 세우지 않음), 아니면 mark 로 읽기 표시를 세우고 true. 확인과 표시가 _gate 아래에서 한 번에 이뤄집니다 (mark 는 필드 대입만).
    private bool TryMarkReading(string game, Action mark)
    {
        HashSet<string> mates = DiskMates(game);
        lock (_gate)
        {
            if (_relocStarted.Overlaps(mates))
            {
                return false;
            }

            foreach (string mate in mates)
            {
                _idleSince.Remove(mate);
            }

            mark();
            return true;
        }
    }

    // 묶음 경계 (게임 사이) 에서 재배치가 돌고 있으면 끝나고 디스크가 다시 한가해질 때까지 기다립니다 (시작 게이트와 같은 조건). 기다리는 중 취소되면 false.
    private bool WaitOutRelocation(GameSettings game, CancellationToken ct) => !RelocationHeld() || WaitStartupGate([game], ct, BootDelay, IdleNeeded);

    // 재배치 중인 게임이 있으면 (어느 게임이든) true. 보류 로그는 게임당 한 번, 재배치가 끝나면 다음에 다시 로그를 남깁니다.
    private bool RelocationHeld()
    {
        bool any = false;
        lock (_gate)
        {
            foreach (GameSettings g in _games)
            {
                if (!Relocating(g.Name))
                {
                    _relocHoldLogged.Remove(g.Name);
                    continue;
                }

                any = true;
                if (_relocHoldLogged.Add(g.Name))
                {
                    _log.Info($"{g.Name}: startup prewarm held while relocation is running");
                }
            }
        }

        return any;
    }

    /// 게임 exe 가 떴을 때 (스트림 취소보다 먼저): 도는 예열 스트림이 그 게임의 것이거나 같은 디스크의 것이면 다음 묶음 경계에서 멈추게 해 디스크를 양보하고 (같은 HDD 동시 읽기는 게임을 느리게 함),
    /// 단 auto.keep_stream_on_game_start (실험용) 가 켜져 있으면 그 게임의 스트림은 끝까지 읽습니다. 기록 없는 예열이 잡아 둔 예산은 풉니다 (게임이 돌면 RAM 이 달라지므로 다음 예산 계산에 쓰지 않음).
    public void NoteGameStart(string game)
    {
        OrbitModel.CancelInFlightGame(game);
        if (_warmStop is { } warm && _coldReading is { } warming && (string.Equals(warming, game, StringComparison.OrdinalIgnoreCase) || SameDisk(warming, game)))
        {
            try
            {
                warm.Cancel(); // 챔피언 선읽기 (WarmFiles) 도 같은 디스크 게임 시작에 바로 멈춤
            }
            catch (ObjectDisposedException)
            {
                // 읽기가 방금 끝남
            }
        }

        if (_streaming is { } reading)
        {
            bool same = string.Equals(reading, game, StringComparison.OrdinalIgnoreCase);
            bool sameDisk = same || SameDisk(reading, game);
            if (sameDisk)
            {
                _streamGameStarted = true;
                try
                {
                    _calibrationStop?.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // 표본 읽기가 방금 끝남
                }
            }

            if (same && _auto.KeepStreamOnGameStart)
            {
                _keepStream = true;
            }
            else if (sameDisk)
            {
                lock (_gate)
                {
                    _yield ??= (game, Stopwatch.GetTimestamp());
                }

                _streamCancelDetail?.Set(same ? "game started" : "another game started on the same disk");

                try
                {
                    _streamStop?.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // 스트림이 방금 끝남
                }
            }
        }

        lock (_gate)
        {
            _coldMib.Remove(game);
            _supplementRetry.Remove(game); // 플레이 뒤 재예열은 정체 기한을 넘겨서 다시 채울 수 있음
        }
    }

    // 두 게임이 같은 물리 디스크에 있는가 (env.SameDisk: 한 디스크에 볼륨이 둘이어도 같다고 봄).
    private bool SameDisk(string a, string b) =>
        _games.FirstOrDefault(g => string.Equals(g.Name, a, StringComparison.OrdinalIgnoreCase)) is { } x && _games.FirstOrDefault(g => string.Equals(g.Name, b, StringComparison.OrdinalIgnoreCase)) is { } y
        && _env.SameDisk(x.OriginRoot, y.OriginRoot);

    // 읽기 증가율이 이 값 미만이면 실행 중인 다른 게임을 한가하다고 봄 (디스크 한가함 기준 BusyBytesPerSecond 4 MiB/s 의 1/4: 상주 클라이언트의 잔 읽기는 넘기고 로딩은 넘지 않게).
    internal const double IdleGameReadBytesPerSecond = 1 << 20;

    // 이 게임이나 같은 디스크의 다른 등록 게임이 돌고 있는가 (다른 게임은 한가하지 않을 때만): 돌면 그 디스크에 새 읽기를 내지 않습니다.
    // running: 이미 찍은 스냅샷으로 판정하는 함수 (묶음 경계 점검이 한 번만 찍어 나눠 씀), 없으면 env.GameRunning.
    private bool DriveHasRunningGame(GameSettings game, Func<GameSettings, bool>? running = null) => (running ?? _env.GameRunning)(game) || BusyOthers(game, running).Count > 0;

    // 같은 디스크에서 돌고 있으면서 한가하지 않은 다른 등록 게임 (재예열 점검도 씀).
    internal List<GameSettings> BusyOthers(GameSettings game, Func<GameSettings, bool>? running = null)
    {
        running ??= _env.GameRunning;
        return [.. AutoRelocator.SameDriveGames(_games, [game], _env.SameDisk).Where(x => !string.Equals(x.Name, game.Name, StringComparison.OrdinalIgnoreCase) && running(x) && !OtherGameIdle(x))];
    }

    // 같은 디스크에서 우리 읽기가 도는 중인가: 그동안의 디스크 처리량에는 우리 읽기가 섞임 (우리 읽기량은 물리 읽기가 아니라 PVM 힌트 바이트라 빼서 보정할 수도 없음).
    private bool SelfReadingOn(GameSettings other) =>
        (_streaming ?? _coldReading) is { } reading && (string.Equals(reading, other.Name, StringComparison.OrdinalIgnoreCase) || SameDisk(reading, other.Name));

    // 읽는 도중 (핫 집합 스트림은 늘, 그 밖의 읽기는 같은 디스크에서 다른 등록 게임이 도는 동안) 이 간격마다 읽기를 잠깐 멈추고 외부 부하를 잼. 멈춤은 간격의 10% 이내.
    private static readonly TimeSpan DiskObserveEvery = TimeSpan.FromSeconds(10);

    // 멈춰 있는 관측 구간의 길이: DiskActivity 표본 하나 (1 초).
    private static readonly TimeSpan DiskObserveWindow = TimeSpan.FromSeconds(1);

    // 양보 관측의 멈춤 앞부분: PrefetchVirtualMemory 가 낸 읽기의 꼬리가 빠져나가게 비우고, 나머지 (DiskObserveWindow - 이 값) 만 잼. 멈춤 전체는 DiskObserveWindow 그대로.
    private static readonly TimeSpan DiskObserveDrain = TimeSpan.FromMilliseconds(200);

    // 읽는 중에 외부 디스크 부하가 바쁨 기준 이상인가: 같은 디스크의 다른 등록 게임이 도는 동안 DiskObserveEvery 마다 다음 묶음을 내기 전에 읽기를 멈추고 (DiskObserveWindow) 처리량을 잼. 못 읽는 고장 (Broken) 이면 바쁨으로 보지 않지만 (진입 시점의 실패만 바쁨) 표본이 아직 없거나 낡았으면 (Warming·Stale) 바쁨.
    // shortcut: 공유 표본의 1 초 창이 멈춤 앞의 우리 읽기를 포함할 수 있어 틀려도 취소 쪽으로만 어긋남 (핫 집합·앞부분·기록 없는 예열의 양보 관측은 ObserveReadYield 가 멈춘 구간만 직접 잼), 이 경로 (파일 예열) 가 필요해지면 같은 방식으로.
    private bool ExternalLoadWhilePaused(GameSettings game, CancellationToken stop, Func<GameSettings, bool>? running = null)
    {
        running ??= _env.GameRunning;
        if (_now() - _driveObservedAt < DiskObserveEvery
            || !AutoRelocator.SameDriveGames(_games, [game], _env.SameDisk).Any(x => !string.Equals(x.Name, game.Name, StringComparison.OrdinalIgnoreCase) && running(x)))
        {
            return false;
        }

        _env.Sleep(DiskObserveWindow, stop);
        _driveObservedAt = _now();
        return !stop.IsCancellationRequested && ExternalDiskBusy(game);
    }

    private bool ExternalDiskBusy(GameSettings game)
    {
        (double? bps, DiskActivity.SampleState state) = ExternalDiskSample(game);
        return DiskActivity.IsBusy(bps, state);
    }

    private (double? Bps, DiskActivity.SampleState State) ExternalDiskSample(GameSettings game) =>
        (_env.GameDiskSample ?? _env.DiskSample)?.Invoke(game) ?? DefaultSample(_env.DiskBytesPerSecond(game));

    // 직접 재는 수단이 없을 때 (시험): 멈춤 전체를 한 번 기다린 뒤 공유 표본을 읽음.
    private (double? Bps, DiskActivity.SampleState State) PausedSample(GameSettings game, CancellationToken stop)
    {
        _env.Sleep(DiskObserveWindow, stop);
        return ExternalDiskSample(game);
    }

    // 핫 집합 스트림의 양보 관측: 스트림이 시작 때 양보 상태였으면 첫 묶음 직전에 한 번, 그 뒤로는 DiskObserveEvery 마다 읽기를 DiskObserveWindow 만큼 멈추고 디스크 처리량을 잼.
    // 같은 디스크의 다른 등록 게임이 돌고 바쁘면 true (스트림 취소, 기존 규칙). 그 밖에는 취소 없이 읽기 스레드의 I/O 우선순위만 바꿈 (바쁘거나 모르면 낮음, 한가하면 보통).
    private bool ObserveReadYield(GameSettings game, CancellationToken stop, Func<GameSettings, bool> running, ReadYield yield)
    {
        if (!yield.FirstObservationPending && _now() - _driveObservedAt < DiskObserveEvery)
        {
            return false;
        }

        yield.FirstObservationPending = false;
        // 멈춘 구간만 직접 잼: 공유 표본은 2 초마다 1 초 창 하나라 창이 멈춤 앞의 우리 읽기를 포함할 수 있어 한가한 디스크도 바쁘게 읽힘.
        (double? bps, DiskActivity.SampleState state) = _env.DiskWindowSample is { } measure
            ? measure(game, () => _env.Sleep(DiskObserveDrain, stop), () => _env.Sleep(DiskObserveWindow - DiskObserveDrain, stop))
            : PausedSample(game, stop);
        _driveObservedAt = _now();
        if (stop.IsCancellationRequested)
        {
            return false;
        }

        bool otherGame = AutoRelocator.SameDriveGames(_games, [game], _env.SameDisk).Any(x => !string.Equals(x.Name, game.Name, StringComparison.OrdinalIgnoreCase) && running(x));
        ReadLoad load = PrewarmYield.Decide(otherGame, bps, state);
        if (load == ReadLoad.Cancel)
        {
            return true;
        }

        if (yield.Apply(load == ReadLoad.Yield, _now()))
        {
            _log.Info($"{game.Name}: prewarm yields to other disk activity (low I/O priority)");
        }

        return false;
    }

    private static (double?, DiskActivity.SampleState) DefaultSample(double? bps) => (bps, bps is null ? DiskActivity.SampleState.Broken : DiskActivity.SampleState.Ok);

    // 읽는 도중 디스크 표본이 아직 없거나 낡았으면 (Warming·Stale) 처리량을 모르므로 다음 묶음부터 읽지 않음 (묶음 경계마다 부름: 스트림·기록 없는 예열·파일 예열 공통).
    // 고장 (Broken) 은 여기서 읽기 허용으로 넓히지도 막지도 않고 기존 동작 그대로 (정체가 60 초 넘으면 Broken 이 되는 점은 알려진 한계, 낡은 표본 (4 초) 이 먼저 멈춤).
    private bool DiskSampleUnknown(GameSettings game) => _env.DiskSample?.Invoke(game).State is DiskActivity.SampleState.Warming or DiskActivity.SampleState.Stale;

    // 실행 중인 게임이 한가한가: 읽기 카운터 증가율 (직전 표본과의 차) 이 낮고 디스크 처리량도 (우리 읽기가 없을 때) 낮은 상태가 IdleNeeded 이상 이어졌을 때만 참.
    // 호출마다 (시각, 바이트) 를 기억하며 첫 표본만 있으면 아직 한가하지 않음. 조회 실패 (null)·카운터 감소 (프로세스 교체) 는 바쁨. 표본 간격이 1 초 미만이면 1 초로 셈 (잔 읽기를 큰 비율로 보지 않게).
    private bool OtherGameIdle(GameSettings other)
    {
        long? bytes = _env.GameReadBytes(other);
        bool diskBusy = !SelfReadingOn(other) && (_env.DiskBytesPerSecond(other) is not { } bps || bps >= DiskActivity.BusyBytesPerSecond); // 우리 읽기 중이면 처리량 판정을 하지 않음 (자기 I/O 를 외부 부하로 오인해 한가함 기록을 지우지 않게)
        DateTimeOffset now = _now();
        lock (_gate)
        {
            if (bytes is not { } total)
            {
                _readSample.Remove(other.Name);
                _gameIdleSince.Remove(other.Name);
                return false;
            }

            if (!_readSample.TryGetValue(other.Name, out (DateTimeOffset At, long Bytes) prev))
            {
                _readSample[other.Name] = (now, total);
                _gameIdleSince.Remove(other.Name);
                return false;
            }

            double secs = (now - prev.At).TotalSeconds;
            bool busy = diskBusy || total < prev.Bytes || (total - prev.Bytes) / Math.Max(secs, 1) >= IdleGameReadBytesPerSecond;
            if (busy || secs >= 1)
            {
                _readSample[other.Name] = (now, total);
            }

            if (busy)
            {
                _gameIdleSince.Remove(other.Name);
                return false;
            }

            if (!_gameIdleSince.TryGetValue(other.Name, out DateTimeOffset since))
            {
                _gameIdleSince[other.Name] = since = prev.At;
            }

            return now - since >= IdleNeeded;
        }
    }

    // 재예열·보충 대상과 같은 디스크의 실행 중인 게임을 틱마다 표본으로 남김 (한가함 판정에 쓸 기록이 쌓이게), 안 도는 게임의 표본은 지움 (다음 실행의 카운터와 섞이지 않게).
    private void SampleDriveGames()
    {
        foreach (GameSettings g in AutoRelocator.SameDriveGames(_games, Warmed(), _env.SameDisk).DistinctBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (_env.GameRunning(g))
            {
                OtherGameIdle(g);
                continue;
            }

            lock (_gate)
            {
                _readSample.Remove(g.Name);
                _gameIdleSince.Remove(g.Name);
            }
        }
    }

    // 읽기를 끊을 만큼 게임이 도는가. keep_stream_on_game_start 가 켜졌으면 읽던 게임 자신의 실행만 무시하고, 같은 디스크의 다른 등록 게임 (한가하지 않은 것) 은 설정과 무관하게 봅니다.
    private bool RunningBlocksRead(GameSettings game, Func<GameSettings, bool>? running = null) => !_auto.KeepStreamOnGameStart
        ? DriveHasRunningGame(game, running)
        : BusyOthers(game, running).Count > 0;

    // 건너뛴 사유 로그용: 이 디스크에서 게임이 돈다고 판정한 근거 프로세스 (없으면 빈 문자열). 건너뛰어 로그를 남길 때만 부름.
    private string BlockerNote(GameSettings game)
    {
        string blockers = string.Join("; ", AutoRelocator.SameDriveGames(_games, [game], _env.SameDisk).Append(game).DistinctBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(_env.DescribeBlockers).Where(b => b is not null));
        return blockers.Length == 0 ? string.Empty : $" [blocked by {blockers}]";
    }

    // 게임 시작으로 멈춘 스트림이면 양보 시간과 읽은 양을 로그에 남기고 표시를 지웁니다.
    private void LogYield(string streamGame, long bytes, DiskReadCoordinator.Lease? lease = null)
    {
        (string Game, long At)? y;
        lock (_gate)
        {
            (y, _yield) = (_yield, null);
        }

        if (y is { } v)
        {
            string activity = lease?.ReadActivityDetail() ?? "stage unknown, no issue timing available, request size unknown (metadata)";
            _log.Info($"{streamGame}: yielded to {v.Game} after {(long)Stopwatch.GetElapsedTime(v.At).TotalMilliseconds} ms, {bytes >> 20} MiB");
            _log.Info($"{streamGame}: yield activity; {activity}");
        }
    }
}
