namespace AsterCache.Tracer;

/// 읽기 이벤트의 파일 이름 표. FileKey 이름 (FileIOName / FileIOFileCreate / FileIOFileRundown) 이 확실하고, FileObject 이름 (FileIOCreate) 은
/// 주소가 닫힌 뒤 다른 파일에 재사용되므로 Close 에서 지우고 보조로만 씁니다. 실제 .etl 에서 FileObject 를 먼저 믿으면 읽기의 절반이 옛 파일로 분류됐습니다.
///
/// 실시간 알림 (ResolveNow): 기록용 이름 (AtRead / Settle) 은 위 규칙 그대로 두고, 선행 읽기에는 읽는 순간 FileKey 이름 → FileObject 이름 순으로 즉시 풉니다.
/// 둘 다 없으면 FileKey 로 보류했다가 그 FileKey (또는 FileObject) 의 이름이 오면 onLate 로 늦게 전달합니다. 닫힌 FileObject 로는 풀지 않습니다.
internal sealed class FileNames(Action<string, long, long>? onLate = null)
{
    private readonly Dictionary<ulong, string> _byKey = [];
    private readonly Dictionary<ulong, string> _byObject = [];
    private readonly Dictionary<ulong, List<Held>> _held = []; // FileKey → 이름을 기다리는 읽기
    private readonly Dictionary<ulong, ulong> _heldKeyOfObject = []; // FileObject → 그 오브젝트로 보류한 마지막 FileKey

    /// 읽는 순간 풀려 전달된 읽기 수 / 늦게 전달된 읽기 수 / 아직 이름을 기다리는 읽기 수.
    public long Resolved { get; private set; }

    public long Late { get; private set; }

    public int Waiting => _held.Values.Sum(l => l.Count);

    public void NameKey(ulong key, string? name)
    {
        if (Set(_byKey, key, name) && _held.Remove(key, out List<Held>? list))
        {
            Release(list, name!);
        }
    }

    public void NameObject(ulong obj, string? name)
    {
        if (Set(_byObject, obj, name) && _heldKeyOfObject.Remove(obj, out ulong key) && _held.TryGetValue(key, out List<Held>? list))
        {
            List<Held> mine = list.FindAll(h => h.Obj == obj);
            list.RemoveAll(h => h.Obj == obj);
            if (list.Count == 0)
            {
                _held.Remove(key);
            }

            Release(mine, name!);
        }
    }

    public void CloseObject(ulong obj)
    {
        _byObject.Remove(obj);
        _heldKeyOfObject.Remove(obj); // 이 주소의 다음 이름은 다른 파일의 것
    }

    /// 읽을 때: FileKey 이름이 이미 있으면 그것 (확정), 없으면 null 과 나중에 쓸 FileObject 이름 (없을 수 있음).
    public (string? Name, string? Fallback) AtRead(ulong key, ulong obj) =>
        _byKey.TryGetValue(key, out string? n) ? (n, null) : (null, _byObject.GetValueOrDefault(obj));

    /// 실시간 알림용: 지금 풀리는 이름 (FileKey 우선, 없으면 열려 있는 FileObject) 을 돌려줍니다. 못 풀면 null 이고 읽기를 보류합니다.
    public string? ResolveNow(ulong key, ulong obj, long offset, long size)
    {
        (string? name, string? fallback) = AtRead(key, obj);
        name ??= fallback;
        if (name is not null)
        {
            Resolved++;
        }
        else if (key != 0)
        {
            if (!_held.TryGetValue(key, out List<Held>? list))
            {
                _held[key] = list = [];
            }

            list.Add(new Held(obj, offset, size));
            if (obj != 0)
            {
                _heldKeyOfObject[obj] = key;
            }
        }

        return name;
    }

    /// 세션이 끝난 뒤 (정리 이벤트까지 받은 뒤) 미뤄 둔 읽기의 이름: FileKey 이름을 먼저, 없으면 읽을 때 붙잡은 FileObject 이름. 둘 다 없으면 null.
    public string? Settle(ulong key, string? fallback) => _byKey.TryGetValue(key, out string? n) ? n : fallback;

    private void Release(List<Held> reads, string name)
    {
        foreach (Held h in reads)
        {
            Late++;
            onLate?.Invoke(name, h.Offset, h.Size);
        }
    }

    private static bool Set(Dictionary<ulong, string> table, ulong id, string? name)
    {
        if (id == 0 || string.IsNullOrEmpty(name))
        {
            return false;
        }

        table[id] = name;
        return true;
    }

    private readonly record struct Held(ulong Obj, long Offset, long Size);
}
