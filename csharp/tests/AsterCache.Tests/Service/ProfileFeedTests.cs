using System.IO.Compression;
using System.Text;
using AsterCache.Core.Apply;
using AsterCache.Core.Discovery;
using AsterCache.Core.Profiles;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Service;
using AsterCache.Tests.Windows;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 공유 프로필 묶음: 묶음 하나만 받아 캐시하고 (ETag), 설치 파일이 같다고 확인된 게임에만 부트 트레이스로 저장합니다. HTTP 는 파일 기반 가짜입니다.
public sealed class ProfileFeedTests : IDisposable
{
    private const string Url = SharedProfilesSettings.DefaultUrl;
    private const string Tag = "\"v1\"";
    private static readonly DateTimeOffset Start = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDir _dir = new();
    private readonly ListLog _log = new();
    private readonly FakeHttp _http = new();
    private readonly string _origin;
    private readonly GameSettings _game;
    private readonly AsterSettings _settings;
    private DateTimeOffset _now = Start;
    private int _zips;

    public ProfileFeedTests()
    {
        _origin = _dir.Combine("Games\\Foo");
        Directory.CreateDirectory(Path.Combine(_origin, "data"));
        Directory.CreateDirectory(Path.Combine(_dir.Path, "plans"));
        _dir.WriteRandom("Games\\Foo\\data\\a.bin", 5000);
        _dir.WriteRandom("Games\\Foo\\data\\b.bin", 70000);
        _dir.WriteRandom("Games\\Foo\\foo.exe", 9000);
        string plan = Path.Combine(_dir.Path, "plans", "Foo.plan.json");
        _game = new GameSettings("Foo", ["foo.exe"], plan + ".prefetch.json", plan, _origin, [_origin], "GAMES", 1440) { Source = "auto" };
        _settings = new AsterSettings(50, [_game]);
    }

    public void Dispose() => _dir.Dispose();

    // ---- 도우미 ----

    private sealed class FakeHttp : IFeedHttp
    {
        public List<(string Url, string? Etag)> Calls { get; } = [];

        public Func<string?, FeedReply> Reply { get; set; } = _ => new FeedReply(404);

        public List<CancellationToken> Tokens { get; } = [];

        public FeedReply Get(string url, string? etag, CancellationToken stop = default)
        {
            Calls.Add((url, etag));
            Tokens.Add(stop);
            return Reply(etag);
        }
    }

    private sealed class ZeroStream(long length) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = (int)Math.Min(count, length - BytesRead);
            Array.Clear(buffer, offset, n);
            BytesRead += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private ProfileFeed Feed() => new(_dir.Path, _http, _log, () => _now, d => d == _origin ? [(Path.Combine(_origin, "foo.exe"), new FileInfo(Path.Combine(_origin, "foo.exe")).Length)] : []);

    private List<TraceEvent> Events() => [new(Path.Combine(_origin, "data", "a.bin"), 0, 4096), new(Path.Combine(_origin, "data", "b.bin"), 4096, 65536)];

    private GameProfile Profile() => GameProfile.Build("Foo", ["foo.exe"], _origin, Events(), [], Start);

    private static string Entry(string key, string body, string profile) => $"{{\"{key}\":{body},\"profile\":\"{profile}\"}}";

    private static string IndexJson(params string[] entries) => "{\"format\":\"astercache-profile-index\",\"version\":1,\"entries\":[" + string.Join(",", entries) + "]}";

    private static string SteamEntry(string profile, string appId = "730", string depots = "{\"731\":\"111\"}") => Entry("steam", $"{{\"appid\":\"{appId}\",\"depots\":{depots}}}", profile);

    private string Zip(string index, params (string Name, byte[] Bytes)[] files)
    {
        string path = _dir.Combine($"feed{++_zips}.zip");
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        Put(zip, "index.json", Encoding.UTF8.GetBytes(index));
        foreach ((string name, byte[] bytes) in files)
        {
            Put(zip, name, bytes);
        }

        return path;
    }

    private static void Put(ZipArchive zip, string name, byte[] bytes)
    {
        using Stream s = zip.CreateEntry(name).Open();
        s.Write(bytes);
    }

    private void Serve(string zipPath, string tag = Tag) =>
        _http.Reply = etag => etag == tag ? new FeedReply(304) : new FeedReply(200, tag, File.OpenRead(zipPath), new FileInfo(zipPath).Length);

    private static DiscoveredGame SteamGame(string dir, string appId = "730", string gid = "111") => new("Foo", "steam", dir, null, true, "steam.exe")
    {
        Steam = new SteamApp(appId, "Foo", "Foo") { InstalledDepots = new Dictionary<string, string> { ["731"] = gid, ["732"] = "222" } },
    };

    private string BootPath => Path.Combine(_dir.Path, "plans", "Foo.boot.trace.json");

    private byte[] ExpectedBoot() => GameProfile.TraceBytes(Profile().RawTrace, _origin, GameProfile.BootSourceShared);

    private (string Zip, byte[] Bytes) GoodZip(string entry) => (Zip(IndexJson(entry), ("p/foo.json", Profile().ToBytes())), Profile().ToBytes());

    // ---- 매칭과 저장 ----

    [Fact]
    public void SteamMatchSavesTheBootTraceAndLogsOneLineAndCachesTheBundleWithItsEtag()
    {
        Serve(GoodZip(SteamEntry("p/foo.json")).Zip);

        int saved = Feed().Run(_settings, [SteamGame(_origin)]);

        Assert.Equal(1, saved);
        Assert.Equal(ExpectedBoot(), File.ReadAllBytes(BootPath));
        Assert.Equal([(Url, (string?)null)], _http.Calls);
        Assert.Equal(Tag, File.ReadAllText(Path.Combine(_dir.Path, "profiles", "feed.etag")));
        Assert.True(File.Exists(Path.Combine(_dir.Path, "profiles", "feed.zip")));
        Assert.Single(_log.Lines);
        Assert.Contains("shared profiles: download ok", _log.Lines.Single(), StringComparison.Ordinal);
        Assert.Contains("saved boot trace for Foo", _log.Lines.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void WithinAWeekThereIsNoSecondDownloadAndNothingIsRewritten()
    {
        Serve(GoodZip(SteamEntry("p/foo.json")).Zip);
        ProfileFeed feed = Feed();
        feed.Run(_settings, [SteamGame(_origin)]);
        DateTime written = File.GetLastWriteTimeUtc(BootPath);
        File.SetLastWriteTimeUtc(BootPath, written.AddHours(-5));
        DateTime old = File.GetLastWriteTimeUtc(BootPath);

        _now += TimeSpan.FromDays(6);
        Assert.Equal(0, feed.Run(_settings, [SteamGame(_origin)]));

        Assert.Single(_http.Calls);
        Assert.Equal(old, File.GetLastWriteTimeUtc(BootPath));
    }

    [Fact]
    public void AfterAWeekTheEtagIsSentAnd304UsesTheCache()
    {
        Serve(GoodZip(SteamEntry("p/foo.json")).Zip);
        ProfileFeed feed = Feed();
        feed.Run(_settings, [SteamGame(_origin)]);
        File.Delete(BootPath); // 캐시만으로 다시 맞춰지는지 보려고 저장물을 지움

        _now += ProfileFeed.Every + TimeSpan.FromMinutes(1);
        Assert.Equal(0, feed.Run(_settings, [SteamGame(_origin)])); // 같은 묶음은 이미 맞춰 봄: 다시 하지 않음
        Assert.Equal([(Url, (string?)null), (Url, Tag)], _http.Calls);
        Assert.Contains(_log.Lines, l => l.Contains("download not modified (cache used)", StringComparison.Ordinal));

        Assert.Equal(1, Feed().Run(_settings, [SteamGame(_origin)])); // 서비스 재시작: 같은 데이터 폴더의 캐시와 etag 로 304 → 캐시에서 저장
        Assert.Equal(Tag, _http.Calls[^1].Etag);
        Assert.Equal(ExpectedBoot(), File.ReadAllBytes(BootPath));
    }

    [Fact]
    public void ForgedProfileWithAWrongSampleHashIsRejected()
    {
        Serve(GoodZip(SteamEntry("p/foo.json")).Zip);
        File.WriteAllBytes(Path.Combine(_origin, "data", "a.bin"), new byte[5000]); // 프로필을 만든 뒤 파일 내용이 달라짐 (같은 크기)

        int saved = Feed().Run(_settings, [SteamGame(_origin)]);

        Assert.Equal(0, saved);
        Assert.False(File.Exists(BootPath));
    }

    [Fact]
    public void ProfileWithAMissingOrResizedFileIsRejected()
    {
        Serve(GoodZip(SteamEntry("p/foo.json")).Zip);
        File.AppendAllText(Path.Combine(_origin, "data", "b.bin"), "x");

        Assert.Equal(0, Feed().Run(_settings, [SteamGame(_origin)]));
        Assert.False(File.Exists(BootPath));
    }

    [Fact]
    public void DepotMismatchAppIdMismatchAndEpicVersionMismatchDoNotMatch()
    {
        Serve(Zip(IndexJson(SteamEntry("p/foo.json"), Entry("epic", "{\"app_name\":\"abc\",\"version\":\"2\"}", "p/foo.json")), ("p/foo.json", Profile().ToBytes())));
        ProfileFeed feed = Feed();

        Assert.Equal(0, feed.Run(_settings, [SteamGame(_origin, gid: "999")])); // depot gid 가 다름
        _now += TimeSpan.FromDays(2);
        Assert.Equal(0, feed.Run(_settings, [SteamGame(_origin, appId: "731")]));
        _now += TimeSpan.FromDays(2);
        var epic = new DiscoveredGame("Foo", "epic", _origin, null, true, null) { Epic = new EpicApp("abc", "Foo", _origin, string.Empty) { AppVersionString = "1" } };
        Assert.Equal(0, feed.Run(_settings, [epic]));

        Assert.False(File.Exists(BootPath));
    }

    [Fact]
    public void EpicMatchesByAppNameAndVersion()
    {
        Serve(Zip(IndexJson(Entry("epic", "{\"app_name\":\"abc\",\"version\":\"2\"}", "p/foo.json")), ("p/foo.json", Profile().ToBytes())));
        var epic = new DiscoveredGame("Foo", "epic", _origin, null, true, null) { Epic = new EpicApp("abc", "Foo", _origin, string.Empty) { AppVersionString = "2" } };

        Assert.Equal(1, Feed().Run(_settings, [epic]));
        Assert.Equal(ExpectedBoot(), File.ReadAllBytes(BootPath));
    }

    [Fact]
    public void ExeFingerprintMatchesWhenThereIsNoLauncherInfo()
    {
        string exe = Path.Combine(_origin, "foo.exe");
        long size = new FileInfo(exe).Length;
        string sha = SampleHasher.Compute(exe, size, null, null);
        Serve(Zip(IndexJson(Entry("exe", $"{{\"size\":{size},\"sample_sha256\":\"{sha}\"}}", "p/foo.json")), ("p/foo.json", Profile().ToBytes())));

        Assert.Equal(1, Feed().Run(_settings, []));
        Assert.Equal(ExpectedBoot(), File.ReadAllBytes(BootPath));
    }

    [Fact]
    public void ExeFingerprintWithAnotherShaOrSizeDoesNotMatch()
    {
        string exe = Path.Combine(_origin, "foo.exe");
        long size = new FileInfo(exe).Length;
        Serve(Zip(IndexJson(
            Entry("exe", $"{{\"size\":{size},\"sample_sha256\":\"{new string('a', 64)}\"}}", "p/foo.json"),
            Entry("exe", $"{{\"size\":{size + 1},\"sample_sha256\":\"{SampleHasher.Compute(exe, size, null, null)}\"}}", "p/foo.json")), ("p/foo.json", Profile().ToBytes())));

        Assert.Equal(0, Feed().Run(_settings, []));
    }

    [Fact]
    public void SteamIsTriedBeforeEpicBeforeTheExeFingerprint()
    {
        // 같은 게임에 맞는 세 항목이 서로 다른 프로필을 가리킴: steam 의 것이 저장됨 (이벤트 수로 구분)
        byte[] steamProfile = Profile().ToBytes();
        GameProfile other = GameProfile.Build("Foo", ["foo.exe"], _origin, Events().Take(1).ToList(), [], Start);
        string exe = Path.Combine(_origin, "foo.exe");
        string sha = SampleHasher.Compute(exe, new FileInfo(exe).Length, null, null);
        Serve(Zip(
            IndexJson(
                Entry("exe", $"{{\"size\":{new FileInfo(exe).Length},\"sample_sha256\":\"{sha}\"}}", "p/exe.json"),
                Entry("epic", "{\"app_name\":\"abc\",\"version\":\"2\"}", "p/epic.json"),
                SteamEntry("p/steam.json")),
            ("p/steam.json", steamProfile), ("p/epic.json", other.ToBytes()), ("p/exe.json", other.ToBytes())));
        DiscoveredGame both = SteamGame(_origin) with { Epic = new EpicApp("abc", "Foo", _origin, string.Empty) { AppVersionString = "2" } };

        Feed().Run(_settings, [both]);

        Assert.Equal(ExpectedBoot(), File.ReadAllBytes(BootPath));
    }

    [Fact]
    public void AFailingSteamMatchFallsThroughToTheNextKind()
    {
        GameProfile forged = GameProfile.Build("Foo", ["foo.exe"], _origin, Events(), [], Start);
        byte[] forgedBytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(forged.ToBytes()).Replace(forged.Files[0].SampleSha256, new string('0', 64), StringComparison.Ordinal));
        string exe = Path.Combine(_origin, "foo.exe");
        string sha = SampleHasher.Compute(exe, new FileInfo(exe).Length, null, null);
        Serve(Zip(IndexJson(SteamEntry("p/forged.json"), Entry("exe", $"{{\"size\":{new FileInfo(exe).Length},\"sample_sha256\":\"{sha}\"}}", "p/ok.json")),
            ("p/forged.json", forgedBytes), ("p/ok.json", Profile().ToBytes())));

        Assert.Equal(1, Feed().Run(_settings, [SteamGame(_origin)]));
        Assert.Equal(ExpectedBoot(), File.ReadAllBytes(BootPath));
    }

    [Fact]
    public void ARawTraceBlocksTheProfileAndAnIdenticalBootTraceIsNotRewritten()
    {
        Serve(GoodZip(SteamEntry("p/foo.json")).Zip);
        File.WriteAllText(Path.Combine(_dir.Path, "plans", "Foo.trace.json"), "[]");

        Assert.Equal(0, Feed().Run(_settings, [SteamGame(_origin)]));
        Assert.False(File.Exists(BootPath));

        File.Delete(Path.Combine(_dir.Path, "plans", "Foo.trace.json"));
        File.WriteAllBytes(BootPath, ExpectedBoot());
        File.SetLastWriteTimeUtc(BootPath, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(0, Feed().Run(_settings, [SteamGame(_origin)])); // 같은 내용: 쓰지 않음
        Assert.Equal(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(BootPath));
    }

    [Theory]
    [InlineData("{\"source\": \"shared\", \"ranges\": []}", true)]
    [InlineData("{\"source\": \"prefetch\", \"ranges\": []}", true)]
    [InlineData("{\"source\": \"import\", \"ranges\": []}", false)]
    [InlineData("[]", false)] // 옛 목록 형식 (beta.4 가져오기일 수 있음): 보호
    [InlineData("{\"ranges\": []}", false)]
    public void OnlySharedAndPrefetchBootTracesAreReplacedByTheFeed(string existing, bool replaced)
    {
        Serve(GoodZip(SteamEntry("p/foo.json")).Zip);
        File.WriteAllText(BootPath, existing);

        Assert.Equal(replaced ? 1 : 0, Feed().Run(_settings, [SteamGame(_origin)]));
        if (replaced)
        {
            Assert.Equal(ExpectedBoot(), File.ReadAllBytes(BootPath));
        }
        else
        {
            Assert.Equal(existing, File.ReadAllText(BootPath));
            Assert.Contains("keeps its imported boot trace", _log.Lines.Single(l => !l.Contains("download", StringComparison.Ordinal) || l.Contains("keeps", StringComparison.Ordinal)), StringComparison.Ordinal);
        }
    }

    // ---- 받기 실패 ----

    [Fact]
    public void NotFoundIsSilentRetriesOnlyAfterAWeekAndSavesNothing()
    {
        ProfileFeed feed = Feed();

        Assert.Equal(0, feed.Run(_settings, [SteamGame(_origin)]));
        Assert.Contains(_log.Lines, l => l.Contains("unavailable (HTTP 404)", StringComparison.Ordinal));
        _now += TimeSpan.FromDays(3);
        feed.Run(_settings, [SteamGame(_origin)]);

        Assert.Single(_http.Calls);
        Assert.False(Directory.Exists(Path.Combine(_dir.Path, "profiles")) && File.Exists(Path.Combine(_dir.Path, "profiles", "feed.zip")));
    }

    [Fact]
    public void OfflineIsSilentAndRetriedAfterAnHour()
    {
        _http.Reply = _ => throw new HttpRequestException("offline");
        ProfileFeed feed = Feed();

        Assert.Equal(0, feed.Run(_settings, [SteamGame(_origin)]));
        Assert.Contains(_log.Lines, l => l.Contains("failed (HttpRequestException)", StringComparison.Ordinal));
        _now += TimeSpan.FromMinutes(30);
        feed.Run(_settings, []);
        Assert.Single(_http.Calls);
        _now += TimeSpan.FromMinutes(40);
        feed.Run(_settings, []);

        Assert.Equal(2, _http.Calls.Count);
    }

    [Fact]
    public void AnOversizedDownloadIsCutOffWhileReadingAndLeavesNoCache()
    {
        var body = new ZeroStream(ProfileFeed.MaxDownloadBytes * 2);
        _http.Reply = _ => new FeedReply(200, Tag, body, null); // 헤더가 길이를 말하지 않음

        Assert.Equal(0, Feed().Run(_settings, [SteamGame(_origin)]));

        Assert.True(body.BytesRead <= ProfileFeed.MaxDownloadBytes + 81920, $"read {body.BytesRead}");
        Assert.Contains(_log.Lines, l => l.Contains("download too large", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(_dir.Path, "profiles", "feed.zip")));
        OnlyTheStateFileRemains(); // ZIP·ETag·임시 파일은 남지 않음
    }

    [Fact]
    public void AHeaderLengthOverTheCapStopsBeforeReadingAnything()
    {
        var body = new ZeroStream(10);
        _http.Reply = _ => new FeedReply(200, Tag, body, ProfileFeed.MaxDownloadBytes + 1);

        Feed().Run(_settings, []);

        Assert.Equal(0, body.BytesRead);
    }

    private sealed class HangStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void CancellingWhileReadingTheBodyStopsTheDownloadAndLeavesNoCache()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));
        _http.Reply = _ => new FeedReply(200, Tag, new HangStream(), null, null, cts.Token);

        Assert.Equal(0, Feed().Run(_settings, [SteamGame(_origin)]));

        Assert.Contains(_log.Lines, l => l.Contains("failed", StringComparison.Ordinal));
        OnlyTheStateFileRemains();
        Assert.Contains("\"failures\": 1", File.ReadAllText(StatePath), StringComparison.Ordinal); // 종료 신호 없이 시간 초과로 끊김: 일시 오류로 셈
    }

    [Fact]
    public void SeveralGamesMatchingOneBundleChargeTheUnpackBudgetOnce()
    {
        // 55 MiB 짜리 프로필 하나를 5 게임이 읽으면 게임별 계산으로는 256 MiB 를 넘음
        byte[] padded = [.. Profile().ToBytes(), .. Enumerable.Repeat((byte)' ', 55 << 20)];
        Serve(Zip(IndexJson(SteamEntry("p/foo.json")), ("p/foo.json", padded)));
        var games = new List<GameSettings> { _game };
        var found = new List<DiscoveredGame> { SteamGame(_origin) };
        for (int i = 2; i <= 5; i++)
        {
            string root = _dir.Combine($"Games\\Foo{i}");
            Directory.CreateDirectory(Path.Combine(root, "data"));
            File.Copy(Path.Combine(_origin, "data", "a.bin"), Path.Combine(root, "data", "a.bin"));
            File.Copy(Path.Combine(_origin, "data", "b.bin"), Path.Combine(root, "data", "b.bin"));
            string plan = Path.Combine(_dir.Path, "plans", $"Foo{i}.plan.json");
            games.Add(new GameSettings($"Foo{i}", ["foo.exe"], plan + ".prefetch.json", plan, root, [root], "GAMES", 1440) { Source = "auto" });
            found.Add(SteamGame(root));
        }

        int saved = Feed().Run(new AsterSettings(50, games), found);

        Assert.Equal(5, saved);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("cache unusable", StringComparison.Ordinal));
    }

    [Fact]
    public void AFailedBootWriteLeavesNoTempFile()
    {
        Serve(GoodZip(SteamEntry("p/foo.json")).Zip);
        Directory.CreateDirectory(BootPath); // 이동이 실패하도록 같은 이름의 폴더

        Assert.Equal(0, Feed().Run(_settings, [SteamGame(_origin)]));

        Assert.Empty(Directory.GetFiles(Path.Combine(_dir.Path, "plans"), "*.tmp"));
    }

    [Fact]
    public void NonHttpsUrlIsNeverRequested()
    {
        AsterSettings http = _settings with { Auto = AutoSettings.Default with { SharedProfiles = new SharedProfilesSettings(true, "http://example.test/p.zip") } };

        Feed().Run(http, []);

        Assert.Empty(_http.Calls);
    }

    [Fact]
    public void DisabledSettingsDoNothing()
    {
        AsterSettings off = _settings with { Auto = AutoSettings.Default with { SharedProfiles = new SharedProfilesSettings(false, Url) } };
        AsterSettings autoOff = _settings with { Auto = AutoSettings.Default with { Enabled = false } };

        Assert.Equal(0, Feed().Run(off, []) + Feed().Run(autoOff, []));
        Assert.Empty(_http.Calls);
        Assert.Empty(_log.Lines);
    }

    // ---- 오류 분류·백오프·영속 상태 ----

    private string StatePath => Path.Combine(_dir.Path, "profiles", "feed.state.json");

    private void OnlyTheStateFileRemains() =>
        Assert.Equal(["feed.state.json"], Directory.GetFiles(Path.Combine(_dir.Path, "profiles")).Select(f => Path.GetFileName(f)).ToArray());

    private void WriteState(string error, string nextFetch, string extra = "")
    {
        Directory.CreateDirectory(Path.Combine(_dir.Path, "profiles"));
        File.WriteAllText(StatePath, $"{{\"format\":\"astercache-profile-feed-state\",\"version\":1,\"failures\":3,\"error\":\"{error}\",\"next_fetch\":\"{nextFetch}\"{extra}}}");
    }

    private const string Far = "2099-01-01T00:00:00.0000000+00:00";

    private List<ZeroStream> ServeCorrupt(string etag)
    {
        var streams = new List<ZeroStream>();
        _http.Reply = _ =>
        {
            var body = new ZeroStream(9); // zip 이 아닌 내용: 손상
            streams.Add(body);
            return new FeedReply(200, etag, body, 9);
        };
        return streams;
    }

    [Fact]
    public void TransientFailuresBackOffByDoublingUpToTheCap()
    {
        _http.Reply = _ => throw new HttpRequestException("offline");
        ProfileFeed feed = Feed();
        feed.Run(_settings, []);

        int[] hours = [1, 2, 4, 8, 16, 24, 24]; // 시작 1 시간, 2 배, 상한 24 시간
        for (int k = 0; k < hours.Length; k++)
        {
            _now += TimeSpan.FromHours(hours[k]) - TimeSpan.FromMinutes(1);
            feed.Run(_settings, []);
            Assert.Equal(k + 1, _http.Calls.Count); // 아직 이름
            _now += TimeSpan.FromMinutes(1);
            feed.Run(_settings, []);
            Assert.Equal(k + 2, _http.Calls.Count);
        }

        Assert.Contains("\"error\": \"transient\"", File.ReadAllText(StatePath), StringComparison.Ordinal);
    }

    [Fact]
    public void BackoffAndFailureCountSurviveARestartAndSuccessResetsTheCount()
    {
        _http.Reply = _ => throw new HttpRequestException("offline");
        Feed().Run(_settings, []);
        Assert.True(File.Exists(StatePath));

        _now += TimeSpan.FromMinutes(30);
        Feed().Run(_settings, []); // 재시작해도 1 시간 안에는 다시 요청하지 않음
        Assert.Single(_http.Calls);

        _now += TimeSpan.FromMinutes(31);
        ProfileFeed second = Feed();
        second.Run(_settings, []);
        Assert.Equal(2, _http.Calls.Count); // 두 번째 실패: 이제 2 시간

        _now += TimeSpan.FromMinutes(90);
        Feed().Run(_settings, []); // 실패 횟수가 복원되어 2 시간째
        Assert.Equal(2, _http.Calls.Count);

        Serve(GoodZip(SteamEntry("p/foo.json")).Zip);
        _now += TimeSpan.FromMinutes(31);
        Assert.Equal(1, Feed().Run(_settings, [SteamGame(_origin)]));
        Assert.Equal(3, _http.Calls.Count);
        Assert.Contains("\"failures\": 0", File.ReadAllText(StatePath), StringComparison.Ordinal); // 성공: 횟수는 0, 다음 정기 요청 시각은 남음
    }

    [Fact]
    public void ACorruptBundleIsNotAskedForDuringExactlyOneWeekThenOnceMoreAndWaitsAnotherWeek()
    {
        List<ZeroStream> streams = ServeCorrupt("\"bad\"");
        Feed().Run(_settings, []);
        Assert.Equal(9, streams[0].BytesRead);
        string state = File.ReadAllText(StatePath);
        Assert.Contains("\"error\": \"corrupt\"", state, StringComparison.Ordinal);
        Assert.Contains("\"next_fetch\": \"" + (Start + ProfileFeed.CorruptRetryCap).ToString("O") + "\"", state, StringComparison.Ordinal); // 정확히 168 시간 뒤

        _now = Start + ProfileFeed.CorruptRetryCap - TimeSpan.FromTicks(1);
        Feed().Run(_settings, []); // 재시작해도 그 사이에는 표지 확인용 요청도 없음
        Assert.Single(_http.Calls);

        _now = Start + ProfileFeed.CorruptRetryCap;
        Feed().Run(_settings, []);
        Assert.Equal(2, _http.Calls.Count);
        Assert.Equal(9, streams[1].BytesRead); // 일주일이 지나면 한 번 더 받아 봄

        _now += ProfileFeed.CorruptRetryCap - TimeSpan.FromTicks(1);
        Feed().Run(_settings, []);
        Assert.Equal(2, _http.Calls.Count); // 또 손상: 다시 일주일
        _now += TimeSpan.FromTicks(1);
        Feed().Run(_settings, []);
        Assert.Equal(3, _http.Calls.Count);
    }

    [Fact]
    public void ACorruptBundleWithAChangedMarkerIsPickedUpByTheWeeklyRequest()
    {
        List<ZeroStream> streams = ServeCorrupt("\"bad\"");
        ProfileFeed feed = Feed();
        feed.Run(_settings, []);
        List<ZeroStream> changed = ServeCorrupt("\"bad2\"");
        _now += TimeSpan.FromHours(2);
        feed.Run(_settings, []);
        Assert.Single(_http.Calls); // 일주일 안에는 표지가 바뀌었어도 묻지 않음

        _now = Start + ProfileFeed.CorruptRetryCap;
        feed.Run(_settings, []);

        Assert.Equal(2, _http.Calls.Count);
        Assert.Equal(9, streams[0].BytesRead);
        Assert.Equal(9, changed[0].BytesRead);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("same bundle", StringComparison.Ordinal));
    }

    [Fact]
    public void ARequestBeforeTheCorruptScheduleWithTheSameMarkerReadsNoBodyAndKeepsTheSchedule()
    {
        WriteState("corrupt", Start.ToString("O"), ",\"bad_marker\":\"\\\"bad\\\"\",\"bad_since\":\"" + (Start - TimeSpan.FromDays(1)).ToString("O") + "\"");
        List<ZeroStream> streams = ServeCorrupt("\"bad\"");

        Feed().Run(_settings, []);

        Assert.Equal(0, streams[0].BytesRead);
        Assert.Contains(_log.Lines, l => l.Contains("same bundle as the rejected one", StringComparison.Ordinal));
        Assert.Contains("\"next_fetch\": \"" + (Start - TimeSpan.FromDays(1) + ProfileFeed.CorruptRetryCap).ToString("O") + "\"", File.ReadAllText(StatePath), StringComparison.Ordinal);
    }

    [Fact]
    public void AnHttpErrorAnswerIsRememberedAcrossARestartForTheUsualWeek()
    {
        Feed().Run(_settings, []); // 404
        _now += TimeSpan.FromDays(3);

        Feed().Run(_settings, []);
        Assert.Single(_http.Calls);

        _now += TimeSpan.FromDays(5);
        Feed().Run(_settings, []);
        Assert.Equal(2, _http.Calls.Count);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"format\":\"other\",\"version\":1,\"failures\":3,\"next_fetch\":\"2026-11-03T12:00:00.0000000+00:00\"}")]
    [InlineData("{\"format\":\"astercache-profile-feed-state\",\"version\":1,\"failures\":3,\"next_fetch\":\"garbage\"}")]
    public void ABrokenStateFileIsIgnoredAndTheFeedAsksAtOnce(string text)
    {
        Directory.CreateDirectory(Path.Combine(_dir.Path, "profiles"));
        File.WriteAllText(StatePath, text);
        _http.Reply = _ => throw new HttpRequestException("offline");

        Feed().Run(_settings, []);

        Assert.Single(_http.Calls);
    }

    [Fact]
    public void ANextFetchFarInTheFutureIsCappedByItsKindOfFailure()
    {
        WriteState("transient", Far);
        _http.Reply = _ => throw new HttpRequestException("offline");
        ProfileFeed feed = Feed();

        feed.Run(_settings, []);
        Assert.Empty(_http.Calls);

        _now += ProfileFeed.MaxBackoff + TimeSpan.FromMinutes(1); // 일시 오류는 24 시간
        feed.Run(_settings, []);
        Assert.Single(_http.Calls);
    }

    [Theory]
    [InlineData("corrupt", 7)]
    [InlineData("http", 7)]
    public void CorruptAndHttpNextFetchesAreCappedAtOneWeek(string error, int days)
    {
        WriteState(error, Far);
        ProfileFeed feed = Feed();

        _now += TimeSpan.FromDays(days) - TimeSpan.FromMinutes(1);
        feed.Run(_settings, []);
        Assert.Empty(_http.Calls);

        _now += TimeSpan.FromMinutes(2);
        feed.Run(_settings, []);
        Assert.Single(_http.Calls);
    }

    [Fact]
    public void ANoMatchRetryTimeFarInTheFutureIsCappedAtOneDay()
    {
        Serve(GoodZip(SteamEntry("p/foo.json")).Zip);
        Feed().Run(_settings, [SteamGame(_origin, gid: "999")]);
        File.WriteAllText(StatePath, System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(StatePath), "\"Foo\": \"[^\"]*\"", $"\"Foo\": \"{Far}\""));

        ProfileFeed feed = Feed(); // 읽을 때의 지금 기준으로 하루를 넘지 않게 줄임
        _now += ProfileFeed.RetryAfterNoMatch + TimeSpan.FromMinutes(1);

        Assert.Equal(1, feed.Run(_settings, [SteamGame(_origin)]));
    }

    [Fact]
    public void SuccessAndNotModifiedKeepTheNextRegularRequestAcrossRestarts()
    {
        Serve(GoodZip(SteamEntry("p/foo.json")).Zip);
        Feed().Run(_settings, [SteamGame(_origin)]);
        _now += TimeSpan.FromHours(1);
        Feed().Run(_settings, [SteamGame(_origin)]); // 재시작해도 일주일 안에는 받지 않음
        Assert.Single(_http.Calls);

        _now += ProfileFeed.Every;
        Feed().Run(_settings, [SteamGame(_origin)]); // 304
        Assert.Equal(2, _http.Calls.Count);
        _now += TimeSpan.FromHours(1);
        Feed().Run(_settings, [SteamGame(_origin)]);
        Assert.Equal(2, _http.Calls.Count);
    }

    [Fact]
    public async Task ConcurrentRunsSendOnlyOneRequest()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _http.Reply = _ =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(30)));
            return new FeedReply(404);
        };
        ProfileFeed feed = Feed();
        Task<int> first = Task.Run(() => feed.Run(_settings, []));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));

        Assert.Equal(0, feed.Run(_settings, [])); // 도는 중: 건너뜀
        release.Set();
        await first;

        Assert.Single(_http.Calls);
        OnlyTheStateFileRemains();
    }

    [Fact]
    public void TheBackoffIsSavedBeforeTheRequestSoAKilledProcessKeepsIt()
    {
        string? during = null;
        _http.Reply = _ =>
        {
            during = File.ReadAllText(StatePath);
            throw new InvalidOperationException("process killed"); // 잡히지 않는 예외: 결과를 기록하지 못한 채 끝나는 상황
        };

        Assert.Throws<InvalidOperationException>(() => Feed().Run(_settings, []));

        Assert.Contains("\"failures\": 1", during, StringComparison.Ordinal); // 요청 중 이미 가정한 백오프가 파일에 있음
        _now += TimeSpan.FromMinutes(30);
        Feed().Run(_settings, []);
        Assert.Single(_http.Calls);
        _http.Reply = _ => new FeedReply(404);
        _now += TimeSpan.FromMinutes(31);
        Feed().Run(_settings, []);
        Assert.Equal(2, _http.Calls.Count);
    }

    [Theory]
    [InlineData("9999-12-31T23:59:59.9999999+00:00")]
    [InlineData("2099-01-01T00:00:00.0000000+00:00")]
    [InlineData("2020-01-01T00:00:00.0000000+00:00")]
    public void ABadSinceOutsideTheValidRangeIsIgnoredWithoutThrowing(string badSince)
    {
        WriteState("corrupt", Start.ToString("O"), $",\"bad_marker\":\"\\\"bad\\\"\",\"bad_since\":\"{badSince}\"");
        List<ZeroStream> streams = ServeCorrupt("\"bad\"");

        Feed().Run(_settings, []);

        Assert.Equal(9, streams[0].BytesRead); // 믿을 수 없는 표지는 버리고 본문을 읽어 봄
    }

    [Theory]
    [InlineData(503)]
    [InlineData(429)]
    [InlineData(408)]
    public void AnyHttpAnswerThatIsNot200Or304WaitsTheUsualWeek(int status)
    {
        _http.Reply = _ => new FeedReply(status);
        ProfileFeed feed = Feed();
        feed.Run(_settings, []);

        _now += ProfileFeed.Every - TimeSpan.FromMinutes(1);
        feed.Run(_settings, []);
        Assert.Single(_http.Calls);
        _now += TimeSpan.FromMinutes(2);
        feed.Run(_settings, []);

        Assert.Equal(2, _http.Calls.Count);
        Assert.Contains("\"error\": \"http\"", File.ReadAllText(StatePath), StringComparison.Ordinal);
    }

    [Fact]
    public void ACancellationFromServiceShutdownKeepsTheStateAndTheRequestInterval()
    {
        _http.Reply = _ => throw new HttpRequestException("offline");
        ProfileFeed first = Feed();
        first.Run(_settings, []);
        _now += TimeSpan.FromMinutes(61);
        first.Run(_settings, []); // 두 번 실패
        _now += TimeSpan.FromMinutes(121);
        using var stop = new CancellationTokenSource();
        stop.CancelAfter(TimeSpan.FromMilliseconds(100));
        _http.Reply = _ => new FeedReply(200, Tag, new HangStream(), null); // 시간 초과 토큰은 없고 종료 신호만 끊음

        Assert.Equal(0, Feed().Run(_settings, [SteamGame(_origin)], stop.Token));

        Assert.Equal(stop.Token, _http.Tokens[^1]); // 종료 신호가 요청에 전달됨
        Assert.Contains(_log.Lines, l => l.Contains("cancelled", StringComparison.Ordinal));
        OnlyTheStateFileRemains(); // 상태 파일을 지우지 않음
        string state = File.ReadAllText(StatePath);
        Assert.Contains("\"failures\": 2", state, StringComparison.Ordinal); // 실패 횟수는 요청 전 값
        Assert.Contains("\"next_fetch\": \"" + (_now + ProfileFeed.RetryAfterFailure).ToString("O") + "\"", state, StringComparison.Ordinal);
        _http.Reply = _ => new FeedReply(404);
        _now += TimeSpan.FromMinutes(59);
        Feed().Run(_settings, []);
        Assert.Equal(3, _http.Calls.Count); // 재시작해도 한 시간 안에는 다시 요청하지 않음
        _now += TimeSpan.FromMinutes(2);
        Feed().Run(_settings, []);
        Assert.Equal(4, _http.Calls.Count);
    }

    [Fact]
    public void ShutdownDuringTheHeaderWaitIsAlsoNotAFailure()
    {
        using var stop = new CancellationTokenSource();
        _http.Reply = _ =>
        {
            stop.Cancel();
            throw new TaskCanceledException();
        };

        Feed().Run(_settings, [], stop.Token);

        Assert.Equal(stop.Token, _http.Tokens[0]);
        Assert.Contains(_log.Lines, l => l.Contains("cancelled", StringComparison.Ordinal));
        Assert.Contains("\"failures\": 0", File.ReadAllText(StatePath), StringComparison.Ordinal);
    }

    [Fact]
    public void LeftoverTempFilesFromAKilledFetchAreDeletedBeforeTheRequestAndOtherFilesStay()
    {
        string dir = Path.Combine(_dir.Path, "profiles");
        Directory.CreateDirectory(dir);
        string zipTmp = Path.Combine(dir, "feed.zip." + Guid.NewGuid().ToString("N") + ".tmp");
        string stateTmp = Path.Combine(dir, "feed.state.json." + Guid.NewGuid().ToString("N") + ".tmp");
        string other = Path.Combine(dir, "feed.zip.keep.tmp");
        File.WriteAllBytes(zipTmp, new byte[1024]);
        File.WriteAllBytes(stateTmp, new byte[1]);
        File.WriteAllBytes(other, new byte[1]);
        bool tempsWhenRequested = true;
        _http.Reply = _ =>
        {
            tempsWhenRequested = File.Exists(zipTmp) || File.Exists(stateTmp);
            return new FeedReply(404);
        };

        Feed().Run(_settings, []);

        Assert.Single(_http.Calls);
        Assert.False(tempsWhenRequested); // 요청 전에 이미 지워져 있어야 함
        Assert.False(File.Exists(zipTmp));
        Assert.False(File.Exists(stateTmp));
        Assert.True(File.Exists(other)); // 패턴에 맞지 않는 파일은 그대로
    }

    [Fact]
    public void AnIoErrorWhileShuttingDownIsNotCountedAsAFailure()
    {
        using var stop = new CancellationTokenSource();
        _http.Reply = _ =>
        {
            stop.Cancel();
            throw new IOException("connection closed");
        };

        Feed().Run(_settings, [], stop.Token);

        Assert.Contains(_log.Lines, l => l.Contains("cancelled", StringComparison.Ordinal));
        Assert.Contains("\"failures\": 0", File.ReadAllText(StatePath), StringComparison.Ordinal); // 종료 중 실패는 세지 않음
    }

    [Fact]
    public void WhenTheStateCannotBeSavedBeforeTheRequestNothingIsRequestedAndItIsTriedAgainNextRun()
    {
        Directory.CreateDirectory(StatePath); // 파일 자리에 폴더: 저장 실패
        ProfileFeed feed = Feed();

        Assert.Equal(0, feed.Run(_settings, []));

        Assert.Empty(_http.Calls);
        Assert.Contains(_log.Lines, l => l.Contains("state not saved", StringComparison.Ordinal));
        Directory.Delete(StatePath);
        feed.Run(_settings, []);
        Assert.Single(_http.Calls);
    }

    [Fact]
    public void ValuesCappedOnLoadAreWrittenBackToTheFile()
    {
        WriteState("transient", Far, ",\"next_try\":{\"Foo\":\"" + Far + "\"}");

        _ = Feed();

        string text = File.ReadAllText(StatePath);
        Assert.DoesNotContain("2099", text, StringComparison.Ordinal);
        Assert.Contains("\"next_fetch\": \"" + (Start + ProfileFeed.MaxBackoff).ToString("O") + "\"", text, StringComparison.Ordinal);
        Assert.Contains("\"Foo\": \"" + (Start + ProfileFeed.RetryAfterNoMatch).ToString("O") + "\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AClockThatGoesBackWhileRunningCapsTheWaitLikeALoadedState()
    {
        _http.Reply = _ => throw new HttpRequestException("offline");
        ProfileFeed feed = Feed();
        feed.Run(_settings, []); // 다음은 한 시간 뒤

        _now = Start - TimeSpan.FromDays(2);
        feed.Run(_settings, []);
        Assert.Single(_http.Calls);
        _now = Start - TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1); // 되감긴 시각 기준 최대 24 시간 뒤
        feed.Run(_settings, []);

        Assert.Equal(2, _http.Calls.Count);
    }

    [Fact]
    public void AFailedFetchKeepsUsingTheLastGoodBundle()
    {
        Serve(GoodZip(SteamEntry("p/foo.json")).Zip);
        Feed().Run(_settings, [SteamGame(_origin)]);
        byte[] cached = File.ReadAllBytes(Path.Combine(_dir.Path, "profiles", "feed.zip"));
        File.Delete(BootPath);
        _http.Reply = _ => throw new HttpRequestException("offline");
        _now += TimeSpan.FromDays(8);

        Assert.Equal(1, Feed().Run(_settings, [SteamGame(_origin)])); // 받기는 실패해도 이전 묶음으로 맞춤

        Assert.Equal(cached, File.ReadAllBytes(Path.Combine(_dir.Path, "profiles", "feed.zip")));
        Assert.Equal(ExpectedBoot(), File.ReadAllBytes(BootPath));
    }

    [Fact]
    public void NoMatchRetryTimesSurviveARestartAndRemovedGamesAreForgotten()
    {
        Serve(GoodZip(SteamEntry("p/foo.json")).Zip);
        Feed().Run(_settings, [SteamGame(_origin, gid: "999")]); // 맞는 프로필 없음: 하루 뒤에 다시
        Assert.Contains("\"Foo\"", File.ReadAllText(StatePath), StringComparison.Ordinal);

        _now += TimeSpan.FromHours(1);
        Assert.Equal(0, Feed().Run(_settings, [SteamGame(_origin)])); // 재시작해도 아직 기다림

        _now += TimeSpan.FromHours(24);
        Assert.Equal(1, Feed().Run(_settings, [SteamGame(_origin)])); // 하루가 지나면 다시 맞춰 봄
    }

    [Fact]
    public void GamesRemovedFromTheSettingsAreDroppedFromTheSavedState()
    {
        Serve(GoodZip(SteamEntry("p/foo.json")).Zip);
        ProfileFeed feed = Feed();
        feed.Run(_settings, [SteamGame(_origin, gid: "999")]);
        Assert.True(File.Exists(StatePath));

        feed.Run(new AsterSettings(50, []), []);

        Assert.DoesNotContain("\"Foo\"", File.ReadAllText(StatePath), StringComparison.Ordinal);
    }

    // ---- 묶음이 믿을 수 없을 때 ----

    [Theory]
    [InlineData("../evil.json")]
    [InlineData("/abs/evil.json")]
    [InlineData("C:/evil.json")]
    [InlineData("a/../../evil.json")]
    [InlineData("a\\evil.json")]
    public void AZipWithAnUnsafeEntryNameIsRejectedWhole(string name)
    {
        Serve(Zip(IndexJson(SteamEntry("p/foo.json")), ("p/foo.json", Profile().ToBytes()), (name, [1, 2, 3])));

        Assert.Equal(0, Feed().Run(_settings, [SteamGame(_origin)]));

        Assert.Contains(_log.Lines, l => l.Contains("unsafe entry name", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(_dir.Path, "profiles", "feed.zip")));
        Assert.False(File.Exists(BootPath));
    }

    [Theory]
    [InlineData("../evil.json")]
    [InlineData("C:/evil.json")]
    [InlineData("p\\foo.json")]
    [InlineData("p/foo.txt")]
    [InlineData("p/missing.json")]
    public void AnIndexEntryPointingAtAnUnsafeOrMissingProfileRejectsTheBundle(string profile)
    {
        Serve(Zip(IndexJson(SteamEntry(profile)), ("p/foo.json", Profile().ToBytes())));

        Assert.Equal(0, Feed().Run(_settings, [SteamGame(_origin)]));
        Assert.False(File.Exists(Path.Combine(_dir.Path, "profiles", "feed.zip")));
    }

    [Fact]
    public void TooManyEntriesIsRejected()
    {
        string path = _dir.Combine("many.zip");
        using (var fs = File.Create(path))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            Put(zip, "index.json", Encoding.UTF8.GetBytes(IndexJson()));
            for (int i = 0; i <= ProfileFeed.MaxZipEntries; i++)
            {
                zip.CreateEntry($"p/{i}.json");
            }
        }

        Serve(path);

        Feed().Run(_settings, []);

        Assert.Contains(_log.Lines, l => l.Contains("too many entries", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(_dir.Path, "profiles", "feed.zip")));
    }

    [Fact]
    public void AZipBombWhoseUnpackedSizeIsOverTheCapIsRejectedWithoutUnpackingIt()
    {
        string path = _dir.Combine("bomb.zip");
        using (var fs = File.Create(path))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            Put(zip, "index.json", Encoding.UTF8.GetBytes(IndexJson()));
            using Stream s = zip.CreateEntry("p/big.json", CompressionLevel.Fastest).Open();
            byte[] chunk = new byte[1 << 20];
            for (int i = 0; i <= ProfileFeed.MaxZipBytes >> 20; i++)
            {
                s.Write(chunk);
            }
        }

        Assert.True(new FileInfo(path).Length < ProfileFeed.MaxDownloadBytes);
        Serve(path);

        Feed().Run(_settings, []);

        Assert.Contains(_log.Lines, l => l.Contains("too large when unpacked", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(_dir.Path, "profiles", "feed.zip")));
    }

    [Fact]
    public void ABrokenBundleDoesNotReplaceAGoodCache()
    {
        Serve(GoodZip(SteamEntry("p/foo.json")).Zip);
        ProfileFeed feed = Feed();
        feed.Run(_settings, [SteamGame(_origin)]);
        byte[] cached = File.ReadAllBytes(Path.Combine(_dir.Path, "profiles", "feed.zip"));
        string bad = _dir.Combine("bad.zip");
        File.WriteAllText(bad, "not a zip");
        _http.Reply = _ => new FeedReply(200, "\"v2\"", File.OpenRead(bad), 9);

        _now += TimeSpan.FromDays(8);
        feed.Run(_settings, [SteamGame(_origin)]);

        Assert.Equal(cached, File.ReadAllBytes(Path.Combine(_dir.Path, "profiles", "feed.zip")));
        Assert.Equal(Tag, File.ReadAllText(Path.Combine(_dir.Path, "profiles", "feed.etag")));
    }

    [Fact]
    public void AnIndexWithTheWrongFormatOrShapeIsRejected()
    {
        foreach (string index in new[]
        {
            "[]",
            "{\"format\":\"other\",\"version\":1,\"entries\":[]}",
            "{\"format\":\"astercache-profile-index\",\"version\":2,\"entries\":[]}",
            "{\"format\":\"astercache-profile-index\",\"version\":1,\"entries\":[{\"profile\":\"p/foo.json\"}]}", // 지문 없음
            "{\"format\":\"astercache-profile-index\",\"version\":1,\"entries\":[{\"steam\":{\"appid\":\"1\"},\"epic\":{\"app_name\":\"a\",\"version\":\"1\"},\"profile\":\"p/foo.json\"}]}", // 둘 이상
            "{\"format\":\"astercache-profile-index\",\"version\":1,\"entries\":[{\"exe\":{\"size\":1,\"sample_sha256\":\"XYZ\"},\"profile\":\"p/foo.json\"}]}",
            IndexJson(SteamEntry("p/foo.json", depots: "{}")), // 빈 depots: 거부 (키가 없으면 appid 만 봄)
        })
        {
            Serve(Zip(index, ("p/foo.json", Profile().ToBytes())));
            _now += TimeSpan.FromDays(8);

            Assert.Equal(0, Feed().Run(_settings, [SteamGame(_origin)]));
            Assert.False(File.Exists(Path.Combine(_dir.Path, "profiles", "feed.zip")));
        }
    }

    [Fact]
    public void AProfileOverTheSizeCapOrNotAProfileIsSkipped()
    {
        string big = _dir.Combine("bigprofile.zip");
        using (var fs = File.Create(big))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            Put(zip, "index.json", Encoding.UTF8.GetBytes(IndexJson(SteamEntry("p/big.json"), SteamEntry("p/junk.json"))));
            using (Stream s = zip.CreateEntry("p/big.json", CompressionLevel.Fastest).Open())
            {
                s.Write(new byte[(int)GameProfile.MaxBytes + 1]);
            }

            Put(zip, "p/junk.json", Encoding.UTF8.GetBytes("{\"format\":\"x\"}"));
        }

        Serve(big);

        Assert.Equal(0, Feed().Run(_settings, [SteamGame(_origin)]));
        Assert.False(File.Exists(BootPath));
    }
}
