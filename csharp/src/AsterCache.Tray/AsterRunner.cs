using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using AsterCache.Core.I18n;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;

namespace AsterCache.Tray;

/// aster 한 번 실행의 결과. Json 은 출력 마지막 줄 (한 줄 JSON) 을 읽은 것이고, 읽지 못하면 null.
/// Ok: 마지막 줄이 JSON 객체이고 "error" 키가 없음 (aster 는 실패 때 {"error": ...} 를 내므로 승격 실행처럼 종료 코드를 못 받는 경우에도 같은 기준).
public sealed record CliResult(bool Ok, string Output, JObject? Json)
{
    /// stdout 만 JSON 으로 읽고, stderr (예: trace --live 의 "tracing; press Ctrl+C") 는 표시용으로 Output 에만 붙입니다.
    public static CliResult Parse(string output, int? exitCode = null, string stderr = "")
    {
        string? last = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
        JObject? json = null;
        if (last is { Length: > 0 } && last[0] == '{')
        {
            try
            {
                json = JsonRead.Parse(last) as JObject;
            }
            catch (PyValueException)
            {
                // 표준 출력이 JSON 이 아님: 아래에서 실패로 처리
            }
        }

        return new CliResult(json is not null && !json.ContainsKey("error") && exitCode is null or 0, output + stderr, json);
    }

    public static CliResult Fail(string message) => new(false, message, null);
}

/// aster.exe 를 자식 프로세스로 부릅니다. 시험은 가짜로 바꿉니다.
public interface IAsterCli
{
    /// elevate: 관리자 권한이 필요한 일 (이미 관리자면 그대로 실행, 아니면 UAC 확인창 뒤 승격 실행).
    CliResult Run(IReadOnlyList<string> args, bool elevate = false);
}

public sealed class AsterProcess(IElevator elevator, Func<bool> isAdmin, ILog log, string? exe = null, string? tempDir = null) : IAsterCli
{
    private const int MaxLogged = 1500;

    private readonly string _exe = exe ?? Path.Combine(AppContext.BaseDirectory, "aster.exe");

    public CliResult Run(IReadOnlyList<string> args, bool elevate = false)
    {
        string line = string.Join(' ', args.Select(Quote));
        log.Info("aster " + line + (elevate && !isAdmin() ? L.T("Run_Elevated") : string.Empty));
        CliResult result;
        try
        {
            result = elevate && !isAdmin() ? RunElevated(args) : RunDirect(args);
        }
        catch (Win32Exception e)
        {
            result = CliResult.Fail(e.NativeErrorCode == 1223 ? L.T("Err_UacCancelled") : L.T("Run_NotStarted", e.NativeErrorCode));
        }
        catch (IOException e)
        {
            result = CliResult.Fail(L.T("Run_ReadFailed", e.GetType().Name));
        }

        string shown = result.Output.Length > MaxLogged ? result.Output[..MaxLogged] + "..." : result.Output;
        log.Info(L.T(result.Ok ? "Run_ResultOk" : "Run_ResultFail", shown.Trim()));
        return result;
    }

    /// CreateProcess 명령줄 규칙에 맞춘 인자 한 개 (공백·따옴표가 있으면 감쌈, 끝 역슬래시는 두 배).
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0)
        {
            return arg;
        }

        var s = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\')
            {
                slashes++;
                continue;
            }

            s.Append('\\', c == '"' ? (slashes * 2) + 1 : slashes).Append(c);
            slashes = 0;
        }

        return s.Append('\\', slashes * 2).Append('"').ToString();
    }

    private CliResult RunDirect(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(_exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using Process p = Process.Start(psi) ?? throw new Win32Exception(2);
        p.StandardInput.Close();
        Task<string> err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return CliResult.Parse(output, p.ExitCode, err.Result);
    }

    /// aster.exe 를 직접 runas 로 실행합니다 (cmd.exe 를 거치지 않음). 표준 출력은 가져올 수 없으므로 전역 옵션 --result-file 로 받고,
    /// 파일은 실행마다 새 GUID 폴더 (사용자 임시 폴더) 에 둡니다.
    private CliResult RunElevated(IReadOnlyList<string> args)
    {
        string dir = Path.Combine(tempDir ?? Path.GetTempPath(), "aster_" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(dir, "result.txt");
        Directory.CreateDirectory(dir);
        try
        {
            elevator.Run(_exe, ["--result-file", file, .. args]);
            return File.Exists(file) ? CliResult.Parse(File.ReadAllText(file, Encoding.UTF8)) : CliResult.Fail(L.T("Run_NoResultFile"));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.Warn("result folder not removed: " + e.GetType().Name);
            }
        }
    }
}
