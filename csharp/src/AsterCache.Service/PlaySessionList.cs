using AsterCache.Core.Json;
using AsterCache.Core.Trace;

namespace AsterCache.Service;

/// 플레이 기록 파일 (play_samples.json) 에서 한 게임의 끝난 플레이 구간 (번호, 시작, 끝) 을 오래된 순으로 읽습니다. 없거나 읽을 수 없으면 빈 목록 (부가 정보라 변경 저널 판정을 하지 않을 뿐).
internal static class PlaySessionList
{
    public static IReadOnlyList<SessionSpan> Read(string path, string game)
    {
        try
        {
            if (!File.Exists(path) || JsonRead.ReadFile(path) is not JObject root || !root.TryGetValue("games", out object? gamesValue) || gamesValue is not JObject games
                || !games.TryGetValue(game, out object? listValue) || listValue is not List<object?> rows)
            {
                return [];
            }

            var spans = new List<SessionSpan>(rows.Count);
            foreach (object? row in rows)
            {
                if (row is JObject o && o.TryGetValue("sessionId", out object? id) && id is string session && session.Length > 0
                    && o.TryGetValue("startedAt", out object? startText) && startText is string s && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out DateTimeOffset start)
                    && o.TryGetValue("endedAt", out object? endText) && endText is string e && DateTimeOffset.TryParse(e, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out DateTimeOffset end)
                    && end > start)
                {
                    spans.Add(new SessionSpan(session, start.UtcDateTime, end.UtcDateTime)); // 끝나지 않은 플레이 (endedAt 없음) 는 건너뜀
                }
            }

            return [.. spans.OrderBy(x => x.StartUtc)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
        {
            return [];
        }
    }
}
