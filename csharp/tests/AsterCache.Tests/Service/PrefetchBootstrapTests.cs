using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Service;
using AsterCache.Tests.Core;
using AsterCache.Tests.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

public sealed class SystemProbesTests
{
    [Theory]
    [InlineData(true, 3, true)]
    [InlineData(true, 1, true)] // 응용 프로그램 비트만
    [InlineData(true, 2, false)] // 부팅 비트만
    [InlineData(true, 0, false)]
    [InlineData(false, 3, false)] // SysMain 이 멈춤
    [InlineData(true, null, false)] // 값을 읽지 못함
    public void PrefetcherIsOnOnlyWhenSysMainRunsAndTheApplicationBitIsSet(bool running, int? value, bool expected) =>
        Assert.Equal(expected, SystemProbes.PrefetcherOn(() => running, () => value));

    [Fact]
    public void RealProbeReadsWithoutThrowing() => _ = SystemProbes.PrefetcherOn();
}

/// 부트스트랩 S2 의 파일 쪽: 임시 Prefetch 폴더의 .pf 로 plans\이름.boot.trace.json 을 만들고, 이미 있는 원시·boot 는 덮지 않습니다.
public sealed class PrefetchBootstrapTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly GameSettings _game;
    private readonly IReadOnlyDictionary<uint, string> _letters;
    private readonly string _pfs;
    private readonly string _gameRoot;

    public PrefetchBootstrapTests()
    {
        _gameRoot = _dir.Combine("game");
        Directory.CreateDirectory(_gameRoot);
        Directory.CreateDirectory(_dir.Combine("plans"));
        File.WriteAllBytes(Path.Combine(_gameRoot, "bench.exe"), new byte[4000]);
        File.WriteAllBytes(Path.Combine(_gameRoot, "a.pak"), new byte[9000]);
        string plan = _dir.Combine(@"plans\bench.plan.json");
        _game = new GameSettings("bench", ["bench.exe"], plan + ".prefetch.json", plan, _gameRoot, [_gameRoot], "GAMES", 1440);
        _letters = new Dictionary<uint, string> { [PfFixture.Serial] = _gameRoot[..2].ToUpperInvariant() };
        _pfs = _dir.Combine("Prefetch");
        Directory.CreateDirectory(_pfs);
        string rest = _gameRoot[2..].ToUpperInvariant();
        File.WriteAllBytes(Path.Combine(_pfs, "BENCH.EXE-00000001.pf"), PfFixture.Build("BENCH.EXE",
            [(PfFixture.Volume + rest + @"\BENCH.EXE", 0x200, 1), (PfFixture.Volume + rest + @"\A.PAK", 0, 1), (PfFixture.Volume + rest + @"\GONE.PAK", 0, 1)]));
    }

    public void Dispose() => _dir.Dispose();

    private int Boot(Func<bool>? on = null) => AutoTracer.BootFromPrefetch(_game, on ?? (() => true), _pfs, _letters);

    [Fact]
    public void WritesABootTraceMarkedAsPrefetchThatTheNormalLoaderReads()
    {
        Assert.Equal(2, Boot());

        string boot = AutoTracer.BootTracePath(_game);
        Assert.Equal("prefetch", AutoTracer.TraceSourceOf(boot));
        List<TraceEvent> rows = TraceLoader.LoadRanges(boot, _gameRoot);
        Assert.Equal([4000L, 9000L], rows.Select(r => r.Length));
        Assert.Equal([0L, 0L], rows.Select(r => r.TMs!.Value));
        Assert.False(File.Exists(boot + ".tmp"));
    }

    [Fact]
    public void PrefetcherOffMeansNothingIsWritten()
    {
        Assert.Equal(0, Boot(() => false));

        Assert.False(File.Exists(AutoTracer.BootTracePath(_game)));
    }

    [Fact]
    public void AnExistingBootTraceFromTheSharedProfileIsNeverOverwritten()
    {
        File.WriteAllText(AutoTracer.BootTracePath(_game), "[]");

        Assert.Equal(0, Boot());

        Assert.Equal("[]", File.ReadAllText(AutoTracer.BootTracePath(_game)));
        Assert.Null(AutoTracer.TraceSourceOf(AutoTracer.BootTracePath(_game)));
    }

    [Fact]
    public void AnExistingRawTraceMeansNothingIsWritten()
    {
        File.WriteAllText(AutoTracer.RawTracePath(_game), "[]");

        Assert.Equal(0, Boot());

        Assert.False(File.Exists(AutoTracer.BootTracePath(_game)));
    }

    [Fact]
    public void NoMatchingRecordWritesNothing()
    {
        File.Delete(Path.Combine(_pfs, "BENCH.EXE-00000001.pf"));

        Assert.Equal(0, Boot());

        Assert.False(File.Exists(AutoTracer.BootTracePath(_game)));
    }

    [Fact]
    public void TraceSourceOfIsNullForPlainListsMissingFilesAndGarbage()
    {
        string list = _dir.Combine("list.json");
        File.WriteAllText(list, "[]");
        string junk = _dir.Combine("junk.json");
        File.WriteAllText(junk, "not json");

        Assert.Null(AutoTracer.TraceSourceOf(list));
        Assert.Null(AutoTracer.TraceSourceOf(junk));
        Assert.Null(AutoTracer.TraceSourceOf(_dir.Combine("none.json")));
    }
}
