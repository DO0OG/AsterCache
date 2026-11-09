using AsterCache.Core.Discovery;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Service;
using AsterCache.Windows;

namespace AsterCache.Cli.Commands;

internal static class OrbitCommand
{
    internal static Func<string> DataDirectory { get; set; } = () => SettingsStore.DefaultDataDir;

    /// 부트스트랩 표본 사이 간격.
    internal static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(10);

    /// 표본 사이 대기 seam: 끝까지 기다렸으면 true, 취소되어 깼으면 false.
    internal static Func<TimeSpan, CancellationToken, bool> Wait { get; set; } = (span, ct) => !ct.WaitHandle.WaitOne(span);

    /// 보정기 생성 seam (데이터 폴더, 게임 활성 판정). 기본은 실제 비버퍼 읽기와 서비스가 쓰는 물리 디스크 키입니다.
    internal static Func<string, Func<GameSettings, bool>, OrbitCalibration> CalibrationFactory { get; set; } = (dataDir, gameActive) =>
        new OrbitCalibration(dataDir, new QuietLog(), OrbitCalibration.UnbufferedRead, () => DateTimeOffset.UtcNow, DiskActivity.IsBusy,
            gameActive, Random.Shared, diskKey: OrbitCalibration.PhysicalDiskKey);

    public static readonly ArgOption[] CalibrateOptions =
    [
        new("disk", Required: true),
        new("samples", Type: ArgType.Int, Default: "60"),
        new("data-dir"),
    ];

    /// 서비스를 멈추고 게임을 끈 상태에서 스트림 표본을 최대 N 개 (기본 60) 까지 10 초 간격으로 모아 disk_model.json 에 bootstrap 표시로 저장합니다.
    /// 표본마다 장치 구간이 생겼는지 보고 생기면 그 자리에서 멈춥니다 (구간에 지표별로 적합 절반과 보정 절반이 필요해 보통 40 개 전후).
    /// 읽기마다 서비스 정지와 등록 게임 미실행을 다시 확인하고, 어긋나면 이유를 내고 중단합니다 (종료 코드 1).
    public static int Calibrate(ParsedArgs a, TextWriter o)
    {
        string letter = PathText.Str(a.Text("disk")!).TrimEnd(':').ToUpperInvariant();
        if (letter.Length != 1 || !char.IsAsciiLetter(letter[0]))
        {
            throw new ArgException("--disk must be a drive letter such as D");
        }

        string disk = letter + ":";
        long samples = a.Int("samples") ?? 60;
        if (samples is < 1 or > 200)
        {
            throw new ArgException("--samples must be between 1 and 200");
        }

        string dataDir = Path.GetFullPath(a.Text("data-dir") is { } d ? PathText.Str(d) : SettingsStore.DefaultDataDir);
        IReadOnlyList<GameSettings> games = SettingsStore.Load(Path.Combine(dataDir, "config.json"), Path.Combine(dataDir, "plans")).Games;
        // 안티치트 게이트가 읽기를 막는 게임의 파일은 표본 후보에서 뺍니다
        List<GameSettings> onDisk = [.. games.Where(g => OrbitCalibration.DiskKey(g.OriginRoot) == disk
            && Capabilities.Decide(CapabilityFeature.NewPrefetch, AntiCheatOf(g)).Outcome == CapabilityOutcome.Allow)];
        if (onDisk.Count == 0)
        {
            throw new InvalidOperationException($"no registered game on {disk} allows calibration reads");
        }

        OrbitBenchCommand.EnsureIdle(games);
        // 계획(예열 목록)이 아직 없는 게임은 표본 파일을 고를 수 없으므로 건너뜁니다
        var opsOf = new Dictionary<string, List<ReadOp>>();
        foreach (GameSettings g in onDisk)
        {
            try
            {
                opsOf[g.Name] = OrbitBenchCommand.IncumbentProvider?.Invoke(g) ?? throw new InvalidOperationException("incumbent prewarm order is not available in this build");
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
            }
        }

        onDisk = [.. onDisk.Where(g => opsOf.ContainsKey(g.Name))];
        if (onDisk.Count == 0)
        {
            throw new InvalidOperationException($"no registered game on {disk} has a prewarm list yet");
        }
        // 표본마다 게임 하나를 돌려 쓰면 작은 목록이 같은 영역을 되풀이 읽어 드라이브 캐시를 재므로, 그 디스크 허용 게임 전부의 목록을 합친 풀에서 뽑습니다
        List<ReadOp> pool = [.. onDisk.SelectMany(g => opsOf[g.Name])];
        List<(GameSettings Game, string? AntiCheatKind)> gates = [.. onDisk.Select(g => (g, AntiCheatOf(g)))];
        OrbitCalibration calibration = CalibrationFactory(dataDir, _ => OrbitBenchCommand.Blocker(games) is not null);

        using var cts = new CancellationTokenSource();
        void OnCancel(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            cts.Cancel();
        }

        Console.CancelKeyPress += OnCancel;
        int read = 0;
        int done = 0;
        bool calibrated = false;
        string key = calibration.KeyOf(onDisk[0]);
        try
        {
            for (; done < samples; done++)
            {
                if ((done > 0 && !Wait(SampleInterval, cts.Token)) || cts.IsCancellationRequested)
                {
                    break;
                }

                OrbitBenchCommand.EnsureIdle(games); // 매 표본 전에 다시 확인
                long bytes = calibration.SampleBootstrap(gates, pool, cts.Token, out string? reason);
                read += bytes > 0 ? 1 : 0;
                var line = new JObject { { "sample", (long)(done + 1) }, { "of", samples }, { "games", (long)onDisk.Count }, { "bytes", bytes } };
                if (reason is not null)
                {
                    line.Add("reason", reason);
                }

                AsterCli.Emit(o, line);
                if (calibration.Envelope(key) is not null)
                {
                    calibrated = true;
                    done++;
                    break;
                }
            }
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
        }

        bool complete = calibrated || done == samples;
        (int seekN, int thrN) = calibration.SampleCounts(key);
        AsterCli.Emit(o, new JObject
        {
            { "disk", disk }, { "samples", (long)done }, { "recorded", (long)read }, { "cancelled", !complete },
            { "calibrated", calibrated }, { "seek_n", (long)seekN }, { "thr_n", (long)thrN },
        });
        return complete && read > 0 ? 0 : 1;
    }

    public static readonly ArgOption[] ModelOptions =
    [
        new("game", Required: true),
        new("data-dir"),
    ];

    /// 명령에서 모형을 만들 때의 시간 상한 (서비스의 백그라운드 구축 상한보다 넉넉히).
    internal static readonly TimeSpan ModelBudget = TimeSpan.FromSeconds(30);

    /// 서비스를 멈추고 게임을 끈 상태에서 기존 run 파일로 에피소드 모형을 동기로 한 번 만들어 plans 폴더의 (게임 이름).orbit.json 으로 씁니다.
    /// 새 빌드를 설치한 직후 다음 플레이 병합을 기다리지 않고 벤치 B 팔이 후보를 낼 수 있게 합니다.
    public static int Model(ParsedArgs a, TextWriter o)
    {
        string dataDir = Path.GetFullPath(a.Text("data-dir") is { } d ? PathText.Str(d) : SettingsStore.DefaultDataDir);
        string name = a.Text("game")!;
        IReadOnlyList<GameSettings> games = SettingsStore.Load(Path.Combine(dataDir, "config.json"), Path.Combine(dataDir, "plans")).Games;
        GameSettings game = games.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new PyValueException($"game not registered: {name}");

        OrbitBenchCommand.EnsureIdle(games); // 서비스가 돌거나 등록 게임이 실행 중이면 아무것도 쓰지 않고 중단
        if (!OrbitModel.Build(game, AutoTracer.MinExtraRunBytes, ModelBudget, CancellationToken.None))
        {
            throw new InvalidOperationException($"orbit model was not built: no usable run files, or the {ModelBudget.TotalSeconds:0} s limit passed");
        }

        OrbitModelSnapshot snapshot = OrbitModel.Load(game);
        return AsterCli.Emit(o, new JObject
        {
            { "game", game.Name },
            { "path", OrbitModel.PathFor(game) },
            { "episodes", (long)snapshot.Episodes.Count },
            { "runs", (long)snapshot.Runs },
            { "unmatched", snapshot.UnmatchedRatio },
        });
    }

    // 설정의 안티치트 값, 없으면 설치 폴더 검사 (열거 실패는 unknown 이라 막힘). 시험이 바꾸는 TierCommand 의 seam 과 섞이지 않게 따로 둡니다.
    private static string? AntiCheatOf(GameSettings game)
    {
        try
        {
            return game.AntiCheat ?? LauncherScan.DetectAntiCheat(game.OriginRoot);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return LauncherScan.AntiCheatUnknown;
        }
    }

    private sealed class QuietLog : ILog
    {
        public void Write(LogLevel level, string message, Exception? exception = null)
        {
        }
    }

    public static int Run(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Count == 1 && args[0] is "-h" or "--help")
        {
            stdout.WriteLine("usage: aster orbit mode off|shadow|active");
            return 0;
        }

        if (args.Count != 1 || args[0] is not ("off" or "shadow" or "active"))
        {
            stderr.WriteLine("usage: aster orbit mode off|shadow|active");
            return 2;
        }

        OrbitRuntime.WriteMode(Path.Combine(DataDirectory(), OrbitRuntime.ModeFileName), args[0]);
        return AsterCli.Emit(stdout, new AsterCache.Core.Json.JObject { { "mode", args[0] } });
    }
}
