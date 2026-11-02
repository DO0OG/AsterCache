using System.Globalization;
using System.Text.RegularExpressions;
using AsterCache.Core.I18n;

namespace AsterCache.Cli;

internal enum ArgKind
{
    /// 값 없는 스위치 (argparse store_true).
    Flag,

    /// 값 하나, 여러 번 주면 마지막 값 (store).
    Single,

    /// 값 하나, 주는 대로 모음 (append).
    Many,

    /// 값 하나 이상 (nargs='+').
    List,
}

internal enum ArgType
{
    Text,
    Int,
    Double,
}

internal sealed record ArgOption(
    string Name, ArgKind Kind = ArgKind.Single, ArgType Type = ArgType.Text, bool Required = false, string? Default = null, string[]? Choices = null);

/// 인자 오류. CLI 는 stderr 에 한 줄 쓰고 종료 코드 2 로 끝냅니다 (Python argparse 의 parser.error).
internal sealed class ArgException : Exception
{
    public ArgException(string message) : base(message)
    {
    }
}

internal sealed class HelpRequestedException : Exception
{
}

internal sealed class ParsedArgs
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ArgOption> _spec;

    public ParsedArgs(IEnumerable<ArgOption> spec) => _spec = spec.ToDictionary(o => o.Name, StringComparer.Ordinal);

    /// 오류·안내 문구의 언어 (ko/en).
    public string Lang { get; init; } = L.En;

    public bool Flag(string name) => _values.ContainsKey(name);

    public bool Given(string name) => _values.ContainsKey(name);

    /// 줬거나 기본값이 있으면 그 값, 아니면 null.
    public string? Text(string name) => _values.TryGetValue(name, out List<string>? v) ? v[^1] : _spec.GetValueOrDefault(name)?.Default;

    public long? Int(string name) => Text(name) is { } s ? ParseInt(s) : null;

    public double? Double(string name) => Text(name) is { } s ? ParseDouble(s) : null;

    public IReadOnlyList<string> List(string name) => _values.TryGetValue(name, out List<string>? v) ? v : [];

    internal static long ParseInt(string s) =>
        long.Parse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture);

    internal static double ParseDouble(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    internal void Set(string name, string value, bool append)
    {
        if (!append || !_values.TryGetValue(name, out List<string>? list))
        {
            _values[name] = list = [];
        }

        list.Add(value);
    }

    internal void SetFlag(string name) => _values[name] = [];
}

/// 직접 만든 작은 인자 파서. argparse 와 같은 규칙: --opt value, --opt=value, 유일한 접두 줄임, 음수는 값, nargs='+'.
internal static partial class ArgParser
{
    public static ParsedArgs Parse(IReadOnlyList<ArgOption> spec, IReadOnlyList<string> args, string lang = L.En)
    {
        var result = new ParsedArgs(spec) { Lang = lang };
        var extras = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            string token = args[i];
            if (token is "-h" or "--help")
            {
                throw new HelpRequestedException();
            }

            if (!IsOption(token))
            {
                extras.Add(token);
                continue;
            }

            string name = token;
            string? inline = null;
            int eq = token.IndexOf('=', StringComparison.Ordinal);
            if (token.StartsWith("--", StringComparison.Ordinal) && eq > 0)
            {
                name = token[..eq];
                inline = token[(eq + 1)..];
            }

            ArgOption? opt = Find(spec, name, token, lang);
            if (opt is null)
            {
                extras.Add(token);
                continue;
            }

            string label = "--" + opt.Name;
            switch (opt.Kind)
            {
                case ArgKind.Flag:
                    if (inline is not null)
                    {
                        throw new ArgException(L.Get(lang, "Cli_Arg_ExplicitValue", label, PyRepr.Str(inline)));
                    }

                    result.SetFlag(opt.Name);
                    break;
                case ArgKind.List:
                    var values = new List<string>();
                    if (inline is not null)
                    {
                        values.Add(inline);
                    }
                    else
                    {
                        while (i + 1 < args.Count && !IsOption(args[i + 1]))
                        {
                            values.Add(args[++i]);
                        }
                    }

                    if (values.Count == 0)
                    {
                        throw new ArgException(L.Get(lang, "Cli_Arg_NeedOneOrMore", label));
                    }

                    for (int k = 0; k < values.Count; k++)
                    {
                        Check(opt, label, values[k], lang);
                        result.Set(opt.Name, values[k], append: k > 0);
                    }

                    break;
                default:
                    string value = inline ?? (i + 1 < args.Count && !IsOption(args[i + 1]) ? args[++i] : throw new ArgException(L.Get(lang, "Cli_Arg_NeedOne", label)));
                    Check(opt, label, value, lang);
                    result.Set(opt.Name, value, opt.Kind == ArgKind.Many);
                    break;
            }
        }

        string[] missing = spec.Where(o => o.Required && !result.Given(o.Name)).Select(o => "--" + o.Name).ToArray();
        if (missing.Length > 0)
        {
            throw new ArgException(L.Get(lang, "Cli_Arg_Required", string.Join(", ", missing)));
        }

        return extras.Count > 0 ? throw new ArgException(L.Get(lang, "Cli_Arg_Unrecognized", string.Join(" ", extras))) : result;
    }

    public static string Usage(string command, IReadOnlyList<ArgOption> spec, string lang = L.En) =>
        L.Get(lang, "Cli_Usage") + "aster " + command + string.Concat(spec.Select(o =>
        {
            string body = "--" + o.Name + (o.Kind == ArgKind.Flag ? "" : o.Kind == ArgKind.List ? " V [V ...]" : " V");
            return o.Required ? " " + body : " [" + body + "]";
        }));

    [GeneratedRegex(@"^-\d+$|^-\d*\.\d+$")]
    private static partial Regex NegativeNumber();

    private static bool IsOption(string token) => token.Length > 1 && token[0] == '-' && !NegativeNumber().IsMatch(token);

    private static ArgOption? Find(IReadOnlyList<ArgOption> spec, string name, string token, string lang)
    {
        if (!name.StartsWith("--", StringComparison.Ordinal))
        {
            return null;
        }

        string bare = name[2..];
        ArgOption? exact = spec.FirstOrDefault(o => o.Name == bare);
        if (exact is not null)
        {
            return exact;
        }

        ArgOption[] byPrefix = spec.Where(o => o.Name.StartsWith(bare, StringComparison.Ordinal)).ToArray();
        return byPrefix.Length switch
        {
            1 => byPrefix[0],
            0 => null,
            _ => throw new ArgException(L.Get(lang, "Cli_Arg_Ambiguous", token, string.Join(", ", byPrefix.Select(o => "--" + o.Name)))),
        };
    }

    private static void Check(ArgOption opt, string label, string value, string lang)
    {
        bool ok = opt.Type switch
        {
            ArgType.Int => long.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out _),
            ArgType.Double => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
            _ => true,
        };
        if (!ok)
        {
            throw new ArgException(L.Get(lang, "Cli_Arg_InvalidValue", label, L.Get(lang, opt.Type == ArgType.Int ? "Cli_Type_Int" : "Cli_Type_Float"), PyRepr.Str(value)));
        }

        if (opt.Choices is not null && !opt.Choices.Contains(value))
        {
            throw new ArgException(L.Get(lang, "Cli_Arg_InvalidChoice", label, PyRepr.Str(value), string.Join(", ", opt.Choices.Select(PyRepr.Str))));
        }
    }
}
