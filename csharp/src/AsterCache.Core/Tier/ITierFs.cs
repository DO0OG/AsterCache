namespace AsterCache.Core.Tier;

/// 파일의 (크기, 마지막 수정 시각 Ticks). 링크를 따라간 대상의 값입니다.
public readonly record struct FileStamp(long Length, long MtimeTicks);

/// SSD 보조 저장이 파일 시스템에 닿는 곳 전부. 시험은 메모리 가짜로 바꿔 링크 권한 없이 논리를 확인합니다.
public interface ITierFs
{
    /// 파일·폴더·링크 (끊어진 링크 포함) 가 있는가.
    bool Exists(string path);

    /// 재해석 지점 (링크·정션) 인가. 없으면 false.
    bool IsReparsePoint(string path);

    /// 심볼릭 링크의 대상, 링크가 아니거나 없으면 null.
    string? LinkTarget(string path);

    /// 링크를 따라간 대상의 크기·수정 시각, 없거나 끊어진 링크면 null.
    FileStamp? Stat(string path);

    /// 표본 SHA 용 읽기: offset 에서 length 바이트 (파일 끝이면 그보다 적게).
    byte[] ReadRange(string path, long offset, long length);

    /// 사본 만들기: 1 MiB 버퍼, 낮은 I/O 우선순위, 대상 폴더를 만들고 수정 시각을 원본과 맞춥니다. 대상이 있으면 덮어씁니다.
    void Copy(string source, string destination);

    /// 이름 바꾸기. overwrite 가 거짓이면 대상이 있을 때 실패합니다.
    void Move(string from, string to, bool overwrite);

    void CreateSymlink(string link, string target);

    /// 링크 자체만 지웁니다 (재해석 지점이 아니면 실패: 일반 파일은 지우지 않음).
    void DeleteLink(string link);

    /// 우리가 만든 사본·임시 파일 하나를 지웁니다 (원본 데이터에는 쓰지 않음).
    void DeleteFile(string path);

    /// 사용 가능한 여유 바이트 (path 가 있는 볼륨).
    long FreeBytes(string path);

    /// 고정 디스크의 드라이브 ("C:").
    IReadOnlyList<string> FixedDrives();

    /// tier 폴더를 만듭니다 (새로 만들 때만 관리자·SYSTEM 쓰기, 일반 사용자 읽기 전용 권한으로).
    void EnsureRoot(string root);

    /// root 아래의 빈 폴더를 지웁니다 (root 자체는 남김).
    void RemoveEmptyDirs(string root);
}
