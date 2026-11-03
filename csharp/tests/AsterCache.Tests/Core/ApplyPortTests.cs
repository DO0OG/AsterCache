using System.ComponentModel;
using AsterCache.Core;
using AsterCache.Core.Apply;
using AsterCache.Core.Json;
using AsterCache.Core.Model;
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
        Assert.Equal(["end", "header", "sha", "step"], rows.Select(r => (string)r["type"]!).Distinct().Order().ToList());
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
    public void CancelAfterKthChunkStopsWithResumePointAndNextRunContinuesAfterIt()
    {
        PlanFile p = Make().Resolve(20000);
        Assert.False(Verify(p).ResumeNeeded); // 아직 아무것도 안 옮김: 부분 적용이 아님
        const int k = 3;
        using var cts = new CancellationTokenSource();
        Vol.AfterMove = n =>
        {
            if (n == k)
            {
                cts.Cancel();
            }
        };

        ApplyReport r1 = Applier.Apply(p, Vol, [Root], JournalPath, Hooks(), Exec(8 * Cb), cts.Token);

        Assert.Equal("cancelled", r1.Stopped);
        Assert.Empty(r1.Failed);
        Assert.Equal(k, Vol.Moves.Count); // 취소가 걸린 조각까지만 끝내고 멈춤
        List<JObject> rows = JournalRows();
        Assert.Equal("cancelled", rows[^1]["stopped"]);
        var jrn = new Journal(JournalPath);
        long inFlight = rows.Where(r => Equals(r["type"], "step")).Select(r => (long)r["idx"]!).Distinct().Order().First(i => jrn.LastState(i) == "planned");
        Assert.Equal("planned", jrn.LastState(inFlight)); // 끝나지 않은 단계는 planned 로 남음 (미리 기록한 뒤 단계 포함)
        LayoutReport partial = Verify(p);
        Assert.False(partial.Contiguous);
        Assert.True(partial.ResumeNeeded);
        Assert.Equal("resume-needed", AsterCache.Cli.Commands.VolumeCommands.Verdict(partial)["verdict"]);

        Vol.AfterMove = null;
        var firstK = Vol.Moves.ToList();
        ApplyReport r2 = Apply(p, Exec(8 * Cb));

        Assert.Null(r2.Stopped);
        Assert.Equal(firstK, Vol.Moves.Take(k).ToList());
        Assert.Equal((firstK[^1].Path, firstK[^1].Vcn + firstK[^1].N), (Vol.Moves[k].Path, Vol.Moves[k].Vcn)); // k+1 번째 조각부터
        Assert.Equal(Vol.Moves.Count, Vol.Moves.Select(m => (m.Path, m.Vcn)).Distinct().Count()); // 어떤 조각도 두 번 옮기지 않음
        LayoutReport full = Verify(p);
        Assert.True(full.Contiguous);
        Assert.False(full.ResumeNeeded);
        rows = JournalRows();
        Assert.Equal(1, rows.Count(r => Equals(r["type"], "header")));
        Assert.Equal(new string?[] { "cancelled", null }, rows.Where(r => Equals(r["type"], "end")).Select(r => (string?)r["stopped"]).ToList());
    }

    [Fact]
    public void AlreadyCancelledTokenMovesNothingButClosesTheJournal()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        ApplyReport rep = Applier.Apply(Make().Resolve(20000), Vol, [Root], JournalPath, Hooks(), Exec(8 * Cb), cts.Token);

        Assert.Equal("cancelled", rep.Stopped);
        Assert.Empty(Vol.Moves);
        Assert.Equal("cancelled", JournalRows()[^1]["stopped"]);
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
    public void FreeZoneIsKeptAsIs()
    {
        ApplyReport rep = Apply(Make().Resolve(20000), Exec());

        Assert.Equal(20000L, rep.ZoneStartLcn);
        Assert.False(rep.ZoneRetargeted);
        Assert.Null(rep.JournalArchivedTo);
        Assert.False(rep.ToJson().ContainsKey("zone_retargeted"));
    }

    [Fact]
    public void ZoneInUseWithUnfinishedJournalStepIsRejected()
    {
        Vol.Ext["other"] = [new(0, 20000, 8)];
        using (var jr = new Journal(JournalPath))
        {
            jr.Add(new JObject { { "type", "header" }, { "plan_sha", "x" } });
            jr.Add(new JObject { { "type", "step" }, { "idx", 0L }, { "state", "planned" }, { "path", F1 } });
        }

        var ex = Assert.Throws<PlanRejectedException>(() => Apply(Make().Resolve(20000)));

        Assert.Contains("zone_not_free", ex.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(JournalPath));
    }

    [Fact]
    public void ZoneInUseWithoutJournalMovesToNewZone()
    {
        Vol.Ext["other"] = [new(0, 20000, 8)];

        ApplyReport rep = Apply(Make().Resolve(20000));

        Assert.True(rep.ZoneRetargeted);
        Assert.NotEqual(20000L, rep.ZoneStartLcn);
        Assert.Null(rep.JournalArchivedTo);
    }

    [Fact]
    public void ZoneInUseAfterFinishedJournalArchivesItAndContinuesInNewZone()
    {
        string saved = Path.Combine(Root, "plan.json");
        PlanFile p = Make().Resolve(20000);
        Apply(p, Exec());
        Reverter.Revert(JournalPath, Vol, [Root], Hooks(), Exec());
        Vol.Ext["other"] = [new(0, 20000, 8)];
        Vol.Moves.Clear();
        p.Save(saved);

        ApplyReport dry = Apply(PlanFile.Load(saved), new ApplyOptions(DryRun: true, SaveTo: saved));

        string archived = Assert.Single(Directory.GetFiles(Root, "j.jsonl.*"));
        Assert.Equal(archived, dry.JournalArchivedTo);
        Assert.Matches(@"j\.jsonl\.\d{8}T\d{6}Z$", archived);
        Assert.False(File.Exists(JournalPath));
        Assert.True(dry.ZoneRetargeted);
        Assert.True(dry.PlanSaved);
        PlanFile again = PlanFile.Load(saved);
        Assert.NotEqual(20000L, again.Zone.StartLcn);
        Assert.NotEqual(p.Sha(), again.Sha());
        Assert.Empty(Vol.Moves);

        ApplyReport rep = Apply(again, Exec());

        Assert.False(rep.ZoneRetargeted);
        Assert.Empty(rep.Failed);
        Assert.True(Verify(again).Contiguous);
        Assert.Contains(new Journal(archived).Rows, r => Equals(r["type"], "revert"));
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
    public void VerifyLayoutLeavesVolatileFilesOfAnOldPlanOutOfTheZoneAndRunCount()
    {
        // 옛 계획 (excluded_files 키 없음): 게임이 다시 쓴 설정 파일이 존 밖에 있어도 나머지가 이어져 있으면 계속 흐트러진 것으로 보지 않음
        var stats = new JObject { { "expected_runs", 1L }, { "current_runs", 3L } };
        PlanFile p = Make() with
        {
            Zone = new PlanZone(100, 20),
            Order = [new(@"D:\g.bin", 0, 10), new(@"D:\g\settings.json", 0, 10), new(@"D:\g.bin", 0, 10)],
            Stats = stats,
        };
        var layout = new Dictionary<string, IReadOnlyList<Extent>?>
        {
            [@"D:\g.bin"] = [new Extent(0, 100, 10)],
            [@"D:\g\settings.json"] = [new Extent(0, 9000, 10)],
            [@"D:\g.bin"] = [new Extent(0, 110, 10)],
        };

        LayoutReport v = LayoutVerifier.Verify(p, f => layout[f]);

        Assert.True(v.Contiguous);
        Assert.Equal(20, v.HotClusters);
        Assert.Equal(20, v.InZoneClusters);
        Assert.Equal(0, v.MissingFiles);
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
        Vol.FailCode = 87;
        Vol.FailNext = 3;

        ApplyReport rep = Apply(Make().Resolve(20000), Exec());

        Assert.Equal(new Win32Exception(87).Message, rep.Failed[0].Reason);
    }

    // ---- 단계별 시간과 대상 충돌 내성

    private PlanFile ManyTasks()
    {
        Sessions = [Enumerable.Range(0, 64).SelectMany(b => new[] { F1, F2 }.Select(f => new AsterCache.Core.Trace.TraceEvent(f, b * 16384L, 16384, 8.0))).ToList()];
        PlanFile plan = Make().Resolve(20000);
        Assert.True(plan.Tasks.Count > 22, $"tasks {plan.Tasks.Count}");
        return plan;
    }

    [Fact]
    public void TimingsAreInTheReportAndAddUpToTotal()
    {
        ApplyReport rep = Apply(Make().Resolve(20000), Exec());

        ApplyTimings t = rep.Timings;
        Assert.Equal(t.OpenS + t.DigestBeforeS + t.MoveS + t.DigestAfterS + t.JournalS + t.OtherS, t.TotalS, 3); // 각 항은 소수 3 자리로 반올림돼 있어 합도 그 자릿수에서만 같음
        Assert.True(t.OtherS >= 0 && t.TotalS > 0);
        Assert.Equal(2, t.Files);
        Assert.Equal(rep.TasksTodo, t.Tasks);
        var j = (JObject)rep.ToJson()["timings"]!;
        Assert.Equal(["open_s", "digest_before_s", "move_s", "move_wall_s", "digest_after_s", "journal_s", "other_s", "total_s", "tasks", "files"], j.Select(kv => kv.Key).ToArray());
        Assert.False(Apply(Make().Resolve(20000), new ApplyOptions(DryRun: true)).ToJson().ContainsKey("timings"));
    }

    [Fact]
    public void AccessDeniedFailsOnlyThatTaskAndContinuesUntilMoreThanTwentyInARow()
    {
        PlanFile plan = ManyTasks();
        Vol.FailNext = 3 * 21;

        ApplyReport rep = Apply(plan, Exec(8 * Cb));

        Assert.Equal(Applier.ZoneOccupied, rep.Stopped);
        Assert.Equal(21, rep.Failed.Count);
        Assert.All(rep.Failed, f => Assert.Equal(Applier.TargetBusy, f.Reason));
        Assert.Equal(Enumerable.Range(0, 21).Select(i => (long)rep.Failed[0].Idx + i), rep.Failed.Select(f => f.Idx)); // 연속, 다음 작업으로 계속
        Assert.Empty(Vol.Moves); // 중단했으므로 22 번째 작업은 시도하지 않음
        Assert.Equal(Applier.ZoneOccupied, JournalRows()[^1]["stopped"]);
    }

    [Fact]
    public void ABusyRunOfTwentyThenSuccessDoesNotStop()
    {
        PlanFile plan = ManyTasks();
        Vol.FailNext = 3 * 20;

        ApplyReport rep = Apply(plan, Exec(8 * Cb));

        Assert.Null(rep.Stopped);
        Assert.Equal(20, rep.Failed.Count);
        Assert.NotEmpty(Vol.Moves); // 21 번째 작업부터 정상으로 이어서 옮김
    }

    // ---- 표본 규칙 (edge64k-v1) 과 저널 묶음 쓰기

    private const long K64 = 64 << 10;

    [Fact]
    public void EdgeRangesKeepSmallSetsAndBoundaryRangesWhole()
    {
        // 범위 128 KiB 는 처음 64 KiB + 끝 64 KiB 가 맞붙어 전체, 64 KiB 는 두 끝이 같아 하나
        var r128 = Applier.EdgeRanges([(0, K64), (K64, K64)], 10 << 20);
        Assert.Equal(new[] { (0L, K64), (K64, K64) }, r128);
        Assert.Equal(new[] { (0L, K64) }, Applier.EdgeRanges([(0, K64), (0, K64)], 10 << 20));
        Assert.Equal(new[] { (0L, 1000L) }, Applier.EdgeRanges([(0, K64), (5 << 20, K64)], 1000)); // 파일 밖은 버리고 끝은 자름
    }

    [Fact]
    public void EdgeRangesCapTheTotalAtOneMiBAndAreDeterministic()
    {
        var many = Enumerable.Range(0, 100).Select(i => (Offset: i * 4 * K64, Length: K64)).ToList();

        var a = Applier.EdgeRanges(many, 1L << 40);
        var b = Applier.EdgeRanges(many.AsEnumerable().Reverse().ToList(), 1L << 40);

        Assert.Equal(16, a.Count);
        Assert.True(a.Sum(x => x.Length) <= Applier.DigestCapBytes);
        Assert.Equal(a, b);
        Assert.Equal(many[0], a[0]); // 고르게 솎되 처음은 남음
        Assert.True(a[^1].Offset > many[80].Offset);
        Assert.Equal(16, Applier.EdgeRanges(many.Take(16).ToList(), 1L << 40).Count); // 상한 이하면 그대로
    }

    private ApplyHooks CountingHooks(List<IReadOnlyList<(long Offset, long Length)>> legacy, List<IReadOnlyList<(long Offset, long Length)>> edge)
    {
        ApplyHooks h = Hooks();
        return new ApplyHooks
        {
            Extents = h.Extents,
            SkipReason = h.SkipReason,
            Sleep = h.Sleep,
            Digest = (p, size, extra) =>
            {
                legacy.Add(extra);
                return SampleHasher.Compute(p, size, extra, null);
            },
            EdgeDigest = (p, size, extra) =>
            {
                edge.Add(extra);
                return SampleHasher.Compute(p, size, extra, null, span: 0);
            },
        };
    }

    [Fact]
    public void NewJournalRecordsTheRuleAndHashesOnlyTheEdgeRanges()
    {
        var legacy = new List<IReadOnlyList<(long Offset, long Length)>>();
        var edge = new List<IReadOnlyList<(long Offset, long Length)>>();

        ApplyReport rep = Apply(Make().Resolve(20000), Exec() with { Verify = VerifyMode.All }, CountingHooks(legacy, edge));

        Assert.Empty(rep.Failed);
        Assert.Equal(Applier.EdgeRule, JournalRows().Single(r => Equals(r["type"], "header"))["digest_rule"]);
        Assert.Empty(legacy);
        Assert.Equal(2 * rep.TasksTodo, edge.Count); // 작업마다 이동 직전과 직후 (샘플이 있는 작업)
        Assert.All(edge, c => Assert.True(c.Sum(x => x.Length) <= Applier.DigestCapBytes));
    }

    [Fact]
    public void JournalWithoutDigestRuleKeepsTheLegacyRuleOnResume()
    {
        PlanFile p = Make().Resolve(20000);
        using (var jr = new Journal(JournalPath))
        {
            jr.Add(new JObject { { "type", "header" }, { "plan_sha", p.Sha() }, { "zone_start_lcn", p.Zone.StartLcn }, { "cluster_bytes", Cb }, { "started", 1.0 } }); // 규칙 없음 = 옛 저널
        }

        var legacy = new List<IReadOnlyList<(long Offset, long Length)>>();
        var edge = new List<IReadOnlyList<(long Offset, long Length)>>();

        ApplyReport rep = Apply(p, Exec(), CountingHooks(legacy, edge));

        Assert.Empty(rep.Failed);
        Assert.Empty(rep.ShaMismatch!);
        Assert.Empty(edge);
        Assert.Equal(4, legacy.Count);
        Assert.False(JournalRows().Single(r => Equals(r["type"], "header")).ContainsKey("digest_rule")); // 옛 저널에 규칙을 덧쓰지 않음
        Assert.True(Verify(p).Contiguous);
        Assert.Empty(Reverter.Revert(JournalPath, Vol, [Root], Hooks(), new ApplyOptions(Execute: true)).Skipped); // 되돌리기는 표본 규칙과 무관
    }

    [Fact]
    public void BatchedJournalFlushesEveryThirtyTwoRowsEverySecondAndOnClose()
    {
        long now = 0;
        using (var jr = new Journal(JournalPath) { Timestamp = () => now })
        {
            for (int i = 0; i < 100; i++)
            {
                jr.AddBatched(new JObject { { "type", "x" }, { "i", (long)i } });
            }

            Assert.Equal(3, jr.Flushes); // 32, 64, 96
            now += System.Diagnostics.Stopwatch.Frequency + 1;
            jr.AddBatched(new JObject { { "type", "x" } });
            Assert.Equal(4, jr.Flushes); // 1 초 경과
            jr.AddBatched(new JObject { { "type", "x" } });
            Assert.Equal(4, jr.Flushes);
            jr.Add(new JObject { { "type", "end" } });
            Assert.Equal(5, jr.Flushes); // 일반 Add 는 즉시
            jr.Flush();
            Assert.Equal(5, jr.Flushes); // 쌓인 것이 없으면 안 내림
            jr.AddBatched(new JObject { { "type", "x" } });
        }

        Assert.Equal(104, new Journal(JournalPath).Rows.Count); // 닫을 때 남은 줄도 기록
    }

    [Fact]
    public void LostJournalTailResumesWithoutMovingAnythingTwiceAndStillReverts()
    {
        PlanFile p = Make().Resolve(20000);
        using var cts = new CancellationTokenSource();
        Vol.AfterMove = n =>
        {
            if (n == 4)
            {
                cts.Cancel();
            }
        };
        _ = Applier.Apply(p, Vol, [Root], JournalPath, Hooks(), Exec(8 * Cb), cts.Token);
        Vol.AfterMove = null;
        int moved = Vol.Moves.Count;
        // 꼬리 유실: 묶음 쓰기라 마지막 done 과 end 가 디스크에 없는 상태. planned 는 이동 전에 확정되므로 남음.
        List<JObject> kept = JournalRows().Where(r => !Equals(r["type"], "end") && !(Equals(r["type"], "step") && Equals(r["state"], "done"))).ToList();
        File.WriteAllText(JournalPath, string.Concat(kept.Select(r => PyJson.Dumps(r) + "\r\n")));

        ApplyReport rep = Apply(p, Exec(8 * Cb));

        Assert.Null(rep.Stopped);
        Assert.Empty(rep.Failed);
        Assert.Equal(Vol.Moves.Count, Vol.Moves.Select(m => (m.Path, m.Vcn)).Distinct().Count()); // 이미 옮긴 작업을 다시 옮기지 않음
        Assert.True(Vol.Moves.Count > moved);
        Assert.True(Verify(p).Contiguous);
        RevertReport rv = Reverter.Revert(JournalPath, Vol, [Root], Hooks(), new ApplyOptions(Execute: true));
        Assert.Empty(rv.Skipped);
        Assert.True(SameExtents(Files, Vol.Ext));
    }

    // ---- 작업 단위 표본 (edge64k-v2) 인터리브와 planned 미리 기록

    [Fact]
    public void BeforeSamplesAreReadRightBeforeEachTasksMoveAndAfterSamplesComparePerTask()
    {
        var movesAtCall = new List<(string Key, int Moves)>();
        ApplyHooks h = Hooks();
        var hooks = new ApplyHooks
        {
            Extents = h.Extents,
            SkipReason = h.SkipReason,
            Sleep = h.Sleep,
            Digest = (p, size, extra) => "legacy",
            EdgeDigest = (p, size, extra) =>
            {
                movesAtCall.Add((p + string.Join(',', extra), Vol.Moves.Count));
                return SampleHasher.Compute(p, size, extra, null, span: 0);
            },
        };

        ApplyReport rep = Apply(Make().Resolve(20000), Exec(8 * Cb), hooks);

        Assert.Empty(rep.ShaMismatch!);
        var firstSeen = movesAtCall.GroupBy(c => c.Key).Select(g => g.First().Moves).ToList(); // 이동 전 표본
        Assert.True(firstSeen.Distinct().Count() > 1, "before samples must be spread over the moves, not read first"); // 모든 작업 먼저가 아님
        Assert.Equal(0, firstSeen[0]);
        Assert.Equal(Vol.Moves.Count, movesAtCall[^1].Moves); // 이동 직후 표본은 끝에
    }

    [Fact]
    public void ChangedContentAfterAMoveIsReportedAsShaMismatchPerTask()
    {
        var seen = new HashSet<string>();
        ApplyHooks h = Hooks();
        var hooks = new ApplyHooks
        {
            Extents = h.Extents,
            SkipReason = h.SkipReason,
            Sleep = h.Sleep,
            Digest = (p, size, extra) => "legacy",
            EdgeDigest = (p, size, extra) => seen.Add(p + string.Join(',', extra)) ? "before" : (p == F2 ? "corrupt" : "before"), // F2 만 이동 뒤 달라짐
        };

        var ex = Assert.Throws<ShaMismatchException>(() => Apply(Make().Resolve(20000), Exec(8 * Cb), hooks));

        Assert.Equal([F2], ex.Paths);
        Assert.Equal([F2], JournalRows()[^1]["sha_mismatch"] is List<object?> l ? l.Cast<string>().ToList() : []);
    }

    [Fact]
    public void PlannedRowsAreWrittenAheadInBatchesSoFlushesStayFarBelowOnePerTask()
    {
        PlanFile plan = ManyTasks();

        ApplyReport rep = Apply(plan, Exec(8 * Cb) with { PlanAhead = 8 });

        long tasks = rep.TasksTodo;
        Assert.Empty(rep.Failed);
        Assert.True(tasks > 16);
        int planBatches = (int)((tasks + 8 - 1) / 8);
        Assert.True(rep.Timings.JournalFlushes >= planBatches);
        Assert.True(rep.Timings.JournalFlushes <= planBatches + ((3 * tasks) / Journal.BatchRows) + 2, $"flushes {rep.Timings.JournalFlushes} tasks {tasks}"); // 행 수에 따른 묶음 + 끝
        Assert.True(rep.Timings.JournalFlushes < tasks / 2);
        // 모든 planned 행은 첫 이동보다 앞서 있음 (최초 묶음)
        List<JObject> rows = JournalRows();
        int firstDone = rows.FindIndex(r => Equals(r["type"], "step") && Equals(r["state"], "done"));
        Assert.True(rows.Take(firstDone).Count(r => Equals(r["type"], "step") && Equals(r["state"], "planned")) >= Math.Min(tasks, 8));
    }

    [Fact]
    public void StopInTheMiddleOfAPlannedBatchResumesByExtentsAndRevertsEverything()
    {
        PlanFile plan = ManyTasks();
        using var cts = new CancellationTokenSource();
        Vol.AfterMove = n =>
        {
            if (n == 5)
            {
                cts.Cancel();
            }
        };
        ApplyReport r1 = Applier.Apply(plan, Vol, [Root], JournalPath, Hooks(), Exec(8 * Cb) with { PlanAhead = 8 }, cts.Token);
        Vol.AfterMove = null;
        Assert.Equal("cancelled", r1.Stopped);
        int moved = Vol.Moves.Count;
        List<JObject> rows = JournalRows();
        int planned = rows.Count(r => Equals(r["type"], "step") && Equals(r["state"], "planned"));
        Assert.Equal(8, planned); // 시작하지 않은 작업의 planned 행도 8개 묶음으로 이미 기록됨 (다음 묶음은 아직)
        Assert.True(planned > moved);

        ApplyReport r2 = Apply(plan, Exec(8 * Cb) with { PlanAhead = 8 });

        Assert.Null(r2.Stopped);
        Assert.Empty(r2.Failed);
        Assert.Equal(Vol.Moves.Count, Vol.Moves.Select(m => (m.Path, m.Vcn)).Distinct().Count()); // 어떤 조각도 두 번 옮기지 않음
        Assert.True(Verify(plan).Contiguous);
        Assert.Contains(JournalRows(), r => Equals(r["type"], "step") && Equals(r["state"], "resumed")); // 시작 안 한 작업은 이번 실행의 단계로 다시 남김
        Assert.Empty(Reverter.Revert(JournalPath, Vol, [Root], Hooks(), new ApplyOptions(Execute: true)).Skipped);
        Assert.True(SameExtents(Files, Vol.Ext));
    }

    // ---- 이동 전후 비교 범위 (verify: sample / all / off), 표본 규칙 edge64k-v3

    private (ApplyHooks Hooks, List<int> Calls) CallCounter()
    {
        var calls = new List<int>();
        ApplyHooks h = Hooks();
        return (new ApplyHooks
        {
            Extents = h.Extents,
            SkipReason = h.SkipReason,
            Sleep = h.Sleep,
            Digest = (p, size, extra) =>
            {
                calls.Add(0);
                return SampleHasher.Compute(p, size, extra, null);
            },
            EdgeDigest = (p, size, extra) =>
            {
                calls.Add(1);
                return SampleHasher.Compute(p, size, extra, null, span: 0);
            },
        }, calls);
    }

    private HashSet<long> SampledTaskIdx() => JournalRows().Where(r => Equals(r["type"], "sha")).Select(r => (long)r["idx"]!).ToHashSet();

    [Fact]
    public void SampleModeReadsOnlyTheFirstTaskOfEachFileAndEveryFourthTask()
    {
        PlanFile plan = ManyTasks();
        (ApplyHooks hooks, List<int> calls) = CallCounter();

        ApplyReport rep = Apply(plan, Exec(8 * Cb), hooks);

        Assert.Empty(rep.Failed);
        Assert.Equal(Applier.EdgeRule, JournalRows().Single(r => Equals(r["type"], "header"))["digest_rule"]);
        var expected = Enumerable.Range(0, plan.Tasks.Count)
            .Where(i => i % 4 == 0 || plan.Tasks.Select((t, k) => (t.Path, k)).First(x => x.Path == plan.Tasks[i].Path).k == i).Select(i => (long)i).ToHashSet();
        Assert.Equal(expected, SampledTaskIdx());
        Assert.True(expected.Count < plan.Tasks.Count);
        Assert.Equal(2 * expected.Count, calls.Count); // 전과 후
        Assert.All(calls, c => Assert.Equal(1, c));
        Assert.True(Verify(plan).Contiguous); // 나머지 작업도 이동 뒤 extent 가 target 과 맞아야 done
        Assert.Equal(plan.Tasks.Count, JournalRows().Count(r => Equals(r["type"], "step") && Equals(r["state"], "done")));
    }

    [Fact]
    public void AllModeReadsEveryTask()
    {
        PlanFile plan = ManyTasks();
        (ApplyHooks hooks, List<int> calls) = CallCounter();

        ApplyReport rep = Apply(plan, Exec(8 * Cb) with { Verify = VerifyMode.All }, hooks);

        Assert.Empty(rep.Failed);
        Assert.Equal(plan.Tasks.Count, SampledTaskIdx().Count);
        Assert.Equal(2 * plan.Tasks.Count, calls.Count);
    }

    [Fact]
    public void OffModeCallsNoDigestAtAll()
    {
        PlanFile plan = ManyTasks();
        (ApplyHooks hooks, List<int> calls) = CallCounter();

        ApplyReport rep = Apply(plan, Exec(8 * Cb) with { Verify = VerifyMode.Off }, hooks);

        Assert.Empty(rep.Failed);
        Assert.Empty(calls);
        Assert.Empty(SampledTaskIdx());
        Assert.Equal(0, rep.ShaChecked);
        Assert.True(Verify(plan).Contiguous);
    }

    [Fact]
    public void CancelledRunReadsNoAfterSamplesAndTheResumeVerifiesThem()
    {
        PlanFile plan = ManyTasks();
        (ApplyHooks hooks, List<int> calls) = CallCounter();
        using var cts = new CancellationTokenSource();
        Vol.AfterMove = n =>
        {
            if (n == 3)
            {
                cts.Cancel();
            }
        };

        ApplyReport r1 = Applier.Apply(plan, Vol, [Root], JournalPath, hooks, Exec(8 * Cb) with { Verify = VerifyMode.All }, cts.Token);

        Assert.Equal("cancelled", r1.Stopped);
        int beforeReads = calls.Count;
        Assert.Equal(SampledTaskIdx().Count, beforeReads); // 이동 전 읽기만 있고 취소 뒤 after 읽기는 0
        Assert.Equal(0, r1.ShaChecked);
        Assert.Equal(true, JournalRows().Last(r => Equals(r["type"], "end"))["after_deferred"]);

        Vol.AfterMove = null;
        calls.Clear();
        ApplyReport r2 = Applier.Apply(plan, Vol, [Root], JournalPath, hooks, Exec(8 * Cb) with { Verify = VerifyMode.All }, CancellationToken.None);

        Assert.Null(r2.Stopped);
        Assert.True(calls.Count >= SampledTaskIdx().Count); // 이어하기가 앞 실행이 미룬 작업까지 after 검증
    }

    [Fact]
    public void SampleModeStillDetectsChangedContentOfASampledTask()
    {
        var seen = new HashSet<string>();
        ApplyHooks h = Hooks();
        var hooks = new ApplyHooks
        {
            Extents = h.Extents,
            SkipReason = h.SkipReason,
            Sleep = h.Sleep,
            Digest = (p, size, extra) => "legacy",
            EdgeDigest = (p, size, extra) => seen.Add(p + string.Join(',', extra)) ? "before" : "after",
        };

        var ex = Assert.Throws<ShaMismatchException>(() => Apply(ManyTasks(), Exec(8 * Cb), hooks));

        Assert.NotEmpty(ex.Paths);
    }

    [Fact]
    public void JournalWithRuleV2KeepsReadingEveryTaskEvenInSampleMode()
    {
        PlanFile plan = ManyTasks();
        using (var jr = new Journal(JournalPath))
        {
            jr.Add(new JObject { { "type", "header" }, { "plan_sha", plan.Sha() }, { "zone_start_lcn", plan.Zone.StartLcn }, { "cluster_bytes", Cb }, { "started", 1.0 }, { "digest_rule", Applier.EdgeTaskRule } });
        }

        ApplyReport rep = Apply(plan, Exec(8 * Cb));

        Assert.Empty(rep.Failed);
        Assert.Equal(plan.Tasks.Count, SampledTaskIdx().Count);
        Assert.Equal(Applier.EdgeTaskRule, JournalRows().Single(r => Equals(r["type"], "header"))["digest_rule"]);
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
