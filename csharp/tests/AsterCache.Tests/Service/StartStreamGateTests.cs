using AsterCache.Core.Settings;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 시작 스트림의 "이미 실행 중" 판정: 등록 이름 프로세스만 보고, 나이는 서비스가 관측한 시작 시각으로 셈.
public sealed class StartStreamGateTests
{
    private static readonly GameSettings Game = Games.Make("cod", "cod.exe", "sp26-cod.exe");

    private readonly FakeStreamer _streamer = new();
    private readonly ListLog _log = new();
    private long _sec; // 시계 (초): 디스패처 시각과 관측 시각이 같은 값을 씀
    private IReadOnlyList<(int Pid, string Exe)> _processes = [];

    private GameDispatcher Dispatcher() => new([Game], _streamer, _log, () => DateTimeOffset.UnixEpoch.AddSeconds(_sec), new DispatchEnv
    {
        OtherProcesses = (g, s) => DispatchEnv.RegisteredOthers(g, _processes, s.Pid),
        Age = s => TimeSpan.FromSeconds(_sec - s.Timestamp),
    });

    private ProcessStart Start(int pid, string exe) => new(pid, exe, _sec);

    [Fact]
    public void UnregisteredResidentProcessUnderTheInstallFolderDoesNotBlockTheFirstStream()
    {
        GameDispatcher d = Dispatcher();
        _processes = [(900, "bootstrapper.exe"), (5, "cod.exe")]; // 폴더 아래 미등록 상주 프로세스: 오래 떠 있어도 무시

        Assert.True(d.Handle(Start(5, "cod.exe")));

        Assert.Single(_streamer.Calls);
    }

    [Fact]
    public void SecondLaunchOfARegisteredExeIsSkippedAndTheLogNamesTheFirstProcess()
    {
        GameDispatcher d = Dispatcher();
        _processes = [(5, "cod.exe")];
        Assert.True(d.Handle(Start(5, "cod.exe")));

        _sec = 100; // 첫 프로세스는 관측상 Settle 보다 오래됨
        _processes = [(5, "cod.exe"), (6, "cod.exe")];
        Assert.False(d.Handle(Start(6, "cod.exe")));

        Assert.Single(_streamer.Calls);
        Assert.Contains(_log.Lines, l => l.Contains("start stream skipped (game already running: cod.exe(5))", StringComparison.Ordinal));
    }

    [Fact]
    public void RegisteredProcessWithoutAnObservedStartIsTreatedAsAlreadyRunning()
    {
        GameDispatcher d = Dispatcher();
        _processes = [(5, "cod.exe"), (8, "SP26-COD.exe")]; // 8 은 서비스가 본 적 없음

        Assert.False(d.Handle(Start(5, "cod.exe")));

        Assert.Empty(_streamer.Calls);
        Assert.Contains(_log.Lines, l => l.Contains("game already running: SP26-COD.exe(8)", StringComparison.Ordinal));
    }

    [Fact]
    public void SiblingObservedWithinSettleDoesNotCountAsAlreadyRunning()
    {
        GameDispatcher d = Dispatcher();
        _processes = [(4, "cod.exe")];
        Assert.True(d.Handle(Start(4, "cod.exe")));

        _sec = 55; // 쿨다운 안: 스트림은 안 하지만 관측은 기록
        _processes = [(5, "sp26-cod.exe")];
        Assert.False(d.Handle(Start(5, "sp26-cod.exe")));

        _sec = 61; // 5 는 6 초 전 관측 (< Settle), 4 는 이미 끝남
        _processes = [(5, "sp26-cod.exe"), (6, "cod.exe")];
        Assert.True(d.Handle(Start(6, "cod.exe")));

        Assert.Equal(2, _streamer.Calls.Count);
    }

    [Fact]
    public void RegisteredOthersKeepsOnlyRegisteredNamesWithoutTheStarterAndTheService()
    {
        IReadOnlyList<(int Pid, string Exe)> others = DispatchEnv.RegisteredOthers(Game,
            [(1, "cod.exe"), (2, "COD.EXE"), (3, "helper.exe"), (Environment.ProcessId, "cod.exe")], 1);

        Assert.Equal([(2, "COD.EXE")], others);
    }
}
