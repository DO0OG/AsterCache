using AsterCache.Core.Logging;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Windows;

namespace AsterCache.Service;

/// SOLAR (설정 memory.standby_ladder): 예열한 게임 데이터를 대기 목록에서 중요한 것이 마지막에 밀려나는 순서로 두고, 밀려나기 전에 디스크 읽기 없이 다시 만져 유지합니다.
/// 예열은 중요한 것을 먼저 읽고 대기 목록은 먼저 들어온 것부터 재사용하므로, 그대로 두면 가장 중요한 데이터가 가장 먼저 밀려납니다.
/// 이 클래스는 표식 (게임별 마지막 사다리) 과 만지는 일만 맡고, 언제 어떤 게임을 만져도 되는지는 호출하는 쪽 (예열 스트림, 재예열 유지) 이 정합니다.
/// 만지기는 캐시에 없는 페이지에서는 디스크 읽기가 되므로 호출하는 쪽이 디스크 대여 안에서 부릅니다. 게임이 실행 중이 아닐 때만 부르는 것이 원칙이고, 실행 중에는 그 게임이 방금 시작했을 때와 (시작 만지기) 실행되는 동안 일정 주기마다 (TouchFront 의 keeping) 그 게임 자신의 목록을 메모리 안에서만 만지고 캐시에 없는 곳에서 멈춥니다.
internal sealed class Solar(PrewarmEnv env, ILog log, Func<DateTimeOffset> now)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, LadderMark> _marks = new(StringComparer.OrdinalIgnoreCase); // 마지막으로 만든 사다리의 표식, 메모리에만 둠 (서비스를 다시 시작하면 없음), _lock 으로 보호
    private readonly HashSet<string> _missing = new(StringComparer.OrdinalIgnoreCase); // 사다리가 목록의 일부가 캐시에 없음을 발견한 게임 (재예열 점검이 다시 채울 때까지), 메모리에만 둠, _lock 으로 보호
    private readonly HashSet<string> _untouchable = new(StringComparer.OrdinalIgnoreCase); // 목록이 전부 스파스 파일이라 만질 것이 없었던 게임 (다음 예열 스트림 뒤 사다리가 다시 판정할 때까지 재예열 점검이 사다리를 다시 시도하지 않음), 메모리에만 둠, _lock 으로 보호
    private readonly Dictionary<string, DroppedMark> _dropped = new(StringComparer.OrdinalIgnoreCase); // 식거나 빠졌다고 보아 지운 표식의 시각·그때의 재사용 양·앞에 있는 양 (재예열 점검의 식음 로그가 한 번 쓰면 버리고, 새 표식이 서도 버림), 메모리에만 둠, _lock 으로 보호
    private readonly HashSet<string> _overlooked = new(StringComparer.OrdinalIgnoreCase); // 드라이브가 바쁠 때 표본이 식어 나온 것을 이 표식에서 이미 한 번 봐 준 게임 (표식이 사라지거나 새로 서거나 warm 표본이 나오면 비움), 메모리에만 둠, _lock 으로 보호
    private DateTimeOffset? _groupAt; // 마지막으로 묶음을 새로 고친 (표식을 같은 기준으로 다시 쓴) 시각, 메모리에만 둠, _lock 으로 보호
    private DateTimeOffset _idleTouchBlockedUntil; // 게임이 없을 때 만지는 데 쓴 시간이 벽시계의 IdleTouchShare 를 넘지 않도록 이 시각까지는 묶음 새로 고침을 하지 않음, 메모리에만 둠, _lock 으로 보호
    private bool _idleTouchDeferredNoted; // 위 상한 때문에 미룬 것을 이미 로그에 남겼는가 (상한이 풀리면 비움), _lock 으로 보호

    private readonly record struct DroppedMark(DateTimeOffset At, long? RepurposedBytes, long AheadBytes);

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
            foreach (string gone in _dropped.Keys.Where(k => !live.Contains(k)).ToList())
            {
                _ = _dropped.Remove(gone);
            }

            _ = _overlooked.RemoveWhere(k => !live.Contains(k));
        }
    }

    /// 중요도 순서 (가장 중요한 것이 앞): 순위 목록 (플레이 전체 기록의 블록 순위) 에 든 블록은 순위 순, 그 밖의 예열 블록은 읽은 순서로 그 뒤에. 순위 목록이 없으면 읽은 순서 그대로입니다.
    /// 사다리·새로 고침 (표식의 사다리)·게임 시작 만지기·실행 중 유지가 모두 이 순서의 역순 (덜 중요한 것부터) 으로 만져, 게임이 메모리를 가져가 대기 목록의 오래된 쪽부터 밀려날 때 덜 중요한 블록이 먼저 나가고 가장 중요한 블록이 가장 늦게 나가게 합니다.
    public IReadOnlyList<ReadOp> ImportanceOrder(GameSettings game, IReadOnlyList<ReadOp> reads) => env.RankedRows(game) is { } ranked ? PlayRanking.Reorder(reads, ranked) : reads;

    /// 읽은 목록을 중요도 순서의 역순으로 만지고 표식을 남깁니다 (예열 직후, 표식 없는 게임의 첫 사다리). 꺼져 있거나 만질 것이 없으면 null.
    /// made: 표식을 남겼을 때 만진 바이트를 알림 (서비스 시작 직후 여러 게임의 사다리가 Regroup 에 넘길 값).
    /// protectedFirst: null 이면 스트림 끝의 사다리가 아님 (기존 규칙). 비어 있으면 스트림 끝의 사다리인데 직전에 보호한 게임이 없음: 이전 표식이 없는 게임이 만진 전체를 다른 게임의 앞에 있는 양에서 줄임.
    /// 비어 있지 않으면 스트림 끝이고 직전에 보호로 만진 다른 게임 (만진 순서, 바이트): 이 게임과 함께 한 묶음으로 다시 표식합니다.
    /// startExe: 주면 게임 실행 파일이 시작할 때 메모리 안에서 다시 만지는 호출 (로그 문구만 다르고 표식·묶음 계산은 같음).
    public TouchEnd? Order(GameSettings game, IReadOnlyList<ReadOp> reads, CancellationToken ct, Action<long>? made = null, IReadOnlyList<(string Game, long Bytes)>? protectedFirst = null, string? startExe = null)
    {
        if (!Enabled)
        {
            return null;
        }

        List<ReadOp> ladder = LadderOrder.Reverse(ImportanceOrder(game, reads));
        if (ladder.Count == 0)
        {
            return null;
        }

        string what = startExe is null ? "ladder" : $"cache touch at game start ({startExe})"; // 로그 줄의 주어
        LadderMark? earlier = MarkOf(game.Name); // 이 줄이 빠짐을 알릴 때 붙일 값을 위해 지우기 전에 잡아 둠
        long? grown = earlier is null ? null : SolarPolicy.Repurposed(earlier, env.Lists());
        TouchResult r = env.TouchRanges(ladder, ct);
        if (startExe is null)
        {
            NoteIdleTouch(r.Seconds); // 게임 시작 때의 만지기는 상한과 무관
        }

        switch (r.End)
        {
            case TouchEnd.Completed:
                if (Untouched(r))
                {
                    if (NoteNothingToTouch(game.Name))
                    {
                        log.Info($"{game.Name}: {what} has nothing to touch ({r.SkippedBytes >> 20} MiB in sparse files not touched)"); // 처음 한 번만
                    }

                    break;
                }

                LadderMark? previous = MarkOf(game.Name); // SetMark 가 덮어쓰기 전의 표식
                SetMark(game.Name, ladder, r);
                made?.Invoke(r.Bytes);
                if (protectedFirst is { Count: > 0 })
                {
                    // 보호한 게임들은 이미 만진 만큼 다른 표식에서 줄였으므로 (alreadyReduced) 이 게임의 몫만 더 줄임. 만진 순서 그대로 한 묶음으로 A 를 실제 순서에 맞게 다시 계산
                    Regroup([.. protectedFirst.Select(p => (p.Game, p.Bytes, (LadderMark?)null)), (game.Name, r.Bytes, previous)], alreadyReduced: protectedFirst.Count, reduceWithoutPrevious: true);
                }
                else
                {
                    ReduceOthers([game.Name], [(previous, r.Bytes)], protectedFirst is not null); // 이 게임이 만진 페이지가 다른 게임의 뒤로 감
                }

                log.Info(startExe is null
                    ? $"{game.Name}: ladder ordered {r.Bytes >> 20} MiB in {r.Seconds:F1} s{Errors(r)}{Sparse(r)}"
                    : $"{game.Name}: cache touched at game start ({startExe}): {r.Bytes >> 20} MiB in {r.Seconds:F1} s (cpu {r.CpuSeconds:F1} s, {r.Maps} views, {r.Checks} checks), {r.DiskBytes >> 20} MiB read from disk{ErrorCount(r)}{Sparse(r)}");
                break;
            case TouchEnd.Canceled:
                Forget(game.Name); // 순서가 반쯤만 만들어짐: 표식 없이 둠
                log.Info($"{game.Name}: {what} stopped after {r.Bytes >> 20} MiB{Errors(r)}{Sparse(r)}");
                break;
            default:
                string since = Since(game.Name, earlier, grown);
                if (!NoteIncomplete(game.Name, r))
                {
                    log.Info($"{game.Name}: {what} was too slow without disk reads seen ({r.SlowPieces} slow pieces{Errors(r)}), skipped");
                    break;
                }

                log.Info($"{game.Name}: {what} found data missing from memory {Missing(r, ladder)}, left to the rewarm check{Sparse(r)}{since}");
                break;
        }

        return r.End;
    }

    /// 게임 실행 파일이 시작할 때 목록의 일부가 이미 메모리에 없는 게임 (사다리 순서로 만지면 덜 중요한 쪽의 빈 곳에서 먼저 멈추므로 가장 중요한 앞부분을 만지지 못함): 중요도 순서 (ImportanceOrder) 로 먼저 만져 메모리에 남은 앞부분의 끝을 찾고 (이 프로세스의 디스크 읽기 수로 경계를 찾는 판정과 디스크 읽기 한도는 Order 와 같음),
    /// 그 앞부분만 사다리 순서로 다시 만져 남은 것 가운데 가장 중요한 블록이 가장 늦게 밀려나게 합니다. 전체가 메모리에 있다는 표식은 남기지 않고 빠짐 표시를 그대로 둬 게임이 끝난 뒤 재예열 점검이 나머지를 읽게 합니다.
    /// 다른 게임의 앞에 있는 양은 앞부분이 메모리에 있던 만큼 줄입니다 (이 게임은 표식이 없어 스트림 끝의 사다리와 같이 만진 전체를 줄이는 규칙: ReduceOthers 의 withoutPreviousAll). 자기 표식은 만들지 않으므로 앞에 있는 양을 따로 셀 일이 없습니다.
    /// touch: 범위를 만드는 함수 (기본은 env.TouchRanges). keeping: 게임이 실행되는 동안 앞부분을 유지하는 호출 (호출한 쪽이 reads 를 한 번에 만질 상한까지 잘라 줌): 전부 메모리에 있어도 표식을 새로 만들지 않고 (Order 를 부르지 않음) 앞부분을 사다리 순서로 만지며, 로그 문구만 다릅니다. noneInMemory: 취소가 아니라 메모리에 하나도 남아 있지 않아 멈췄을 때 부름 (유지가 그 회차에서 끝나게).
    /// walk: 주면 (keeping 일 때만) 앞부분을 한 번에 찾는 대신 이 함수로 목록 끝까지 빠진 조각을 건너뛰며 만져 (합친 결과, 메모리에 있던 범위, 로그 꼬리) 를 받음: 앞부분 하나의 경계에서 끝나지 않게 합니다.
    public TouchEnd? TouchFront(GameSettings game, IReadOnlyList<ReadOp> reads, CancellationToken ct, string startExe, Func<IReadOnlyList<ReadOp>, CancellationToken, TouchResult>? touch = null, bool keeping = false, Action? noneInMemory = null, int skippedBefore = 0,
        Func<IReadOnlyList<ReadOp>, CancellationToken, (TouchResult Find, IReadOnlyList<ReadOp> Kept, string Note)>? walk = null)
    {
        if (!Enabled || reads.Count == 0)
        {
            return null;
        }

        if (!keeping)
        {
            reads = ImportanceOrder(game, reads); // 게임 시작 때의 호출은 읽은 순서의 목록을 넘기므로 여기서 중요도 순서로 (유지는 이미 그 순서의 목록을 넘김: 두 번 정렬하지 않음)
        }

        touch ??= env.TouchRanges;
        string what = keeping ? $"cache keeping while playing ({startExe})" : $"cache touch at game start ({startExe})";
        IReadOnlyList<ReadOp>? walked = null;
        string walkNote = string.Empty;
        TouchResult find;
        if (keeping && walk is not null)
        {
            (find, walked, walkNote) = walk(reads, ct);
        }
        else
        {
            find = touch(reads, ct);
        }

        if (find.End == TouchEnd.Completed && !keeping)
        {
            return Order(game, reads, ct, startExe: startExe); // 전부 메모리에 있음 (빠짐 표시가 낡았거나 앞의 판정이 잡음으로 멈춤): 처음부터 사다리 한 번
        }

        long total = reads.Sum(o => o.Length);
        long inMemory = Math.Max(0, find.Bytes - find.DiskBytes);
        if (find.End == TouchEnd.Canceled || inMemory <= 0)
        {
            if (find.End != TouchEnd.Canceled)
            {
                noneInMemory?.Invoke();
            }

            log.Info(find.End == TouchEnd.Canceled
                ? $"{game.Name}: {what} stopped after {find.Bytes >> 20} MiB{Errors(find)}{Sparse(find)}"
                : $"{game.Name}: {what} found none of the {total >> 20} MiB list in memory ({find.SlowPieces} slow pieces, {find.DiskBytes >> 20} MiB read from disk{ErrorCount(find)}), left to the rewarm check{ProbeText(find, reads, total, ct)}");
            return find.End;
        }

        // 멈춘 곳까지가 앞부분 (건너뛴 스파스 범위의 길이는 목록 위치에 들어감). 멈춘 조각 근처에서 디스크로 읽힌 만큼도 이제 메모리에 있으므로 함께 만짐
        TouchResult r = touch(LadderOrder.Reverse(walked ?? PrefetchStreamer.Head(reads, find.Bytes + find.SkippedBytes)), ct);
        ReduceOthers([game.Name], [(null, inMemory)], withoutPreviousAll: true);
        string kept = $"{inMemory >> 20} MiB of {total >> 20} MiB still in memory{Stopped(find)}, in {find.Seconds + r.Seconds:F1} s (cpu {find.CpuSeconds + r.CpuSeconds:F1} s, {find.Maps + r.Maps} views, {find.Checks + r.Checks} checks), {(find.DiskBytes + r.DiskBytes) >> 20} MiB read from disk ({find.HardFaults + r.HardFaults} hard faults){ErrorCount(find)}{Sparse(find)}{(skippedBefore > 0 ? $", {skippedBefore} rounds skipped before" : string.Empty)}{walkNote}{(r.End == TouchEnd.Canceled ? string.Empty : ProbeText(find, reads, total, ct))}";
        log.Info(r.End == TouchEnd.Canceled
            ? $"{game.Name}: {what} stopped after {r.Bytes >> 20} MiB{Errors(r)}{Sparse(r)}"
            : keeping ? $"{game.Name}: cache kept while playing ({startExe}): {kept}" : $"{game.Name}: cache touched at game start ({startExe}): {kept}, left to the rewarm check");
        return r.End;
    }

    /// 표식이 있는 게임의 사다리를 다시 만듭니다 (디스크 읽기 없음). 표식이 없거나 꺼져 있으면 null.
    /// 디스크 읽기가 섞이면 표식을 지우고 다시 채움 표시를 남겨 기존 재예열 점검에 맡깁니다: 재예열은 여기서 부르지 않으며, 재예열이 끝나 사다리가 다시 만들어질 때까지 새로 고치지 않습니다.
    /// touched: 끝까지 만졌을 때 이전 표식과 만진 바이트를 알림 (묶음 새로 고침이 Regroup 에 넘길 값).
    public TouchEnd? Refresh(GameSettings game, CancellationToken ct, Action<LadderMark, long>? touched = null)
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

        long? grown = SolarPolicy.Repurposed(mark, env.Lists());
        long repurposed = grown ?? 0;
        TouchResult r = env.TouchRanges(mark.Ladder, ct);
        NoteIdleTouch(r.Seconds);
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
                    touched?.Invoke(mark, r.Bytes);
                }

                log.Info($"{game.Name}: cache refreshed in memory: {r.Bytes >> 20} MiB in {r.Seconds:F1} s ({repurposed >> 20} MiB repurposed of {mark.AheadBytes >> 20} MiB ahead){Errors(r)}{Sparse(r)}");
                break;
            case TouchEnd.Canceled:
                log.Info($"{game.Name}: cache refresh stopped after {r.Bytes >> 20} MiB{Errors(r)}{Sparse(r)}"); // 표식은 그대로 둠: 다음 틱에 다시 시도
                break;
            default:
                string since = Since(game.Name, mark, grown);
                if (!NoteIncomplete(game.Name, r))
                {
                    log.Info($"{game.Name}: cache refresh was too slow without disk reads seen ({r.SlowPieces} slow pieces{Errors(r)}), left to the rewarm check");
                    break;
                }

                log.Info($"{game.Name}: cache refresh found data missing from memory {Missing(r, mark.Ladder)}, left to the rewarm check{Sparse(r)}{since}");
                break;
        }

        return r.End;
    }

    /// 표식이 있는 게임 (입력 순서 그대로).
    public IReadOnlyList<GameSettings> Marked(IEnumerable<GameSettings> games) => [.. MarkedPairs(games).Select(m => m.Game)];

    /// 묶음 새로 고침 대상: 표식이 있는 게임 전체 (입력 순서 그대로). 그중 지금 만질 수 있는 (usable, 없으면 모두) 게임 하나라도 새로 고칠 때가 됐고 직전 묶음 새로 고침 뒤 GroupEvery 가 지났을 때만, 아니면 빈 목록.
    /// 게이트에 막힌 게임은 때가 됐어도 시작 판정에 쓰지 않습니다: 계속 막힌 게임 하나 때문에 다른 게임이 되풀이해 만져지지 않게. 대기 목록은 표식이 하나라도 있을 때만 한 번 읽습니다.
    public IReadOnlyList<GameSettings> DueGroup(IEnumerable<GameSettings> games, DateTimeOffset at, Func<GameSettings, bool>? usable = null, bool capped = true)
    {
        List<(GameSettings Game, LadderMark Mark)> marked = MarkedPairs(games);
        DateTimeOffset? last;
        lock (_lock)
        {
            last = _groupAt;
        }

        if (marked.Count == 0 || (last is { } groupAt && at - groupAt < SolarPolicy.GroupEvery))
        {
            return [];
        }

        MemoryLists? lists = env.Lists();
        if (capped && IdleTouchBlocked(at, out DateTimeOffset until)) // 스트림 도중의 보호는 상한에 막히지 않음 (capped: false)
        {
            // 상한 때문에 미룸: 때가 된 게임이 있어도 만지지 않고 (게이트 판정도 건너뜀), 미루기 시작할 때만 한 줄 남김
            if (marked.Any(m => SolarPolicy.RefreshDue(m.Mark, lists, at)) && NoteIdleTouchDeferred())
            {
                log.Info($"cache refresh deferred: touching time is capped at {SolarPolicy.IdleTouchShare * 100:F0}% of wall time, next in {Math.Max(0, (long)(until - at).TotalSeconds)} s");
            }

            return [];
        }

        return marked.Any(m => SolarPolicy.RefreshDue(m.Mark, lists, at) && (usable?.Invoke(m.Game) ?? true)) ? [.. marked.Select(m => m.Game)] : [];
    }

    /// 표식이 있는 게임 (입력 순서 그대로) 중 어느 하나라도 앞으로 readBytes 를 더 읽으면 새로 고칠 때가 되는 경우에만 그 전체를, 아니면 빈 목록: 작은 재예열이 다른 게임의 캐시를 밀어낼 수 없을 때 스트림 앞의 보호 (와 같은 스트림 끝의 보호) 를 건너뛰는 데 씁니다.
    /// 묶음은 순서를 지켜야 하므로 하나라도 닿으면 전체를 돌려줍니다 (DueGroup 과 같음). GroupEvery·상한은 보지 않음: 이 보호는 스트림 한 번에 한 번 필요한 순서 맞추기입니다.
    public IReadOnlyList<GameSettings> MarkedAtRisk(IEnumerable<GameSettings> games, long readBytes, DateTimeOffset at)
    {
        List<(GameSettings Game, LadderMark Mark)> marked = MarkedPairs(games);
        if (marked.Count == 0)
        {
            return [];
        }

        MemoryLists? lists = env.Lists();
        return marked.Any(m => SolarPolicy.ReadReaches(m.Mark, lists, readBytes, at)) ? [.. marked.Select(m => m.Game)] : [];
    }

    /// 게임이 없을 때 만지는 데 seconds 초를 썼음을 알립니다 (게임 시작·실행 중의 만지기는 알리지 않음): 다음 묶음 새로 고침은 그 시간의 1/IdleTouchShare 뒤로 미룹니다. 상한 안에서 막지 않고 쓴 만지기 (스트림 앞·뒤의 보호, 사다리) 도 이 계산에는 들어갑니다. 스트림이 읽는 묶음을 곧바로 다시 만드는 시간은 읽는 일의 일부라 넣지 않습니다 (넣으면 묶음마다 50 배씩 쌓여 도중의 보호가 영영 막힘).
    public void NoteIdleTouch(double seconds)
    {
        if (seconds <= 0)
        {
            return;
        }

        DateTimeOffset end = now();
        lock (_lock)
        {
            _idleTouchBlockedUntil = (end > _idleTouchBlockedUntil ? end : _idleTouchBlockedUntil) + SolarPolicy.IdleTouchWait(seconds);
        }
    }

    private bool IdleTouchBlocked(DateTimeOffset at, out DateTimeOffset until)
    {
        lock (_lock)
        {
            until = _idleTouchBlockedUntil;
            if (at >= until)
            {
                _idleTouchDeferredNoted = false;
                return false;
            }

            return true;
        }
    }

    // 상한 때문에 미룬 것을 처음 알릴 때만 true.
    private bool NoteIdleTouchDeferred()
    {
        lock (_lock)
        {
            bool first = !_idleTouchDeferredNoted;
            _idleTouchDeferredNoted = true;
            return first;
        }
    }

    /// 묶음을 만진 순서대로 (touched: 게임, 만진 바이트, 만지기 전의 표식, 가장 오래된 것이 먼저) 표식을 같은 기준 시점 (재사용 누계·대기 바이트) 으로 다시 쓰고,
    /// 이번에 만지지 않은 표식은 앞에 있는 양을 SolarPolicy.AheadAfterOthersTouched 로 줄입니다. 대기 목록을 못 읽으면 표식은 그대로 두고 (시간 기준) 줄이기만 합니다.
    /// aheadLess: 진행 중인 스트림이 이미 읽은 양 중 끝의 사다리가 줄여 주지 않을 몫 (스트림 도중의 보호), 캐시 대기 바이트에서 빼고 계산합니다.
    /// alreadyReduced: touched 의 앞 몇 개는 이미 다른 표식에서 줄였음 (스트림 끝의 묶음: 보호가 먼저 줄임). reduceWithoutPrevious: SolarPolicy.AheadAfterOthersTouched 의 withoutPreviousAll.
    public void Regroup(IReadOnlyList<(string Game, long Bytes, LadderMark? Previous)> touched, long aheadLess = 0, int alreadyReduced = 0, bool reduceWithoutPrevious = false)
    {
        if (touched.Count == 0)
        {
            return;
        }

        MemoryLists? lists = env.Lists();
        long[] ahead = lists is { } l ? SolarPolicy.GroupAhead(SolarPolicy.Ahead(l.CacheStandbyBytes, aheadLess), [.. touched.Select(t => t.Bytes)]) : [];
        DateTimeOffset at = now();
        lock (_lock)
        {
            for (int i = 0; lists is { } current && i < touched.Count; i++)
            {
                if (_marks.TryGetValue(touched[i].Game, out LadderMark? mark))
                {
                    _marks[touched[i].Game] = new LadderMark(at.AddTicks(i), current.CacheRepurposedBytes, ahead[i], touched[i].Bytes, mark.Ladder); // 만진 순서대로 시각을 늘려 (가장 오래된 게임이 가장 이름) 같은 묶음 안에서도 AheadAfterOthersTouched 가 앞뒤를 가림
                }
            }

            ReduceOthersLocked([.. touched.Select(t => t.Game)], [.. touched.Skip(alreadyReduced).Select(t => (t.Previous, t.Bytes))], reduceWithoutPrevious);
            _groupAt = at;
        }
    }

    private List<(GameSettings Game, LadderMark Mark)> MarkedPairs(IEnumerable<GameSettings> games)
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

        return marked;
    }

    private LadderMark? MarkOf(string game)
    {
        lock (_lock)
        {
            return _marks.GetValueOrDefault(game);
        }
    }

    // except 가 아닌 표식의 A 를 줄입니다 (touched: 그 게임들이 만지기 전의 표식과 만진 바이트).
    private void ReduceOthers(IEnumerable<string> except, IReadOnlyList<(LadderMark? Previous, long Bytes)> touched, bool withoutPreviousAll = false)
    {
        lock (_lock)
        {
            ReduceOthersLocked(except, touched, withoutPreviousAll);
        }
    }

    private void ReduceOthersLocked(IEnumerable<string> except, IReadOnlyList<(LadderMark? Previous, long Bytes)> touched, bool withoutPreviousAll = false)
    {
        HashSet<string> skip = new(except, StringComparer.OrdinalIgnoreCase);
        foreach ((string game, LadderMark mark) in _marks.Where(m => !skip.Contains(m.Key)).Select(m => (m.Key, m.Value)).ToList())
        {
            _marks[game] = mark with { AheadBytes = SolarPolicy.AheadAfterOthersTouched(mark, touched.Select(t => (t.Previous, t.Bytes)), withoutPreviousAll) };
        }
    }

    private void SetMark(string game, IReadOnlyList<ReadOp> ladder, TouchResult r)
    {
        MemoryLists? lists = env.Lists();
        var mark = new LadderMark(now(), lists?.CacheRepurposedBytes, lists is { } l ? SolarPolicy.Ahead(l.CacheStandbyBytes, r.Bytes) : 0, r.Bytes, ladder);
        lock (_lock)
        {
            _marks[game] = mark;
            _ = _dropped.Remove(game);
            _ = _overlooked.Remove(game);
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
            _ = _overlooked.Remove(game);
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
            _ = _overlooked.Remove(game);
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

    /// 드라이브가 바쁠 때 표본이 식어 나온 것을 이 표식에서 처음 봐 주는가 (표식이 있고 아직 안 봐 줬으면 기억하고 true). 둘째부터는 false: 바쁘지 않을 때와 같게 표식을 지워 재예열 점검에 맡김 (바쁜 상태가 이어져도 표본을 되풀이해 읽지 않음).
    public bool OverlookBusyCold(string game)
    {
        lock (_lock)
        {
            return _marks.ContainsKey(game) && _overlooked.Add(game);
        }
    }

    /// warm 표본이 나왔으므로 봐 준 기억을 지웁니다.
    public void ClearOverlooked(string game)
    {
        lock (_lock)
        {
            _ = _overlooked.Remove(game);
        }
    }

    /// 표식이 있는 게임이 직전에 만든 사다리가 만진 바이트 (표식이 없으면 0): 스트림 도중의 보호가 스트림의 끝 사다리와 겹쳐 줄이지 않게 하는 데 씀.
    public long PriorTouchedBytes(string game) => MarkOf(game)?.TouchedBytes ?? 0;

    /// 표본이 식어서 표식을 지웁니다: 지우기 전의 값으로 만든 로그 꼬리 (표식이 없었으면 빈 문자열) 를 돌려주고, 뒤의 재예열 점검 로그가 쓰도록 기억합니다.
    public string ForgetCold(string game)
    {
        LadderMark? mark = MarkOf(game);
        string since = Since(game, mark, mark is null ? null : SolarPolicy.Repurposed(mark, env.Lists()));
        Forget(game);
        return since;
    }

    /// 표식이 있던 게임이 식었다고 볼 때 로그 줄 끝에 붙일 말 (표식도 지우기 전에 기억한 값도 없으면 빈 문자열).
    public string SinceLadder(string game)
    {
        LadderMark? mark = MarkOf(game);
        if (mark is not null)
        {
            return SolarPolicy.SinceLadder(mark.At, now(), SolarPolicy.Repurposed(mark, env.Lists()), mark.AheadBytes);
        }

        lock (_lock)
        {
            return _dropped.Remove(game, out DroppedMark dropped) ? SolarPolicy.SinceLadder(dropped.At, now(), dropped.RepurposedBytes, dropped.AheadBytes) : string.Empty;
        }
    }

    // 지우려는 표식의 로그 꼬리를 만들고 그 값을 기억합니다 (표식이 없었으면 빈 문자열).
    private string Since(string game, LadderMark? mark, long? repurposed)
    {
        if (mark is null)
        {
            return string.Empty;
        }

        lock (_lock)
        {
            _dropped[game] = new DroppedMark(mark.At, repurposed, mark.AheadBytes);
        }

        return SolarPolicy.SinceLadder(mark.At, now(), repurposed, mark.AheadBytes);
    }

    /// 표식을 지웁니다 (새로 고침을 더 시도하지 않음).
    public void Forget(string game)
    {
        lock (_lock)
        {
            _ = _marks.Remove(game);
            _ = _overlooked.Remove(game);
            _ = _untouchable.Remove(game);
        }
    }

    // 다시 채움 로그의 꼬리: 사다리 (역순) 를 몇 MiB 만지고 멈췄는지 (만진 양이 전체에 가까우면 빠진 것은 가장 중요한 앞쪽), 느린 조각 수와 디스크에서 읽힌 양, 오류 수. N 은 건너뛴 스파스 범위를 포함한 사다리 전체 (건너뛴 양은 줄 끝 꼬리로 따로 보임).
    private static string Missing(TouchResult r, IReadOnlyList<ReadOp> ladder) => $"after {r.Bytes >> 20} of {ladder.Sum(o => o.Length) >> 20} MiB ({r.SlowPieces} slow pieces{(r.DiskBytes > 0 ? $", {r.DiskBytes >> 20} MiB read from disk" : string.Empty)}{(r.HardFaults > 0 ? $", {r.HardFaults} hard faults" : string.Empty)}{(r.Reason == StopReason.SlowTime ? ", stopped by slow time" : string.Empty)}{ErrorCount(r)})";

    // 경계에서 멈춘 만지기의 로그 꼬리: 디스크 읽기 수가 허용치를 넘었는지 (경계), 아니면 안전망 (느린 조각의 시간 합) 에 걸렸는지
    // 오류 수와 종류별 서술 (열기 실패와 Win32 오류 번호, 파일 끝 밖, 매핑 실패, 읽기 실패, 가장 많이 실패한 파일의 경로): 종류를 모르면 (시험의 가짜 만지기) 수만.
    private static string ErrorCount(TouchResult r) => r.Errors > 0 ? $", {r.Errors} errors{(r.ErrorKinds?.Describe() is { Length: > 0 } detail ? $" ({detail})" : string.Empty)}" : string.Empty;

    // 경계를 찾은 만지기 (find) 에서만 표본 확인을 하고 로그 꼬리 (앞에 쉼표) 를 돌려줍니다: 기록만 하고 판정·순서에는 쓰지 않으며, 실패해도 빈 문자열입니다.
    private string ProbeText(TouchResult find, IReadOnlyList<ReadOp> reads, long total, CancellationToken ct)
    {
        if (find.End != TouchEnd.NeedsDiskReads)
        {
            return string.Empty;
        }

        try
        {
            IReadOnlyList<(ReadOp Op, long Offset)> picks = KeepProbe.Pick(reads);
            return picks.Count > 0 && env.KeepProbeSamples([.. picks.Select(p => p.Op)], ct) is { } faults ? ", " + KeepProbe.Describe(picks, faults, total) : string.Empty;
        }
        catch (Exception e) when (e is not (StackOverflowException or OutOfMemoryException))
        {
            return string.Empty;
        }
    }

    private static string Stopped(TouchResult r) => r.End != TouchEnd.NeedsDiskReads ? string.Empty : r.Reason == StopReason.SlowTime ? " (stopped by slow time)" : " (boundary found by hard faults)";

    // 줄 끝에 붙이는 스파스 파일이라 만지지 않은 양 (MiB 로 0 이면 빈 문자열)
    private static string Sparse(TouchResult r) => r.SkippedBytes >> 20 > 0 ? $", {r.SkippedBytes >> 20} MiB in sparse files not touched" : string.Empty;

    // 줄 끝에 붙이는 오류 수와 디스크에서 읽힌 것으로 본 양
    private static string Errors(TouchResult r) => ErrorCount(r) + (r.DiskBytes > 0 ? $", {r.DiskBytes >> 20} MiB read from disk" : string.Empty);
}
