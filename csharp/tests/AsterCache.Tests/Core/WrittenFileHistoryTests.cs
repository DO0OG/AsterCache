using AsterCache.Core.Trace;
using Xunit;

namespace AsterCache.Tests.Core;

/// 게임이 쓰는 파일의 플레이별 크기 기록: 기록이 모자란 파일·줄어든 적이 있는 파일·조건을 채운 파일의 판정, 기록 수 상한, 저장 파일 왕복과 읽을 수 없는 파일을 시험합니다.
public sealed class WrittenFileHistoryTests : IDisposable
{
    private const long Mib = 1L << 20;
    private const string A = @"D:\Games\xpak_cache\cache_0_2.xpakdata";
    private const string B = @"D:\Games\xpak_cache\cache_1_2.xpakdata";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_written_" + Guid.NewGuid().ToString("N"));

    public WrittenFileHistoryTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static void Play(WrittenFileHistory h, string session, string path, long sizeMib) => h.Record(session, [(path, sizeMib * Mib, 7)], 16 * Mib);

    [Fact]
    public void AFileNeedsAtLeastThreeRecordedPlaysAndANeverRecordedFileHasNoHistory()
    {
        var h = new WrittenFileHistory();
        Assert.Equal(WrittenVerdict.InsufficientHistory, h.Judge(A, 100 * Mib));

        Play(h, "s1", A, 100);
        Play(h, "s2", A, 100);
        Assert.Equal(WrittenVerdict.InsufficientHistory, h.Judge(A, 100 * Mib));

        Play(h, "s3", A, 120);
        Assert.Equal(WrittenVerdict.Ok, h.Judge(A, 120 * Mib));
        Assert.Equal(WrittenVerdict.Ok, h.Judge(A, 130 * Mib)); // 늘어난 것은 괜찮음
        Assert.Equal(WrittenVerdict.InsufficientHistory, h.Judge(B, 100 * Mib)); // 다른 파일은 기록이 없음
    }

    [Fact]
    public void AFileThatEverShrankStaysExcludedEvenAfterItGrewAndTheOldSessionsWereDropped()
    {
        var h = new WrittenFileHistory();
        Play(h, "s1", A, 100);
        Play(h, "s2", A, 90); // 줄어듦
        for (int i = 3; i < 3 + (2 * WrittenFileHistory.MaxSessionsPerFile); i++)
        {
            Play(h, "s" + i, A, 200 + i); // 그 뒤 계속 커지고, 줄어든 플레이는 기록에서 밀려남
        }

        Assert.DoesNotContain(h.Files.Single().Sessions, s => s.Session == "s2");
        Assert.Equal(WrittenFileHistory.MaxSessionsPerFile, h.Files.Single().Sessions.Count);
        Assert.Equal(WrittenVerdict.Shrank, h.Judge(A, 10_000 * Mib));
    }

    [Fact]
    public void ACurrentSizeBelowTheLastRecordedSizeCountsAsShrunkAndAMissingFileRecordedAsZeroIsShrunk()
    {
        var h = new WrittenFileHistory();
        Play(h, "s1", A, 100);
        Play(h, "s2", A, 100);
        Play(h, "s3", A, 100);
        Assert.Equal(WrittenVerdict.Shrank, h.Judge(A, 99 * Mib)); // 마지막 기록 뒤에 줄어듦

        Play(h, "s4", B, 100);
        Play(h, "s5", B, 100);
        Play(h, "s6", B, 100);
        h.Record("s7", [(B, 0, 0)], 16 * Mib); // 지워짐
        Play(h, "s8", B, 100);
        Assert.Equal(WrittenVerdict.Shrank, h.Judge(B, 100 * Mib));
    }

    [Fact]
    public void RecordingTheSamePlayAgainReplacesItsLineAndSmallUntrackedFilesAreSkipped()
    {
        var h = new WrittenFileHistory();
        Play(h, "s1", A, 100);
        Play(h, "s1", A, 150);
        WrittenFile file = h.Files.Single();
        Assert.Equal([new WrittenSession("s1", 150 * Mib, 7)], file.Sessions);
        Assert.False(file.Shrank); // 같은 플레이를 다시 더해도 줄어든 것으로 세지 않음

        h.Record("s2", [(B, 1 * Mib, 7)], 16 * Mib); // 기록한 적 없는 작은 파일은 건너뜀
        Assert.DoesNotContain(h.Files, f => f.Path == B);
        Play(h, "s2", A, 160);
        Play(h, "s3", A, 4); // 기록한 파일은 작아져도 기록 (줄어든 표시를 남기려고)
        Assert.True(h.Files.Single(f => f.Path == A).Shrank);
    }

    [Fact]
    public void TheFileCountIsCappedKeepingTheMostRecentlyRecordedFiles()
    {
        var h = new WrittenFileHistory();
        for (int i = 0; i < WrittenFileHistory.MaxFiles + 5; i++)
        {
            Play(h, "s1", $@"D:\Games\f{i}.bin", 100);
        }

        Assert.Equal(WrittenFileHistory.MaxFiles, h.Files.Count);
        Assert.DoesNotContain(h.Files, f => f.Path == @"D:\Games\f0.bin");
        Assert.Contains(h.Files, f => f.Path == $@"D:\Games\f{WrittenFileHistory.MaxFiles + 4}.bin");
    }

    [Fact]
    public void TheHistorySurvivesAWriteAndReadAndBadOrMissingFilesReadAsEmpty()
    {
        string path = Path.Combine(_dir, "g.trace.json.written.json");
        var h = new WrittenFileHistory();
        Play(h, "s1", A, 100);
        Play(h, "s2", A, 110);
        Play(h, "s3", A, 120);
        Play(h, "s4", B, 50);
        Play(h, "s5", B, 40); // 줄어듦
        h.Write(path);

        WrittenFileHistory back = WrittenFileHistory.Read(path);

        Assert.Equal(WrittenVerdict.Ok, back.Judge(A, 120 * Mib));
        Assert.Equal(WrittenVerdict.Shrank, back.Judge(B, 40 * Mib));
        Assert.Equal(h.Files.Select(f => (f.Path, f.Shrank, f.Sessions.Count)), back.Files.Select(f => (f.Path, f.Shrank, f.Sessions.Count)));
        Assert.False(File.Exists(path + ".tmp"));

        Assert.Empty(WrittenFileHistory.Read(Path.Combine(_dir, "none.json")).Files); // 없음
        File.WriteAllText(path, "not json");
        Assert.Empty(WrittenFileHistory.Read(path).Files); // 깨짐
        File.WriteAllText(path, """{"version": 9, "files": []}""");
        Assert.Empty(WrittenFileHistory.Read(path).Files); // 다른 판
        File.WriteAllText(path, """{"version": 1, "files": [{"path": "x", "shrank": false, "sessions": [["s", -1, 0]]}]}""");
        Assert.Empty(WrittenFileHistory.Read(path).Files); // 모양이 다름
    }

    // ---- 변경 저널 판정 ----

    private static readonly DateTime T0 = new(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc);
    private const ulong FrnA = 0x0005_0000_0000_1234;

    // 플레이 n (1 부터): T0 + (n * 10) 시 ~ +1 시간
    private static SessionSpan Play(int n) => new("p" + n, T0.AddHours(n * 10), T0.AddHours((n * 10) + 1));

    private static UsnEvent Ev(int play, uint reason, ulong frn = FrnA) => new(frn, Play(play).StartUtc.AddMinutes(5), reason);

    private static WrittenFileHistory WithOneSizeRecord()
    {
        var h = new WrittenFileHistory();
        Play(h, "s1", A, 100);
        return h;
    }

    private static int Journal(WrittenFileHistory h, UsnRead? read, params SessionSpan[] sessions) => h.RecordJournal(read, [(A, FrnA)], sessions);

    [Fact]
    public void ThreeCoveredPlaysWithOnlyExtensionsAndOverwritesAreEnoughRightAfterInstallEvenWithOneSizeRecord()
    {
        WrittenFileHistory h = WithOneSizeRecord();
        Assert.Equal(WrittenVerdict.InsufficientHistory, h.Judge(A, 100 * Mib));

        int added = Journal(h, new UsnRead(T0, [Ev(1, UsnReasons.DataExtend), Ev(2, UsnReasons.DataOverwrite | UsnReasons.DataExtend)]), Play(1), Play(2), Play(3)); // 셋째 플레이는 기록 없음 (바뀌지 않음)

        Assert.Equal(3, added);
        Assert.Equal(WrittenVerdict.Ok, h.Judge(A, 100 * Mib));
        Assert.True(h.IsAppendOnly(A, 100 * Mib));
        Assert.Equal(['e', 'e', 'u'], h.Files.Single().Journal!.Select(j => j.Verdict));
    }

    [Fact]
    public void ATruncationInAnyPlayExcludesTheFilePermanentlyAndOneInTheGapBetweenPlaysCountsToo()
    {
        WrittenFileHistory h = WithOneSizeRecord();
        // 셋째 플레이 직전 (둘째 플레이가 끝난 뒤 ~ 셋째 시작 사이) 의 잘라내기
        var gap = new UsnEvent(FrnA, Play(2).EndUtc.AddMinutes(30), UsnReasons.DataTruncation | UsnReasons.DataExtend);

        _ = Journal(h, new UsnRead(T0, [Ev(1, UsnReasons.DataExtend), Ev(2, UsnReasons.DataExtend), gap]), Play(1), Play(2), Play(3));

        Assert.Equal(WrittenVerdict.Shrank, h.Judge(A, 100 * Mib));
        Assert.False(h.IsAppendOnly(A, 100 * Mib));
        Assert.True(h.Files.Single().Shrank);
        _ = Journal(h, new UsnRead(T0, []), Play(4), Play(5), Play(6), Play(7)); // 그 뒤 잘라내기가 없어도 계속 제외
        Assert.Equal(WrittenVerdict.Shrank, h.Judge(A, 100 * Mib));
    }

    [Fact]
    public void FewerThanThreeCoveredPlaysAreNotEnoughAndPlaysBeforeTheOldestRecordAreNotCounted()
    {
        WrittenFileHistory h = WithOneSizeRecord();

        // 저널이 둘째 플레이 도중부터 남음: 첫 플레이는 덮지 못해 세지 않고, 둘째는 시작이 저널보다 앞이라 세지 않음
        int added = Journal(h, new UsnRead(Play(2).StartUtc.AddMinutes(1), [Ev(2, UsnReasons.DataExtend)]), Play(1), Play(2), Play(3));

        Assert.Equal(1, added);
        Assert.Equal(["p3"], h.Files.Single().Journal!.Select(j => j.Session));
        Assert.Equal(WrittenVerdict.InsufficientHistory, h.Judge(A, 100 * Mib)); // 저널 1 + 크기 기록 1
    }

    [Fact]
    public void AReplacementKeepsTheHoldPossibleButTheRankingDoesNotTreatTheFileAsAppendOnly()
    {
        WrittenFileHistory h = WithOneSizeRecord();

        _ = Journal(h, new UsnRead(T0, [Ev(1, UsnReasons.DataExtend), Ev(2, UsnReasons.FileCreate | UsnReasons.RenameNewName), Ev(3, UsnReasons.DataExtend)]), Play(1), Play(2), Play(3));

        Assert.Equal(WrittenVerdict.Ok, h.Judge(A, 100 * Mib));
        Assert.False(h.IsAppendOnly(A, 100 * Mib));
        Assert.Equal(['e', 'r', 'e'], h.Files.Single().Journal!.Select(j => j.Verdict));
    }

    [Fact]
    public void AnUnreadableJournalOrAnUnknownFileReferenceChangesNothingSoTheSizeHistoryDecides()
    {
        WrittenFileHistory h = WithOneSizeRecord();

        Assert.Equal(0, Journal(h, null, Play(1), Play(2), Play(3))); // 저널을 읽지 못함
        Assert.Equal(0, h.RecordJournal(new UsnRead(T0, []), [(A, null)], [Play(1), Play(2), Play(3)])); // 파일 참조 번호를 모름
        Assert.Empty(h.Files.Single().Journal ?? []);
        Assert.Equal(WrittenVerdict.InsufficientHistory, h.Judge(A, 100 * Mib));
        Play(h, "s2", A, 100);
        Play(h, "s3", A, 100);
        Assert.Equal(WrittenVerdict.Ok, h.Judge(A, 100 * Mib)); // 크기 기록 세 플레이
    }

    [Fact]
    public void AChangedFileReferenceMarksTheLatestPlayAsReplacedAndAlreadyJudgedPlaysAreNotRedone()
    {
        WrittenFileHistory h = WithOneSizeRecord();
        _ = Journal(h, new UsnRead(T0, []), Play(1), Play(2), Play(3));
        Assert.Equal(0, Journal(h, new UsnRead(T0, [Ev(1, UsnReasons.DataTruncation)]), Play(1), Play(2), Play(3))); // 이미 판정한 플레이는 건너뜀
        Assert.False(h.Files.Single().Shrank);

        int added = h.RecordJournal(new UsnRead(T0, []), [(A, FrnA + 1)], [Play(1), Play(2), Play(3), Play(4)]); // 다른 번호의 파일이 끼워짐

        Assert.Equal(1, added);
        Assert.Equal('r', h.Files.Single().Journal![^1].Verdict);
        Assert.False(h.IsAppendOnly(A, 100 * Mib));
    }

    [Fact]
    public void JournalFieldsSurviveAWriteAndReadAndAnOldFileWithoutThemStillReads()
    {
        string path = Path.Combine(_dir, "g.trace.json.written.json");
        WrittenFileHistory h = WithOneSizeRecord();
        _ = Journal(h, new UsnRead(T0, [Ev(1, UsnReasons.DataExtend)]), Play(1), Play(2), Play(3));
        h.Write(path);

        WrittenFile back = Assert.Single(WrittenFileHistory.Read(path).Files);

        Assert.Equal(unchecked((long)FrnA), back.Frn);
        Assert.Equal(['e', 'u', 'u'], back.Journal!.Select(j => j.Verdict));

        File.WriteAllText(path, """{"version": 1, "files": [{"path": "x", "shrank": false, "sessions": [["s", 5, 0]]}]}""");
        WrittenFile old = Assert.Single(WrittenFileHistory.Read(path).Files);
        Assert.Equal(0, old.Frn);
        Assert.Empty(old.Journal ?? []);
        File.WriteAllText(path, """{"version": 1, "files": [{"path": "x", "shrank": false, "sessions": [], "frn": 1, "journal": [["p", "z"]]}]}""");
        Assert.Empty(WrittenFileHistory.Read(path).Files); // 모르는 판정 글자
    }
}
