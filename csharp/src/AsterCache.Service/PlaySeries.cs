using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AsterCache.Core.Logging;
using AsterCache.Windows;

namespace AsterCache.Service;

/// 플레이의 초 단위 기록이 한 번 읽는 원시 값: 누계는 그대로 담고 (차이는 PlaySeriesBuilder 가 구함) 못 읽은 값은 null.
/// 모두 프로세스를 열지 않는 조회입니다: GetSystemTimes, NtQuerySystemInformation (SystemProcessInformation 과 메모리 목록, 시스템 성능 정보), GlobalMemoryStatusEx, 성능 카운터 (그래픽 메모리).
/// YieldApplied 는 배경 양보가 지금까지 새로 우선순위를 적용한 프로세스 수의 누계 (메모리 안의 값을 읽을 뿐 프로세스를 열지 않음).
/// Processes 는 읽는 쪽이 다음 조회 때 다시 쓰는 목록이라 받은 쪽이 바로 처리해야 합니다. GpuMib (모든 어댑터의 전용 사용량) 는 표본 루프가 이미 읽는 값 (PlaySystemSample) 을 쓰므로 실제 조회는 비워 둡니다 (같은 성능 카운터를 초마다 두 번 읽지 않게).
internal sealed record PlaySeriesRaw(long? CpuIdleTicks, long? CpuTotalTicks, IReadOnlyList<ProcessUsage> Processes, int ServicePid, long? AvailableMib, long? RepurposedBytes,
    uint? PageReads, int? GpuMib, int? GpuGameMib, string Ops, long? YieldApplied = null);

internal interface IPlaySeriesProbe : IDisposable
{
    /// gamePids: 서비스가 이미 아는 게임 프로세스 번호 (그 프로세스의 그래픽 메모리 몫을 고르는 데만 씀).
    PlaySeriesRaw Read(IReadOnlyCollection<int> gamePids);

    /// 우선순위별 대기 목록 (30 초마다 부름). 읽지 못하면 null.
    MemoryPriorityLists? ReadLists();
}

/// 게임이 설치된 디스크의 한 초 동안의 읽기·쓰기 (디스크가 여럿이면 합).
internal readonly record struct PlaySeriesDisk(ulong ReadCount, long ReadBytes, double ReadMeanMs, ulong WriteCount, long WriteBytes, double WriteMeanMs, uint Queue);

/// 한 초의 시스템 값 (원시 값의 앞뒤 차이로 구함). 첫 초처럼 앞의 값이 없으면 차이 값은 null.
/// CPU 비율은 모두 전체 CPU (모든 논리 프로세서) 에서 차지한 비율이라 게임·서비스·다른 프로세스를 더하면 시스템 전체와 맞습니다.
internal sealed record PlaySeriesPoint(double? CpuSystemPct, double? CpuGamePct, double? CpuServicePct, (string Name, double Pct)? Other1, (string Name, double Pct)? Other2,
    long? AvailableMib, double? ReusedMib, long? PageReads, int? GpuMib, int? GpuGameMib, PlaySeriesDisk? Disk, string? WriteName, long WriteBytes, string Ops,
    long? GameReadBytes = null, long? GameWorkingSetMib = null, LoadState Load = LoadState.Unknown,
    long? OtherWorkingSetMib = null, string? WsGrowName = null, long WsGrowBytes = 0, long? YieldApplied = null);

/// 원시 값 두 개의 차이로 초 단위 값을 만듭니다 (표본 루프 스레드 하나에서만 부름).
/// 게임 프로세스의 읽기 전송 바이트 증가분과 작업 집합은 같은 프로세스 목록에서 구합니다 (앞의 값이 없던 프로세스는 증가분을 셈하지 않음).
/// 프로세스는 이름으로 묶어 셉니다 (같은 이름의 프로세스가 여럿이어도 합쳐서 가장 많이 쓴 것을 가림). 번호가 다시 쓰여 CPU 시간이 줄어든 항목은 0 으로 둡니다 (shortcut: 번호가 다시 쓰이고 앞의 값보다 크면 그 한 초가 틀림).
internal sealed class PlaySeriesBuilder
{
    private PlaySeriesRaw? _previous;
    private readonly Dictionary<int, ProcessUsage> _before = [];
    private readonly Dictionary<string, long> _cpuByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _writeByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _growByName = new(StringComparer.OrdinalIgnoreCase);

    /// 앞 값을 버립니다: 값을 읽지 못한 초 (프레임 세션이 닫혔거나 조회 실패) 뒤의 첫 초가 여러 초의 누계 차이를 한 초 값으로 적지 않게 (그 초는 차이 칸이 비고 기준값만 새로 잡힘).
    public void Reset()
    {
        _previous = null;
        _before.Clear();
    }

    public PlaySeriesPoint Next(PlaySeriesRaw raw, IReadOnlyCollection<int> gamePids, IReadOnlyList<PlayDiskCounters> previousDisk, IReadOnlyList<PlayDiskCounters> currentDisk, LoadState load = LoadState.Unknown)
    {
        PlaySeriesRaw? before = _previous;
        _previous = raw;
        long totalDelta = before is { CpuTotalTicks: { } t0 } && raw.CpuTotalTicks is { } t1 ? t1 - t0 : 0;
        double? system = totalDelta > 0 && before!.CpuIdleTicks is { } i0 && raw.CpuIdleTicks is { } i1 ? Math.Clamp(100.0 * (1.0 - ((double)(i1 - i0) / totalDelta)), 0, 100) : null;
        bool hasProcesses = before is not null && _before.Count > 0 && raw.Processes.Count > 0;
        long gameTicks = 0;
        long serviceTicks = 0;
        long gameRead = 0;
        long gameWorkingSet = 0;
        long otherWorkingSet = 0;
        _cpuByName.Clear();
        _writeByName.Clear();
        _growByName.Clear();
        if (hasProcesses)
        {
            foreach (ProcessUsage p in raw.Processes)
            {
                bool known = _before.TryGetValue(p.Pid, out ProcessUsage old);
                long cpu = Math.Max(0, p.CpuTicks - old.CpuTicks);
                long write = Math.Max(0, p.WriteBytes - old.WriteBytes);
                if (p.Pid == 0)
                {
                    continue; // 시스템 유휴 프로세스: CPU 시간이 유휴 시간
                }

                if (gamePids.Contains(p.Pid))
                {
                    gameTicks += cpu;
                    gameWorkingSet += p.WorkingSetBytes;
                    gameRead += known ? Math.Max(0, p.ReadBytes - old.ReadBytes) : 0;
                }
                else if (p.Pid == raw.ServicePid)
                {
                    serviceTicks += cpu;
                }
                else if (cpu > 0)
                {
                    _cpuByName[p.Name] = _cpuByName.GetValueOrDefault(p.Name) + cpu;
                }

                if (write > 0)
                {
                    _writeByName[p.Name] = _writeByName.GetValueOrDefault(p.Name) + write;
                }

                if (!gamePids.Contains(p.Pid) && p.Pid != raw.ServicePid)
                {
                    otherWorkingSet += p.WorkingSetBytes; // 게임·서비스 밖 프로세스의 작업 집합 (앞의 값이 없던 새 프로세스는 그 값 전체가 늘어난 양)
                    long grow = p.WorkingSetBytes - (known ? old.WorkingSetBytes : 0);
                    if (grow > 0)
                    {
                        _growByName[p.Name] = _growByName.GetValueOrDefault(p.Name) + grow;
                    }
                }
            }
        }

        _before.Clear();
        foreach (ProcessUsage p in raw.Processes)
        {
            _before[p.Pid] = p;
        }

        bool cpuKnown = hasProcesses && totalDelta > 0;
        (string Name, long Value)? write1 = null;
        foreach ((string name, long value) in _writeByName)
        {
            if (write1 is not { } best || value > best.Value)
            {
                write1 = (name, value);
            }
        }

        (string Name, long Value)? grow1 = null;
        foreach ((string name, long value) in _growByName)
        {
            if (grow1 is not { } top || value > top.Value)
            {
                grow1 = (name, value);
            }
        }

        (string, double)? Pct((string Name, long Ticks)? other) => other is { } o && cpuKnown ? (o.Name, 100.0 * o.Ticks / totalDelta) : null;
        List<(string Name, long Ticks)> others = [.. _cpuByName.OrderByDescending(c => c.Value).Take(2).Select(c => (c.Key, c.Value))];
        return new PlaySeriesPoint(system, cpuKnown ? 100.0 * gameTicks / totalDelta : null, cpuKnown ? 100.0 * serviceTicks / totalDelta : null,
            Pct(others.Count > 0 ? others[0] : null), Pct(others.Count > 1 ? others[1] : null),
            raw.AvailableMib, before is { RepurposedBytes: { } r0 } && raw.RepurposedBytes is { } r1 && r1 >= r0 ? (r1 - r0) / 1_048_576d : null,
            before?.PageReads is { } p0 && raw.PageReads is { } p1 ? unchecked((uint)(p1 - p0)) : null,
            raw.GpuMib, raw.GpuGameMib, Disk(previousDisk, currentDisk), hasProcesses ? write1?.Name : null, hasProcesses ? write1?.Value ?? 0 : 0, raw.Ops,
            hasProcesses ? gameRead : null, hasProcesses ? gameWorkingSet >> 20 : null, load,
            hasProcesses ? otherWorkingSet >> 20 : null, hasProcesses ? grow1?.Name : null, hasProcesses ? grow1?.Value ?? 0 : 0,
            before?.YieldApplied is { } a0 && raw.YieldApplied is { } a1 && a1 >= a0 ? a1 - a0 : null);
    }

    /// 디스크별 앞뒤 차이의 합 (디스크 번호로 맞추고, 짝이 없거나 누계가 줄어든 디스크는 뺌). 읽기 횟수는 32비트 순환을 고려합니다. 평균 시간은 시간 합 / 횟수 (ms), 대기열은 지금 값의 합.
    public static PlaySeriesDisk? Disk(IReadOnlyList<PlayDiskCounters> previous, IReadOnlyList<PlayDiskCounters> current)
    {
        ulong reads = 0;
        ulong writes = 0;
        long readBytes = 0;
        long writeBytes = 0;
        long readTime = 0;
        long writeTime = 0;
        uint queue = 0;
        bool any = false;
        foreach (PlayDiskCounters now in current)
        {
            int index = -1;
            for (int i = 0; i < previous.Count && index < 0; i++)
            {
                index = previous[i].DiskNumber == now.DiskNumber ? i : -1;
            }

            if (index < 0)
            {
                continue;
            }

            PlayDiskCounters b = previous[index];
            if (now.ReadBytes < b.ReadBytes || now.WriteBytes < b.WriteBytes || now.ReadTime100ns < b.ReadTime100ns || now.WriteTime100ns < b.WriteTime100ns)
            {
                continue;
            }

            any = true;
            reads += unchecked((uint)(now.ReadCount - b.ReadCount));
            writes += unchecked((uint)(now.WriteCount - b.WriteCount));
            readBytes += now.ReadBytes - b.ReadBytes;
            writeBytes += now.WriteBytes - b.WriteBytes;
            readTime += now.ReadTime100ns - b.ReadTime100ns;
            writeTime += now.WriteTime100ns - b.WriteTime100ns;
            queue += now.QueueDepth;
        }

        return any ? new PlaySeriesDisk(reads, readBytes, reads == 0 ? 0 : readTime / 10_000d / reads, writes, writeBytes, writes == 0 ? 0 : writeTime / 10_000d / writes, queue) : null;
    }
}

/// 초 단위 기록 (CSV) 의 열과 줄 만들기. 값을 못 읽은 칸은 비웁니다. 숫자는 문화권과 무관하게 점을 씁니다.
internal static class PlaySeriesFormat
{
    /// 머리줄. cpu_* 는 전체 CPU 에서 차지한 비율 (%), cpu_other* 는 게임·서비스가 아닌 프로세스 가운데 그 초에 가장 많이 쓴 이름 (같은 이름은 합침).
    /// standby57_reused_mib 는 대기 목록 우선순위 5~7 에서 그 초에 재사용된 양, page_reads_n 은 시스템 전체가 디스크에서 페이지를 읽어 온 횟수의 증가분, gpu_game_mib 는 게임 프로세스들의 그래픽 전용 메모리 몫입니다.
    /// disk_* 는 게임이 설치된 물리 디스크의 그 초 값 (읽기·쓰기 횟수, 바이트, 평균 시간 ms, 대기열), write_anyvol_top_* 는 그 초에 쓰기 전송량이 가장 큰 프로세스 이름과 바이트로, 디스크·볼륨을 가리지 않는 값입니다 (네트워크·파이프 쓰기 포함).
    /// game_read_bytes 는 게임 프로세스들의 읽기 전송 바이트 증가분 (캐시 적중 포함, 디스크·볼륨을 가리지 않음), game_ws_mib 는 게임 프로세스들의 작업 집합 (MiB), load_phase 는 그 초의 로딩 판정 (loading, not_loading, 판정이 없으면 빈 칸) 입니다.
    /// other_ws_mib 는 게임·서비스 밖 프로세스의 작업 집합 합 (MiB), ws_grow_top_name·ws_grow_top_mib 는 그 초에 작업 집합이 가장 많이 늘어난 프로세스 이름 (같은 이름은 합침) 과 증가량 (MiB, 늘어난 프로세스가 없으면 빈 칸),
    /// yield_applied_n 은 배경 양보가 그 초에 새로 우선순위를 적용한 프로세스 수입니다. 모두 프로세스를 열지 않는 조회와 메모리 안의 값으로 구합니다.
    public const string Header = "t_s,frames,max_frame_ms,over50_n,over100_n,pauses_n,pause_max_ms,cpu_sys_pct,cpu_game_pct,cpu_service_pct,cpu_other1_name,cpu_other1_pct,cpu_other2_name,cpu_other2_pct,"
        + "avail_mib,standby57_reused_mib,page_reads_n,gpu_mib,gpu_game_mib,disk_read_n,disk_read_bytes,disk_read_mean_ms,disk_write_n,disk_write_bytes,disk_write_mean_ms,disk_queue,"
        + "write_anyvol_top_name,write_anyvol_top_bytes,service_ops,game_read_bytes,game_ws_mib,load_phase,other_ws_mib,ws_grow_top_name,ws_grow_top_mib,yield_applied_n";

    /// 우선순위별 대기 목록 파일의 머리줄: 여유 + 0 으로 채운 목록 MiB, 우선순위 0~7 별 대기 MiB, 우선순위 0~7 별 재사용 누계 MiB.
    public const string ListsHeader = "t_s,free_zero_mib,standby_mib_p0,standby_mib_p1,standby_mib_p2,standby_mib_p3,standby_mib_p4,standby_mib_p5,standby_mib_p6,standby_mib_p7,"
        + "reused_mib_p0,reused_mib_p1,reused_mib_p2,reused_mib_p3,reused_mib_p4,reused_mib_p5,reused_mib_p6,reused_mib_p7";

    public static string Line(long second, FrameSecond frame, PlaySeriesPoint? p)
    {
        var sb = new StringBuilder(256);
        sb.Append(second.ToString(CultureInfo.InvariantCulture)).Append(',').Append(frame.Frames.ToString(CultureInfo.InvariantCulture)).Append(',').Append(Num(frame.MaxMs, "F1"))
            .Append(',').Append(frame.Over50Ms.ToString(CultureInfo.InvariantCulture)).Append(',').Append(frame.Over100Ms.ToString(CultureInfo.InvariantCulture))
            .Append(',').Append(frame.Pauses.ToString(CultureInfo.InvariantCulture)).Append(',').Append(Num(frame.PauseMaxMs, "F1"));
        if (p is null)
        {
            sb.Append(new string(',', Header.Count(c => c == ',') - 6)); // 시스템 값이 없는 초: 나머지 칸을 모두 비움
            return sb.ToString();
        }

        sb.Append(',').Append(Num(p.CpuSystemPct, "F1")).Append(',').Append(Num(p.CpuGamePct, "F1")).Append(',').Append(Num(p.CpuServicePct, "F1"))
            .Append(',').Append(Name(p.Other1?.Name)).Append(',').Append(Num(p.Other1?.Pct, "F1")).Append(',').Append(Name(p.Other2?.Name)).Append(',').Append(Num(p.Other2?.Pct, "F1"))
            .Append(',').Append(Num(p.AvailableMib, "F0")).Append(',').Append(Num(p.ReusedMib, "F1")).Append(',').Append(Num(p.PageReads, "F0"))
            .Append(',').Append(Num(p.GpuMib, "F0")).Append(',').Append(Num(p.GpuGameMib, "F0"));
        if (p.Disk is { } d)
        {
            sb.Append(',').Append(d.ReadCount.ToString(CultureInfo.InvariantCulture)).Append(',').Append(d.ReadBytes.ToString(CultureInfo.InvariantCulture)).Append(',').Append(Num(d.ReadMeanMs, "F2"))
                .Append(',').Append(d.WriteCount.ToString(CultureInfo.InvariantCulture)).Append(',').Append(d.WriteBytes.ToString(CultureInfo.InvariantCulture)).Append(',').Append(Num(d.WriteMeanMs, "F2"))
                .Append(',').Append(d.Queue.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            sb.Append(",,,,,,,");
        }

        sb.Append(',').Append(Name(p.WriteName)).Append(',').Append(p.WriteName is null ? string.Empty : p.WriteBytes.ToString(CultureInfo.InvariantCulture)).Append(',').Append(Name(p.Ops))
            .Append(',').Append(Num(p.GameReadBytes, "F0")).Append(',').Append(Num(p.GameWorkingSetMib, "F0"))
            .Append(',').Append(p.Load switch { LoadState.Loading => "loading", LoadState.NotLoading => "not_loading", _ => string.Empty })
            .Append(',').Append(Num(p.OtherWorkingSetMib, "F0")).Append(',').Append(Name(p.WsGrowName)).Append(',').Append(p.WsGrowName is null ? string.Empty : Num(p.WsGrowBytes / 1_048_576d, "F1"))
            .Append(',').Append(Num(p.YieldApplied, "F0"));
        return sb.ToString();
    }

    public static string ListsLine(long second, MemoryPriorityLists lists)
    {
        var sb = new StringBuilder(160);
        sb.Append(second.ToString(CultureInfo.InvariantCulture)).Append(',').Append((lists.FreeBytes >> 20).ToString(CultureInfo.InvariantCulture));
        foreach (long bytes in lists.StandbyBytes.Concat(lists.RepurposedBytes))
        {
            sb.Append(',').Append((bytes >> 20).ToString(CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    private static string Num(double? value, string format) => value is { } v ? v.ToString(format, CultureInfo.InvariantCulture) : string.Empty;

    private static string Num(long? value, string format) => value is { } v ? v.ToString(format, CultureInfo.InvariantCulture) : string.Empty;

    // 이름 칸: 쉼표·따옴표·줄바꿈이 줄을 깨지 않게 밑줄로 바꿈 (프로세스 이름은 파일 이름이라 드묾)
    private static string Name(string? name) => name is null ? string.Empty : string.Concat(name.Select(c => c is ',' or '"' or '\r' or '\n' ? '_' : c));
}

/// 플레이 하나의 초 단위 기록 파일 (`play_series` 폴더, CSV 머리줄 포함) 과 우선순위별 대기 목록 파일 (`.lists.csv`).
/// 줄은 메모리에 모았다가 FlushEvery 마다 덧붙이고 (분당 한 번), 닫을 때 남은 줄을 씁니다. 파일은 첫 줄을 쓸 때 만들고 그때 게임별 최근 KeepPerGame 개만 남깁니다 (더 오래된 것은 목록 파일과 함께 지움).
/// 쓰지 못하면 한 번 알리고 그 플레이의 기록을 접습니다 (디스크 기록이 다른 일을 막지 않게). 줄을 더하는 쪽과 쓰는 쪽이 다른 스레드라 잠금이 있습니다.
internal sealed class PlaySeriesFile(string directory, string gameName, DateTimeOffset startedAt, ILog log, Func<DateTimeOffset> now, TimeSpan? flushEvery = null)
{
    public const int KeepPerGame = 8;
    public static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private readonly StringBuilder _rows = new();
    private readonly StringBuilder _lists = new();
    private readonly string _key = Key(gameName);
    private DateTimeOffset _lastFlush = now();
    private bool _created;
    private bool _failed;
    private bool _closed;

    /// 초 단위 기록 파일 이름: `<게임 키>_<시작 시각>.csv` (시작 시각은 PC 시계, 목록 파일은 `.lists.csv`).
    public string FileName => $"{_key}_{Stamp(startedAt)}.csv";

    public string ListsFileName => $"{_key}_{Stamp(startedAt)}.lists.csv";

    public void AddRow(string line) => Add(_rows, line);

    public void AddListsRow(string line) => Add(_lists, line);

    /// 마지막 쓰기 뒤 FlushEvery 가 지났으면 쌓인 줄을 씁니다.
    public void FlushIfDue()
    {
        lock (_gate)
        {
            if (!_closed && now() - _lastFlush >= (flushEvery ?? FlushEvery))
            {
                WriteLocked();
            }
        }
    }

    /// 남은 줄을 쓰고 닫습니다 (이후 더하는 줄은 버림).
    public void Close()
    {
        lock (_gate)
        {
            if (!_closed)
            {
                WriteLocked();
                _closed = true;
            }
        }
    }

    /// 게임 이름에서 파일 이름에 쓸 키 (글자·숫자·`-`·`.` 이외는 밑줄).
    public static string Key(string name) => new(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '_').ToArray());

    public static string Stamp(DateTimeOffset at) => at.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    private void Add(StringBuilder buffer, string line)
    {
        lock (_gate)
        {
            if (!_closed && !_failed)
            {
                _ = buffer.Append(line).Append('\n');
            }
        }
    }

    private void WriteLocked()
    {
        _lastFlush = now();
        if (_failed || (_rows.Length == 0 && _lists.Length == 0))
        {
            return;
        }

        try
        {
            _ = Directory.CreateDirectory(directory);
            Append(FileName, PlaySeriesFormat.Header, _rows);
            Append(ListsFileName, PlaySeriesFormat.ListsHeader, _lists);
            if (!_created)
            {
                _created = true;
                log.Info($"{gameName}: play record series: {FileName}");
                Prune();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _failed = true;
            _ = _rows.Clear();
            _ = _lists.Clear();
            log.Warn("play series not written: " + e.GetType().Name);
        }
    }

    private void Append(string fileName, string header, StringBuilder buffer)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        using var stream = new FileStream(Path.Combine(directory, fileName), FileMode.Append, FileAccess.Write, FileShare.Read);
        string text = (stream.Length == 0 ? header + "\n" : string.Empty) + buffer;
        byte[] bytes = new UTF8Encoding(false).GetBytes(text);
        stream.Write(bytes);
        _ = buffer.Clear();
    }

    // 같은 게임 키의 파일 가운데 시작 시각이 가장 늦은 KeepPerGame 개의 기록만 남깁니다 (초 단위 기록과 목록 파일을 같은 한 개로 셈). 지우지 못해도 다음 플레이에서 다시 시도합니다.
    private void Prune()
    {
        var pattern = new Regex($@"^{Regex.Escape(_key)}_(\d{{8}}-\d{{6}})(\.lists)?\.csv$", RegexOptions.CultureInvariant);
        List<(string Path, string Stamp)> files = [];
        foreach (string path in Directory.EnumerateFiles(directory, _key + "_*.csv"))
        {
            Match m = pattern.Match(Path.GetFileName(path));
            if (m.Success)
            {
                files.Add((path, m.Groups[1].Value));
            }
        }

        HashSet<string> keep = [.. files.Select(f => f.Stamp).Distinct().OrderDescending().Take(KeepPerGame)];
        foreach ((string path, string stamp) in files.Where(f => !keep.Contains(f.Stamp)))
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 지우지 못한 옛 파일은 다음 플레이에서 다시 지움
            }
        }
    }
}

/// 한 플레이의 초 단위 기록: 시스템 값을 읽고 (Read), 앞뒤 차이로 초 단위 값을 만들고 (Point), 프레임 값과 합쳐 줄을 파일에 더하고 (AddSecond), 30 초마다 우선순위별 대기 목록을 남깁니다 (SampleLists).
/// 서비스의 동작에는 쓰지 않고 파일에만 남깁니다.
internal sealed class PlaySeriesRecorder(IPlaySeriesProbe probe, PlaySeriesFile file) : IDisposable
{
    /// 우선순위별 대기 목록을 남기는 간격 (초).
    public const int ListsEverySeconds = 30;

    private readonly PlaySeriesBuilder _builder = new();
    private long _nextLists;

    public PlaySeriesRaw Read(IReadOnlyCollection<int> gamePids) => probe.Read(gamePids);

    public PlaySeriesPoint Point(PlaySeriesRaw raw, IReadOnlyCollection<int> gamePids, IReadOnlyList<PlayDiskCounters> previousDisk, IReadOnlyList<PlayDiskCounters> currentDisk, LoadState load = LoadState.Unknown) =>
        _builder.Next(raw, gamePids, previousDisk, currentDisk, load);

    public void Reset() => _builder.Reset();

    public void AddSecond(long second, FrameSecond frame, PlaySeriesPoint? point) => file.AddRow(PlaySeriesFormat.Line(second, frame, point));

    /// 시작 뒤 elapsedSeconds 초에 부르면 ListsEverySeconds 마다 한 줄 (처음 부를 때 한 줄) 남깁니다.
    public void SampleLists(double elapsedSeconds)
    {
        long second = (long)Math.Floor(elapsedSeconds);
        if (second < _nextLists)
        {
            return;
        }

        _nextLists = (second / ListsEverySeconds * ListsEverySeconds) + ListsEverySeconds;
        if (probe.ReadLists() is { } lists)
        {
            file.AddListsRow(PlaySeriesFormat.ListsLine(second, lists));
        }
    }

    public void FlushIfDue() => file.FlushIfDue();

    public void Dispose()
    {
        file.Close();
        probe.Dispose();
    }
}

/// 초 단위 기록의 시스템 값 읽기: 프로세스를 열지 않는 조회만 씁니다 (프로세스별 CPU 시간·쓰기 바이트는 SystemProcessInformation 한 번, 그래픽 메모리는 성능 카운터).
internal sealed class WindowsPlaySeriesProbe(Func<long>? yieldApplied = null) : IPlaySeriesProbe
{
    private readonly List<ProcessUsage> _processes = [];
    private readonly DiskActivity.GpuProcessMemory? _gpuProcesses = Open(DiskActivity.GpuProcessMemory.Open);

    /// yieldApplied: 배경 양보가 새로 우선순위를 적용한 프로세스 수의 누계 (없으면 그 열은 빈 칸).
    public static IPlaySeriesProbe Open(Func<long>? yieldApplied = null) => new WindowsPlaySeriesProbe(yieldApplied);

    public PlaySeriesRaw Read(IReadOnlyCollection<int> gamePids)
    {
        bool times = CpuLoad.Times(out long idle, out long total);
        _ = ProcessFaults.Snapshot(_processes); // 못 읽으면 목록이 비어 프로세스 값만 빠짐
        long available = SystemMemory.AvailableMib();
        return new PlaySeriesRaw(times ? idle : null, times ? total : null, _processes, Environment.ProcessId, available > 0 ? available : null,
            MemoryListInfo.Query()?.CacheRepurposedBytes, SystemPageReads.Query(), null, gamePids.Count == 0 ? null : _gpuProcesses?.Mib(gamePids), ServiceActivity.Take(), yieldApplied?.Invoke());
    }

    public MemoryPriorityLists? ReadLists() => MemoryListInfo.QueryPriorities();

    public void Dispose() => _gpuProcesses?.Dispose();

    private static T? Open<T>(Func<T?> open)
        where T : class
    {
        try
        {
            return open();
        }
        catch (Exception e) when (e is not (StackOverflowException or OutOfMemoryException))
        {
            return null; // 그래픽 메모리 값만 없이 나머지는 계속 남김
        }
    }
}
