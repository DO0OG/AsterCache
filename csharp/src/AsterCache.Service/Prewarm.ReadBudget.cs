using AsterCache.Core.Logging;
using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Windows;

namespace AsterCache.Service;

/// Prewarmer 의 계기 없는 디스크 읽기 하루 상한: 게임 실행 계기 없이 주기로 도는 재예열·보충 예열·사다리가 드라이브마다 24시간에 읽는 양을 DiskReadLedger 로 세어 상한에 닿으면 다음 창까지 쉽니다.
/// 게임·런처 시작과 게임 종료 뒤 한 번의 다시 채우기, 서비스 시작 예열, 재배치 뒤 예열, 사용자가 시킨 명령은 상한의 대상이 아니라 세기만 합니다 (상한에 닿아도 평소처럼 읽음).
/// 세는 값은 스트림이 받아들인 바이트 (StreamStats.Bytes) 입니다: 스트림은 캐시에 이미 있는 범위와 디스크에서 읽은 범위를 구별하지 않으므로 실제 디스크 읽기보다 많게 셀 수 있습니다.
/// 사다리의 만지기는 디스크로 읽힌 바이트 (TouchResult.DiskBytes), 표본 점검은 느린 (디스크에서 읽힌) 표본 수 x 표본 크기로 셉니다.
public sealed partial class Prewarmer
{
    /// 계기 없는 읽기의 하루 상한에 닿아 읽지 않고 건너뛴 예열의 사유 (재시도 대상이 아님: 다음 게임·런처 시작 예열이 채움).
    internal const string IdleReadLimit = "idle read limit";

    /// 기본 상한의 바닥 (바이트): 예열 목록이 작은 드라이브도 이만큼은 읽을 수 있음.
    internal const long IdleReadFloorBytes = 12L << 30;

    /// 기본 상한은 그 드라이브 게임들의 예열 목록 합의 이 배수, 게임마다의 몫도 그 게임 목록의 이 배수 이상입니다: 목록을 하루에 세 번 통째로 다시 채울 수 있는 용량입니다 (시간으로 환산하면 약 8시간마다 한 번이라는 어림일 뿐, 캐시가 식는 빈도는 재지 않았음).
    /// 플레이 직전의 채우기는 런처·게임 시작 예열이 이 상한과 무관하게 합니다. 2 배는 이 PC 의 최근 로그에 대입한 어림에서 일상적으로 상한에 걸려 올렸습니다.
    internal const int IdleReadListFactor = 3;

    private readonly DiskReadLedger _reads; // 드라이브별·목적별 읽은 양의 24시간 집계, 상태 파일에 함께 저장

    internal DiskReadLedger DiskReads => _reads;

    /// 게임이 마지막으로 플레이된 시각 (자동 모드가 넣음, 없으면 null): 하루 읽기 예산을 가장 최근에 플레이한 게임부터 쓰게 순서를 정하는 데 씀.
    public Func<string, DateTimeOffset?>? LastPlayedAt { get; set; }

    /// 게임을 마지막으로 플레이한 시각 (모르면 가장 오래된 시각): OrderByDescending 에 넘겨 가장 최근에 플레이한 게임이 앞에 오게 합니다 (같은 시각이면 받은 순서 그대로).
    internal DateTimeOffset PlayedAt(string game) => LastPlayedAt?.Invoke(game) ?? DateTimeOffset.MinValue;

    /// 이 게임 드라이브의 계기 없는 읽기 하루 상한 (바이트): auto.idle_read_gib_per_day 가 0 보다 크면 그 값, 아니면 그 드라이브에 놓인 (예열이 허용된) 게임들의 예열 목록 합의 3 배와 12 GiB 가운데 큰 값.
    internal long IdleReadCap(GameSettings game) => IdleReadLimits(game).Cap;

    /// 목록 크기를 모르는 게임의 하루 몫 (바이트): 이만큼과 드라이브에 남은 양 중 작은 값까지만 읽음.
    internal const long IdleReadUnknownListShare = 1L << 30;

    /// 드라이브 상한과 이 게임의 하루 몫 (바이트): 몫은 상한을 그 드라이브 게임들의 예열 목록 크기에 비례해 나눈 것이라 기본 상한에서는 그 게임 목록의 3 배 이상입니다.
    /// 목록 크기를 모르는 게임의 몫은 IdleReadUnknownListShare. 지금 설정에 없는 (지운) 게임은 나누는 데 들지 않으므로 남은 게임의 몫을 바꾸지 않습니다 (그 게임이 쓴 양은 드라이브 합계에는 남음).
    private (long Cap, long Share) IdleReadLimits(GameSettings game)
    {
        string drive = DiskReadLedger.DriveOf(game.OriginRoot);
        List<(string Name, long Bytes)> lists = [.. _games.Where(g => GamePolicy.AllowsPrewarm(g) && DiskReadLedger.DriveOf(g.OriginRoot) == drive).Select(g => (g.Name, MibBytes(ZoneMib(g, estimate: true))))];
        long listSum = lists.Sum(l => l.Bytes);
        long cap = _auto.IdleReadGibPerDay > 0 ? (long)_auto.IdleReadGibPerDay << 30 : Math.Max(IdleReadFloorBytes, listSum * IdleReadListFactor);
        long own = lists.Where(l => string.Equals(l.Name, game.Name, StringComparison.OrdinalIgnoreCase)).Sum(l => l.Bytes);
        return (cap, Math.Min(cap, own > 0 && listSum > 0 ? (long)((double)cap * own / listSum) : IdleReadUnknownListShare));
    }

    /// 이 게임의 하루 몫 (바이트).
    internal long IdleReadShare(GameSettings game) => IdleReadLimits(game).Share;

    /// 이 게임이 지금 계기 없이 더 읽을 수 있는 양 (바이트): 게임 몫과 드라이브 상한 가운데 남은 양이 더 작은 쪽, 다 썼으면 0.
    internal long IdleReadRemaining(GameSettings game)
    {
        (long cap, long share) = IdleReadLimits(game);
        long left = Math.Min(share - _reads.Used(game.OriginRoot, ReadPurpose.Idle, game.Name).Bytes, cap - _reads.Used(game.OriginRoot, ReadPurpose.Idle).Bytes);
        return Math.Max(0, left);
    }

    /// 읽기를 시작하기 전에 남은 양을 그 읽기의 예산 상한 (MiB) 에 반영합니다: 남은 양이 지금 정해진 상한 (room, 없으면 제한 없음) 보다 작으면 그 값으로 줄여 한 번의 읽기가 상한을 넘는 크기를 줄임.
    /// 목록 전체가 남은 양에 들어가면 바꾸지 않습니다. 계기 있는 읽기 범위에서는 그대로.
    internal long? IdleReadRoom(GameSettings game, long? room)
    {
        if (DiskReadLedger.Current != ReadPurpose.Idle)
        {
            return room;
        }

        long leftMib = IdleReadRemaining(game) >> 20;
        return leftMib < ZoneMib(game, estimate: true) && leftMib < (room ?? long.MaxValue) ? leftMib : room;
    }

    private readonly HashSet<string> _idleReadsPaused = new(StringComparer.Ordinal); // 상한에 닿아 계기 없는 읽기를 쉬는 드라이브·게임 (쉬기 시작할 때와 풀릴 때만 로그), _gate 로 보호

    /// 이 게임의 계기 없는 읽기를 지금 시작하면 안 되는가: 게임의 하루 몫과 드라이브 상한 가운데 하나라도 다 썼을 때입니다. 몫 덕에 한 게임이 드라이브 상한을 다 쓰지 못합니다.
    /// 읽는 도중에는 끊지 않지만 읽기를 시작할 때 남은 양을 창 상한으로 넘기므로 (IdleReadRoom) 한 번의 읽기가 넘는 양은 작습니다.
    /// 가장 오래된 읽기가 24시간 창에서 빠져 다시 읽을 수 있는 시각과 함께, 쉬기 시작할 때와 풀릴 때만 로그를 남깁니다.
    internal bool IdleReadsExhausted(GameSettings game)
    {
        (long cap, long share) = IdleReadLimits(game);
        long usedGame = _reads.Used(game.OriginRoot, ReadPurpose.Idle, game.Name).Bytes;
        long usedDrive = _reads.Used(game.OriginRoot, ReadPurpose.Idle).Bytes;
        bool byGame = usedGame >= share;
        bool exhausted = byGame || usedDrive >= cap;
        string drive = DiskReadLedger.DriveOf(game.OriginRoot);
        string what = byGame ? $"{game.Name} on drive {drive}" : $"drive {drive}";
        long used = byGame ? usedGame : usedDrive;
        long limit = byGame ? share : cap;
        bool changed;
        lock (_gate)
        {
            if (!exhausted && _idleReadsPaused.Remove($"{game.Name} on drive {drive}"))
            {
                what = $"{game.Name} on drive {drive}"; // 풀린 쪽 이름으로 로그
                changed = true;
            }
            else
            {
                changed = exhausted ? _idleReadsPaused.Add(what) : _idleReadsPaused.Remove($"drive {drive}");
            }
        }

        if (changed)
        {
            _log.Info(exhausted
                ? $"idle disk reads of {what} paused: {MibText(used)} read of the {MibText(limit)} limit in the last 24 h, reading again from {_reads.FreeAt(game.OriginRoot, ReadPurpose.Idle, (bytes, _) => bytes < limit, byGame ? game.Name : null):O} (reads started by a game or launcher are not limited)"
                : $"idle disk reads of {what} resumed: {MibText(usedDrive)} read of the {MibText(cap)} drive limit in the last 24 h");
        }

        return exhausted;
    }

    /// 읽기 한 번 (스트림) 이 디스크에서 읽은 양을 어림해 집계에 더합니다 (게임별 몫으로도 셈). 시간은 스트리머가 잰, 그 읽기가 실제로 읽은 시간입니다 (대기·양보로 쉰 시간과 사다리가 만진 시간은 들지 않음: 사다리는 자기 양을 따로 더함).
    internal void NoteStreamRead(GameSettings game, StreamStats stats) => NoteReadBytes(game, stats.Bytes, stats.Seconds);

    internal void NoteReadBytes(GameSettings game, long accepted, double seconds)
    {
        double ceiling = Recovery?.CeilingFor(DiskReadLedger.DriveOf(game.OriginRoot)) ?? DriveRecovery.CeilingMbps;
        _reads.Add(game.OriginRoot, DiskReadLedger.Current, CountedDiskBytes(accepted, seconds, ceiling), game.Name);
    }

    /// 받아들인 바이트 (accepted) 를 실제 디스크 읽기에 가깝게 줄입니다: 받아들인 바이트와 읽은 시간 x 그 드라이브의 순차 읽기 상한 (MB/s) 가운데 작은 값, 상한이나 시간을 모르면 받아들인 바이트.
    /// 디스크에서 읽으면 시간이 바이트 / 상한 이상 걸려 받아들인 바이트 그대로 세어지고, 메모리에 있던 범위는 훨씬 빨리 끝나 걸린 시간만큼만 세어집니다.
    internal static long CountedDiskBytes(long accepted, double seconds, double ceilingMbps) =>
        seconds >= 0 && ceilingMbps > 0 && double.IsFinite(seconds) ? Math.Min(accepted, (long)(seconds * ceilingMbps * 1048576)) : accepted;

    /// 표본 점검이 디스크에서 읽은 양을 더합니다: 느린 (디스크에서 읽힌) 표본 수 x 표본 크기 (캐시에서 읽힌 표본은 디스크를 읽지 않았으므로 셈하지 않음).
    internal void NoteProbeRead(GameSettings game, ProbeResult? probe)
    {
        if (probe is { Slow: > 0 })
        {
            _reads.Add(game.OriginRoot, DiskReadLedger.Current, (long)probe.Slow * CacheProbe.SampleBytes, game.Name);
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
            long recovery = _reads.Used(drive, ReadPurpose.Recovery, also: ReadPurpose.RecoveryCut).Bytes;
            int samples = _reads.Used(drive, ReadPurpose.Recovery).Count;
            GameSettings? any = _games.FirstOrDefault(g => DiskReadLedger.DriveOf(g.OriginRoot) == drive);
            string cap = any is null ? string.Empty : $" of {MibText(IdleReadCap(any))} limit";
            return $"{drive} trigger reads {MibText(trigger)}, idle reads {MibText(idle)}{cap}, recovery reads {MibText(recovery)} in {samples} samples";
        });
        _log.Info("disk reads in the last 24 h: " + string.Join("; ", drives));
    }
}
