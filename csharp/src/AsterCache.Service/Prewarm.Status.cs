using System.Globalization;
using AsterCache.Core;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Settings;

namespace AsterCache.Service;

/// Prewarmer 의 상태 파일 (적재·기록·저장), 예열 결과 기록, 건너뜀 로그, 마지막 예열 시각 조회.
public sealed partial class Prewarmer
{
    // 이전 실행의 상태 파일: 항목은 그대로 이어 쓰고, 부팅 뒤에 성공한 예열만 _last 로 되살려 서비스만 다시 시작했을 때 같은 게임을 또 읽지 않게 합니다 (부팅 전 것은 파일 캐시가 비었으므로 제외).
    private void LoadStatus()
    {
        if (_statusPath is null || !File.Exists(_statusPath))
        {
            return;
        }

        try
        {
            if (JsonRead.ReadFile(_statusPath) is not JObject root || !root.TryGetValue("games", out object? games) || games is not JObject all)
            {
                return;
            }

            DateTimeOffset boot = _now() - TimeSpan.FromMilliseconds(_env.UptimeMs());
            foreach ((string name, object? value) in all)
            {
                if (value is not JObject o)
                {
                    continue;
                }

                _status[name] = o;
                if (o.TryGetValue("profile_miss_key", out object? miss) && miss is string missKey)
                {
                    _profileMiss[name] = missKey; // 프로필 미일치 판정은 설치 폴더 mtime 이 같은 동안 다시 열거하지 않음 (재시작 뒤에도 이어짐)
                }

                DateTimeOffset? okAt = LastOkAt(o);
                if (okAt is { } earlier && earlier <= boot)
                {
                    _lastBeforeBoot[name] = earlier; // 부팅 전 예열: _last 로는 되살리지 않음 (캐시가 비었음), 새 트레이스로 다시 치지 않도록 LastWarmed 만 돌려줌
                }

                if (okAt is { } when && when > boot)
                {
                    _last[name] = when;
                    if (o.TryGetValue("warmed_mib", out object? warmed) && warmed is long w && o.TryGetValue("hot_mib", out object? hot) && hot is long h && w < h)
                    {
                        _partial[name] = (w, h); // 부분 예열 뒤 서비스만 다시 시작해도 보충이 이어짐
                        if (o.TryGetValue("partial", out object? memoryFlag) && memoryFlag is true && w > 0 && w <= int.MaxValue)
                        {
                            _memoryPartial[name] = (w, h); // 메모리가 모자라 앞부분만 읽은 상태도 이어짐 (표본 제한과 재읽기)
                        }
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
        {
            _log.Warn("prewarm status file not read: " + e.GetType().Name);
        }
    }

    // 마지막 성공 시각: last_ok_at 은 마지막 시도가 건너뜀·실패여도 남습니다 (재시작 뒤 같은 트레이스를 새 트레이스로 다시 치지 않게). 옛 파일은 result ok 의 at.
    private static DateTimeOffset? LastOkAt(JObject o)
    {
        string? text = null;
        if (o.TryGetValue("last_ok_at", out object? kept))
        {
            text = kept as string;
        }
        else if (o.TryGetValue("result", out object? result) && result as string == "ok" && o.TryGetValue("at", out object? at))
        {
            text = at as string;
        }

        return text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset when) ? when : null;
    }

    // 이번 시도가 성공이 아니면 이전 항목의 마지막 성공 시각을 그대로 이어 씁니다.
    private string? KeptOkAt(string game)
    {
        lock (_gate)
        {
            return _status.TryGetValue(game, out object? prior) && prior is JObject po && LastOkAt(po) is { } ok ? ok.ToString("O") : null;
        }
    }

    /// 이 게임을 마지막으로 예열한 시각, 예열한 적이 없으면 null. 부팅 전에 한 예열은 원시·boot 트레이스가 있는 게임에만 돌려줍니다 (재시작 뒤 같은 트레이스를 새 트레이스로 다시 치지 않게).
    public DateTimeOffset? LastWarmed(string game)
    {
        DateTimeOffset? before;
        lock (_gate)
        {
            if (_last.TryGetValue(game, out DateTimeOffset at))
            {
                return at;
            }

            before = _lastBeforeBoot.TryGetValue(game, out DateTimeOffset prior) ? prior : null;
        }

        return before is { } t && _games.FirstOrDefault(g => string.Equals(g.Name, game, StringComparison.OrdinalIgnoreCase)) is { } gs && _env.RawTraceExists(gs) ? t : null;
    }

    /// 게임 시작 시 이미 메모리에 있는 마지막 예열 상태를 돌려줍니다. 데이터 파일이나 게임 디스크를 새로 읽지 않습니다.
    internal PlayWarmSnapshot? PlayStartSnapshot(string game, DateTimeOffset startedAt)
    {
        lock (_gate)
        {
            if (!_status.TryGetValue(game, out object? status) || status is not JObject entry)
            {
                return null;
            }

            string result = StatusString(entry, "result") ?? "unknown";
            string? reason = StatusString(entry, "reason");
            long? bytes = StatusLong(entry, "bytes");
            long? readMib = StatusLong(entry, "warmed_mib") ?? (bytes is { } read ? read >> 20 : null);
            long? targetMib = StatusLong(entry, "hot_mib");
            if (targetMib is null && result == "ok")
            {
                targetMib = readMib;
            }

            DateTimeOffset? completedAt = StatusString(entry, "at") is { } at
                && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed) ? parsed : null;
            double? secondsAgo = completedAt is { } then && startedAt >= then ? (startedAt - then).TotalSeconds : null;
            return new PlayWarmSnapshot(StatusString(entry, "mode") ?? "hot set", result, reason, readMib, targetMib, secondsAgo);
        }
    }

    private static string? StatusString(JObject entry, string key) =>
        entry.TryGetValue(key, out object? value) ? value as string : null;

    private static long? StatusLong(JObject entry, string key) =>
        entry.TryGetValue(key, out object? value) ? value switch { long number => number, int number => number, _ => null } : null;

    // 상태 파일의 그 게임 항목에 warmed_mib/hot_mib 를 남깁니다 (항목이 없으면 아무것도 안 함).
    private void PatchStatus(string game, long warmedMib, long hotMib)
    {
        lock (_run)
        {
            bool changed = false;
            lock (_gate)
            {
                if (_status.TryGetValue(game, out object? entry) && entry is JObject o)
                {
                    o["warmed_mib"] = warmedMib;
                    o["hot_mib"] = hotMib;
                    o["partial"] = false; // 나머지까지 읽음
                    changed = true;
                }
            }

            if (changed)
            {
                FlushStatus();
            }
        }
    }

    /// 같은 게임·같은 사유의 "prewarm skipped" 를 남기는 최소 간격 (트리거 프로그램이 연달아 떠도 줄이 쌓이지 않게).
    public static readonly TimeSpan SkipLogEvery = TimeSpan.FromSeconds(60);

    // 간격 안에 같은 줄이 또 나오면 남기지 않고 세어 두었다가, 다음에 남기는 줄에 "(suppressed N)" 로 붙입니다.
    private void LogSkipped(string game, string reason, string line) => LogSkipped(game, reason, () => line);

    // line 은 실제로 남길 때만 만듭니다 (억제되는 틱에는 프로세스 스냅샷 같은 비싼 일을 하지 않음).
    private void LogSkipped(string game, string reason, Func<string> line)
    {
        int suppressed = 0;
        lock (_gate)
        {
            string key = SkipLogKey(game, reason);
            DateTimeOffset now = _now();
            if (_skipLogged.TryGetValue(key, out (DateTimeOffset At, int Count) last))
            {
                if (now - last.At < SkipLogEvery)
                {
                    _skipLogged[key] = last with { Count = last.Count + 1 };
                    return;
                }

                suppressed = last.Count;
            }

            _skipLogged[key] = (now, 0);
        }

        string text = line();
        _log.Info(suppressed > 0 ? $"{text} (suppressed {suppressed})" : text);
    }

    internal static string SkipLogKey(string game, string reason) => game + "|" + StableSkipReason(reason);

    private static string StableSkipReason(string reason)
    {
        if (reason.StartsWith("supplement cancelled ", StringComparison.Ordinal))
        {
            return "supplement-cancelled|" + StableSkipReason(reason[21..]);
        }

        if (reason.StartsWith("prewarm cancelled ", StringComparison.Ordinal))
        {
            return "prewarm-cancelled|" + StableSkipReason(reason[18..]);
        }

        if (reason.StartsWith("head cancelled ", StringComparison.Ordinal))
        {
            return "head-cancelled|" + StableSkipReason(reason[15..]);
        }

        if (reason.StartsWith("supplement low memory", StringComparison.Ordinal))
        {
            return "supplement-low-memory";
        }

        if (reason.StartsWith("low memory", StringComparison.Ordinal))
        {
            return "low-memory";
        }

        if (reason.StartsWith("memory reserve:", StringComparison.Ordinal))
        {
            return "memory-reserve";
        }

        if (reason.StartsWith("memory budget:", StringComparison.Ordinal))
        {
            return "memory-budget";
        }

        return reason.StartsWith("cooldown (", StringComparison.Ordinal) ? "cooldown" : reason;
    }

    private void LogSupplementNothingNew(string game, long capMib, long warmedMib, long hotMib)
    {
        var state = (Reason: "supplement nothing new", WarmedMib: warmedMib, HotMib: hotMib); // cap 은 가용 메모리에 따라 계속 흔들리므로 키에 넣지 않음
        lock (_gate)
        {
            if (_supplementNothingNew.TryGetValue(game, out var previous) && previous == state)
            {
                return;
            }

            _supplementNothingNew[game] = state;
        }

        _log.Info($"{game}: supplement prewarm skipped (the {capMib} MiB that fits is already read: {warmedMib} of {hotMib} MiB)");
    }

    private PrewarmResult Record(GameSettings game, DateTimeOffset now, PrewarmResult r, double mbps)
    {
        TrackPendingWarm(game, r, now);
        string? okAt = r.Ran ? now.ToString("O") : KeptOkAt(game.Name);
        JObject entry = new()
        {
            { "at", now.ToString("O") },
            { "result", r.Ran ? "ok" : r.SkipReason!.StartsWith("failed", StringComparison.Ordinal) ? "failed" : "skipped" },
            { "reason", r.SkipReason },
            { "bytes", r.Stats?.Bytes },
            { "hint_accepted_bytes", r.Stats?.HintAcceptedBytes },
            { "seconds", r.Stats?.Seconds },
            { "hint_mb_per_s", r.Stats is null ? null : Math.Round(mbps, 1) },
            { "last_ok_at", okAt },
        };
        lock (_gate)
        {
            if (_profileMiss.TryGetValue(game.Name, out string? miss))
            {
                entry["profile_miss_key"] = miss;
            }
        }
        if (r.Ran && r.Mode is null && _streamer.LoadHeadBytes(game) is { } head)
        {
            entry["head_mib"] = head >> 20; // 로딩 앞부분 (관리 창 상태 탭)
        }

        if (r.Mode is not null)
        {
            entry["mode"] = r.Mode;
        }

        lock (_gate)
        {
            if (r.Ran && r.Mode is null && _startupTrim.TryGetValue(game.Name, out (long WarmedMib, long HotMib) trim) && game.MaxStreamMib == trim.WarmedMib)
            {
                entry["warmed_mib"] = ActualMib(r.Stats, trim.WarmedMib); // 서비스 시작 상한으로 앞부분만 읽음 (실제 읽은 양) (보충 예열이 끝나면 둘이 같아짐)
                entry["hot_mib"] = trim.HotMib;
            }
        }

        lock (_gate)
        {
            if (r.Mode is null && _partial.TryGetValue(game.Name, out (long WarmedMib, long HotMib) part))
            {
                // 부분 예열 상태는 건너뜀·취소·실패 기록이 덮어쓰지 않음 (재시작 뒤에도 보충·표본 제한이 이어지게). 지우는 것은 완전 성공이나 보충 성공뿐.
                bool memory = _memoryPartial.TryGetValue(game.Name, out (long WarmedMib, long HotMib) mem);
                if (memory)
                {
                    (part, entry["partial"]) = (mem, true); // 메모리가 모자라 앞부분만 읽음
                }

                if (!r.Ran || memory)
                {
                    entry["warmed_mib"] = part.WarmedMib;
                    entry["hot_mib"] = part.HotMib;
                }
            }

            _status[game.Name] = entry;
        }

        FlushStatus();
        return r;
    }

    /// 메모리가 모자라 앞부분만 읽은 게임이면 그 앞부분만 읽도록 줄인 사본 (재예열 점검의 표본을 읽은 구간에서만 고르게), 아니면 그대로.
    internal GameSettings WarmedPart(GameSettings g)
    {
        lock (_gate)
        {
            return _memoryPartial.TryGetValue(g.Name, out (long WarmedMib, long HotMib) mem) && _partial.ContainsKey(g.Name) ? g with { MaxStreamMib = (int)mem.WarmedMib, MaxStreamSet = true } : g;
        }
    }

    /// 부분 예열 중이거나 부분 예열로 끝난 게임의 (읽은 양, 전체) MiB, 아니면 null (상태 표시용).
    internal (long WarmedMib, long HotMib)? PartialInfo(string game)
    {
        lock (_gate)
        {
            if (_memoryRunning.TryGetValue(game, out (long WarmedMib, long HotMib) running))
            {
                return running;
            }

            return _memoryPartial.TryGetValue(game, out (long WarmedMib, long HotMib) mem) && _partial.ContainsKey(game) ? mem : null;
        }
    }

    internal bool MemoryPartialPending(string game)
    {
        lock (_gate)
        {
            return _memoryPartial.ContainsKey(game) && _partial.ContainsKey(game);
        }
    }

    private bool HasPartialState(string game)
    {
        lock (_gate)
        {
            return _partial.ContainsKey(game) || _memoryPartial.ContainsKey(game) || _resume.ContainsKey(game);
        }
    }

    private readonly object _flushGate = new(); // 예열 락 밖 (읽지 않는 트리거 건너뜀) 에서도 기록하므로 임시 파일 쓰기를 직렬화

    private void FlushStatus()
    {
        if (_statusPath is not null)
        {
            try
            {
                string tmp = _statusPath + ".tmp";
                lock (_flushGate)
                {
                    string contents;
                    lock (_gate)
                    {
                        contents = PyJson.Dumps(new JObject { { "games", _status } }, indent: 2, ensureAscii: false) + "\n";
                    }

                    DurableFile.WriteAllBytes(tmp, PyJson.Utf8.GetBytes(contents));
                    File.Move(tmp, _statusPath, true);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log.Warn("prewarm status file not written: " + e.GetType().Name);
            }
        }
    }
}
