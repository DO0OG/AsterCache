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

    /// 서비스 등록 (데이터 폴더 권한 설정 포함, 설치 프로그램과 같은 순서). serviceExe 는 관리자 전용 폴더에 있어야 합니다 (SYSTEM 으로 실행됨).
    void RequestInstall(string serviceExe, string dataDir);

    /// 서비스 정지와 삭제 (데이터 폴더는 남김).
    void RequestUninstall();
}

/// 관리자 권한으로 명령을 한 번 실행 (UAC 확인창). 사용자가 취소하면 Win32Exception (1223).
public interface IElevator
{
    /// 끝날 때까지 기다립니다.
    void Run(string exe, string arguments);

    /// 인자를 하나씩 넘겨 (따옴표·특수문자 처리를 시스템에 맡김) 끝날 때까지 기다립니다.
    void Run(string exe, IReadOnlyList<string> arguments);

    /// 기다리지 않고 승격 실행만 시작합니다 (관리자로 다시 실행).
    void Start(string exe, string arguments);
}

public sealed class UacElevator : IElevator
{
    public void Run(string exe, string arguments)
    {
        using Process? p = Launch(exe, arguments, []);
        p?.WaitForExit();
    }

    public void Run(string exe, IReadOnlyList<string> arguments)
    {
        using Process? p = Launch(exe, null, arguments);
        p?.WaitForExit();
    }

    public void Start(string exe, string arguments) => Launch(exe, arguments, [])?.Dispose();

    private static Process? Launch(string exe, string? arguments, IReadOnlyList<string> list)
    {
        var psi = new ProcessStartInfo { FileName = exe, Verb = "runas", UseShellExecute = true, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        if (arguments is not null)
        {
            psi.Arguments = arguments;
        }

        foreach (string a in list)
        {
            psi.ArgumentList.Add(a);
        }

        return Process.Start(psi);
    }
}

/// SCM 조회는 비관리자도 되고, 시작/정지는 sc.exe 를 승격 실행합니다. 트레이는 서비스에 다른 명령을 보내지 않습니다.
public sealed class ScmServiceControl(IElevator elevator, string serviceName = ScmServiceControl.DefaultName) : IServiceControl
{
    public const string DefaultName = "AsterCache";
    private const int ErrorServiceDoesNotExist = 1060;
    private readonly FileMetadataCache<string> _versionCache = new();

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
            return exe is null ? null : _versionCache.Get(exe, () => File.Exists(exe) ? FileVersionOf(exe) : null);
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

    public void RequestInstall(string serviceExe, string dataDir) => elevator.Run(Cmd, InstallArguments(serviceExe, dataDir));

    public void RequestUninstall() => elevator.Run(Cmd, $"/d /c \"\"{Sc}\" stop {serviceName} & \"{Sc}\" delete {serviceName}\"");

    private static string Cmd => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    /// 설치 프로그램 (aster.iss) 과 같은 순서: 폴더 만들기, 소유자를 Administrators 로, 권한 초기화, SYSTEM·Administrators 전체 + Users 읽기만, 서비스 등록 (LocalSystem, 자동), 시작.
    /// 폴더 만들기만 실패해도 (이미 있음) 이어가고, 그 뒤 한 단계라도 실패하면 나머지는 하지 않습니다.
    internal string InstallArguments(string serviceExe, string dataDir)
    {
        string icacls = Path.Combine(Environment.SystemDirectory, "icacls.exe");
        string bin = $"\\\"{serviceExe}\\\" --data-dir \\\"{dataDir}\\\"";
        return $"/d /c \"mkdir \"{dataDir}\\plans\" \"{dataDir}\\journals\" \"{dataDir}\\logs\" & "
            + $"\"{icacls}\" \"{dataDir}\" /setowner *S-1-5-32-544 /T /C && "
            + $"\"{icacls}\" \"{dataDir}\" /reset /T /C && "
            + $"\"{icacls}\" \"{dataDir}\" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX && "
            + $"\"{Sc}\" create {serviceName} binPath= \"{bin}\" start= auto obj= LocalSystem DisplayName= {serviceName} && "
            + $"\"{Sc}\" start {serviceName}\"";
    }
}
