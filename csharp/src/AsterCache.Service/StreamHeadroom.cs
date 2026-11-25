using System.Diagnostics;
using AsterCache.Windows;

namespace AsterCache.Service;

/// 시작·단계별 스트림이 묶음을 내기 전에 쓰는 메모리 여유 판정입니다.
/// 여유 = 빈 페이지 + 0 으로 채운 페이지 + 대기 목록 우선순위 0~4 (MemoryLists.HeadroomBytes: 예열·게임 페이지가 들어가는 우선순위 5 의 바로 아래까지).
/// Windows 는 새 페이지를 빈 목록, 0 으로 채운 목록, 대기 목록의 낮은 우선순위 순으로 가져가므로, 여유가 묶음보다 크면 읽어 들인 페이지가 우선순위 5 이상의 대기 페이지를 밀어내지 않습니다.
/// 스트림 하나에 하나씩 만들며 읽는 스레드에서만 부릅니다.
internal sealed class StreamHeadroom(Func<MemoryLists?> lists, Func<long> timestamp)
{
    /// 묶음 크기 위에 더 두는 여유: 판정과 읽기 사이에 게임이 더 잡을 몫. 실측 전에 정한 값입니다.
    public const long SpareBytes = 256L << 20;

    /// 직전 조회가 이보다 새로우면 다시 조회하지 않습니다 (초당 4 회 이하). 조회는 시스템 전체 통계를 읽는 호출 한 번이며 플레이 기록이 1 초마다 같은 조회를 합니다.
    private static readonly TimeSpan MaxAge = TimeSpan.FromMilliseconds(250);

    private bool _hasRead;
    private long _readAt;
    private long? _headroomBytes;

    /// 스트림 동안 조회로 본 가장 작은 여유 (바이트), 조회에 성공한 적이 없으면 null.
    public long? MinBytes { get; private set; }

    /// 여유가 모자라 (또는 조회가 안 돼) 여유가 돌아올 때까지 기다린 묶음의 수 (기다린 뒤 낸 것도 셈) 와 기다린 시간 (초).
    public long HeldBatches { get; private set; }

    public double HeldSeconds { get; private set; }

    /// 가장 최근 조회가 실패했는가 (호출한 쪽이 스트림 하나에 한 번만 로그를 남기려고 봅니다).
    public bool LookupFailed => _hasRead && _headroomBytes is null;

    public static bool Enough(long headroomBytes, long nextBytes) => headroomBytes >= nextBytes + SpareBytes;

    /// nextBytes 크기의 묶음을 지금 내면 안 되는가. 조회가 실패하면 여유가 없는 것으로 봅니다.
    public bool Short(long nextBytes)
    {
        long now = timestamp();
        if (!_hasRead || Stopwatch.GetElapsedTime(_readAt, now) >= MaxAge)
        {
            _hasRead = true;
            _readAt = now;
            _headroomBytes = Query();
            if (_headroomBytes is { } bytes)
            {
                MinBytes = MinBytes is { } min ? Math.Min(min, bytes) : bytes;
            }
        }

        return _headroomBytes is not { } headroom || !Enough(headroom, nextBytes);
    }

    /// 묶음 하나를 기다린 뒤 (냈든 못 냈든) 부릅니다.
    public void NoteHeld(double seconds)
    {
        HeldBatches++;
        HeldSeconds += Math.Max(0, seconds);
    }

    private long? Query()
    {
        try
        {
            return lists()?.HeadroomBytes;
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            return null;
        }
    }
}
