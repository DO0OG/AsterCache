using AsterCache.Cli.Commands;
using AsterCache.Core.Json;
using AsterCache.Core.Settings;
using AsterCache.Core.Tier;
using AsterCache.Core.Trace;
using AsterCache.Tests.Core;
using AsterCache.Tests.Service;
using AsterCache.Tests.Windows;
using AsterCache.Windows;
using Xunit;
using Xunit.Abstractions;

namespace AsterCache.Tests.Cli;

/// aster tier status|apply|revert|clean: 출력 형식 (한 줄 JSON), 거부 사유, 관리자 요구, 되돌림·정리. 파일 시스템은 메모리 가짜이고 (실제 링크 왕복 하나만 임시 폴더 + 링크 권한이 있을 때),
/// 데이터 폴더는 임시 폴더입니다 (ProgramData 의 실제 설정·저널은 쓰지 않음).
public sealed class TierCommandTests : IDisposable
{
    private readonly (Func<bool> Admin, Func<ITierFs> Fs, Func<string, bool?> Hdd, Func<GameSettings, bool> Running, Func<GameSettings, string?> Cheat, Func<string> Sys, Func<SsdTierSettings, ITierFs, TierRootChoice> Root) _saved =
        (TierCommand.IsAdmin, TierCommand.Fs, TierCommand.IsHdd, TierCommand.Running, TierCommand.AntiCheatOf, TierCommand.SystemDrive, TierCommand.ResolveRoot);

    private readonly Func<bool> _savedVanguard = AntiCheatGuard.VanguardInstalled;
    private readonly ITestOutputHelper _output;
    private readonly TempDir _dir = new();
    private readonly MemTierFs _fs = new();
    private readonly string _data;
    private readonly string _root;
    private readonly string _tierRoot;
    private readonly string _plans;

    public TierCommandTests(ITestOutputHelper output)
    {
        _output = output;
        _data = _dir.Combine("data");
        _plans = Path.Combine(_data, "plans");
        _root = Path.Combine(_dir.Path, "Games", "Foo");
        _tierRoot = Path.Combine(_dir.Path, "ssd");
        Directory.CreateDirectory(_plans);
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_tierRoot);
        TierCommand.IsAdmin = () => true;
        TierCommand.Fs = () => _fs;
        TierCommand.IsHdd = _ => true;
        TierCommand.Running = _ => false;
        TierCommand.AntiCheatOf = _ => null;
        TierCommand.SystemDrive = () => "Z:";
        TierCommand.ResolveRoot = (t, _) => new TierRootChoice(@"S:\AsterCache\tier", null);
        WriteConfig(enabled: true);
    }

    public void Dispose()
    {
        (TierCommand.IsAdmin, TierCommand.Fs, TierCommand.IsHdd, TierCommand.Running, TierCommand.AntiCheatOf, TierCommand.SystemDrive, TierCommand.ResolveRoot) = _saved;
        AntiCheatGuard.VanguardInstalled = _savedVanguard;
        _dir.Dispose();
    }

    private void WriteConfig(bool enabled, string? root = null)
    {
        var config = new JObject
        {
            { "games", new List<object?> { new JObject
                {
                    { "name", "Foo" }, { "exe_names", new List<object?> { "foo.exe" } }, { "trace", Path.Combine(_plans, "Foo.prefetch.json") }, { "plan", Path.Combine(_plans, "Foo.plan.json") },
                    { "origin_root", _root }, { "allowed_roots", new List<object?> { _root } }, { "volume_label", "GAMES" },
                } } },
            { "auto", new JObject { { "ssd_tier", new JObject { { "enabled", enabled }, { "budget_gib", 1L }, { "root", root ?? "auto" } } } } },
        };
        File.WriteAllText(Path.Combine(_data, "config.json"), PyJson.Dumps(config));
    }

    private void WriteTrace(params string[] files) => File.WriteAllText(Path.Combine(_plans, "Foo.trace.json"),
        PyJson.Dumps(files.Select(f => (object?)new TraceEvent(f, 0, 4096).ToJson()).ToList(), ensureAscii: false));

    private (int Code, string Text, JObject Json) Run(params string[] args)
    {
        (int code, string text, _) = CliTests.Run(args);
        return (code, text, (JObject)JsonRead.Parse(text.Trim().Split('\n')[^1])!);
    }

    private (int Code, string Text, JObject Json) Tier(params string[] args) => Run([.. args, "--data-dir", _data]);

    // 엔진으로 두 파일을 적용해 둡니다 (CLI 가 같은 데이터 폴더의 저널을 읽음).
    private (string A, string B, byte[] DataA) Applied()
    {
        string a = @"D:\Games\Foo\a.bin", b = @"D:\Games\Foo\b.bin";
        byte[] data = new byte[3000];
        new Random(5).NextBytes(data);
        _fs.Put(a, data);
        _fs.Put(b, new byte[2000]);
        var g = new GameSettings("Foo", ["foo.exe"], "x.json", "y.json", @"D:\Games\Foo", [@"D:\Games"], "GAMES", 60);
        var engine = new TierEngine(_fs, new ListLog(), Path.Combine(_data, "tier"));
        TierSelection sel = new([new TierPick(a, 3000, 3000), new TierPick(b, 2000, 2000)], 5000, 0, 0, []);
        Assert.Equal(2, engine.Apply(g, @"S:\AsterCache\tier", 1, sel, default).Applied);
        return (a, b, data);
    }

    [Fact]
    public void StatusWithoutAnyConfigOrJournalPrintsTheEmptyShape()
    {
        File.Delete(Path.Combine(_data, "config.json"));

        (int code, string text, _) = Tier("tier", "status");

        Assert.Equal(0, code);
        Assert.Equal("""{"enabled":null,"budget_gib":null,"root":null,"root_error":null,"games":[]}""", text.Trim());
    }

    [Fact]
    public void StatusShowsTheSettingsTheRootAndEveryJournal()
    {
        Applied();

        (int code, _, JObject o) = Tier("tier", "status");

        Assert.Equal(0, code);
        Assert.Equal(["enabled", "budget_gib", "root", "root_error", "games"], o.Select(kv => kv.Key));
        Assert.Equal((true, 1L, @"S:\AsterCache\tier", null), (o["enabled"], o["budget_gib"], o["root"], o["root_error"]));
        var game = (JObject)((List<object?>)o["games"]!)[0]!;
        Assert.Equal(["game", "root", "active", "stale", "reverted", "active_bytes", "copy_bytes", "corrupt"], game.Select(kv => kv.Key));
        Assert.Equal(("Foo", 2L, 0L, 0L, 5000L, false), (game["game"], game["active"], game["stale"], game["reverted"], game["active_bytes"], game["corrupt"]));
        WriteConfig(enabled: false);
        Assert.Equal(false, Tier("tier", "status").Json["enabled"]);
    }

    [Fact]
    public void ApplyRefusalsEachSayWhy()
    {
        WriteTrace(Path.Combine(_root, "a.bin"));
        File.WriteAllBytes(Path.Combine(_root, "a.bin"), new byte[4096]);

        TierCommand.SystemDrive = () => Path.GetPathRoot(_root)![..2].ToUpperInvariant();
        Assert.Equal((1, "system_volume"), Refusal());

        TierCommand.SystemDrive = () => "Z:";
        TierCommand.IsHdd = _ => false;
        Assert.Equal((1, "not_an_hdd"), Refusal());

        TierCommand.IsHdd = _ => true;
        TierCommand.Running = _ => true;
        Assert.Equal((1, "game_running"), Refusal());

        TierCommand.Running = _ => false;
        TierCommand.AntiCheatOf = _ => "eac";
        (int code, string error, string? detail) = RefusalDetail();
        Assert.Equal((1, "anti_cheat", "eac"), (code, error, detail));

        TierCommand.AntiCheatOf = _ => null;
        TierCommand.ResolveRoot = (_, _) => new TierRootChoice(null, "no SSD found");
        Assert.Equal((1, "no_ssd_root"), Refusal());

        TierCommand.ResolveRoot = (_, _) => new TierRootChoice(@"S:\AsterCache\tier", null);
        File.Delete(Path.Combine(_plans, "Foo.trace.json"));
        Assert.Equal((1, "no_trace"), Refusal());

        WriteConfig(enabled: false);
        Assert.Equal((1, "ssd_tier_disabled"), Refusal());
        Assert.Empty(_fs.Links);
        Assert.Equal(0, _fs.Mutations);
    }

    [Fact]
    public void ApplyAppliesTheRiotRuleToSettingsItLoads()
    {
        WriteTrace(Path.Combine(_root, "a.bin"));
        string config = Path.Combine(_data, "config.json");
        File.WriteAllText(config, File.ReadAllText(config).Replace("foo.exe", "LeagueClient.exe", StringComparison.Ordinal)); // 수동 등록한 리그 오브 레전드: anticheat 값이 비어 있음
        TierCommand.AntiCheatOf = g => g.AntiCheat; // 설정에서 채운 값만 봄
        AntiCheatGuard.VanguardInstalled = () => true;

        Assert.Equal((1, "anti_cheat", "vanguard"), RefusalDetail());

        AntiCheatGuard.VanguardInstalled = () => false;
        (_, _, JObject applied) = Tier("tier", "apply", "--game", "Foo");
        Assert.False(applied.TryGetValue("error", out object? error) && error as string == "anti_cheat");
    }

    private (int Code, string Error) Refusal()
    {
        (int code, string error, _) = RefusalDetail();
        return (code, error);
    }

    private (int Code, string Error, string? Detail) RefusalDetail()
    {
        (int code, _, JObject o) = Tier("tier", "apply", "--game", "Foo");
        return (code, (string)o["error"]!, o.TryGetValue("detail", out object? d) ? d as string : null);
    }

    [Fact]
    public void ApplyOfAnUnknownGameIsAValueError()
    {
        (int code, _, JObject o) = Tier("tier", "apply", "--game", "Nope");

        Assert.Equal((1, "ValueError"), (code, (string)o["error"]!));
    }

    [Fact]
    public void ApplyWithNothingSelectableReportsAnEmptyRun()
    {
        File.WriteAllBytes(Path.Combine(_root, "a.bin"), new byte[4096]);
        WriteTrace(Path.Combine(_root, "a.bin")); // 메모리 파일 시스템에는 없는 파일: 크기를 읽지 못해 고르지 않음

        (int code, _, JObject o) = Tier("tier", "apply", "--game", "Foo");

        Assert.Equal(0, code);
        Assert.Equal(["game", "root", "selected", "selected_bytes", "applied", "copied_bytes", "skipped", "stopped"], o.Select(kv => kv.Key));
        Assert.Equal(("Foo", @"S:\AsterCache\tier", 0L, 0L, 0L, 0L, null), (o["game"], o["root"], o["selected"], o["selected_bytes"], o["applied"], o["copied_bytes"], o["stopped"]));
        Assert.Empty((List<object?>)o["skipped"]!);
    }

    [Fact]
    public void ApplyRevertAndCleanNeedAdministratorButStatusDoesNot()
    {
        TierCommand.IsAdmin = () => false;

        foreach (string[] args in new[] { new[] { "tier", "apply", "--game", "Foo" }, ["tier", "revert", "--all"], ["tier", "clean"] })
        {
            (int code, string text, _) = Tier(args);
            Assert.Equal((3, """{"error":"not_admin"}"""), (code, text.Trim()));
        }

        Assert.Equal(0, Tier("tier", "status").Code);
    }

    [Fact]
    public void RevertNeedsExactlyOneOfGameOrAll()
    {
        (int code, _, string err) = CliTests.Run("tier", "revert", "--data-dir", _data);
        Assert.Equal(2, code);
        Assert.Contains("--game or --all", err);

        (code, _, err) = CliTests.Run("tier", "revert", "--game", "Foo", "--all", "--data-dir", _data);
        Assert.Equal(2, code);
        Assert.Contains("--game or --all", err);
    }

    [Fact]
    public void RevertGameThenRevertAllThenCleanRestoreTheOriginalsAndDropTheCopies()
    {
        (string a, string b, byte[] data) = Applied();

        (int code, _, JObject o) = Tier("tier", "revert", "--game", "Foo");

        Assert.Equal(0, code);
        Assert.Equal(["reverted", "failed", "games"], o.Select(kv => kv.Key));
        Assert.Equal((2L, 0L), (o["reverted"], o["failed"]));
        var row = (JObject)((List<object?>)o["games"]!)[0]!;
        Assert.Equal(["game", "reverted", "stale", "failed", "corrupt"], row.Select(kv => kv.Key));
        Assert.Empty(_fs.Links);
        Assert.Equal(data, _fs.Files[MemTierFs.N(a)]);
        Assert.True(_fs.Files.ContainsKey(MemTierFs.N(b)));

        Assert.Equal(0L, Tier("tier", "revert", "--all").Json["reverted"]); // 이미 되돌렸으면 할 일 없음

        (code, _, o) = Tier("tier", "clean");
        Assert.Equal((0, 2L, 5000L), (code, o["deleted_files"], o["freed_bytes"]));
        Assert.Equal(["deleted_files", "freed_bytes"], o.Select(kv => kv.Key));
        Assert.Equal(data, _fs.Files[MemTierFs.N(a)]); // 원본은 그대로
    }

    [Fact]
    public void RevertAllWithACorruptJournalTouchesNothingAndExitsNonZero()
    {
        Applied();
        File.WriteAllText(Path.Combine(_data, "tier", "Foo.json"), "not json");
        int mutations = _fs.Mutations;

        (int code, _, JObject o) = Tier("tier", "revert", "--all");

        Assert.Equal((1, 1L), (code, o["failed"]));
        Assert.True((bool)((JObject)((List<object?>)o["games"]!)[0]!)["corrupt"]!);
        Assert.Equal(mutations, _fs.Mutations);
        Assert.Equal(2, _fs.Links.Count);
    }

    [Fact]
    public void ApplyAndRevertWithRealFilesAndRealLinksWhenThePrivilegeIsAvailable()
    {
        string probe = _dir.Combine("probe.lnk");
        File.WriteAllBytes(_dir.Combine("probe.bin"), new byte[10]);
        try
        {
            File.CreateSymbolicLink(probe, _dir.Combine("probe.bin"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _output.WriteLine("SKIPPED CLI real-link round trip: cannot create symbolic links here (" + e.GetType().Name + ")");
            return;
        }

        File.Delete(probe);
        byte[] a = new byte[(1 << 20) + 5];
        new Random(8).NextBytes(a);
        string pa = Path.Combine(_root, "a.bin");
        File.WriteAllBytes(pa, a);
        WriteTrace(pa);
        TierCommand.Fs = () => new TierFs();
        TierCommand.ResolveRoot = (_, _) => new TierRootChoice(_tierRoot, null);

        (int code, _, JObject o) = Tier("tier", "apply", "--game", "Foo");

        if (o["skipped"] is List<object?> { Count: > 0 } skipped && ((JObject)skipped[0]!)["reason"] is "reparse_point")
        {
            _output.WriteLine("SKIPPED CLI real-link round trip: the temp folder sits under a reparse point");
            return;
        }

        _output.WriteLine("RAN CLI real-link round trip: " + o["applied"] + " applied");
        Assert.Equal((0, 1L, (long)a.Length), (code, o["applied"], o["copied_bytes"]));
        Assert.NotNull(new FileInfo(pa).LinkTarget);
        Assert.Equal(a, File.ReadAllBytes(pa));

        (code, _, o) = Tier("tier", "revert", "--all");

        Assert.Equal((0, 1L), (code, o["reverted"]));
        Assert.Null(new FileInfo(pa).LinkTarget);
        Assert.Equal(a, File.ReadAllBytes(pa));
    }
}
