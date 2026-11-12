using System.ComponentModel;
using AsterCache.Core;
using AsterCache.Core.I18n;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Settings;
using AsterCache.Service;

namespace AsterCache.Tray;

public readonly record struct ActionResult(bool Ok, string Message);

/// 설정 탭의 자동 모드 입력값 (UI 스레드에서 읽어 배경 작업에 넘기는 값 묶음).
public sealed record AutoForm(
    bool Enabled, bool Discover, bool Trace, bool Relocate, int PrewarmRecent, string IgnoreExes, string ZoneLimit, string RewarmMinutes)
{
    /// SSD 보조 저장 (선택 기능): 켬과 예산 (GiB 숫자).
    public bool TierEnabled { get; init; }

    public string TierBudget { get; init; } = "8";

    /// 하루 재배치 쓰기 상한 (GiB 숫자, 0 = 무제한).
    public string RelocateGib { get; init; } = "8";

    /// 시작 스트림 켬 (null 이면 파일 값을 그대로 둠).
    public bool? StartStream { get; init; }

    /// 재배치 시간대의 시각 목록 (null 이면 파일 값을 그대로 둠, 비어 있으면 언제든).
    public IReadOnlyList<int>? RelocateHours { get; init; }

    /// 고급 구역의 입력 (null 이면 고급 값은 그대로 둠).
    public AdvancedForm? Advanced { get; init; }
}

/// 설정 탭 고급 구역의 입력값 (숫자 글자, null 이면 사용자가 건드리지 않은 칸이라 파일 값을 그대로 둠). 범위는 저장할 때 설정 검증이 확인합니다.
public sealed record AdvancedForm(
    string? CacheMib, string? StartupGames, string? StartupMaxMib, string? RewarmAfterExitMin, string? MaxRelocationsPerDay, string? IdleSeconds, string? StartStreamCapSeconds);

/// 파일·폴더를 기본 프로그램으로 엽니다. 시험은 가짜로 바꿉니다.
public interface IShell
{
    void Open(string path);
}

/// 트레이 메뉴가 보여주고 부르는 모든 것. UI 없이 시험할 수 있게 화면 객체를 직접 쓰지 않습니다.
public sealed class TrayViewModel(
    ConfigEditor config, IServiceControl service, IGameStreamer streamer, IShell shell, ILog log,
    string? trayVersion = null, string? traySettingsPath = null, bool notificationsEnabled = true)
{
    private readonly FileMetadataCache<AsterSettings> _settingsCache = new();
    private AsterSettings? _settings;
    private string? _configError;
    private int _streaming;

    public ServiceState Service { get; private set; } = ServiceState.Unknown;

    /// 서비스 실행 파일 버전이 트레이와 다름 (서비스 등록을 고르지 않았거나 설치가 중간에 멈췄을 때): 설치 프로그램으로 서비스를 다시 설치해야 합니다.
    public bool ServiceReinstallNeeded { get; private set; }

    public IReadOnlyList<string> Games => _settings?.Games.Select(g => g.Name).ToList() ?? [];

    /// 서비스가 상태 파일에 남긴 런처 표시 이름 (게임 키 → 이름). 관리 창이 상태 파일을 읽을 때마다 채웁니다.
    public IReadOnlyDictionary<string, string> LauncherNames { get; set; } = new Dictionary<string, string>();

    /// 화면에 보일 게임 이름 (현재 언어). 내부 키는 그대로 두고 표시만 바꿉니다. 이름이 없으면 밑줄만 공백으로.
    public string DisplayName(string key)
    {
        GameSettings? game = _settings?.Games.FirstOrDefault(g => string.Equals(g.Name, key, StringComparison.OrdinalIgnoreCase));
        LauncherNames.TryGetValue(game?.Name ?? key, out string? launcher);
        return game?.DisplayFor(L.Current, launcher) ?? launcher ?? key.Replace('_', ' ');
    }

    /// 마지막으로 읽은 설정 (읽지 못했거나 아직 없으면 null).
    public AsterSettings? Settings => _settings;

    public string? TrayVersion => trayVersion;

    /// 트레이 알림 창 (토스트) 을 띄울지 (tray.json 의 notifications.enabled, 기본 켜짐).
    public bool NotificationsEnabled { get; private set; } = notificationsEnabled;

    /// 알림 창을 띄워도 되는지. 꺼져 있으면 띄우지 않고 내용만 트레이 로그에 남깁니다.
    public bool AllowToast(string text)
    {
        if (NotificationsEnabled)
        {
            return true;
        }

        log.Info("notification off: " + text);
        return false;
    }

    /// 알림 창 켬·끔을 tray.json 에 저장합니다 (데이터 폴더 쓰기 권한이 필요).
    public ActionResult SetNotifications(bool enabled)
    {
        if (traySettingsPath is null)
        {
            return Report(false, L.T("Err_ConfigChange", "tray.json"));
        }

        try
        {
            TraySettings.SaveNotifications(traySettingsPath, enabled);
        }
        catch (UnauthorizedAccessException e)
        {
            log.Error("tray settings change denied", e);
            return Report(false, L.T("Err_NoPermission"));
        }
        catch (Exception e) when (e is SettingsException or IOException)
        {
            log.Error("tray settings change failed", e);
            return Report(false, L.T("Err_ConfigChange", FileLog.Scrub(e.Message)));
        }

        NotificationsEnabled = enabled;
        return Report(true, L.T(enabled ? "Msg_NotificationsOn" : "Msg_NotificationsOff"));
    }

    /// 등록된 서비스 실행 파일의 버전 (미설치면 null).
    public string? ServiceVersion { get; private set; }

    public string PlansDir => config.PlansDir;

    public string LastMessage { get; private set; } = string.Empty;

    /// LastMessage 를 남긴 시각 (툴팁은 오래된 메시지를 빼려고 씁니다).
    public DateTimeOffset LastMessageAt { get; private set; } = DateTimeOffset.MinValue;

    /// 설정을 못 읽었거나 서비스 재설치가 필요함 (작업표시줄 상태를 주의로).
    public bool HasProblem => _configError is not null || ServiceReinstallNeeded;

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
            _settings = _settingsCache.Get(config.ConfigPath, config.Load);
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

    public ActionResult AddGame(GameInput game) => Edit(() => config.Add(game), L.T("Msg_Registered", DisplayName(game.Name)));

    public ActionResult RemoveGame(string name)
    {
        IReadOnlyList<string> dropped = [];
        ActionResult r = Edit(() => dropped = config.Remove(name), L.T("Msg_Removed", DisplayName(name)));
        if (r.Ok && dropped.Count > 0)
        {
            log.Warn($"auto.ignore_exes full: dropped oldest {string.Join(", ", dropped)}");
        }

        return r;
    }

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
            return Report(true, L.T("Msg_Streamed", DisplayName(name), r.Bytes >> 20, r.Requests, r.Errors, r.Seconds));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException or Win32Exception)
        {
            log.Error($"{name}: stream failed", e);
            return Report(false, L.T("Err_StreamFailed", DisplayName(name), e.GetType().Name));
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

    /// 자동 모드 켬·꺼짐과 예열 개수, 제외 exe (쉼표로 구분) 를 기록합니다. 나머지 auto 값은 지금 값을 그대로 보존합니다.
    public ActionResult SetAuto(bool enabled, bool discover, bool trace, bool relocate, int prewarmRecent, string ignoreExes) =>
        Edit(() => config.UpdateAuto(a => a with
        {
            Enabled = enabled,
            Discover = discover,
            Trace = trace,
            Relocate = relocate,
            PrewarmRecent = prewarmRecent,
            IgnoreExes = ignoreExes.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
        }), L.T("Msg_AutoSet"));

    /// 설정 탭의 "자동 모드 적용": 모든 입력을 먼저 확인하고 auto 값·존 상한·예열 유지 주기·SSD 보조 저장·재배치 상한을 잠금 안에서 한 번에 저장합니다 (일부만 적용되지 않음).
    public ActionResult ApplyAuto(AutoForm f)
    {
        if (!TryZoneAndRewarm(f.ZoneLimit, f.RewarmMinutes, out int mib, out bool zoneAuto, out int minutes))
        {
            return Report(false, L.T("Err_SpeedInput"));
        }

        if (!TryTierBudget(f.TierBudget, out int tierGib))
        {
            return Report(false, L.T("Err_TierBudget"));
        }

        if (!TryRelocateGib(f.RelocateGib, out int relocateGib))
        {
            return Report(false, L.T("Err_RelocateGib"));
        }

        int?[] adv = [];
        if (f.Advanced is { } av && !TryNumbers([av.CacheMib, av.StartupGames, av.StartupMaxMib, av.RewarmAfterExitMin, av.MaxRelocationsPerDay, av.IdleSeconds, av.StartStreamCapSeconds], out adv))
        {
            return Report(false, L.T("Err_AdvancedInput"));
        }

        int? cache = null;
        if (adv.Length > 0 && adv[0] is int wanted && wanted != CacheMib(_settings))
        {
            if (_settings is not { Games.Count: > 0 })
            {
                return Report(false, L.T("Err_CacheNeedsGame"));
            }

            cache = wanted;
        }

        string[] ignore = f.IgnoreExes.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return Edit(() => config.UpdateAuto(a => WithAdvanced(WithRelocateGib(a with
        {
            Enabled = f.Enabled,
            Discover = f.Discover,
            Trace = f.Trace,
            Relocate = f.Relocate,
            PrewarmRecent = f.PrewarmRecent,
            IgnoreExes = ignore,
            RewarmMinutes = minutes,
            SsdTier = a.SsdTier with { Enabled = f.TierEnabled, BudgetGib = tierGib },
            StartStreamEnabled = f.StartStream ?? a.StartStreamEnabled,
            RelocateHours = f.RelocateHours ?? a.RelocateHours,
        }, relocateGib), adv), mib, zoneAuto, cache), L.T("Msg_AutoSet"));
    }

    /// 캐시 예산 (MiB, 0 = 자동): 서비스처럼 게임들에 적힌 값 중 가장 큰 것.
    public static int CacheMib(AsterSettings? s) => s?.Games.Select(g => g.Prewarm.MaxCacheMib).DefaultIfEmpty(0).Max() ?? 0;

    // adv 는 TryNumbers 로 읽은 고급 구역 값 (비어 있거나 항목이 null 이면 그대로 둠). 유휴 판정은 지금 값과 같으면 건드리지 않아 idle_minutes 만 있는 파일을 유지합니다.
    private static AutoSettings WithAdvanced(AutoSettings a, int?[] adv)
    {
        if (adv.Length == 0)
        {
            return a;
        }

        AutoSettings b = a with
        {
            StartupWarmGames = adv[1] ?? a.StartupWarmGames,
            StartupWarmMaxMib = adv[2] ?? a.StartupWarmMaxMib,
            RewarmAfterExitMin = adv[3] ?? a.RewarmAfterExitMin,
            MaxRelocationsPerDay = adv[4] ?? a.MaxRelocationsPerDay,
            StartStreamCapSeconds = adv[6] ?? a.StartStreamCapSeconds,
        };
        return adv[5] is not int idle || idle == (int)a.IdleWindow.TotalSeconds ? b : b with { IdleSeconds = idle, IdleSecondsSet = true };
    }

    private static bool TryNumbers(string?[] texts, out int?[] numbers)
    {
        numbers = new int?[texts.Length];
        for (int i = 0; i < texts.Length; i++)
        {
            if (texts[i] is null)
            {
                continue;
            }

            if (!int.TryParse(texts[i]!.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n))
            {
                return false;
            }

            numbers[i] = n;
        }

        return true;
    }

    /// 재배치 시간대를 시각 목록으로: from 시부터 to 시 직전까지 (to 가 from 보다 작으면 자정을 넘김). from 과 to 가 같으면 빈 목록 (호출한 쪽이 먼저 걸러야 함).
    public static IReadOnlyList<int> HoursBetween(int from, int to) =>
        from == to ? [] : Enumerable.Range(0, ((to - from + 24) % 24)).Select(i => (from + i) % 24).ToList();

    /// 시각 목록이 한 구간 (자정을 넘겨도 됨) 으로 표현되면 (From, To) 를, 아니면 null (비어 있어도 null).
    public static (int From, int To)? RangeOf(IReadOnlyList<int> hours)
    {
        HashSet<int> set = [.. hours];
        if (set.Count is 0 or 24)
        {
            return null;
        }

        int[] starts = set.Where(h => !set.Contains((h + 23) % 24)).ToArray();
        int[] ends = set.Where(h => !set.Contains((h + 1) % 24)).ToArray();
        return starts.Length == 1 ? (starts[0], (ends[0] + 1) % 24) : null;
    }

    /// 게임의 정책 (mode·일시 중지)·표시 이름을 한 번에 저장합니다. 이름 칸은 앞뒤 공백을 지우고, 비면 키를 지웁니다.
    public ActionResult SetGameOptions(string name, GameMode mode, bool paused, string displayName, string nameKo, string nameEn) =>
        Edit(() => config.SetGameOptions(name, mode, paused, displayName.Trim(), new Dictionary<string, string> { ["ko"] = nameKo.Trim(), ["en"] = nameEn.Trim() }),
            L.T("Msg_GameOptionsSet", DisplayName(name)));

    /// 하루 (24 시간) 에 자동 재배치가 쓸 수 있는 총량 (GiB, 0 = 무제한) 을 기록합니다. 나머지 auto 값은 그대로 보존합니다.
    public ActionResult SetRelocateGib(string gib)
    {
        if (!TryRelocateGib(gib, out int n))
        {
            return Report(false, L.T("Err_RelocateGib"));
        }

        return Edit(() => config.UpdateAuto(a => WithRelocateGib(a, n)), L.T("Msg_AutoSet"));
    }

    // 표시된 기본값 그대로면 명시하지 않음 (SMR 기본값 규칙 유지)
    private static AutoSettings WithRelocateGib(AutoSettings a, int n) =>
        n == a.MaxRelocateGibPerDay && !a.MaxRelocateGibPerDaySet ? a : a with { MaxRelocateGibPerDay = n, MaxRelocateGibPerDaySet = true };

    private static bool TryRelocateGib(string gib, out int n) =>
        int.TryParse(gib.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out n) && n <= 1048576;

    private static bool TryTierBudget(string budgetGib, out int gib) =>
        int.TryParse(budgetGib.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out gib) && gib is >= 1 and <= 4096;

    private static bool TryZoneAndRewarm(string zoneLimit, string rewarmMinutes, out int mib, out bool auto, out int minutes)
    {
        string zone = zoneLimit.Trim();
        mib = 0;
        minutes = 0;
        auto = zone.Equals("auto", StringComparison.OrdinalIgnoreCase);
        return (auto || int.TryParse(zone, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out mib))
            && int.TryParse(rewarmMinutes.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out minutes);
    }

    /// SSD 보조 저장 (선택 기능) 의 켬과 예산 (GiB) 을 기록합니다. 폴더(root) 와 나머지 auto 값은 그대로 보존합니다. 링크는 서비스가 만듭니다 (트레이는 설정만 바꿈).
    public ActionResult SetSsdTier(bool enabled, string budgetGib)
    {
        if (!TryTierBudget(budgetGib, out int gib))
        {
            return Report(false, L.T("Err_TierBudget"));
        }

        return Edit(() => config.UpdateAuto(a => a with { SsdTier = a.SsdTier with { Enabled = enabled, BudgetGib = gib } }), L.T("Msg_AutoSet"));
    }

    /// 오류 알림용 한 줄: 예외 형식과 메시지 첫 줄 (사용자 폴더 경로는 가림).
    public static string Brief(Exception e)
    {
        string first = FileLog.Scrub(e.Message).Split('\n', 2)[0].Trim();
        return first.Length == 0 ? e.GetType().Name : e.GetType().Name + ": " + first;
    }

    /// 존 상한 ("auto" 또는 MiB 숫자) 과 예열 유지 주기 (분, 0 = 끔) 를 기록합니다. 나머지 auto 값은 그대로 보존합니다.
    public ActionResult SetSpeed(string zoneLimit, string rewarmMinutes)
    {
        if (!TryZoneAndRewarm(zoneLimit, rewarmMinutes, out int mib, out bool auto, out int minutes))
        {
            return Report(false, L.T("Err_SpeedInput"));
        }

        return Edit(() => config.UpdateAuto(a => a with { RewarmMinutes = minutes }, mib, auto), L.T("Msg_SpeedSet"));
    }

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

    /// 업데이트 방식과 프리릴리스 포함 여부를 config.json 에 저장합니다 (데이터 폴더 쓰기 권한이 필요). 업데이트는 다음 확인부터 이 값을 따릅니다.
    public ActionResult SetUpdate(UpdateMode mode, bool prerelease) =>
        Edit(() => config.SetUpdate(mode, prerelease), L.T("Msg_UpdateSet"));

    private ActionResult Edit(Action edit, string done)
    {
        try
        {
            edit();
        }
        catch (UnauthorizedAccessException e)
        {
            log.Error("config change denied", e);
            // 데이터 폴더는 SYSTEM·Administrators 만 쓸 수 있음 (서비스가 SYSTEM 으로 파일을 옮기므로 일반 사용자 쓰기를 막아 둠)
            return Report(false, L.T("Err_NoPermission"));
        }
        catch (Exception e) when (e is SettingsException or IOException)
        {
            log.Error("config change failed", e);
            return Report(false, L.T("Err_ConfigChange", FileLog.Scrub(e.Message)));
        }

        _settingsCache.Invalidate();
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
        LastMessageAt = DateTimeOffset.UtcNow;
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
