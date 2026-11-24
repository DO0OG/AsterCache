using System.Globalization;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Settings;
using AsterCache.Prefetch;

namespace AsterCache.Service;

/// Prewarmer 의 식은 앞부분 다시 읽기 (재예열 점검이 보충 대기 게임의 읽은 앞부분이 식은 것을 찾았을 때): 처음 읽은 것과 같은 범위를 읽고,
/// 하루 횟수 상한과 "다시 읽은 것이 곧 또 식으면 쉬는" 규칙을 적용합니다. 횟수와 쉬는 시각은 서비스를 다시 시작해도 이어지게 상태 파일의 그 게임 항목에 함께 남깁니다.
public sealed partial class Prewarmer
{
    // 플레이 기록은 사흘에 다섯 번(하루 한두 번)이라 그보다 많이 다시 채워도 다른 플레이 앞에 쓰일 수 없고, 하루 재예열 요청량이 게임 전체로 약 9~13 GiB 였던 데 견줘
    // 6번이면 (게임 전체로 약 96 GiB) 7~10배라 2번으로 묶습니다. 이 PC 는 하루 7~11번 서비스를 다시 시작하므로 메모리에만 두면 상한이 풀립니다.
    internal const int MaxPartialRewarmsPerDay = 2;

    internal static readonly TimeSpan PartialRewarmSpan = TimeSpan.FromHours(24);

    private sealed class PartialRewarmLog
    {
        public readonly List<DateTimeOffset> Times = [];
        public DateTimeOffset? RestUntil;

        /// 마지막 시도가 다른 게임·이 게임의 실행으로 끊겼고 읽은 위치를 남겼음 (_rewarmResume): 이어 읽는 시도는 하루 횟수에 새로 세지 않고, 곧 또 식었다는 판정도 하지 않음.
        public bool Open;
    }

    private readonly Dictionary<string, PartialRewarmLog> _partialRewarms = new(StringComparer.OrdinalIgnoreCase); // 게임별 다시 읽은 시각과 쉬는 시각, _gate 로 보호
    private readonly Dictionary<string, (long WindowMib, long Bytes)> _rewarmResume = new(StringComparer.OrdinalIgnoreCase); // 끊긴 식은 앞부분 다시 읽기가 그 창의 스케줄 앞쪽에서 이미 읽은 바이트: 다음 다시 읽기가 이어 읽는 위치, 상태 파일에도 남김, _gate 로 보호

    // 24시간 지난 기록을 지웁니다. 시계가 뒤로 가서 지금보다 뒤인 시각은 지금으로 당겨 상한이 풀리지 않게 합니다. _gate 안에서 부릅니다.
    private PartialRewarmLog? FreshPartialRewarmLocked(string game, DateTimeOffset now)
    {
        if (!_partialRewarms.TryGetValue(game, out PartialRewarmLog? log))
        {
            return null;
        }

        for (int i = 0; i < log.Times.Count; i++)
        {
            log.Times[i] = log.Times[i] > now ? now : log.Times[i];
        }

        _ = log.Times.RemoveAll(t => now - t >= PartialRewarmSpan);
        if (log.RestUntil is { } rest)
        {
            log.RestUntil = rest <= now ? null : rest > now + PartialRewarmSpan ? now + PartialRewarmSpan : rest;
        }

        if (log.Times.Count == 0 && log.RestUntil is null)
        {
            _ = _partialRewarms.Remove(game);
            _ = _rewarmResume.Remove(game); // 하루가 지난 끊긴 읽기의 위치는 낡음
            return null;
        }

        return log;
    }

    /// 지금 이 게임의 식은 앞부분 다시 읽기를 쉬어야 하면 (풀리는 시각, 곧 다시 식어서 쉬는 중인가), 아니면 null. 디스크를 읽지 않습니다.
    internal (DateTimeOffset Until, bool Resting)? PartialRewarmPause(string game)
    {
        DateTimeOffset now = _now();
        lock (_gate)
        {
            if (FreshPartialRewarmLocked(game, now) is not { } log)
            {
                return null;
            }

            if (log.RestUntil is { } rest)
            {
                return (rest, true);
            }

            return log.Times.Count >= MaxPartialRewarmsPerDay && !log.Open ? (log.Times.Order().ElementAt(log.Times.Count - MaxPartialRewarmsPerDay) + PartialRewarmSpan, false) : null;
        }
    }

    /// 식은 앞부분을 또 읽으려는데 마지막으로 다시 읽은 지 within 안이면, 그 읽기로부터 24시간 쉽니다 (읽은 것이 곧 밀려나는 상태에서 같은 읽기를 되풀이하지 않게). 쉬기 시작했으면 풀리는 시각.
    internal DateTimeOffset? NotePartialColdAgain(string game, TimeSpan within)
    {
        DateTimeOffset now = _now();
        DateTimeOffset until;
        lock (_gate)
        {
            if (FreshPartialRewarmLocked(game, now) is not { } log || log.Times.Count == 0 || log.Open || now - log.Times.Max() > within) // 끊긴 읽기는 읽다 만 것이라 곧 식었다고 볼 수 없음
            {
                return null;
            }

            until = log.Times.Max() + PartialRewarmSpan;
            log.RestUntil = until;
        }

        lock (_run)
        {
            bool changed = false;
            lock (_gate)
            {
                if (_status.TryGetValue(game, out object? entry) && entry is JObject o)
                {
                    WritePartialRewarmFields(game, o, now);
                    changed = true;
                }
            }

            if (changed)
            {
                FlushStatus(); // 쉬기 시작할 때만 한 번 더 씀 (읽을 때는 PatchPartialStatus 가 함께 씀)
            }
        }

        return until;
    }

    // 식은 앞부분을 실제로 다시 읽은 때 부릅니다 (_gate 안에서).
    private void NotePartialRewarmReadLocked(string game, DateTimeOffset now)
    {
        if (!_partialRewarms.TryGetValue(game, out PartialRewarmLog? log))
        {
            _partialRewarms[game] = log = new PartialRewarmLog();
        }

        log.Times.Add(now);
    }

    // 상태 파일 항목에 횟수와 쉬는 시각을 함께 씁니다 (_gate 안에서 부름, 기록이 없으면 키를 지움).
    private void WritePartialRewarmFields(string game, JObject entry, DateTimeOffset now)
    {
        PartialRewarmLog? log = FreshPartialRewarmLocked(game, now);
        if (log is { Times.Count: > 0 })
        {
            entry["partial_rewarm_at"] = string.Join(';', log.Times.Select(t => t.ToString("O", CultureInfo.InvariantCulture)));
        }
        else
        {
            entry.Remove("partial_rewarm_at");
        }

        if (log?.RestUntil is { } rest)
        {
            entry["partial_rewarm_rest_until"] = rest.ToString("O", CultureInfo.InvariantCulture);
        }
        else
        {
            entry.Remove("partial_rewarm_rest_until");
        }

        if (log is { Open: true } && _rewarmResume.TryGetValue(game, out (long WindowMib, long Bytes) resume))
        {
            entry["partial_rewarm_resume"] = $"{resume.WindowMib};{resume.Bytes}";
        }
        else
        {
            entry.Remove("partial_rewarm_resume");
        }
    }

    // 상태 파일에서 횟수와 쉬는 시각을 되살립니다 (LoadStatus 가 부름).
    private void LoadPartialRewarm(string game, JObject entry)
    {
        PartialRewarmLog log = new();
        if (entry.TryGetValue("partial_rewarm_at", out object? times) && times is string text)
        {
            foreach (string part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                if (DateTimeOffset.TryParse(part, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset at))
                {
                    log.Times.Add(at);
                }
            }
        }

        if (entry.TryGetValue("partial_rewarm_rest_until", out object? rest) && rest is string restText
            && DateTimeOffset.TryParse(restText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset until))
        {
            log.RestUntil = until;
        }

        if (entry.TryGetValue("partial_rewarm_resume", out object? resume) && resume is string resumeText && resumeText.Split(';') is [var window, var bytes]
            && long.TryParse(window, CultureInfo.InvariantCulture, out long windowMib) && long.TryParse(bytes, CultureInfo.InvariantCulture, out long readBytes) && windowMib > 0 && readBytes > 0 && log.Times.Count > 0)
        {
            log.Open = true;
            _rewarmResume[game] = (windowMib, readBytes);
        }

        if (log.Times.Count > 0 || log.RestUntil is not null)
        {
            _partialRewarms[game] = log;
        }
    }

    // 식은 앞부분 다시 읽기를 읽을 수 있는 상태인가: 보충이 남아 있고, 처음 읽은 범위를 같은 방식으로 다시 만들 수 있거나 (같은 창, 같은 스파스 표시) 메모리 부분 예열이라 전체 재예열에 맡길 수 있음.
    // 범위를 모르는 상태 (메모리로 멈추거나 한도로 마무리해 창이 없음, 옛 형식의 상태 파일) 의 시작 상한 예열은 읽지 않고 보충에 맡김 (이 변경 전과 같음).
    internal bool PartialRewarmReadable(string game)
    {
        lock (_gate)
        {
            return _partial.ContainsKey(game) && (!_rangeUnknown.Contains(game) || _memoryPartial.ContainsKey(game));
        }
    }

    // 끊긴 식은 앞부분 다시 읽기를 이어 읽을 위치: 기억한 창이 지금 창과 같고 (목록이 같아야 앞 바이트를 건너뛸 수 있음) 그 앞부분 표본이 메모리에 있을 때만 (ResumePoint, 보충과 같은 확인), 아니면 처음부터 (창, 0).
    // refused: 디스크 대여가 거절·취소돼 표본을 읽지 못함 (기억은 그대로 둠).
    private (long WindowMib, long SkipBytes) RewarmResumePoint(GameSettings game, long windowMib, CancellationToken ct, out bool refused)
    {
        refused = false;
        lock (_gate)
        {
            if (!_rewarmResume.TryGetValue(game.Name, out (long WindowMib, long Bytes) remembered))
            {
                return (windowMib, 0);
            }

            if (remembered.WindowMib != windowMib)
            {
                _ = _rewarmResume.Remove(game.Name);
                return (windowMib, 0);
            }
        }

        return ResumePoint(game, windowMib, ct, out refused, _rewarmResume);
    }

    // 다시 읽기 한 번을 하루 횟수와 이어 읽을 위치에 반영합니다 (_gate 안에서). 끝까지 읽었으면 끊긴 시도를 이어 끝낸 것이면 새로 세지 않고, 아니면 한 번으로 셉니다.
    // 끊겼으면 (Ran 아님) 끝까지 읽은 위치가 믿을 만할 때 (오류·틈 메우기·순서 바꿈 없음) 그 위치를 남겨 다음 시도가 이어 읽게 하고 (이어 읽는 시도는 새로 세지 않음) 그 위치를 돌려줍니다. 위치를 믿을 수 없으면 null.
    // 하루 읽기 양은 스트림이 실제로 읽은 만큼만 이미 셈 (NoteStreamRead).
    private long? NoteRewarmAttemptLocked(GameSettings game, long windowMib, long skipBytes, PrewarmResult result, DateTimeOffset now)
    {
        bool open = _partialRewarms.TryGetValue(game.Name, out PartialRewarmLog? log) && log.Open;
        long? position = null;
        if (!result.Ran)
        {
            StreamStats stats = result.Stats!;
            long at = skipBytes + Math.Max(0, stats.Bytes - stats.GapBytes);
            if (stats.Errors == 0 && stats.GapBytes == 0 && at > 0 && at < MibBytes(windowMib) && !_streamer.WasPrewarmReordered(game))
            {
                position = at;
            }
        }

        if (position is { } kept)
        {
            _rewarmResume[game.Name] = (windowMib, kept);
        }
        else
        {
            _ = _rewarmResume.Remove(game.Name);
        }

        if (open && result.Ran)
        {
            _partialRewarms[game.Name].Open = false; // 끊긴 시도를 이어 끝냄: 그 시도가 이미 하루 횟수에 한 번으로 세어졌음
            return null;
        }

        if (!open)
        {
            NotePartialRewarmReadLocked(game.Name, now);
        }

        _partialRewarms[game.Name].Open = position is not null;
        return position;
    }

    // 읽은 시각을 남기고 상태 파일에 씁니다 (RewarmPartialGame 이 부분 상태를 따로 못 쓰는 경로).
    private void PersistPartialRewarm(string game)
    {
        lock (_run)
        {
            bool changed = false;
            lock (_gate)
            {
                if (_status.TryGetValue(game, out object? entry) && entry is JObject o)
                {
                    WritePartialRewarmFields(game, o, _now());
                    changed = true;
                }
            }

            if (changed)
            {
                FlushStatus();
            }
        }
    }

    // 보충이 남은 상태의 재예열: 처음 읽은 것과 같은 범위 (같은 창 크기, 같은 스파스 제외) 만 다시 읽고 보충 상태를 유지합니다.
    // 목록은 창을 자른 뒤 스파스·캐시 없이 읽는 파일을 빼서 만들어지므로, 실제로 읽은 양이 아니라 처음 목록을 만든 창을 씁니다.
    // 읽은 범위를 다시 만들 수 없으면 (_rangeUnknown) 메모리 부분 예열은 전체 재예열에 맡기고 (이 변경 전과 같음) 그 밖에는 읽지 않습니다.
    internal PrewarmResult RewarmPartialGame(GameSettings game, CancellationToken ct, long? roomMib)
    {
        (long WarmedMib, long HotMib) partial;
        bool hasPartial;
        bool wasMemoryPartial;
        bool unknown;
        long windowMib;
        bool hadWindow;
        long storedWindow;
        lock (_gate)
        {
            hasPartial = _partial.TryGetValue(game.Name, out partial);
            wasMemoryPartial = _memoryPartial.ContainsKey(game.Name);
            unknown = _rangeUnknown.Contains(game.Name);
            hadWindow = _partialWindowMib.TryGetValue(game.Name, out storedWindow);
            windowMib = Math.Max(partial.WarmedMib, hadWindow ? storedWindow : 0);
        }

        if (!hasPartial)
        {
            return RewarmGame(game, ct, roomMib);
        }

        if (unknown)
        {
            if (!wasMemoryPartial)
            {
                return new PrewarmResult(game.Name, false, "partial range unknown", null, _now());
            }

            PrewarmResult whole = RewarmGame(game, ct, roomMib);
            if (whole.Ran || whole.Stats is { Bytes: > 0 })
            {
                lock (_gate)
                {
                    NotePartialRewarmReadLocked(game.Name, _now());
                }

                PersistPartialRewarm(game.Name);
            }

            return whole;
        }

        // 스파스 표시는 사본에 실어 넘기고 (예열이 이미 스파스를 뺀 사본의 표시를 지우지 않음) 공유 표시는 건드리지 않음
        GameSettings front = WithSparseMark(game) with { MaxStreamMib = (int)Math.Min(windowMib, int.MaxValue), MaxStreamSet = true };
        (long resumeWindowMib, long skipBytes) = RewarmResumePoint(game, windowMib, ct, out bool refused);
        if (refused)
        {
            return new PrewarmResult(game.Name, false, "disk read lease refused", null, _now()); // 끊긴 위치는 그대로 두고 다음에 이어 읽음
        }

        if (skipBytes > 0)
        {
            _log.Info($"{game.Name}: rewarm of the part read into memory continues after {skipBytes >> 20} MiB of {resumeWindowMib} MiB, which a cancelled read had already put in memory");
        }

        PrewarmResult result = RewarmGame(front, ct, roomMib is { } room ? Math.Min(room, windowMib) : windowMib, skipBytes);
        if (!result.Ran && result.Stats is not { Bytes: > 0 })
        {
            return result; // 읽지 않고 건너뜀: 상태는 그대로
        }

        long actualMib = result.Stats is { Bytes: > 0 } stats ? ActualMib(stats, windowMib, skipBytes) : 0;
        DateTimeOffset now = _now();
        (long WarmedMib, long HotMib) current;
        bool memoryPartial;
        long? cutShort;
        lock (_gate)
        {
            if (!_partial.TryGetValue(game.Name, out current))
            {
                // 예열이 끝난 일반 예열로 보고 부분 상태를 지움 (창 크기가 등록 상한과 같을 때): 다시 읽기 전 상태로 되돌림
                current = partial;
                _partial[game.Name] = current;
                if (hadWindow)
                {
                    _partialWindowMib[game.Name] = storedWindow; // 기록이 지운 창 크기도 되살림
                }

                if (wasMemoryPartial)
                {
                    _memoryPartial[game.Name] = current;
                }
            }
            else if (current == partial)
            {
                // 메모리 때문에 줄여 읽은 것이 아니면 읽은 양은 줄지 않음 (같은 범위를 다시 읽었으므로)
                long warmedMib = Math.Max(partial.WarmedMib, actualMib);
                if (warmedMib < partial.HotMib)
                {
                    current = (warmedMib, partial.HotMib);
                    _partial[game.Name] = current;
                    if (wasMemoryPartial)
                    {
                        _memoryPartial[game.Name] = current;
                    }
                }
            }
            else
            {
                // 다시 읽기 자체가 메모리로 줄어 예열이 부분 상태를 새로 정함: 전체 크기가 창 크기로 줄지 않게 처음 값을 유지
                current = (current.WarmedMib, partial.HotMib);
                _partial[game.Name] = current;
                if (_memoryPartial.ContainsKey(game.Name))
                {
                    _memoryPartial[game.Name] = current;
                }
            }

            memoryPartial = wasMemoryPartial || _memoryPartial.ContainsKey(game.Name);
            cutShort = NoteRewarmAttemptLocked(game, windowMib, skipBytes, result, now);
        }

        if (!result.Ran)
        {
            _log.Info(cutShort is { } position
                ? $"{game.Name}: rewarm of the part read into memory was cut short after {position >> 20} MiB of {windowMib} MiB ({result.CancelDetail}), the next read continues from there and counts as the same attempt"
                : $"{game.Name}: rewarm of the part read into memory was cut short ({result.CancelDetail}), the position is not reliable so the next read starts over");
        }

        PatchPartialStatus(game.Name, current, memoryPartial);
        return result;
    }
}
