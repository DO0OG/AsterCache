using System.Text.RegularExpressions;
using AsterCache.Core.Json;

namespace AsterCache.Core.Profiles;

/// 포함 규칙 하나: glob (설치 폴더 기준 상대 경로) 과, 각 파일의 앞 HeadMib MiB 만 읽을지 (0 = 파일 전체).
/// TailMib 가 0 보다 크면 (HeadMib 와 함께) 파일 끝 TailMib MiB 도 읽습니다: 인덱스·푸터가 파일 끝에 있는 형식 (Unreal .pak) 용. 파일이 앞+뒤 이하면 겹치지 않게 전체를 한 번만 읽습니다.
public sealed record ProfileInclude(string Glob, int HeadMib, int TailMib = 0);

/// 엔진 하나: 감지 규칙 (이 중 하나라도 맞는 파일이 있으면 그 엔진) 과 포함 규칙 (앞에 있을수록 우선).
public sealed record EngineProfile(string Name, IReadOnlyList<string> Detect, IReadOnlyList<ProfileInclude> Include);

/// 고른 파일 하나의 구간: Offset 부터 Bytes 바이트 (기본은 파일 앞쪽부터, 꼬리 구간은 Offset 이 0 이 아님).
public sealed record ProfilePick(string Path, long Bytes, long Offset = 0);

public sealed record ProfileSelection(IReadOnlyList<string> Engines, IReadOnlyList<ProfilePick> Picks)
{
    public long TotalBytes => Picks.Sum(p => p.Bytes);
}

/// 엔진 기본 프로필 (engines.json, 임베디드 리소스): 기록이 없는 큰 게임에서 "로딩 초반에 반드시 읽는 파일 집합" 을 고릅니다.
/// glob: '/' 또는 '\' 로 나눈 칸마다 '*' '?' (대소문자 무시), 한 칸 전체가 '**' 이면 0 개 이상의 폴더. '&lt;name&gt;' 같은 꺾쇠 이름은 한 칸 와일드카드.
/// 읽기 전용 순수 계산이며 파일에는 닿지 않습니다 (파일 목록은 호출자가 줌).
public sealed class EngineProfiles
{
    public const string ResourceName = "AsterCache.Core.Profiles.engines.json";

    /// 공통 포함 규칙의 접두어: 게임 exe 가 있는 폴더 (설치 폴더 기준 상대) 로 바뀜.
    public const string ExeDir = "{exe_dir}";

    private static readonly Lazy<EngineProfiles> BuiltinProfiles = new(LoadBuiltin);

    public EngineProfiles(IReadOnlyList<EngineProfile> engines, IReadOnlyList<ProfileInclude> common)
    {
        Engines = engines;
        Common = common;
    }

    public IReadOnlyList<EngineProfile> Engines { get; }

    public IReadOnlyList<ProfileInclude> Common { get; }

    public static EngineProfiles Builtin => BuiltinProfiles.Value;

    public static EngineProfiles Parse(string json)
    {
        var root = JsonRead.Parse(json) as JObject ?? throw new PyValueException("engines: object expected");
        var engines = new List<EngineProfile>();
        foreach (object? item in root.TryGetValue("engines", out object? e) && e is List<object?> list ? list : [])
        {
            var o = item as JObject ?? throw new PyValueException("engines: object expected");
            string name = o["name"] as string ?? throw new PyValueException("engines.name: string expected");
            IReadOnlyList<string> detect = Strings(o, "detect", name);
            engines.Add(new EngineProfile(name, detect, Includes(o, "include")));
        }

        return new EngineProfiles(engines, Includes(root, "common"));
    }

    /// 파일 경로 (설치 폴더 기준 상대) 가 glob 에 맞는가.
    public static bool Match(string glob, string path) => MatchSegments(Split(glob, true), 0, Split(path, false), 0);

    /// 감지: files 중 감지 규칙에 맞는 것이 있는 엔진들 (profiles 파일의 순서대로).
    public IReadOnlyList<EngineProfile> Detect(IReadOnlyList<(string Path, long Size)> files) =>
        [.. Engines.Where(e => e.Detect.Any(d => files.Any(f => Match(d, f.Path))))];

    /// 엔진을 감지해 읽을 파일을 고릅니다: 공통 규칙 → 엔진별 규칙 순 (엔진은 모두 합침), 규칙마다 경로 순, 같은 파일은 한 번만 (먼저 맞은 규칙이 이김).
    /// 합이 budgetBytes 를 넘으면 앞에서부터 자르고 (넘기는 파일은 남은 만큼만 앞쪽부터), 엔진이 하나도 안 맞으면 아무것도 고르지 않습니다.
    /// exeDir: 게임 exe 가 있는 폴더 (설치 폴더 기준 상대, 설치 폴더면 빈 문자열), 모르면 null (공통 규칙은 건너뜀).
    public ProfileSelection Select(IReadOnlyList<(string Path, long Size)> files, string? exeDir, long budgetBytes)
    {
        IReadOnlyList<EngineProfile> hit = Detect(files);
        var picks = new List<ProfilePick>();
        if (hit.Count == 0)
        {
            return new ProfileSelection([], picks);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long left = budgetBytes;
        IEnumerable<ProfileInclude> rules = (exeDir is null ? [] : Common.Select(c => Anchor(c, exeDir))).Concat(hit.SelectMany(h => h.Include));
        foreach (ProfileInclude rule in rules)
        {
            foreach ((string path, long size) in files.Where(f => f.Size > 0 && Match(rule.Glob, f.Path)).OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
            {
                if (left <= 0)
                {
                    return new ProfileSelection([.. hit.Select(h => h.Name)], picks);
                }

                if (!seen.Add(path))
                {
                    continue;
                }

                long head = (long)rule.HeadMib << 20;
                long tail = head > 0 ? (long)rule.TailMib << 20 : 0;
                bool split = tail > 0 && size > head + tail; // 앞뒤가 겹치거나 맞닿으면 전체를 한 번만
                long want = head > 0 && (tail == 0 || split) ? Math.Min(size, head) : size;
                long take = Math.Min(want, left);
                picks.Add(new ProfilePick(path, take));
                left -= take;
                if (take < want)
                {
                    return new ProfileSelection([.. hit.Select(h => h.Name)], picks); // 예산이 차서 자름
                }

                if (split)
                {
                    long end = Math.Min(tail, left); // 예산이 모자라면 파일의 맨 끝쪽만
                    if (end > 0)
                    {
                        picks.Add(new ProfilePick(path, end, size - end));
                        left -= end;
                    }

                    if (end < tail)
                    {
                        return new ProfileSelection([.. hit.Select(h => h.Name)], picks);
                    }
                }
            }
        }

        return new ProfileSelection([.. hit.Select(h => h.Name)], picks);
    }

    private static ProfileInclude Anchor(ProfileInclude rule, string exeDir) =>
        rule.Glob.StartsWith(ExeDir, StringComparison.Ordinal)
            ? rule with { Glob = (exeDir.Length == 0 ? string.Empty : exeDir.TrimEnd('/', '\\') + "/") + rule.Glob[ExeDir.Length..].TrimStart('/', '\\') }
            : rule;

    private static EngineProfiles LoadBuiltin()
    {
        using Stream s = typeof(EngineProfiles).Assembly.GetManifestResourceStream(ResourceName) ?? throw new InvalidOperationException("engines.json resource missing");
        using var r = new StreamReader(s);
        return Parse(r.ReadToEnd());
    }

    private static IReadOnlyList<string> Strings(JObject o, string key, string engine) =>
        o.TryGetValue(key, out object? v) && v is List<object?> list && list.All(x => x is string)
            ? [.. list.Cast<string>()]
            : throw new PyValueException($"engines.{engine}.{key}: list of strings expected");

    private static IReadOnlyList<ProfileInclude> Includes(JObject o, string key)
    {
        if (!o.TryGetValue(key, out object? v) || v is not List<object?> list)
        {
            return [];
        }

        return [.. list.Select(x =>
        {
            var r = x as JObject ?? throw new PyValueException($"engines.{key}: object expected");
            string glob = r["glob"] as string ?? throw new PyValueException($"engines.{key}.glob: string expected");
            return new ProfileInclude(glob, Mib(r, "head_mib", key), Mib(r, "tail_mib", key));
        })];
    }

    private static int Mib(JObject r, string field, string key) =>
        r.TryGetValue(field, out object? h) ? h is long n and >= 0 and <= 1048576 ? (int)n : throw new PyValueException($"engines.{key}.{field}: 0..1048576 expected") : 0;

    // 꺾쇠 이름 (<name>, <Game>) 은 한 칸 와일드카드 '*' 로 바꿉니다.
    private static string[] Split(string text, bool isGlob)
    {
        if (isGlob)
        {
            text = Regex.Replace(text, "<[^<>/\\\\]*>", "*", RegexOptions.None, TimeSpan.FromSeconds(1));
        }

        return text.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool MatchSegments(string[] pat, int pi, string[] path, int si)
    {
        while (pi < pat.Length)
        {
            if (pat[pi] == "**")
            {
                for (int k = si; k <= path.Length; k++)
                {
                    if (MatchSegments(pat, pi + 1, path, k))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (si >= path.Length || !MatchName(pat[pi], path[si]))
            {
                return false;
            }

            pi++;
            si++;
        }

        return si == path.Length;
    }

    // '*' 는 0 자 이상, '?' 는 한 글자, 대소문자 무시.
    private static bool MatchName(string pattern, string name)
    {
        int p = 0;
        int n = 0;
        int star = -1;
        int mark = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = n;
            }
            else if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(name[n])))
            {
                p++;
                n++;
            }
            else if (star >= 0)
            {
                p = star + 1;
                n = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}
