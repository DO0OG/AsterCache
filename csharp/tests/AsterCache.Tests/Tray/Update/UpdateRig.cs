using System.Net;
using System.Security.Cryptography;
using AsterCache.Core.Logging;
using AsterCache.Core.Settings;
using AsterCache.Tray.Update;

namespace AsterCache.Tests.Tray;

/// 네트워크 없이 요청을 받아 주소별로 응답하는 가짜 핸들러 (받은 주소를 모두 기록).
internal sealed class FakeHandler : HttpMessageHandler
{
    public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<HttpRequestMessage> Requests { get; } = [];

    public IEnumerable<string> Urls => Requests.Select(r => r.RequestUri!.AbsoluteUri);

    public void Serve(string url, byte[] body) => Routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    public void Serve(string url, string body) => Serve(url, System.Text.Encoding.UTF8.GetBytes(body));

    public void Redirect(string from, string to) => Routes[from] = () => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(to, UriKind.RelativeOrAbsolute) } };

    public void Fail(string url, HttpStatusCode code = HttpStatusCode.InternalServerError) => Routes[url] = () => new HttpResponseMessage(code);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(Routes.TryGetValue(request.RequestUri!.AbsoluteUri, out Func<HttpResponseMessage>? make) ? make() : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

internal sealed class FakeGate : IUpdateGate
{
    public GateState State { get; set; } = GateState.Idle;

    public GateState Query() => State;
}

internal sealed class FakeLauncher : IInstallerLauncher
{
    public List<(string Path, string[] Args)> Launches { get; } = [];

    public void Launch(string installerPath, IReadOnlyList<string> arguments) => Launches.Add((installerPath, [.. arguments]));
}

/// 기다리지 않고 요청된 지연만 기록합니다. Stop 번째 지연에서 취소합니다.
internal sealed class FakeClock(int stopAfter = int.MaxValue) : IUpdateClock
{
    public List<TimeSpan> Delays { get; } = [];

    public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
    {
        Delays.Add(delay);
        return Delays.Count >= stopAfter ? throw new OperationCanceledException() : Task.CompletedTask;
    }
}

internal sealed class ListLog : ILog
{
    public List<string> Lines { get; } = [];

    public void Write(LogLevel level, string message, Exception? exception = null) => Lines.Add(message);
}

/// 업데이트 한 벌 (가짜 HTTP, 임시 데이터 폴더, 가짜 게임 상태·실행기·시계).
internal sealed class UpdateRig : IDisposable
{
    public const string Latest = "https://api.github.com/repos/DO0OG/AsterCache/releases/latest";
    public const string List = "https://api.github.com/repos/DO0OG/AsterCache/releases?per_page=20";

    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "aster_update_" + Guid.NewGuid().ToString("N"));

    public byte[] Installer { get; set; } = RandomNumberGenerator.GetBytes(4096);

    public FakeHandler Handler { get; } = new();

    public FakeGate Gate { get; } = new();

    public FakeLauncher Launcher { get; } = new();

    public FakeClock Clock { get; set; } = new();

    public ListLog Log { get; } = new();

    public List<Uri> Opened { get; } = [];

    public UpdateOptions Options { get; set; } = new(UpdateMode.Auto, false);

    public bool Admin { get; set; } = true;

    public bool Installed { get; set; } = true;

    public string Current { get; set; } = "0.9.0-beta.38";

    public UpdateLimits Limits { get; set; } = new();

    public Func<string, bool> IsReparse { get; set; } = _ => false;

    /// 가짜 ACL 판정: 신뢰 밖 쓰기 권한자 SID 목록 (비어 있으면 안전).
    public Func<string, IReadOnlyList<string>> DirWriters { get; set; } = _ => [];

    public Func<string, IReadOnlyList<string>> FileWriters { get; set; } = _ => [];

    public string Sha => Convert.ToHexStringLower(SHA256.HashData(Installer));

    public string StagingDir => Path.Combine(DataDir, "updates");

    public UpdateRig()
    {
        Directory.CreateDirectory(DataDir);
    }

    public static string InstallerName(string version) => $"AsterCache-v{version}-win-x64-setup.exe";

    public static string DownloadUrl(string version, string name) => $"https://github.com/DO0OG/AsterCache/releases/download/v{version}/{name}";

    /// 릴리스 한 건의 JSON. digest 가 null 이고 sums 가 false 면 체크섬이 없는 릴리스입니다.
    public string Release(string version, bool prerelease = false, bool draft = false, bool digest = true, bool sums = false, long? size = null, string? name = null)
    {
        name ??= InstallerName(version);
        string digestField = digest ? $",\"digest\":\"sha256:{Sha}\"" : string.Empty;
        string sumsAsset = sums ? $",{{\"name\":\"SHA256SUMS.txt\",\"size\":100,\"browser_download_url\":\"{DownloadUrl(version, "SHA256SUMS.txt")}\"}}" : string.Empty;
        return $"{{\"tag_name\":\"v{version}\",\"draft\":{(draft ? "true" : "false")},\"prerelease\":{(prerelease ? "true" : "false")},\"assets\":["
            + $"{{\"name\":\"{name}\",\"size\":{size ?? Installer.Length},\"browser_download_url\":\"{DownloadUrl(version, name)}\"{digestField}}}{sumsAsset}]}}";
    }

    /// latest 주소에 이 릴리스를 올리고 설치 파일 (그리고 요청하면 SHA256SUMS.txt) 도 내려받게 해 둡니다.
    public void Publish(string version, bool digest = true, bool sums = false, long? size = null, string? sumsText = null)
    {
        Handler.Serve(Latest, Release(version, digest: digest, sums: sums, size: size));
        Handler.Serve(DownloadUrl(version, InstallerName(version)), Installer);
        if (sums)
        {
            Handler.Serve(DownloadUrl(version, "SHA256SUMS.txt"), sumsText ?? $"{Sha}  {InstallerName(version)}\n");
        }
    }

    public AutoUpdater Build(Func<UpdateOptions>? options = null)
    {
        var context = new UpdateContext(SemVer.TryParse(Current, out SemVer v) ? v : throw new InvalidOperationException(Current), Admin, Installed);
        return new AutoUpdater(context, options ?? (() => Options), new UpdateHttp("test", Limits, Handler), Installed ? new UpdateStaging(DataDir, IsReparse, p => DirWriters(p), p => FileWriters(p)) : null, Gate, Launcher, Clock, Opened.Add, Log, () => "korean");
    }

    public void Dispose()
    {
        if (Directory.Exists(DataDir))
        {
            Directory.Delete(DataDir, true);
        }
    }
}
