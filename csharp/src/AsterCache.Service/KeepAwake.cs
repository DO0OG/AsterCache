using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using AsterCache.Core;
using AsterCache.Core.Logging;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Windows;

namespace AsterCache.Service;

/// KeepAwake 가 바깥 세계에 닿는 곳 전부. 시험은 가짜로 바꿉니다.
public sealed class KeepAwakeEnv
{
    private const int BatteryNeverOk = 101; // 배터리면 남은 비율과 상관없이 거짓

    /// 지역 시각 (습관 판정이 요일·시각을 이 오프셋으로 봄).
    public required Func<DateTimeOffset> Now { get; init; }

    /// 'X:' 가 HDD 인가 (SSD 면 false, 모르면 null).
    public required Func<string, bool?> IsHdd { get; init; }

    public required Func<IEnumerable<string>, bool> AnyRunning { get; init; }

    public required Func<bool> PowerOk { get; init; }

    /// 게임별 플레이 시각들 (AutoStatus.Plays).
    public required Func<IReadOnlyDictionary<string, IReadOnlyList<DateTimeOffset>>> Plays { get; init; }

    /// 그 게임 설치 폴더의 파일 하나, 없으면 null.
    public required Func<GameSettings, string?> PickFile { get; init; }

    /// 파일 앞 4 KiB 를 비버퍼드로 읽음. 실패하면 IOException 등.
    public required Action<string> ReadSector { get; init; }

    /// 현재 AC 전원의 "디스크 끄기" 시간 (초, 0 = 안 끔), 읽지 못하면 null.
    public required Func<int?> DiskIdleSeconds { get; init; }

    public static KeepAwakeEnv Create(Func<IReadOnlyDictionary<string, IReadOnlyList<DateTimeOffset>>> plays) => new()
    {
        Now = () => DateTimeOffset.Now,
        IsHdd = letter => DiskInfo.Query(letter).IncursSeekPenalty,
        AnyRunning = exes => ProcessSnapshot.IsRunning(exes),
        PowerOk = () => PowerProbe.PowerOk(BatteryNeverOk),
        Plays = plays,
        PickFile = KeepAwake.FirstFile,
        ReadSector = path =>
        {
            using var reader = new UnbufferedReader();
            reader.Read(path, 0, KeepAwake.ReadBytes);
        },
        DiskIdleSeconds = KeepAwake.PowercfgIdleSeconds,
    };
}

/// HDD 깨워 두기: Windows 가 유휴 N 분 뒤 HDD 를 끄면 다음 첫 접근이 스핀업 수 초라, 게임을 할 것 같은 동안 (런처 실행 중이거나 지금이 플레이하던 시간대) 10 분마다 게임 드라이브에서 4 KiB 를 읽습니다.
/// 전원 설정은 읽기만 하고 바꾸지 않습니다. 서비스 로그는 드라이브별 시작·중단 때만 한 줄.
public sealed class KeepAwake
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(10);
    public const int ReadBytes = 4096;

    private readonly KeepAwakeEnv _env;
    private readonly ILog _log;
    private readonly Func<AsterSettings> _settings;
    private readonly HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _next = DateTimeOffset.MinValue;
    private bool _probed;
    private bool _off; // 디스크 끄기 시간이 0 이라 필요 없음

    public KeepAwake(KeepAwakeEnv env, ILog log, Func<AsterSettings> settings)
    {
        _env = env;
        _log = log;
        _settings = settings;
    }

    /// 한 번에 한 스레드에서 부릅니다 (Worker 의 전용 루프). 10 분이 안 지났으면 아무것도 안 합니다.
    public void Tick()
    {
        AsterSettings s = _settings();
        if (!s.Auto.Enabled || !s.Auto.KeepAwake)
        {
            Update([]);
            return;
        }

        DateTimeOffset now = _env.Now();
        if (now < _next)
        {
            return;
        }

        _next = now + Every;
        if (!_probed)
        {
            _probed = true;
            if (_env.DiskIdleSeconds() == 0)
            {
                _off = true;
                _log.Info("disk idle timeout off, keep-awake not needed");
            }
        }

        if (_off || !_env.PowerOk())
        {
            Update([]);
            return;
        }

        var awake = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hdd = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, IReadOnlyList<DateTimeOffset>> plays = _env.Plays();
        foreach (GameSettings g in s.Games)
        {
            string letter = PlanFile.SplitDrive(g.OriginRoot).ToUpperInvariant();
            if (awake.Contains(letter))
            {
                continue;
            }

            if (!hdd.TryGetValue(letter, out bool isHdd))
            {
                hdd[letter] = isHdd = _env.IsHdd(letter) != false;
            }

            bool wanted = isHdd && ((g.Prewarm.TriggerExes.Count > 0 && _env.AnyRunning(g.Prewarm.TriggerExes))
                || (plays.TryGetValue(g.Name, out IReadOnlyList<DateTimeOffset>? p) && PlayedAround(p, now)));
            if (wanted && _env.PickFile(g) is { } file && TryRead(file))
            {
                awake.Add(letter);
            }
        }

        Update(awake);
    }

    /// 서비스 종료: 깨워 두던 드라이브가 있으면 중단 로그.
    public void Stop() => Update([]);

    /// 같은 요일·±2 시간 (PrewarmScore 의 습관 창) 에 플레이한 적이 있는가.
    public static bool PlayedAround(IReadOnlyList<DateTimeOffset> plays, DateTimeOffset now) =>
        plays.Any(p => p.ToOffset(now.Offset) is var q && q.DayOfWeek == now.DayOfWeek && Math.Abs((q.TimeOfDay - now.TimeOfDay).TotalMinutes) <= PrewarmScore.HabitWindowMinutes);

    /// `powercfg /q SCHEME_CURRENT SUB_DISK DISKIDLE` 출력에서 현재 AC 설정 (초): 끝의 0x 값 두 줄이 AC, DC 순 (문구는 OS 언어를 따라 바뀌므로 줄 끝 값만 봄). 못 읽으면 null.
    public static int? ParseAcIdleSeconds(string output)
    {
        MatchCollection m = Regex.Matches(output, @"0x([0-9a-fA-F]{1,8})[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        return m.Count >= 2 && uint.TryParse(m[^2].Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v) ? (int)Math.Min(v, int.MaxValue) : null;
    }

    internal static int? PowercfgIdleSeconds()
    {
        try
        {
            var info = new ProcessStartInfo("powercfg", "/q SCHEME_CURRENT SUB_DISK DISKIDLE") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using Process p = Process.Start(info)!;
            Task<string> text = p.StandardOutput.ReadToEndAsync();
            return p.WaitForExit(5000) && p.ExitCode == 0 ? ParseAcIdleSeconds(text.GetAwaiter().GetResult()) : null;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    internal static string? FirstFile(GameSettings g)
    {
        try
        {
            return Directory.EnumerateFiles(g.OriginRoot, "*", SearchOption.TopDirectoryOnly).FirstOrDefault()
                ?? Directory.EnumerateFiles(g.OriginRoot, "*", SearchOption.AllDirectories).FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private bool TryRead(string file)
    {
        try
        {
            _env.ReadSector(file);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return false; // 다음 주기에 다시 (로그는 시작·중단만)
        }
    }

    private void Update(HashSet<string> awake)
    {
        foreach (string gone in _active.Where(l => !awake.Contains(l)).ToList())
        {
            _active.Remove(gone);
            _log.Info($"keep-awake stopped: {gone}");
        }

        foreach (string added in awake.Where(_active.Add))
        {
            _log.Info($"keep-awake started: {added}");
        }
    }
}
