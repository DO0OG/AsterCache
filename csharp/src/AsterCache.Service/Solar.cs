using AsterCache.Core.Logging;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Windows;

namespace AsterCache.Service;

/// SOLAR (설정 memory.standby_ladder): 예열한 게임 데이터를 대기 목록에서 중요한 것이 마지막에 밀려나는 순서로 두고, 밀려나기 전에 디스크 읽기 없이 다시 만져 유지합니다.
/// 예열은 중요한 것을 먼저 읽고 대기 목록은 먼저 들어온 것부터 재사용하므로, 그대로 두면 가장 중요한 데이터가 가장 먼저 밀려납니다.
/// 이 클래스는 표식 (게임별 마지막 사다리) 과 만지는 일만 맡고, 언제 어떤 게임을 만져도 되는지는 호출하는 쪽 (예열 스트림, 재예열 유지) 이 정합니다.
/// 만지기는 캐시에 없는 페이지에서는 디스크 읽기가 되므로 호출하는 쪽이 디스크 대여 안에서, 게임이 실행 중이 아닐 때만 부릅니다.
internal sealed class Solar(PrewarmEnv env, ILog log, Func<DateTimeOffset> now)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, LadderMark> _marks = new(StringComparer.OrdinalIgnoreCase); // 마지막으로 만든 사다리의 표식, 메모리에만 둠 (서비스를 다시 시작하면 없음), _lock 으로 보호
    private readonly HashSet<string> _missing = new(StringComparer.OrdinalIgnoreCase); // 사다리가 목록의 일부가 캐시에 없음을 발견한 게임 (재예열 점검이 다시 채울 때까지), 메모리에만 둠, _lock 으로 보호
    private readonly HashSet<string> _untouchable = new(StringComparer.OrdinalIgnoreCase); // 목록이 전부 스파스 파일이라 만질 것이 없었던 게임 (다음 예열 스트림 뒤 사다리가 다시 판정할 때까지 재예열 점검이 사다리를 다시 시도하지 않음), 메모리에만 둠, _lock 으로 보호

    public bool Enabled => env.StandbyLadderEnabled();

    public bool HasMark(string game)
    {
        lock (_lock)
        {
            return _marks.ContainsKey(game);
        }
    }

    /// 사다리가 목록의 일부가 캐시에 없는 것을 발견해 다시 채워야 하는 게임인가.
    public bool HasMissing(string game)
    {
        lock (_lock)
        {
            return _missing.Contains(game);
        }
    }

    /// 사다리가 만질 것이 하나도 없었던 (목록 전체가 스파스 파일인) 게임인가.
    public bool HasNothingToTouch(string game)
    {
        lock (_lock)
        {
            return _untouchable.Contains(game);
        }
    }

    /// 다시 채워야 한다는 표시를 지우고 있었는지 돌려줍니다 (재예열을 시도하는 시점에 부름: 읽기 전에 건너뛰면 호출한 쪽이 RestoreMissing 으로 되돌리고, 빈도는 호출한 쪽의 대기 조건이 정함).
    public bool TakeMissing(string game)
    {
        lock (_lock)
        {
            return _missing.Remove(game);
        }
    }

    /// 설정에서 빠진 게임의 표식과 다시 채움 표시를 버립니다.
    public void Prune(IReadOnlySet<string> live)
    {
        lock (_lock)
        {
            foreach (string gone in _marks.Keys.Where(k => !live.Contains(k)).ToList())
            {
                _ = _marks.Remove(gone);
            }

            _ = _missing.RemoveWhere(k => !live.Contains(k));
            _ = _untouchable.RemoveWhere(k => !live.Contains(k));
        }
    }

    /// 읽은 목록을 예열 순서의 역순으로 만지고 표식을 남깁니다 (예열 직후, 표식 없는 게임의 첫 사다리). 꺼져 있거나 만질 것이 없으면 null.
    public TouchEnd? Order(GameSettings game, IReadOnlyList<ReadOp> reads, CancellationToken ct)
    {
        if (!Enabled)
        {
            return null;
        }

        List<ReadOp> ladder = LadderOrder.Reverse(reads);
        if (ladder.Count == 0)
        {
            return null;
        }

        TouchResult r = env.TouchRanges(ladder, ct);
        switch (r.End)
        {
            case TouchEnd.Completed:
                if (Untouched(r))
                {
                    if (NoteNothingToTouch(game.Name))
                    {
                        log.Info($"{game.Name}: ladder has nothing to touch ({r.SkippedBytes >> 20} MiB in sparse files not touched)"); // 처음 한 번만
                    }

                    break;
                }

                SetMark(game.Name, ladder, r);
                log.Info($"{game.Name}: ladder ordered {r.Bytes >> 20} MiB in {r.Seconds:F1} s{Errors(r)}{Sparse(r)}");
                break;
            case TouchEnd.Canceled:
                Forget(game.Name); // 순서가 반쯤만 만들어짐: 표식 없이 둠
                log.Info($"{game.Name}: ladder stopped after {r.Bytes >> 20} MiB{Errors(r)}{Sparse(r)}");
                break;
            default:
                if (!NoteIncomplete(game.Name, r))
                {
                    log.Info($"{game.Name}: ladder was too slow without disk reads seen ({r.SlowPieces} slow pieces{Errors(r)}), skipped");
                    break;
                }

                log.Info($"{game.Name}: ladder found data missing from memory {Missing(r, ladder)}, left to the rewarm check{Sparse(r)}");
                break;
        }

        return r.End;
    }

    /// 표식이 있는 게임의 사다리를 다시 만듭니다 (디스크 읽기 없음). 표식이 없거나 꺼져 있으면 null.
    /// 디스크 읽기가 섞이면 표식을 지우고 다시 채움 표시를 남겨 기존 재예열 점검에 맡깁니다: 재예열은 여기서 부르지 않으며, 재예열이 끝나 사다리가 다시 만들어질 때까지 새로 고치지 않습니다.
    public TouchEnd? Refresh(GameSettings game, CancellationToken ct)
    {
        LadderMark? mark;
        lock (_lock)
        {
            _ = _marks.TryGetValue(game.Name, out mark);
        }

        if (mark is null || !Enabled)
        {
            return null;
        }

        long repurposed = SolarPolicy.Repurposed(mark, env.Lists()) ?? 0;
        TouchResult r = env.TouchRanges(mark.Ladder, ct);
        switch (r.End)
        {
            case TouchEnd.Completed:
                if (Untouched(r))
                {
                    _ = NoteNothingToTouch(game.Name); // 표식을 지워 틱마다 되풀이하지 않음
                }
                else
                {
                    SetMark(game.Name, mark.Ladder, r);
                }

                log.Info($"{game.Name}: cache refreshed in memory: {r.Bytes >> 20} MiB in {r.Seconds:F1} s ({repurposed >> 20} MiB repurposed of {mark.AheadBytes >> 20} MiB ahead){Errors(r)}{Sparse(r)}");
                break;
            case TouchEnd.Canceled:
                log.Info($"{game.Name}: cache refresh stopped after {r.Bytes >> 20} MiB{Errors(r)}{Sparse(r)}"); // 표식은 그대로 둠: 다음 틱에 다시 시도
                break;
            default:
                if (!NoteIncomplete(game.Name, r))
                {
                    log.Info($"{game.Name}: cache refresh was too slow without disk reads seen ({r.SlowPieces} slow pieces{Errors(r)}), left to the rewarm check");
                    break;
                }

                log.Info($"{game.Name}: cache refresh found data missing from memory {Missing(r, mark.Ladder)}, left to the rewarm check{Sparse(r)}");
                break;
        }

        return r.End;
    }

    /// 표식이 있고 새로 고칠 때가 된 게임 (입력 순서 그대로). 대기 목록은 표식이 하나라도 있을 때만 한 번 읽습니다.
    public IReadOnlyList<GameSettings> Due(IEnumerable<GameSettings> games, DateTimeOffset at)
    {
        List<(GameSettings Game, LadderMark Mark)> marked = [];
        lock (_lock)
        {
            foreach (GameSettings g in games)
            {
                if (_marks.TryGetValue(g.Name, out LadderMark? mark))
                {
                    marked.Add((g, mark));
                }
            }
        }

        if (marked.Count == 0)
        {
            return [];
        }

        MemoryLists? lists = env.Lists();
        return [.. marked.Where(m => SolarPolicy.RefreshDue(m.Mark, lists, at)).Select(m => m.Game)];
    }

    private void SetMark(string game, IReadOnlyList<ReadOp> ladder, TouchResult r)
    {
        MemoryLists? lists = env.Lists();
        var mark = new LadderMark(now(), lists?.CacheRepurposedBytes, lists is { } l ? SolarPolicy.Ahead(l.CacheStandbyBytes, r.Bytes) : 0, r.Bytes, ladder);
        lock (_lock)
        {
            _marks[game] = mark;
            _ = _missing.Remove(game); // 목록 전체가 메모리에 있음
            _ = _untouchable.Remove(game);
        }
    }

    // 목록이 전부 스파스 파일이라 만진 것이 없음 (건너뛴 것만 있음): 표식도 빠짐 표시도 세우지 않고 기존 표식은 지우며, 처음 기억할 때만 true.
    private bool NoteNothingToTouch(string game)
    {
        lock (_lock)
        {
            _ = _marks.Remove(game);
            _ = _missing.Remove(game);
            return _untouchable.Add(game);
        }
    }

    private static bool Untouched(TouchResult r) => r.Bytes == 0 && r.SkippedBytes > 0;

    // 표식을 지우고, 디스크 읽기를 실제로 본 경우에만 다시 채움 표시를 남깁니다 (표본이 warm 이어도 재예열 점검이 다시 읽게 함). 시간 기준이나 읽기 실패만으로 끝났다면 재예열로 고쳐지지 않으므로 표시 없이 점검의 표본 판정에 맡깁니다. 표시를 세웠으면 true.
    private bool NoteIncomplete(string game, TouchResult r)
    {
        bool diskSeen = r.DiskBytes > 0;
        lock (_lock)
        {
            _ = _marks.Remove(game);
            _ = _untouchable.Remove(game); // 디스크 읽기를 만났으면 만질 것이 있음
            if (diskSeen)
            {
                _ = _missing.Add(game);
            }
        }

        return diskSeen;
    }

    /// 재예열이 읽기 전에 건너뛰어 아무것도 채우지 못했을 때 표시를 되돌려 다음 점검에서 다시 시도하게 합니다 (그 사이 표식이 섰다면 되돌리지 않음).
    public void RestoreMissing(string game)
    {
        lock (_lock)
        {
            if (!_marks.ContainsKey(game))
            {
                _ = _missing.Add(game);
            }
        }
    }

    /// 표식을 지웁니다 (새로 고침을 더 시도하지 않음).
    public void Forget(string game)
    {
        lock (_lock)
        {
            _ = _marks.Remove(game);
            _ = _untouchable.Remove(game);
        }
    }

    // 다시 채움 로그의 꼬리: 사다리 (역순) 를 몇 MiB 만지고 멈췄는지 (만진 양이 전체에 가까우면 빠진 것은 가장 중요한 앞쪽), 느린 조각 수와 디스크에서 읽힌 양, 오류 수. N 은 건너뛴 스파스 범위를 포함한 사다리 전체 (건너뛴 양은 줄 끝 꼬리로 따로 보임).
    private static string Missing(TouchResult r, IReadOnlyList<ReadOp> ladder) => $"after {r.Bytes >> 20} of {ladder.Sum(o => o.Length) >> 20} MiB ({r.SlowPieces} slow pieces{(r.DiskBytes > 0 ? $", {r.DiskBytes >> 20} MiB read from disk" : string.Empty)}{(r.Errors > 0 ? $", {r.Errors} errors" : string.Empty)})";

    // 줄 끝에 붙이는 스파스 파일이라 만지지 않은 양 (없으면 빈 문자열)
    private static string Sparse(TouchResult r) => r.SkippedBytes > 0 ? $", {r.SkippedBytes >> 20} MiB in sparse files not touched" : string.Empty;

    // 줄 끝에 붙이는 오류 수와 디스크에서 읽힌 것으로 본 양
    private static string Errors(TouchResult r) => (r.Errors > 0 ? $", {r.Errors} errors" : string.Empty) + (r.DiskBytes > 0 ? $", {r.DiskBytes >> 20} MiB read from disk" : string.Empty);
}
