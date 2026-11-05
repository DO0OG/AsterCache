using System.Text.RegularExpressions;
using AsterCache.Core.Logging;
using AsterCache.Core.Settings;

namespace AsterCache.Service;

/// 감시 간격 (시험은 짧게).
public sealed record RiotWatchTiming
{
    /// 게임 흐름 단계를 묻는 간격 (챔피언 선택 중에도 같음).
    public TimeSpan PhasePoll { get; init; } = TimeSpan.FromSeconds(3);

    /// 챔피언 선택 세션을 묻는 간격.
    public TimeSpan ChampPoll { get; init; } = TimeSpan.FromSeconds(2);

    /// 설정이 꺼졌거나 클라이언트가 없을 때 다시 보는 간격 (이때는 클라이언트 API를 부르지 않음).
    public TimeSpan Idle { get; init; } = TimeSpan.FromSeconds(10);

    /// 요청 하나의 시간 초과.
    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// 연속 실패 때 간격을 2 배씩 늘리는 상한.
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(60);
}

/// 챔피언 선택 선읽기가 바깥 세계에 닿는 곳 전부. 시험은 가짜로 바꿉니다.
public sealed class RiotWatchEnv
{
    public RiotWatchTiming Timing { get; init; } = new();

    /// LeagueClient.exe 가 실행 중인가.
    public Func<bool> ClientRunning { get; init; } = () => ProcessSnapshot.IsRunning([RiotPhaseWatcher.ClientExe]);

    /// 리그 오브 레전드 게임 exe (클라이언트 제외) 가 실행 중인가.
    public Func<GameSettings, bool> GameExeRunning { get; init; } = g => ProcessSnapshot.IsRunning([.. g.ExeNames.Where(e => !RiotPhaseWatcher.IsClientExe(e))]);

    /// lockfile 의 PID 가 지금도 LeagueClient.exe 인가 (남은 옛 lockfile 이나 재사용된 PID 를 거름).
    public Func<Lockfile, bool> ClientAlive { get; init; } = l => RiotPhaseWatcher.IsClientExe(ProcessSnapshot.ImageName(l.Pid));

    /// lockfile 내용 (없거나 읽을 수 없으면 null).
    public Func<string, string?> ReadLockfile { get; init; } = RiotPhaseWatcher.ReadLockfileText;

    public Func<Lockfile, ILcuClient> CreateClient { get; init; } = l => new LcuHttpClient(l, new RiotWatchTiming().HttpTimeout);

    /// (게임, 파일들, 예산 바이트, 취소) -> 읽은 바이트. 진짜는 Prewarmer.WarmFiles.
    public required Func<GameSettings, IReadOnlyList<string>, long, CancellationToken, long> WarmFiles { get; init; }

    public Func<string, IReadOnlyList<string>> Locales { get; init; } = RiotPhaseWatcher.DetectLocales;

    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = static (t, ct) => Task.Delay(t, ct);

    public static RiotWatchEnv Create(Prewarmer prewarm) => new() { WarmFiles = prewarm.WarmFiles };
}

/// 리그 오브 레전드 챔피언 선택 선읽기 (설정 riot.champ_select_prewarm, 기본 꺼짐). Worker 가 별도 비동기 루프로 돌립니다 (30 초 재배치 루프·게임 시작 처리와 따로).
/// 켜져 있고, 등록 게임 중 안티치트가 vanguard 이고 LeagueClient.exe 가 있는 게임이 있고, 클라이언트 실행 중일 때만 lockfile 로 로컬 API 에 접속해 단계를 봅니다.
/// ChampSelect 동안 잠긴 챔피언 → 호버 챔피언 → 맵 순서로 데이터 파일을 WarmFiles 로 읽고, ChampSelect 를 벗어나거나 게임 exe 가 뜨면 즉시 취소합니다.
public sealed class RiotPhaseWatcher
{
    public const string ClientExe = "LeagueClient.exe";
    public const string ChampSelect = "ChampSelect";

    private readonly Func<AsterSettings> _settings;
    private readonly RiotWatchEnv _env;
    private readonly ILog _log;
    private readonly RiotWatchTiming _timing;
    private readonly object _sync = new(); // _current 보호 (NoteProcessStart 는 다른 스레드)
    private readonly HashSet<string> _logged = new(StringComparer.OrdinalIgnoreCase); // 이번 실패 구간에서 이미 로그한 종류, RunAsync 스레드만
    private CancellationTokenSource? _current;
    private Task? _task;
    private volatile bool _suppressed; // 게임 exe 시작으로 취소한 뒤 단계가 ChampSelect 를 벗어날 때까지 새 세션을 열지 않음
    private Lockfile? _creds;
    private ILcuClient? _client;
    private IReadOnlyDictionary<int, string>? _aliases; // 클라이언트를 만든 뒤 처음 챔피언 선택 때 한 번 받음
    private int _failures;

    public RiotPhaseWatcher(Func<AsterSettings> settings, RiotWatchEnv env, ILog log)
    {
        _settings = settings;
        _env = env;
        _log = log;
        _timing = env.Timing;
    }

    public static bool IsClientExe(string? exe) => string.Equals(exe, ClientExe, StringComparison.OrdinalIgnoreCase);

    /// 등록 게임 중 안티치트가 vanguard 이고 LeagueClient.exe 를 가진 게임 (리그 오브 레전드).
    public static GameSettings? FindGame(AsterSettings s) => s.Games.FirstOrDefault(g => g.AntiCheat == "vanguard" && g.ExeNames.Any(IsClientExe));

    public static string? ReadLockfileText(string path)
    {
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            byte[] buf = new byte[1024];
            int n = f.Read(buf, 0, buf.Length);
            return System.Text.Encoding.UTF8.GetString(buf, 0, n);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static readonly Regex LocalePattern = new("^[A-Za-z]{2,3}_[A-Za-z0-9]{2,4}$", RegexOptions.CultureInvariant);

    /// FINAL 폴더에 실제 있는 Global.<locale>.wad.client 의 locale 들 (예: ko_KR). 없으면 빈 목록.
    public static IReadOnlyList<string> DetectLocales(string finalDir)
    {
        try
        {
            return [.. Directory.EnumerateFiles(finalDir, "Global.*.wad.client").Select(f => Path.GetFileName(f)["Global.".Length..^".wad.client".Length]).Where(l => LocalePattern.IsMatch(l)).Order(StringComparer.Ordinal)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// 챔피언 데이터 파일: <alias>.wad.client 와 locale 별 <alias>.<locale>.wad.client. alias 는 영숫자만 (아니면 빈 목록).
    public static IReadOnlyList<string> ChampionFiles(string finalDir, string alias, IReadOnlyList<string> locales) =>
        LcuHttpClient.IsAlias(alias) ? Names(Path.Combine(finalDir, "Champions"), alias, locales) : [];

    /// 맵 데이터 파일: Map<id>.wad.client 와 locale 별 Map<id>.<locale>.wad.client.
    public static IReadOnlyList<string> MapFiles(string finalDir, int mapId, IReadOnlyList<string> locales) =>
        mapId > 0 ? Names(Path.Combine(finalDir, "Maps", "Shipping"), "Map" + mapId.ToString(System.Globalization.CultureInfo.InvariantCulture), locales) : [];

    private static List<string> Names(string dir, string stem, IReadOnlyList<string> locales) =>
        [Path.Combine(dir, stem + ".wad.client"), .. locales.Select(l => Path.Combine(dir, stem + "." + l + ".wad.client"))];

    /// 게임 exe 가 떴을 때 (감시기 스레드): 리그 오브 레전드 게임 exe 이면 도는 선읽기를 바로 취소하고, 단계가 ChampSelect 를 벗어날 때까지 새로 열지 않습니다.
    public void NoteProcessStart(string exe)
    {
        if (FindGame(_settings()) is not { } g || IsClientExe(exe) || !g.ExeNames.Contains(exe, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        _suppressed = true;
        lock (_sync)
        {
            _current?.Cancel();
        }
    }

    /// 서비스가 도는 동안의 루프. ct 가 취소되면 세션을 마치고 깨끗이 끝납니다 (예외를 밖으로 내지 않음).
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TimeSpan wait;
                try
                {
                    wait = await StepAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e)
                {
                    LogOnce("step", $"champion select prewarm step failed: {e.GetType().Name}");
                    wait = _timing.Idle;
                }

                await _env.Delay(wait, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 종료
        }
        finally
        {
            await EndSessionAsync();
            DropClient();
        }
    }

    // 한 번의 판단: 다음까지 기다릴 시간. 설정이 꺼졌거나 클라이언트가 없으면 API 를 부르지 않습니다.
    private async Task<TimeSpan> StepAsync(CancellationToken ct)
    {
        AsterSettings s = _settings();
        GameSettings? game = s.Riot.ChampSelectPrewarm ? FindGame(s) : null;
        if (game is null || !_env.ClientRunning())
        {
            await EndSessionAsync();
            DropClient();
            return _timing.Idle;
        }

        if (_env.GameExeRunning(game))
        {
            await EndSessionAsync();
            return _timing.PhasePoll;
        }

        Lockfile? creds = Lockfile.Parse(_env.ReadLockfile(Path.Combine(game.OriginRoot, "lockfile")));
        if (creds is null || !_env.ClientAlive(creds))
        {
            LogOnce("lockfile", $"{game.Name}: client lockfile is missing, malformed or stale, champion select prewarm waits");
            await EndSessionAsync();
            DropClient();
            return _timing.Idle;
        }

        if (_client is null || _creds != creds)
        {
            await EndSessionAsync();
            DropClient(); // lockfile 이 바뀌면 (클라이언트 재시작) 새 자격으로
            (_creds, _client) = (creds, _env.CreateClient(creds));
        }

        string? phase;
        try
        {
            phase = await _client.GetPhaseAsync(ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            _failures++;
            LogOnce("api", $"{game.Name}: client API is not reachable ({e.GetType().Name}), champion select prewarm retries with backoff");
            await EndSessionAsync();
            return Backoff();
        }

        _failures = 0;
        _logged.Clear();
        if (phase == ChampSelect)
        {
            if (_task is null && !_suppressed)
            {
                StartSession(game, _client, (long)s.Riot.ChampSelectBudgetMib << 20);
            }
        }
        else
        {
            _suppressed = false;
            await EndSessionAsync();
        }

        return _timing.PhasePoll;
    }

    private TimeSpan Backoff()
    {
        TimeSpan d = _timing.PhasePoll * Math.Pow(2, Math.Min(_failures, 16));
        return d > _timing.MaxBackoff ? _timing.MaxBackoff : d;
    }

    private void LogOnce(string kind, string message)
    {
        if (_logged.Add(kind))
        {
            _log.Info(message);
        }
    }

    private void DropClient()
    {
        _client?.Dispose();
        (_client, _creds, _aliases) = (null, null, null);
    }

    private void StartSession(GameSettings game, ILcuClient client, long budgetBytes)
    {
        var cts = new CancellationTokenSource();
        lock (_sync)
        {
            _current = cts;
        }

        _task = Task.Run(() => SessionAsync(game, client, budgetBytes, cts.Token), CancellationToken.None);
    }

    // 세션을 취소하고 끝나기를 기다린 뒤 해제합니다 (취소 신호는 _current 를 비운 뒤에는 닿지 않으므로 Dispose 경합이 없음).
    private async Task EndSessionAsync()
    {
        CancellationTokenSource? cts;
        lock (_sync)
        {
            (cts, _current) = (_current, null);
        }

        Task? task = _task;
        _task = null;
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        if (task is not null)
        {
            try
            {
                await task;
            }
            catch (Exception)
            {
                // 세션은 예외를 밖으로 내지 않지만, 만약을 위해
            }
        }

        cts.Dispose();
    }

    private async Task SessionAsync(GameSettings game, ILcuClient client, long budget, CancellationToken ct)
    {
        long remaining = budget;
        try
        {
            string final = Path.Combine(game.OriginRoot, "Game", "DATA", "FINAL");
            IReadOnlyList<string> locales = _env.Locales(final);
            IReadOnlyDictionary<int, string> aliases = await AliasesAsync(game, client, ct);
            var champions = new HashSet<int>();
            bool mapDone = false;
            _log.Info($"{game.Name}: champion select prewarm enabled (disabled by default), started (budget {budget >> 20} MiB)");
            while (!ct.IsCancellationRequested && remaining > 0)
            {
                if (await client.GetChampSelectAsync(ct) is not { } state)
                {
                    break; // 챔피언 선택이 끝남
                }

                foreach (int id in state.Locked.Concat(state.Hovered))
                {
                    if (ct.IsCancellationRequested || remaining <= 0)
                    {
                        break;
                    }

                    if (champions.Add(id) && aliases.TryGetValue(id, out string? alias))
                    {
                        remaining -= _env.WarmFiles(game, ChampionFiles(final, alias, locales), remaining, ct);
                    }
                }

                if (!mapDone && remaining > 0 && !ct.IsCancellationRequested && await client.GetMapIdAsync(ct) is { } mapId)
                {
                    mapDone = true;
                    remaining -= _env.WarmFiles(game, MapFiles(final, mapId, locales), remaining, ct);
                }

                await _env.Delay(_timing.ChampPoll, ct);
            }

            _log.Info($"{game.Name}: champion select prewarm {(ct.IsCancellationRequested ? "cancelled" : "finished")} ({(budget - remaining) >> 20} MiB read)");
        }
        catch (OperationCanceledException)
        {
            _log.Info($"{game.Name}: champion select prewarm cancelled ({(budget - remaining) >> 20} MiB read)"); // 취소는 예외를 밖으로 내지 않음
        }
        catch (Exception e)
        {
            _log.Info($"{game.Name}: champion select prewarm stopped ({e.GetType().Name})");
        }
    }

    // 챔피언 id -> alias 는 클라이언트를 만든 뒤 처음 한 번만 받습니다. 못 받으면 챔피언 없이 맵만 (다음 세션에 다시 시도).
    private async Task<IReadOnlyDictionary<int, string>> AliasesAsync(GameSettings game, ILcuClient client, CancellationToken ct)
    {
        if (_aliases is { } cached)
        {
            return cached;
        }

        try
        {
            return _aliases = await client.GetChampionAliasesAsync(ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            _log.Info($"{game.Name}: champion list not available ({e.GetType().Name}), reading the map only");
            return new Dictionary<int, string>();
        }
    }
}
