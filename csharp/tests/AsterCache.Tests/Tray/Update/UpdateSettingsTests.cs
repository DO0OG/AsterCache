using AsterCache.Core.Settings;
using AsterCache.Tray;
using AsterCache.Tray.Update;
using Xunit;

namespace AsterCache.Tests.Tray;

/// config.json 의 update.mode·update.prerelease 가 실제 업데이트 동작을 정하는지.
public sealed class UpdateSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_updcfg_" + Guid.NewGuid().ToString("N"));

    public UpdateSettingsTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "config.json"), "{\"games\": []}");
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private TrayViewModel Vm() => new(new ConfigEditor(_dir), new TrayViewModelTests.FakeService(), new TrayViewModelTests.FakeStreamer(), new TrayViewModelTests.FakeShell(), new TrayViewModelTests.MemoryLog());

    [Fact]
    public void UnreadableSettingsFallBackToNotifyOnlyWithoutPrereleases()
    {
        Assert.Equal(new UpdateOptions(UpdateMode.Notify, false), UpdateOptions.From(null));
        Assert.Equal(UpdateOptions.Default, UpdateOptions.From(new ConfigEditor(_dir).Load())); // 키가 없으면 기본값: 자동, 정식만
    }

    [Fact]
    public void SetUpdateSavesTheModeAndThePrereleaseFlagAndTheTrayReadsThemBack()
    {
        TrayViewModel vm = Vm();

        Assert.True(vm.SetUpdate(UpdateMode.Notify, true).Ok);
        Assert.Equal(new UpdateOptions(UpdateMode.Notify, true), UpdateOptions.From(vm.Settings)); // Edit 이 설정 캐시를 바로 갱신
        Assert.True(vm.SetUpdate(UpdateMode.Off, false).Ok);
        Assert.Equal(new UpdateOptions(UpdateMode.Off, false), UpdateOptions.From(vm.Settings));
        Assert.True(vm.SetUpdate(UpdateMode.Auto, false).Ok);
        Assert.Equal(UpdateOptions.Default, UpdateOptions.From(vm.Settings));
        Assert.DoesNotContain("\"update\"", File.ReadAllText(Path.Combine(_dir, "config.json")), StringComparison.Ordinal); // 기본값이면 키를 지움
    }

    [Fact]
    public async Task TheRealSettingDecidesWhetherTheUpdaterInstallsAnnouncesOrStaysQuiet()
    {
        using var rig = new UpdateRig();
        rig.Publish("1.0.0");
        TrayViewModel vm = Vm();
        vm.Refresh();
        AutoUpdater u = rig.Build(() => UpdateOptions.From(vm.Settings));

        vm.SetUpdate(UpdateMode.Off, false);
        await u.StepAsync(CancellationToken.None);
        Assert.Empty(rig.Handler.Requests); // off: 조회 없음

        vm.SetUpdate(UpdateMode.Notify, false);
        await u.StepAsync(CancellationToken.None);
        Assert.Equal(UpdateState.Available, u.Status.State); // notify: 알림만
        Assert.Empty(rig.Launcher.Launches);

        vm.SetUpdate(UpdateMode.Auto, false);
        await u.StepAsync(CancellationToken.None);
        Assert.Equal(UpdateState.Installing, u.Status.State); // auto: 설치
        Assert.Single(rig.Launcher.Launches);
    }

    [Fact]
    public async Task ThePrereleaseSettingSelectsTheReleaseListOnlyWhenOn()
    {
        using var rig = new UpdateRig();
        rig.Handler.Serve(UpdateRig.Latest, rig.Release("1.0.0"));
        rig.Handler.Serve(UpdateRig.List, "[" + rig.Release("1.1.0-rc.1", prerelease: true) + "]");
        TrayViewModel vm = Vm();
        vm.Refresh();
        AutoUpdater u = rig.Build(() => UpdateOptions.From(vm.Settings));
        vm.SetUpdate(UpdateMode.Notify, false);

        await u.StepAsync(CancellationToken.None);
        Assert.Equal("1.0.0", u.Status.Version);
        Assert.Equal(UpdateRig.Latest, rig.Handler.Urls.Last());

        vm.SetUpdate(UpdateMode.Notify, true);
        await u.StepAsync(CancellationToken.None);
        Assert.Equal("1.1.0-rc.1", u.Status.Version);
        Assert.Equal(UpdateRig.List, rig.Handler.Urls.Last());
    }

    [Fact]
    public async Task WhenTheSettingsCannotBeReadTheUpdaterOnlyAnnounces()
    {
        using var rig = new UpdateRig();
        rig.Publish("1.0.0");
        File.WriteAllText(Path.Combine(_dir, "config.json"), "{not json");
        TrayViewModel vm = Vm();
        vm.Refresh();
        Assert.Null(vm.Settings);
        AutoUpdater u = rig.Build(() => UpdateOptions.From(vm.Settings));

        await u.StepAsync(CancellationToken.None);

        Assert.Equal(UpdateState.Available, u.Status.State);
        Assert.Empty(rig.Launcher.Launches);
    }

    [Fact]
    public async Task LastCheckedIsSetAfterACheckEvenWhenItFails()
    {
        using var rig = new UpdateRig();
        rig.Handler.Fail(UpdateRig.Latest);
        AutoUpdater u = rig.Build();
        Assert.Null(u.LastChecked);

        await u.CheckNowAsync(CancellationToken.None);

        Assert.NotNull(u.LastChecked);
        Assert.Equal(UpdateState.Failed, u.Status.State);
    }

    [Fact]
    public async Task SwitchingFromAutoToNotifyWhileWaitingStopsTheSilentInstallUntilTheUserClicks()
    {
        using var rig = new UpdateRig();
        rig.Publish("1.0.0");
        rig.Gate.State = GateState.Busy;
        AutoUpdater u = rig.Build();
        await u.StepAsync(CancellationToken.None);
        Assert.Equal(UpdateState.WaitingForGame, u.Status.State); // auto 로 받아 두고 게임 대기

        rig.Options = new UpdateOptions(UpdateMode.Notify, false);
        rig.Gate.State = GateState.Idle;
        await u.StepAsync(CancellationToken.None);

        Assert.Equal(UpdateState.Available, u.Status.State);
        Assert.Empty(rig.Launcher.Launches);

        await u.InstallNowAsync(CancellationToken.None); // 사용자가 직접 누르면 설치
        Assert.Equal(UpdateState.Installing, u.Status.State);
        Assert.Single(rig.Launcher.Launches);
    }

    [Fact]
    public async Task TheSettingIsCheckedAgainJustBeforeRunningAfterADownloadStartedUnderAuto()
    {
        using var rig = new UpdateRig();
        rig.Publish("1.0.0");
        AutoUpdater u = rig.Build(() => rig.Handler.Requests.Count > 1 ? new UpdateOptions(UpdateMode.Notify, false) : rig.Options); // 받기 시작한 뒤 notify 로 바뀜

        await u.StepAsync(CancellationToken.None);

        Assert.Equal(UpdateState.Available, u.Status.State);
        Assert.Empty(rig.Launcher.Launches);
    }

    [Fact]
    public async Task CheckingOrInstallingWhileTheInstallerIsRunningDoesNothing()
    {
        using var rig = new UpdateRig();
        rig.Publish("1.0.0");
        AutoUpdater u = rig.Build();
        await u.StepAsync(CancellationToken.None);
        Assert.Equal(UpdateState.Installing, u.Status.State);
        int requests = rig.Handler.Requests.Count;

        Assert.Equal(UpdateState.Installing, (await u.CheckNowAsync(CancellationToken.None)).State);
        Assert.Equal(UpdateState.Installing, (await u.InstallNowAsync(CancellationToken.None)).State);
        await u.OptionsChangedAsync(CancellationToken.None);

        Assert.Equal(requests, rig.Handler.Requests.Count);
        Assert.Single(rig.Launcher.Launches);
        Assert.Equal(UpdateState.Installing, u.Status.State);
    }

    [Fact]
    public async Task APendingPrereleaseIsDroppedWhenPrereleasesAreSwitchedOff()
    {
        using var rig = new UpdateRig { Options = new UpdateOptions(UpdateMode.Auto, true) };
        rig.Handler.Serve(UpdateRig.List, "[" + rig.Release("1.1.0-rc.1", prerelease: true) + "]");
        rig.Handler.Serve(UpdateRig.DownloadUrl("1.1.0-rc.1", UpdateRig.InstallerName("1.1.0-rc.1")), rig.Installer);
        rig.Handler.Serve(UpdateRig.Latest, "{\"tag_name\":\"v0.9.0-beta.38\",\"assets\":[]}");
        rig.Gate.State = GateState.Busy;
        AutoUpdater u = rig.Build();
        await u.StepAsync(CancellationToken.None);
        Assert.Equal(UpdateState.WaitingForGame, u.Status.State);
        Assert.True(File.Exists(Path.Combine(rig.StagingDir, UpdateRig.InstallerName("1.1.0-rc.1"))));

        rig.Options = new UpdateOptions(UpdateMode.Auto, false);
        rig.Gate.State = GateState.Idle;
        await u.StepAsync(CancellationToken.None);

        Assert.Empty(rig.Launcher.Launches);
        Assert.Equal(UpdateState.UpToDate, u.Status.State); // 다시 확인 주기로 돌아가 정식 기준으로 판정
        Assert.False(File.Exists(Path.Combine(rig.StagingDir, UpdateRig.InstallerName("1.1.0-rc.1"))));
    }

    [Fact]
    public async Task SavingTheSettingMakesTheUpdaterDecideAgainRightAway()
    {
        using var rig = new UpdateRig { Options = new UpdateOptions(UpdateMode.Notify, false) };
        rig.Publish("1.0.0");
        AutoUpdater u = rig.Build();
        await u.OptionsChangedAsync(CancellationToken.None);
        Assert.Equal(UpdateState.Available, u.Status.State); // 곧 한 번 확인

        rig.Options = new UpdateOptions(UpdateMode.Auto, false);
        await u.OptionsChangedAsync(CancellationToken.None);
        Assert.Equal(UpdateState.Installing, u.Status.State);
    }

    [Fact]
    public async Task SwitchingOffClearsWhatWasWaitingWithoutTouchingTheNetwork()
    {
        using var rig = new UpdateRig();
        rig.Publish("1.0.0");
        rig.Gate.State = GateState.Busy;
        AutoUpdater u = rig.Build();
        await u.StepAsync(CancellationToken.None);
        int requests = rig.Handler.Requests.Count;

        rig.Options = new UpdateOptions(UpdateMode.Off, false);
        await u.OptionsChangedAsync(CancellationToken.None);

        Assert.Equal(UpdateState.Idle, u.Status.State);
        Assert.Equal(requests, rig.Handler.Requests.Count);
        Assert.False(File.Exists(Path.Combine(rig.StagingDir, UpdateRig.InstallerName("1.0.0"))));
        rig.Gate.State = GateState.Idle;
        await u.StepAsync(CancellationToken.None);
        Assert.Empty(rig.Launcher.Launches);
    }

    [Fact]
    public void SetUpdateWritesBothValuesInOneSaveAndLeavesTheFileUntouchedOnFailure()
    {
        string path = Path.Combine(_dir, "config.json");
        var editor = new ConfigEditor(_dir);

        editor.SetUpdate(UpdateMode.Notify, true);
        AsterSettings s = editor.Load()!;
        Assert.Equal((UpdateMode.Notify, true), (s.Update.Mode, s.Update.Prerelease));

        editor.SetUpdate(UpdateMode.Auto, false); // 기본값이면 키를 지움
        Assert.DoesNotContain("update", File.ReadAllText(path), StringComparison.Ordinal);
        string before = File.ReadAllText(path);
        editor.SetUpdate(UpdateMode.Auto, false); // 바꿀 것이 없으면 다시 쓰지 않음
        Assert.Equal(before, File.ReadAllText(path));

        File.WriteAllText(path, "{\"games\": [], \"update\": 5}");
        Assert.Throws<SettingsException>(() => editor.SetUpdate(UpdateMode.Notify, true));
        Assert.Equal("{\"games\": [], \"update\": 5}", File.ReadAllText(path)); // 한쪽만 저장되는 일 없음
        Assert.False(Vm().SetUpdate(UpdateMode.Notify, true).Ok);
    }
}
