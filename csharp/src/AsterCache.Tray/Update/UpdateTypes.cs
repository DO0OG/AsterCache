using AsterCache.Core.I18n;
using AsterCache.Core.Settings;

namespace AsterCache.Tray.Update;

/// 업데이트 방식 (config.json 의 update.mode, update.prerelease) 의 값 형. 방식 이름은 Core 설정의 UpdateMode 를 그대로 씁니다.
public sealed record UpdateOptions(UpdateMode Mode, bool IncludePrerelease)
{
    public static UpdateOptions Default { get; } = new(UpdateMode.Auto, false);

    /// 설정을 읽지 못했을 때의 안전한 값: 알림만, 프리릴리스 제외.
    public static UpdateOptions Safe { get; } = new(UpdateMode.Notify, false);

    /// 트레이가 마지막으로 읽은 설정에서 (읽지 못했으면 Safe).
    public static UpdateOptions From(AsterSettings? settings) => settings is null ? Safe : new UpdateOptions(settings.Update.Mode, settings.Update.Prerelease);
}

public enum UpdateState { Idle, Checking, Disabled, Failed, UpToDate, Available, Downloading, WaitingForGame, Installing }

/// 현재 업데이트 상태 (메뉴 항목과 로그에 그대로 씁니다). Percent 는 Downloading 일 때만 의미가 있습니다.
public sealed record UpdateStatus(UpdateState State, string? Version = null, int Percent = 0)
{
    public static UpdateStatus Idle { get; } = new(UpdateState.Idle);

    /// 메뉴를 다시 만들지 판단하는 키 (진행률은 10 % 단위로만 바뀜).
    public string Key => $"{State}|{Version}|{Percent / 10}";

    public string Text() => State switch
    {
        UpdateState.Checking => L.T("Update_Checking"),
        UpdateState.Disabled => L.T("Update_Off"),
        UpdateState.Failed => L.T("Update_Failed"),
        UpdateState.UpToDate => L.T("Update_Current"),
        UpdateState.Available => L.T("Update_Available", Version),
        UpdateState.Downloading => L.T("Update_Downloading", Version, Percent),
        UpdateState.WaitingForGame => L.T("Update_Waiting", Version),
        UpdateState.Installing => L.T("Update_Installing", Version),
        _ => string.Empty,
    };
}

/// 업데이트 검사·내려받기·검증 중 예상된 실패 (원인 문구는 로그용 영어, 화면에는 일반 문구만 보임).
public sealed class UpdateException(string message) : Exception(message);

/// 크기 상한. 시험이 작은 값으로 바꿉니다.
public sealed record UpdateLimits(long InstallerBytes = 512L << 20, long MetadataBytes = 2L << 20)
{
    public TimeSpan MetadataTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromMinutes(30);
}

/// 이번 실행의 환경 (시작할 때 한 번 정함).
/// InstalledBuild: 설치 프로그램으로 Program Files 에 설치됐고 데이터 폴더가 기본 ProgramData\AsterCache (설치 프로그램이 관리자 전용으로 잠금) 임.
public sealed record UpdateContext(SemVer Current, bool IsAdmin, bool InstalledBuild);

public enum GateState { Idle, Busy, Unknown }

/// 등록 게임 실행 중이거나 재배치 중인지 (Unknown: 판단할 수 없음 → 설치하지 않음).
public interface IUpdateGate
{
    GateState Query();
}

public interface IUpdateClock
{
    DateTimeOffset UtcNow { get; }

    Task Delay(TimeSpan delay, CancellationToken cancellationToken);
}

/// 검증을 마친 설치 프로그램을 분리된 프로세스로 실행합니다. 인수는 호출하는 쪽이 고정 값으로만 만듭니다.
public interface IInstallerLauncher
{
    void Launch(string installerPath, IReadOnlyList<string> arguments);
}

/// GitHub 릴리스 한 건 중 업데이트에 필요한 것만. Page 는 태그에서 만든 고정 형식 주소 (릴리스 본문의 문자열이 아님).
public sealed record ReleaseInfo(SemVer Version, string InstallerName, Uri InstallerUrl, long InstallerSize, string? Sha256, Uri? ChecksumsUrl, Uri Page);
