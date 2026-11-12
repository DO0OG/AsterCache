namespace AsterCache.Tracer;

/// 수집 구간 하나가 받은 읽기 수와 닫힐 때의 대상 프로세스 수. EventsLost 는 세션 누적값입니다 (구간별 아님).
public readonly record struct WindowStats(long Reads, int Targets, long EventsLost);
