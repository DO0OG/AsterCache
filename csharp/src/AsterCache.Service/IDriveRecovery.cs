namespace AsterCache.Service;

/// 쓰기(재배치) 뒤 드라이브가 아직 회복 중인가. drive 는 Path.GetPathRoot 형식 볼륨 루트(예 "E:\"), 대소문자 무시.
public interface IDriveRecovery
{
    bool IsRecovering(string drive);

    /// 추가 재배치를 보류해야 하는가: 기본은 IsRecovering 과 같고, 회복을 확인하지 못한 probe 추적은 절대 상한까지 계속 보류합니다. 예열·고정은 IsRecovering 만 봄.
    bool IsRelocationHeld(string drive) => IsRecovering(drive);

    /// 업데이트 뒤 지연 (업데이트 시각 updatedAt 기준): 이 드라이브에 회복 추적이 있으면 아직 회복 중인지, 없으면 null (호출하는 쪽이 고정 분을 씀).
    bool? WatchUpdate(string drive, DateTimeOffset updatedAt, TimeSpan limit);

    /// 게임이 gameDrive 에서 시작했다: 같은 물리 디스크에서 재는 중인 회복 표본을 취소합니다 (기본: 하지 않음).
    void YieldTo(string gameDrive)
    {
    }
}
