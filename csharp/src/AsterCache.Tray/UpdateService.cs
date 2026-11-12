using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Settings;
using Velopack;

namespace AsterCache.Tray;

/// tray.json (트레이 전용 설정). 없으면 기본값: 업데이트 비활성, 알림 켜짐. update_feed_url 은 https 주소여야 하고 비어 있으면 비활성입니다.
/// notifications.enabled (기본 true) 가 false 면 트레이 알림 창을 띄우지 않습니다.
public sealed record TraySettings(string? UpdateFeedUrl, bool NotificationsEnabled = true)
{
    public bool UpdateEnabled => !string.IsNullOrEmpty(UpdateFeedUrl);

    public static TraySettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return new TraySettings((string?)null);
        }

        try
        {
            if (JsonRead.ReadFile(path) is not JObject o || o.Any(kv => kv.Key is not ("update_feed_url" or "notifications")))
            {
                throw new SettingsException("tray.json: only update_feed_url and notifications are allowed");
            }

            string? url = o.TryGetValue("update_feed_url", out object? v) ? v as string : null;
            if (!string.IsNullOrEmpty(url) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                throw new SettingsException("tray.json: update_feed_url must be https");
            }

            bool notifications = true;
            if (o.TryGetValue("notifications", out object? n))
            {
                if (n is not JObject group || group.Any(kv => kv.Key != "enabled"))
                {
                    throw new SettingsException("tray.json: notifications only allows enabled");
                }

                if (group.TryGetValue("enabled", out object? e))
                {
                    notifications = e as bool? ?? throw new SettingsException("tray.json: notifications.enabled must be true or false");
                }
            }

            return new TraySettings(url, notifications);
        }
        catch (PyValueException e)
        {
            throw new SettingsException("tray.json: " + e.Message);
        }
    }

    /// notifications.enabled 만 바꿔 저장합니다 (다른 키는 그대로, 임시 파일에 쓴 뒤 교체). 못 읽는 파일이면 SettingsException, 쓸 수 없으면 UnauthorizedAccessException·IOException.
    public static void SaveNotifications(string path, bool enabled)
    {
        JObject root;
        try
        {
            root = File.Exists(path) ? JsonRead.ReadFile(path) as JObject ?? throw new SettingsException("tray.json: not an object") : new JObject();
        }
        catch (PyValueException e)
        {
            throw new SettingsException("tray.json: " + e.Message);
        }

        root["notifications"] = new JObject { { "enabled", enabled } };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string tmp = path + ".tmp";
        bool moved = false;
        try
        {
            File.WriteAllBytes(tmp, PyJson.Utf8.GetBytes(PyJson.Dumps(root, indent: 2, ensureAscii: false) + "\n"));
            File.Move(tmp, path, true);
            moved = true;
        }
        finally
        {
            if (!moved)
            {
                try
                {
                    File.Delete(tmp); // 실패해도 임시 파일을 남기지 않음
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // 정리 실패는 원래 오류를 가리지 않음
                }
            }
        }
    }
}

/// Velopack UpdateManager 를 시험에서 바꿔 끼울 수 있게 얇게 감싼 래퍼.
public interface IUpdateManager
{
    bool IsInstalled { get; }

    Task<string?> CheckForUpdatesAsync();

    Task DownloadUpdatesAsync(Action<int> progress, CancellationToken cancellationToken);

    void ApplyUpdatesAndRestart();
}

public sealed class VelopackUpdateManager(string feedUrl) : IUpdateManager
{
    private readonly UpdateManager _inner = new(feedUrl);
    private UpdateInfo? _info;

    public bool IsInstalled => _inner.IsInstalled;

    public async Task<string?> CheckForUpdatesAsync()
    {
        _info = await _inner.CheckForUpdatesAsync();
        return _info?.TargetFullRelease.Version.ToString();
    }

    public Task DownloadUpdatesAsync(Action<int> progress, CancellationToken cancellationToken) =>
        _inner.DownloadUpdatesAsync(_info ?? throw new InvalidOperationException("no checked update"), progress, cancellationToken);

    public void ApplyUpdatesAndRestart() => _inner.ApplyUpdatesAndRestart(_info?.TargetFullRelease);
}

/// 새 버전 확인과 설치. 설치되지 않은 실행 (개발 빌드) 은 건너뜁니다. 서비스 쪽 갱신은 설치 관리자 몫입니다.
public sealed class UpdateService(IUpdateManager manager, ILog log)
{
    public bool IsSupported => manager.IsInstalled;

    /// 새 버전 문자열, 없거나 확인 실패면 null.
    public async Task<string?> CheckAsync()
    {
        if (!IsSupported)
        {
            log.Info("update check skipped: not an installed copy");
            return null;
        }

        try
        {
            return await manager.CheckForUpdatesAsync();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.Warn("update check failed: " + e.GetType().Name);
            return null;
        }
    }

    public async Task DownloadAndApplyAsync(Action<int> progress, CancellationToken cancellationToken)
    {
        await manager.DownloadUpdatesAsync(progress, cancellationToken);
        manager.ApplyUpdatesAndRestart();
    }
}
