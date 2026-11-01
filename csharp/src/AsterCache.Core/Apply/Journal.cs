using System.Text;
using AsterCache.Core.Json;

namespace AsterCache.Core.Apply;

/// 실행 기록 (JSON Lines, 줄마다 fsync). 이어하기와 되돌리기의 힌트일 뿐이고, 진실은 항상 파일의 현재 extent 와 SHA 표본입니다.
/// 줄바꿈은 Windows 텍스트 모드 Python 과 같은 CRLF 입니다. 읽을 때는 LF 도 받습니다.
public sealed class Journal : IDisposable
{
    private readonly bool _fixNewline;
    private readonly List<JObject> _rows = [];
    private FileStream? _file;

    /// 파일이 있으면 읽습니다. 빈 줄은 무시하고, 마지막 줄만 깨졌으면 (쓰다 멈춤) 버리며, 중간 줄이 깨졌으면 PyValueException.
    public Journal(string path)
    {
        Path = path;
        if (!File.Exists(path))
        {
            return;
        }

        byte[] bytes = File.ReadAllBytes(path);
        string text = new UTF8Encoding(false, true).GetString(bytes);
        List<string> lines = SplitLines(text).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        for (int i = 0; i < lines.Count; i++)
        {
            try
            {
                _rows.Add(JsonRead.Parse(lines[i]) as JObject ?? throw new PyValueException("journal row is not an object"));
            }
            catch (PyValueException) when (i == lines.Count - 1)
            {
                _fixNewline = true; // 쓰다 멈춘 마지막 줄: 버리고, 다음 쓰기는 새 줄에서 시작
            }
        }

        // 온전한 JSON 이어도 줄바꿈 없이 끝났으면 (쓰기 도중 중단) 다음 줄이 이어 붙지 않도록 다시 씁니다.
        _fixNewline |= bytes.Length > 0 && bytes[^1] != (byte)'\n';
    }

    public string Path { get; }

    public IReadOnlyList<JObject> Rows => _rows;

    /// type 이 kind 이고 key 값이 val 인 첫 행. val 은 long/string/null 로 비교합니다.
    public JObject? First(string kind, string key, object? val) =>
        _rows.FirstOrDefault(r => r.TryGetValue("type", out object? t) && Equals(t, kind) && r.TryGetValue(key, out object? v) && Equals(v, val));

    /// idx 단계에 마지막으로 기록된 state, 없으면 null.
    public string? LastState(long idx)
    {
        string? state = null;
        foreach (JObject r in _rows)
        {
            if (r.TryGetValue("type", out object? t) && Equals(t, "step") && r.TryGetValue("idx", out object? i) && Equals(i, idx)
                && r.TryGetValue("state", out object? s))
            {
                state = s as string;
            }
        }

        return state;
    }

    /// 한 줄 쓰고 디스크까지 내립니다 (Flush(true)).
    public void Add(JObject row)
    {
        if (_file is null)
        {
            if (_fixNewline)
            {
                // 잘린 조각을 먼저 지움. 아니면 그것이 깨진 중간 줄이 됩니다.
                File.WriteAllBytes(Path, PyJson.Utf8.GetBytes(string.Concat(_rows.Select(Line))));
            }

            _file = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
        }

        byte[] line = PyJson.Utf8.GetBytes(Line(row));
        _file.Write(line);
        _file.Flush(true);
        _rows.Add(row);
    }

    public void Close()
    {
        _file?.Dispose();
        _file = null;
    }

    public void Dispose() => Close();

    private static string Line(JObject row) => PyJson.Dumps(row) + "\r\n";

    // str.splitlines 처럼 \r\n, \n, \r 을 모두 줄 끝으로 봅니다 (저널 JSON 은 ensure_ascii 라 다른 구분자가 날것으로 나오지 않음).
    private static IEnumerable<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
}
