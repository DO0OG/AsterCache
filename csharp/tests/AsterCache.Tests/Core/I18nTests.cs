using System.Text.RegularExpressions;
using AsterCache.Cli;
using AsterCache.Core.I18n;
using AsterCache.Core.Settings;
using AsterCache.Tests.Cli;
using AsterCache.Tray;
using Xunit;

namespace AsterCache.Tests.Core;

/// 전역 언어(L.Current)·환경변수를 바꾸는 시험은 다른 시험과 겹치지 않게 한 묶음으로 순서대로 돌립니다.
[CollectionDefinition("Language", DisableParallelization = true)]
public sealed class LanguageCollection
{
}

/// 문구 표 (ko, en) 의 완전성과 언어 결정 순서.
public sealed partial class I18nTests
{
    private const string Plans = @"C:\ProgramData\AsterCache\plans";

    [GeneratedRegex(@"\{(\d+)(?::[^}]*)?\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"[\uAC00-\uD7A3]")]
    private static partial Regex Hangul();

    [GeneratedRegex("\"([A-Z][a-z]+_[A-Za-z_]+)\"")]
    private static partial Regex KeyLiteral();

    private static string[] Indexes(string text) => Placeholder().Matches(text).Select(m => m.Groups[1].Value).Distinct().Order().ToArray();

    [Fact]
    public void EveryKeyHasBothLanguagesWithTheSamePlaceholders()
    {
        foreach ((string key, (string ko, string en)) in L.Table)
        {
            Assert.False(string.IsNullOrWhiteSpace(ko), key + " ko is empty");
            Assert.False(string.IsNullOrWhiteSpace(en), key + " en is empty");
            Assert.Equal(Indexes(ko), Indexes(en));
        }
    }

    [Fact]
    public void EnglishTextHasNoHangulExceptTheLanguageName()
    {
        foreach ((string key, (string ko, string en)) in L.Table)
        {
            Assert.True(key == "Lang_Ko" || !Hangul().IsMatch(en), key + " en has Hangul");
            Assert.True(ko == en || Hangul().IsMatch(ko), key + " ko is not Korean");
        }
    }

    [Fact]
    public void SourceKeysExistInTheTableAndEveryTableKeyIsUsed()
    {
        string? csharp = AppContext.BaseDirectory;
        while (csharp is not null && !File.Exists(Path.Combine(csharp, "AsterCache.sln")))
        {
            csharp = Path.GetDirectoryName(csharp);
        }

        if (csharp is null)
        {
            return; // 소스 폴더가 없는 배치에서는 건너뜀
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(Path.Combine(csharp, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\obj\", StringComparison.Ordinal) && !f.Contains(@"\bin\", StringComparison.Ordinal) && Path.GetFileName(f) != "Strings.cs"))
        {
            foreach (Match m in KeyLiteral().Matches(File.ReadAllText(file)))
            {
                used.Add(m.Groups[1].Value);
            }
        }

        string[] unknown = used.Where(k => !L.Table.ContainsKey(k)).ToArray();
        string[] unused = L.Table.Keys.Where(k => !used.Contains(k)).ToArray();
        Assert.True(unknown.Length == 0, "keys missing from the table: " + string.Join(", ", unknown));
        Assert.True(unused.Length == 0, "table keys never used: " + string.Join(", ", unused));
    }

    [Fact]
    public void MissingKeyIsReturnedAsIs() => Assert.Equal("No_Such_Key", L.Get(L.En, "No_Such_Key"));

    [Fact]
    public void FormatArgumentsAreFilledPerLanguage()
    {
        Assert.Equal("Install update (1.2.3)", L.Get(L.En, "Menu_InstallUpdate", "1.2.3"));
        Assert.Equal("업데이트 설치 (1.2.3)", L.Get(L.Ko, "Menu_InstallUpdate", "1.2.3"));
    }

    [Theory]
    [InlineData("ko", "en", "en", "ko")]
    [InlineData(null, "en", "ko", "en")]
    [InlineData(null, null, "ko", "ko")]
    [InlineData(null, "auto", "en", "en")] // auto 와 잘못된 값은 건너뛰고 다음 단계
    [InlineData("fr", " KO ", "en", "ko")]
    [InlineData(null, null, null, "os")]
    [InlineData(null, null, "auto", "os")]
    [InlineData("auto", "", "bogus", "os")]
    public void ExplicitLanguageBeatsAutoAndOsIsTheLastStep(string? flag, string? env, string? config, string expected) =>
        Assert.Equal(expected == "os" ? L.En : expected, L.Resolve(flag, env, config, () => L.En));

    [Fact]
    public void AutoFollowsTheOsLanguage()
    {
        Assert.Equal(L.Ko, L.Resolve(null, null, "auto", () => L.Ko));
        Assert.Equal(L.Ko, L.Resolve(null, null, null, () => L.Ko));
        Assert.Equal(L.En, L.Resolve(null, null, null, () => L.En));
        Assert.True(L.IsLanguage(L.OsLanguage()));
    }

    [Fact]
    public void LanguageSettingDefaultsToAutoAndAcceptsOnlyKnownValues()
    {
        Assert.Equal("auto", SettingsStore.Parse("""{"games":[]}""", Plans).Language);
        foreach (string ok in new[] { "auto", "ko", "en" })
        {
            Assert.Equal(ok, SettingsStore.Parse($$"""{"games":[],"language":"{{ok}}"}""", Plans).Language);
        }

        foreach (string bad in new[] { "\"fr\"", "\"KO\"", "\"\"", "1", "null", "true" })
        {
            Assert.Throws<SettingsException>(() => SettingsStore.Parse($$"""{"games":[],"language":{{bad}}}""", Plans));
        }
    }

    [Fact]
    public void ReadLanguageIsLenientAboutOtherFields()
    {
        string dir = Path.Combine(Path.GetTempPath(), "aster_lang_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "config.json");
            Assert.Null(SettingsStore.ReadLanguage(path));
            File.WriteAllText(path, """{"games":"broken","language":"en"}""");
            Assert.Equal("en", SettingsStore.ReadLanguage(path));
            File.WriteAllText(path, """{"language":"auto"}""");
            Assert.Null(SettingsStore.ReadLanguage(path));
            File.WriteAllText(path, "not json");
            Assert.Null(SettingsStore.ReadLanguage(path));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ConfigEditorWritesAndValidatesLanguage()
    {
        string dir = Path.Combine(Path.GetTempPath(), "aster_lang_" + Guid.NewGuid().ToString("N"));
        try
        {
            var editor = new ConfigEditor(dir);
            editor.SetLanguage("en");
            Assert.Equal("en", editor.Load()!.Language);
            editor.SetLanguage("auto");
            Assert.Equal("auto", editor.Load()!.Language);
            Assert.Throws<SettingsException>(() => editor.SetLanguage("de"));
            Assert.Equal("auto", editor.Load()!.Language); // 거부되면 파일은 그대로
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public void ArgParserMessagesFollowTheRequestedLanguage()
    {
        ArgOption[] spec = [new("zone-mib", Type: ArgType.Int)];
        Assert.Contains("invalid int value", Assert.Throws<ArgException>(() => ArgParser.Parse(spec, ["--zone-mib", "x"])).Message, StringComparison.Ordinal);
        Assert.Contains("정수", Assert.Throws<ArgException>(() => ArgParser.Parse(spec, ["--zone-mib", "x"], L.Ko)).Message, StringComparison.Ordinal);
    }
}

[Collection("Language")]
public sealed class LanguageSwitchTests : IDisposable
{
    private readonly string? _env = Environment.GetEnvironmentVariable("ASTER_LANG");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ASTER_LANG", _env);
        L.Current = L.Ko;
    }

    [Fact]
    public void CliLangOptionSwitchesUsageAndErrors()
    {
        Environment.SetEnvironmentVariable("ASTER_LANG", null);
        (int code, string output, _) = CliTests.Run("--lang", "en", "--help");
        Assert.Equal(0, code);
        Assert.StartsWith("usage: aster {", output, StringComparison.Ordinal);

        (code, output, _) = CliTests.Run("--lang", "ko", "--help");
        Assert.Equal(0, code);
        Assert.StartsWith("사용법: aster {", output, StringComparison.Ordinal);

        Assert.StartsWith("usage: aster trace", CliTests.Run("--lang", "en", "trace", "--help").Out, StringComparison.Ordinal);
        Assert.StartsWith("사용법: aster trace", CliTests.Run("--lang", "ko", "trace", "--help").Out, StringComparison.Ordinal);

        (code, _, string err) = CliTests.Run("--lang", "en", "trace", "--out", "x.json");
        Assert.Equal(2, code);
        Assert.Contains("exactly one of --csv, --etl, --live", err, StringComparison.Ordinal);
        (code, _, err) = CliTests.Run("--lang", "ko", "trace", "--out", "x.json");
        Assert.Equal(2, code);
        Assert.Contains("정확히 하나가 필요함", err, StringComparison.Ordinal);

        (code, _, err) = CliTests.Run("--lang", "en", "nope");
        Assert.Equal(2, code);
        Assert.Contains("invalid choice: 'nope'", err, StringComparison.Ordinal);
        (code, _, err) = CliTests.Run("--lang", "ko", "nope");
        Assert.Equal(2, code);
        Assert.Contains("알 수 없는 명령: 'nope'", err, StringComparison.Ordinal);

        (code, _, err) = CliTests.Run("--lang", "fr", "--help");
        Assert.Equal(2, code);
        Assert.Contains("'fr'", err, StringComparison.Ordinal);
    }

    [Fact]
    public void CliLangOptionBeatsEnvironmentVariable()
    {
        Environment.SetEnvironmentVariable("ASTER_LANG", "ko");
        Assert.StartsWith("usage: aster {", CliTests.Run("--lang", "en", "--help").Out, StringComparison.Ordinal);
        Environment.SetEnvironmentVariable("ASTER_LANG", "en");
        Assert.StartsWith("사용법: aster {", CliTests.Run("--lang", "ko", "--help").Out, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentVariableChoosesTheLanguageWithoutTheOption()
    {
        Environment.SetEnvironmentVariable("ASTER_LANG", "en");
        Assert.StartsWith("usage: aster {", CliTests.Run("--help").Out, StringComparison.Ordinal);
        Environment.SetEnvironmentVariable("ASTER_LANG", "ko");
        Assert.StartsWith("사용법: aster {", CliTests.Run("--help").Out, StringComparison.Ordinal);
        Assert.Equal("ko", AsterCli.ResolveLanguage(null));
        Assert.Equal("en", AsterCli.ResolveLanguage("en"));
    }

    [Fact]
    public void ManagementWindowTextFollowsTheCurrentLanguage()
    {
        L.Current = L.En;
        Assert.Equal("Unknown", ManageViewModel.KindOf(null));
        Assert.Equal("On service start: Yes · triggers: none · free RAM 4096 MiB · cooldown 30 min", ManageViewModel.DescribePrewarm(PrewarmSettings.Default));
        L.Current = L.Ko;
        Assert.Equal("알 수 없음", ManageViewModel.KindOf(null));
        Assert.Equal("서비스 시작 시 예 · 트리거 없음 · 여유 RAM 4096 MiB · 쿨다운 30분", ManageViewModel.DescribePrewarm(PrewarmSettings.Default));
    }
}
