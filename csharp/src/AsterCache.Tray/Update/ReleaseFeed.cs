using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AsterCache.Tray.Update;

/// 고정 저장소의 GitHub Releases 응답 해석과 대상 선택. 네트워크는 쓰지 않습니다.
public static partial class ReleaseFeed
{
    public const string Repo = "DO0OG/AsterCache";
    public const string ChecksumsName = "SHA256SUMS.txt";

    public static Uri MetadataUrl(bool includePrerelease) =>
        new($"https://api.github.com/repos/{Repo}/releases" + (includePrerelease ? "?per_page=20" : "/latest"));

    /// 설치 파일 이름은 이 고정 형식과 정확히 일치해야 합니다.
    /// 자산 내려받기 주소는 이 릴리스의 고정 경로와 정확히 같아야 합니다 (허용 호스트 안의 다른 저장소·다른 릴리스 경로 차단).
    public static string AssetUrl(string version, string name) => $"https://github.com/{Repo}/releases/download/v{version}/{name}";

    public static string InstallerName(string version) => $"AsterCache-v{version}-win-x64-setup.exe";

    [GeneratedRegex("^sha256:([0-9a-fA-F]{64})$")]
    private static partial Regex DigestGrammar();

    [GeneratedRegex(@"^([0-9a-fA-F]{64}) [ *](.+)$")]
    private static partial Regex SumsLine();

    /// 현재 버전보다 SemVer 가 높은 릴리스 중 가장 높은 것 (설치 파일 자산이 정확한 이름으로 있는 것만), 없으면 null.
    /// 프리릴리스는 설정이 켜졌을 때만. 모양이 틀린 릴리스는 건너뜁니다. 응답 전체가 JSON 이 아니면 UpdateException.
    public static ReleaseInfo? Select(byte[] json, SemVer current, bool includePrerelease)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            IEnumerable<JsonElement> releases = doc.RootElement.ValueKind switch
            {
                JsonValueKind.Array => doc.RootElement.EnumerateArray(),
                JsonValueKind.Object => [doc.RootElement],
                _ => throw new UpdateException("release list is not JSON object or array"),
            };
            ReleaseInfo? best = null;
            foreach (JsonElement r in releases)
            {
                if (Candidate(r, current, includePrerelease) is { } c && (best is null || c.Version > best.Version))
                {
                    best = c;
                }
            }

            return best;
        }
        catch (JsonException e)
        {
            throw new UpdateException("release metadata is not valid JSON: " + e.GetType().Name);
        }
    }

    private static ReleaseInfo? Candidate(JsonElement r, SemVer current, bool includePrerelease)
    {
        if (r.ValueKind != JsonValueKind.Object || Bool(r, "draft") || (Bool(r, "prerelease") && !includePrerelease)
            || Str(r, "tag_name") is not { Length: > 1 } tag || tag[0] != 'v' || tag.Contains('+')
            || !SemVer.TryParse(tag[1..], out SemVer version) || (version.IsPrerelease && !includePrerelease) || version <= current
            || !r.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string wanted = InstallerName(tag[1..]);
        JsonElement? installer = null;
        Uri? sums = null;
        foreach (JsonElement a in assets.EnumerateArray())
        {
            string? name = a.ValueKind == JsonValueKind.Object ? Str(a, "name") : null;
            if (name == wanted)
            {
                installer = a;
            }
            else if (name == ChecksumsName && Url(a, AssetUrl(tag[1..], ChecksumsName)) is { } u)
            {
                sums = u;
            }
        }

        if (installer is not { } inst || Url(inst, AssetUrl(tag[1..], wanted)) is not { } url
            || !inst.TryGetProperty("size", out JsonElement size) || !size.TryGetInt64(out long bytes) || bytes <= 0)
        {
            return null;
        }

        string? digest = Str(inst, "digest") is { } d && DigestGrammar().Match(d) is { Success: true } m ? m.Groups[1].Value.ToLowerInvariant() : null;
        return new ReleaseInfo(version, wanted, url, bytes, digest, sums, new Uri($"https://github.com/{Repo}/releases/tag/v{version.Core}"));
    }

    /// SHA256SUMS.txt ("<해시>  <파일 이름>" 줄들) 에서 해당 파일의 소문자 해시, 없거나 서로 다른 해시가 둘 이상이면 null. 이름은 정확히 일치해야 합니다.
    public static string? ChecksumFor(byte[] sums, string fileName)
    {
        string? found = null;
        foreach (string line in Encoding.UTF8.GetString(sums).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (SumsLine().Match(line.TrimEnd('\r')) is { Success: true } m && m.Groups[2].Value == fileName)
            {
                string hash = m.Groups[1].Value.ToLowerInvariant();
                if (found is not null && found != hash)
                {
                    return null; // 같은 파일이 다른 해시로 두 번 나오면 믿지 않음
                }

                found = hash;
            }
        }

        return found;
    }

    private static Uri? Url(JsonElement asset, string expected) =>
        Str(asset, "browser_download_url") is { } s && Uri.TryCreate(s, UriKind.Absolute, out Uri? u) && UpdateHttp.IsAllowed(u) && u.AbsoluteUri == expected ? u : null;

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement o, string name) =>
        o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;
}
