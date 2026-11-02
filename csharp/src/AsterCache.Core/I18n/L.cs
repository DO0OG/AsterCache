using System.Globalization;
using System.Runtime.InteropServices;

namespace AsterCache.Core.I18n;

/// 화면·명령줄 문구 표 (ko, en). InvariantGlobalization 이라 .resx 위성 어셈블리와 CultureInfo 를 쓰지 못하므로 코드 안의 표로 둡니다.
/// 서식 인자는 string.Format 의 {0} 자리표시자입니다. 표에 없는 키는 키 이름을 그대로 돌려줍니다 (시험이 코드의 모든 키가 표에 있는지 확인함).
public static partial class L
{
    public const string Ko = "ko";
    public const string En = "en";

    private const int PrimaryLanguageMask = 0x3FF;
    private const int KoreanPrimaryLanguage = 0x12;

    /// 프로그램이 시작할 때 Resolve 로 정합니다. 정하기 전 (시험) 에는 ko.
    public static string Current { get; set; } = Ko;

    public static bool IsLanguage(string? value) => value is Ko or En;

    /// 앞뒤 공백과 대소문자를 무시하고 "ko"/"en" 이면 그 값, 아니면 (auto·빈 값·잘못된 값) null.
    public static string? Normalize(string? value) => value?.Trim().ToLowerInvariant() is { } lower && IsLanguage(lower) ? lower : null;

    /// 현재 언어의 문구.
    public static string T(string key, params object?[] args) => Get(Current, key, args);

    /// 지정한 언어의 문구 (명령줄은 호출마다 언어가 달라 전역값을 바꾸지 않고 이 길을 씁니다).
    public static string Get(string lang, string key, params object?[] args)
    {
        if (!Table.TryGetValue(key, out (string Ko, string En) pair))
        {
            return key;
        }

        string format = lang == Ko ? pair.Ko : pair.En;
        return args.Length == 0 ? format : string.Format(CultureInfo.InvariantCulture, format, args);
    }

    /// 언어 결정 순서: 명령줄 옵션 > 환경변수 > 설정 파일 (ko/en 일 때만, auto·잘못된 값은 건너뜀) > OS 표시 언어.
    public static string Resolve(string? flag, string? env, string? config, Func<string>? os = null)
    {
        return Normalize(flag) ?? Normalize(env) ?? Normalize(config) ?? (os ?? OsLanguage)();
    }

    /// OS 표시 언어가 한국어면 ko, 아니면 (비 Windows·실패 포함) en. InvariantGlobalization 이라 CultureInfo 로 읽지 못해 Win32 로 읽습니다.
    public static string OsLanguage()
    {
        if (!OperatingSystem.IsWindows())
        {
            return En;
        }

        try
        {
            return (GetUserDefaultUILanguage() & PrimaryLanguageMask) == KoreanPrimaryLanguage ? Ko : En;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return En;
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial ushort GetUserDefaultUILanguage();
}
