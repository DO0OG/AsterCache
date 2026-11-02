using System.ComponentModel;
using AsterCache.Core;
using AsterCache.Core.I18n;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Settings;
using AsterCache.Service;

namespace AsterCache.Tray;

public readonly record struct ActionResult(bool Ok, string Message);

/// 파일·폴더를 기본 프로그램으로 엽니다. 시험은 가짜로 바꿉니다.
public interface IShell
{
    void Open(string path);
}

/// 트레이 메뉴가 보여주고 부르는 모든 것. UI 없이 시험할 수 있게 화면 객체를 직접 쓰지 않습니다.
public sealed class TrayViewModel(
    ConfigEditor config, IServiceControl service, IGameStreamer streamer, IShell shell, ILog log, UpdateService? updates = null,
    string? trayVersion = null)
{
    private AsterSettings? _settings;
    private string? _configError;
    private int _streaming;

    public ServiceState Service { get; private set; } = ServiceState.Unknown;

    /// 서비스 실행 파일 버전이 트레이와 다름 (트레이만 Velopack 으로 갱신됐을 때): 설치 프로그램으로 서비스를 다시 설치해야 합니다.
    public bool ServiceReinstallNeeded { get; private set; }

    public IReadOnlyList<string> Games => _settings?.Games.Select(g => g.Name).ToList() ?? [];

    /// 마지막으로 읽은 설정 (읽지 못했거나 아직 없으면 null).
    public AsterSettings? Settings => _settings;

    public string? TrayVersion => trayVersion;

    /// 등록된 서비스 실행 파일의 버전 (미설치면 null).
    public string? ServiceVersion { get; private set; }

    /// 확인된 새 버전 (없으면 null).
    public string? PendingVersion { get; private set; }

    public string PlansDir => config.PlansDir;

    public string LastMessage { get; private set; } = string.Empty;

    public string StatusText { get; private set; } = string.Empty;

    /// 서비스 상태와 설정을 다시 읽어 StatusText 를 만듭니다.
    public void Refresh()
    {
        Service = service.Query();
        string? serviceVersion = Service == ServiceState.NotInstalled ? null : service.InstalledVersion();
        ServiceVersion = serviceVersion;
        ServiceReinstallNeeded = trayVersion is not null && serviceVersion is not null && !SameVersion(trayVersion, serviceVersion);
        try
        {
            _settings = config.Load();
            _configError = null;
        }
        catch (Exception e) when (e is SettingsException or IOException or UnauthorizedAccessException)
        {
            _settings = null;
            _configError = FileLog.Scrub(e.Message);
        }

        string text = $"AsterCache: {Describe(Service)} · " + (Games.Count == 0 && _configError is null ? L.T("Status_NoGames") : L.T("Status_GameCount", Games.Count));
        if (ServiceReinstallNeeded)
        {
            text += " · " + L.T("Status_Reinstall", serviceVersion, trayVersion);
        }

        StatusText = _configError is null ? text : text + " · " + L.T("Status_ConfigError", _configError);
    }

    public ActionResult AddGame(GameInput game) => Edit(() => config.Add(game), L.T("Msg_Registered", game.Name));

    public ActionResult RemoveGame(string name) => Edit(() => config.Remove(name), L.T("Msg_Removed", name));

    /// 선택한 게임의 .prefetch.json 으로 지금 스트림 (비관리자 가능). 이미 돌고 있으면 거절.
    public ActionResult StreamNow(string name)
    {
        GameSettings? game = _settings?.Games.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
        if (game is null)
        {
            return Report(false, L.T("Err_NotRegistered", name));
        }

        if (Interlocked.CompareExchange(ref _streaming, 1, 0) != 0)
        {
            return Report(false, L.T("Err_AlreadyStreaming"));
        }

        try
        {
            AsterCache.Prefetch.StreamStats r = streamer.Stream(game);
            return Report(true, L.T("Msg_Streamed", name, r.Bytes >> 20, r.Requests, r.Errors, r.Seconds));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException or Win32Exception)
        {
            log.Error($"{name}: stream failed", e);
            return Report(false, L.T("Err_StreamFailed", name, e.GetType().Name));
        }
        finally
        {
            Interlocked.Exchange(ref _streaming, 0);
        }
    }

    public ActionResult StartService() => Elevated(service.RequestStart, L.T("Msg_StartRequested"));

    public ActionResult StopService() => Elevated(service.RequestStop, L.T("Msg_StopRequested"));

    public ActionResult InstallService(string serviceExe) => Elevated(() => service.RequestInstall(serviceExe, Path.GetDirectoryName(config.ConfigPath)!), L.T("Msg_InstallRequested"));

    public ActionResult UninstallService() => Elevated(service.RequestUninstall, L.T("Msg_UninstallRequested"));

    public ActionResult SetWatcher(string watcher) => Edit(() => config.SetWatcher(watcher), L.T("Msg_WatcherSet", watcher));

    /// "auto", "ko" 또는 "en". 화면 언어는 앱을 다시 실행해야 바뀝니다.
    public ActionResult SetLanguage(string language) => Edit(() => config.SetLanguage(language), L.T("Msg_LanguageSaved"));

    public ActionResult OpenLogs()
    {
        try
        {
            shell.Open(Path.Combine(Path.GetDirectoryName(config.ConfigPath)!, "logs"));
            return Report(true, L.T("Msg_LogsOpened"));
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return Report(false, L.T("Err_LogsOpen"));
        }
    }

    public async Task<ActionResult> CheckUpdateAsync()
    {
        if (updates is null)
        {
            return Report(false, L.T("Err_UpdateDisabled"));
        }

        if (!updates.IsSupported)
        {
            return Report(false, L.T("Err_UpdateUnsupported"));
        }

        PendingVersion = await updates.CheckAsync();
        return Report(true, PendingVersion is null ? L.T("Msg_UpToDate") : L.T("Msg_NewVersion", PendingVersion));
    }

    /// 확인된 업데이트를 받아 적용하고 다시 시작합니다.
    public async Task<ActionResult> ApplyUpdateAsync(CancellationToken cancellationToken)
    {
        if (updates is null || PendingVersion is null)
        {
            return Report(false, L.T("Err_NoUpdate"));
        }

        try
        {
            await updates.DownloadAndApplyAsync(_ => { }, cancellationToken);
            return Report(true, L.T("Msg_Updating"));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.Error("update apply failed", e);
            return Report(false, L.T("Err_UpdateFailed"));
        }
    }

    private ActionResult Edit(Action edit, string done)
    {
        try
        {
            edit();
        }
        catch (UnauthorizedAccessException)
        {
            // 데이터 폴더는 SYSTEM·Administrators 만 쓸 수 있음 (서비스가 SYSTEM 으로 파일을 옮기므로 일반 사용자 쓰기를 막아 둠)
            return Report(false, L.T("Err_NoPermission"));
        }
        catch (Exception e) when (e is SettingsException or IOException)
        {
            return Report(false, L.T("Err_ConfigChange", FileLog.Scrub(e.Message)));
        }

        Refresh();
        return Report(true, done);
    }

    private ActionResult Elevated(Action request, string done)
    {
        try
        {
            request();
        }
        catch (Win32Exception e)
        {
            return Report(false, e.NativeErrorCode == 1223 ? L.T("Err_UacCancelled") : L.T("Err_ServiceControl", e.NativeErrorCode));
        }

        return Report(true, done);
    }

    private ActionResult Report(bool ok, string message)
    {
        LastMessage = message;
        log.Info(message);
        return new ActionResult(ok, message);
    }

    private static bool SameVersion(string a, string b) =>
        Version.TryParse(a, out Version? x) && Version.TryParse(b, out Version? y)
            ? (x.Major, x.Minor, Math.Max(0, x.Build), Math.Max(0, x.Revision)) == (y.Major, y.Minor, Math.Max(0, y.Build), Math.Max(0, y.Revision))
            : string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Describe(ServiceState s) => s switch
    {
        ServiceState.NotInstalled => L.T("Svc_NotInstalled"),
        ServiceState.Running => L.T("Svc_Running"),
        ServiceState.Stopped => L.T("Svc_Stopped"),
        ServiceState.Pending => L.T("Svc_Pending"),
        _ => L.T("Svc_Unknown"),
    };
}
