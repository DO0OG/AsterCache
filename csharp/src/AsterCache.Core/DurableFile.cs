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
}
