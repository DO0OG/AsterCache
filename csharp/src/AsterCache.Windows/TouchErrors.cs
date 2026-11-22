using System.Globalization;

namespace AsterCache.Windows;

/// 만지기 오류의 종류: 파일 열기 실패, 파일 끝 밖의 범위, 매핑 실패, 읽기 실패.
public enum TouchErrorKind
{
    Open,
    BeyondEnd,
    Map,
    Read,
}

/// 만지기 오류를 종류별로 센 값 (StandbyTouch.Run 한 번 또는 여러 번을 합친 것): 종류별 수, 종류별 Win32 오류 번호 분포, 가장 많이 실패한 파일의 경로.
/// 수는 TouchResult.Errors 와 같은 단위 (만지지 못한 범위 수) 라 종류별 수의 합이 그 값과 같습니다. 기록만 하고 만지기의 동작에는 쓰지 않습니다.
public sealed class TouchErrors
{
    /// Open 종류의 코드: 크기 0 파일 (매핑할 수 없음, Win32 오류가 아님).
    public const int EmptyFile = -1;

    /// 경로별 수를 세는 파일 수의 상한: 넘으면 새 경로는 세지 않습니다 (shortcut: 이미 센 경로의 수는 계속 늘고, 종류별 수와 합계는 영향이 없음).
    public const int MaxPaths = 4096;

    private readonly int[] _counts = new int[4];
    private readonly Dictionary<(TouchErrorKind Kind, int Code), int> _codes = [];
    private readonly Dictionary<string, int> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string>[] _files = [.. Enumerable.Range(0, 4).Select(_ => new HashSet<string>(StringComparer.OrdinalIgnoreCase))]; // 종류별로 실패한 파일 (수는 MaxPaths 까지만 셈)

    public int Count(TouchErrorKind kind) => _counts[(int)kind];

    /// 종류 하나에서 실패한 서로 다른 파일의 수 (범위 수와 달리 같은 파일은 한 번).
    public int Files(TouchErrorKind kind) => _files[(int)kind].Count;

    public int Total => _counts.Sum();

    /// 가장 많이 실패한 파일의 경로와 그 수 (오류가 없으면 null).
    public (string Path, int Count)? Worst
    {
        get
        {
            if (_paths.Count == 0)
            {
                return null;
            }

            KeyValuePair<string, int> worst = _paths.MaxBy(p => p.Value);
            return (worst.Key, worst.Value);
        }
    }

    /// 종류 하나의 오류 번호별 수, 많은 것부터.
    public IReadOnlyList<(int Code, int Count)> Codes(TouchErrorKind kind) => [.. _codes.Where(c => c.Key.Kind == kind).OrderByDescending(c => c.Value).Select(c => (c.Key.Code, c.Value))];

    public void Add(TouchErrorKind kind, int count, string path, int code)
    {
        if (count <= 0)
        {
            return;
        }

        _counts[(int)kind] += count;
        _codes[(kind, code)] = _codes.GetValueOrDefault((kind, code)) + count;
        if (_paths.TryGetValue(path, out int known) || _paths.Count < MaxPaths)
        {
            _paths[path] = known + count;
        }

        if (_files[(int)kind].Count < MaxPaths)
        {
            _ = _files[(int)kind].Add(path);
        }
    }

    /// 둘을 합친 값 (앞의 것이 있으면 거기에 더해 돌려주고, 없으면 뒤의 것): 나눠 만진 결과의 오류를 합칠 때.
    public static TouchErrors? Combine(TouchErrors? first, TouchErrors? second)
    {
        if (first is null || second is null)
        {
            return first ?? second;
        }

        first.Merge(second);
        return first;
    }

    /// 다른 값을 더합니다 (여러 번 나눠 만진 결과를 합칠 때).
    public void Merge(TouchErrors other)
    {
        foreach (((TouchErrorKind kind, int code), int count) in other._codes)
        {
            _counts[(int)kind] += count;
            _codes[(kind, code)] = _codes.GetValueOrDefault((kind, code)) + count;
        }

        foreach ((string path, int count) in other._paths)
        {
            if (_paths.TryGetValue(path, out int known) || _paths.Count < MaxPaths)
            {
                _paths[path] = known + count;
            }
        }

        for (int k = 0; k < _files.Length; k++)
        {
            foreach (string path in other._files[k])
            {
                if (_files[k].Count < MaxPaths)
                {
                    _ = _files[k].Add(path);
                }
            }
        }
    }

    /// 로그 한 줄용 서술: `open 12 ranges in 3 files (win32 5 ×12), beyond end 3 ranges in 1 file; most failures in C:\a\b.bin ×15`. 오류가 없으면 빈 문자열.
    public string Describe()
    {
        List<string> kinds = [];
        foreach ((TouchErrorKind kind, string label) in new[] { (TouchErrorKind.Open, "open"), (TouchErrorKind.BeyondEnd, "beyond end"), (TouchErrorKind.Map, "map"), (TouchErrorKind.Read, "read") })
        {
            if (Count(kind) == 0)
            {
                continue;
            }

            string codes = string.Join(", ", Codes(kind).Take(3)
                .Select(c => $"{(c.Code == EmptyFile ? "empty file" : c.Code == 0 ? "no win32 error" : $"win32 {c.Code}")} ×{c.Count.ToString(CultureInfo.InvariantCulture)}"));
            int files = Files(kind);
            string spread = $" {(Count(kind) == 1 ? "range" : "ranges")} in {files} {(files == 1 ? "file" : "files")}"; // 범위 수 (3882) 가 파일 몇 개에 걸친 것인지
            kinds.Add($"{label} {Count(kind)}{spread}{(kind == TouchErrorKind.BeyondEnd || codes.Length == 0 ? string.Empty : $" ({codes})")}");
        }

        return kinds.Count == 0 ? string.Empty : $"{string.Join(", ", kinds)}{(Worst is { } worst ? $"; most failures in {worst.Path} ×{worst.Count}" : string.Empty)}";
    }
}
