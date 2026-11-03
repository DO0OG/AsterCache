namespace AsterCache.Service;

/// 쓰기(재배치) 뒤 드라이브가 아직 회복 중인가. drive 는 Path.GetPathRoot 형식 볼륨 루트(예 "E:\"), 대소문자 무시.
public interface IDriveRecovery
{
    bool IsRecovering(string drive);
}
