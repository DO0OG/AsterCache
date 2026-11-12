using System.Globalization;
using System.Text.RegularExpressions;

namespace AsterCache.Tray.Update;

/// SemVer 2.0 (semver.org 정규식 그대로, 빌드 메타데이터는 비교에서 제외). 버전 문자열은 설치 파일 이름에 들어가므로 이 엄격한 문법만 받습니다.
public readonly partial record struct SemVer(int Major, int Minor, int Patch, string[] Pre, string Text) : IComparable<SemVer>
{
    public bool IsPrerelease => Pre.Length > 0;

    [GeneratedRegex(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-((?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?(?:\+([0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?$")]
    private static partial Regex Grammar();

    /// "1.2.3", "1.2.3-rc.1", "1.2.3+build" 형식만. 앞의 v 나 공백은 받지 않습니다 (부르는 쪽이 정함).
    public static bool TryParse(string? text, out SemVer version)
    {
        version = default;
        if (text is null || text.Length > 64 || Grammar().Match(text) is not { Success: true } m)
        {
            return false;
        }

        if (!int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int major)
            || !int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int minor)
            || !int.TryParse(m.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int patch))
        {
            return false;
        }

        string[] pre = m.Groups[4].Success ? m.Groups[4].Value.Split('.') : [];
        version = new SemVer(major, minor, patch, pre, text);
        return true;
    }

    /// 빌드 메타데이터 (+…) 를 뺀 문자열.
    public string Core => Text.Split('+', 2)[0];

    public int CompareTo(SemVer other)
    {
        int c = Major.CompareTo(other.Major);
        c = c != 0 ? c : Minor.CompareTo(other.Minor);
        c = c != 0 ? c : Patch.CompareTo(other.Patch);
        if (c != 0)
        {
            return c;
        }

        if (Pre.Length == 0 || other.Pre.Length == 0)
        {
            return other.Pre.Length.CompareTo(Pre.Length); // 프리릴리스가 없는 쪽이 높음
        }

        for (int i = 0; i < Math.Min(Pre.Length, other.Pre.Length); i++)
        {
            c = ComparePart(Pre[i], other.Pre[i]);
            if (c != 0)
            {
                return c;
            }
        }

        return Pre.Length.CompareTo(other.Pre.Length);
    }

    public bool Equals(SemVer other) => CompareTo(other) == 0;

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, string.Join('.', Pre));

    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;

    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;

    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;

    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;

    public override string ToString() => Core;

    // 숫자만인 식별자는 숫자로 (자릿수가 길어도 넘치지 않게 길이 먼저), 숫자가 문자 섞인 것보다 낮음
    private static int ComparePart(string a, string b)
    {
        bool an = a.All(char.IsAsciiDigit);
        bool bn = b.All(char.IsAsciiDigit);
        if (an && bn)
        {
            int c = a.Length.CompareTo(b.Length);
            return c != 0 ? c : string.CompareOrdinal(a, b);
        }

        return an ? -1 : bn ? 1 : string.CompareOrdinal(a, b);
    }
}
