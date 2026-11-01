namespace AsterCache.Tests.Windows;

/// 시험마다 쓰는 임시 폴더 (Dispose 때 지움). 시험 보조 클래스.
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = Directory.CreateTempSubdirectory("astercache-").FullName;
    }

    public string Path { get; }

    public string Combine(string name) => System.IO.Path.Combine(Path, name);

    /// 무작위 바이트 파일을 만들고 내용을 돌려줍니다.
    public byte[] WriteRandom(string name, int length)
    {
        byte[] data = new byte[length];
        Random.Shared.NextBytes(data);
        File.WriteAllBytes(Combine(name), data);
        return data;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch (IOException)
        {
            // 임시 폴더 정리 실패는 시험 결과와 무관.
        }
    }
}
