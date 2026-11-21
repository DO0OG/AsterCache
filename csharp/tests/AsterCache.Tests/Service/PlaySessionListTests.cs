using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 플레이 기록 파일에서 끝난 플레이 구간을 읽는 곳: 끝나지 않은 플레이·깨진 항목·다른 게임·없는 파일을 건너뛰고 시작 순으로 돌려주는지 확인합니다.
public sealed class PlaySessionListTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_playsessions_" + Guid.NewGuid().ToString("N"));

    public PlaySessionListTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void OnlyFinishedPlaysOfTheGameComeBackInStartOrderAndBadEntriesAreSkipped()
    {
        string path = Path.Combine(_dir, "play_samples.json");
        File.WriteAllText(path, """
            {"games": {
              "Call_of_Duty": [
                {"sessionId": "b", "startedAt": "2026-11-21T11:01:34+00:00", "endedAt": "2026-11-21T11:24:07+00:00"},
                {"sessionId": "a", "startedAt": "2026-11-21T02:03:12+00:00", "endedAt": "2026-11-21T03:03:29+00:00"},
                {"sessionId": "running", "startedAt": "2026-11-21T20:00:00+00:00", "endedAt": null},
                {"sessionId": "backwards", "startedAt": "2026-11-21T20:00:00+00:00", "endedAt": "2026-11-21T19:00:00+00:00"},
                {"startedAt": "2026-11-21T21:00:00+00:00", "endedAt": "2026-11-21T22:00:00+00:00"}
              ],
              "Other": [{"sessionId": "x", "startedAt": "2026-11-21T01:00:00+00:00", "endedAt": "2026-11-21T02:00:00+00:00"}]
            }}
            """);

        var spans = PlaySessionList.Read(path, "Call_of_Duty");

        Assert.Equal(["a", "b"], spans.Select(s => s.Session));
        Assert.Equal(new DateTime(2026, 11, 21, 2, 3, 12, DateTimeKind.Utc), spans[0].StartUtc);
        Assert.Equal(new DateTime(2026, 11, 21, 11, 24, 7, DateTimeKind.Utc), spans[1].EndUtc);
        Assert.Empty(PlaySessionList.Read(path, "None"));
        Assert.Empty(PlaySessionList.Read(Path.Combine(_dir, "missing.json"), "Call_of_Duty"));
        File.WriteAllText(path, "not json");
        Assert.Empty(PlaySessionList.Read(path, "Call_of_Duty"));
    }
}
