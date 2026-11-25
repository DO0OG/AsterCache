using System.Diagnostics;
using AsterCache.Windows;

namespace AsterCache.Service;

/// 시작·단계별 스트림이 묶음을 내기 전에 쓰는 메모리 여유 판정입니다.
/// 여유 = 빈 페이지 + 0 으로 채운 페이지 + 대기 목록 우선순위 0~4 (MemoryLists.HeadroomBytes: 예열·게임 페이지가 들어가는 우선순위 5 의 바로 아래까지).
/// Windows 는 새 페이지를 빈 목록, 0 으로 채운 목록, 대기 목록의 낮은 우선순위 순으로 가져가므로, 여유가 묶음보다 크면 읽어 들인 페이지가 우선순위 5 이상의 대기 페이지를 밀어내지 않을 것으로 봅니다.
/// 디스패처 하나에 하나를 두고 (스트림은 디스패처 스레드에서 차례로 돎) 조회 주기를 스트림 사이에도 공유하며, 스트림마다 BeginStream 으로 기록값을 지웁니다.
internal sealed class StreamHeadroom(Func<MemoryLists?> lists, Func<long> timestamp)
{
    /// 묶음 크기 위에 더 두는 여유: 판정과 읽기 사이에 게임이 더 잡을 몫. 실측 전에 정한 값입니다.
    public const long SpareBytes = 256L << 20;

    /// 직전 조회가 이보다 새로우면 다시 조회하지 않습니다 (초당 4 회 이하). 조회는 시스템 전체 통계를 읽는 호출 한 번이며 플레이 기록이 1 초마다 같은 조회를 합니다.
    private static readonly TimeSpan MaxAge = TimeSpan.FromMilliseconds(250);

    private bool _hasRead;
    private long _readAt;
    private long? _headroomBytes;
    private long _issuedSinceRead; // 직전 조회 뒤에 내도록 허락한 묶음의 바이트: 같은 조회 결과를 잇단 묶음이 거듭 쓰지 않게 뺌

    /// 이번 스트림 동안 판정에 쓴 가장 작은 여유 (조회 결과에서 그 뒤 허락한 묶음을 뺀 값, 바이트), 성공한 조회가 없으면 null.
    public long? MinBytes { get; private set; }

    /// 여유가 모자라 (또는 조회가 안 돼) 여유가 돌아올 때까지 기다린 묶음의 수 (기다린 뒤 낸 것도 셈) 와 기다린 시간 (초).
    public long HeldBatches { get; private set; }

    public double HeldSeconds { get; private set; }

    /// 가장 최근 조회가 실패했는가 (호출한 쪽이 스트림 하나에 한 번만 로그를 남기려고 봅니다).
    public bool LookupFailed => _hasRead && _headroomBytes is null;

    public static bool Enough(long headroomBytes, long nextBytes) => headroomBytes >= nextBytes + SpareBytes;

    /// 새 스트림을 시작: 기록값만 지웁니다 (조회 결과와 그 뒤 허락한 양은 이어 씀).
    public void BeginStream() => (MinBytes, HeldBatches, HeldSeconds) = (null, 0, 0);

    /// nextBytes 크기의 묶음을 지금 내면 안 되는가. 조회가 실패하면 여유가 없는 것으로 봅니다.
    public bool Short(long nextBytes)
    {
        long now = timestamp();
        if (!_hasRead || Stopwatch.GetElapsedTime(_readAt, now) >= MaxAge)
        {
            _hasRead = true;
            _readAt = now;
            _issuedSinceRead = 0;
            _headroomBytes = Query();
        }

        if (_headroomBytes is not { } headroom)
        {
            return true;
        }

        long left = Math.Max(0, headroom - _issuedSinceRead);
        MinBytes = MinBytes is { } min ? Math.Min(min, left) : left;
        return !Enough(left, nextBytes);
    }

    /// 여유가 있다고 보고 묶음을 내기로 했을 때 부릅니다: 그 크기를 같은 조회 결과에서 뺍니다.
    public void NoteIssued(long bytes) => _issuedSinceRead += Math.Max(0, bytes);

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
