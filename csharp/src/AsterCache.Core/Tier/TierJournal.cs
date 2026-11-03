using System.Globalization;
using AsterCache.Core.Json;

namespace AsterCache.Core.Tier;

public static class TierState
{
    /// 원래 자리에 SSD 사본으로 가는 링크가 있고 원본은 `.aster-orig` 로 HDD 에 있음.
    public const string Active = "active";

    /// 런처 업데이트 등으로 링크가 일반 파일로 바뀌었거나 사본이 링크를 통해 바뀜: 더는 관리하지 않고 파일은 그대로 둠.
    public const string Stale = "stale";

    /// 링크를 지우고 원래 이름을 되돌림 (SSD 사본은 남아 있을 수 있음).
    public const string Reverted = "reverted";
}

public sealed class TierEntry
{
    public required string Path { get; init; }

    public required string Target { get; init; }

    public required string Orig { get; init; }

    public required long Size { get; init; }

    public required string Sha { get; init; }

    /// 사본의 수정 시각 (Ticks): 링크를 통해 사본이 바뀌었는지 보는 기준.
    public required long MtimeTicks { get; init; }

    public required DateTimeOffset At { get; init; }

    public string State { get; set; } = TierState.Active;
}

/// 게임 하나의 SSD 보조 저장 저널 (<tier 저널 폴더>\<게임>.json). 읽을 수 없거나 항목이 규칙에 어긋나면 TierJournalException:
/// 호출한 쪽은 어떤 파일도 건드리지 않고 로그만 남기고 건너뜁니다.
public sealed class TierJournal
{
    public const int Version = 1;

    public required string Game { get; init; }

    public required string Root { get; set; }

    public int BudgetGib { get; set; }

    public DateTimeOffset AppliedAt { get; set; }

    public List<TierEntry> Entries { get; } = [];

    public IEnumerable<TierEntry> Live => Entries.Where(e => e.State == TierState.Active);

    public long ActiveBytes => Live.Sum(e => e.Size);

    public static TierJournal Load(string path)
    {
        try
        {
            if (JsonRead.ReadFile(path) is not JObject o || o.TryGetValue("version", out object? v) && v is not 1L)
            {
                throw new TierJournalException("object version 1 expected");
            }

            var j = new TierJournal { Game = Str(o, "game"), Root = Str(o, "root"), BudgetGib = (int)Num(o, "budget_gib"), AppliedAt = Time(Str(o, "applied_at")) };
            if (!o.TryGetValue("entries", out object? list) || list is not List<object?> rows)
            {
                throw new TierJournalException("entries expected");
            }

            foreach (object? row in rows)
            {
                var e = row as JObject ?? throw new TierJournalException("entry object expected");
                string state = Str(e, "state");
                j.Entries.Add(new TierEntry
                {
                    Path = Str(e, "path"), Target = Str(e, "target"), Orig = Str(e, "orig"), Size = Num(e, "size"), Sha = Str(e, "sha"), MtimeTicks = Num(e, "mtime_ticks"),
                    At = Time(Str(e, "at")), State = state is TierState.Active or TierState.Stale or TierState.Reverted ? state : throw new TierJournalException("bad state"),
                });
            }

            j.Validate();
            return j;
        }
        catch (Exception ex) when (ex is PyValueException or IOException or UnauthorizedAccessException or FormatException or InvalidCastException or OverflowException)
        {
            throw new TierJournalException(ex.GetType().Name + ": " + ex.Message);
        }
    }

    public void Save(string path)
    {
        var entries = Entries.Select(e => (object?)new JObject
        {
            { "path", e.Path }, { "target", e.Target }, { "orig", e.Orig }, { "size", e.Size }, { "sha", e.Sha }, { "mtime_ticks", e.MtimeTicks },
            { "at", Iso(e.At) }, { "state", e.State },
        }).ToList();
        var root = new JObject
        {
            { "version", (long)Version }, { "game", Game }, { "root", Root }, { "budget_gib", (long)BudgetGib }, { "applied_at", Iso(AppliedAt) }, { "entries", entries },
        };
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, PyJson.Utf8.GetBytes(PyJson.Dumps(root, indent: 2, ensureAscii: false) + "\n"));
        File.Move(tmp, path, true);
    }

    // 저널은 SYSTEM 이 이름을 바꾸고 링크를 지우는 근거라서, 모든 항목이 구조 규칙 (원본 이름 = 경로 + 접미사, 사본은 root 아래) 을 지켜야 합니다.
    private void Validate()
    {
        foreach (TierEntry e in Entries)
        {
            if (!System.IO.Path.IsPathFullyQualified(e.Path) || !string.Equals(e.Orig, e.Path + TierEngine.OrigSuffix, StringComparison.OrdinalIgnoreCase)
                || !TierEngine.IsUnder(e.Target, Root))
            {
                throw new TierJournalException("entry breaks the layout rules");
            }
        }
    }

    private static string Str(JObject o, string key) => o.TryGetValue(key, out object? v) && v is string s && s.Length > 0 ? s : throw new TierJournalException($"{key} expected");

    private static long Num(JObject o, string key) => o.TryGetValue(key, out object? v) && v is long n && n >= 0 ? n : throw new TierJournalException($"{key} expected");

    private static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Time(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

public sealed class TierJournalException(string message) : Exception(message);
