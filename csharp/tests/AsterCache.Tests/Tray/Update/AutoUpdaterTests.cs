using AsterCache.Tray.Update;
using Xunit;

namespace AsterCache.Tests.Tray;

public sealed class AutoUpdaterTests
{
    private static readonly string[] FixedArgs = ["/SILENT", "/NORESTART", "/SUPPRESSMSGBOXES", "/LANG=korean"];

    private static string[] Files(UpdateRig rig) => Directory.Exists(rig.StagingDir) ? [.. Directory.GetFiles(rig.StagingDir).Select(Path.GetFileName).Cast<string>().Order()] : [];

    [Fact]
    public async Task AutoInstallsAVerifiedNewVersionWithFixedArguments()
    {
        using var rig = new UpdateRig();
        rig.Publish("1.0.0");
        AutoUpdater u = rig.Build();

        TimeSpan wait = await u.StepAsync(CancellationToken.None);

        Assert.Equal(AutoUpdater.InstallWait, wait);
        Assert.Equal(UpdateState.Installing, u.Status.State);
        (string path, string[] args) = Assert.Single(rig.Launcher.Launches);
        Assert.Equal(Path.Combine(rig.StagingDir, "AsterCache-v1.0.0-win-x64-setup.exe"), path);
        Assert.Equal(FixedArgs, args);
        Assert.Equal(["AsterCache-v1.0.0-win-x64-setup.exe", UpdateStaging.AttemptsFile], Files(rig)); // 부분 파일 없음
        Assert.Equal(rig.Installer, File.ReadAllBytes(path));
        Assert.Equal(["https://api.github.com/repos/DO0OG/AsterCache/releases/latest", UpdateRig.DownloadUrl("1.0.0", UpdateRig.InstallerName("1.0.0"))], rig.Handler.Urls);
    }

    [Fact]
    public async Task ChecksumFileIsUsedWhenThereIsNoDigest()
    {
        using var rig = new UpdateRig();
        rig.Publish("1.0.0", digest: false, sums: true);
        AutoUpdater u = rig.Build();

        await u.StepAsync(CancellationToken.None);

        Assert.Equal(UpdateState.Installing, u.Status.State);
        Assert.Single(rig.Launcher.Launches);
        Assert.Contains(UpdateRig.DownloadUrl("1.0.0", "SHA256SUMS.txt"), rig.Handler.Urls);
    }

    [Fact]
    public async Task DigestMismatchDeletesTheFileAndNeverInstalls()
    {
        using var rig = new UpdateRig();
        rig.Publish("1.0.0");
        byte[] real = rig.Installer;
        rig.Handler.Serve(UpdateRig.DownloadUrl("1.0.0", UpdateRig.InstallerName("1.0.0")), Enumerable.Reverse(real).ToArray());
        AutoUpdater u = rig.Build();

        TimeSpan wait = await u.StepAsync(CancellationToken.None);

        Assert.Equal(UpdateState.Failed, u.Status.State);
        Assert.Equal(AutoUpdater.RetryDelays[0], wait);
        Assert.Empty(rig.Launcher.Launches);
        Assert.Empty(Files(rig));
    }

    [Fact]
    public async Task ChecksumFileMismatchOrMissingEntryNeverInstalls()
    {
        using var rig = new UpdateRig();
        rig.Publish("1.0.0", digest: false, sums: true, sumsText: new string('0', 64) + "  AsterCache-v1.0.0-win-x64-setup.exe\n");
        AutoUpdater u = rig.Build();
        await u.StepAsync(CancellationToken.None);
        Assert.Equal(UpdateState.Failed, u.Status.State);

        rig.Publish("1.0.0", digest: false, sums: true, sumsText: rig.Sha + "  something-else.zip\n");
        await u.StepAsync(CancellationToken.None);
        Assert.Equal(UpdateState.Failed, u.Status.State);
        Assert.Empty(rig.Launcher.Launches);
        Assert.Empty(Files(rig));
    }

    [Fact]
    public async Task NoChecksumAtAllMeansNothingIsDownloaded()
    {
        using var rig = new UpdateRig();
        rig.Publish("1.0.0", digest: false, sums: false);
        AutoUpdater u = rig.Build();

        await u.StepAsync(CancellationToken.None);

        Assert.Equal(UpdateState.Failed, u.Status.State);
        Assert.Empty(rig.Launcher.Launches);
        Assert.DoesNotContain(UpdateRig.DownloadUrl("1.0.0", UpdateRig.InstallerName("1.0.0")), rig.Handler.Urls);
    }

    [Fact]
    public async Task SizeDifferentFromTheMetadataIsRejectedAndCleaned()
    {
        using var rig = new UpdateRig();
        rig.Publish("1.0.0", size: rig.Installer.Length + 1);
        AutoUpdater u = rig.Build();

        await u.StepAsync(CancellationToken.None);

        Assert.Equal(UpdateState.Failed, u.Status.State);
        Assert.Empty(rig.Launcher.Launches);
        Assert.Empty(Files(rig));
    }

    [Fact]
    public async Task OversizeInstallerIsRejectedAndCleaned()
    {
        using var rig = new UpdateRig { Limits = new UpdateLimits(InstallerBytes: 1000) };
        rig.Publish("1.0.0");
        AutoUpdater u = rig.Build();

        await u.StepAsync(CancellationToken.None);

        Assert.Equal(UpdateState.Failed, u.Status.State);
        Assert.Empty(rig.Launcher.Launches);
        Assert.Empty(Files(rig));
    }

    [Fact]
    public async Task RedirectToAForeignHostFailsTheUpdate()
    {
        using var rig = new UpdateRig();
        rig.Publish("1.0.0");
        rig.Handler.Redirect(UpdateRig.DownloadUrl("1.0.0", UpdateRig.InstallerName("1.0.0")), "https://evil.example/a.exe");
        rig.Handler.Serve("https://evil.example/a.exe", rig.Installer);
        AutoUpdater u = rig.Build();

        await u.StepAsync(CancellationToken.None);

        Assert.Equal(UpdateState.Failed, u.Status.State);
        Assert.DoesNotContain("https://evil.example/a.exe", rig.Handler.Urls);
        Assert.Empty(rig.Launcher.Launches);
        Assert.Empty(Files(rig));
    }

    [Fact]
    public async Task WaitsWhileAGameRunsThenInstallsWhenItEnds()
    {
        using var rig = new UpdateRig();
        rig.Publish("1.0.0");
        rig.Gate.State = GateState.Busy;
        AutoUpdater u = rig.Build();

        TimeSpan wait = await u.StepAsync(CancellationToken.None);

        Assert.Equal(UpdateState.WaitingForGame, u.Status.State);
        Assert.Equal(AutoUpdater.WaitRecheck, wait);
        Assert.Empty(rig.Launcher.Launches);
        int requests = rig.Handler.Requests.Count;

        wait = await u.StepAsync(CancellationToken.None); // 아직 실행 중: 다시 받지 않고 조건만 봄
        Assert.Equal(AutoUpdater.WaitRecheck, wait);
        Assert.Equal(requests, rig.Handler.Requests.Count);

        rig.Gate.State = GateState.Unknown; // 판단할 수 없으면 설치하지 않음
        await u.StepAsync(CancellationToken.None);
        Assert.Empty(rig.Launcher.Launches);

        rig.Gate.State = GateState.Idle;
        wait = await u.StepAsync(CancellationToken.None);
        Assert.Equal(AutoUpdater.InstallWait, wait);
        Assert.Equal(UpdateState.Installing, u.Status.State);
        Assert.Equal(FixedArgs, Assert.Single(rig.Launcher.Launches).Args);
        Assert.Equal(requests, rig.Handler.Requests.Count);
    }

    [Fact]
    public async Task InstallerChangedAfterVerificationIsNeverRun()
    {
        using var rig = new UpdateRig();
        rig.Publish("1.0.0");
        rig.Gate.State = GateState.Busy;
        AutoUpdater u = rig.Build();
        await u.StepAsync(CancellationToken.None);

        File.WriteAllBytes(Path.Combine(rig.StagingDir, UpdateRig.InstallerName("1.0.0")), [1, 2, 3]); // 검증 뒤 바꿔치기
        rig.Gate.State = GateState.Idle;
        await u.StepAsync(CancellationToken.None);

        Assert.Equal(UpdateState.Failed, u.Status.State);
        Assert.Empty(rig.Launcher.Launches);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task WithoutAdminOrAnInstalledCopyItOnlyAnnouncesAndOpensThePage(bool admin, bool installed)
    {
        using var rig = new UpdateRig { Admin = admin, Installed = installed };
        rig.Publish("1.0.0");
        AutoUpdater u = rig.Build();

        TimeSpan wait = await u.StepAsync(CancellationToken.None);

        Assert.Equal(UpdateState.Available, u.Status.State);
        Assert.Equal(AutoUpdater.CheckInterval, wait);
        Assert.False(u.CanInstall);
        Assert.DoesNotContain(UpdateRig.DownloadUrl("1.0.0", UpdateRig.InstallerName("1.0.0")), rig.Handler.Urls); // 받지도 않음
        Assert.False(Directory.Exists(rig.StagingDir));

        await u.InstallNowAsync(CancellationToken.None);
        Assert.Equal(new Uri("https://github.com/DO0OG/AsterCache/releases/tag/v1.0.0"), Assert.Single(rig.Opened));
        Assert.Empty(rig.Launcher.Launches);
    }

    [Fact]
    public async Task NotifyModeAnnouncesAndInstallsOnlyAfterTheUserClicks()
    {
        using var rig = new UpdateRig { Options = new UpdateOptions(UpdateMode.Notify, false) };
        rig.Publish("1.0.0");
        AutoUpdater u = rig.Build();

        await u.StepAsync(CancellationToken.None);
        Assert.Equal(UpdateState.Available, u.Status.State);
        Assert.Empty(rig.Launcher.Launches);
        Assert.False(Directory.Exists(rig.StagingDir));

        rig.Gate.State = GateState.Busy; // 사용자가 눌렀지만 게임 중: 끝나기를 기다렸다가 설치 (눌렀다는 사실이 유지됨)
        await u.InstallNowAsync(CancellationToken.None);
        Assert.Equal(UpdateState.WaitingForGame, u.Status.State);
        rig.Gate.State = GateState.Idle;
        await u.StepAsync(CancellationToken.None);
        Assert.Equal(UpdateState.Installing, u.Status.State);
        Assert.Single(rig.Launcher.Launches);
    }

    [Fact]
    public async Task OffNeverTouchesTheNetwork()
    {
        using var rig = new UpdateRig { Options = new UpdateOptions(UpdateMode.Off, false) };
        rig.Publish("1.0.0");
        AutoUpdater u = rig.Build();

        TimeSpan wait = await u.StepAsync(CancellationToken.None);
        UpdateStatus manual = await u.CheckNowAsync(CancellationToken.None);

        Assert.Equal(AutoUpdater.OffRecheck, wait);
        Assert.Equal(UpdateState.Disabled, manual.State);
        Assert.Empty(rig.Handler.Requests);
    }

    [Fact]
    public async Task ACheckFailureIsNeverShownAsUpToDate()
    {
        using var rig = new UpdateRig();
        rig.Handler.Fail(UpdateRig.Latest);
        AutoUpdater u = rig.Build();

        UpdateStatus failed = await u.CheckNowAsync(CancellationToken.None);

        Assert.Equal(UpdateState.Failed, failed.State);
        Assert.NotEqual(UpdateState.UpToDate, failed.State);
        Assert.DoesNotContain("최신", failed.Text());

        rig.Handler.Serve(UpdateRig.Latest, "{\"tag_name\":\"v0.9.0-beta.38\",\"assets\":[]}"); // 같은 버전은 업데이트가 아님
        UpdateStatus ok = await u.CheckNowAsync(CancellationToken.None);
        Assert.Equal(UpdateState.UpToDate, ok.State);
        Assert.Contains("최신", ok.Text());
    }

    [Fact]
    public async Task NoNewerReleaseDownloadsNothingAndCleansOldInstallers()
    {
        using var rig = new UpdateRig { Current = "1.0.0" };
        Directory.CreateDirectory(rig.StagingDir);
        File.WriteAllText(Path.Combine(rig.StagingDir, "AsterCache-v1.0.0-win-x64-setup.exe"), "old");
        File.WriteAllText(Path.Combine(rig.StagingDir, "AsterCache-v1.0.1-win-x64-setup.exe.partial"), "half");
        rig.Publish("1.0.0"); // 같은 버전
        AutoUpdater u = rig.Build();

        await u.StepAsync(CancellationToken.None);

        Assert.Equal(UpdateState.UpToDate, u.Status.State);
        Assert.Empty(Files(rig));
        Assert.Single(rig.Handler.Requests);
    }

    [Fact]
    public async Task ARollbackToALowerVersionIsIgnored()
    {
        using var rig = new UpdateRig { Current = "1.0.0" };
        rig.Publish("0.9.0");
        AutoUpdater u = rig.Build();
        await u.StepAsync(CancellationToken.None);
        Assert.Equal(UpdateState.UpToDate, u.Status.State);
        Assert.Empty(rig.Launcher.Launches);
    }

    [Fact]
    public async Task PrereleaseSettingUsesTheListAndPicksTheHighest()
    {
        using var rig = new UpdateRig { Options = new UpdateOptions(UpdateMode.Auto, true) };
        rig.Handler.Serve(UpdateRig.List, "[" + rig.Release("1.0.0") + "," + rig.Release("1.1.0-rc.1", prerelease: true) + "]");
        rig.Handler.Serve(UpdateRig.DownloadUrl("1.1.0-rc.1", UpdateRig.InstallerName("1.1.0-rc.1")), rig.Installer);
        AutoUpdater u = rig.Build();

        await u.StepAsync(CancellationToken.None);

        Assert.Equal(UpdateRig.List, rig.Handler.Urls.First());
        Assert.Equal("AsterCache-v1.1.0-rc.1-win-x64-setup.exe", Path.GetFileName(Assert.Single(rig.Launcher.Launches).Path));
    }

    [Fact]
    public async Task ReparsePointStagingFolderIsRefused()
    {
        using var rig = new UpdateRig { IsReparse = p => p.EndsWith("updates", StringComparison.Ordinal) };
        rig.Publish("1.0.0");
        AutoUpdater u = rig.Build();

        await u.StepAsync(CancellationToken.None);

        Assert.Equal(UpdateState.Failed, u.Status.State);
        Assert.Empty(rig.Launcher.Launches);
        Assert.DoesNotContain(UpdateRig.DownloadUrl("1.0.0", UpdateRig.InstallerName("1.0.0")), rig.Handler.Urls);
    }

    [Fact]
    public async Task FailureRetriesBackOffAndResetAfterSuccess()
    {
        using var rig = new UpdateRig();
        rig.Handler.Fail(UpdateRig.Latest);
        AutoUpdater u = rig.Build();

        TimeSpan[] waits = [await u.StepAsync(CancellationToken.None), await u.StepAsync(CancellationToken.None), await u.StepAsync(CancellationToken.None), await u.StepAsync(CancellationToken.None)];
        Assert.Equal([TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(6), TimeSpan.FromHours(6)], waits);

        rig.Handler.Serve(UpdateRig.Latest, "{\"tag_name\":\"v0.9.0-beta.38\",\"assets\":[]}");
        Assert.Equal(TimeSpan.FromHours(24), await u.StepAsync(CancellationToken.None));

        rig.Handler.Fail(UpdateRig.Latest);
        Assert.Equal(TimeSpan.FromMinutes(15), await u.StepAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RunChecksFiveMinutesAfterStartThenEveryDayAndStopsOnCancel()
    {
        using var rig = new UpdateRig { Clock = new FakeClock(stopAfter: 4) };
        rig.Handler.Serve(UpdateRig.Latest, "{\"tag_name\":\"v0.9.0-beta.38\",\"assets\":[]}");
        AutoUpdater u = rig.Build();

        await u.RunAsync(CancellationToken.None);

        Assert.Equal([TimeSpan.FromMinutes(5), TimeSpan.FromHours(24), TimeSpan.FromHours(24), TimeSpan.FromHours(24)], rig.Clock.Delays);
        Assert.Equal(3, rig.Handler.Requests.Count); // 첫 지연 뒤 확인 3 번
    }

    [Fact]
    public async Task RunWaitsForTheTrayToCloseAfterLaunchingTheInstaller()
    {
        using var rig = new UpdateRig { Clock = new FakeClock(stopAfter: 2) };
        rig.Publish("1.0.0");
        AutoUpdater u = rig.Build();

        await u.RunAsync(CancellationToken.None);

        Assert.Equal([AutoUpdater.FirstCheckDelay, AutoUpdater.InstallWait], rig.Clock.Delays);
        Assert.Single(rig.Launcher.Launches);
    }

    [Fact]
    public async Task RunSurvivesAnUnexpectedExceptionAndTriesAgainAfterTheLongestInterval()
    {
        using var rig = new UpdateRig { Clock = new FakeClock(stopAfter: 3) };
        rig.Handler.Routes[UpdateRig.Latest] = () => throw new InvalidOperationException("unexpected");
        AutoUpdater u = rig.Build();

        await u.RunAsync(CancellationToken.None);

        Assert.Equal([TimeSpan.FromMinutes(5), TimeSpan.FromHours(6), TimeSpan.FromHours(6)], rig.Clock.Delays);
        Assert.Equal(2, rig.Handler.Requests.Count);
        Assert.Contains(rig.Log.Lines, l => l.StartsWith("update loop error", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallerThatNeverClosesTheTrayIsTreatedAsFailedAndStopsAfterTwoAttempts()
    {
        using var rig = new UpdateRig();
        rig.Publish("1.0.0");
        AutoUpdater u = rig.Build();

        Assert.Equal(AutoUpdater.InstallWait, await u.StepAsync(CancellationToken.None)); // 1회 시도
        Assert.Equal(UpdateState.Installing, u.Status.State);
        Assert.Equal(AutoUpdater.RetryDelays[0], await u.StepAsync(CancellationToken.None)); // 트레이가 아직 살아 있음 → 실패로 되돌림
        Assert.Equal(UpdateState.Failed, u.Status.State);

        Assert.Equal(AutoUpdater.InstallWait, await u.StepAsync(CancellationToken.None)); // 2회 시도
        Assert.Equal(2, rig.Launcher.Launches.Count);
        await u.StepAsync(CancellationToken.None);
        Assert.Equal(UpdateState.Failed, u.Status.State);

        Assert.Equal(AutoUpdater.CheckInterval, await u.StepAsync(CancellationToken.None)); // 3번째는 자동으로 하지 않고 알리기만
        Assert.Equal(UpdateState.Available, u.Status.State);
        Assert.Equal(2, rig.Launcher.Launches.Count);

        await u.InstallNowAsync(CancellationToken.None); // 사용자가 직접 누르면 다시 시도
        Assert.Equal(3, rig.Launcher.Launches.Count);
        Assert.Equal(UpdateState.Installing, u.Status.State);
    }

    [Fact]
    public async Task SuccessfulUpgradeClearsTheAttemptCount()
    {
        using var rig = new UpdateRig();
        rig.Publish("1.0.0");
        await rig.Build().StepAsync(CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(rig.StagingDir, UpdateStaging.AttemptsFile)));

        rig.Current = "1.0.0"; // 설치가 끝나 새 버전으로 다시 뜸
        await rig.Build().StepAsync(CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(rig.StagingDir, UpdateStaging.AttemptsFile)));
    }

    [Fact]
    public async Task UntrustedWritersOnTheUpdateFolderMeanNothingIsDownloadedOrRun()
    {
        using var rig = new UpdateRig { DirWriters = _ => ["S-1-5-32-545"] };
        rig.Publish("1.0.0");
        AutoUpdater u = rig.Build();

        await u.StepAsync(CancellationToken.None);

        Assert.Equal(UpdateState.Failed, u.Status.State);
        Assert.Empty(rig.Launcher.Launches);
        Assert.DoesNotContain(UpdateRig.DownloadUrl("1.0.0", UpdateRig.InstallerName("1.0.0")), rig.Handler.Urls);
    }

    [Fact]
    public async Task PermissionsAreCheckedAgainJustBeforeRunning()
    {
        using var rig = new UpdateRig();
        rig.Publish("1.0.0");
        rig.Gate.State = GateState.Busy;
        AutoUpdater u = rig.Build();
        await u.StepAsync(CancellationToken.None);
        Assert.Equal(UpdateState.WaitingForGame, u.Status.State);

        rig.Gate.State = GateState.Idle;
        rig.DirWriters = _ => ["S-1-5-32-545"]; // 기다리는 동안 폴더 권한이 풀림 (폴더가 다시 만들어진 경우)
        await u.StepAsync(CancellationToken.None);
        Assert.Equal(UpdateState.Failed, u.Status.State);
        Assert.Empty(rig.Launcher.Launches);
    }

    [Fact]
    public async Task UntrustedWritersOnTheInstallerFileAreRefusedBeforeHashingAndBeforeRunning()
    {
        using var rig = new UpdateRig { FileWriters = _ => ["S-1-5-32-545"] };
        rig.Publish("1.0.0");
        AutoUpdater u = rig.Build();
        await u.StepAsync(CancellationToken.None);
        Assert.Equal(UpdateState.Failed, u.Status.State);
        Assert.Empty(rig.Launcher.Launches);
        Assert.DoesNotContain(Files(rig), f => f.StartsWith("AsterCache-v", StringComparison.Ordinal));

        rig.FileWriters = _ => [];
        rig.Gate.State = GateState.Busy;
        await u.StepAsync(CancellationToken.None);
        Assert.Equal(UpdateState.WaitingForGame, u.Status.State);
        rig.FileWriters = _ => ["S-1-5-32-545"]; // 검증 뒤 파일 권한이 풀림
        rig.Gate.State = GateState.Idle;
        await u.StepAsync(CancellationToken.None);
        Assert.Equal(UpdateState.Failed, u.Status.State);
        Assert.Empty(rig.Launcher.Launches);
    }

    [Fact]
    public async Task FilesAlreadyInTheFolderAreNeverTrustedAndAreReplaced()
    {
        using var rig = new UpdateRig();
        Directory.CreateDirectory(rig.StagingDir);
        string planted = Path.Combine(rig.StagingDir, UpdateRig.InstallerName("1.0.0"));
        File.WriteAllBytes(planted, [6, 6, 6]);
        File.WriteAllBytes(planted + ".partial", [6, 6, 6]);
        rig.Publish("1.0.0");
        AutoUpdater u = rig.Build();

        await u.StepAsync(CancellationToken.None);

        Assert.Equal(UpdateState.Installing, u.Status.State);
        Assert.Contains(UpdateRig.DownloadUrl("1.0.0", UpdateRig.InstallerName("1.0.0")), rig.Handler.Urls); // 새로 받음
        Assert.Equal(rig.Installer, File.ReadAllBytes(planted));
        Assert.DoesNotContain(Files(rig), f => f.EndsWith(".partial", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StatusTextsExistInBothLanguagesAndStatusChangesAreAnnounced()
    {
        using var rig = new UpdateRig();
        rig.Publish("1.0.0");
        AutoUpdater u = rig.Build();
        var seen = new List<UpdateState>();
        u.StatusChanged += () => seen.Add(u.Status.State);

        await u.StepAsync(CancellationToken.None);

        Assert.Equal([UpdateState.Available, UpdateState.Downloading, UpdateState.Installing], seen.Distinct());
        Assert.Contains(rig.Log.Lines, l => l.StartsWith("update: ", StringComparison.Ordinal));
        foreach (UpdateState s in Enum.GetValues<UpdateState>().Where(s => s != UpdateState.Idle))
        {
            Assert.DoesNotContain("Update_", new UpdateStatus(s, "1.0.0", 5).Text());
        }
    }
}
