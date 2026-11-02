namespace AsterCache.Tracer;

/// 읽기 이벤트의 파일 이름 표. FileKey 이름 (FileIOName / FileIOFileCreate / FileIOFileRundown) 이 확실하고, FileObject 이름 (FileIOCreate) 은
/// 주소가 닫힌 뒤 다른 파일에 재사용되므로 Close 에서 지우고 보조로만 씁니다. 실제 .etl 에서 FileObject 를 먼저 믿으면 읽기의 절반이 옛 파일로 분류됐습니다.
internal sealed class FileNames
{
    private readonly Dictionary<ulong, string> _byKey = [];
    private readonly Dictionary<ulong, string> _byObject = [];

    public void NameKey(ulong key, string? name) => Set(_byKey, key, name);

    public void NameObject(ulong obj, string? name) => Set(_byObject, obj, name);

    public void CloseObject(ulong obj) => _byObject.Remove(obj);

    /// 읽을 때: FileKey 이름이 이미 있으면 그것 (확정), 없으면 null 과 나중에 쓸 FileObject 이름 (없을 수 있음).
    public (string? Name, string? Fallback) AtRead(ulong key, ulong obj) =>
        _byKey.TryGetValue(key, out string? n) ? (n, null) : (null, _byObject.GetValueOrDefault(obj));

    /// 세션이 끝난 뒤 (정리 이벤트까지 받은 뒤) 미뤄 둔 읽기의 이름: FileKey 이름을 먼저, 없으면 읽을 때 붙잡은 FileObject 이름. 둘 다 없으면 null.
    public string? Settle(ulong key, string? fallback) => _byKey.TryGetValue(key, out string? n) ? n : fallback;

    private static void Set(Dictionary<ulong, string> table, ulong id, string? name)
    {
        if (id != 0 && !string.IsNullOrEmpty(name))
        {
            table[id] = name;
        }
    }
}
