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

    private AutoStatus Make(string? path = "") => new(path == string.Empty ? StatusPath : path, _log, () => _now);

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
        s.BeginRelocation(_now);
        s.Complete("mw4", "auto", _now, counted: true, null);
        s.Set("kr", "manual", null, AutoStage.Failed, "한글 detail");

        var root = (JObject)JsonRead.ReadFile(StatusPath)!;
        Assert.Equal(["updated", "relocations", "games", "events"], root.Select(kv => kv.Key));
        var game = (JObject)((JObject)root["games"]!)["mw4"]!;
        Assert.Equal(["source", "launcher", "stage", "detail", "at", "last_played", "relocated_at"], game.Select(kv => kv.Key));
        Assert.Equal(("auto", "steam", "done", null), (game["source"], game["launcher"], game["stage"], game["detail"]));
        Assert.Equal("2026-11-02T12:15:00.0000000+00:00", game["relocated_at"]);
        Assert.Equal("2026-11-02T12:05:00.0000000+00:00", game["last_played"]);
        Assert.Equal(["2026-11-02T12:15:00.0000000+00:00"], ((List<object?>)root["relocations"]!).Cast<string>());
        var events = ((List<object?>)root["events"]!).Cast<JObject>().ToList();
        Assert.Equal(["discovered", "relocated"], events.Select(e => (string)e["kind"]!));
        Assert.Equal(["at", "game", "kind"], events[0].Select(kv => kv.Key));

        AutoStatus again = Make();
        Assert.Equal(s.Get("mw4"), again.Get("mw4"));
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

        s.BeginRelocation(_now);
        s.Complete("old", "auto", _now, counted: true, null);
        _now = _now.AddHours(30);
        s.BeginRelocation(_now);
        s.Complete("mid", "auto", _now, counted: true, null);
        Assert.Equal(1, s.RelocationsWithin(TimeSpan.FromHours(24)));
        Assert.Equal(2, s.RelocationsWithin(TimeSpan.FromHours(48)));

        _now = _now.AddHours(19); // old 는 49 시간 전
        s.AddEvent("x", "discovered");
        var saved = (JObject)JsonRead.ReadFile(StatusPath)!;
        Assert.Single((List<object?>)saved["relocations"]!);
    }

    [Fact]
    public void CompletingDoesNotCountTheRelocationAgain()
    {
        AutoStatus s = Make();

        s.BeginRelocation(_now);
        s.Complete("g", "auto", _now, counted: true, null);

        Assert.Equal(1, s.RelocationsWithin(TimeSpan.FromHours(24)));
        Assert.Equal(["relocated"], s.Events.Select(e => e.Kind));
    }

    [Fact]
    public async Task SharingViolationDuringTheSwapIsRetriedUntilTheFileIsFree()
    {
        AutoStatus s = Make();
        s.Set("g", "auto", null, AutoStage.Discovered, null);
        using var held = new FileStream(StatusPath, FileMode.Open, FileAccess.Read, FileShare.None);
        // 해제 스레드가 실제로 돌기 시작한 뒤에 쓰기를 시작해야, 느린 러너의 스레드 풀 지연이 재시도 창(5 x 100 ms)을 잡아먹지 않습니다.
        using var started = new ManualResetEventSlim();
        Task release = Task.Run(() =>
        {
            started.Set();
            Thread.Sleep(150);
            held.Dispose();
        });
        started.Wait();

        s.Set("g", "auto", null, AutoStage.Tracing, null);
        await release;

        Assert.DoesNotContain(_log.Lines, l => l.Contains("not written", StringComparison.Ordinal));
        Assert.Contains("tracing", File.ReadAllText(StatusPath), StringComparison.Ordinal);
    }

    [Fact]
    public void FileThatStaysLockedIsGivenUpAfterTheRetriesWithAWarning()
    {
        AutoStatus s = Make();
        s.Set("g", "auto", null, AutoStage.Discovered, null);
        using var held = new FileStream(StatusPath, FileMode.Open, FileAccess.Read, FileShare.None);

        s.Set("g", "auto", null, AutoStage.Tracing, null);

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
