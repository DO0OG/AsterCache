namespace AsterCache.Core;

/// 상태 파일 교체: 임시 파일을 디스크까지 내린 뒤 (WriteThrough + Flush(true)) 이름을 바꿉니다. 정전이 나도 빈 파일이 목적지를 덮어쓰지 않게 하려는 것입니다.
public static class DurableFile
{
    public static void WriteAllBytes(string path, byte[] bytes)
    {
        using var f = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        f.Write(bytes, 0, bytes.Length);
        f.Flush(true);
    }

    /// 디스크 쓰기 캐시를 비우지 않는 교체용 쓰기 (WriteThrough·Flush(true) 없음): 프로세스가 죽어도 운영체제가 살아 있으면 캐시의 내용이 디스크에 내려가므로 충분한 상태 파일에만 씁니다.
    /// 정전이면 재부팅되어 파일이 필요 없는 경우에 한합니다.
    public static void WriteAllBytesCached(string path, byte[] bytes) => File.WriteAllBytes(path, bytes);
}
