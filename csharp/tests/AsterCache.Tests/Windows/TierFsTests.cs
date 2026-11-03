using AsterCache.Core.Settings;
using AsterCache.Core.Tier;
using AsterCache.Tests.Service;
using AsterCache.Windows;
using Xunit;
using Xunit.Abstractions;

namespace AsterCache.Tests.Windows;

/// 실제 파일 시스템의 SSD 보조 저장: 복사·통계·읽기는 항상, 심볼릭 링크 왕복은 링크를 만들 권한이 있을 때만 (없으면 그 사유를 출력하고 통과).
/// 모든 파일은 임시 폴더 안입니다 (게임 폴더·ProgramData 는 쓰지 않음).
public sealed class TierFsTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void CopyKeepsTheBytesTheModifiedTimeAndCreatesTheFolder()
    {
        byte[] data = _dir.WriteRandom("a.bin", (3 << 20) + 17);
        File.SetLastWriteTimeUtc(_dir.Combine("a.bin"), new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var fs = new TierFs(new IoPriorityHint(_ => { }));
        string dst = Path.Combine(_dir.Path, "deep", "er", "a.bin");

        fs.Copy(_dir.Combine("a.bin"), dst);

        Assert.Equal(data, File.ReadAllBytes(dst));
        FileStamp s = fs.Stat(dst)!.Value;
        Assert.Equal((data.Length, new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc).Ticks), (s.Length, s.MtimeTicks));
        Assert.Equal(data[5..15], fs.ReadRange(dst, 5, 10));
        Assert.Equal(data.Length - 5, fs.ReadRange(dst, 5, data.Length).Length);
        Assert.Null(fs.Stat(_dir.Combine("none.bin")));
        Assert.True(fs.FreeBytes(_dir.Path) > 0);
        Assert.NotEmpty(fs.FixedDrives());
    }

    [Fact]
    public void EnsureRootCreatesAnAdminOnlyWritableFolderAndLeavesAnExistingOneAlone()
    {
        try
        {
            EnsureRootChecks();
        }
        finally
        {
            foreach (string d in new[] { _dir.Combine("fresh"), _dir.Combine("newparent") }.Concat(Directory.Exists(_dir.Combine("newparent")) ? [_dir.Combine(Path.Combine("newparent", "tier"))] : []).Where(Directory.Exists))
            {
                var info = new DirectoryInfo(d);
                System.Security.AccessControl.DirectorySecurity open = info.GetAccessControl();
                open.SetAccessRuleProtection(false, false); // 임시 폴더 정리를 위해 상속을 되살림 (소유자는 권한을 바꿀 수 있음)
                info.SetAccessControl(open);
            }
        }
    }

    private void EnsureRootChecks()
    {
        var fs = new TierFs();
        string fresh = _dir.Combine("fresh");
        string existing = _dir.Combine("existing");
        Directory.CreateDirectory(existing);
        int before = new DirectoryInfo(existing).GetAccessControl().GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)).Count;

        string nested = _dir.Combine(Path.Combine("newparent", "tier"));
        fs.EnsureRoot(fresh);
        fs.EnsureRoot(existing);
        fs.EnsureRoot(nested);
        Assert.True(new DirectoryInfo(Path.GetDirectoryName(nested)!).GetAccessControl().AreAccessRulesProtected); // 새로 만든 바로 위 폴더도

        System.Security.AccessControl.DirectorySecurity acl = new DirectoryInfo(fresh).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected); // 위쪽 폴더의 권한을 물려받지 않음
        var rules = acl.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)).Cast<System.Security.AccessControl.FileSystemAccessRule>().ToList();
        Assert.Equal(3, rules.Count);
        Assert.Contains(rules, r => r.IdentityReference.Value == "S-1-5-18" && r.FileSystemRights.HasFlag(System.Security.AccessControl.FileSystemRights.FullControl));
        Assert.Contains(rules, r => r.IdentityReference.Value == "S-1-5-32-545" && r.FileSystemRights.HasFlag(System.Security.AccessControl.FileSystemRights.ReadAndExecute));
        Assert.DoesNotContain(rules, r => r.IdentityReference.Value == "S-1-5-32-545" && (r.FileSystemRights & System.Security.AccessControl.FileSystemRights.WriteData) != 0);
        Assert.Equal(before, new DirectoryInfo(existing).GetAccessControl().GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)).Count);
    }

    [Fact]
    public void DeleteLinkRefusesARegularFileAndMoveDoesNotOverwrite()
    {
        _dir.WriteRandom("a.bin", 100);
        _dir.WriteRandom("b.bin", 100);
        var fs = new TierFs();

        Assert.Throws<IOException>(() => fs.DeleteLink(_dir.Combine("a.bin")));
        Assert.True(File.Exists(_dir.Combine("a.bin"))); // 일반 파일은 지우지 않음
        Assert.Throws<IOException>(() => fs.Move(_dir.Combine("a.bin"), _dir.Combine("b.bin"), false));
        Assert.True(fs.Exists(_dir.Combine("a.bin")));
        Assert.False(fs.IsReparsePoint(_dir.Combine("a.bin")));
        Assert.Null(fs.LinkTarget(_dir.Combine("a.bin")));
    }

    [Fact]
    public void RealSymbolicLinkRoundTripWhenTheLinkPrivilegeIsAvailable()
    {
        string probe = _dir.Combine("probe.lnk");
        _dir.WriteRandom("probe.bin", 10);
        try
        {
            File.CreateSymbolicLink(probe, _dir.Combine("probe.bin"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            output.WriteLine("SKIPPED real symlink round trip: cannot create symbolic links here (" + e.GetType().Name + ": " + e.Message + ")");
            return;
        }

        File.Delete(probe);
        string game = _dir.Combine("Games");
        Directory.CreateDirectory(Path.Combine(game, "sub"));
        byte[] a = _dir.WriteRandom(Path.Combine("Games", "a.bin"), (1 << 20) + 123);
        byte[] b = _dir.WriteRandom(Path.Combine("Games", "sub", "b.bin"), 4096);
        var settings = new GameSettings("Foo", ["foo.exe"], "x.json", "y.json", game, [game], "GAMES", 60);
        var engine = new TierEngine(new TierFs(new IoPriorityHint(_ => { })), new ListLog(), _dir.Combine("journals"));
        string root = _dir.Combine("tier");
        Directory.CreateDirectory(root); // 이미 있는 폴더의 권한은 건드리지 않음 (새로 만들면 관리자 전용 권한이라 승격 안 된 시험 프로세스는 못 씀)
        string pa = Path.Combine(game, "a.bin"), pb = Path.Combine(game, "sub", "b.bin");
        var sel = new TierSelection([new TierPick(pa, a.Length, a.Length), new TierPick(pb, b.Length, b.Length)], a.Length + b.Length, 0, 0, []);

        TierApplyReport rep = engine.Apply(settings, root, 1, sel, default);

        if (rep.Applied == 0 && rep.Skipped.Any(s => s.Reason == "reparse_point"))
        {
            output.WriteLine("SKIPPED real symlink round trip: the temp folder sits under a reparse point (" + string.Join(", ", rep.Skipped.Select(s => s.Reason)) + ")");
            return;
        }

        output.WriteLine("RAN real symlink round trip: applied " + rep.Applied + " file(s); skipped: " + string.Join("; ", rep.Skipped.Select(x => x.Reason)) + "; stopped: " + rep.Stopped);
        Assert.Equal(2, rep.Applied);
        Assert.NotNull(new FileInfo(pa).LinkTarget);
        Assert.Equal(a, File.ReadAllBytes(pa)); // 링크를 통해 읽음
        Assert.Equal(b, File.ReadAllBytes(pb));
        Assert.Equal(a, File.ReadAllBytes(pa + TierEngine.OrigSuffix));
        TierGameInfo info = engine.Maintain("Foo")!;
        Assert.Equal((2, 0), (info.Active, info.Stale));
        Assert.Equal(a.Length, PlanFileSize(pa)); // 계획 지문은 링크 대상의 크기

        TierRevertReport back = engine.Revert("Foo", "test");

        Assert.Equal((2, 0), (back.Reverted, back.Failed));
        Assert.Null(new FileInfo(pa).LinkTarget);
        Assert.Equal(a, File.ReadAllBytes(pa));
        Assert.Equal(b, File.ReadAllBytes(pb));
        Assert.False(File.Exists(pa + TierEngine.OrigSuffix));
        Assert.True(File.Exists(TierEngine.DestPath(root, "Foo", pa))); // 사본은 남음
        (int files, _) = engine.Clean();
        Assert.Equal(2, files);
        Assert.Equal(a, File.ReadAllBytes(pa));
    }

    private static long PlanFileSize(string path) => AsterCache.Core.Plan.PlanFile.FingerprintOf(path).Size;
}
