using System.Diagnostics;
using AsterCache.Core.Settings;
using AsterCache.Tray.Update;
using Xunit;

namespace AsterCache.Tests.Tray;

public sealed class UpdateSystemTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_updsys_" + Guid.NewGuid().ToString("N"));

    public UpdateSystemTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static AsterSettings Settings(params string[] exes) =>
        new(1000, exes.Length == 0 ? [] : [new GameSettings("g", exes, "t", "p", @"C:\g", [@"C:\"], "V", 0)]);

    private string StatusFile(string json)
    {
        string path = Path.Combine(_dir, "auto_status.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void GateIsBusyWhileARegisteredGameRuns()
    {
        string? asked = null;
        var gate = new TrayUpdateGate(() => Settings("a.exe", "b.exe"), exes => (asked = string.Join(',', exes)) is not null, Path.Combine(_dir, "none.json"));
        Assert.Equal(GateState.Busy, gate.Query());
        Assert.Equal("a.exe,b.exe", asked);
    }

    [Fact]
    public void GateIsIdleWhenNothingRunsAndThereIsNoStatusFile() =>
        Assert.Equal(GateState.Idle, new TrayUpdateGate(() => Settings("a.exe"), _ => false, Path.Combine(_dir, "none.json")).Query());

    [Fact]
    public void GateIsUnknownWhenTheGameListCannotBeRead() =>
        Assert.Equal(GateState.Unknown, new TrayUpdateGate(() => null, _ => false, Path.Combine(_dir, "none.json")).Query());

    [Fact]
    public void GateIsUnknownWhenTheProcessListFails() =>
        Assert.Equal(GateState.Unknown, new TrayUpdateGate(() => Settings("a.exe"), _ => throw new System.ComponentModel.Win32Exception(5), Path.Combine(_dir, "none.json")).Query());

    [Fact]
    public void GateIsBusyWhileARelocationRunsAndUnknownWhenTheStatusFileIsBroken()
    {
        string relocating = StatusFile("{\"games\":{\"g\":{\"stage\":\"relocating\"}}}");
        Assert.Equal(GateState.Busy, new TrayUpdateGate(() => Settings(), _ => false, relocating).Query());

        string done = StatusFile("{\"games\":{\"g\":{\"stage\":\"done\"}}}");
        Assert.Equal(GateState.Idle, new TrayUpdateGate(() => Settings(), _ => false, done).Query());

        string broken = StatusFile("{not json");
        Assert.Equal(GateState.Unknown, new TrayUpdateGate(() => Settings(), _ => false, broken).Query());
    }

    [Theory]
    [InlineData(@"C:\Program Files\AsterCache\AsterCache.Tray.exe", true, @"C:\ProgramData\AsterCache", true)]
    [InlineData(@"c:\program files\asterCache\AsterCache.Tray.exe", true, @"C:\ProgramData\AsterCache\", true)]
    [InlineData(@"C:\Program Files2\AsterCache\AsterCache.Tray.exe", true, @"C:\ProgramData\AsterCache", false)] // 접두사만 같은 폴더
    [InlineData(@"D:\Tools\AsterCache\AsterCache.Tray.exe", true, @"C:\ProgramData\AsterCache", false)] // zip 을 푼 위치
    [InlineData(@"C:\Program Files\AsterCache\AsterCache.Tray.exe", false, @"C:\ProgramData\AsterCache", false)] // 제거 프로그램 없음: 설치 프로그램으로 설치된 것이 아님
    [InlineData(@"C:\Program Files\AsterCache\AsterCache.Tray.exe", true, @"D:\MyData", false)] // 관리자 전용으로 잠기지 않은 데이터 폴더
    [InlineData(null, true, @"C:\ProgramData\AsterCache", false)]
    public void InstalledBuildNeedsProgramFilesAnUninstallerAndTheLockedDataFolder(string? exe, bool uninstaller, string dataDir, bool expected) =>
        Assert.Equal(expected, UpdateEnvironment.IsInstalledBuild(exe, [@"C:\Program Files", @"C:\Program Files (x86)"], f => uninstaller && f.EndsWith("unins000.exe", StringComparison.Ordinal), dataDir, @"C:\ProgramData\AsterCache"));

    [Fact]
    public void CurrentVersionComesFromTheInformationalVersion()
    {
        SemVer? v = UpdateEnvironment.CurrentVersion(typeof(AutoUpdater).Assembly);
        Assert.NotNull(v);
        Assert.True(SemVer.TryParse("0.9.0-beta.38", out SemVer floor) && v.Value >= floor);
    }

    [Fact]
    public void StagingCountsInstallAttemptsPerVersion()
    {
        var staging = new UpdateStaging(_dir, null, _ => [], _ => []);
        staging.Prepare();
        Assert.Equal(0, staging.Attempts("1.0.0"));
        staging.RecordAttempt("1.0.0");
        staging.RecordAttempt("1.0.0");
        Assert.Equal(2, staging.Attempts("1.0.0"));
        Assert.Equal(0, staging.Attempts("1.0.1")); // 다른 버전은 새로 셈
        staging.Clean(keep: null);
        Assert.Equal(2, staging.Attempts("1.0.0")); // 정리가 기록을 지우지 않음
        staging.ClearAttempts();
        Assert.Equal(0, staging.Attempts("1.0.0"));
    }

    [Fact]
    public void StagingRefusesUntrustedWritersOnTheFolderAndTheFile()
    {
        var bad = new UpdateStaging(_dir, null, _ => ["S-1-5-32-545"], _ => []);
        Assert.Contains("untrusted", Assert.Throws<UpdateException>(bad.Prepare).Message);

        string file = Path.Combine(_dir, "AsterCache-v1.0.0-win-x64-setup.exe");
        File.WriteAllText(file, "x");
        var badFile = new UpdateStaging(_dir, null, _ => [], _ => ["S-1-5-32-545"]);
        Assert.Throws<UpdateException>(() => badFile.Sha256Hex(file));
        Assert.NotEmpty(new UpdateStaging(_dir, null, _ => [], _ => []).Sha256Hex(file));
    }

    [Fact]
    public void StagingRefusesTraversalNamesAndJunctions()
    {
        var staging = new UpdateStaging(_dir, null, _ => [], _ => []);
        Assert.Throws<UpdateException>(() => staging.PathFor(@"..\evil.exe"));
        Assert.Throws<UpdateException>(() => staging.PathFor(@"sub\evil.exe"));
        staging.Prepare(); // 일반 폴더는 통과

        string target = Path.Combine(_dir, "target");
        string link = Path.Combine(_dir, "link");
        Directory.CreateDirectory(target);
        using Process cmd = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        cmd.WaitForExit();
        if (Directory.Exists(link)) // junction 을 만들 수 없는 환경이면 이 부분은 건너뜀
        {
            try
            {
                Assert.Throws<UpdateException>(() => new UpdateStaging(link, null, _ => [], _ => []).Prepare()); // 데이터 폴더가 junction
            }
            finally
            {
                Directory.Delete(link); // 재귀 삭제가 junction 을 따라가지 않도록 먼저 연결만 지움
            }
        }
    }
}
