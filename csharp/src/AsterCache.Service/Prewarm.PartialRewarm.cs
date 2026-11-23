using System.Globalization;
using AsterCache.Core.Json;
using AsterCache.Core.Settings;

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
    }

    private readonly Dictionary<string, PartialRewarmLog> _partialRewarms = new(StringComparer.OrdinalIgnoreCase); // 게임별 다시 읽은 시각과 쉬는 시각, _gate 로 보호

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

            return log.Times.Count >= MaxPartialRewarmsPerDay ? (log.Times.Order().ElementAt(log.Times.Count - MaxPartialRewarmsPerDay) + PartialRewarmSpan, false) : null;
        }
    }

    /// 식은 앞부분을 또 읽으려는데 마지막으로 다시 읽은 지 within 안이면, 그 읽기로부터 24시간 쉽니다 (읽은 것이 곧 밀려나는 상태에서 같은 읽기를 되풀이하지 않게). 쉬기 시작했으면 풀리는 시각.
    internal DateTimeOffset? NotePartialColdAgain(string game, TimeSpan within)
    {
        DateTimeOffset now = _now();
        DateTimeOffset until;
        lock (_gate)
        {
            if (FreshPartialRewarmLocked(game, now) is not { } log || log.Times.Count == 0 || now - log.Times.Max() > within)
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
        PrewarmResult result = RewarmGame(front, ct, roomMib is { } room ? Math.Min(room, windowMib) : windowMib);
        if (!result.Ran && result.Stats is not { Bytes: > 0 })
        {
            return result; // 읽지 않고 건너뜀: 상태는 그대로
        }

        long actualMib = result.Stats is { Bytes: > 0 } stats ? ActualMib(stats, windowMib) : 0;
        DateTimeOffset now = _now();
        (long WarmedMib, long HotMib) current;
        bool memoryPartial;
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
            NotePartialRewarmReadLocked(game.Name, now);
        }

        PatchPartialStatus(game.Name, current, memoryPartial);
        return result;
    }
}
