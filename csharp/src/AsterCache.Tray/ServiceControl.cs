using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;

namespace AsterCache.Tray;

public enum ServiceState { NotInstalled, Stopped, Running, Pending, Unknown }

/// 서비스 상태 조회와 시작/정지 요청. 시험은 가짜로 바꿉니다.
public interface IServiceControl
{
    ServiceState Query();

    /// 등록된 서비스 실행 파일의 FileVersion. 설치되지 않았거나 읽지 못하면 null.
    string? InstalledVersion();

    void RequestStart();

    void RequestStop();
}

/// 관리자 권한으로 명령을 한 번 실행 (UAC 확인창). 사용자가 취소하면 Win32Exception (1223).
public interface IElevator
{
    void Run(string exe, string arguments);
}

public sealed class UacElevator : IElevator
{
    public void Run(string exe, string arguments)
    {
        using Process? p = Process.Start(new ProcessStartInfo { FileName = exe, Arguments = arguments, Verb = "runas", UseShellExecute = true, CreateNoWindow = true });
        p?.WaitForExit();
    }
}

/// SCM 조회는 비관리자도 되고, 시작/정지는 sc.exe 를 승격 실행합니다. 트레이는 서비스에 다른 명령을 보내지 않습니다.
public sealed class ScmServiceControl(IElevator elevator, string serviceName = ScmServiceControl.DefaultName) : IServiceControl
{
    public const string DefaultName = "AsterCache";
    private const int ErrorServiceDoesNotExist = 1060;

    private static string Sc => Path.Combine(Environment.SystemDirectory, "sc.exe");

    public ServiceState Query()
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            return sc.Status switch
            {
                ServiceControllerStatus.Running => ServiceState.Running,
                ServiceControllerStatus.Stopped => ServiceState.Stopped,
                ServiceControllerStatus.StartPending or ServiceControllerStatus.StopPending or ServiceControllerStatus.ContinuePending
                    or ServiceControllerStatus.PausePending => ServiceState.Pending,
                _ => ServiceState.Unknown,
            };
        }
        catch (InvalidOperationException e)
        {
            return e.InnerException is Win32Exception { NativeErrorCode: ErrorServiceDoesNotExist } ? ServiceState.NotInstalled : ServiceState.Unknown;
        }
    }

    /// SCM 에 등록된 경로 (ImagePath, 일반 사용자도 읽음) 의 실행 파일 버전. 트레이는 Velopack 으로, 서비스는 설치 프로그램으로 따로 갱신되므로 둘이 어긋날 수 있습니다.
    public string? InstalledVersion()
    {
        try
        {
            string? exe = ImagePathExe(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\" + serviceName, "ImagePath", null) as string);
            return exe is null || !File.Exists(exe) ? null : FileVersionOf(exe);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// 파일의 FileVersion (예: 1.2.3.0), 없으면 null.
    public static string? FileVersionOf(string? path) => path is null ? null : FileVersionInfo.GetVersionInfo(path).FileVersion;

    /// ImagePath ("C:\Program Files\x\a.exe" --data-dir ...) 에서 실행 파일 경로만. 따옴표가 없으면 첫 .exe 까지.
    public static string? ImagePathExe(string? imagePath)
    {
        string s = imagePath?.Trim() ?? string.Empty;
        if (s.StartsWith('"'))
        {
            int close = s.IndexOf('"', 1);
            return close > 1 ? s[1..close] : null;
        }

        int exe = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? s[..(exe + 4)] : null;
    }

    public void RequestStart() => elevator.Run(Sc, "start " + serviceName);

    public void RequestStop() => elevator.Run(Sc, "stop " + serviceName);
}
