using System.ComponentModel;
using AsterCache.Cli.Commands;
using AsterCache.Core;
using AsterCache.Core.Apply;
using AsterCache.Core.Json;

namespace AsterCache.Cli;

/// aster 명령줄. 종료 코드: 0 성공, 1 실행 오류 (오류 JSON), 2 잘못된 인자, 3 관리자 아님.
/// 표준 출력은 Python 과 같이 한 줄 JSON (relayout/prefetch) 입니다.
public static class AsterCli
{
    private sealed record Command(string Name, ArgOption[] Options, Func<ParsedArgs, TextWriter, int> Run, bool Relayout = false);

    private static readonly Command[] Commands =
    [
        new("plan", PlanCommand.Options, PlanCommand.Run, Relayout: true),
        new("probe", VolumeCommands.ProbeOptions, VolumeCommands.Probe, Relayout: true),
        new("apply", VolumeCommands.ApplyOptions, VolumeCommands.Apply, Relayout: true),
        new("verify", VolumeCommands.VerifyOptions, VolumeCommands.Verify, Relayout: true),
        new("revert", VolumeCommands.RevertOptions, VolumeCommands.Revert, Relayout: true),
        new("prefetch", PrefetchCommand.Options, PrefetchCommand.Run),
        new("trace", TraceCommand.Options, TraceCommand.Run),
        new("launch", LaunchCommand.Options, LaunchCommand.Run),
        new("status", StatusCommand.Options, StatusCommand.Run),
        new("stream-bench", StreamBenchCommand.Options, StreamBenchCommand.Run),
    ];

    public static int Run(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Count == 0 || args[0] is "-h" or "--help")
        {
            (args.Count == 0 ? stderr : stdout).WriteLine("usage: aster {" + string.Join(",", Commands.Select(c => c.Name)) + "} ...");
            return args.Count == 0 ? 2 : 0;
        }

        Command? cmd = Commands.FirstOrDefault(c => c.Name == args[0]);
        if (cmd is null)
        {
            stderr.WriteLine($"aster: error: argument cmd: invalid choice: {PyRepr.Str(args[0])} (choose from {string.Join(", ", Commands.Select(c => PyRepr.Str(c.Name)))})");
            return 2;
        }

        try
        {
            ParsedArgs a = ArgParser.Parse(cmd.Options, args.Skip(1).ToList());
            return cmd.Relayout && !ValidRelayoutNumbers(a) ? Emit(stdout, new JObject { { "error", "invalid_argument" } }, 2) : cmd.Run(a, stdout);
        }
        catch (HelpRequestedException)
        {
            stdout.WriteLine(ArgParser.Usage(cmd.Name, cmd.Options));
            return 0;
        }
        catch (ArgException e)
        {
            stderr.WriteLine(ArgParser.Usage(cmd.Name, cmd.Options));
            stderr.WriteLine($"aster {cmd.Name}: error: {e.Message}");
            return 2;
        }
        catch (NotAdminException)
        {
            return Emit(stdout, new JObject { { "error", "not_admin" } }, 3);
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            (string name, string detail) = Describe(e);
            return Emit(stdout, new JObject { { "error", name }, { "detail", detail } }, 1);
        }
    }

    /// 한 줄 JSON (Python json.dumps(obj, separators=(',', ':'))) 을 쓰고 코드를 돌려줍니다.
    internal static int Emit(TextWriter o, object? obj, int code = 0)
    {
        o.WriteLine(PyJson.Dumps(obj, compact: true));
        return code;
    }

    // relayout/cli.py main: zone_mib, min_sessions, chunk_mib, write_cap_mib 가 1 미만이면 invalid_argument (min_sessions 0 은 기본값).
    private static bool ValidRelayoutNumbers(ParsedArgs a)
    {
        long? ms = a.Int("min-sessions");
        return (a.Int("zone-mib") ?? 1) >= 1 && (ms is null or 0 ? 1 : ms) >= 1 && (a.Int("chunk-mib") ?? 1) >= 1 && (a.Int("write-cap-mib") ?? 1) >= 1;
    }

    // Python open() 의 문구. 현재 폴더 아래 경로는 입력한 대로 보이게 상대 경로로 보여 줍니다.
    private static string Missing(string path)
    {
        string cwd = Directory.GetCurrentDirectory().TrimEnd('\\') + "\\";
        string shown = path.StartsWith(cwd, StringComparison.OrdinalIgnoreCase) ? path[cwd.Length..] : path;
        return $"[Errno 2] No such file or directory: {PyRepr.Str(shown)}";
    }

    /// Python 예외 이름 (type(e).__name__) 에 대응시킨 오류 이름과 설명.
    private static (string Name, string Detail) Describe(Exception e) => e switch
    {
        PlanRejectedException => ("PlanRejected", e.Message),
        PyValueException v when v.Message.StartsWith("invalid JSON", StringComparison.Ordinal) => ("JSONDecodeError", e.Message),
        PyValueException or FormatException or ArgumentException or OverflowException => ("ValueError", e.Message),
        FileNotFoundException f => ("FileNotFoundError", Missing(f.FileName ?? string.Empty)),
        DirectoryNotFoundException => ("FileNotFoundError", Missing(System.Text.RegularExpressions.Regex.Match(e.Message, "'(.*)'") is { Success: true } m ? m.Groups[1].Value : e.Message)),
        UnauthorizedAccessException => ("PermissionError", e.Message),
        IOException or Win32Exception => ("OSError", e.Message),
        ShaMismatchException or InvalidOperationException or NotSupportedException => ("RuntimeError", e.Message),
        _ => (e.GetType().Name, e.Message),
    };
}
