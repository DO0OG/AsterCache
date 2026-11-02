using AsterCache.Core.Settings;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 서비스 로그의 자동 모드 요약 한 줄: 서비스 시작 뒤 첫 틱과 매 1 시간 (가짜 시계).
public sealed class AutoSummaryTests : IDisposable
{
    private const string Prefix = "Info: auto summary: ";

    private readonly AutoRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private IEnumerable<string> Summaries => _rig.Log.Lines.Where(l => l.StartsWith(Prefix, StringComparison.Ordinal));

    private void Tick(AutoMode mode) => mode.RelocateTick(new ReapplyGate(), CancellationToken.None);

    private void Game(string name, string source, string? stage)
    {
        _rig.Add(name, name + ".exe", @"D:\Games\" + name, source);
        if (stage is not null)
        {
            _rig.Status.Set(name, source, null, stage, null);
        }
    }

    [Fact]
    public void FirstTickLogsTheCountsAndTheNextOneComesAfterAnHour()
    {
        Game("a", "auto", AutoStage.Learning);
        Game("b", "auto", AutoStage.Done);
        Game("c", "auto", AutoStage.Failed);
        Game("d", "auto", AutoStage.Skipped);
        Game("e", "auto", AutoStage.Relocating);
        Game("f", "auto", null); // 상태가 아직 없는 게임은 등록 수에만 셈
        Game("m", "manual", AutoStage.WaitingIdle);
        var prewarm = new Prewarmer(_rig.Settings.Games, new FakeStreamer(), new PrewarmEnv
        {
            AvailableRamMib = () => 99999,
            GameRunning = _ => false,
            ZoneBytes = _ => 1 << 20,
            TraceExists = _ => true,
        }, _rig.Log, null, () => _rig.World.Now);
        Assert.True(prewarm.Run(_rig.Settings.Games.Single(g => g.Name == "b"), "test").Ran);
        var mode = new AutoMode(_rig.Editor, _rig.World.Env(), _rig.Status, _rig.Log, _rig.Settings, prewarm);

        Tick(mode);

        Assert.Equal(
            [Prefix + "registered 7 (auto 6, manual 1), learning 1, waiting_idle 1, relocating 1, done 1, prewarmed 1, failed 1, skipped 1"],
            Summaries);

        _rig.World.Now += TimeSpan.FromSeconds(30);
        Tick(mode);
        _rig.World.Now += TimeSpan.FromMinutes(59);
        Tick(mode);
        Assert.Single(Summaries); // 한 시간이 되기 전에는 다시 남기지 않음

        _rig.World.Now += TimeSpan.FromMinutes(1);
        _rig.Status.Set("a", "auto", null, AutoStage.Done, null);
        Tick(mode);

        Assert.Equal(2, Summaries.Count());
        Assert.Contains("learning 0", Summaries.Last(), StringComparison.Ordinal);
        Assert.Contains("done 2", Summaries.Last(), StringComparison.Ordinal);
    }

    [Fact]
    public void NothingIsLoggedWhileAutoModeIsOffAndTheFirstLineComesWhenItTurnsOn()
    {
        Game("a", "auto", AutoStage.Done);
        _rig.Settings = _rig.Settings with { Auto = AutoSettings.Default with { Enabled = false } };
        var mode = new AutoMode(_rig.Editor, _rig.World.Env(), _rig.Status, _rig.Log, _rig.Settings);

        Tick(mode);
        Assert.Empty(Summaries);

        mode.SetSettings(_rig.Settings with { Auto = AutoSettings.Default });
        Tick(mode);

        Assert.Single(Summaries);
    }

    [Fact]
    public void WithoutAPrewarmerThePrewarmedCountIsZero()
    {
        Game("a", "auto", AutoStage.WaitingIdle);
        var mode = new AutoMode(_rig.Editor, _rig.World.Env(), _rig.Status, _rig.Log, _rig.Settings);

        Tick(mode);

        Assert.Equal(Prefix + "registered 1 (auto 1, manual 0), learning 0, waiting_idle 1, relocating 0, done 0, prewarmed 0, failed 0, skipped 0", Assert.Single(Summaries));
    }
}
