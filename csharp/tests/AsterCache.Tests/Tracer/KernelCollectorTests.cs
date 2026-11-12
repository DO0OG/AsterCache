using AsterCache.Tracer;
using Xunit;

namespace AsterCache.Tests.Tracer;

/// 수집 구간 (창) 밖의 읽기를 거르고, 창 밖에서도 이름을 계속 익히는 동작. 이벤트 파서 없이 처리 메서드를 직접 부릅니다.
public sealed class KernelCollectorTests
{
    private static readonly DateTime T0 = new(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
    private const string File1 = @"D:\Games\a.bin";

    private static KernelCollector Collector()
    {
        var collector = new KernelCollector(["game.exe"], new DevicePaths([]));
        collector.StartProcess(T0, 10, 1, "game.exe");
        return collector;
    }

    [Fact]
    public void ReadsOutsideTheWindowAreDroppedAndThoseInsideKeepTheirOwnTimes()
    {
        KernelCollector c = Collector();
        c.NameKey(7, File1);
        c.AddRead(T0.AddSeconds(1), 10, 7, 70, 0, 4096);
        c.Pause();
        c.AddRead(T0.AddSeconds(2), 10, 7, 70, 4096, 4096); // 창 밖
        c.Resume();
        c.AddRead(T0.AddSeconds(3), 10, 7, 70, 8192, 4096);

        WindowStats stats = c.TakeWindowStats();
        List<TraceRecord> reads = c.Finish().Where(r => r.Kind == RecordKind.Read).ToList();

        Assert.Equal(2, stats.Reads);
        Assert.Equal([0L, 8192L], reads.Select(r => r.Offset));
        Assert.Equal([T0.AddSeconds(1), T0.AddSeconds(3)], reads.Select(r => r.Time));
    }

    [Fact]
    public void ANameLearnedWhileClosedResolvesTheFirstReadOfTheNextWindow()
    {
        KernelCollector c = Collector();
        c.Pause();
        c.NameKey(7, File1); // 창 밖에서도 이름 이벤트는 계속 처리됨
        c.Resume();
        c.AddRead(T0.AddSeconds(5), 10, 7, 0, 0, 4096);

        List<TraceRecord> reads = c.Finish().Where(r => r.Kind == RecordKind.Read).ToList();

        Assert.Equal(0, c.Unresolved);
        Assert.Equal(File1, Assert.Single(reads).Path);
    }

    [Fact]
    public void AReadWithNoNameAtAllIsDroppedAndCountedAsUnresolved()
    {
        KernelCollector c = Collector();
        c.AddRead(T0.AddSeconds(1), 10, 9, 0, 0, 4096); // 이름 이벤트가 하나도 없는 파일: 새 세션이 창 전에 열린 파일을 만나는 경우

        Assert.DoesNotContain(c.Finish(), r => r.Kind == RecordKind.Read);
        Assert.Equal(1, c.Unresolved);
    }

    [Fact]
    public void WritesOutsideTheWindowAndStopsOfOtherProcessesAreNotRecorded()
    {
        KernelCollector c = Collector();
        c.NameKey(8, File1);
        c.Pause();
        c.AddWrite(T0.AddSeconds(1), 10, 7, 70); // 창 밖 쓰기
        c.StopProcess(T0.AddSeconds(2), 99); // 대상이 아닌 프로세스의 종료
        c.Resume();
        c.AddWrite(T0.AddSeconds(3), 10, 8, 80);

        List<TraceRecord> records = c.Finish();

        Assert.Single(records, r => r.Kind == RecordKind.Write);
        Assert.DoesNotContain(records, r => r.Kind == RecordKind.ProcessStop);
        c.StopProcess(T0.AddSeconds(4), 10);
        Assert.Contains(c.Finish(), r => r.Kind == RecordKind.ProcessStop && r.Pid == 10);
    }

    [Fact]
    public void ReadsOfAProcessOutsideTheTargetTreeAreIgnored()
    {
        KernelCollector c = Collector();
        c.NameKey(7, File1);
        c.AddRead(T0, 99, 7, 70, 0, 4096);

        Assert.Equal(0, c.TakeWindowStats().Reads);
        Assert.Equal(1, c.TakeWindowStats().Targets);
    }
}
