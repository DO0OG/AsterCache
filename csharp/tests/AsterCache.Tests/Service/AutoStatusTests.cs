using AsterCache.Core.Json;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// auto_status.json: 형식 왕복, 손상 복구, 단계 시각, 이벤트·재배치 기록의 상한.
public sealed class AutoStatusTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_autostatus_" + Guid.NewGuid().ToString("N"));
    private readonly ListLog _log = new();
    private DateTimeOffset _now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    public AutoStatusTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private string StatusPath => Path.Combine(_dir, "auto_status.json");

    private AutoStatus Make(string? path = "", Action<TimeSpan>? sleep = null) => new(path == string.Empty ? StatusPath : path, _log, () => _now, sleep ?? (_ => { }), FakeClock.Ticks(() => _now));

    [Fact]
    public void FileHasTheAgreedShapeAndRoundTrips()
    {
        AutoStatus s = Make();
        s.Set("mw4", "auto", "steam", AutoStage.Discovered, null);
        s.AddEvent("mw4", "discovered");
        _now = _now.AddMinutes(5);
        s.MarkPlayed("mw4", "auto", AutoStage.Discovered, _now);
        _now = _now.AddMinutes(5);
        s.Set("mw4", "auto", null, AutoStage.WaitingIdle, "한글 detail");
        _now = _now.AddMinutes(5);
        s.BeginRelocation(_now, 1);
        s.Complete("mw4", "auto", _now, counted: true, null);
        s.Set("kr", "manual", null, AutoStage.Failed, "한글 detail");

        var root = (JObject)JsonRead.ReadFile(StatusPath)!;
        Assert.Equal(["updated", "relocations", "relocation_bytes", "games", "events"], root.Select(kv => kv.Key));
        var game = (JObject)((JObject)root["games"]!)["mw4"]!;
        Assert.Equal(["source", "launcher", "stage", "detail", "at", "last_played", "relocated_at", "plays"], game.Select(kv => kv.Key));
        Assert.Equal(("auto", "steam", "done", null), (game["source"], game["launcher"], game["stage"], game["detail"]));
        Assert.Equal("2026-11-02T12:15:00.0000000+00:00", game["relocated_at"]);
        Assert.Equal("2026-11-02T12:05:00.0000000+00:00", game["last_played"]);
        Assert.Equal(["2026-11-02T12:15:00.0000000+00:00"], ((List<object?>)root["relocations"]!).Cast<string>());
        var events = ((List<object?>)root["events"]!).Cast<JObject>().ToList();
        Assert.Equal(["discovered", "relocated"], events.Select(e => (string)e["kind"]!));
        Assert.Equal(["at", "game", "kind"], events[0].Select(kv => kv.Key));

        AutoStatus again = Make();
        Assert.Equal(s.Get("mw4")! with { Plays = null }, again.Get("mw4")! with { Plays = null }); // 목록은 참조 비교라 따로 견줌
        Assert.Equal(s.Get("mw4")!.Plays, again.Get("mw4")!.Plays);
        Assert.Equal(s.Events, again.Events);
        Assert.Equal(1, again.RelocationsWithin(TimeSpan.FromHours(24)));
        Assert.StartsWith("{\n  \"updated\"", File.ReadAllText(StatusPath), StringComparison.Ordinal); // indent 2, ensure_ascii 거짓
        Assert.Contains("한글 detail", File.ReadAllText(StatusPath), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("{\"games\": 5}")]
    public void DamagedFileStartsEmptyAndIsReplacedOnNextWrite(string text)
    {
        File.WriteAllText(StatusPath, text);

        AutoStatus s = Make();

        Assert.Null(s.Get("x"));
        Assert.Empty(s.Events);
        s.Set("x", "manual", null, AutoStage.Discovered, null);
        Assert.NotNull(Make().Get("x"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    public void UnreadableFileIsKeptAsBadAndHoldsRelocationFor24Hours(string text)
    {
        File.WriteAllText(StatusPath, text);
        DateTimeOffset until = _now.AddHours(24);

        AutoStatus s = Make();

        Assert.True(File.Exists(StatusPath + ".bad"));
        Assert.Equal(until, s.RelocationHeldUntil());
        Assert.Contains("status file was unreadable; relocation held until " + until.ToString("u"), _log.Lines.Single(l => l.Contains("unreadable", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Equal((0, 0L), (s.RelocationsWithin(TimeSpan.FromHours(24)), s.RelocationBytesWithin(TimeSpan.FromHours(24)))); // 실제 이동 집계는 건드리지 않음
        Assert.Equal(until, Make().RelocationHeldUntil()); // 다시 시작해도 유지
        _now = _now.AddHours(24).AddMinutes(1);
        Assert.Null(s.RelocationHeldUntil());
    }

    [Fact]
    public void DailyWindowKeepsTheLaterEndOfWallClockAndSessionElapsed()
    {
        DateTimeOffset wall = _now;
        long ticks = 0;
        var s = new AutoStatus(StatusPath, _log, () => wall, _ => { }, () => ticks);
        s.BeginRelocation(wall, 5);

        ticks += System.Diagnostics.Stopwatch.Frequency * 60;
        wall = wall.AddHours(25); // 시계가 앞으로 튀어도 세션 경과는 1 분
        Assert.Equal((1, 5L), (s.RelocationsWithin(TimeSpan.FromHours(24)), s.RelocationBytesWithin(TimeSpan.FromHours(24))));

        ticks += System.Diagnostics.Stopwatch.Frequency * 3600 * 24;
        Assert.Equal(0, s.RelocationsWithin(TimeSpan.FromHours(24)));

        var back = new AutoStatus(null, _log, () => wall, _ => { }, () => ticks);
        back.BeginRelocation(wall, 1);
        wall = wall.AddHours(-3); // 시계가 뒤로 가면 벽시계 시각 쪽이 더 늦게 끝남
        ticks += System.Diagnostics.Stopwatch.Frequency * 3600 * 25;
        Assert.Equal(1, back.RelocationsWithin(TimeSpan.FromHours(24)));
    }

    [Fact]
    public void AtChangesOnlyWhenTheStageChanges()
    {
        AutoStatus s = Make();
        Assert.True(s.Set("g", "manual", null, AutoStage.WaitingIdle, null));
        DateTimeOffset first = s.Get("g")!.At;
        _now = _now.AddMinutes(1);

        Assert.True(s.Set("g", "manual", null, AutoStage.WaitingIdle, "daily limit reached"));
        Assert.Equal(first, s.Get("g")!.At);
        Assert.False(s.Set("g", "manual", null, AutoStage.WaitingIdle, "daily limit reached"));
        Assert.True(s.Set("g", "manual", null, AutoStage.Relocating, null));
        Assert.Equal(_now, s.Get("g")!.At);
    }

    [Fact]
    public void LauncherIsKeptWhenNotGivenAndPlayedKeepsTheStage()
    {
        AutoStatus s = Make();
        s.Set("g", "auto", "epic", AutoStage.Discovered, null);

        s.Set("g", "auto", null, AutoStage.Tracing, null);
        s.MarkPlayed("g", "auto", AutoStage.WaitingIdle, _now);

        AutoGameEntry e = s.Get("g")!;
        Assert.Equal(("epic", AutoStage.Tracing, _now), (e.Launcher, e.Stage, e.LastPlayed));
    }

    [Fact]
    public void EventsKeepTheLast20AndRelocationsExpireAfter48Hours()
    {
        AutoStatus s = Make();
        for (int i = 0; i < 25; i++)
        {
            s.AddEvent("g" + i, "discovered");
        }

        Assert.Equal(20, s.Events.Count);
        Assert.Equal("g5", s.Events[0].Game);

        s.BeginRelocation(_now, 1);
        s.Complete("old", "auto", _now, counted: true, null);
        _now = _now.AddHours(30);
        s.BeginRelocation(_now, 1);
        s.Complete("mid", "auto", _now, counted: true, null);
        Assert.Equal(1, s.RelocationsWithin(TimeSpan.FromHours(24)));
        Assert.Equal(2, s.RelocationsWithin(TimeSpan.FromHours(48)));

        _now = _now.AddHours(19); // old 는 49 시간 전
        s.AddEvent("x", "discovered");
        var saved = (JObject)JsonRead.ReadFile(StatusPath)!;
        Assert.Single((List<object?>)saved["relocations"]!);
    }

    [Fact]
    public void FinishedRelocationKeepsTheActualMovedBytesNotThePlannedOnes()
    {
        AutoStatus s = Make();
        s.BeginRelocation(_now, 5L << 30); // 시작 때의 예정 바이트
        s.SetLastRelocationBytes(3L << 30); // 실제로 옮긴 양

        Assert.Equal(3L << 30, s.RelocationBytesWithin(TimeSpan.FromHours(24)));
        var root = (JObject)JsonRead.ReadFile(StatusPath)!;
        Assert.Equal([3L << 30], ((List<object?>)root["relocation_bytes"]!).Cast<long>());
    }

    [Fact]
    public void RelocationsThatMovedNothingAreNotCountedTowardsTheDailyLimit()
    {
        AutoStatus s = Make();
        for (int i = 0; i < 3; i++)
        {
            s.BeginRelocation(_now, 5L << 30);
            s.SetLastRelocationBytes(0);
        }

        s.BeginRelocation(_now, 5L << 30);
        s.SetLastRelocationBytes(1L << 30);

        Assert.Equal((1, 1L << 30), (s.RelocationsWithin(TimeSpan.FromHours(24)), s.RelocationBytesWithin(TimeSpan.FromHours(24))));
    }

    [Fact]
    public void RelocationBytesAreSummedWithinTheWindowAndRoundTrip()
    {
        AutoStatus s = Make();
        s.BeginRelocation(_now, 5L << 30);
        _now = _now.AddHours(30);
        s.BeginRelocation(_now, 3L << 30);

        Assert.Equal(3L << 30, s.RelocationBytesWithin(TimeSpan.FromHours(24)));
        Assert.Equal(8L << 30, s.RelocationBytesWithin(TimeSpan.FromHours(48)));
        var root = (JObject)JsonRead.ReadFile(StatusPath)!;
        Assert.Equal([5L << 30, 3L << 30], ((List<object?>)root["relocation_bytes"]!).Cast<long>());
        Assert.Equal(3L << 30, Make().RelocationBytesWithin(TimeSpan.FromHours(24)));
    }

    [Fact]
    public void ZeroByteRecordsFromAnOldStatusFileAreNotCountedTowardsTheDailyLimit()
    {
        string t1 = _now.AddHours(-3).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        string t2 = _now.AddHours(-2).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(StatusPath, "{\"relocations\": [\"" + t1 + "\", \"" + t2 + "\"]}");
        AutoStatus old = Make();
        Assert.Equal((0, 0L), (old.RelocationsWithin(TimeSpan.FromHours(24)), old.RelocationBytesWithin(TimeSpan.FromHours(24))));

        File.WriteAllText(StatusPath, "{\"relocations\": [\"" + t1 + "\", \"" + t2 + "\"], \"relocation_bytes\": [7]}");
        AutoStatus shortList = Make();
        Assert.Equal((1, 7L), (shortList.RelocationsWithin(TimeSpan.FromHours(24)), shortList.RelocationBytesWithin(TimeSpan.FromHours(24))));
    }

    [Fact]
    public void ExpiringRelocationsTrimsTheByteListAlongWithTheTimes()
    {
        AutoStatus s = Make();
        s.BeginRelocation(_now, 100);
        _now = _now.AddHours(30);
        s.BeginRelocation(_now, 200);
        _now = _now.AddHours(19); // 첫 항목은 49 시간 전
        s.AddEvent("x", "discovered");

        var saved = (JObject)JsonRead.ReadFile(StatusPath)!;
        Assert.Equal([200L], ((List<object?>)saved["relocation_bytes"]!).Cast<long>());
        Assert.Single((List<object?>)saved["relocations"]!);
    }

    [Fact]
    public void CompletingDoesNotCountTheRelocationAgain()
    {
        AutoStatus s = Make();

        s.BeginRelocation(_now, 1);
        s.Complete("g", "auto", _now, counted: true, null);

        Assert.Equal(1, s.RelocationsWithin(TimeSpan.FromHours(24)));
        Assert.Equal(["relocated"], s.Events.Select(e => e.Kind));
    }

    [Fact]
    public void ResetForReplanClearsRelocatedAtAndGoesBackToDiscoveredButKeepsTheDailyCount()
    {
        AutoStatus s = Make();
        s.BeginRelocation(_now, 1);
        s.Complete("g", "auto", _now, counted: true, null);
        _now = _now.AddMinutes(5);

        s.ResetForReplan("g", "game updated");
        s.ResetForReplan("nobody", "ignored");

        AutoGameEntry e = s.Get("g")!;
        Assert.Equal((AutoStage.Discovered, "game updated", null, _now), (e.Stage, e.Detail, e.RelocatedAt, e.At));
        Assert.Equal(1, s.RelocationsWithin(TimeSpan.FromHours(24)));
        Assert.Null(s.Get("nobody"));
        Assert.Null(Make().Get("g")!.RelocatedAt); // 저장돼 다시 읽어도 지워져 있음
    }

    [Fact]
    public void SharingViolationDuringTheSwapIsRetriedUntilTheFileIsFree()
    {
        FileStream? held = null;
        var waits = new List<TimeSpan>();
        // 대기 훅이 두 번째 재시도 전에 파일을 풂: 실제로 자지도, 스레드 시점에 기대지도 않음
        AutoStatus s = Make(sleep: wait =>
        {
            waits.Add(wait);
            if (waits.Count == 2)
            {
                held!.Dispose();
            }
        });
        s.Set("g", "auto", null, AutoStage.Discovered, null);
        held = new FileStream(StatusPath, FileMode.Open, FileAccess.Read, FileShare.None);

        s.Set("g", "auto", null, AutoStage.Tracing, null);

        Assert.Equal([AutoStatus.MoveRetryDelay, AutoStatus.MoveRetryDelay], waits);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("not written", StringComparison.Ordinal));
        Assert.Contains("tracing", File.ReadAllText(StatusPath), StringComparison.Ordinal);
    }

    [Fact]
    public void FileThatStaysLockedIsGivenUpAfterTheRetriesWithAWarning()
    {
        var waits = new List<TimeSpan>();
        AutoStatus s = Make(sleep: waits.Add);
        s.Set("g", "auto", null, AutoStage.Discovered, null);
        using var held = new FileStream(StatusPath, FileMode.Open, FileAccess.Read, FileShare.None);

        s.Set("g", "auto", null, AutoStage.Tracing, null);

        Assert.Equal(Enumerable.Repeat(AutoStatus.MoveRetryDelay, AutoStatus.MoveRetries), waits); // 정해진 횟수만 기다리고 포기
        Assert.Contains(_log.Lines, l => l.Contains("auto status file not written", StringComparison.Ordinal));
        Assert.Equal(AutoStage.Tracing, s.Get("g")!.Stage); // 메모리 상태는 유지
    }

    [Fact]
    public void AlreadyInPlaceIsDoneButNotCounted()
    {
        AutoStatus s = Make();

        s.Complete("g", "manual", _now, counted: false, "already in place");

        Assert.Equal(0, s.RelocationsWithin(TimeSpan.FromHours(24)));
        Assert.Empty(s.Events);
        Assert.Equal(("done", "already in place", _now), (s.Get("g")!.Stage, s.Get("g")!.Detail, s.Get("g")!.RelocatedAt));
    }

    [Fact]
    public void WithoutAPathNothingIsWritten()
    {
        AutoStatus s = Make(null);

        s.Set("g", "auto", null, AutoStage.Discovered, null);

        Assert.Equal(AutoStage.Discovered, s.Get("g")!.Stage);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void LastPlayedListsOnlyPlayedGamesAndRemoveForgetsOne()
    {
        AutoStatus s = Make();
        s.Set("never", "auto", null, AutoStage.Discovered, null);
        s.MarkPlayed("played", "manual", AutoStage.Discovered, _now);

        Assert.Equal(["played"], s.LastPlayed().Keys);
        s.Remove("played");
        Assert.Null(s.Get("played"));
        Assert.Empty(s.LastPlayed());
    }
}
