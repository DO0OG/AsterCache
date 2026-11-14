using AsterCache.Core.Prefetch;
using AsterCache.Prefetch;
using AsterCache.Tests.Windows;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Prefetch;

/// 스파스 속성 파일은 섹션 (매핑) 없이 버퍼드 읽기로 읽고, 읽는 순서와 일반 파일의 처리는 그대로인지. 속성 조회와 PVM 호출은 가짜로 바꿔 확인합니다.
public sealed class ZoneStreamerSparseTests
{
    private const long Mib = 1 << 20;
    private const uint Archive = 0x20;

    private static (long Bytes, long Requests, long Errors, bool Aborted, long Hint, long Buffered) Run(IReadOnlyList<ReadOp> ops, HashSet<string> sparse, CancellationToken ct, Func<bool>? onPrefetch, List<string> notes, List<string>? asked = null, Action<string>? onNote = null)
    {
        (long bytes, long requests, long errors) = ZoneStreamer.RunPvmOrBuffered(ops, ZoneStreamer.DefaultChunkBytes, ct, out bool aborted, out long hint, out long buffered,
            _ => onPrefetch?.Invoke() ?? true, path =>
            {
                asked?.Add(path);
                return sparse.Contains(path) ? Archive | FileGuards.SparseAttribute : Archive;
            }, (stage, _, _) =>
            {
                notes.Add(stage);
                onNote?.Invoke(stage);
            });
        return (bytes, requests, errors, aborted, hint, buffered);
    }

    [Fact]
    public void RangesOfSparseFilesAreReadBufferedAndNeverReachThePvmPath()
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("s.bin", (int)(4 * Mib));
        string s = tmp.Combine("s.bin");
        int calls = 0;

        var r = Run([new(s, 0, Mib), new(s, 2 * Mib, 2 * Mib)], [s], CancellationToken.None, () => { calls++; return true; }, []);

        Assert.Equal(0, calls); // PVM 경로로 가면 섹션을 열고 이 호출이 일어남
        Assert.Equal((3 * Mib, 2L, 0L, false), (r.Bytes, r.Requests, r.Errors, r.Aborted));
        Assert.Equal((0L, 3 * Mib), (r.Hint, r.Buffered));
    }

    [Fact]
    public void AMixedBatchIsReadInListOrderWithEachSideCountedApart()
    {
        using var tmp = new TempDir();
        foreach (string n in new[] { "a.bin", "s1.bin", "s2.bin", "b.bin" })
        {
            tmp.WriteRandom(n, (int)(2 * Mib));
        }

        var notes = new List<string>();
        var asked = new List<string>();
        string[] p = [.. new[] { "a.bin", "s1.bin", "s2.bin", "b.bin" }.Select(tmp.Combine)];

        var r = Run([new(p[0], 0, Mib), new(p[1], 0, 2 * Mib), new(p[2], 0, Mib), new(p[3], 0, Mib), new(p[1], 0, Mib)], [p[1], p[2]], CancellationToken.None, null, notes, asked);

        Assert.Equal(["pvm fragment 1/1 started, progress 0/1048576 bytes", "pvm fragment 1/1 completed, progress 1048576/1048576 bytes", "buffered read of sparse files",
            "pvm fragment 1/1 started, progress 0/1048576 bytes", "pvm fragment 1/1 completed, progress 1048576/1048576 bytes", "buffered read of sparse files"], notes); // 일반 / 스파스 2개 / 일반 / 스파스
        Assert.Equal((6 * Mib, 5L, 0L, false), (r.Bytes, r.Requests, r.Errors, r.Aborted));
        Assert.Equal((2 * Mib, 4 * Mib), (r.Hint, r.Buffered));
        Assert.Equal(p, asked); // 파일마다 한 번만 조회
    }

    [Fact]
    public void ABatchWithoutSparseFilesGivesTheSameResultAsThePvmPathAlone()
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("f.bin", (int)(20 * Mib));
        ReadOp[] ops = [new(tmp.Combine("f.bin"), 0, 20 * Mib), new(tmp.Combine("none.bin"), 0, Mib)];
        int calls = 0;

        var split = Run(ops, [], CancellationToken.None, () => { calls++; return true; }, []);
        (long bytes, long requests, long errors) = ZoneStreamer.RunPvm(ops, CancellationToken.None, out bool aborted, out long hint, _ => true);

        Assert.Equal(3, calls);
        Assert.Equal((bytes, requests, errors, aborted, hint, 0L), split);
    }

    [Fact]
    public void ErrorsOfSparseFilesAreCountedAndTheRestKeepsGoing()
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("f.bin", (int)Mib);
        string gone = tmp.Combine("gone.bin");

        var r = Run([new(gone, 0, Mib), new(tmp.Combine("f.bin"), 0, Mib)], [gone], CancellationToken.None, null, []);

        Assert.Equal((Mib, 1L, 1L, false), (r.Bytes, r.Requests, r.Errors, r.Aborted));
        Assert.Equal((Mib, 0L), (r.Hint, r.Buffered));
    }

    [Fact]
    public void AnAttributeReadThatThrowsIsNotTakenForSparseSoThePvmPathIsUsed()
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("f.bin", (int)Mib);
        int calls = 0;

        (long bytes, _, _) = ZoneStreamer.RunPvmOrBuffered([new(tmp.Combine("f.bin"), 0, Mib)], ZoneStreamer.DefaultChunkBytes, CancellationToken.None, out _, out long hint, out long buffered,
            _ => { calls++; return true; }, _ => throw new IOException("attributes unreadable"));

        Assert.Equal((1, Mib, Mib, 0L), (calls, bytes, hint, buffered));
    }

    [Fact]
    public void CancelledAtTheStartOfTheSparsePartStopsItAndReadsNothingAfterIt()
    {
        using var tmp = new TempDir();
        foreach (string n in new[] { "n0.bin", "s.bin", "n1.bin" })
        {
            tmp.WriteRandom(n, (int)Mib);
        }

        using var cts = new CancellationTokenSource();
        int calls = 0;

        var r = Run([new(tmp.Combine("n0.bin"), 0, Mib), new(tmp.Combine("s.bin"), 0, Mib), new(tmp.Combine("n1.bin"), 0, Mib)], [tmp.Combine("s.bin")], cts.Token,
            () => { calls++; return true; }, [], onNote: stage => { if (stage.StartsWith("buffered", StringComparison.Ordinal)) { cts.Cancel(); } });

        Assert.Equal((1, Mib, true), (calls, r.Bytes, r.Aborted)); // 앞 일반 구간만 읽고, 스파스 구간은 시작하자마자 멈추고, 뒤 일반 구간은 부르지 않음
        Assert.Equal(0L, r.Buffered);
    }

    [Fact]
    public void AlreadyCancelledReadsNoAttributesAndNothingElse()
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("s.bin", (int)Mib);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var asked = new List<string>();

        var r = Run([new(tmp.Combine("s.bin"), 0, Mib)], [tmp.Combine("s.bin")], cts.Token, null, [], asked);

        Assert.Equal((0L, true, 0L), (r.Bytes, r.Aborted, r.Buffered));
        Assert.Empty(asked);
    }

    [Fact]
    public void CancelledInTheMiddleOfTheSparsePartStopsBeforeTheNextRangeAndSetsAborted()
    {
        using var tmp = new TempDir();
        foreach (string n in new[] { "s0.bin", "s1.bin", "s2.bin" })
        {
            tmp.WriteRandom(n, (int)Mib);
        }

        string[] p = [tmp.Combine("s0.bin"), tmp.Combine("s1.bin"), tmp.Combine("s2.bin")];
        using var cts = new CancellationTokenSource();
        int calls = 0;

        // 스파스 구간 하나 (목록 전체) 를 읽는 중, 둘째 범위를 꺼내는 순간 취소: 첫 범위만 읽고 멈춤 (범위마다 취소를 확인)
        var r = Run(new CancelAt([new(p[0], 0, Mib), new(p[1], 0, Mib), new(p[2], 0, Mib)], 1, cts), [.. p], cts.Token, () => { calls++; return true; }, []);

        Assert.Equal((Mib, 1L, true, 0), (r.Bytes, r.Requests, r.Aborted, calls));
        Assert.Equal(Mib, r.Buffered);
    }

    // 읽기 열거 (둘째 열거) 에서 index 번째 op 를 꺼내는 순간 취소하는 목록 (RunRead 가 읽는 도중 게임이 뜨는 상황).
    private sealed class CancelAt(List<ReadOp> ops, int index, CancellationTokenSource cts) : IReadOnlyList<ReadOp>
    {
        public int Count => ops.Count;

        public ReadOp this[int i] => ops[i];

        private int _enumerations;

        public IEnumerator<ReadOp> GetEnumerator()
        {
            bool reading = ++_enumerations == 2; // 첫 열거는 알림용 바이트 합계, 둘째가 RunRead 의 읽기
            for (int i = 0; i < ops.Count; i++)
            {
                if (reading && i == index)
                {
                    cts.Cancel();
                }

                yield return ops[i];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void CancelledDuringThePvmPartAlsoSkipsTheSparsePartAfterIt()
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("n.bin", (int)(20 * Mib));
        tmp.WriteRandom("s.bin", (int)Mib);
        using var cts = new CancellationTokenSource();
        int calls = 0;

        var r = Run([new(tmp.Combine("n.bin"), 0, 20 * Mib), new(tmp.Combine("s.bin"), 0, Mib)], [tmp.Combine("s.bin")], cts.Token, () => { calls++; cts.Cancel(); return true; }, []);

        Assert.Equal((1, 8 * Mib, true, 0L), (calls, r.Bytes, r.Aborted, r.Buffered)); // 첫 8 MiB 조각 직후 취소
    }

    [Fact]
    public void CancelledRightAfterTheLastPvmCallStillStopsBeforeTheSparsePart()
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("n.bin", (int)(4 * Mib));
        tmp.WriteRandom("s.bin", (int)Mib);
        using var cts = new CancellationTokenSource();

        var r = Run([new(tmp.Combine("n.bin"), 0, 4 * Mib), new(tmp.Combine("s.bin"), 0, Mib)], [tmp.Combine("s.bin")], cts.Token, () => { cts.Cancel(); return true; }, []);

        Assert.Equal((4 * Mib, true, 0L), (r.Bytes, r.Aborted, r.Buffered)); // PVM 구간만 보면 끝났지만 뒤 구간이 남았으므로 중단
    }

    [Fact]
    public void RunReportsNoBufferedBytesForOrdinaryFilesAndTheNoteAppearsOnlyWhenThereAreSome()
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("f.bin", 10_000);

        StreamStats r = ZoneStreamer.Run([new(tmp.Combine("f.bin"), 0, 10_000)], StreamMethod.Pvm);

        Assert.Equal((10_000L, 10_000L, 0L), (r.Bytes, r.HintAcceptedBytes, r.BufferedBytes));
        Assert.Equal(string.Empty, r.BufferedNote());
        Assert.Equal(", 3 MiB of sparse files by buffered read", new StreamStats(3 * Mib, 1, 0, 1) { BufferedBytes = 3 * Mib }.BufferedNote());
    }
}
