using System.Collections.Concurrent;
using System.Diagnostics;
using AsterCache.Core.Settings;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 챔피언 선택 선읽기: 단계 전환, 챔피언 목록·alias 매핑·잠금 우선, 취소 (ChampSelect 이탈·게임 exe 시작), 예산, lockfile, 실패 폴백·백오프, 설정 꺼짐. 클라이언트 API 는 가짜.
public sealed class RiotPhaseWatcherTests : IAsyncDisposable
{
    private const long Mib = 1L << 20;
    private const string Lock = "LeagueClient:1234:51234:s3cret:https";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private readonly ListLog _log = new();
    private readonly FakeClient _client = new();
    private readonly ConcurrentQueue<TimeSpan> _delays = new();
    private readonly ConcurrentQueue<(string[] Files, long Budget)> _warm = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _run;
    private int _created;
    private int _lockReads;
    private int _clientCalls;
    private bool _clientRunning = true;
    private bool _gameRunning;
    private string? _lockText = Lock;
    private bool _alive = true;
    private AsterSettings _settings;
    private Func<string[], long, CancellationToken, long> _warmFiles = (_, _, _) => 0;

    public RiotPhaseWatcherTests() => _settings = Settings(true);

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_run is not null)
        {
            await _run.WaitAsync(Bound);
        }

        _stop.Dispose();
    }

    private static GameSettings Lol() => Games.Make("lol", "LeagueClient.exe", "League of Legends.exe") with { AntiCheat = "vanguard", OriginRoot = @"C:\Riot Games\League of Legends" };

    private static AsterSettings Settings(bool on, int budgetMib = 2048, params GameSettings[] games) =>
        new(50, games.Length > 0 ? games : [Lol()]) { Riot = new RiotSettings(on, budgetMib) };

    private sealed class FakeClient : ILcuClient
    {
        public Func<string?> Phase { get; set; } = () => "ChampSelect";

        public Func<int?> MapId { get; set; } = () => 11;

        public Func<ChampSelectState?> State { get; set; } = () => new ChampSelectState([], []);

        public Func<IReadOnlyDictionary<int, string>> Aliases { get; set; } = () => new Dictionary<int, string>();

        public int PhaseCalls;
        public int AliasCalls;
        public int StateCalls;
        public bool Disposed;

        public Task<string?> GetPhaseAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref PhaseCalls);
            return Task.FromResult(Phase());
        }

        public Task<int?> GetMapIdAsync(CancellationToken ct) => Task.FromResult(MapId());

        public Task<ChampSelectState?> GetChampSelectAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref StateCalls);
            return Task.FromResult(State());
        }

        public Task<IReadOnlyDictionary<int, string>> GetChampionAliasesAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref AliasCalls);
            return Task.FromResult(Aliases());
        }

        public void Dispose() => Disposed = true;
    }

    private RiotPhaseWatcher Make(RiotWatchTiming? timing = null)
    {
        var env = new RiotWatchEnv
        {
            Timing = timing ?? new RiotWatchTiming(),
            ClientRunning = () => { Interlocked.Increment(ref _clientCalls); return _clientRunning; },
            GameExeRunning = _ => _gameRunning,
            ClientAlive = _ => _alive,
            ReadLockfile = path =>
            {
                Interlocked.Increment(ref _lockReads);
                Assert.EndsWith(@"League of Legends\lockfile", path, StringComparison.Ordinal);
                return _lockText;
            },
            CreateClient = _ =>
            {
                Interlocked.Increment(ref _created);
                return _client;
            },
            WarmFiles = (_, files, budget, ct) =>
            {
                string[] f = [.. files];
                _warm.Enqueue((f, budget));
                return _warmFiles(f, budget, ct);
            },
            Locales = _ => ["ko_KR"],
            Delay = (t, ct) =>
            {
                _delays.Enqueue(t);
                return Task.Delay(2, ct);
            },
        };
        return new RiotPhaseWatcher(() => _settings, env, _log);
    }

    private void Start(RiotPhaseWatcher w) => _run = Task.Run(() => w.RunAsync(_stop.Token));

    private static async Task Until(Func<bool> condition, string what)
    {
        DateTime end = DateTime.UtcNow + Bound;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < end, "timed out waiting for: " + what);
            await Task.Delay(5);
        }
    }

    private static string Champ(string alias) => $@"C:\Riot Games\League of Legends\Game\DATA\FINAL\Champions\{alias}.wad.client";

    private static string[] ChampFiles(string alias) => [Champ(alias), Champ(alias).Replace(".wad.client", ".ko_KR.wad.client", StringComparison.Ordinal)];

    private static string[] MapFiles(int id) =>
    [
        $@"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping\Map{id}.wad.client",
        $@"C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping\Map{id}.ko_KR.wad.client",
    ];

    [Fact]
    public async Task SettingOffMeansNoClientCallsAndNoLockfileRead()
    {
        _settings = Settings(false);
        Start(Make());

        await Until(() => _delays.Count >= 20, "idle loops");

        Assert.Equal((0, 0, 0, 0), (_created, _lockReads, _client.PhaseCalls, _clientCalls));
        Assert.Empty(_warm);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("champion select", StringComparison.Ordinal)); // 꺼져 있으면 로그도 없음
    }

    [Fact]
    public async Task OtherGamesAreNotAffected()
    {
        GameSettings noVanguard = Lol() with { AntiCheat = "eac" };
        GameSettings other = Games.Make("other", "other.exe") with { AntiCheat = "vanguard" }; // vanguard 지만 LeagueClient.exe 가 없음 (예: 발로란트)
        _settings = Settings(true, 2048, noVanguard, other);
        Start(Make());

        await Until(() => _delays.Count >= 20, "idle loops");

        Assert.Equal((0, 0, 0), (_created, _lockReads, _client.PhaseCalls));
    }

    [Fact]
    public async Task ClientNotRunningMeansNoLockfileOrApiCalls()
    {
        _clientRunning = false;
        Start(Make());

        await Until(() => _delays.Count >= 20, "idle loops");

        Assert.Equal((0, 0), (_lockReads, _created));
    }

    [Theory]
    [InlineData("LeagueClient:1234:51234:s3cret", true)] // 필드 수 틀림
    [InlineData("LeagueClient:1234:51234:s3cret:https:x", true)]
    [InlineData(null, true)] // lockfile 없음
    [InlineData(Lock, false)] // PID 가 없음 (클라이언트가 이미 끝남)
    public async Task BadOrStaleLockfileCreatesNoClientAndLogsOnce(string? text, bool aliveFlag)
    {
        _lockText = text;
        _alive = aliveFlag;
        Start(Make());

        await Until(() => _delays.Count >= 30, "idle loops");

        Assert.Equal(0, _created);
        Assert.Equal(0, _client.PhaseCalls);
        Assert.Single(_log.Lines, l => l.Contains("lockfile is missing, malformed or stale", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadsLockedChampionsThenHoveredThenTheMapWithLocaleSuffixes()
    {
        _client.Aliases = () => new Dictionary<int, string> { [1] = "Aatrox", [2] = "Ahri", [3] = "Annie" };
        _client.State = () => new ChampSelectState([2], [1, 3]);
        _client.MapId = () => 11;
        Start(Make());

        await Until(() => _warm.Count >= 4, "four warm calls");

        Assert.Equal([ChampFiles("Ahri"), ChampFiles("Aatrox"), ChampFiles("Annie"), MapFiles(11)], _warm.Take(4).Select(w => w.Files));
        Assert.Equal(1, _client.AliasCalls);
        Assert.Contains(_log.Lines, l => l.Contains("champion select prewarm enabled (disabled by default), started", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AChampionIsReadOnceEvenWhenItIsHoveredThenLockedAndNewOnesAreAddedLater()
    {
        _client.Aliases = () => new Dictionary<int, string> { [5] = "Brand", [7] = "Caitlyn" };
        int pass = 0;
        _client.State = () => ++pass switch
        {
            1 => new ChampSelectState([], [5]),
            2 => new ChampSelectState([7], [5]),
            _ => new ChampSelectState([7, 5], []),
        };
        Start(Make());

        await Until(() => pass >= 4 && _warm.Count >= 3, "several passes");

        Assert.Equal([ChampFiles("Brand"), MapFiles(11), ChampFiles("Caitlyn")], _warm.Select(w => w.Files).Take(3)); // 호버 5 -> (첫 패스의 맵) -> 새로 잠긴 7, 5 는 다시 읽지 않음
        Assert.Equal(3, _warm.Count);
    }

    [Fact]
    public async Task BudgetIsSpentAcrossCallsAndStopsTheSession()
    {
        _client.Aliases = () => new Dictionary<int, string> { [1] = "Aatrox", [2] = "Ahri", [3] = "Annie" };
        _client.State = () => new ChampSelectState([1, 2, 3], []);
        _warmFiles = (_, _, _) => 60 * Mib;
        _settings = Settings(true, 100);
        Start(Make());

        await Until(() => _warm.Count >= 2, "two warm calls");
        await Task.Delay(100);

        Assert.Equal([100 * Mib, 40 * Mib], _warm.Select(w => w.Budget)); // 남은 예산이 2 번째 호출에 넘어가고, 다 쓰면 더 읽지 않음
    }

    [Fact]
    public async Task LeavingChampSelectCancelsTheReadAtOnceWithoutAnException()
    {
        _client.Aliases = () => new Dictionary<int, string> { [1] = "Aatrox" };
        _client.State = () => new ChampSelectState([1], []);
        bool cancelled = false;
        int inside = 0;
        _warmFiles = (_, _, ct) =>
        {
            Interlocked.Increment(ref inside);
            ct.WaitHandle.WaitOne(Bound); // WarmFiles 가 읽는 중: 취소 신호만 기다림
            cancelled = ct.IsCancellationRequested;
            return 0;
        };
        Start(Make());
        await Until(() => inside > 0, "read in progress");

        _client.Phase = () => "GameStart"; // 챔피언 선택 이탈

        await Until(() => cancelled, "cancellation");
        await Until(() => _log.Lines.Any(l => l.Contains("champion select prewarm cancelled", StringComparison.Ordinal)), "session end log");
        Assert.False(_run!.IsFaulted);
        Assert.DoesNotContain(_log.Lines, l => l.StartsWith("Error", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GameExeStartCancelsTheReadAndDoesNotReopenWhileThePhaseStaysChampSelect()
    {
        _client.Aliases = () => new Dictionary<int, string> { [1] = "Aatrox" };
        _client.State = () => new ChampSelectState([1], []);
        bool cancelled = false;
        int inside = 0;
        _warmFiles = (_, _, ct) =>
        {
            Interlocked.Increment(ref inside);
            ct.WaitHandle.WaitOne(Bound);
            cancelled = ct.IsCancellationRequested;
            return 0;
        };
        RiotPhaseWatcher w = Make();
        Start(w);
        await Until(() => inside > 0, "read in progress");

        w.NoteProcessStart("LeagueClient.exe"); // 클라이언트 자신은 취소하지 않음
        await Task.Delay(50);
        Assert.False(cancelled);

        w.NoteProcessStart("League of Legends.exe");
        await Until(() => cancelled, "cancellation by the game exe");
        int after = inside;
        await Task.Delay(100); // 단계가 아직 ChampSelect 로 보여도 새 세션을 열지 않음
        Assert.Equal(after, Volatile.Read(ref inside));

        _client.Phase = () => "None";
        await Until(() => _client.PhaseCalls > 0 && _log.Lines.Any(l => l.Contains("cancelled", StringComparison.Ordinal)), "left champ select");
        int calls = _client.PhaseCalls;
        await Until(() => _client.PhaseCalls > calls + 2, "polling continues");
        _client.Phase = () => "ChampSelect"; // 다음 챔피언 선택에서는 다시 읽음
        await Until(() => Volatile.Read(ref inside) > after, "new session");
    }

    [Fact]
    public async Task RunningGameExeBlocksSessions()
    {
        _gameRunning = true;
        Start(Make());

        await Until(() => _delays.Count >= 20, "loops");

        Assert.Equal((0, 0), (_client.PhaseCalls, _warm.Count));
    }

    [Fact]
    public async Task ApiFailuresBackOffDoublingUpToTheCapLogOnceAndRecover()
    {
        int fail = 5;
        _client.Phase = () => Interlocked.Decrement(ref fail) >= 0 ? throw new HttpRequestException("boom") : "None";
        var timing = new RiotWatchTiming { PhasePoll = TimeSpan.FromSeconds(3), MaxBackoff = TimeSpan.FromSeconds(60), Idle = TimeSpan.FromSeconds(10) };
        Start(Make(timing));

        await Until(() => _delays.Count >= 8, "backoff delays");

        Assert.Equal([6, 12, 24, 48, 60], _delays.Take(5).Select(d => (int)d.TotalSeconds)); // 연속 실패마다 2 배, 상한 60 초
        Assert.Equal(3, (int)_delays.ElementAt(5).TotalSeconds); // 성공하면 보통 간격으로 돌아옴
        Assert.Single(_log.Lines, l => l.Contains("not reachable", StringComparison.Ordinal)); // 한 구간에 로그 1 줄
        Assert.DoesNotContain(_log.Lines, l => l.Contains("s3cret", StringComparison.Ordinal));
        Assert.False(_run!.IsFaulted);
    }

    [Fact]
    public async Task ChangedLockfileMakesANewClientAndDisposesTheOldOne()
    {
        _client.Phase = () => "None";
        Start(Make());
        await Until(() => _created == 1 && _client.PhaseCalls > 2, "first client");

        _lockText = "LeagueClient:999:60000:other:https";

        await Until(() => _created == 2, "second client");
        Assert.True(_client.Disposed);
    }

    [Fact]
    public async Task ChampionListIsFetchedOnceAcrossSessions()
    {
        _client.Aliases = () => new Dictionary<int, string> { [1] = "Aatrox" };
        _client.State = () => new ChampSelectState([1], []);
        Start(Make());
        await Until(() => _warm.Count >= 2, "first session");

        _client.Phase = () => "None";
        int calls = _client.PhaseCalls;
        await Until(() => _client.PhaseCalls >= calls + 2, "old session ended"); // None 을 읽은 감시 단계가 세션을 정리한 뒤 다음 단계까지 끝남
        int before = _warm.Count;
        _client.Phase = () => "ChampSelect";
        await Until(() => _warm.Count >= before + 2, "second session");

        Assert.Equal(1, _client.AliasCalls);
    }

    [Fact]
    public async Task ChampionListFailureStillReadsTheMap()
    {
        _client.Aliases = () => throw new HttpRequestException("no list");
        _client.State = () => new ChampSelectState([1], []);
        Start(Make());

        await Until(() => _warm.Count >= 1, "map read");

        Assert.Equal(MapFiles(11), _warm.First().Files);
    }

    [Fact]
    public async Task NoPassWithoutAChampSelectStateEndsTheSessionQuietly()
    {
        _client.State = () => null; // 404: 이미 챔피언 선택이 아님
        Start(Make());

        await Until(() => _client.StateCalls > 0 && _log.Lines.Any(l => l.Contains("finished", StringComparison.Ordinal)), "session end");

        Assert.Empty(_warm);
    }

    [Fact]
    public async Task ServiceShutdownEndsTheLoopCleanlyEvenMidRead()
    {
        _client.Aliases = () => new Dictionary<int, string> { [1] = "Aatrox" };
        _client.State = () => new ChampSelectState([1], []);
        int inside = 0;
        _warmFiles = (_, _, ct) =>
        {
            Interlocked.Increment(ref inside);
            ct.WaitHandle.WaitOne(Bound);
            return 0;
        };
        Start(Make());
        await Until(() => inside > 0, "read in progress");

        _stop.Cancel();

        await _run!.WaitAsync(Bound);
        Assert.True(_run.IsCompletedSuccessfully);
        Assert.True(_client.Disposed);
    }

    [Fact]
    public async Task PasswordNeverAppearsInTheLog()
    {
        _client.Aliases = () => new Dictionary<int, string> { [1] = "Aatrox" };
        _client.State = () => new ChampSelectState([1], []);
        Start(Make());
        await Until(() => _warm.Count >= 2, "reads");

        Assert.DoesNotContain(_log.Lines, l => l.Contains("s3cret", StringComparison.Ordinal));
    }

    private sealed class CapturingWatcher : IProcessWatcher
    {
        public Action<ProcessStart>? OnStart { get; private set; }

        public async Task RunAsync(Action<ProcessStart> onStart, CancellationToken cancellationToken)
        {
            OnStart = onStart;
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 종료
            }
        }
    }

    [Fact]
    public async Task WorkerRunsTheLoopRoutesTheGameExeStartAndStopsCleanly()
    {
        _client.Aliases = () => new Dictionary<int, string> { [1] = "Aatrox" };
        _client.State = () => new ChampSelectState([1], []);
        bool cancelled = false;
        int inside = 0;
        _warmFiles = (_, _, ct) =>
        {
            Interlocked.Increment(ref inside);
            ct.WaitHandle.WaitOne(Bound);
            cancelled = ct.IsCancellationRequested;
            return 0;
        };
        var watcher = new CapturingWatcher();
        var worker = new Worker(_settings, _log, watcher, null, new FakeStreamer(), new ReapplyChecker(ReapplyTests.NoIoEnv(), _log), riot: Make());

        await worker.StartAsync(CancellationToken.None);
        await Until(() => inside > 0 && watcher.OnStart is not null, "worker started the riot loop");

        watcher.OnStart!(new ProcessStart(5, "League of Legends.exe", Stopwatch.GetTimestamp()));
        await Until(() => cancelled, "game exe start reached the riot watcher");

        await worker.StopAsync(CancellationToken.None);
        Assert.True(_client.Disposed);
    }
}
