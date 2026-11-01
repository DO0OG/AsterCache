namespace AsterCache.Core.Json;

/// Python 문자열 비교 (코드 포인트 순서). string.CompareOrdinal 은 UTF-16 단위라 U+E000..U+FFFF 와 보조 평면 문자의 순서가 다릅니다.
public sealed class CodePointComparer : IComparer<string>
{
    public static readonly CodePointComparer Instance = new();

    public static int Compare(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            if (a[i] != b[i])
            {
                return Fix(a[i]).CompareTo(Fix(b[i]));
            }
        }

        return a.Length.CompareTo(b.Length);
    }

    int IComparer<string>.Compare(string? x, string? y) => Compare(x!, y!);

    // 서로게이트 (D800..DFFF) 를 E000..FFFF 위로 올려 보조 평면 문자가 BMP 문자보다 뒤에 오게 합니다.
    private static int Fix(char c) => c >= 0xE000 ? c - 0x800 : c >= 0xD800 ? c + 0x2000 : c;
}
