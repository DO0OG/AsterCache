using AsterCache.Core.Settings;
using AsterCache.Core.Tier;
using AsterCache.Tests.Service;
using AsterCache.Tests.Windows;
using Xunit;

namespace AsterCache.Tests.Core;

/// SSD 보조 저장 엔진: 적용 → 링크로 읽기 → 되돌림 후 원본 바이트 동일, 그리고 저널 손상·사본 유실·링크 유실이 각각 안전하게 처리되는지.
/// 파일 시스템은 메모리 가짜이고 저널만 임시 폴더에 씁니다 (실제 게임 폴더·ProgramData 는 쓰지 않음).
public sealed class TierEngineTests : IDisposable
{
    private const string Root = @"C:\AsterCache\tier";
    private const string A = @"D:\Games\Foo\a.bin";
    private const string B = @"D:\Games\Foo\sub\b.bin";
    private readonly TempDir _dir = new();
    private readonly MemTierFs _fs = new();
    private readonly ListLog _log = new();
    private readonly TierEngine _engine;
    private readonly GameSettings _game = new("Foo", ["foo.exe"], @"C:\p\Foo.trace.json", @"C:\p\Foo.plan.json", @"D:\Games\Foo", [@"D:\Games"], "GAMES", 60);
    private readonly byte[] _a = Bytes(3000, 1);
    private readonly byte[] _b = Bytes(5000, 2);

    public TierEngineTests()
    {
        _engine = new TierEngine(_fs, _log, _dir.Combine("tier"), () => new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));
        _fs.Put(A, _a);
        _fs.Put(B, _b);
    }

    public void Dispose() => _dir.Dispose();

    private static byte[] Bytes(int n, int seed)
    {
        byte[] d = new byte[n];
        new Random(seed).NextBytes(d);
        return d;
    }

    private static TierSelection Sel(params (string Path, long Size)[] files) =>
        new([.. files.Select(f => new TierPick(f.Path, f.Size, f.Size))], files.Sum(f => f.Size), files.Sum(f => f.Size), files.Sum(f => f.Size), []);

    private TierApplyReport ApplyBoth(CancellationToken ct = default) => _engine.Apply(_game, Root, 8, Sel((A, _a.Length), (B, _b.Length)), ct);

    private string Dst(string path) => TierEngine.DestPath(Root, "Foo", path);

    [Fact]
    public void ApplyMakesLinksReadableThroughThemAndRevertRestoresTheOriginalBytes()
    {
        TierApplyReport rep = ApplyBoth();

        Assert.Equal((2, 8000L, null), (rep.Applied, rep.CopiedBytes, rep.Stopped));
        Assert.Equal(Dst(A), _fs.Links[MemTierFs.N(A)]);
        Assert.Equal(_a, _fs.ReadRange(A, 0, 4000)); // 링크를 통해 읽은 내용
        Assert.Equal(_a, _fs.Files[MemTierFs.N(A + TierEngine.OrigSuffix)]); // 원본은 HDD 에 남음
        Assert.Equal(_b, _fs.Files[MemTierFs.N(Dst(B))]);
        TierGameInfo info = Assert.Single(_engine.Status());
        Assert.Equal((2, 0, 0, 8000L), (info.Active, info.Stale, info.Reverted, info.ActiveBytes));

        TierRevertReport back = _engine.Revert("Foo", "test");

        Assert.Equal((2, 0, 0), (back.Reverted, back.Stale, back.Failed));
        Assert.Empty(_fs.Links);
        Assert.Equal(_a, _fs.Files[MemTierFs.N(A)]);
        Assert.Equal(_b, _fs.Files[MemTierFs.N(B)]);
        Assert.False(_fs.Files.ContainsKey(MemTierFs.N(A + TierEngine.OrigSuffix)));
        Assert.True(_fs.Files.ContainsKey(MemTierFs.N(Dst(A)))); // 사본은 남음
        Assert.Equal((0, 0, 2), (_engine.Status()[0].Active, _engine.Status()[0].Stale, _engine.Status()[0].Reverted));
    }

    [Fact]
    public void CleanDeletesOnlyRevertedCopiesAndKeepsActiveOnes()
    {
        ApplyBoth();
        _engine.Revert("Foo", "test");
        ApplyBoth(); // 같은 사본을 다시 쓰고 (되돌린 뒤 남은 사본 재사용) 새 항목을 만듦
        Assert.Equal(1, _fs.Files.Keys.Count(k => k.EndsWith(@"Foo\Games\Foo\a.bin", StringComparison.OrdinalIgnoreCase)));

        (int files, _) = _engine.Clean();

        Assert.Equal(0, files); // 같은 사본을 active 항목이 쓰는 중이라 지우지 않음
        Assert.True(_fs.Files.ContainsKey(MemTierFs.N(Dst(A))));
        _engine.Revert("Foo", "test");
        (files, long bytes) = _engine.Clean();
        Assert.Equal((2, 8000L), (files, bytes));
        Assert.False(_fs.Files.ContainsKey(MemTierFs.N(Dst(A))));
        Assert.Equal(_a, _fs.Files[MemTierFs.N(A)]); // 원본은 그대로
        Assert.False(File.Exists(_engine.JournalPath("Foo"))); // 항목이 없으면 저널도 정리
    }

    [Fact]
    public void CorruptJournalTouchesNothingAndIsLogged()
    {
        ApplyBoth();
        File.WriteAllText(_engine.JournalPath("Foo"), "{ this is not json");
        byte[] journalBefore = File.ReadAllBytes(_engine.JournalPath("Foo"));
        var filesBefore = _fs.Files.ToDictionary(kv => kv.Key, kv => kv.Value);
        var linksBefore = _fs.Links.ToDictionary(kv => kv.Key, kv => kv.Value);
        int mutations = _fs.Mutations;

        TierApplyReport rep = ApplyBoth();
        TierGameInfo? maintain = _engine.Maintain("Foo");
        TierRevertReport revert = _engine.Revert("Foo", "test");
        (int cleaned, _) = _engine.Clean();

        Assert.Equal("journal_corrupt", rep.Stopped);
        Assert.True(maintain!.Corrupt);
        Assert.True(revert.Corrupt);
        Assert.Equal(0, cleaned);
        Assert.Equal(mutations, _fs.Mutations); // 파일 시스템을 한 번도 바꾸지 않음
        Assert.Equal(filesBefore.Keys.Order(), _fs.Files.Keys.Order());
        Assert.Equal(linksBefore, _fs.Links);
        Assert.Equal(journalBefore, File.ReadAllBytes(_engine.JournalPath("Foo")));
        Assert.Contains(_log.Lines, l => l.Contains("journal unreadable") && l.StartsWith("Warn", StringComparison.Ordinal));
        Assert.True(_engine.Status()[0].Corrupt);
    }

    [Fact]
    public void JournalThatBreaksTheLayoutRulesIsTreatedAsCorrupt()
    {
        ApplyBoth();
        string json = File.ReadAllText(_engine.JournalPath("Foo")).Replace(@"a.bin.aster-orig", @"other.bin", StringComparison.Ordinal);
        File.WriteAllText(_engine.JournalPath("Foo"), json);
        int mutations = _fs.Mutations;

        Assert.True(_engine.Revert("Foo", "test").Corrupt);
        Assert.Equal(mutations, _fs.Mutations);
        Assert.Equal(2, _fs.Links.Count);
    }

    [Fact]
    public void LostCopyIsRevertedAtOnceAndTheOriginalComesBack()
    {
        ApplyBoth();
        _fs.Files.Remove(MemTierFs.N(Dst(A)));

        TierGameInfo info = _engine.Maintain("Foo")!;

        Assert.Equal((1, 0, 1), (info.Active, info.Stale, info.Reverted));
        Assert.False(_fs.Links.ContainsKey(MemTierFs.N(A)));
        Assert.Equal(_a, _fs.Files[MemTierFs.N(A)]);
        Assert.True(_fs.Links.ContainsKey(MemTierFs.N(B))); // 다른 파일은 그대로
    }

    [Fact]
    public void CopyWithADifferentSizeIsRevertedAtOnce()
    {
        ApplyBoth();
        _fs.Files[MemTierFs.N(Dst(B))] = _b[..100];

        TierGameInfo info = _engine.Maintain("Foo")!;

        Assert.Equal((1, 0, 1), (info.Active, info.Stale, info.Reverted));
        Assert.Equal(_b, _fs.Files[MemTierFs.N(B)]);
    }

    [Fact]
    public void LinkReplacedByARegularFileIsMarkedStaleAndNothingIsTouched()
    {
        ApplyBoth();
        byte[] updated = Bytes(3500, 9);
        _fs.Links.Remove(MemTierFs.N(A)); // 런처가 업데이트로 링크를 지우고 새 파일을 씀
        _fs.Put(A, updated);
        int mutations = _fs.Mutations;

        TierGameInfo info = _engine.Maintain("Foo")!;

        Assert.Equal((1, 1, 0), (info.Active, info.Stale, info.Reverted));
        Assert.Equal(mutations, _fs.Mutations);
        Assert.Equal(updated, _fs.Files[MemTierFs.N(A)]);
        Assert.Equal(_a, _fs.Files[MemTierFs.N(A + TierEngine.OrigSuffix)]);
        Assert.Equal(_a, _fs.Files[MemTierFs.N(Dst(A))]);
        Assert.Contains(_log.Lines, l => l.Contains("stale"));

        _engine.Revert("Foo", "test"); // stale 항목은 되돌려도 새 파일을 덮어쓰지 않음
        Assert.Equal(updated, _fs.Files[MemTierFs.N(A)]);
        Assert.Equal(_b, _fs.Files[MemTierFs.N(B)]);
    }

    [Fact]
    public void LinkGoneWithoutAReplacementRestoresTheOriginalName()
    {
        ApplyBoth();
        _fs.Links.Remove(MemTierFs.N(A)); // 적용 도중 멈췄거나 링크만 지워진 상태: 이름이 비어 있음

        TierGameInfo info = _engine.Maintain("Foo")!;

        Assert.Equal((1, 0, 1), (info.Active, info.Stale, info.Reverted));
        Assert.Equal(_a, _fs.Files[MemTierFs.N(A)]);
    }

    [Fact]
    public void CopyChangedThroughTheLinkIsKeptAndRevertWritesTheChangedDataBack()
    {
        ApplyBoth();
        byte[] written = Bytes(_a.Length, 7); // 같은 크기, 다른 내용: 링크를 통해 쓴 것
        _fs.Put(Dst(A), written);

        TierGameInfo info = _engine.Maintain("Foo")!;

        Assert.Equal((1, 1), (info.Active, info.Stale));
        Assert.True(_fs.Links.ContainsKey(MemTierFs.N(A))); // 링크는 그대로 (데이터를 잃지 않음)

        _engine.Revert("Foo", "test");

        Assert.False(_fs.Links.ContainsKey(MemTierFs.N(A)));
        Assert.Equal(written, _fs.Files[MemTierFs.N(A)]); // 바뀐 데이터가 일반 파일로
        Assert.Equal(_a, _fs.Files[MemTierFs.N(A + TierEngine.OrigSuffix)]); // 옛 원본은 지우지 않음
    }

    [Fact]
    public void FailedLinkRollsBackThatFileAndLeavesNoJournalEntry()
    {
        _fs.SymlinkDenied = true;

        TierApplyReport rep = ApplyBoth();

        Assert.Equal(0, rep.Applied);
        Assert.Equal(2, rep.Skipped.Count);
        Assert.Contains("UnauthorizedAccessException", rep.Skipped[0].Reason);
        Assert.Equal(_a, _fs.Files[MemTierFs.N(A)]);
        Assert.False(_fs.Files.ContainsKey(MemTierFs.N(A + TierEngine.OrigSuffix)));
        Assert.Empty(_fs.Links);
        Assert.DoesNotContain(_engine.Status(), s => s.Active > 0);
        Assert.False(File.Exists(_engine.JournalPath("Foo")));
    }

    [Fact]
    public void SkipsFilesWithReparsePointsTakenOriginalNamesOutsideRootsAndExecutables()
    {
        _fs.Reparse.Add(MemTierFs.N(@"D:\Games\Foo\sub")); // B 의 위쪽 폴더가 정션
        _fs.Put(A + TierEngine.OrigSuffix, new byte[1]); // A 의 원본 이름이 이미 쓰임
        _fs.Put(@"E:\Other\c.bin", new byte[10]);
        _fs.Put(@"D:\Games\Foo\game.exe", new byte[10]);
        int mutations = _fs.Mutations;

        TierApplyReport rep = _engine.Apply(_game, Root, 8, Sel((A, _a.Length), (B, _b.Length), (@"E:\Other\c.bin", 10), (@"D:\Games\Foo\game.exe", 10)), default);

        Assert.Equal(0, rep.Applied);
        Assert.Equal(["orig_name_taken", "reparse_point", "outside_allowed_root", "excluded_type"], rep.Skipped.Select(s => s.Reason));
        Assert.Equal(mutations, _fs.Mutations);
        Assert.False(File.Exists(_engine.JournalPath("Foo"))); // 아무것도 적용하지 않았으면 저널도 만들지 않음
    }

    [Fact]
    public void SkipsWhenTheCopyFolderHasAReparsePointPlantedInIt()
    {
        _fs.Reparse.Add(MemTierFs.N(@"C:\AsterCache\tier\Foo"));
        int mutations = _fs.Mutations;

        TierApplyReport rep = ApplyBoth();

        Assert.Equal(0, rep.Applied);
        Assert.All(rep.Skipped, s => Assert.Equal("target_reparse_point", s.Reason));
        Assert.Equal(mutations, _fs.Mutations);
    }

    [Fact]
    public void LowFreeSpaceStopsBeforeCopying()
    {
        _fs.Free = TierEngine.MinFreeFor(_fs.Total) + 4000; // 첫 파일 (3000) 만 들어감

        TierApplyReport rep = ApplyBoth();

        Assert.Equal((1, "ssd_free_low"), (rep.Applied, rep.Stopped));
        Assert.Equal(_b, _fs.Files[MemTierFs.N(B)]);
    }

    [Theory]
    [InlineData(256_000_000_000L, 16L << 30)] // 작은 SSD: 16 GiB
    [InlineData(2_000_000_000_000L, 40_000_000_000L)] // 2 TB: 용량의 2%
    public void MinFreeIsTheLargerOf16GiBAndTwoPercentOfTheVolume(long total, long expected)
    {
        Assert.Equal(expected, TierEngine.MinFreeFor(total));
    }

    [Fact]
    public void CancelDuringACopyAbortsThatFileAndLeavesNoPartFile()
    {
        using var cts = new CancellationTokenSource();
        _fs.AfterCopy = (_, dst) =>
        {
            if (dst.Contains("b.bin", StringComparison.Ordinal))
            {
                cts.Cancel(); // 둘째 파일을 복사하는 도중 게임이 시작됨
            }
        };

        TierApplyReport rep = ApplyBoth(cts.Token);

        Assert.Equal((1, "cancelled"), (rep.Applied, rep.Stopped));
        Assert.True(_fs.Links.ContainsKey(MemTierFs.N(A))); // 앞 파일은 끝까지
        Assert.False(_fs.Links.ContainsKey(MemTierFs.N(B)));
        Assert.Equal(_b, _fs.Files[MemTierFs.N(B)]);
        Assert.DoesNotContain(_fs.Files.Keys, k => k.EndsWith(TierEngine.PartSuffix, StringComparison.OrdinalIgnoreCase));
        Assert.Empty(rep.Skipped);
    }

    [Fact]
    public void CopyThatDoesNotMatchIsNotLinked()
    {
        _fs.AfterCopy = (_, dst) => _fs.Files[MemTierFs.N(dst)] = Bytes(_a.Length, 99); // 복사 도중 깨짐

        TierApplyReport rep = _engine.Apply(_game, Root, 8, Sel((A, _a.Length)), default);

        Assert.Equal("verify_mismatch", Assert.Single(rep.Skipped).Reason);
        Assert.Empty(_fs.Links);
        Assert.Equal(_a, _fs.Files[MemTierFs.N(A)]);
        Assert.False(_fs.Files.ContainsKey(MemTierFs.N(Dst(A) + TierEngine.PartSuffix))); // 임시 사본도 치움
    }

    [Fact]
    public void ApplyWithAnotherRootWhileLinksExistIsRefusedAndOldestActiveIsFound()
    {
        ApplyBoth();

        Assert.Equal("root_changed", _engine.Apply(_game, @"E:\Tier", 8, Sel((A, _a.Length)), default).Stopped);
        Assert.Equal("Foo", _engine.OldestActive(Root));
        Assert.Null(_engine.OldestActive(@"E:\Tier"));
        Assert.True(TierEngine.HasActive(_engine.JournalDir, "Foo"));
        Assert.False(TierEngine.HasActive(_engine.JournalDir, "Nope"));
    }
}
