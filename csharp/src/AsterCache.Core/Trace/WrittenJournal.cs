namespace AsterCache.Core.Trace;

/// NTFS 변경 저널 (USN) 기록 하나 중 필요한 것: 파일 참조 번호, 기록 시각 (UTC), 사유 비트.
public readonly record struct UsnEvent(ulong Frn, DateTime TimeUtc, uint Reason);

/// 변경 저널을 읽은 결과: 저널에 남은 가장 오래된 기록의 시각 (이보다 앞선 구간은 저널이 덮지 못함)과 찾는 파일들의 기록.
public sealed record UsnRead(DateTime OldestUtc, IReadOnlyList<UsnEvent> Events);

/// 플레이 한 번의 구간 (I/O 집계의 관찰 시작·끝)과 그 번호.
public readonly record struct SessionSpan(string Session, DateTime StartUtc, DateTime EndUtc);

/// 변경 저널로 본 한 플레이의 파일 변화: 기록 없음, 확장·덮어쓰기만, 잘라내기 (크기 줄임), 교체 (만들기·삭제·이름 바꾸기).
public enum JournalVerdict
{
    Unchanged,
    Extended,
    Truncated,
    Replaced,
}

/// 변경 저널 사유 비트 (USN_REASON_*)와 플레이 구간 판정.
public static class UsnReasons
{
    public const uint DataOverwrite = 0x1;
    public const uint DataExtend = 0x2;
    public const uint DataTruncation = 0x4;
    public const uint FileCreate = 0x100;
    public const uint FileDelete = 0x200;
    public const uint RenameOldName = 0x1000;
    public const uint RenameNewName = 0x2000;

    /// 파일 하나의 기록 (events) 에서 [from, to] 구간의 변화. 구간이 저널이 덮는 범위 안에 온전히 들어가지 않으면 (from 이 oldest 보다 앞) null: 그 구간은 세지 않습니다.
    /// 우선순위는 잘라내기 > 교체 > 확장·덮어쓰기 > 기록 없음.
    public static JournalVerdict? Judge(IEnumerable<UsnEvent> events, DateTime from, DateTime to, DateTime oldest)
    {
        if (from < oldest)
        {
            return null;
        }

        uint reasons = 0;
        foreach (UsnEvent e in events)
        {
            if (e.TimeUtc >= from && e.TimeUtc <= to)
            {
                reasons |= e.Reason;
            }
        }

        if ((reasons & DataTruncation) != 0)
        {
            return JournalVerdict.Truncated;
        }

        if ((reasons & (FileCreate | FileDelete | RenameOldName | RenameNewName)) != 0)
        {
            return JournalVerdict.Replaced;
        }

        return (reasons & (DataOverwrite | DataExtend)) != 0 ? JournalVerdict.Extended : JournalVerdict.Unchanged;
    }
}
