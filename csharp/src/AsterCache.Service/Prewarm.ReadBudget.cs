using AsterCache.Core.Logging;
using AsterCache.Core.Settings;
using AsterCache.Windows;

namespace AsterCache.Service;

/// Prewarmer 의 계기 없는 디스크 읽기 하루 상한: 게임 실행 계기 없이 주기로 도는 재예열·보충 예열·사다리가 드라이브마다 24시간에 읽는 양을 DiskReadLedger 로 세어 상한에 닿으면 다음 창까지 쉽니다.
/// 게임·런처 시작과 게임 종료 뒤 한 번의 다시 채우기, 서비스 시작 예열, 재배치 뒤 예열, 사용자가 시킨 명령은 상한의 대상이 아니라 세기만 합니다 (상한에 닿아도 평소처럼 읽음).
/// 세는 값은 스트림이 받아들인 바이트 (StreamStats.Bytes) 입니다: 스트림은 캐시에 이미 있는 범위와 디스크에서 읽은 범위를 구별하지 않으므로 실제 디스크 읽기보다 많게 셀 수 있습니다.
/// 사다리의 만지기는 디스크로 읽힌 바이트 (TouchResult.DiskBytes), 표본 점검은 느린 (디스크에서 읽힌) 표본 수 x 표본 크기로 셉니다.
public sealed partial class Prewarmer
{
    /// 기본 상한의 바닥 (바이트): 예열 목록이 작은 드라이브도 이만큼은 읽을 수 있음.
    internal const long IdleReadFloorBytes = 8L << 30;

    /// 기본 상한은 그 드라이브 게임들의 예열 목록 합의 이 배수 (목록을 전부 두 번 다시 읽는 양).
    internal const int IdleReadListFactor = 2;

    private readonly DiskReadLedger _reads; // 드라이브별·목적별 읽은 양의 24시간 집계, 상태 파일에 함께 저장

    internal DiskReadLedger DiskReads => _reads;

    /// 게임이 마지막으로 플레이된 시각 (자동 모드가 넣음, 없으면 null): 하루 읽기 예산을 가장 최근에 플레이한 게임부터 쓰게 순서를 정하는 데 씀.
    public Func<string, DateTimeOffset?>? LastPlayedAt { get; set; }

    /// 게임을 마지막으로 플레이한 시각 (모르면 가장 오래된 시각): OrderByDescending 에 넘겨 가장 최근에 플레이한 게임이 앞에 오게 합니다 (같은 시각이면 받은 순서 그대로).
    internal DateTimeOffset PlayedAt(string game) => LastPlayedAt?.Invoke(game) ?? DateTimeOffset.MinValue;

    /// 이 게임 드라이브의 계기 없는 읽기 하루 상한 (바이트): auto.idle_read_gib_per_day 가 0 보다 크면 그 값, 아니면 그 드라이브에 놓인 (예열이 허용된) 게임들의 예열 목록 합의 2 배와 8 GiB 가운데 큰 값.
    internal long IdleReadCap(GameSettings game)
    {
        if (_auto.IdleReadGibPerDay > 0)
        {
            return (long)_auto.IdleReadGibPerDay << 30;
        }

        string drive = DiskReadLedger.DriveOf(game.OriginRoot);
        long listMib = _games.Where(g => GamePolicy.AllowsPrewarm(g) && DiskReadLedger.DriveOf(g.OriginRoot) == drive).Sum(g => ZoneMib(g, estimate: true));
        return Math.Max(IdleReadFloorBytes, MibBytes(listMib) * IdleReadListFactor);
    }

    private readonly HashSet<string> _idleReadsPaused = new(StringComparer.Ordinal); // 상한에 닿아 계기 없는 읽기를 쉬는 드라이브 (쉬기 시작할 때와 풀릴 때만 로그), _gate 로 보호

    /// 이 게임 드라이브가 이미 계기 없는 읽기 상한에 닿았는가: 닿았으면 계기 없는 읽기를 시작하지 않습니다 (읽는 도중에는 끊지 않으므로 한 번의 읽기만큼 넘길 수 있음).
    /// 가장 오래된 읽기가 24시간 창에서 빠져 다시 읽을 수 있는 시각과 함께, 쉬기 시작할 때와 풀릴 때만 로그를 남깁니다.
    internal bool IdleReadsExhausted(GameSettings game)
    {
        long cap = IdleReadCap(game);
        (long used, _) = _reads.Used(game.OriginRoot, ReadPurpose.Idle);
        string drive = DiskReadLedger.DriveOf(game.OriginRoot);
        bool exhausted = used >= cap;
        bool changed;
        lock (_gate)
        {
            changed = exhausted ? _idleReadsPaused.Add(drive) : _idleReadsPaused.Remove(drive);
        }

        if (changed)
        {
            _log.Info(exhausted
                ? $"idle disk reads of drive {drive} paused: {MibText(used)} read of the {MibText(cap)} limit in the last 24 h, reading again from {_reads.FreeAt(game.OriginRoot, ReadPurpose.Idle, (bytes, _) => bytes < cap):O} (reads started by a game or launcher are not limited)"
                : $"idle disk reads of drive {drive} resumed: {MibText(used)} read of the {MibText(cap)} limit in the last 24 h");
        }

        return exhausted;
    }

    /// 표본 점검이 디스크에서 읽은 양을 더합니다: 느린 (디스크에서 읽힌) 표본 수 x 표본 크기 (캐시에서 읽힌 표본은 디스크를 읽지 않았으므로 셈하지 않음).
    internal void NoteProbeRead(GameSettings game, ProbeResult? probe)
    {
        if (probe is { Slow: > 0 })
        {
            _reads.Add(game.OriginRoot, DiskReadLedger.Current, (long)probe.Slow * CacheProbe.SampleBytes);
        }
    }

    // 하루 한 번: 최근 24시간에 드라이브마다 목적별로 읽은 양을 한 줄로 남김 (Tick 이 부름, 서비스를 다시 시작해도 마지막 기록 시각이 이어져 하루에 한 번).
    private void LogDiskReadsDaily()
    {
        if (!_reads.TakeDailyLog())
        {
            return;
        }

        IEnumerable<string> drives = _reads.Drives().Select(drive =>
        {
            (long trigger, _) = _reads.Used(drive, ReadPurpose.Trigger);
            (long idle, _) = _reads.Used(drive, ReadPurpose.Idle);
            (long recovery, int samples) = _reads.Used(drive, ReadPurpose.Recovery);
            GameSettings? any = _games.FirstOrDefault(g => DiskReadLedger.DriveOf(g.OriginRoot) == drive);
            string cap = any is null ? string.Empty : $" of {MibText(IdleReadCap(any))} limit";
            return $"{drive} trigger reads {MibText(trigger)}, idle reads {MibText(idle)}{cap}, recovery reads {MibText(recovery)} in {samples} samples";
        });
        _log.Info("disk reads in the last 24 h: " + string.Join("; ", drives));
    }
}
