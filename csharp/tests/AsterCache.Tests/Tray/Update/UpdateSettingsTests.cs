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
}
