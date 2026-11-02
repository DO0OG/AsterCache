using System.Diagnostics;
using System.Text.RegularExpressions;
using AsterCache.Core.I18n;
using AsterCache.Core.Json;
using AsterCache.Core.Prefetch;
using AsterCache.Prefetch;

namespace AsterCache.Cli.Commands;

/// scripts/game_launch.ps1: 핫 세트를 존 순서로 RAM 에 읽어 들이는 스트림을 먼저 시작하고, 게임을 바로 시작한 뒤 스트림이 끝나기를 기다립니다.
/// 일반 사용자 권한. 게임은 이 프로그램이 끝나도 계속 실행됩니다. --no-prefetch 는 게임만 시작합니다 (비교 실행용).
internal static partial class LaunchCommand
{
    public static readonly ArgOption[] Options =
    [
        new("trace"),
        new("origin-root"),
        new("game", Required: true),
        new("game-arg", ArgKind.Many),
        new("no-prefetch", ArgKind.Flag),
        new("full-file-ratio", Type: ArgType.Double, Default: "2"),
    ];

    private const string Strategy = "physical";
    private const long BudgetMib = 1024;

    public static int Run(ParsedArgs a, TextWriter o)
    {
        string game = a.Text("game")!;
        if (!UriPattern().IsMatch(game) && !CommandExists(game))
        {
            throw new PyValueException($"-Game '{game}' is neither a file nor a URI");
        }

        if (a.Flag("no-prefetch"))
        {
            Process? plain = StartGame(game, a.List("game-arg"));
            return AsterCli.Emit(o, new JObject { { "prefetch", false }, { "game_pid", plain?.Id } });
        }

        string? trace = a.Text("trace") ?? PrefetchCommand.EnvOrNull("ASTER_TRACE");
        string? root = a.Text("origin-root") ?? PrefetchCommand.EnvOrNull("ASTER_ROOT");
        if (trace is null || root is null)
        {
            throw new ArgException(L.Get(a.Lang, "Cli_Launch_NeedArgs"));
        }

        trace = PathText.Str(trace);
        root = PathText.Str(root);
        if (!File.Exists(trace))
        {
            throw new PyValueException($"-Trace file not found: {trace}");
        }

        if (!Directory.Exists(root))
        {
            throw new PyValueException($"-OriginRoot not found: {root}");
        }

        double ratio = a.Double("full-file-ratio")!.Value;
        var watch = Stopwatch.StartNew();
        Task<StreamStats> stream = Task.Run(() =>
        {
            (List<ReadOp> ops, _) = PrefetchCommand.Schedule(trace, root, Strategy, BudgetMib, 0, ratio);
            return ZoneStreamer.Run(ops);
        });
        Process? started = StartGame(game, a.List("game-arg"));
        double gameDelay = watch.Elapsed.TotalSeconds;
        StreamStats s = stream.GetAwaiter().GetResult(); // 읽기 실패 (트레이스 오류 포함) 는 게임을 시작한 뒤에 오류로 알림
        double wall = watch.Elapsed.TotalSeconds;
        if (s.Errors != 0)
        {
            Console.Error.WriteLine(L.Get(a.Lang, "Cli_Launch_ReadsFailed"));
        }

        return AsterCli.Emit(o, new JObject
        {
            { "prefetch", true },
            { "game_pid", started?.Id },
            { "game_delay_seconds", Math.Round(gameDelay, 2) },
            { "stream_wall_seconds", Math.Round(wall, 2) },
            { "seconds", s.Seconds },
            { "bytes", s.Bytes },
            { "requests", s.Requests },
            { "errors", s.Errors },
            { "mib_per_second", Math.Round(s.Bytes / 1048576.0 / Math.Max(s.Seconds, 0.001), 1) },
        });
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.-]+://")]
    private static partial Regex UriPattern();

    // Get-Command 와 같이: 파일 경로이거나 PATH (+ PATHEXT) 에서 찾아지는 이름.
    private static bool CommandExists(string name)
    {
        if (File.Exists(name))
        {
            return true;
        }

        string[] exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE").Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string candidate in new[] { name }.Concat(exts.Select(e => name + e)))
            {
                try
                {
                    if (File.Exists(Path.Combine(dir.Trim('"'), candidate)))
                    {
                        return true;
                    }
                }
                catch (ArgumentException)
                {
                    // PATH 에 잘못된 항목이 섞여 있으면 그 항목만 건너뜀
                }
            }
        }

        return false;
    }

    // Start-Process 처럼 셸 연결로 시작합니다 (URI 도 열림). 인자는 공백이 있으면 따옴표로 감쌉니다.
    private static Process? StartGame(string game, IReadOnlyList<string> args)
    {
        var info = new ProcessStartInfo(game) { UseShellExecute = true, Arguments = string.Join(' ', args.Select(x => x.Contains(' ', StringComparison.Ordinal) ? "\"" + x + "\"" : x)) };
        return Process.Start(info);
    }
}
