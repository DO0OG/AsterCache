using System.Diagnostics;
using AsterCache.Core.Model;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Profiles;
using AsterCache.Prefetch;
using AsterCache.Tests.Windows;
using Xunit;

namespace AsterCache.Tests.Prefetch;

/// 기록 없는 게임의 예열 계획 (통째 / 엔진 프로필) 과 묶음 읽기. 계획은 tests/fixtures/engines 의 가짜 설치 폴더와 임시 폴더의 실제 열거로, 읽기는 가짜 읽기 함수로 확인합니다.
public sealed class FolderWarmTests
{
    private const long Mib = 1L << 20;
    private static readonly EngineProfiles Profiles = EngineProfiles.Builtin;

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "engines", name);

    private static string[] Rel(string root, ColdPlan plan) => [.. plan.Ops.Select(o => Path.GetRelativePath(root, o.Path).Replace('\\', '/'))];

    private static long TotalSize(string root) => new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);

    [Fact]
    public void SmallFolderIsWarmedWholeInPhysicalOrderAndUnknownLayoutGoesLast()
    {
        string root = Fixture("unity");
        string[] byPath = [.. new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories).Where(f => f.Length > 0).Select(f => f.FullName).Order(StringComparer.Ordinal)];
        string unknown = byPath.First(p => p.EndsWith("notes.txt", StringComparison.Ordinal));
        Func<string, FileLayoutInfo?> layout = p => p == unknown
            ? null
            : new FileLayoutInfo([new Extent(0, 1_000_000 - Array.IndexOf(byPath, p) * 1000L, (new FileInfo(p).Length + 4095) / 4096)], 4096); // 경로 역순이 디스크 앞쪽

        ColdPlan plan = FolderWarm.Plan(root, ["Game.exe"], Mib, Mib, Profiles, layout: layout);

        Assert.Equal("full", plan.Label);
        Assert.Null(plan.Skip);
        Assert.Equal([.. byPath.Where(p => p != unknown).Reverse(), unknown], plan.Ops.Select(o => o.Path)); // 물리 순서 (작은 파일부터 규칙은 대체됨), 배치를 모르는 파일은 맨 뒤
        Assert.Equal(TotalSize(root), plan.Bytes);
        Assert.All(plan.Ops, o => Assert.Equal(0, o.Offset));
        Assert.All(plan.Ops, o => Assert.Equal(new FileInfo(o.Path).Length, o.Length)); // 파일 전체
    }

    [Fact]
    public void RealLayoutKeepsEveryFileOnceAndStopsWhenCancelled()
    {
        string root = Fixture("unity");

        ColdPlan plan = FolderWarm.Plan(root, ["Game.exe"], Mib, Mib, Profiles);

        Assert.Equal("full", plan.Label);
        Assert.Equal(TotalSize(root), plan.Bytes);
        Assert.Equal(plan.Ops.Count, plan.Ops.Select(o => o.Path).Distinct().Count());
        Assert.All(plan.Ops, o => Assert.Equal(new FileInfo(o.Path).Length, o.Length));
        using var cts = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => FolderWarm.Plan(root, ["Game.exe"], Mib, Mib, Profiles, cts.Token, p =>
        {
            cts.Cancel(); // 파일별 배치 조회 사이에서 취소를 봄
            return null;
        }));
    }

    [Fact]
    public void FolderOverTheLimitUsesTheProfileInstead()
    {
        string root = Fixture("unity");
        long total = TotalSize(root);

        ColdPlan atLimit = FolderWarm.Plan(root, ["Game.exe"], total, Mib, Profiles);
        ColdPlan over = FolderWarm.Plan(root, ["Game.exe"], total - 1, Mib, Profiles);

        Assert.Equal("full", atLimit.Label); // 상한과 같으면 통째
        Assert.Equal("profile Unity", over.Label);
        Assert.True(over.Bytes < total);
    }

    [Fact]
    public void FolderOverTheLimitWithProfilesOffIsSkipped()
    {
        ColdPlan plan = FolderWarm.Plan(Fixture("unity"), ["Game.exe"], 10, 0, Profiles);

        Assert.Contains("larger", plan.Skip, StringComparison.Ordinal);
        Assert.Empty(plan.Ops);
    }

    [Fact]
    public void FullPrewarmOffGoesStraightToTheProfile()
    {
        ColdPlan plan = FolderWarm.Plan(Fixture("unity"), ["Game.exe"], 0, Mib, Profiles);

        Assert.Equal("profile Unity", plan.Label);
    }

    [Fact]
    public void UnityProfilePicksTheLoadingSetOnly()
    {
        string root = Fixture("unity");

        ColdPlan plan = FolderWarm.Plan(root, ["Game.exe"], 0, 100 * Mib, Profiles);

        string[] rel = Rel(root, plan);
        Assert.Equal(
            ["Helper.dll", "UnityPlayer.dll", // 공통 규칙: exe 와 같은 폴더의 *.dll
                "Game_Data/globalgamemanagers", "Game_Data/globalgamemanagers.assets", "Game_Data/resources.assets",
                "Game_Data/sharedassets0.assets", "Game_Data/sharedassets0.assets.resS", "Game_Data/level0",
                "Game_Data/Managed/Assembly-CSharp.dll", "Game_Data/Managed/UnityEngine.dll", "Game_Data/il2cpp_data/Metadata/global-metadata.dat",
                "MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll"],
            rel);
        Assert.DoesNotContain(rel, r => r.Contains("movie", StringComparison.Ordinal) || r.EndsWith("level1", StringComparison.Ordinal) || r.EndsWith("sharedassets1.assets", StringComparison.Ordinal) || r == "notes.txt");
    }

    [Fact]
    public void UnrealProfileTakesChunk0WholeAndTheRestAsAHead()
    {
        string root = Fixture("unreal");

        ColdPlan plan = FolderWarm.Plan(root, ["MyGame-Win64-Shipping.exe"], 0, 100 * Mib, Profiles);

        Assert.Equal("profile Unreal", plan.Label);
        string[] rel = Rel(root, plan);
        Assert.Contains("MyGame/Content/Paks/pakchunk0-WindowsNoEditor.pak", rel);
        Assert.Contains("MyGame/Content/Paks/pakchunk0-WindowsNoEditor.utoc", rel);
        Assert.Contains("MyGame/Content/Paks/pakchunk0-WindowsNoEditor.ucas", rel);
        Assert.Contains("MyGame/Content/Paks/pakchunk1-WindowsNoEditor.pak", rel); // 앞 64 MiB (작은 파일이라 전체)
        Assert.Contains("MyGame/Binaries/Win64/steam_api64.dll", rel);
        Assert.DoesNotContain(rel, r => r.Contains("intro", StringComparison.Ordinal) || r.EndsWith(".exe", StringComparison.Ordinal) || r.EndsWith("readme.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void SourceProfilePicksDirVpksAndBinDlls()
    {
        string root = Fixture("source");

        ColdPlan plan = FolderWarm.Plan(root, ["game.exe"], 0, 100 * Mib, Profiles);

        Assert.Equal("profile Source", plan.Label);
        Assert.Equal(
            new[] { "hl2/hl2_pak_dir.vpk", "portal/portal_pak_dir.vpk", "bin/client.dll", "bin/engine.dll" }.Order(StringComparer.Ordinal),
            Rel(root, plan).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void FolderWithoutAKnownEngineIsSkipped()
    {
        ColdPlan plan = FolderWarm.Plan(Fixture("plain"), ["Game.exe"], 0, 100 * Mib, Profiles);

        Assert.Equal("no engine profile matched", plan.Skip);
        Assert.Empty(plan.Ops);
    }

    [Fact]
    public void SeveralEnginesAreMergedIntoOnePlan()
    {
        using var dir = new TempDir();
        CopyTree(Fixture("unity"), dir.Path);
        CopyTree(Fixture("source"), dir.Path);

        ColdPlan plan = FolderWarm.Plan(dir.Path, ["Game.exe"], 0, 100 * Mib, Profiles);

        Assert.Equal("profile Unity+Source", plan.Label);
        string[] rel = Rel(dir.Path, plan);
        Assert.Contains("Game_Data/globalgamemanagers", rel);
        Assert.Contains("hl2/hl2_pak_dir.vpk", rel);
        Assert.Contains("bin/engine.dll", rel);
    }

    [Fact]
    public void ProfileTotalIsCutToTheBudgetFromTheFront()
    {
        string root = Fixture("unity");

        ColdPlan plan = FolderWarm.Plan(root, ["Game.exe"], 0, 70, Profiles);

        Assert.Equal(70, plan.Bytes);
        string[] rel = Rel(root, plan);
        // 공통 규칙 (Helper 3 B, UnityPlayer 40 B) → globalgamemanagers 20 B (7 B 남음) → .assets 는 7 B 만
        Assert.Equal(["Helper.dll", "UnityPlayer.dll", "Game_Data/globalgamemanagers", "Game_Data/globalgamemanagers.assets"], rel);
        Assert.Equal(7, plan.Ops[^1].Length); // 마지막 파일은 남은 만큼만 앞쪽부터
    }

    [Fact]
    public void HeadAndTailMibLimitEachOtherPakToItsEndsAndUtocIsWhole()
    {
        using var dir = new TempDir();
        string paks = Path.Combine(dir.Path, "G", "Content", "Paks");
        Directory.CreateDirectory(paks);
        Directory.CreateDirectory(Path.Combine(dir.Path, "G", "Binaries", "Win64"));
        foreach ((string name, long size) in new[] { ("pakchunk0-Win.pak", 100 * Mib), ("pakchunk1-Win.pak", 300 * Mib), ("pakchunk2-Win.pak", 10 * Mib), ("pakchunk3-Win.pak", 120 * Mib), ("other.utoc", 70 * Mib), ("other.ucas", 100 * Mib) })
        {
            using FileStream fs = File.Create(Path.Combine(paks, name));
            fs.SetLength(size);
        }

        ColdPlan plan = FolderWarm.Plan(dir.Path, ["G.exe"], 0, 10_000 * Mib, Profiles);

        List<(long Offset, long Length)> Of(string name) => [.. plan.Ops.Where(o => Path.GetFileName(o.Path) == name).Select(o => (o.Offset, o.Length)).OrderBy(x => x.Offset)];
        Assert.Equal([(0L, 100 * Mib)], Of("pakchunk0-Win.pak"));
        Assert.Equal([(0L, 64 * Mib), (236 * Mib, 64 * Mib)], Of("pakchunk1-Win.pak")); // 앞 64 + 끝 64 MiB (인덱스·푸터)
        Assert.Equal([(0L, 10 * Mib)], Of("pakchunk2-Win.pak"));
        Assert.Equal([(0L, 120 * Mib)], Of("pakchunk3-Win.pak")); // 앞+뒤 (128) 이하면 겹치지 않게 한 번
        Assert.Equal([(0L, 70 * Mib)], Of("other.utoc")); // .utoc 는 전체
        Assert.Equal([(0L, 64 * Mib)], Of("other.ucas")); // .ucas 는 앞 64 MiB 만
    }

    [Fact]
    public void CommonRuleFollowsTheFoundExeAndIsDroppedWhenItIsNotFound()
    {
        string root = Fixture("unity");

        ColdPlan withExe = FolderWarm.Plan(root, ["game.exe"], 0, 100 * Mib, Profiles); // 대소문자 무시
        ColdPlan withoutExe = FolderWarm.Plan(root, ["other.exe"], 0, 100 * Mib, Profiles);

        Assert.Contains("Helper.dll", Rel(root, withExe));
        Assert.DoesNotContain("Helper.dll", Rel(root, withoutExe));
        Assert.Contains("UnityPlayer.dll", Rel(root, withoutExe)); // 엔진 규칙은 그대로
    }

    [Fact]
    public void MissingFolderThrowsDirectoryNotFound()
    {
        Assert.Throws<DirectoryNotFoundException>(() => FolderWarm.Plan(Path.Combine(Fixture("unity"), "nope"), ["a.exe"], Mib, Mib, Profiles));
    }

    [Fact]
    public void JunctionsAreNotFollowed()
    {
        using var dir = new TempDir();
        string root = Path.Combine(dir.Path, "game");
        string outside = Path.Combine(dir.Path, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(root, "a.dat"), new byte[10]);
        File.WriteAllBytes(Path.Combine(outside, "secret.dat"), new byte[10]);
        string link = Path.Combine(root, "link");
        using (Process p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!)
        {
            p.WaitForExit();
            Assert.Equal(0, p.ExitCode);
        }

        try
        {
            ColdPlan plan = FolderWarm.Plan(root, ["g.exe"], Mib, Mib, Profiles);

            Assert.Equal(["a.dat"], Rel(root, plan));
        }
        finally
        {
            Directory.Delete(link); // 연결 지점만 지움 (재귀 삭제가 연결 대상까지 가지 않게)
        }
    }

    [Fact]
    public void ReadGroupsFilesIntoBatchesAndSumsTheStats()
    {
        ReadOp[] ops = [.. Enumerable.Range(0, 5).Select(i => new ReadOp($@"C:\g\f{i}", 0, 40 * Mib))];
        var batches = new List<int>();

        (StreamStats stats, bool complete) = FolderWarm.Read(ops, CancellationToken.None, b =>
        {
            batches.Add(b.Count);
            return new StreamStats(b.Sum(o => o.Length), b.Count, 0, 0.5);
        });

        Assert.True(complete);
        Assert.Equal([2, 2, 1], batches); // 40 + 40 MiB >= 64 MiB 에서 끊음
        Assert.Equal((200 * Mib, 5, 0), (stats.Bytes, stats.Requests, stats.Errors));
        Assert.Equal(1.5, stats.Seconds);
    }

    [Fact]
    public void CancelStopsAtTheNextFileBoundaryAndReportsIncomplete()
    {
        ReadOp[] ops = [.. Enumerable.Range(0, 5).Select(i => new ReadOp($@"C:\g\f{i}", 0, 40 * Mib))];
        using var cts = new CancellationTokenSource();
        var read = new List<string>();

        (StreamStats stats, bool complete) = FolderWarm.Read(ops, cts.Token, b =>
        {
            read.AddRange(b.Select(o => o.Path));
            cts.Cancel(); // 첫 묶음을 읽는 중 게임이 시작됨
            return new StreamStats(b.Sum(o => o.Length), b.Count, 0, 0.1);
        });

        Assert.False(complete);
        Assert.Equal([@"C:\g\f0", @"C:\g\f1"], read); // 시작한 묶음은 끝까지 (파일을 쪼개지 않음), 다음 묶음은 시작하지 않음
        Assert.Equal(80 * Mib, stats.Bytes);
    }

    [Fact]
    public void CancelledBeforeStartReadsNothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        (StreamStats stats, bool complete) = FolderWarm.Read([new ReadOp("x", 0, Mib)], cts.Token, _ => throw new InvalidOperationException("must not read"));

        Assert.False(complete);
        Assert.Equal(0, stats.Bytes);
    }

    [Fact]
    public void PlanCancelsWhileEnumerating()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => FolderWarm.Plan(Fixture("unity"), ["Game.exe"], Mib, Mib, Profiles, cts.Token));
    }

    private static void CopyTree(string from, string to)
    {
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string dest = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, true);
        }
    }
}
