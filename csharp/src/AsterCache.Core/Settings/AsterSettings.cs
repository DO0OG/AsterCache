namespace AsterCache.Core.Settings;

/// 게임 한 개의 서비스 설정. 경로는 모두 정규화된 절대 경로 (X:\...), 끝 역슬래시 없음.
public sealed record GameSettings(
    string Name, IReadOnlyList<string> ExeNames, string Trace, string Plan, string OriginRoot, IReadOnlyList<string> AllowedRoots,
    string VolumeLabel, int ReapplyMinutes);

/// config.json 한 벌 (검증을 통과한 값만 존재).
public sealed record AsterSettings(int PollMs, IReadOnlyList<GameSettings> Games);

/// 설정이 형식·경로 검증을 통과하지 못함. 메시지는 필드 이름만 담고 값(경로)은 담지 않습니다.
public sealed class SettingsException : Exception
{
    public SettingsException(string message) : base(message)
    {
    }
}
