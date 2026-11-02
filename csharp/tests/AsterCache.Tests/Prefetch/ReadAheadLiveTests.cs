using AsterCache.Prefetch;
using Xunit;
using Xunit.Abstractions;

namespace AsterCache.Tests.Prefetch;

/// 실제 파일 읽기 경로 확인 (ETW 없음, 관리자 아님): C: 의 임시 폴더에 합성 파일을 만들고, 가짜 "게임" 이 큰 파일을 0 에서 끝까지 1 MiB 씩 순차로 읽는 알림을 엔진에 보냅니다 (틈 모드 시험은 여덟 번 읽을 때마다 150 ms 쉼: 틈에서만 선행 읽기가 일어남. 스트림 모드 시험은 쉬지 않아도 앞서 읽음).
/// 작은 파일은 열기 알림만 보냅니다 (열었다고 통째로 읽지 않음).
/// 기본 실행에서는 제외되고 `--filter "Category=Live"` 로 따로 돌립니다. 파일 캐시에 이미 있는 합성 파일이라 시간이 아니라 엔진의 집계 (선행 읽기 바이트·적중 추정) 만 의미가 있습니다.
[Trait("Category", "Live")]
public sealed class ReadAheadLiveTests(ITestOutputHelper output)
{
    private const long Mib = 1L << 20;

    [Fact]
    public void SequentialGameReadOf200MibIsFollowedByTheWorkerThread()
    {
        string dir = Path.Combine(Path.GetTempPath(), "aster_learn_live_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string big = Path.Combine(dir, "big.pak");
            using (FileStream fs = File.Create(big))
            {
                byte[] block = new byte[Mib];
                Random.Shared.NextBytes(block);
                for (int i = 0; i < 200; i++)
                {
                    fs.Write(block);
                }
            }

            string[] small = [.. Enumerable.Range(0, 20).Select(i => Path.Combine(dir, $"s{i}.dat"))];
            foreach (string s in small)
            {
                File.WriteAllBytes(s, new byte[300 * 1024]);
            }

            var engine = new ReadAhead(new ReadAheadOptions([dir], 64 * Mib, 4096 * Mib, 0, () => 1 << 20, Stream: false), new FileReadAheadIo());
            engine.Start();
            foreach (string s in small)
            {
                engine.OnOpen(s); // 열기만: 읽지 않음
            }

            for (long off = 0; off < 200 * Mib; off += Mib)
            {
                engine.OnRead(big, off, Mib);
                Thread.Sleep(off / Mib % 8 == 7 ? 150 : 3); // 게임이 읽은 데이터를 처리하는 시간, 여덟 번마다 틈
            }

            DateTime until = DateTime.UtcNow.AddSeconds(10);
            while (engine.Stats.ReadAheadBytes < 100 * Mib && DateTime.UtcNow < until)
            {
                Thread.Sleep(20);
            }

            engine.Dispose();
            ReadAheadStats st = engine.Stats;
            output.WriteLine($"read_ahead_bytes={st.ReadAheadBytes} game_read_bytes={st.GameReadBytes} hit_bytes={st.HitBytes} hit_ratio={st.HitRatio:F4} events={st.Events} failed_files={st.FailedFiles} sequential_files={st.SequentialFiles} low_io_failures={st.LowPriorityFailures} gap_count={st.GapCount} yielded_requests={st.YieldedRequests} read_ahead_while_idle_bytes={st.ReadAheadWhileIdleBytes}");
            Assert.True(st.ReadAheadBytes > 0);
            Assert.True(st.ReadAheadBytes <= 200 * Mib, $"small files must not be read whole: {st.ReadAheadBytes}");
            Assert.True(st.ReadAheadBytes <= (st.GameReadBytes * 3 / 2) + (64 * Mib), $"read ahead above the cap: {st.ReadAheadBytes}");
            Assert.True(st.GapCount > 0 && st.ReadAheadWhileIdleBytes > 0 && st.ReadAheadWhileIdleBytes <= st.ReadAheadBytes);
            Assert.Equal(1, st.SequentialFiles);
            Assert.Equal(200, st.ResolvedReads);
            Assert.Equal(200 * Mib, st.GameReadBytes);
            Assert.Null(engine.Fault);
            Assert.Equal(0, st.FailedFiles);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void StreamModeFollowsAContinuousSequentialReaderWithSixteenMibRequestsOnARealFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "aster_learn_live_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string big = Path.Combine(dir, "big.pak");
            using (FileStream fs = File.Create(big))
            {
                byte[] block = new byte[Mib];
                Random.Shared.NextBytes(block);
                for (int i = 0; i < 200; i++)
                {
                    fs.Write(block);
                }
            }

            var engine = new ReadAhead(new ReadAheadOptions([dir], 512 * Mib, 4096 * Mib, 0, () => 1 << 20), new FileReadAheadIo()); // 스트림 모드 (기본)
            engine.Start();
            for (long off = 0; off < 200 * Mib; off += Mib)
            {
                engine.OnRead(big, off, Mib);
                Thread.Sleep(3); // 틈 없이 계속 읽음
            }

            DateTime until = DateTime.UtcNow.AddSeconds(10);
            while (engine.Stats.ReadAheadBytes < 100 * Mib && DateTime.UtcNow < until)
            {
                Thread.Sleep(20);
            }

            engine.Dispose();
            ReadAheadStats st = engine.Stats;
            output.WriteLine($"stream read_ahead_bytes={st.ReadAheadBytes} game_read_bytes={st.GameReadBytes} hit_bytes={st.HitBytes} hit_ratio={st.HitRatio:F4} failed_files={st.FailedFiles} gap_count={st.GapCount} yielded_requests={st.YieldedRequests}");
            Assert.True(st.ReadAheadBytes >= 100 * Mib);
            Assert.True(st.ReadAheadBytes <= (st.GameReadBytes * 3 / 2) + (64 * Mib), $"read ahead above the cap: {st.ReadAheadBytes}");
            Assert.Equal((0L, 0L), (st.GapCount, st.YieldedRequests));
            Assert.Equal(1, st.SequentialFiles);
            Assert.Equal(0, st.FailedFiles);
            Assert.Null(engine.Fault);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
