namespace AsterCache.Tracer;

/// 실시간 세션이 대상 프로세스 트리의 파일 활동을 바로 알리는 콜백 (ETW 처리 스레드에서 불림: 짧게 끝나고 던지면 안 됨).
/// OnRead(DOS 경로, 오프셋, 길이): 읽는 순간 파일 이름이 풀린 읽기 (FileKey 이름, 없으면 열려 있는 FileObject 이름).
/// OnLateRead(같은 인자): 읽는 순간 이름을 몰라 보류했다가 FileKey (또는 FileObject) 의 이름이 나중에 와서 늦게 알리는 읽기. 끝까지 이름이 없으면 알리지 않고 ReadNameStats.Unresolved 로 셉니다.
/// OnOpen(DOS 경로): 파일 열기 (FileIO Create).
public sealed record LiveListener(Action<string, long, long> OnRead, Action<string> OnOpen, Action<string, long, long>? OnLateRead = null);

/// 실시간 세션의 읽기 이름 해석 집계 (대상 프로세스 트리의 모든 읽기): 즉시 / 늦게 / 끝내 못 풂.
public readonly record struct ReadNameStats(long Resolved, long Late, long Unresolved);
