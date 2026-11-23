using System.ComponentModel;
using AsterCache.Core;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Settings;
using AsterCache.Prefetch;

namespace AsterCache.Service;

/// Prewarmer 의 메모리 창·캐시 예산 배정: 게임별 몫, 부분 예열 상한, 메모리 부족으로 멈춘 예열의 재시도 판단.
public sealed partial class Prewarmer
{
    public static readonly TimeSpan ProgressEvery = TimeSpan.FromSeconds(10);

    internal const int GapFillReserveMib = 4096;

    /// 메모리가 모자라 예열을 줄여 읽을 때 이보다 작게 줄어들면 읽지 않고 건너뜁니다 (MiB).
    internal const int MinPartialMib = 256;

    // 최종 예산 재축소 뒤에도 짧은 메모리 변동으로 같은 전체 예열을 반복하지 않게 남길 여유 (MiB).
    internal const int FinalShrinkHeadroomMib = 256;

    // 메모리 사유로 멈춘 게임을 다시 읽기 전 최소 간격: 메모리가 계속 빠듯한 동안 같은 시도를 되풀이하지 않음.
    internal static readonly TimeSpan MemoryRetryFloor = TimeSpan.FromMinutes(2);

    // 부분 예열 창을 잡을 때 쓰는 가용 RAM (MiB): 처음 잡을 때도 계획 직후 재확인과 같은 여유를 빼서, 몇십 MiB 변동에 읽기가 통째 취소되지 않게 함.
    internal static long WindowAvailableMib(long availableRamMib) => Math.Max(0, availableRamMib - FinalShrinkHeadroomMib);

    // 재시도 성격의 예열 (대기 재시도, 재예열 점검): 메모리로 멈춘 직후 MemoryRetryFloor 안에는 다시 읽지 않음. 트리거·서비스 시작은 해당 없음.
    private static bool IsRetryReason(string why) => why is "pending warm retry" or "rewarm";

    // 실제로 읽은 양 (스트림 통계, MiB 올림) 을 설정한 상한 limit 으로 자른 값. 통계가 없으면 limit, 0 바이트면 0 (호출 쪽이 성공으로 확정하지 않음).
    // skipped: 이어 읽을 때 건너뛴 (이미 읽은) 바이트.
    internal static long ActualMib(StreamStats? stats, long limit, long skipped = 0) => stats is null ? limit : Math.Min(limit, (stats.Bytes + skipped + (1 << 20) - 1) >> 20);

    // 예열 예산 (MiB, 게임들이 나눠 씀): prewarm.max_cache_mib 가 있으면 (게임마다 적혀 있으면 가장 큰 값) 그 값을 물리 RAM 으로 자른 것, 없으면 물리 RAM / 2. 물리 RAM 을 모르면 제한 없음.
    // 존 최대 크기 (max_zone_mib auto) 의 자동값 (ZoneSize.AutoMaxMib) 과는 별개입니다.
    internal static long CacheCapMib(long physicalRamBytes, IEnumerable<GameSettings> games)
    {
        long physMib = physicalRamBytes > 0 ? physicalRamBytes >> 20 : long.MaxValue;
        long set = games.Select(g => (long)g.Prewarm.MaxCacheMib).DefaultIfEmpty(0).Max();
        return set > 0 ? Math.Min(set, physMib) : physMib == long.MaxValue ? physMib : physMib / 2;
    }

    // 게임이 실행 뒤 잡는 메모리 (몫, MiB) 와 그 값을 낸 플레이 수를 돌려줌 (서비스가 플레이 기록을 보고 정함). 기본 null = 기록 없음: 예열 상한은 기존 규칙 그대로.
    public Func<string, (long Mib, int Plays)?>? Footprint { get; set; }

    private (long Mib, int Plays)? FootprintOf(GameSettings game) => Footprint?.Invoke(game.Name);

    // 예열 상한의 예비 (MiB): 몫을 알면 max(min_free_ram_mib, 몫 + 1024), 모르면 기존 max(min_free_ram_mib, 4096).
    internal static long ReserveMib(GameSettings game, long? footprintMib) =>
        footprintMib is { } f ? GameFootprint.ReserveMib(game.Prewarm.MinFreeRamMib, f) : Math.Max(game.Prewarm.MinFreeRamMib, GapFillReserveMib);

    // 몫을 반영한 상한이 내려갈 수 있는 하한: 몫을 모를 때 상한의 절반. 로드 초반에 읽는 앞부분은 밀려나기 전에 쓰이고, 잘못 배운 몫 (다른 프로그램이 메모리를 잡은 플레이 등) 으로 인한 감소를 절반으로 제한하며,
    // 상한이 0 이 되어 대기 재시도가 끝없이 도는 일을 막습니다. oldCap: 몫을 모를 때의 상한, newCap: 몫을 반영한 상한 (oldCap 이하).
    internal static long FootprintCap(long oldCap, long newCap) => Math.Min(oldCap, Math.Max(newCap, oldCap / 2));

    // 가용 RAM 에서 예비를 뺀 상한 (MiB) 과 그것을 다른 상한 others 와 합친 값: 몫을 알면 하한 (FootprintCap) 안에서 몫 예비를 씀.
    internal static long AvailableCap(GameSettings game, long availableMib, long others, long? footprintMib)
    {
        long old = Math.Min(others, availableMib - ReserveMib(game, null));
        return footprintMib is null ? old : FootprintCap(old, Math.Min(others, availableMib - ReserveMib(game, footprintMib)));
    }

    private static readonly TimeSpan PhysCapKeep = TimeSpan.FromSeconds(10);
    private (DateTimeOffset At, string Game, long Mib)? _physCapAt; // 몫을 반영한 예산의 짧은 캐시: 가용 RAM 의 잔 변동으로 한 점검 안에서 예산이 흔들리지 않게, _gate 로 보호

    // 예열 예산 (MiB): 물리 RAM 기준 예산과, 곧 플레이할 게임 (예산 순위 1순위) 이 실행 뒤 남길 캐시 (지금 가용 - 그 게임의 예비) 중 작은 쪽, 단 몫을 모를 때의 절반 아래로는 내려가지 않음.
    // 모든 게임의 예열 합이 이 선을 넘으면 그 게임이 뜰 때 몫이 넘친 예열을 밀어내므로 디스크 시간만 쓰고 남아야 할 블록까지 밀려납니다.
    // 몫을 아는 게임이 없거나 어떤 게임이든 실행 중이면 (그 게임의 몫은 이미 가용에서 빠졌으므로 두 번 빼지 않음) 기존 예산 그대로이고, 이때는 프로세스 스냅샷도 정렬도 하지 않습니다.
    private long PhysCapMib()
    {
        long cap = CacheCapMib(_env.PhysicalRamBytes(), _games);
        if (Footprint is null || !_games.Any(g => FootprintOf(g) is not null))
        {
            return cap;
        }

        Func<GameSettings, bool> running = _env.GameRunningSnapshot?.Invoke() ?? _env.GameRunning;
        if (_games.Any(running) || FirstPlayPriority() is not { } next || FootprintOf(next) is not { } fp)
        {
            return cap;
        }

        DateTimeOffset now = _now();
        lock (_gate)
        {
            if (_physCapAt is { } cached && cached.Game == next.Name && now >= cached.At && now - cached.At < PhysCapKeep)
            {
                return Math.Min(cap, cached.Mib);
            }
        }

        long mib = FootprintCap(cap, Math.Min(cap, WindowAvailableMib(_env.AvailableRamMib()) - ReserveMib(next, fp.Mib)));
        lock (_gate)
        {
            _physCapAt = (now, next.Name, mib);
        }

        return mib;
    }

    // 전체가 들어가지 않을 때 읽을 앞부분의 크기 (MiB): min(원래 읽을 양, 가용 RAM - 예비, 예열 예산 physCapMib). 너무 작으면 null (건너뜀). 앞쪽 = 계획 순서 = 게임이 먼저 읽는 순서.
    // roomMib: 묶음에서 남은 예산 (MiB, 단독 예열이면 제한 없음).
    // footprintMib: 게임이 실행 뒤 잡는 메모리 (몫, MiB), 알면 고정 예비 대신 max(min_free_ram_mib, 몫 + 1024) 를 뺌.
    internal static long? PartialCapMib(GameSettings game, long hotMib, long availableRamMib, long physCapMib, long roomMib = long.MaxValue, long? footprintMib = null)
    {
        long cap = AvailableCap(game, availableRamMib, Math.Min(Math.Min(hotMib, roomMib), physCapMib), footprintMib);
        return cap >= MinPartialMib && cap < hotMib && cap <= int.MaxValue ? cap : null;
    }

    // 묶음에 남은 예산 (MiB) = 예열 예산 - 이미 올린 양. 이미 올린 양이 있으면 가용 RAM - 여유 - 이미 올린 양도 넘지 않음 (첫 게임의 가용 RAM 쪽 판단은 Run 의 부분 예열이 함).
    // RunBatch (전체 예열) 와 WarmHeads (앞부분 예열) 가 같은 식을 씁니다.
    internal static long BatchRoomMib(long physCapMib, long availableRamMib, int minFreeRamMib, long heldMib)
    {
        long room = physCapMib - heldMib;
        return heldMib > 0 ? Math.Min(room, availableRamMib - minFreeRamMib - heldMib) : room;
    }

    // 창 없이 전체를 읽던 예열이 메모리 검사로 멈췄을 때 (호출 쪽이 확인), 그 시점 가용 메모리에 들어가는 앞부분 (창) 의 크기 (MiB). 호출 쪽이 한 회에 한 번만 부름.
    // 읽기 전 관문 (게임 시작 등) 이 막혔거나 256 MiB 미만만 들어가면 null: 같은 디스크에 새 읽기를 내지 않고 기존 취소 경로를 따름.
    // fullMib: 전체 읽기 목록 크기 (모르면 0 → null), roomMib: 묶음·배정 상한 (PartialCapMib 의 roomMib 와 같음).
    private long? MemoryFallbackCapMib(GameSettings game, long fullMib, long roomMib, CancellationToken ct) =>
        fullMib <= 0 || ct.IsCancellationRequested || PolicyBlocksPrewarm(game) || DriveHasRunningGame(game)
            ? null
            : PartialCapMib(game, fullMib, WindowAvailableMib(_env.AvailableRamMib()), PhysCapMib(), roomMib, FootprintOf(game)?.Mib);

    // 묶음 예산이 모자랄 때만 순서를 바꿉니다. 순위 (입력 순서) 대로 통째 들어가는 앞 게임들과, 부분 예열이 남은 예산을 받는 첫 게임은 자리를 지키고,
    // 그 뒤 후보는 바이트당 가치 (순위 가중 1/(순위+1) ÷ 존 MiB, 존을 모르면 DefaultFullPrewarmMib) 순으로 늘어놓습니다. 모두 들어가면 입력 그대로.
    // shortcut: 점수 자체가 아니라 순위 위치를 가치로 씀 (예열 점수는 자동 모드 쪽에서 순위로만 넘어옴), 점수를 Prewarmer 에 넘기게 되면 그 값으로.
    internal static IReadOnlyList<GameSettings> OrderByValuePerByte(IReadOnlyList<GameSettings> list, Func<GameSettings, long> zoneMib, Func<GameSettings, long, long> roomMib)
    {
        long used = 0;
        int keep = 0;
        while (keep < list.Count && used + zoneMib(list[keep]) <= roomMib(list[keep], used))
        {
            used += zoneMib(list[keep++]);
        }

        if (keep < list.Count && roomMib(list[keep], used) >= MinPartialMib)
        {
            keep++; // 부분 예열이 남은 예산을 받는 게임은 순위 자리 유지
        }

        keep = Math.Max(keep, 1); // 순위 1위는 항상 자리를 지킴 (못 들어가도 뒤 후보와 섞이지 않음)
        if (list.Count - keep < 2)
        {
            return list;
        }

        return [.. list.Take(keep), .. list.Skip(keep).Select((g, n) => (G: g, V: 1.0 / (keep + n + 1) / (zoneMib(g) is > 0 and long z ? z : DefaultFullPrewarmMib))).OrderByDescending(x => x.V).Select(x => x.G)];
    }

    // estimate: 예산 계산의 추정으로만 쓰는 호출 (입력 파일·설정이 같고 MaxAge 안이면 이전 값 재사용). 읽을 범위·양을 정하는 호출은 false 로 매번 새로 읽음.
    private long ZoneMib(GameSettings game, bool estimate = false) =>
        (((estimate ? EstimatedZoneBytes(game) : _env.ZoneBytes(game)) ?? 0) + (1 << 20) - 1) >> 20;

    private long? EstimatedZoneBytes(GameSettings game) => Estimated("zone", game, false, () => _env.ZoneBytes(game), true);

    // 추정의 재사용 키: 입력 파일 서명 + ZoneBytes·ObservedBytes 가 쓰는 게임 설정. 서명을 만들 수 없으면 (시험의 가짜 env 포함) null = 재사용 안 함.
    private string? EstimateStampOf(GameSettings game, bool fromMemoryCaches) =>
        _env.EstimateStamp?.Invoke(game, fromMemoryCaches) is { } files
            ? $"{files}|{game.Plan}|{game.Trace}|{game.OriginRoot}|{game.MaxStreamSet}|{game.MaxStreamMib}"
            : null;

    private long? Estimated(string kind, GameSettings game, bool fromMemoryCaches, Func<long?> compute, bool estimate) =>
        _estimates.Get(kind, game.Name, estimate ? EstimateStampOf(game, fromMemoryCaches) : null, compute);

    private long ObservedMib(GameSettings game, long totalMib, bool allowSourceRead = true, bool estimate = false)
    {
        if (totalMib <= 0)
        {
            return totalMib;
        }

        long? observedBytes = allowSourceRead
            ? Estimated("observed", game, false, () => _env.ObservedBytes(game), estimate)
            : Estimated("observed-cached", game, true, () => _env.ObservedBytesIfCached(game), estimate);
        if (observedBytes is not { } bytes)
        {
            return totalMib;
        }

        return Math.Min(totalMib, (bytes + (1 << 20) - 1) >> 20);
    }

    // 묶음 안 게임별 관찰분 (MiB)과, 각 게임 뒤에 남겨 둘 양: 뒤 게임들의 관찰분 합. 앞 게임의 추측분이 뒤 게임의 관찰분을 밀어내지 않게 합니다.
    // 자기 관찰분은 항상 보장되므로 목록 전체가 관찰된 (출처 표식이 없는) 게임은 이전 순위 규칙 그대로입니다. 건너뛰는 게임과 기록 없는 예열은 남기지 않습니다.
    private (long[] Observed, long[] Later) ObservedSplit(IReadOnlyList<GameSettings> list, long avail)
    {
        bool[] skips = [.. list.Select(g => SkipsInBatch(g, avail))];
        long[] observed = [.. list.Select((g, i) => skips[i] ? ZoneMib(g) : ObservedMib(g, ZoneMib(g), allowSourceRead: !DriveHasRunningGame(g)))];
        var later = new long[list.Count];
        for (int i = list.Count - 2; i >= 0; i--)
        {
            later[i] = later[i + 1] + (skips[i + 1] ? 0 : observed[i + 1]);
        }

        return (observed, later);
    }

    // 이 게임이 읽을 수 있는 양: 남은 예산에서 뒤 게임의 관찰분을 뺀 만큼 (자기 관찰분은 예산 안에서 항상 보장).
    private static long RoomKeepingLaterObserved(long room, long ownObserved, long later) => Math.Max(Math.Min(ownObserved, room), room - later);

    // 틈 메우기 포함 읽기 전체의 상한: min(유효 존 상한 (모르면 현재 존), 스트림 상한, 물리 RAM / 4, 가용 RAM - 예비). 원래 범위는 호출 쪽에서 뺍니다.
    internal static long GapFillBudgetBytes(GameSettings game, long? zoneBytes, long? effectiveZoneBytes, long physicalRamBytes, long availableRamMib)
    {
        long? capZone = effectiveZoneBytes is > 0 ? effectiveZoneBytes : zoneBytes;
        long streamCap = capZone is > 0 and long zone
            ? Math.Min(zone, game.StreamCapBytes(zone))
            : (long)game.MaxStreamMib << 20;
        long physicalCap = physicalRamBytes > 0 ? physicalRamBytes / 4 : long.MaxValue;
        long availableMib = Math.Max(0, availableRamMib - Math.Max(game.Prewarm.MinFreeRamMib, GapFillReserveMib));
        long availableCap = availableMib > (long.MaxValue >> 20) ? long.MaxValue : availableMib << 20;
        return Math.Min(streamCap, Math.Min(physicalCap, availableCap));
    }

    /// 플레이 가능성이 가장 높은 예열한 게임 (Warmed 의 첫 게임), 예산 순위 (WarmRank) 가 없으면 순서가 예열 시각뿐이라 null.
    internal GameSettings? FirstPlayPriority() => WarmRank is null ? null : Warmed().FirstOrDefault();

    /// 예열에 성공한 적이 있는 게임 (설정에 아직 있는 것만, 게임 정책이 예열을 막은 게임은 제외). 순서는 예산 순위 (WarmRank, 자동 모드의 예열 점수) 가 있으면 그 순위 (높은 것부터, 순위에 없는 게임은 뒤에 예열 시각 순),
    /// 없으면 마지막 예열 시각이 최근인 것부터. 예산 배정과 사다리가 같은 기준을 쓰므로 배정 1순위와 사다리 1순위가 같은 게임입니다.
    internal IReadOnlyList<GameSettings> Warmed()
    {
        List<GameSettings> byWarmTime;
        lock (_gate)
        {
            byWarmTime = [.. _games.Where(g => _last.ContainsKey(g.Name) && HasPlayed(g.Name) && GamePolicy.AllowsPrewarm(g)).OrderByDescending(g => _last[g.Name]).ThenBy(g => g.Name, StringComparer.Ordinal)];
        }

        if (WarmRank is not { } rank || byWarmTime.Count < 2)
        {
            return byWarmTime;
        }

        List<GameSettings> ranked = [.. rank(byWarmTime).Where(g => byWarmTime.Any(w => string.Equals(w.Name, g.Name, StringComparison.OrdinalIgnoreCase)))];
        return [.. ranked, .. byWarmTime.Where(w => !ranked.Any(g => string.Equals(w.Name, g.Name, StringComparison.OrdinalIgnoreCase)))];
    }

    /// 재예열·보충 예열 대상이 게임들 합계로 캐시 예산 (기본 물리 RAM/2, prewarm.max_cache_mib) 을 넘지 않게 합니다. 관찰 범위를 우선 배정하고 미배정량은 기존 순위대로 나눕니다.
    /// 상태를 저장하지 않으므로 순위가 바뀌면 다음 점검에서 대상도 바뀝니다. 순위가 없으면 null (모두 대상).
    /// AllocMib: 들어간 게임이 셈에 쓴 양 (MiB): 재예열은 이만큼까지만 읽어야 계상과 실제 읽기가 같습니다.
    internal (HashSet<string> In, long CapMib, long UsedMib, Dictionary<string, long> AllocMib)? WarmBudget()
    {
        Func<IReadOnlyList<GameSettings>, IEnumerable<GameSettings>>? rank = WarmRank;
        bool hasPending;
        lock (_gate)
        {
            hasPending = _pendingWarm.Count > 0;
        }

        if (rank is null && !hasPending)
        {
            return null;
        }

        long cap = PhysCapMib();
        List<GameSettings> candidates = [.. Warmed()];
        lock (_gate)
        {
            candidates.AddRange(_games.Where(g => _partial.ContainsKey(g.Name) && GamePolicy.AllowsPrewarm(g) && !candidates.Any(c => string.Equals(c.Name, g.Name, StringComparison.OrdinalIgnoreCase))));
            candidates.AddRange(_games.Where(g => _pendingWarm.ContainsKey(g.Name) && GamePolicy.AllowsPrewarm(g) && !candidates.Any(c => string.Equals(c.Name, g.Name, StringComparison.OrdinalIgnoreCase))));
        }

        HashSet<string> names = [.. candidates.Select(g => g.Name)];
        var inBudget = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var alloc = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long used = 0;
        string? triggered;
        lock (_gate)
        {
            triggered = _triggered is { } t && _now() - t.At < TriggerPriorityWindow ? t.Game : null;
        }

        // 방금 트리거로 켠 게임은 관찰·보충 두 단계 모두에서 먼저 배정받고, 같은 단계의 나머지는 기존 순위를 유지합니다.
        List<GameSettings> ordered = [.. (rank?.Invoke(candidates) ?? candidates).Where(g => names.Contains(g.Name)).Concat(candidates)
            .DistinctBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => string.Equals(g.Name, triggered, StringComparison.OrdinalIgnoreCase) ? 0 : 1)];
        var totalMib = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var observedMib = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (GameSettings g in ordered)
        {
            long total = ZoneMib(g, estimate: true);
            bool hasZoneList = total > 0;
            long coldHeld;
            bool pending;
            lock (_gate)
            {
                coldHeld = _coldMib.GetValueOrDefault(g.Name);
                pending = _pendingWarm.ContainsKey(g.Name);
            }

            if (total <= 0)
            {
                total = coldHeld; // 기록 없이 예열한 게임은 올려 둔 양
            }

            bool unknownPendingSize = pending && coldHeld <= 0 && total <= 0;
            if (unknownPendingSize)
            {
                total = cap;
            }

            total = Math.Min(total, cap);
            long observed = unknownPendingSize ? 0 : total <= 0 || !hasZoneList
                ? total
                : ObservedMib(g, total, allowSourceRead: !DriveHasRunningGame(g), estimate: true); // 같은 디스크에서 게임이 실행 중이면 출처를 읽지 않고, 출처를 모르면 전부 관찰된 것으로 셈
            totalMib[g.Name] = total;
            observedMib[g.Name] = observed;
        }

        // 1단계: 관찰 범위는 예전과 같이 게임 단위로 통째 들어갈 때만 (순위 순). 2단계: 남은 예산을 순위대로 보충 범위에 나눔 (들어가지 못한 게임의 보충 범위는 읽지 않음).
        var rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (GameSettings g in ordered)
        {
            long observed = observedMib[g.Name];
            if (used + observed <= cap)
            {
                if (observed > 0 || totalMib[g.Name] <= 0) // 크기를 모르는 게임은 예전처럼 대상 (배정량 0 = 제한 없음)
                {
                    inBudget.Add(g.Name);
                    alloc[g.Name] = observed;
                    used += observed;
                }
            }
            else
            {
                rejected.Add(g.Name);
            }
        }

        foreach (GameSettings g in ordered.Where(g => !rejected.Contains(g.Name)))
        {
            long take = Math.Min(totalMib[g.Name] - observedMib[g.Name], Math.Max(0, cap - used));
            if (take <= 0)
            {
                continue;
            }

            inBudget.Add(g.Name);
            alloc[g.Name] = alloc.GetValueOrDefault(g.Name) + take;
            used += take;
        }

        // 3단계: 두 단계에서 배정되지 않은 관찰 범위도 남은 예산으로 최소 부분량을 받습니다.
        foreach (GameSettings g in ordered.Where(g => rejected.Contains(g.Name)))
        {
            long take = Math.Min(Math.Max(0, totalMib[g.Name] - alloc.GetValueOrDefault(g.Name)), Math.Max(0, cap - used));
            if (take < MinPartialMib)
            {
                continue;
            }

            inBudget.Add(g.Name);
            alloc[g.Name] = alloc.GetValueOrDefault(g.Name) + take;
            used += take;
        }

        lock (_gate)
        {
            DateTimeOffset now = _now();
            foreach (string recovered in _pendingWarmBudgetSkipped.Keys.Where(name => alloc.GetValueOrDefault(name) > 0).ToList())
            {
                _pendingWarmBudgetSkipped.Remove(recovered);
                if (_pendingWarm.TryGetValue(recovered, out PendingWarm? pending) && pending.RetryAt > now)
                {
                    _pendingWarm[recovered] = pending with { RetryAt = now }; // 배정이 회복되면 대기 기한을 앞당김
                }
            }
        }

        return (inBudget, cap, used, alloc);
    }

    private readonly HashSet<string> _sparseOut = new(StringComparer.OrdinalIgnoreCase); // 마지막 예열 판정이 스파스 범위를 뺀 게임, _gate 로 보호

    internal void SetSparseOut(string game, bool on)
    {
        lock (_gate)
        {
            _ = on ? _sparseOut.Add(game) : _sparseOut.Remove(game);
        }
    }

    // 마지막 예열 판정이 스파스 범위를 뺐으면 그 표시를 단 사본, 아니면 그대로: 재예열·사다리 표본, 보충, 고정이 예열이 읽은 목록과 같은 범위를 보게 함.
    internal GameSettings WithSparseMark(GameSettings g)
    {
        lock (_gate)
        {
            return !g.SkipSparse && _sparseOut.Contains(g.Name) ? g with { SkipSparse = true } : g;
        }
    }

    // 계획한 예열 목록에서 스파스 파일이 차지하는 양 (MiB): 사다리가 만지지 못하는 범위. 목록을 계획할 수 없으면 0.
    private long SparseMib(GameSettings game)
    {
        try
        {
            if (_streamer.PlannedPrewarmReads(game) is not { Count: > 0 } planned)
            {
                return 0;
            }

            var sparse = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            long bytes = planned.Where(op => sparse.TryGetValue(op.Path, out bool s) ? s : sparse[op.Path] = _env.IsSparse(op.Path)).Sum(op => op.Length);
            return bytes >> 20;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException or Win32Exception)
        {
            return 0;
        }
    }

    /// 예산 안에서 이 게임이 읽어도 되는 양 (MiB), 제한 없으면 null: 재배치 뒤 예열처럼 묶음 예산 없이 도는 예열이 다른 게임의 몫을 잠식하지 않게 재예열·보충과 같은 배정 (WarmBudget) 을 받습니다.
    /// 순위가 없으면 (예산을 나누지 않는 설정) 제한 없음. 예산에 든 게임은 배정량 (0 은 크기를 모르는 게임이라 제한 없음), 든 적 없는 게임은 남은 예산.
    internal long? BudgetRoomMib(GameSettings game)
    {
        if (WarmBudget() is not { } budget)
        {
            return null;
        }

        if (budget.In.Contains(game.Name))
        {
            return budget.AllocMib.GetValueOrDefault(game.Name) is > 0 and long alloc ? alloc : null;
        }

        return Math.Max(0, budget.CapMib - budget.UsedMib);
    }

    internal static string WarmBudgetSkipDetail(long capMib, long usedMib) =>
        $"the cache budget of {capMib} MiB has {usedMib} MiB assigned to warm targets; no allocation of at least {MinPartialMib} MiB fits this game";

    // 미플레이 게임은 예산에도 표본 읽기에도 넣지 않습니다 (플레이한 게임이 우선, 미플레이 표본을 HDD 에서 읽지 않음).
    // 기록 없이 예열한 게임들이 지금도 RAM 에 있다고 보는 양 (MiB): 기록한 양이되, 표본을 다시 읽어 식었으면 (CacheProbe) 0 으로 바꿉니다. 영구히 예산을 막지 않게.
    // 표본을 읽을 수 없으면 (null) 기록한 양을 그대로 씁니다. 표본 읽기는 잠금 밖에서.
    private long ColdMib(string? except)
    {
        List<(string Name, IReadOnlyList<(string Path, long Offset, int Bytes)>? Samples)> held;
        lock (_gate)
        {
            held = [.. _coldMib.Where(kv => kv.Value > 0 && HasPlayed(kv.Key) && !string.Equals(kv.Key, except, StringComparison.OrdinalIgnoreCase)).Select(kv => (kv.Key, _coldSamples.GetValueOrDefault(kv.Key)))];
        }

        foreach ((string name, var samples) in held)
        {
            GameSettings? gm = _games.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            bool busy = gm is not null && (DriveHasRunningGame(gm) || PolicyBlocksPrewarm(gm)); // 같은 디스크에서 게임이 도는 동안과 정책이 예열을 막은 게임은 표본을 읽지 않음
            if (!busy && gm is not null && samples is not null && ProbeSamplesForGame(gm, samples, "warm state sample") is { Cold: true })
            {
                lock (_gate)
                {
                    if (_coldMib.ContainsKey(name))
                    {
                        _coldMib[name] = 0;
                    }
                }

                _log.Info($"{name}: cold prewarm no longer counts against the budget (its samples left the file cache)");
            }
        }

        lock (_gate)
        {
            return _coldMib.Where(kv => HasPlayed(kv.Key) && !string.Equals(kv.Key, except, StringComparison.OrdinalIgnoreCase)).Sum(kv => kv.Value);
        }
    }

    // 메모리 부분 예열 상태를 실제 읽은 양으로 확정 (_gate 안에서 부름): 완전 예열로 기록하지 않고, 나머지는 메모리가 날 때 보충 예열이 읽고, 재예열 점검은 읽은 앞부분만 잼.
    // listEnded: 계획 목록을 끝까지 읽었음 (메모리로 멈추거나 한도로 마무리해 창 앞쪽만 읽은 것이 아님). 이때 창보다 적게 읽었으면 목록이 창보다 짧은 것이라 창 크기를 남김.
    private void SetMemoryPartialLocked(GameSettings game, StreamStats stats, long windowMib, long hotMib, string? inputs, bool listEnded = false)
    {
        (long WarmedMib, long HotMib) actual = (ActualMib(stats, windowMib), hotMib);
        if (listEnded && windowMib > actual.WarmedMib)
        {
            _partialWindowMib[game.Name] = windowMib; // 목록이 창보다 짧음: 다시 읽을 때 같은 창으로 같은 목록을 만들도록 창 크기를 남김
        }
        else
        {
            _partialWindowMib.Remove(game.Name);
        }

        _ = listEnded ? _rangeUnknown.Remove(game.Name) : _rangeUnknown.Add(game.Name); // 메모리로 멈추거나 한도로 마무리하면 읽은 범위는 창 앞쪽이라 같은 창으로 다시 만든 목록과 다름

        (_memoryPartial[game.Name], _partial[game.Name]) = (actual, actual);
        _supplementReadProgress[game.Name] = new(stats.Bytes, inputs, game);
        _supplementRetry.Remove(game.Name);
    }

    // 읽는 도중 메모리 판정으로 멈춘 예열: 멈춘 시각을 기억하고, 줄여 읽던 창 (plan) 이 틈·오류·순서 바꿈 없이 읽은 만큼을 부분 예열로 남김. 남긴 양 (MiB), 안 남겼으면 null.
    // 전체 계획 경로 (plan 없음) 와 0 MiB 는 남기지 않음: 읽은 곳이 목록 앞쪽과 같다고 보장할 수 없음 (보충의 NoteProgress 규칙과 같게).
    // shortcut: 위치는 창 스케줄 기준의 근사 (접근 순서 head 를 넘는 구간은 물리 순서라 목록 앞쪽과 다를 수 있음), 어긋난 구간은 재예열 점검의 표본이 식은 것으로 잡아 다시 읽음.
    private long? NoteMemoryStop(GameSettings game, StreamStats stats, (long WarmedMib, long HotMib)? plan)
    {
        bool positional = plan is not null && stats.Bytes > 0 && stats.GapBytes == 0 && stats.Errors == 0 && !_streamer.WasPrewarmReordered(game);
        string? inputs = positional ? PrewarmRun.InputStamp(game) : null;
        lock (_gate)
        {
            _memoryStopAt[game.Name] = _now();
            if (!positional)
            {
                return null;
            }

            (long windowMib, long hotMib) = plan!.Value;
            long warmedMib = ActualMib(stats, windowMib);
            if (warmedMib >= hotMib)
            {
                return null;
            }

            _supplementNothingNew.Remove(game.Name);
            _resume.Remove(game.Name); // 부분 상태를 새로 정함: 보충의 옛 이어 읽기 위치는 낡음
            SetMemoryPartialLocked(game, stats, windowMib, hotMib, inputs);
            return warmedMib;
        }
    }

    // 메모리로 멈춘 게임의 재시도 대기를 한 주기 대신 MemoryRetryFloor 뒤로 당김 (기한을 뒤로 밀지는 않음). 메모리가 회복됐는지는 다시 읽을 때 창 계산이 확인함.
    private void ExpediteMemoryRetry(string game)
    {
        lock (_gate)
        {
            DateTimeOffset soon = _now() + MemoryRetryFloor;
            if (_pendingWarm.TryGetValue(game, out PendingWarm? pending) && pending.RetryAt > soon)
            {
                _pendingWarm[game] = pending with { RetryAt = soon };
            }
        }
    }

    private bool HasMemoryStop(string game)
    {
        lock (_gate)
        {
            return _memoryStopAt.ContainsKey(game);
        }
    }

    // 메모리로 멈춘 지 MemoryRetryFloor 가 안 지났는가.
    private bool MemoryRetryTooSoon(string game)
    {
        lock (_gate)
        {
            return _memoryStopAt.TryGetValue(game, out DateTimeOffset at) && _now() - at < MemoryRetryFloor;
        }
    }

    // 읽는 도중 메모리 판정이 "여기서 마무리" 를 알리는 표시 (스트림 한 번, 창 읽기에서만): 취소가 아니라 새 묶음을 내지 않고 읽은 데까지를 부분 예열로 남김.
    // skipBytes: 이미 읽어 건너뛴 앞부분. limitBytes: 이번 읽기가 (건너뛴 앞부분 포함) 넘지 않을 총량 (이어 읽는 창이 지금 예산보다 클 때), 없으면 long.MaxValue.
    private sealed class MemoryFit(long skipBytes, long limitBytes)
    {
        private int _finished;
        private bool _budget;

        public long AvailableMib { get; private set; }

        public long ReserveMib { get; private set; }

        public bool Finished => Volatile.Read(ref _finished) != 0;

        public string Detail => $"available {AvailableMib} MiB, reserve {ReserveMib} MiB";

        // 마무리 로그의 "the rest ..." 뒤에 붙는 사유: 메모리에 안 들어감인지, 읽을 총량 한도 (배정 몫·캐시 예산) 에 닿았는지.
        public string Reason => _budget ? $"is left for later, the budget was reached ({Detail})" : $"does not fit in memory ({Detail})";

        // 이번 읽기 목록 (건너뛴 앞부분 제외) 에서 읽을 총량: 한도가 있으면 한도까지만.
        public long EffectiveTotal(long total) => Math.Min(total, Math.Max(0, limitBytes - skipBytes));

        public void Finish(long availableMib, long reserveMib, bool budget = false)
        {
            (AvailableMib, ReserveMib, _budget) = (availableMib, reserveMib, budget);
            Volatile.Write(ref _finished, 1);
        }
    }

    // 실제 읽기 집합 크기. 부분 예열은 스트림처럼 마지막 범위를 자르고, 조회하지 못하면 null입니다.
    private long? ReadSetBytes(GameSettings game, bool partial = false)
    {
        try
        {
            return _streamer.PrewarmReadBytes(game, partial);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException or Win32Exception)
        {
            return null;
        }
    }
}
