using AsterCache.Core.Logging;
using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Service;
using AsterCache.Tray;
using Xunit;

namespace AsterCache.Tests.Tray;

public sealed class TrayViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_tray_" + Guid.NewGuid().ToString("N"));
    private readonly FakeService _service = new();
    private readonly FakeStreamer _streamer = new();
    private readonly FakeShell _shell = new();
    private readonly TrayViewModel _vm;

    public TrayViewModelTests()
    {
        Directory.CreateDirectory(_dir);
        _vm = new TrayViewModel(new ConfigEditor(_dir), _service, _streamer, _shell, new MemoryLog());
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private GameInput Game(string name = "mw4") => new(
        name, ["game.exe"], Path.Combine(_dir, "plans", name + ".prefetch.json"), Path.Combine(_dir, "plans", name + ".plan.json"),
        Path.Combine(_dir, "Games", name), [Path.Combine(_dir, "Games")], "GAMES");

    [Fact]
    public void NotInstalledServiceIsShown()
    {
        _service.State = ServiceState.NotInstalled;
        _vm.Refresh();
        Assert.Equal("AsterCache: 서비스 미설치 · 등록된 게임 없음", _vm.StatusText);
        Assert.Empty(_vm.Games);
    }

    [Fact]
    public void StatusShowsServiceStateAndGameCount()
    {
        Assert.True(_vm.AddGame(Game()).Ok);
        _service.State = ServiceState.Running;
        _vm.Refresh();
        Assert.Equal("AsterCache: 서비스 실행 중 · 게임 1개", _vm.StatusText);
        Assert.Equal(["mw4"], _vm.Games);
    }

    [Fact]
    public void BrokenConfigIsShownNotThrown()
    {
        File.WriteAllText(Path.Combine(_dir, "config.json"), "{\"games\": 5}");
        _vm.Refresh();
        Assert.Contains("설정 오류", _vm.StatusText);
        Assert.Empty(_vm.Games);
    }

    [Fact]
    public void AddThenRemoveGame()
    {
        Assert.True(_vm.AddGame(Game("a")).Ok);
        Assert.True(_vm.AddGame(Game("b") with { ExeNames = ["other.exe"] }).Ok);
        Assert.Equal(["a", "b"], _vm.Games);
        Assert.True(_vm.RemoveGame("a").Ok);
        Assert.Equal(["b"], _vm.Games);
    }

    [Fact]
    public void ValidationRejectsOutsideAllowedRootsAndLeavesFileUntouched()
    {
        Assert.True(_vm.AddGame(Game()).Ok);
        byte[] before = File.ReadAllBytes(Path.Combine(_dir, "config.json"));
        ActionResult outside = _vm.AddGame(Game("evil") with { OriginRoot = Path.Combine(_dir, "Elsewhere", "x") });
        Assert.False(outside.Ok);
        Assert.Contains("origin_root", outside.Message);
        Assert.False(_vm.AddGame(Game("mw4")).Ok); // 이름 중복
        Assert.False(_vm.AddGame(Game("p") with { Plan = Path.Combine(_dir, "other", "p.json") }).Ok); // plans 폴더 밖
        Assert.False(_vm.AddGame(Game("..\\x")).Ok); // 이름 문자
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(_dir, "config.json")));
    }

    [Fact]
    public void RemoveUnknownIsRejectedButLastGameCanBeRemoved()
    {
        Assert.False(_vm.RemoveGame("nope").Ok);
        Assert.True(_vm.AddGame(Game()).Ok);
        _service.State = ServiceState.Running;
        ActionResult last = _vm.RemoveGame("mw4"); // 빈 목록은 정상 설정: 서비스는 대기만 함
        Assert.True(last.Ok);
        Assert.Empty(_vm.Games);
        Assert.Equal("AsterCache: 서비스 실행 중 · 등록된 게임 없음", _vm.StatusText);
        Assert.True(_vm.AddGame(Game("again")).Ok);
    }

    [Fact]
    public void SaveDeniedByFolderPermissionsAsksForAdministratorAndKeepsConfig()
    {
        Assert.True(_vm.AddGame(Game("a")).Ok);
        byte[] before = File.ReadAllBytes(Path.Combine(_dir, "config.json"));
        Directory.CreateDirectory(Path.Combine(_dir, "config.json.tmp")); // 임시 파일 자리를 폴더로 막아 UnauthorizedAccessException 을 흉내

        ActionResult add = _vm.AddGame(Game("b") with { ExeNames = ["b.exe"] });
        ActionResult remove = _vm.RemoveGame("a");

        Assert.False(add.Ok);
        Assert.False(remove.Ok);
        Assert.Contains("관리자 권한으로 트레이를 다시 실행", add.Message);
        Assert.Contains("관리자 권한으로 트레이를 다시 실행", remove.Message);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(_dir, "config.json")));
    }

    [Fact]
    public void StreamNowStreamsTheSelectedGameOnly()
    {
        _vm.AddGame(Game("a"));
        _vm.AddGame(Game("b") with { ExeNames = ["b.exe"] });
        Assert.True(_vm.StreamNow("B").Ok);
        Assert.Equal(["b"], _streamer.Streamed);
        Assert.False(_vm.StreamNow("zzz").Ok);
    }

    [Fact]
    public void StreamFailureIsReportedNotThrown()
    {
        _vm.AddGame(Game());
        _streamer.Fail = true;
        ActionResult r = _vm.StreamNow("mw4");
        Assert.False(r.Ok);
        Assert.Contains("스트림 실패", r.Message);
    }

    [Fact]
    public void ServiceRequestsAndCancelledUac()
    {
        Assert.True(_vm.StartService().Ok);
        Assert.Equal(1, _service.Starts);
        _service.Cancel = true;
        ActionResult r = _vm.StopService();
        Assert.False(r.Ok);
        Assert.Contains("취소", r.Message);
    }

    [Fact]
    public void ServiceVersionDifferingFromTrayShowsReinstallNeeded()
    {
        var vm = new TrayViewModel(new ConfigEditor(_dir), _service, _streamer, _shell, new MemoryLog(), trayVersion: "1.3.0.0");
        _service.State = ServiceState.Running;
        _service.Version = "1.2.0.0";
        vm.Refresh();
        Assert.True(vm.ServiceReinstallNeeded);
        Assert.Contains("서비스 재설치 필요 (서비스 1.2.0.0, 트레이 1.3.0.0)", vm.StatusText);

        _service.Version = "1.3.0"; // 4번째 자리가 없어도 같은 버전
        vm.Refresh();
        Assert.False(vm.ServiceReinstallNeeded);
        Assert.DoesNotContain("재설치", vm.StatusText);

        _service.Version = null; // 읽지 못하면 비교하지 않음
        vm.Refresh();
        Assert.False(vm.ServiceReinstallNeeded);
        _service.Version = "9.9.9.9";
        _service.State = ServiceState.NotInstalled; // 서비스가 없으면 "미설치"가 먼저
        vm.Refresh();
        Assert.False(vm.ServiceReinstallNeeded);
    }

    [Fact]
    public async Task AppliedUpdateNeverTouchesTheService()
    {
        var manager = new FakeUpdates();
        var vm = new TrayViewModel(new ConfigEditor(_dir), _service, _streamer, _shell, new MemoryLog(), new UpdateService(manager, new MemoryLog()), "1.0.0.0");
        _service.State = ServiceState.Running;
        _service.Version = "1.0.0.0";

        Assert.True((await vm.CheckUpdateAsync()).Ok);
        ActionResult applied = await vm.ApplyUpdateAsync(CancellationToken.None);

        Assert.True(applied.Ok);
        Assert.Contains("서비스는 설치 프로그램으로", applied.Message);
        Assert.True(manager.Applied);
        Assert.Equal((0, 0), (_service.Starts, _service.Stops)); // 서비스 시작·정지·교체 요청 없음
    }

    [Fact]
    public void OpenLogsOpensLogsFolder()
    {
        Assert.True(_vm.OpenLogs().Ok);
        Assert.Equal(Path.Combine(_dir, "logs"), _shell.Opened);
    }

    [Fact]
    public async Task UpdateDisabledByDefault()
    {
        ActionResult r = await _vm.CheckUpdateAsync();
        Assert.False(r.Ok);
        Assert.Contains("비활성", r.Message);
    }

    private sealed class FakeService : IServiceControl
    {
        public ServiceState State { get; set; } = ServiceState.Stopped;

        public int Starts { get; private set; }

        public int Stops { get; private set; }

        public string? Version { get; set; }

        public bool Cancel { get; set; }

        public ServiceState Query() => State;

        public string? InstalledVersion() => Version;

        public void RequestStart() => Starts++;

        public void RequestStop()
        {
            Stops++;
            Throw();
        }

        private void Throw()
        {
            if (Cancel)
            {
                throw new System.ComponentModel.Win32Exception(1223);
            }
        }
    }

    private sealed class FakeUpdates : IUpdateManager
    {
        public bool Applied { get; private set; }

        public bool IsInstalled => true;

        public Task<string?> CheckForUpdatesAsync() => Task.FromResult<string?>("2.0.0");

        public Task DownloadUpdatesAsync(Action<int> progress, CancellationToken cancellationToken) => Task.CompletedTask;

        public void ApplyUpdatesAndRestart() => Applied = true;
    }

    private sealed class FakeStreamer : IGameStreamer
    {
        public List<string> Streamed { get; } = [];

        public bool Fail { get; set; }

        public StreamStats Stream(GameSettings game)
        {
            if (Fail)
            {
                throw new IOException("boom");
            }

            Streamed.Add(game.Name);
            return new StreamStats(1 << 20, 1, 0, 0.01);
        }
    }

    private sealed class FakeShell : IShell
    {
        public string? Opened { get; private set; }

        public void Open(string path) => Opened = path;
    }

    private sealed class MemoryLog : ILog
    {
        public void Write(LogLevel level, string message, Exception? exception = null)
        {
        }
    }
}
