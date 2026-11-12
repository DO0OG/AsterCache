using System.ComponentModel;
using AsterCache.Core.Logging;

namespace AsterCache.Tray.Update;

/// 정식 릴리스 자동 업데이트: 확인 → (조건이 맞으면) 내려받기 → SHA-256 검증 → 조건부 조용한 설치.
/// 서비스는 네트워크에 접근하지 않고 이 클래스는 트레이 안에서만 돕니다. SHA-256 은 전송 무결성 확인일 뿐 게시자 인증이 아닙니다 (서명은 배포하지 않음).
public sealed class AutoUpdater(
    UpdateContext context, Func<UpdateOptions> options, UpdateHttp http, UpdateStaging? staging, IUpdateGate gate, IInstallerLauncher launcher,
    IUpdateClock clock, Action<Uri> openPage, ILog log, Func<string> installerLanguage)
{
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    public static readonly TimeSpan WaitRecheck = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan OffRecheck = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(6)];

    /// 설치 프로그램을 실행한 뒤 트레이가 이 시간 안에 종료되지 않으면 설치가 실패한 것으로 보고 상태를 되돌립니다.
    public static readonly TimeSpan InstallWait = TimeSpan.FromMinutes(10);

    /// 같은 버전의 조용한 설치를 이만큼 시도하고도 버전이 그대로면 그 버전은 자동으로 설치하지 않고 알리기만 합니다 (사용자가 메뉴에서 누르면 다시 시도).
    public const int MaxInstallAttempts = 2;

    /// 설치 프로그램 인수는 이 고정 값과 /LANG=<Inno 언어 이름> 뿐입니다 (릴리스 메타데이터의 문자열은 넣지 않음).
    public static readonly string[] InstallerArguments = ["/SILENT", "/NORESTART", "/SUPPRESSMSGBOXES"];

    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly object _statusLock = new();
    private ReleaseInfo? _release;
    private string? _staged; // 내려받아 검증을 마친 설치 파일 경로
    private string? _stagedHash;
    private bool _approved; // 설치해도 된다고 정해짐 (auto 설정 또는 사용자가 메뉴에서 누름)
    private bool _manual; // 사용자가 메뉴에서 설치를 눌렀음 (자동 시도 횟수 제한을 넘어서도 시도함)
    private int _failures;
    private UpdateStatus _status = UpdateStatus.Idle;

    public event Action? StatusChanged;

    public UpdateStatus Status
    {
        get
        {
            lock (_statusLock)
            {
                return _status;
            }
        }
    }

    /// 이 실행에서 조용한 설치까지 할 수 있는가 (관리자, 설치 프로그램으로 설치된 복사본, 신뢰할 수 있는 내려받기 폴더). 아니면 알림만 합니다.
    public bool CanInstall => context.IsAdmin && context.InstalledBuild && staging is not null;

    /// 시작 5 분 뒤 첫 확인, 이후 24 시간마다. 실패는 15 분 → 1 시간 → 6 시간 간격으로 다시 시도합니다. 취소하면 조용히 끝납니다.
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await clock.Delay(FirstCheckDelay, cancellationToken).ConfigureAwait(false);
            while (true)
            {
                TimeSpan wait;
                try
                {
                    wait = await StepAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // 예상하지 못한 예외로 확인 루프가 영영 멈추지 않게: 기록하고 가장 긴 재시도 간격 뒤에 다시 돕니다
                    log.Warn("update loop error: " + e.GetType().Name + ": " + e.Message);
                    wait = RetryDelays[^1];
                }

                await clock.Delay(wait, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 트레이 종료
        }
    }

    /// 한 번의 주기: 설치 대기 중이면 조건만 다시 보고, 아니면 새로 확인합니다. 돌려주는 값은 다음까지 기다릴 시간 (설치를 시작했으면 InstallWait 뒤에 트레이가 아직 살아 있는지 봄).
    internal async Task<TimeSpan> StepAsync(CancellationToken cancellationToken)
    {
        await _busy.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            UpdateStatus s;
            if (Status.State == UpdateState.Installing)
            {
                log.Warn("update: the installer did not close the tray in time, treating the install as failed");
                _staged = null;
                s = Set(new UpdateStatus(UpdateState.Failed));
            }
            else
            {
                s = _staged is not null && _approved && options().Mode != UpdateMode.Off
                    ? await ProceedAsync(cancellationToken).ConfigureAwait(false)
                    : await CheckCycleAsync(manual: false, cancellationToken).ConfigureAwait(false);
            }

            switch (s.State)
            {
                case UpdateState.Failed:
                    return RetryDelays[Math.Min(_failures++, RetryDelays.Length - 1)];
                case UpdateState.WaitingForGame:
                    return WaitRecheck;
                case UpdateState.Installing:
                    return InstallWait;
                case UpdateState.Disabled or UpdateState.Idle:
                    return OffRecheck;
                default:
                    _failures = 0;
                    return CheckInterval;
            }
        }
        finally
        {
            _busy.Release();
        }
    }

    /// 메뉴의 "업데이트 확인". 다른 작업이 진행 중이면 현재 상태를 그대로 돌려줍니다.
    public async Task<UpdateStatus> CheckNowAsync(CancellationToken cancellationToken)
    {
        if (!await _busy.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return Status;
        }

        try
        {
            return await CheckCycleAsync(manual: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _busy.Release();
        }
    }

    /// 메뉴의 "업데이트 설치". 이 실행이 조용한 설치를 할 수 없으면 (관리자 아님·zip 실행) 릴리스 페이지를 엽니다.
    public async Task<UpdateStatus> InstallNowAsync(CancellationToken cancellationToken)
    {
        if (!await _busy.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return Status;
        }

        try
        {
            if (_release is null)
            {
                return Status;
            }

            if (!CanInstall)
            {
                log.Info("update: silent install is not possible here, opening the release page");
                openPage(_release.Page);
                return Status;
            }

            _approved = true;
            _manual = true;
            return await ProceedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _busy.Release();
        }
    }

    private async Task<UpdateStatus> CheckCycleAsync(bool manual, CancellationToken cancellationToken)
    {
        UpdateOptions o = options();
        if (o.Mode == UpdateMode.Off)
        {
            _release = null;
            _staged = null;
            _approved = false;
            _manual = false;
            return Set(manual ? new UpdateStatus(UpdateState.Disabled) : UpdateStatus.Idle);
        }

        if (manual)
        {
            Set(new UpdateStatus(UpdateState.Checking));
        }

        ReleaseInfo? release;
        try
        {
            byte[] json = await http.GetBytesAsync(ReleaseFeed.MetadataUrl(o.IncludePrerelease), cancellationToken).ConfigureAwait(false);
            release = ReleaseFeed.Select(json, context.Current, o.IncludePrerelease);
        }
        catch (UpdateException e)
        {
            log.Warn("update check failed: " + e.Message);
            return Set(new UpdateStatus(UpdateState.Failed));
        }

        if (release is null)
        {
            _release = null;
            _staged = null;
            _approved = false;
            _manual = false;
            staging?.Clean(keep: null);
            staging?.ClearAttempts();
            return Set(new UpdateStatus(UpdateState.UpToDate, context.Current.Core));
        }

        if (_release is null || !_release.Version.Equals(release.Version))
        {
            _staged = null;
            _approved = false;
            _manual = false;
        }

        _release = release;
        Set(new UpdateStatus(UpdateState.Available, release.Version.Core));
        if (o.Mode == UpdateMode.Auto && CanInstall)
        {
            _approved = true;
        }

        return _approved && CanInstall ? await ProceedAsync(cancellationToken).ConfigureAwait(false) : Status;
    }

    /// 내려받아 검증하고 (아직이면) 설치 조건을 봅니다. 조건이 맞으면 설치 프로그램을 실행합니다.
    private async Task<UpdateStatus> ProceedAsync(CancellationToken cancellationToken)
    {
        ReleaseInfo release = _release!;
        try
        {
            if (!_manual && staging?.Attempts(release.Version.Core) >= MaxInstallAttempts)
            {
                log.Warn($"update: {release.Version.Core} was already installed {MaxInstallAttempts} times without the version changing, announcing only");
                return Set(new UpdateStatus(UpdateState.Available, release.Version.Core));
            }

            _staged ??= await DownloadAsync(release, cancellationToken).ConfigureAwait(false);
            return TryInstall(release);
        }
        catch (Exception e) when (e is UpdateException or IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            _staged = null;
            log.Warn("update failed: " + (e is UpdateException ? e.Message : e.GetType().Name));
            return Set(new UpdateStatus(UpdateState.Failed));
        }
    }

    private UpdateStatus TryInstall(ReleaseInfo release)
    {
        string version = release.Version.Core;
        GateState g = gate.Query();
        if (g != GateState.Idle)
        {
            log.Info(g == GateState.Busy ? "update: a game is running or a relocation is in progress, waiting" : "update: cannot tell whether a game is running, waiting");
            return Set(new UpdateStatus(UpdateState.WaitingForGame, version));
        }

        // 실행 직전에 폴더·파일 권한과 해시를 다시 확인합니다 (검증 뒤 파일이 바뀌었는지)
        staging!.Prepare();
        string path = staging.PathFor(release.InstallerName);
        if (_staged != path || !File.Exists(path) || staging.Sha256Hex(path) != _stagedHash)
        {
            throw new UpdateException("staged installer changed after verification");
        }

        staging.RecordAttempt(version);
        Set(new UpdateStatus(UpdateState.Installing, version));
        launcher.Launch(path, [.. InstallerArguments, "/LANG=" + installerLanguage()]);
        return Status;
    }

    private async Task<string> DownloadAsync(ReleaseInfo release, CancellationToken cancellationToken)
    {
        if (staging is null)
        {
            throw new UpdateException("no trusted update folder");
        }

        staging.Prepare();
        string finalPath = staging.PathFor(release.InstallerName);
        staging.Clean(keep: null); // 폴더에 이미 있던 같은 이름의 파일·부분 파일은 믿지 않고 지운 뒤 새로 받음
        if (File.Exists(finalPath))
        {
            throw new UpdateException("an existing installer file could not be removed");
        }

        string expected = await ExpectedHashAsync(release, cancellationToken).ConfigureAwait(false);

        string version = release.Version.Core;
        Set(new UpdateStatus(UpdateState.Downloading, version));
        string partial = finalPath + UpdateStaging.PartialSuffix;
        bool done = false;
        try
        {
            int last = 0;
            using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await http.DownloadAsync(release.InstallerUrl, file, (got, total) =>
                {
                    int percent = (int)Math.Min(100, got * 100 / Math.Max(1, total ?? release.InstallerSize));
                    if (percent != last)
                    {
                        last = percent;
                        Set(new UpdateStatus(UpdateState.Downloading, version, percent), announce: false);
                    }
                }, cancellationToken).ConfigureAwait(false);
            }

            if (new FileInfo(partial).Length != release.InstallerSize)
            {
                throw new UpdateException("downloaded size differs from the release metadata");
            }

            if (staging.Sha256Hex(partial) != expected)
            {
                throw new UpdateException("SHA-256 does not match");
            }

            File.Move(partial, finalPath, overwrite: true);
            done = true;
        }
        finally
        {
            if (!done)
            {
                UpdateStaging.TryDelete(partial);
            }
        }

        _stagedHash = expected;
        return finalPath;
    }

    /// 기대 해시: 자산의 digest, 없으면 같은 릴리스의 SHA256SUMS.txt. 둘 다 없으면 설치하지 않습니다.
    private async Task<string> ExpectedHashAsync(ReleaseInfo release, CancellationToken cancellationToken)
    {
        if (release.Sha256 is { } digest)
        {
            return digest;
        }

        if (release.ChecksumsUrl is null)
        {
            throw new UpdateException("no checksum published for this release");
        }

        byte[] sums = await http.GetBytesAsync(release.ChecksumsUrl, cancellationToken).ConfigureAwait(false);
        return ReleaseFeed.ChecksumFor(sums, release.InstallerName) ?? throw new UpdateException("installer is not listed in " + ReleaseFeed.ChecksumsName);
    }

    private UpdateStatus Set(UpdateStatus status, bool announce = true)
    {
        lock (_statusLock)
        {
            _status = status;
        }

        if (announce && status.State != UpdateState.Idle)
        {
            log.Info("update: " + status.Text());
        }

        StatusChanged?.Invoke();
        return status;
    }
}
