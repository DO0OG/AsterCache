using AsterCache.Core.Json;
using AsterCache.Core.Settings;
using AsterCache.Service;
using AsterCache.Tray;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 게임 시작 때 잰 예열 적중률(hit_percent): 측정, auto_status.json 기록·왕복, 트레이 상태 열.
public sealed class HitPercentTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);
    private readonly AutoRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private Prewarmer MakePrewarm(ProbeResult? probe) => new(_rig.Settings.Games, new FakeStreamer(), new PrewarmEnv
    {
        AvailableRamMib = () => 99999,
        GameRunning = _ => false,
        ZoneBytes = _ => null,
        TraceExists = _ => true,
        ProbeReadMs = _ => probe,
    }, _rig.Log, null, () => T0);

    [Theory]
    [InlineData(8, 0, 100)]
    [InlineData(8, 2, 75)]
    [InlineData(8, 8, 0)]
    [InlineData(3, 1, 66)]
    public void HitPercentIsTheShareOfFastSamples(int samples, int slow, int expected)
    {
        _rig.Editor.Add(_rig.Input("foo", "foo.exe", @"D:\Games\foo", "auto", null));
        _rig.Reload();

        Assert.Equal(expected, MakePrewarm(new ProbeResult(samples, slow, 1.0)).HitPercent(_rig.Settings.Games[0]));
        Assert.Null(MakePrewarm(null).HitPercent(_rig.Settings.Games[0]));
    }

    [Fact]
    public void GameStartRecordsHitPercentAndItIsSerialized()
    {
        _rig.Editor.Add(_rig.Input("foo", "foo.exe", @"D:\Games\foo", "auto", null));
        _rig.Reload();
        _rig.World.Now = T0;
        _rig.World.Files.Add(AutoTracer.RawTracePath(_rig.Settings.Games[0]));
        var mode = new AutoMode(_rig.Editor, _rig.World.Env(), _rig.Status, _rig.Log, _rig.Settings, MakePrewarm(new ProbeResult(8, 2, 3.0)));

        mode.OnGameStart(_rig.Settings.Games[0]);

        AutoGameEntry e = _rig.Status.Get("foo")!;
        Assert.Equal((75, T0), (e.HitPercent, e.HitAt));
        string path = Path.Combine(Path.GetTempPath(), "aster_hit_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var s = new AutoStatus(path, _rig.Log, () => T0);
            s.Set("foo", "auto", null, AutoStage.Done, null);
            s.RecordHit("foo", 75, T0);
            var game = (JObject)((JObject)((JObject)JsonRead.ReadFile(path)!)["games"]!)["foo"]!;
            Assert.Equal(75L, game["hit_percent"]);
            Assert.Equal("2026-11-03T09:00:00.0000000+00:00", game["hit_at"]);
            Assert.Equal(75, new AutoStatus(path, _rig.Log).Get("foo")!.HitPercent); // 다시 읽어도 유지
            Assert.Equal("예열 적중 75%", AutoStatusFile.StageText(AutoStatusFile.Load(path)!.Games["foo"], "ko").Split('(', ')')[1]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UnmeasuredGameKeepsTheOldShapeAndTrayShowsHitInBothLanguages()
    {
        var s = new AutoStatus(null, _rig.Log, () => T0);
        s.Set("foo", "auto", null, AutoStage.Done, null);
        s.RecordHit("none", 50, T0); // 항목이 없는 게임은 남기지 않음
        Assert.Null(s.Get("none"));
        Assert.Null(s.Get("foo")!.HitPercent);

        var g = new AutoGame("auto", null, "done", null, 40);
        Assert.Equal("완료 (예열 적중 40%)", AutoStatusFile.StageText(g, "ko"));
        Assert.Equal("done (prewarm hit 40%)", AutoStatusFile.StageText(g, "en"));
        Assert.Equal("완료", AutoStatusFile.StageText(g with { HitPercent = null }, "ko"));
    }
}
