using System.Diagnostics;
using AsterCache.Core.Settings;
using AsterCache.Windows;

namespace AsterCache.Service;

/// `AsterCache.Service --console --watch-test <초>`: 설정의 exe 이름으로 감시기를 그 시간 동안 돌려 보고, 감지한 시작과 세션으로 들어온 이벤트 집계를 출력합니다
/// (ETW 감시기가 아무것도 못 알릴 때 원인을 가르는 진단). 이어서 폴링 감시기로 같은 시험을 한 번 더 돌려 비교합니다. 예외는 삼키지 않고 그대로 출력합니다.
public static class WatchTest
{
    public static async Task<int> RunAsync(IReadOnlyCollection<string> exeNames, int seconds, TextWriter o)
    {
        o.WriteLine($"watch-test: {seconds} s, exe names: [{string.Join(", ", exeNames)}], administrator: {Elevation.IsAdmin()}");
        o.WriteLine("start one of those programs now (for example cmd.exe /c \"ping -n 2 127.0.0.1 >nul\")");
        var diag = new EtwWatchDiagnostics(line => o.WriteLine("  " + line));
        int etw = await RunOneAsync("EtwProcessWatcher", new EtwProcessWatcher(exeNames, diagnostics: diag, silenceTimeout: Timeout.InfiniteTimeSpan), seconds, o);
        foreach (KeyValuePair<string, long> kv in diag.Counts.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            o.WriteLine($"  session events: {kv.Key} = {kv.Value}");
        }

        if (diag.Counts.Count == 0)
        {
            o.WriteLine("  session events: none (the session received no Kernel-Process event at all)");
        }

        int poll = await RunOneAsync("PollingProcessWatcher", new PollingProcessWatcher(exeNames, TimeSpan.FromMilliseconds(SettingsStore.DefaultPollMs)), seconds, o);
        o.WriteLine($"watch-test result: etw detected {etw} start(s), polling detected {poll} start(s)");
        return 0;
    }

    private static async Task<int> RunOneAsync(string name, IProcessWatcher watcher, int seconds, TextWriter o)
    {
        o.WriteLine($"== {name} for {seconds} s");
        int count = 0;
        long t0 = Stopwatch.GetTimestamp();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        try
        {
            await watcher.RunAsync(s =>
            {
                Interlocked.Increment(ref count);
                o.WriteLine($"  start pid={s.Pid} exe={s.ExeName} at +{Stopwatch.GetElapsedTime(t0, s.Timestamp).TotalMilliseconds:F0} ms");
            }, cts.Token);
        }
        catch (Exception e)
        {
            o.WriteLine($"  {name} threw: {e}");
        }

        return Volatile.Read(ref count);
    }
}
