using AsterCache.Core.Settings;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// config.json 다시 읽기: 바뀜 판정, 잘못된 파일, 감시기·디스패처 교체.
public sealed class ReloadTests
{
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(20);
    private readonly ListLog _log = new();

    private sealed class Probe : IProcessWatcher
    {
        private readonly TaskCompletionSource _started = new();
        private Action<ProcessStart>? _onStart;

        public bool Cancelled { get; private set; }

        public Task Started => _started.Task;

        public void Emit(ProcessStart s) => _onStart!(s);

        public async Task RunAsync(Action<ProcessStart> onStart, CancellationToken cancellationToken)
        {
            _onStart = onStart;
            _started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 취소는 정상 종료
            }

            Cancelled = true;
        }
    }

    private static AsterSettings One(int reapplyMinutes = 60) => new(50, [Games.Make("a", "a.exe") with { ReapplyMinutes = reapplyMinutes }]);

    private static AsterSettings Two() => new(50, [Games.Make("a", "a.exe"), Games.Make("b", "b.exe")]);

    private static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    // ---- SettingsReloader ----

    [Fact]
    public void ChangedStampLoadsOnceAndUnchangedOrMissingFileKeepsTheOldSettings()
    {
        (long, long)? stamp = (1, 1);
        int loads = 0;
        var r = new SettingsReloader(() => stamp, () =>
        {
            loads++;
            return Two();
        }, s => (new Probe(), null), _log, (1, 1));

        Assert.Null(r.Poll());
        stamp = (2, 1);
        Assert.NotNull(r.Poll());
        Assert.Null(r.Poll());
        stamp = null; // 파일이 사라짐
        Assert.Null(r.Poll());
        Assert.Null(r.Poll());
        stamp = (3, 1); // 돌아온 파일이 바뀌어 있으면 다시 읽음
        Assert.NotNull(r.Poll());
        Assert.Equal(2, loads);
    }

    [Fact]
    public void InvalidFileKeepsTheOldSettingsAndIsNotReadAgainUntilItChanges()
    {
        (long, long)? stamp = (1, 1);
        int loads = 0;
        bool valid = false;
        var r = new SettingsReloader(() => stamp, () =>
        {
            loads++;
            return valid ? Two() : throw new SettingsException("games: list expected");
        }, s => (new Probe(), null), _log, (1, 1));

        stamp = (2, 1);
        Assert.Null(r.Poll());
        Assert.Null(r.Poll());
        Assert.Equal(1, loads);
        Assert.Contains(_log.Lines, l => l.StartsWith("Error: config.json changed but is invalid", StringComparison.Ordinal) && l.Contains("games: list expected", StringComparison.Ordinal));

        valid = true;
        stamp = (3, 1);
        Assert.NotNull(r.Poll());
    }

    [Fact]
    public void ReadErrorIsRetriedOnTheNextPollAndLoggedOnce()
    {
        (long, long)? stamp = (2, 1);
        bool failing = true;
        var r = new SettingsReloader(() => stamp, () => failing ? throw new IOException("locked") : Two(), s => (new Probe(), null), _log, (1, 1));

        Assert.Null(r.Poll());
        Assert.Null(r.Poll());
        failing = false;

        Assert.NotNull(r.Poll());
        Assert.Single(_log.Lines, l => l.StartsWith("Warn: config.json could not be read", StringComparison.Ordinal));
    }

    [Fact]
    public void StampComesFromLengthAndWriteTime()
    {
        string path = Path.Combine(Path.GetTempPath(), "aster_stamp_" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Null(SettingsReloader.StampOf(path));
            File.WriteAllText(path, "{}");
            (long Length, long Ticks)? a = SettingsReloader.StampOf(path);
            File.WriteAllText(path, "{ }");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

            Assert.Equal(2, a!.Value.Length);
            Assert.NotEqual(a, SettingsReloader.StampOf(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- Worker ----

    private sealed class Rig
    {
        public Rig(AsterSettings first, Func<AsterSettings> load)
        {
            Initial = new Probe();
            Created.Add(Initial);
            Reloader = new SettingsReloader(() => (Stamp, 0), load, s =>
            {
                var w = new Probe();
                lock (Created)
                {
                    Created.Add(w);
                }

                return (w, null);
            }, Log, (Stamp, 0), Fast);
            Worker = new Worker(first, Log, Initial, null, Streamer, new ReapplyChecker(ReapplyTests.NoIoEnv(), Log), reload: Reloader);
        }

        public long Stamp { get; set; } = 1;

        public ListLog Log { get; } = new();

        public FakeStreamer Streamer { get; } = new();

        public Probe Initial { get; }

        public List<Probe> Created { get; } = [];

        public SettingsReloader Reloader { get; }

        public Worker Worker { get; }

        public int WatcherCount
        {
            get
            {
                lock (Created)
                {
                    return Created.Count;
                }
            }
        }

        public Probe Latest
        {
            get
            {
                lock (Created)
                {
                    return Created[^1];
                }
            }
        }
    }

    [Fact]
    public async Task NewGameInTheConfigIsWatchedAndStreamedWithoutRestartingTheService()
    {
        var r = new Rig(One(), Two);
        await r.Worker.StartAsync(CancellationToken.None);
        await r.Initial.Started;

        r.Stamp = 2;
        await Until(() => r.WatcherCount == 2 && r.Latest.Started.IsCompleted);
        r.Latest.Emit(new ProcessStart(5, "b.exe", 0));
        await Until(() => !r.Streamer.Calls.IsEmpty);
        await r.Worker.StopAsync(CancellationToken.None);

        Assert.True(r.Initial.Cancelled);
        Assert.Equal("b", Assert.Single(r.Streamer.Calls).Game);
        Assert.Contains(r.Log.Lines, l => l.Equals("Info: settings reloaded: 2 game(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidConfigKeepsWatchingTheOldGamesThenAValidOneTakesOver()
    {
        bool valid = false;
        var r = new Rig(One(), () => valid ? Two() : throw new SettingsException("poll_ms: out of range"));
        await r.Worker.StartAsync(CancellationToken.None);
        await r.Initial.Started;

        r.Stamp = 2;
        await Until(() => r.Log.Lines.Any(l => l.Contains("keeping the previous settings", StringComparison.Ordinal)));
        Assert.Equal(1, r.WatcherCount);
        Assert.False(r.Initial.Cancelled);
        r.Initial.Emit(new ProcessStart(5, "a.exe", 0)); // 옛 설정으로 계속 동작
        await Until(() => !r.Streamer.Calls.IsEmpty);

        valid = true;
        r.Stamp = 3;
        await Until(() => r.WatcherCount == 2);
        await r.Worker.StopAsync(CancellationToken.None);

        Assert.Equal("a", r.Streamer.Calls.First().Game);
    }

    [Fact]
    public async Task ChangeThatKeepsTheWatchedNamesDoesNotRestartTheWatcher()
    {
        var r = new Rig(One(60), () => One(5));
        await r.Worker.StartAsync(CancellationToken.None);
        await r.Initial.Started;

        r.Stamp = 2;
        await Until(() => r.Log.Lines.Any(l => l.Contains("settings reloaded", StringComparison.Ordinal)));
        r.Stamp = 3;
        await Until(() => r.Log.Lines.Count(l => l.Contains("settings reloaded", StringComparison.Ordinal)) >= 2); // 다시 읽기는 한 스레드라 첫 번째 적용 (감시기 재시작 판단 포함) 이 끝난 뒤에야 두 번째가 로그에 나옴

        Assert.Equal(1, r.WatcherCount);
        Assert.False(r.Initial.Cancelled);
        await r.Worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ServiceStartedWithNoGamesWaitsForTheConfigAndStartsWatchingWhenAGameAppears()
    {
        var r = new Rig(new AsterSettings(50, []), () => One());
        await r.Worker.StartAsync(CancellationToken.None);
        await Until(() => r.Log.Lines.Any(l => l.StartsWith("Info: service started: no games registered yet", StringComparison.Ordinal)));

        Assert.False(r.Initial.Started.IsCompleted); // 감시할 이름이 없으면 감시기를 돌리지 않음
        Assert.Contains(r.Log.Lines, l => l.StartsWith("Info: service started: no games registered yet", StringComparison.Ordinal));

        r.Stamp = 2;
        await Until(() => r.WatcherCount == 2 && r.Latest.Started.IsCompleted);
        r.Latest.Emit(new ProcessStart(5, "a.exe", 0));
        await Until(() => !r.Streamer.Calls.IsEmpty);
        await r.Worker.StopAsync(CancellationToken.None);

        Assert.Equal("a", Assert.Single(r.Streamer.Calls).Game);
    }
}
