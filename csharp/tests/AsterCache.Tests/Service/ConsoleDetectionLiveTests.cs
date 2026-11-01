using System.Diagnostics;
using AsterCache.Core.Settings;
using AsterCache.Service;
using Xunit;
using Xunit.Abstractions;

namespace AsterCache.Tests.Service;

/// 비관리자 콘솔 모드와 같은 구성 (폴링 감시자 + Worker) 으로 이 시험이 직접 띄운 자식 프로세스의 감지 지연을 잽니다.
/// 기본 실행에서는 제외되고 `--filter "Category=Live"` 로 따로 돌립니다. 사용자 앱은 건드리지 않고 짧게 도는 ping 자식만 씁니다.
[Trait("Category", "Live")]
public sealed class ConsoleDetectionLiveTests(ITestOutputHelper output)
{
    [Fact]
    public async Task PollingDetectsOwnChildAndStreams()
    {
        GameSettings game = Games.Make("probe", "ping.exe");
        var streamer = new FakeStreamer();
        var watcher = new PollingProcessWatcher(game.ExeNames, TimeSpan.FromMilliseconds(SettingsStore.DefaultPollMs));
        var worker = new Worker(new AsterSettings(SettingsStore.DefaultPollMs, [game]), new ListLog(), watcher, null, streamer,
            new ReapplyChecker(ReapplyTests.NoIoEnv(), new ListLog()));
        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(Random.Shared.Next(1000, 1250)); // 기준선 스냅샷이 먼저 찍히고, 틱과 위상이 맞물리지 않도록 무작위
        Process? child = null;
        try
        {
            long t0 = Stopwatch.GetTimestamp();
            child = Process.Start(new ProcessStartInfo("ping.exe", "-n 4 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            for (int i = 0; i < 300 && streamer.Calls.IsEmpty; i++)
            {
                await Task.Delay(10);
            }

            Assert.True(streamer.Calls.TryPeek(out (string Game, long At) call), "child start was not detected within 3 s");
            double ms = Stopwatch.GetElapsedTime(t0, call.At).TotalMilliseconds;
            output.WriteLine($"polling {SettingsStore.DefaultPollMs} ms: detection latency {ms:F1} ms (from just before Process.Start)");
            Assert.InRange(ms, 0, 1500);
        }
        finally
        {
            if (child is { HasExited: false })
            {
                child.Kill();
            }

            child?.Dispose();
            await worker.StopAsync(CancellationToken.None);
        }
    }
}
