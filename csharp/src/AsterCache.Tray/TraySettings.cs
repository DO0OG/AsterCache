using AsterCache.Core.Json;
using AsterCache.Core.Settings;

namespace AsterCache.Tray;

/// tray.json (트레이 전용 설정). 없으면 기본값: 알림 켜짐. notifications.enabled (기본 true) 가 false 면 트레이 알림 창을 띄우지 않습니다.
/// 예전 버전이 쓰던 update_feed_url 키는 더는 쓰지 않지만, 업그레이드한 사용자의 파일을 깨지 않도록 거부하지 않고 무시합니다 (저장할 때도 그대로 남김).
public sealed record TraySettings(bool NotificationsEnabled = true)
{
    public static TraySettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return new TraySettings();
        }

        try
        {
            if (JsonRead.ReadFile(path) is not JObject o || o.Any(kv => kv.Key is not ("update_feed_url" or "notifications")))
            {
                throw new SettingsException("tray.json: only notifications is allowed");
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

            return new TraySettings(notifications);
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
