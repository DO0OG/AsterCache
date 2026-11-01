using System.ComponentModel;
using AsterCache.Core;
using AsterCache.Core.Apply;
using AsterCache.Core.Json;
using AsterCache.Core.Plan;
using Xunit;

namespace AsterCache.Tests.Core;

/// tests/test_relayout.py 의 apply / revert / verify 시험 포팅 (가짜 볼륨 + 실제 임시 파일).
public sealed class ApplyPortTests : ApplyTestBase
{
    private static ApplyOptions Exec(long chunkBytes = 4L << 20, long writeCap = 2L << 30, string? saveTo = null) =>
        new(Execute: true, ChunkBytes: chunkBytes, WriteCap: writeCap, SaveTo: saveTo);

    private List<JObject> JournalRows() => new Journal(JournalPath).Rows.ToList();

    // ---- ApplyTests

    [Fact]
    public void WithoutExecute_NothingMovesOrIsWritten()
    {
        ApplyReport rep = Apply(Make());

        Assert.False(rep.Executed);
        Assert.Equal(5, rep.TasksTodo);
        Assert.Empty(Vol.Moves);
        Assert.False(File.Exists(JournalPath));
        Assert.True(SameExtents(Vol.Ext, Files));
    }

    [Fact]
    public void ExecuteMovesInChunksAndJournals()
    {
        PlanFile p = Make();
        string saved = Path.Combine(Root, "plan.json");

        ApplyReport rep = Apply(p, Exec(8 * Cb, saveTo: saved));

        Assert.Empty(rep.Failed);
        Assert.All(Vol.Moves, m => Assert.True(m.N <= 8));
        Assert.Equal((long)p.Stats["moved_bytes"]!, rep.MovedBytes);
        Assert.Equal(0L, rep.ZoneStartLcn); // 가짜 볼륨에서 가장 낮은 빈 구간
        Assert.Equal(Vol.Moves.OrderBy(m => m.Lcn).ToList(), Vol.Moves); // 목표는 엄격히 위로 씀 (SMR 친화)
        List<JObject> rows = JournalRows();
        Assert.Equal(["end", "file", "header", "step"], rows.Select(r => (string)r["type"]!).Distinct().Order().ToList());
        Assert.All(rows.Where(r => Equals(r["type"], "step") && Equals(r["state"], "planned")), r => Assert.NotEmpty((List<object?>)r["from"]!));
        Assert.Equal("done", new Journal(JournalPath).LastState(0));
        Assert.Equal(rep.ZoneStartLcn, PlanFile.Load(saved).Zone.StartLcn);
    }

    [Fact]
    public void DryRunFixesZoneInPlanFileButMovesNothing()
    {
        string saved = Path.Combine(Root, "plan.json");
        Make().Save(saved);

        ApplyReport rep = Apply(PlanFile.Load(saved), new ApplyOptions(DryRun: true, SaveTo: saved));

        Assert.True(rep.PlanSaved);
        Assert.False(rep.Executed);
        Assert.Empty(Vol.Moves);
        Assert.Equal(0, PlanFile.Load(saved).Zone.StartLcn % 4);
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public void WriteCapStopsAndSecondRunFinishes()
    {
        PlanFile p = Make().Resolve(20000);

        ApplyReport r1 = Apply(p, Exec(8 * Cb, 64 * Cb));

        Assert.Equal("write_cap", r1.Stopped);
        int done = Vol.Moves.Count;
        ApplyReport r2 = Apply(p, Exec(8 * Cb));
        Assert.Null(r2.Stopped);
        Assert.True(Vol.Moves.Count > done);
        Assert.True(Verify(p).Contiguous);
        List<JObject> rows = JournalRows();
        Assert.Equal(1, rows.Count(r => Equals(r["type"], "header")));
        var firsts = rows.Where(r => Equals(r["type"], "step") && Equals(r["state"], "planned")).ToList();
        Assert.Equal(firsts.Count, firsts.Select(r => r["idx"]).Distinct().Count()); // from 은 단계마다 한 번만 기록
        Assert.Equal(new string?[] { "write_cap", null }, rows.Where(r => Equals(r["type"], "end")).Select(r => (string?)r["stopped"]).ToList());
    }

    [Fact]
    public void ResumeAfterInterruptionKeepsOriginalExtents()
    {
        PlanFile p = Make().Resolve(20000);
        Vol.FailAfter = 3;

        ApplyReport rep = Apply(p, Exec(8 * Cb));

        Assert.NotEmpty(rep.Failed);
        Vol.FailAfter = null;
        rep = Apply(p, Exec(8 * Cb));
        Assert.Empty(rep.Failed);
        Assert.True(Verify(p).Contiguous);
        var allOrig = Files.Values.SelectMany(x => x).ToList();
        var recorded = JournalRows().Where(r => Equals(r["type"], "step") && r.ContainsKey("from"))
            .SelectMany(r => ((List<object?>)r["from"]!).Cast<List<object?>>().Select(e => (Lcn: (long)e[1]!, N: (long)e[2]!))).ToList();
        Assert.NotEmpty(recorded); // 기록된 모든 원래 위치는 이동 전 extent 안에 있다
        Assert.All(recorded, e => Assert.Contains(allOrig, o => o.Lcn <= e.Lcn && e.Lcn + e.N <= o.Lcn + o.Clusters));
    }

    [Fact]
    public void ShaMismatchIsReported()
    {
        var calls = new List<string>();
        string Digest(string path, long size, IReadOnlyList<(long Offset, long Length)> extra)
        {
            calls.Add(path);
            return calls.Count <= 2 ? "a" : "b";
        }

        var ex = Assert.Throws<ShaMismatchException>(() => Apply(Make().Resolve(20000), Exec(), Hooks(Digest)));

        Assert.Contains("sha_mismatch", ex.Message, StringComparison.Ordinal);
        JObject end = JournalRows()[^1]; // 예외 때도 end 행이 남는다
        Assert.Equal("end", end["type"]);
        Assert.Equal(2, ((List<object?>)end["sha_mismatch"]!).Count);
    }

    [Fact]
    public void PathOutsideRootAndChangedFileRejected()
    {
        PlanFile p = Make();

        Assert.Throws<PlanRejectedException>(() => Applier.Apply(p, Vol, [Path.Combine(Root, "other")], Path.Combine(Root, "j"), Hooks()));
        File.WriteAllBytes(F1, new byte[10]);
        var ex = Assert.Throws<PlanRejectedException>(() => Apply(p));
        Assert.Contains("source_changed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AttributeFilterSkipsFiles()
    {
        ApplyReport rep = Apply(Make(), hooks: Hooks(skip: p => p == F2 ? "compressed" : null));

        Assert.Equal(new KeyValuePair<string, string>(F2, "compressed"), Assert.Single(rep.SkippedFiles));
        Assert.Equal(3, rep.TasksTodo);
    }

    [Fact]
    public void ZoneInUseIsRejected()
    {
        Vol.Ext["other"] = [new(0, 20000, 8)];

        var ex = Assert.Throws<PlanRejectedException>(() => Apply(Make().Resolve(20000)));

        Assert.Contains("zone_not_free", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RevertRestoresOriginalExtents()
    {
        PlanFile p = Make().Resolve(20000);
        Apply(p, Exec());

        RevertReport dry = Reverter.Revert(JournalPath, Vol, [Root], Hooks());
        Assert.False(dry.Executed);
        Assert.NotEmpty(Vol.Moves);
        RevertReport rep = Reverter.Revert(JournalPath, Vol, [Root], Hooks(), Exec());

        Assert.Empty(rep.Skipped);
        Assert.True(SameExtents(Vol.Ext, Files));
    }

    // ---- 신규: 되돌리기 역순, 자리 점유 건너뜀

    [Fact]
    public void RevertGoesInReverseOrder()
    {
        Apply(Make().Resolve(20000), Exec());
        int applied = Vol.Moves.Count;

        Reverter.Revert(JournalPath, Vol, [Root], Hooks(), Exec());

        long[] idx = JournalRows().Where(r => Equals(r["type"], "revert")).Select(r => (long)r["idx"]!).ToArray();
        Assert.True(idx.Length > 1);
        Assert.Equal(idx.OrderByDescending(x => x), idx);
        Assert.True(Vol.Moves.Count > applied);
    }

    [Fact]
    public void RevertSkipsPieceWhoseOriginalSpotIsTaken()
    {
        Apply(Make().Resolve(20000), Exec());
        Vol.Ext["other"] = [new(0, 1000, 8)]; // f1 의 원래 자리 앞부분을 다른 파일이 씀
        Sleeps.Clear();

        RevertReport rep = Reverter.Revert(JournalPath, Vol, [Root], Hooks(), Exec());

        Assert.Single(rep.Skipped);
        Assert.NotEqual(Files[F1][0], Vol.Ext[F1][0]);
        Assert.True(Vol.Ext[F2].SequenceEqual(Files[F2])); // 나머지 파일은 끝까지 되돌림
        Assert.Equal(2, Sleeps.Count); // 재시도 3번 사이 대기 2번
    }

    [Fact]
    public void RevertWriteCapStops()
    {
        Apply(Make().Resolve(20000), Exec());

        RevertReport rep = Reverter.Revert(JournalPath, Vol, [Root], Hooks(), Exec(8 * Cb, 8 * Cb));

        Assert.Equal("write_cap", rep.Stopped);
        Assert.Equal(8 * Cb, rep.MovedBytes);
    }

    // ---- verify_layout

    [Fact]
    public void VerifyLayoutBeforeAndAfter()
    {
        PlanFile p = Make().Resolve(20000);

        LayoutReport v = Verify(p);
        Assert.False(v.Contiguous);
        Assert.True(v.OrderedRuns > 1);
        Apply(p, Exec());
        v = Verify(p);

        Assert.True(v.Contiguous);
        Assert.Equal(1, v.OrderedRuns);
        Assert.True(v.Ascending);
        Assert.Equal(v.HotClusters, v.InZoneClusters);
    }

    [Fact]
    public void VerifyLayoutCountsMissingFilesAndUnknownZone()
    {
        PlanFile p = Make();

        LayoutReport v = LayoutVerifier.Verify(p, _ => null);

        Assert.False(v.Contiguous);
        Assert.Equal(p.Order.Count, v.MissingFiles);
        Assert.Null(v.InZoneClusters); // 존 시작을 아직 모름
    }

    // ---- 볼륨 신원과 거부

    [Fact]
    public void AllowedRootOnOtherDriveOrPlanOnOtherVolumeRejected()
    {
        PlanFile p = Make();
        string other = Letter == "Q:" ? @"R:\" : @"Q:\";

        Assert.Throws<PlanRejectedException>(() => Applier.Apply(p, Vol, [other], JournalPath, Hooks()));
        Vol.Letter = other[..2];
        Assert.Throws<PlanRejectedException>(() => Apply(p)); // 열린 볼륨이 계획 볼륨과 다름
    }

    [Fact]
    public void LinkResolvingToOtherDriveRejected()
    {
        PlanFile p = Make();
        ApplyHooks h = new()
        {
            Extents = x => Vol.Ext[x],
            Digest = (x, size, extra) => string.Empty,
            SkipReason = _ => null,
            RealPath = x => x == F1 ? @"Q:\elsewhere\f1.bin" : Path.GetFullPath(x),
        };

        Assert.Throws<PlanRejectedException>(() => Applier.Apply(p, Vol, [Root], JournalPath, h));
    }

    [Fact]
    public void RevertChecksRootsDrive()
    {
        Assert.Throws<PlanRejectedException>(() => Reverter.Revert(Path.Combine(Root, "none.jsonl"), Vol, [@"Q:\x"], Hooks()));
        Assert.Throws<PlanRejectedException>(() => Reverter.Revert(Path.Combine(Root, "none.jsonl"), Vol, [], Hooks()));
    }

    [Fact]
    public void RevertRejectsJournalPathOutsideRoots()
    {
        Apply(Make().Resolve(20000), Exec());

        Assert.Throws<PlanRejectedException>(() => Reverter.Revert(JournalPath, Vol, [Path.Combine(Root, "sub")], Hooks(), Exec()));
    }

    [Fact]
    public void ClusterSizeMismatchRejected()
    {
        Vol.ClusterBytes = 512;

        Assert.Throws<PlanRejectedException>(() => Apply(Make()));
    }

    [Fact]
    public void MalformedPlanRejected()
    {
        PlanFile p = Make();
        PlanFile bad = p with { Tasks = [p.Tasks[0] with { Clusters = 0 }] };

        Assert.Throws<PyValueException>(() => Apply(bad));
    }

    [Fact]
    public void JournalOfDifferentPlanIsRefused()
    {
        File.WriteAllText(JournalPath, "{\"type\": \"header\", \"plan_sha\": \"x\"}\r\n");

        Assert.Throws<PlanRejectedException>(() => Apply(Make().Resolve(20000), Exec()));

        Assert.Empty(Vol.Moves);
        Assert.Single(JournalRows()); // 아무것도 덧붙이지 않음
    }

    // ---- 이동 재시도와 실패 중단

    [Fact]
    public void MoveRetriesAfterShortWaitAndSucceeds()
    {
        Vol.FailNext = 1;

        ApplyReport rep = Apply(Make().Resolve(20000), Exec());

        Assert.Empty(rep.Failed);
        Assert.Equal(TimeSpan.FromSeconds(0.2), Assert.Single(Sleeps));
    }

    [Fact]
    public void MoveFailingThreeTimesMarksStepFailed()
    {
        Vol.FailNext = 3;

        ApplyReport rep = Apply(Make().Resolve(20000), Exec());

        Assert.Single(rep.Failed);
        Assert.Equal(2, Sleeps.Count);
        JObject failed = JournalRows().Single(r => Equals(r["type"], "step") && Equals(r["state"], "failed"));
        Assert.Equal(rep.Failed[0].Reason, failed["reason"]);
    }

    [Fact]
    public void UnexpectedExceptionStillWritesEndRow()
    {
        Vol.CrashAt = 2;

        var ex = Assert.Throws<InvalidOperationException>(() => Apply(Make().Resolve(20000), Exec(8 * Cb)));

        Assert.Equal("simulated crash", ex.Message);
        JObject end = JournalRows()[^1];
        Assert.Equal("end", end["type"]);
        Assert.Equal(2 * 8 * Cb, end["written_bytes"]);
    }

    [Fact]
    public void ErrorStringOfWin32ExceptionIsKept()
    {
        Vol.FailNext = 3;

        ApplyReport rep = Apply(Make().Resolve(20000), Exec());

        Assert.Equal(new Win32Exception(5).Message, rep.Failed[0].Reason);
    }

    // ---- PlanGuards 단위

    [Theory]
    [InlineData(@"D:\AsterBench\Data\X.bin", @"d:/asterbench", true)]
    [InlineData(@"D:\AsterBench", @"D:\AsterBench\", true)]
    [InlineData(@"D:\AsterBench2\x.bin", @"D:\AsterBench", false)]
    [InlineData(@"E:\AsterBench\x.bin", @"D:\AsterBench", false)]
    public void InsideComparesCaseAndSeparatorInsensitivePrefixes(string path, string root, bool expected) =>
        Assert.Equal(expected, PlanGuards.Inside(path, [root], x => x));

    // ---- 교차 확인용: 환경 변수가 있으면 C# 이 쓴 저널을 복사해 둡니다 (Python Journal 로 읽어 확인)

    [Fact]
    public void WrittenJournalCanBeExported()
    {
        Apply(Make().Resolve(20000), Exec(8 * Cb));
        string? export = Environment.GetEnvironmentVariable("ASTER_JOURNAL_EXPORT");
        if (!string.IsNullOrEmpty(export))
        {
            File.Copy(JournalPath, export, overwrite: true);
        }

        Assert.True(JournalRows().Count > 3);
    }
}
