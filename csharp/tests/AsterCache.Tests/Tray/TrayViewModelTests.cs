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
        Assert.Equal("AsterCache: 서비스 미설치 · 게임 0개", _vm.StatusText);
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
    public void RemoveUnknownOrLastGameIsRejected()
    {
        Assert.False(_vm.RemoveGame("nope").Ok);
        Assert.True(_vm.AddGame(Game()).Ok);
        ActionResult last = _vm.RemoveGame("mw4"); // 게임이 0개인 설정은 서비스가 읽지 못함
        Assert.False(last.Ok);
        Assert.Equal(["mw4"], _vm.Games);
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

        public bool Cancel { get; set; }

        public ServiceState Query() => State;

        public void RequestStart() => Starts++;

        public void RequestStop() => Throw();

        private void Throw()
        {
            if (Cancel)
            {
                throw new System.ComponentModel.Win32Exception(1223);
            }
        }
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
