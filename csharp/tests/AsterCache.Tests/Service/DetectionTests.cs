using System.Collections.Concurrent;
using System.Security.AccessControl;
using System.Security.Principal;
using AsterCache.Core;
using AsterCache.Core.Logging;
using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Service;
using AsterCache.Tests.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

internal sealed class ListLog : ILog
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyCollection<string> Lines => _lines;

    public void Write(LogLevel level, string message, Exception? exception = null) =>
        _lines.Enqueue($"{level}: {message}" + (exception is null ? string.Empty : " | " + exception.Message));
}

internal sealed class FakeStreamer : IGameStreamer
{
    public ConcurrentQueue<(string Game, long At)> Calls { get; } = new();

    public Exception? Throw { get; set; }

    public StreamStats Stream(GameSettings game)
    {
        Calls.Enqueue((game.Name, System.Diagnostics.Stopwatch.GetTimestamp()));
        return Throw is null ? new StreamStats(100, 2, 0, 0.01) : throw Throw;
    }
}

internal static class Games
{
    public static GameSettings Make(string name, params string[] exes) => new(name, exes, @"C:\p\" + name + ".prefetch.json", @"C:\p\" + name + ".plan.json",
        @"D:\Games\" + name, [@"D:\Games"], "GAMES", 60);
}

public sealed class PollingWatcherTests
{
    [Fact]
    public async Task ReportsOnlyNewWatchedProcessesNotTheBaseline()
    {
        var steps = new List<(int, string)[]>
        {
            new[] { (1, "a.exe"), (2, "game.exe") },                   // 기준선: 이미 떠 있는 game.exe 는 알리지 않음
            new[] { (1, "a.exe"), (2, "game.exe") },
            new[] { (1, "a.exe"), (2, "game.exe"), (3, "GAME.EXE"), (9, "other.exe") }, // 새 game (대소문자 다름), 감시 밖 other
            new[] { (1, "a.exe"), (2, "tool.exe"), (9, "other.exe") },  // 3 종료, PID 2 는 다른 이름
            new[] { (1, "a.exe"), (2, "game.exe") },                    // PID 2 가 game.exe 로 재사용 → 새 프로세스
            new[] { (1, "a.exe"), (2, "game.exe") },
        };
        int i = 0;
        using var cts = new CancellationTokenSource();
        var found = new List<ProcessStart>();
        var watcher = new PollingProcessWatcher(["game.exe"], TimeSpan.FromMilliseconds(5), () =>
        {
            IReadOnlyList<(int, string)> now = steps[Math.Min(i, steps.Count - 1)];
            if (++i > steps.Count)
            {
                cts.Cancel();
            }

            return now;
        });

        await watcher.RunAsync(found.Add, cts.Token);

        Assert.Equal([3, 2], found.Select(s => s.Pid));
        Assert.All(found, s => Assert.Equal("game.exe", s.ExeName, ignoreCase: true));
    }
}

public sealed class EtwWatcherTests
{
    [Theory]
    [InlineData(@"\Device\HarddiskVolume3\Windows\System32\cmd.exe", "cmd.exe")]
    [InlineData(@"C:\Games\MW4\game.exe", "game.exe")]
    [InlineData("game.exe", "game.exe")]
    [InlineData(@"\Device\HarddiskVolume3\dir\", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ExeNameIsTheLastPathPart(string? image, string? expected)
    {
        Assert.Equal(expected, EtwProcessWatcher.ExeNameOf(image));
    }

    [Fact]
    public void RawProcessStartPayloadYieldsPidAndImageWithoutTdh()
    {
        // 버전 4 배치: ProcessID, ProcessSequenceNumber, CreateTime, ParentProcessID, ParentSequenceNumber, SessionID, Flags, ElevationType, IsElevated, SID, ImageName, ...
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.Unicode, leaveOpen: true))
        {
            w.Write(4242u);
            w.Write(7UL);
            w.Write(133000000000000000L);
            w.Write(100u);
            w.Write(8UL);
            w.Write(1u);
            w.Write(0u);
            w.Write(2u);
            w.Write(1u);
            w.Write(new byte[] { 1, 1, 0, 0, 0, 0, 0, 16, 0, 48, 0, 0 }); // S-1-16-12288
            w.Write(System.Text.Encoding.Unicode.GetBytes(@"\Device\HarddiskVolume3\Windows\System32\cmd.exe" + "\0"));
            w.Write(0x1234u);
            w.Write(0x5678u);
            w.Write(System.Text.Encoding.Unicode.GetBytes("Package_1.0_x64__abc\0"));
        }

        Assert.True(ProcessStartPayload.TryParse(ms.ToArray(), out int pid, out string? image));
        Assert.Equal(4242, pid);
        Assert.Equal(@"\Device\HarddiskVolume3\Windows\System32\cmd.exe", image);
        Assert.Equal("cmd.exe", EtwProcessWatcher.ExeNameOf(image));
        Assert.False(ProcessStartPayload.TryParse(new byte[4], out _, out _));
        Assert.False(ProcessStartPayload.TryParse(new byte[64], out _, out _));
    }

    [Fact]
    public void NonAdministratorGetsNotAdminBeforeAnySessionExists()
    {
        var watcher = new EtwProcessWatcher(["game.exe"], () => false);

        Assert.Throws<NotAdminException>(() => { _ = watcher.RunAsync(_ => { }, CancellationToken.None); });
    }

    [Fact]
    public void WorkerChoosesEtwOnlyForAdministratorsAndKeepsPollingAsFallback()
    {
        var s = new AsterSettings(250, [Games.Make("g", "game.exe")], "etw");

        (IProcessWatcher p1, IProcessWatcher? f1) = Worker.ChooseWatchers(s, admin: false);
        (IProcessWatcher p2, IProcessWatcher? f2) = Worker.ChooseWatchers(s, admin: true);

        Assert.IsType<PollingProcessWatcher>(p1);
        Assert.Null(f1);
        Assert.IsType<EtwProcessWatcher>(p2);
        Assert.IsType<PollingProcessWatcher>(f2);
    }

    [Fact]
    public void PollingIsTheDefaultEvenForAdministrators()
    {
        var s = new AsterSettings(250, [Games.Make("g", "game.exe")]);

        (IProcessWatcher p, IProcessWatcher? f) = Worker.ChooseWatchers(s, admin: true);

        Assert.Equal("poll", s.Watcher);
        Assert.IsType<PollingProcessWatcher>(p);
        Assert.Null(f);
    }

    [Fact]
    public void FlushingAMissingSessionReturnsAnErrorCodeInsteadOfThrowing()
    {
        Assert.NotEqual(0u, EtwFlush.Flush("AsterCache-NoSuchSession-" + Guid.NewGuid().ToString("N")));
    }
}

public sealed class DispatcherTests
{
    private static readonly GameSettings Mw4 = Games.Make("mw4", "game.exe", "launcher.exe");
    private static readonly GameSettings Other = Games.Make("other", "other.exe");

    private static ProcessStart Start(string exe) => new(1, exe, 0);

    [Fact]
    public void DetectedExeStreamsItsOwnGameCaseInsensitively()
    {
        var streamer = new FakeStreamer();
        var d = new GameDispatcher([Mw4, Other], streamer, new ListLog());

        Assert.True(d.Handle(Start("GAME.EXE")));
        Assert.True(d.Handle(Start("other.exe")));
        Assert.False(d.Handle(Start("notepad.exe")));

        Assert.Equal(["mw4", "other"], streamer.Calls.Select(c => c.Game));
    }

    [Fact]
    public void SameGameStreamsOnceWithinCooldown()
    {
        var streamer = new FakeStreamer();
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        var d = new GameDispatcher([Mw4], streamer, new ListLog(), () => now);

        Assert.True(d.Handle(Start("launcher.exe")));
        now += TimeSpan.FromSeconds(5);
        Assert.False(d.Handle(Start("game.exe")));
        now += GameDispatcher.Cooldown;
        Assert.True(d.Handle(Start("game.exe")));

        Assert.Equal(2, streamer.Calls.Count);
    }

    [Fact]
    public void EveryDetectionIsLoggedWithPidEvenWithinCooldownAndMissingTraceIsNotAnError()
    {
        var streamer = new FakeStreamer { Throw = new FileNotFoundException() };
        var log = new ListLog();
        var d = new GameDispatcher([Mw4], streamer, log, () => DateTimeOffset.UnixEpoch);

        Assert.False(d.Handle(new ProcessStart(111, "game.exe", 0, "poll")));
        Assert.False(d.Handle(new ProcessStart(222, "game.exe", 0))); // 쿨다운 안: 스트림은 안 부르지만 감지는 기록

        Assert.Contains(log.Lines, l => l.Contains("game.exe started (pid 111) via poll", StringComparison.Ordinal)); // 어느 길로 잡았는지 남김
        Assert.Contains(log.Lines, l => l.Contains("game.exe started (pid 222)", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Lines, l => l.StartsWith("Error", StringComparison.Ordinal));
    }

    [Fact]
    public void StreamFailureIsLoggedAndDoesNotStopLaterStreams()
    {
        var streamer = new FakeStreamer { Throw = new IOException("disk gone") };
        var log = new ListLog();
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        var d = new GameDispatcher([Mw4], streamer, log, () => now);

        Assert.False(d.Handle(Start("game.exe")));
        streamer.Throw = null;
        now += GameDispatcher.Cooldown;
        Assert.True(d.Handle(Start("game.exe")));

        Assert.Contains(log.Lines, l => l.StartsWith("Error: mw4: stream failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WorkerTurnsAWatcherEventIntoAStreamCall()
    {
        var streamer = new FakeStreamer();
        var emitted = new TaskCompletionSource();
        var settings = new AsterSettings(250, [Mw4]);
        var worker = new Worker(settings, new ListLog(), new OneShotWatcher(Start("game.exe"), emitted), null, streamer,
            new ReapplyChecker(ReapplyTests.NoIoEnv(), new ListLog()));

        await worker.StartAsync(CancellationToken.None);
        await emitted.Task;
        for (int i = 0; i < 200 && streamer.Calls.IsEmpty; i++)
        {
            await Task.Delay(10);
        }

        await worker.StopAsync(CancellationToken.None);

        Assert.Equal("mw4", Assert.Single(streamer.Calls).Game);
    }

    [Fact]
    public async Task WatcherEventCancelsTheRunningReapplyBeforeStreaming()
    {
        var gate = new ReapplyGate();
        using var started = new ManualResetEventSlim();
        Task<bool> reapply = Task.Run(() => gate.Run(CancellationToken.None, token =>
        {
            started.Set();
            return token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
        }));
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        var worker = new Worker(new AsterSettings(250, [Mw4]), new ListLog(), new OneShotWatcher(Start("game.exe"), new TaskCompletionSource()), null,
            new FakeStreamer(), new ReapplyChecker(ReapplyTests.NoIoEnv(), new ListLog()), gate);

        await worker.StartAsync(CancellationToken.None);

        Assert.True(await reapply);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WorkerWithNoGamesIdlesWithoutWatchingOrReapplying()
    {
        var log = new ListLog();
        var watcher = new CountingWatcher();
        var worker = new Worker(new AsterSettings(250, []), log, watcher, watcher, new FakeStreamer(), new ReapplyChecker(ReapplyTests.NoIoEnv(), log));

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(0, watcher.Runs);
        Assert.Contains(log.Lines, l => l.StartsWith("Info: service started: no games registered", StringComparison.Ordinal));
        Assert.Contains(log.Lines, l => l.Equals("Info: service stopped", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Lines, l => l.StartsWith("Error", StringComparison.Ordinal) || l.StartsWith("Warn", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FaultedPrimaryWatcherIsLoggedAndPollingFallbackTakesOver()
    {
        var streamer = new FakeStreamer();
        var log = new ListLog();
        var emitted = new TaskCompletionSource();
        var worker = new Worker(new AsterSettings(250, [Games.Make("mw4", "game.exe")]), log, new ThrowingWatcher(), new OneShotWatcher(new ProcessStart(1, "game.exe", 0), emitted), streamer,
            new ReapplyChecker(ReapplyTests.NoIoEnv(), log));

        await worker.StartAsync(CancellationToken.None);
        await emitted.Task;
        for (int i = 0; i < 200 && streamer.Calls.IsEmpty; i++)
        {
            await Task.Delay(10);
        }

        await worker.StopAsync(CancellationToken.None);

        Assert.Contains(log.Lines, l => l.StartsWith("Error: process watcher failed", StringComparison.Ordinal) && l.Contains("session never started", StringComparison.Ordinal));
        Assert.Contains(log.Lines, l => l.StartsWith("Warn: falling back to", StringComparison.Ordinal));
        Assert.Equal("mw4", Assert.Single(streamer.Calls).Game);
    }

    private sealed class ThrowingWatcher : IProcessWatcher
    {
        public Task RunAsync(Action<ProcessStart> onStart, CancellationToken cancellationToken) => throw new InvalidOperationException("session never started");
    }

    private sealed class CountingWatcher : IProcessWatcher
    {
        private int _runs;

        public int Runs => _runs;

        public Task RunAsync(Action<ProcessStart> onStart, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _runs);
            return Task.CompletedTask;
        }
    }

    private sealed class OneShotWatcher(ProcessStart start, TaskCompletionSource emitted) : IProcessWatcher
    {
        public async Task RunAsync(Action<ProcessStart> onStart, CancellationToken cancellationToken)
        {
            onStart(start);
            emitted.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 취소는 정상 종료
            }
        }
    }
}

public sealed class AclCheckTests
{
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier System_ = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    private static DirectorySecurity Locked()
    {
        var sec = new DirectorySecurity();
        sec.SetOwner(Admins);
        sec.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(System_, FileSystemRights.FullControl, AccessControlType.Allow));
        return sec;
    }

    [Fact]
    public void FileOwnedByAUserIsFlaggedEvenWithoutWriteRules()
    {
        var sec = new FileSecurity();
        sec.SetOwner(Users);
        sec.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, AccessControlType.Allow));

        Assert.Equal(["owner:" + Users.Value], AclCheck.UntrustedWriters(sec));
        sec.SetOwner(Admins);
        Assert.Empty(AclCheck.UntrustedWriters(sec));
    }

    [Fact]
    public void AdminsAndSystemOnlyIsSafe() => Assert.Empty(AclCheck.UntrustedWriters(Locked()));

    [Fact]
    public void ReadOnlyAccessForUsersIsStillSafe()
    {
        DirectorySecurity sec = Locked();
        sec.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, AccessControlType.Allow));

        Assert.Empty(AclCheck.UntrustedWriters(sec));
    }

    [Theory]
    [InlineData(FileSystemRights.Modify)]
    [InlineData(FileSystemRights.WriteData)]
    [InlineData(FileSystemRights.AppendData)]
    [InlineData(FileSystemRights.ChangePermissions)]
    public void WriteAccessForUsersIsReported(FileSystemRights rights)
    {
        DirectorySecurity sec = Locked();
        sec.AddAccessRule(new FileSystemAccessRule(Users, rights, AccessControlType.Allow));

        Assert.Equal([Users.Value], AclCheck.UntrustedWriters(sec));
    }

    [Fact]
    public void UntrustedOwnerIsReported()
    {
        DirectorySecurity sec = Locked();
        sec.SetOwner(Users);

        Assert.Equal(["owner:" + Users.Value], AclCheck.UntrustedWriters(sec));
    }

    [Fact]
    public void OrdinaryUserFolderIsNotSafe()
    {
        using var tmp = new TempDir();

        Assert.NotEmpty(AclCheck.UntrustedWriters(tmp.Path));
    }
}
