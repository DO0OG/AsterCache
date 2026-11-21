using AsterCache.Core.Json;

namespace AsterCache.Core.Trace;

/// 게임이 플레이 중에 쓰는 파일의 플레이별 크기 기록 한 줄: 플레이 (I/O 집계의 세션 번호), 그때의 크기, 수정 시각 (UTC ticks).
public sealed record WrittenSession(string Session, long Size, long MTimeTicks);

/// 변경 저널로 판정한 한 플레이의 기록 (Verdict 는 JournalVerdict 의 한 글자: u 기록 없음, e 확장·덮어쓰기, t 잘라내기, r 교체).
public sealed record WrittenJournalSession(string Session, char Verdict);

/// 파일 하나의 기록. Shrank 는 한 번이라도 크기가 줄었거나 변경 저널에 잘라내기가 있었다는 표시이고 기록이 밀려나도 남습니다 (줄어든 적이 있는 파일은 영구히 조건 밖).
/// Frn 은 마지막으로 본 파일 참조 번호 (0 이면 모름), Journal 은 변경 저널이 온전히 덮은 플레이의 판정입니다 (옛 형식에는 없음).
public sealed record WrittenFile(string Path, bool Shrank, IReadOnlyList<WrittenSession> Sessions, long Frn = 0, IReadOnlyList<WrittenJournalSession>? Journal = null);

/// 크기 기록으로 본 판정: 붙들어도 되는 파일, 기록이 모자란 파일, 줄어든 적이 있는 파일.
public enum WrittenVerdict
{
    Ok,
    InsufficientHistory,
    Shrank,
}

/// 게임이 플레이 중에 쓰는 파일 (xpak_cache 의 데이터 파일 같은 것) 의 플레이별 크기 기록 (계획 폴더, 원시 트레이스 옆 한 파일).
/// 붙들기는 사용자 매핑 뷰가 있는 파일의 크기 줄이기가 오류 1224 로 실패하므로 크기가 줄어든 적이 없는 파일만 대상으로 삼고, 그것을 알려면 플레이마다 크기를 남겨야 합니다.
/// 플레이가 끝날 때마다 Record 로 더하고 (파일당 MaxSessionsPerFile 개, 파일 MaxFiles 개까지만 남김), 붙들기 시작 때 Judge 로 묻습니다. 기록이 없거나 읽을 수 없으면 빈 기록이라 아무것도 붙들지 않습니다.
public sealed class WrittenFileHistory
{
    public const int Version = 1;
    public const string Suffix = ".written.json";

    /// 붙들려면 필요한 기록된 플레이 수: 크기가 줄지 않은 플레이가 이만큼 쌓여야 합니다. 근거: 한 번의 크기 비교로는 파일이 줄어드는 경우를 가려낼 수 없고, 세 플레이면 두 번의 플레이 사이 변화를 봅니다.
    public const int MinSessions = 3;

    /// 파일당 남기는 플레이 수와 파일 수: 기록이 한 파일 (수십 KiB 이하) 로 유지되게 하는 상한입니다. 줄어든 적이 있다는 표시는 이 상한과 무관하게 남습니다.
    public const int MaxSessionsPerFile = 8;

    public const int MaxFiles = 256;

    private readonly List<WrittenFile> _files; // 가장 최근에 기록한 파일이 뒤

    public WrittenFileHistory(IEnumerable<WrittenFile>? files = null) => _files = [.. files ?? []];

    public IReadOnlyList<WrittenFile> Files => _files;

    /// 마지막으로 끝난 플레이가 끝났을 때의 게임 상태 표지 (게임 파일의 크기·수정 시각을 합친 값, 계산할 수 없었으면 null): 다음 플레이에서 붙들기 전에 같은 표지를 다시 계산해 다르거나 없으면 업데이트 직후로 보고 붙들지 않습니다.
    public string? GameState { get; set; }

    public static string PathFor(string rawTracePath) => rawTracePath + Suffix;

    /// 이 플레이 (session) 에 본 파일들의 크기를 더합니다. 크기 0 은 없어진 파일입니다 (줄어든 것으로 셈).
    /// 기록한 적이 없는 파일은 minBytes 보다 작으면 건너뜁니다 (작은 파일은 붙들지 않으므로 기록할 까닭이 없음). 같은 플레이를 다시 더하면 그 줄만 바뀝니다.
    public void Record(string session, IEnumerable<(string Path, long Size, long MTimeTicks)> seen, long minBytes)
    {
        foreach ((string path, long size, long ticks) in seen)
        {
            int at = _files.FindIndex(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
            if (at < 0 && size < minBytes)
            {
                continue;
            }

            WrittenFile file = at >= 0 ? _files[at] : new WrittenFile(path, false, []);
            List<WrittenSession> sessions = [.. file.Sessions];
            int same = sessions.FindIndex(s => s.Session == session);
            if (same >= 0)
            {
                sessions.RemoveAt(same);
            }

            bool shrank = file.Shrank || (sessions.Count > 0 && size < sessions[^1].Size);
            sessions.Add(new WrittenSession(session, size, ticks));
            if (sessions.Count > MaxSessionsPerFile)
            {
                sessions.RemoveRange(0, sessions.Count - MaxSessionsPerFile);
            }

            if (at >= 0)
            {
                _files.RemoveAt(at);
            }

            _files.Add(file with { Shrank = shrank, Sessions = sessions });
        }

        while (_files.Count > MaxFiles)
        {
            int drop = _files.FindIndex(f => !f.Shrank); // 줄어든 적이 있다는 영구 표시는 파일 수 상한 때문에 지우지 않음 (모두 그런 파일이면 가장 오래된 것부터)
            _files.RemoveAt(drop < 0 ? 0 : drop);
        }
    }

    /// 지금 크기가 currentSize 인 파일을 붙들어도 되는가: 줄어든 적이 있거나 (또는 마지막 기록보다 지금 작음) 아니면 근거가 모자라면 아님.
    /// recentPlays 는 가장 최근에 끝난 플레이부터의 번호들 (최신이 뒤): 그 가운데 마지막 MinSessions 개가 모두 변경 저널로 판정돼 있어야 저널 근거로 충분하고 (판정이 빠진 플레이가 끼면 저널 근거는 버림), 아니면 크기 기록 MinSessions 개로 갈음합니다.
    public WrittenVerdict Judge(string path, long currentSize, IReadOnlyList<string>? recentPlays = null)
    {
        WrittenFile? file = _files.Find(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        if (file is null)
        {
            return WrittenVerdict.InsufficientHistory;
        }

        if (file.Shrank || (file.Sessions.Count > 0 && currentSize < file.Sessions[^1].Size))
        {
            return WrittenVerdict.Shrank;
        }

        // 변경 저널이 온전히 덮은 플레이가 MinSessions 개 이상이고 잘라내기가 없으면 (있으면 위에서 Shrank) 설치 직후부터도 충분합니다. 저널을 읽지 못한 파일은 크기 기록의 플레이 수로 갈음합니다.
        if (recentPlays is { Count: >= MinSessions } && recentPlays.Skip(recentPlays.Count - MinSessions).All(p => (file.Journal ?? []).Any(j => j.Session == p)))
        {
            return WrittenVerdict.Ok;
        }

        return file.Sessions.Count < MinSessions ? WrittenVerdict.InsufficientHistory : WrittenVerdict.Ok;
    }

    /// 순위가 수정된 플레이의 읽기도 점수에 넣어도 되는 (덧붙이기로 보는) 파일인가: Judge 가 Ok 이고 변경 저널이 기록한 최근 플레이에 교체 (만들기·삭제·이름 바꾸기) 가 없음.
    /// 저널 기록이 없는 파일은 교체 여부를 모르므로 크기 기록만으로 판단합니다.
    public bool IsAppendOnly(string path, long currentSize, IReadOnlyList<string>? recentPlays = null) =>
        Judge(path, currentSize, recentPlays) == WrittenVerdict.Ok
        && _files.Find(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)) is { } file
        && !(file.Journal ?? []).Any(j => j.Verdict == 'r');

    /// 변경 저널에서 읽은 기록 (read, 못 읽었으면 null → 아무것도 바꾸지 않음) 으로 sessions 의 플레이들을 파일마다 판정해 더합니다. files 는 (경로, 파일 참조 번호) 이고 번호를 모르는 파일은 판정 불가라 건너뜁니다.
    /// 플레이의 판정 구간은 앞 플레이가 끝난 때 (저널에 남은 가장 오래된 기록 시각 이후) 부터 이 플레이가 끝날 때까지입니다 (플레이 사이의 잘라내기도 잡음, 첫 플레이는 자기 시작부터). 플레이가 저널이 덮는 범위 안에서 시작하지 않았으면 (시작이 가장 오래된 기록보다 앞) 그 플레이는 세지 않습니다.
    /// 이미 판정한 플레이는 다시 하지 않고, 잘라내기가 있으면 Shrank 를 켭니다. 저장된 번호와 다른 번호의 파일은 바뀌어 끼워진 것이라 마지막 플레이를 교체로 셉니다. 더한 판정 수를 돌려줍니다.
    public int RecordJournal(UsnRead? read, IEnumerable<(string Path, ulong? Frn)> files, IReadOnlyList<SessionSpan> sessions)
    {
        if (read is null)
        {
            return 0;
        }

        SessionSpan[] ordered = [.. sessions.OrderBy(s => s.StartUtc)];
        int added = 0;
        foreach ((string path, ulong? frn) in files)
        {
            int at = _files.FindIndex(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
            if (at < 0 || frn is not { } number)
            {
                continue;
            }

            WrittenFile file = _files[at];
            long stored = unchecked((long)number);
            bool swapped = file.Frn != 0 && file.Frn != stored;
            UsnEvent[] mine = [.. read.Events.Where(e => e.Frn == number)];
            List<WrittenJournalSession> journal = [.. file.Journal ?? []];
            bool shrank = file.Shrank;
            DateTime? previousEnd = null;
            for (int i = 0; i < ordered.Length; i++)
            {
                SessionSpan s = ordered[i];
                DateTime from = previousEnd ?? s.StartUtc;
                previousEnd = s.EndUtc;
                if (journal.Any(j => j.Session == s.Session) || s.StartUtc < read.OldestUtc)
                {
                    continue;
                }

                from = from > read.OldestUtc ? from : read.OldestUtc; // 저널이 덮지 못하는 플레이 사이의 앞부분은 알 수 없으므로 덮는 만큼만 봄

                JournalVerdict? verdict = UsnReasons.Judge(mine, from, s.EndUtc, read.OldestUtc);
                if (verdict is null)
                {
                    continue;
                }

                if (swapped && i == ordered.Length - 1)
                {
                    verdict = JournalVerdict.Replaced;
                }

                shrank |= verdict == JournalVerdict.Truncated;
                journal.Add(new WrittenJournalSession(s.Session, verdict switch
                {
                    JournalVerdict.Truncated => 't',
                    JournalVerdict.Replaced => 'r',
                    JournalVerdict.Extended => 'e',
                    _ => 'u',
                }));
                added++;
            }

            if (journal.Count > MaxSessionsPerFile)
            {
                journal.RemoveRange(0, journal.Count - MaxSessionsPerFile);
            }

            _files[at] = file with { Shrank = shrank, Frn = stored, Journal = journal };
        }

        return added;
    }

    /// 없거나 읽을 수 없거나 형식이 다르면 빈 기록 (부가 정보라 없으면 붙들지 않을 뿐).
    public static WrittenFileHistory Read(string path)
    {
        try
        {
            if (!File.Exists(path) || JsonRead.ReadFile(path) is not JObject root
                || !root.TryGetValue("version", out object? version) || version is not long v || v != Version
                || !root.TryGetValue("files", out object? filesValue) || filesValue is not List<object?> rows)
            {
                return new WrittenFileHistory();
            }

            var files = new List<WrittenFile>(rows.Count);
            foreach (object? row in rows)
            {
                if (row is not JObject o || !o.TryGetValue("path", out object? name) || name is not string text || text.Length == 0
                    || !o.TryGetValue("shrank", out object? shrankValue) || shrankValue is not bool shrank
                    || !o.TryGetValue("sessions", out object? sessionsValue) || sessionsValue is not List<object?> sessionRows)
                {
                    return new WrittenFileHistory();
                }

                var sessions = new List<WrittenSession>(sessionRows.Count);
                foreach (object? cells in sessionRows)
                {
                    if (cells is not List<object?> { Count: 3 } c || c[0] is not string id || c[1] is not long size || size < 0 || c[2] is not long ticks)
                    {
                        return new WrittenFileHistory();
                    }

                    sessions.Add(new WrittenSession(id, size, ticks));
                }

                long frn = 0; // 옛 형식에는 없는 선택 항목
                if (o.TryGetValue("frn", out object? frnValue))
                {
                    if (frnValue is not long parsedFrn)
                    {
                        return new WrittenFileHistory();
                    }

                    frn = parsedFrn;
                }

                var journal = new List<WrittenJournalSession>();
                if (o.TryGetValue("journal", out object? journalValue) && journalValue is not null)
                {
                    if (journalValue is not List<object?> journalRows)
                    {
                        return new WrittenFileHistory();
                    }

                    foreach (object? cells in journalRows)
                    {
                        if (cells is not List<object?> { Count: 2 } c || c[0] is not string id || c[1] is not string code || code.Length != 1 || !"uert".Contains(code[0], StringComparison.Ordinal))
                        {
                            return new WrittenFileHistory();
                        }

                        journal.Add(new WrittenJournalSession(id, code[0]));
                    }
                }

                files.Add(new WrittenFile(text, shrank, sessions, frn, journal));
            }

            return new WrittenFileHistory(files) { GameState = root.TryGetValue("game_state", out object? state) && state is string stateText ? stateText : null };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException or OverflowException or FormatException or ArgumentException)
        {
            return new WrittenFileHistory();
        }
    }

    /// 임시 파일에 쓴 뒤 바꿔 끼웁니다.
    public void Write(string path)
    {
        var root = new JObject
        {
            { "version", (long)Version },
            { "game_state", GameState },
            { "files", _files.Select(f => (object?)new JObject
                {
                    { "path", f.Path },
                    { "shrank", f.Shrank },
                    { "sessions", f.Sessions.Select(s => (object?)new List<object?> { s.Session, s.Size, s.MTimeTicks }).ToList() },
                    { "frn", f.Frn },
                    { "journal", (f.Journal ?? []).Select(j => (object?)new List<object?> { j.Session, j.Verdict.ToString() }).ToList() },
                }).ToList() },
        };
        string temp = path + ".tmp";
        string text = PyJson.Dumps(root, ensureAscii: false) + "\n";
        DurableFile.WriteAllBytes(temp, PyJson.Utf8.GetBytes(PyJson.ToCrlf(text)));
        File.Move(temp, path, true);
    }
}
