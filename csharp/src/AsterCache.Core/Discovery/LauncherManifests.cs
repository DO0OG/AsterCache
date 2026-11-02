using System.Text;
using AsterCache.Core.Json;

namespace AsterCache.Core.Discovery;

/// Steam appmanifest 한 개에서 읽은 값.
public sealed record SteamApp(string AppId, string Name, string InstallDir);

/// Epic 설치 항목 (*.item) 한 개에서 읽은 값. LaunchExecutable 은 없으면 빈 문자열.
public sealed record EpicApp(string AppName, string DisplayName, string InstallLocation, string LaunchExecutable);

/// 런처 설치 목록 파일의 순수 파서와 설정용 이름 만들기. 파일·레지스트리에는 접근하지 않습니다.
public static class LauncherManifests
{
    private const int MaxDepth = 16;
    private const int MaxNameLength = 64;

    // 게임이 아닌 Steam 항목 (공용 재배포 패키지, 호환 계층).
    private static readonly string[] NonGameNamePrefixes = ["Steamworks", "Proton", "Steam Linux Runtime"];

    /// Valve KeyValues 텍스트 (vdf/acf): 따옴표 문자열 (\\ \" \n \t 이스케이프), 따옴표 없는 낱말, { }, // 주석.
    /// 값은 string 또는 Dictionary (키는 대소문자 무시, 같은 키는 나중 것). 형식이 틀리면 FormatException.
    public static Dictionary<string, object> ParseKeyValues(string text)
    {
        int pos = 0;
        return ReadBlock(text, ref pos, 0);
    }

    /// libraryfolders.vdf → 라이브러리 경로 목록 (새 형식 "0" { "path" ... } 와 옛 형식 "1" "경로" 모두).
    public static IReadOnlyList<string> ParseLibraryFolders(string text)
    {
        var result = new List<string>();
        if (!ParseKeyValues(text).TryGetValue("libraryfolders", out object? lf) || lf is not Dictionary<string, object> libs)
        {
            return result;
        }

        foreach ((string key, object value) in libs)
        {
            string? path = value switch
            {
                Dictionary<string, object> d => d.GetValueOrDefault("path") as string,
                string s when key.All(char.IsAsciiDigit) => s,
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(path))
            {
                result.Add(path);
            }
        }

        return result;
    }

    /// appmanifest_*.acf → (appid, name, installdir). 필수 값이 없거나 게임이 아니면 null.
    public static SteamApp? ParseAppManifest(string text)
    {
        if (!ParseKeyValues(text).TryGetValue("AppState", out object? st) || st is not Dictionary<string, object> s
            || s.GetValueOrDefault("appid") is not string id || s.GetValueOrDefault("installdir") is not string dir || dir.Length == 0)
        {
            return null;
        }

        string name = s.GetValueOrDefault("name") as string ?? string.Empty;
        return id == "228980" || NonGameNamePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ? null : new SteamApp(id, name, dir);
    }

    /// Epic *.item (JSON) → 항목. InstallLocation 이 없거나 게임이 아니면 (DLC, 소프트웨어) null.
    public static EpicApp? ParseEpicItem(string json)
    {
        if (JsonRead.Parse(json) is not JObject o || Get(o, "InstallLocation") is not { Length: > 0 } loc)
        {
            return null;
        }

        string appName = Get(o, "AppName") ?? string.Empty;
        string main = Get(o, "MainGameAppName") ?? string.Empty;
        bool notGame = o.TryGetValue("AppCategories", out object? cats) && cats is List<object?> list && !list.Contains("games");
        return notGame || (main.Length > 0 && main != appName) ? null : new EpicApp(appName, Get(o, "DisplayName") ?? string.Empty, loc, Get(o, "LaunchExecutable") ?? string.Empty);
    }

    /// config.json 의 게임 이름 ([A-Za-z0-9._-]{1,64}): 후보를 순서대로 보고, 허용 밖 글자는 _ 로 바꿔 연속 _ 를 하나로 줄이고 양끝의 _ . 를 뗀 것이 비지 않은 첫 후보. 다 비면 "game".
    public static string SafeName(string display, string folder, string exeStem)
    {
        foreach (string candidate in new[] { display, folder, exeStem })
        {
            var sb = new StringBuilder();
            foreach (char c in candidate)
            {
                char x = char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '_';
                if (x != '_' || sb.Length == 0 || sb[^1] != '_')
                {
                    sb.Append(x);
                }
            }

            string name = sb.ToString().Trim('_', '.');
            if (name.Length > MaxNameLength)
            {
                name = name[..MaxNameLength].TrimEnd('_', '.');
            }

            if (name.Length > 0)
            {
                return name;
            }
        }

        return "game";
    }

    /// taken (대소문자 무시) 에 없는 이름으로 만들고 taken 에 넣습니다: 겹치면 -2, -3 ... 접미사 (전체 64자 이내).
    public static string MakeUnique(string name, ISet<string> taken)
    {
        string result = name;
        for (int n = 2; !taken.Add(result); n++)
        {
            string suffix = "-" + n;
            result = name[..Math.Min(name.Length, MaxNameLength - suffix.Length)] + suffix;
        }

        return result;
    }

    private static string? Get(JObject o, string key) => o.TryGetValue(key, out object? v) ? v as string : null;

    // 닫는 } 또는 (최상위면) 끝까지 읽어 키-값 사전을 만듭니다.
    private static Dictionary<string, object> ReadBlock(string text, ref int pos, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new FormatException("keyvalues: nested too deep");
        }

        var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            string? key = Next(text, ref pos, out bool isString);
            if (key is null || (!isString && key == "}"))
            {
                return (key is null) == (depth == 0) ? dict : throw new FormatException("keyvalues: unbalanced braces");
            }

            if (!isString && key == "{")
            {
                throw new FormatException("keyvalues: key expected");
            }

            string? value = Next(text, ref pos, out bool valueIsString);
            if (value is null || (!valueIsString && value == "}"))
            {
                throw new FormatException("keyvalues: value expected");
            }

            dict[key] = !valueIsString && value == "{" ? ReadBlock(text, ref pos, depth + 1) : value;
        }
    }

    // 다음 낱말. 따옴표 문자열이면 isString = true (내용이 { } 여도 구분), 따옴표 없는 낱말·{ · } 는 false. 끝이면 null.
    private static string? Next(string text, ref int pos, out bool isString)
    {
        isString = false;
        while (pos < text.Length)
        {
            if (char.IsWhiteSpace(text[pos]))
            {
                pos++;
            }
            else if (text[pos] == '/' && pos + 1 < text.Length && text[pos + 1] == '/')
            {
                while (pos < text.Length && text[pos] != '\n')
                {
                    pos++;
                }
            }
            else
            {
                break;
            }
        }

        if (pos >= text.Length)
        {
            return null;
        }

        if (text[pos] is '{' or '}')
        {
            return text[pos++].ToString();
        }

        var sb = new StringBuilder();
        if (text[pos] == '"')
        {
            isString = true;
            pos++;
            while (pos < text.Length && text[pos] != '"')
            {
                char c = text[pos++];
                if (c == '\\' && pos < text.Length)
                {
                    char e = text[pos++];
                    sb.Append(e switch { 'n' => '\n', 't' => '\t', _ => e });
                }
                else
                {
                    sb.Append(c);
                }
            }

            if (pos >= text.Length)
            {
                throw new FormatException("keyvalues: unterminated string");
            }

            pos++; // 닫는 따옴표
            return sb.ToString();
        }

        while (pos < text.Length && !char.IsWhiteSpace(text[pos]) && text[pos] is not ('{' or '}' or '"'))
        {
            sb.Append(text[pos++]);
        }

        return sb.ToString();
    }
}
