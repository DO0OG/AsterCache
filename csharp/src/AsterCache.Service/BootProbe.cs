using System.Diagnostics;
using System.Runtime.InteropServices;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using Microsoft.Win32.SafeHandles;

namespace AsterCache.Service;

/// 부팅 뒤 콜드 비용 진단: 서비스가 부팅 뒤 첫 시작이면 (가동 시간 10 분 미만), 가장 최근에 한 게임의 선읽기 목록 앞쪽 파일 MaxFiles 개를 메타데이터만 열었다 닫아 파일당 평균 열기 시간을 재서 남깁니다.
/// 서비스 시작 예열이 파일을 올리기 직전에 부르므로 아직 아무것도 데워지지 않은 상태의 비용입니다. 데이터는 읽지 않고 (FILE_READ_ATTRIBUTES, 공유 읽기·쓰기·삭제) 잠금도 쓰기도 없습니다.
public static partial class BootProbe
{
    public const int MaxFiles = 200;
    public static readonly TimeSpan MaxUptime = TimeSpan.FromMinutes(10);

    private const uint FileReadAttributes = 0x80;
    private const uint ShareAll = 7; // 읽기 | 쓰기 | 삭제
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000; // 디렉터리·읽기 접근 없는 열기

    /// 진단을 하고 결과를 auto_status 에 남깁니다. 하지 않았거나 열린 파일이 없으면 null (실패한 열기는 개수에 넣지 않고 시간도 평균에 넣지 않음).
    /// driveBusy: 그 게임의 드라이브에서 게임이 도는 중이면 true (그러면 새 읽기를 내지 않고 건너뜀). uptimeMs: 시스템 가동 시간 (ms). files: 게임의 선읽기 목록 (파일을 열지 않고 경로만). open: 파일 하나를 열었다 닫음 (열었으면 true). elapsed: 단조 증가 시계.
    public static AutoBootProbe? Run(
        AsterSettings settings, AutoStatus status, ILog log, DateTimeOffset now, long uptimeMs, Func<GameSettings, IReadOnlyList<string>> files, Func<string, bool> open, Func<TimeSpan> elapsed, Func<GameSettings, bool>? driveBusy = null, CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, DateTimeOffset> played = status.LastPlayed();
        GameSettings? game = settings.Games.Where(g => played.ContainsKey(g.Name)).OrderByDescending(g => played[g.Name]).ThenBy(g => g.Name, StringComparer.Ordinal).FirstOrDefault();
        if (!settings.Auto.Enabled || game is null || TimeSpan.FromMilliseconds(uptimeMs) >= MaxUptime || GameRunning(driveBusy, game))
        {
            return null;
        }

        int opened = 0;
        TimeSpan total = TimeSpan.Zero;
        foreach (string path in files(game).Take(MaxFiles))
        {
            if (ct.IsCancellationRequested || GameRunning(driveBusy, game))
            {
                break; // 게임이 떠서 예열이 취소되면 (또는 파일 사이에 같은 디스크 게임이 떴으면) 지금까지 연 것만으로 마무리
            }

            TimeSpan t0 = elapsed();
            bool ok = open(path);
            if (ok)
            {
                total += elapsed() - t0;
                opened++;
            }
        }

        if (opened == 0)
        {
            return null;
        }

        var probe = new AutoBootProbe(now, opened, Math.Round(total.TotalMilliseconds / opened, 3));
        status.RecordBootProbe(game.Name, probe);
        log.Info($"{game.Name}: boot probe opened {probe.Files} files, {probe.OpenMsPerFile:F2} ms per file (cold metadata, uptime {(int)(uptimeMs / 60000)} min)");
        return probe;
    }

    // 연결되지 않았거나 (null) 판정이 예외로 실패하면 게임이 도는 것으로 봄 (게임 디스크를 열지 않는 쪽).
    private static bool GameRunning(Func<GameSettings, bool>? driveBusy, GameSettings game)
    {
        try
        {
            return driveBusy?.Invoke(game) ?? true;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// 선읽기 목록 (game.Trace) 의 경로를 나온 순서대로, 중복 없이 MaxFiles 개까지. 파일 크기를 재거나 경로를 풀지 않습니다 (재면 그 파일이 먼저 데워짐). 기록이 없거나 읽을 수 없으면 빈 목록.
    public static IReadOnlyList<string> TracePaths(GameSettings game)
    {
        try
        {
            return [.. TraceLoader.LoadRanges(game.Trace, game.OriginRoot, p => p, _ => long.MaxValue).Select(r => r.Path).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxFiles)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
        {
            return [];
        }
    }

    /// 파일의 메타데이터만 열었다 닫음 (읽기 접근 없음, 데이터 읽기 없음).
    public static bool OpenClose(string path)
    {
        using SafeFileHandle h = CreateFileW(path, FileReadAttributes, ShareAll, 0, OpenExisting, BackupSemantics, 0);
        return !h.IsInvalid;
    }

    /// 실제 환경으로 한 번: 시계는 Stopwatch, 가동 시간은 Environment.TickCount64.
    public static AutoBootProbe? RunReal(AsterSettings settings, AutoStatus status, ILog log, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        return Run(settings, status, log, DateTimeOffset.UtcNow, Environment.TickCount64, TracePaths, OpenClose, () => sw.Elapsed,
            g => AutoRelocator.SameDriveGames(settings.Games, [g], AsterCache.Windows.VolumeDisks.SameDisk).Concat([g]).Any(DispatchEnv.IsGameRunning), ct);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(string fileName, uint access, uint share, nint security, uint disposition, uint flags, nint template);
}
