using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;

namespace AsterCache.Tray;

public enum ServiceState { NotInstalled, Stopped, Running, Pending, Unknown }

/// 서비스 상태 조회와 시작/정지 요청. 시험은 가짜로 바꿉니다.
public interface IServiceControl
{
    ServiceState Query();

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

    public void RequestStart() => elevator.Run(Sc, "start " + serviceName);

    public void RequestStop() => elevator.Run(Sc, "stop " + serviceName);
}
