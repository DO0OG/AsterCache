using System.IO.Compression;
using AsterCache.Core;
using AsterCache.Core.I18n;
using AsterCache.Core.Json;
using AsterCache.Core.Profiles;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Windows;

namespace AsterCache.Cli.Commands;

/// aster profile export / import: 게임 하나의 trace 를 경로 없는 파일 (.json 또는 json 하나를 담은 .zip) 로 내보내고, 다른 PC 에서 파일마다 크기·표본 SHA 가 맞을 때만 받아들입니다.
/// 계획은 주고받지 않습니다 (그 PC 의 볼륨에서 다시 만듦): 가져온 trace 는 boot 이름으로 써서 서비스가 바로 계획·예열하고, 첫 실행은 그대로 기록합니다.
internal static class ProfileCommand
{
    public const string ZipEntry = "profile.json";
    private const int ShownMismatches = 200;

    public static readonly ArgOption[] ExportOptions = [new("game", Required: true), new("out", Required: true), new("data-dir")];

    public static readonly ArgOption[] ImportOptions = [new("file", Required: true), new("game", Required: true), new("data-dir"), new("json", ArgKind.Flag)];

    /// 시험이 바꿔 끼우는 관리자 판정 (가져오기는 관리자 전용 plans 폴더에 씀).
    internal static Func<bool> IsAdmin { get; set; } = Elevation.IsAdmin;

    public static int Export(ParsedArgs a, TextWriter o)
    {
        string outPath = PathText.Str(a.Text("out")!);
        bool zip = outPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        if (!zip && !outPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgException(L.Get(a.Lang, "Cli_Profile_BadOut"));
        }

        (GameSettings g, string plans) = LoadGame(a);
        string rawPath = Path.Combine(plans, g.Name + ".trace.json");
        if (!File.Exists(rawPath))
        {
            rawPath = Path.Combine(plans, g.Name + GameProfile.BootTraceSuffix); // 가져온 프로필만 있는 게임도 다시 내보낼 수 있게
        }

        List<TraceEvent> raw = File.Exists(rawPath) ? TraceLoader.LoadRanges(rawPath, g.OriginRoot) : [];
        List<TraceEvent> prefetch = File.Exists(g.Trace) ? TraceLoader.LoadRanges(g.Trace, g.OriginRoot) : [];
        GameProfile profile = GameProfile.Build(g.Name, g.ExeNames, g.OriginRoot, raw, prefetch, DateTimeOffset.UtcNow);
        if (profile.RawTrace.Count == 0 && profile.PrefetchTrace.Count == 0)
        {
            throw new PyValueException($"no trace to export for game {g.Name}");
        }

        byte[] bytes = profile.ToBytes();
        if (bytes.Length > GameProfile.MaxBytes)
        {
            throw new InvalidOperationException($"profile too large: {bytes.Length} bytes (limit {GameProfile.MaxBytes})");
        }

        if (zip)
        {
            using var archive = new ZipArchive(new FileStream(outPath, FileMode.Create, FileAccess.Write), ZipArchiveMode.Create);
            using Stream entry = archive.CreateEntry(ZipEntry, CompressionLevel.Optimal).Open();
            entry.Write(bytes);
        }
        else
        {
            File.WriteAllBytes(outPath, bytes);
        }

        return AsterCli.Emit(o, new JObject
        {
            { "game", g.Name },
            { "out", outPath },
            { "files", (long)profile.Files.Count },
            { "raw_events", (long)profile.RawTrace.Count },
            { "prefetch_events", (long)profile.PrefetchTrace.Count },
            { "bytes", (long)bytes.Length },
        });
    }

    public static int Import(ParsedArgs a, TextWriter o)
    {
        GameProfile profile = GameProfile.ParseBytes(ReadProfile(PathText.Str(a.Text("file")!)));
        (GameSettings g, string plans) = LoadGame(a);
        List<ProfileMismatch> bad = profile.Verify(g.OriginRoot);
        bool json = a.Flag("json");
        if (bad.Count > 0)
        {
            if (json)
            {
                return AsterCli.Emit(o, new JObject
                {
                    { "imported", false },
                    { "error", "profile_mismatch" },
                    { "mismatch_count", (long)bad.Count },
                    { "mismatches", bad.Take(ShownMismatches).Select(m => (object?)new JObject { { "path", m.Path }, { "reason", m.Reason }, { "expected_size", m.ExpectedSize }, { "actual_size", m.ActualSize } }).ToList() },
                }, 1);
            }

            o.WriteLine(L.Get(a.Lang, "Cli_Profile_Mismatch", bad.Count));
            foreach (ProfileMismatch m in bad.Take(ShownMismatches))
            {
                o.WriteLine($"{m.Path}\t{m.Reason}\t{m.ExpectedSize}\t{m.ActualSize?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}");
            }

            return 1;
        }

        if (!IsAdmin())
        {
            throw new NotAdminException();
        }

        string rawPath = Path.Combine(plans, g.Name + GameProfile.BootTraceSuffix); // 원시 트레이스 (.trace.json) 가 아님: 있으면 첫 실행 기록이 건너뛰어짐
        var writes = new List<(string Path, byte[] Bytes)>();
        if (profile.RawTrace.Count > 0)
        {
            writes.Add((rawPath, GameProfile.TraceBytes(profile.RawTrace, g.OriginRoot, GameProfile.BootSourceImport)));
        }

        if (profile.PrefetchTrace.Count > 0)
        {
            writes.Add((g.Trace, GameProfile.TraceBytes(profile.PrefetchTrace, g.OriginRoot)));
        }

        WriteAll(writes);
        if (json)
        {
            return AsterCli.Emit(o, new JObject
            {
                { "imported", true },
                { "game", g.Name },
                { "files", (long)profile.Files.Count },
                { "raw_events", (long)profile.RawTrace.Count },
                { "prefetch_events", (long)profile.PrefetchTrace.Count },
            });
        }

        o.WriteLine(L.Get(a.Lang, "Cli_Profile_Imported", g.Name, profile.Files.Count, profile.RawTrace.Count, profile.PrefetchTrace.Count));
        return 0;
    }

    private static (GameSettings Game, string PlansDir) LoadGame(ParsedArgs a)
    {
        string dataDir = Path.GetFullPath(a.Text("data-dir") is { } d ? PathText.Str(d) : SettingsStore.DefaultDataDir);
        string plans = Path.Combine(dataDir, "plans");
        string name = a.Text("game")!;
        GameSettings g = SettingsStore.Load(Path.Combine(dataDir, "config.json"), plans).Games.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new PyValueException($"game not registered: {name}");
        return (g, plans);
    }

    /// .zip 이면 profile.json 항목 하나를 풀어 읽습니다. 풀린 크기도 상한을 넘으면 거부합니다 (압축 폭탄).
    private static byte[] ReadProfile(string path)
    {
        if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return new FileInfo(path).Length > GameProfile.MaxBytes ? throw new PyValueException("profile too large") : File.ReadAllBytes(path);
        }

        using var archive = new ZipArchive(File.OpenRead(path), ZipArchiveMode.Read);
        ZipArchiveEntry entry = archive.Entries.Count == 1 && archive.Entries[0].FullName == ZipEntry ? archive.Entries[0] : throw new PyValueException($"zip: only {ZipEntry} expected");
        using Stream s = entry.Open();
        using var ms = new MemoryStream();
        byte[] buf = new byte[81920];
        int n;
        while ((n = s.Read(buf)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > GameProfile.MaxBytes)
            {
                throw new PyValueException("profile too large");
            }
        }

        return ms.ToArray();
    }

    // 임시 파일에 먼저 다 쓴 뒤 옮깁니다: 중간에 실패해도 반쪽 trace 가 남지 않음.
    private static void WriteAll(List<(string Path, byte[] Bytes)> writes)
    {
        foreach ((string path, byte[] bytes) in writes)
        {
            File.WriteAllBytes(path + ".tmp", bytes);
        }

        foreach ((string path, _) in writes)
        {
            File.Move(path + ".tmp", path, overwrite: true);
        }
    }
}
