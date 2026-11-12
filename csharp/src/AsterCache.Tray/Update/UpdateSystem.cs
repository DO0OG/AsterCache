using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using AsterCache.Core.Settings;

namespace AsterCache.Tray.Update;

public sealed class SystemUpdateClock : IUpdateClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

/// 설치 프로그램을 분리된 프로세스로 실행합니다 (셸을 거치지 않고 인수를 하나씩 넘김). 설치 프로그램이 트레이를 종료시키므로 기다리지 않습니다.
public sealed class ProcessInstallerLauncher : IInstallerLauncher
{
    public void Launch(string installerPath, IReadOnlyList<string> arguments)
    {
        var psi = new ProcessStartInfo { FileName = installerPath, UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(installerPath)! };
        foreach (string a in arguments)
        {
            psi.ArgumentList.Add(a);
        }

        Process.Start(psi)?.Dispose();
    }
}

/// 등록 게임이 실행 중이거나 자동 재배치 중이면 Busy. 게임 목록이나 상태 파일을 읽지 못하면 Unknown (설치하지 않고 다음에 다시 봄).
public sealed class TrayUpdateGate(Func<AsterSettings?> settings, Func<IEnumerable<string>, bool> isRunning, string autoStatusPath) : IUpdateGate
{
    public GateState Query()
    {
        if (settings() is not { } s)
        {
            return GateState.Unknown;
        }

        try
        {
            string[] exes = [.. s.Games.SelectMany(g => g.ExeNames)];
            if (exes.Length > 0 && isRunning(exes))
            {
                return GateState.Busy;
            }
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return GateState.Unknown;
        }

        if (!File.Exists(autoStatusPath))
        {
            return GateState.Idle; // 자동 모드가 아직 한 번도 상태를 쓰지 않음
        }

        return AutoStatusFile.Load(autoStatusPath) is not { } auto ? GateState.Unknown : auto.Games.Values.Any(g => g.Stage == "relocating") ? GateState.Busy : GateState.Idle;
    }
}

/// 이 실행이 설치 프로그램으로 설치된 복사본인지와 현재 버전 판정.
public static class UpdateEnvironment
{
    /// Program Files 아래 (설치 프로그램의 자동 시작 예약 작업과 같은 기준) 에 설치 프로그램이 만든 제거 프로그램이 있고, 데이터 폴더가 기본 위치 (설치 프로그램이 관리자 전용으로 잠금) 일 때만 참입니다.
    public static bool IsInstalledBuild(string? processPath, IEnumerable<string> programFilesDirs, Func<string, bool> fileExists, string dataDir, string defaultDataDir)
    {
        if (processPath is null || Path.GetDirectoryName(processPath) is not { } app)
        {
            return false;
        }

        return programFilesDirs.Any(pf => pf.Length > 0 && Under(app, pf))
            && fileExists(Path.Combine(app, "unins000.exe"))
            && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDir)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(defaultDataDir)), StringComparison.OrdinalIgnoreCase);
    }

    /// 접두사 비교 (C:\Program Files2 가 C:\Program Files 아래로 보이지 않게 끝 구분자를 붙임, 대소문자 무시).
    private static bool Under(string dir, string parent) =>
        Slash(dir).StartsWith(Slash(parent), StringComparison.OrdinalIgnoreCase);

    private static string Slash(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;

    public static bool IsInstalledBuild(string? processPath, string dataDir) =>
        IsInstalledBuild(processPath, ProgramFilesDirs(), File.Exists, dataDir, SettingsStore.DefaultDataDir);

    public static string[] ProgramFilesDirs() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
    ];

    /// 이 트레이의 버전 (어셈블리 정보 버전, 예: 0.9.0-beta.38). 읽지 못하면 null → 업데이트 확인을 쓰지 않습니다.
    public static SemVer? CurrentVersion(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { } text && SemVer.TryParse(text, out SemVer v) ? v : null;
}
