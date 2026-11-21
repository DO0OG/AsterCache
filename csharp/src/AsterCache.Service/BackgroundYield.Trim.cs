using AsterCache.Core.Logging;
using AsterCache.Windows;

namespace AsterCache.Service;

/// BackgroundYield 의 세션 시작과 비우기 판정·실행. 판정 논리는 YieldPressure 의 순수 함수입니다.
public sealed partial class BackgroundYield
{
    // 쓰고 있는 프로세스를 뺀 낮추기는 항상, 비우기는 (가용 - 게임이 가져갈 양) 이 하한보다 모자랄 때만 그 모자라는 만큼 (조회가 안 되는 것은 대기 목록 통계뿐이고 가용 메모리는 항상 읽음).
    private YieldStart StartSession(DateTimeOffset now)
    {
        _lastBegin = now;
        (_active, _lastActivity, _lastMaintain, _lastTrim) = (true, now, now, now);
        long before = env.AvailableMib();
        _startAvailableMib = before;
        SampleUsage(env.Monotonic());
        _skipped.Clear();
        IReadOnlyList<(int Pid, string Exe)> processes = env.Processes();
        Revalidate(processes);
        List<YieldTarget> found = Select(processes, _ => false);
        _targets.Clear();
        foreach (YieldTarget t in found)
        {
            _targets[t.Pid] = t;
        }

        if (_lowered.Count == 0)
        {
            _cpuApplied = CpuEnabled && GameRunning(processes); // 이미 낮춘 대상이 있는 채 다시 시작하면 그 대상의 CPU 등급은 틱의 LowerCpuLate 가 이어서 낮춤 (여기서 적용된 것으로 쳐 버리지 않음)
        }

        (int lowered, int ioLowered, int cpuLowered) = Lower(found, processes);
        int spared = found.Count(t => !t.InheritOnly && _usage.IsInUse(t.Pid));
        _lastRepurposed = QueryLists()?.CacheRepurposedBytes;
        (long need, long expectedMib, long floorMib) = Shortfall(processes, before);
        _beginTrimmed = need > 0;
        if (!_beginTrimmed)
        {
            return new YieldStart(before, 0, 0, lowered, ioLowered, cpuLowered, false, spared, 0, expectedMib, floorMib);
        }

        (int trimmed, long bytes, _) = TrimAll(found, now, need);
        return new YieldStart(before, trimmed, bytes, lowered, ioLowered, cpuLowered, true, spared, need >> 20, expectedMib, floorMib);
    }

    // 조회 실패는 세션당 한 번만 알립니다.
    private MemoryLists? QueryLists()
    {
        MemoryLists? lists = env.Lists();
        if (lists is null && !_listsWarned)
        {
            _listsWarned = true;
            log.Warn("background yield: memory lists unavailable, trimming once per session");
        }

        return lists;
    }

    // 쓰고 있지 않은 대상 가운데 작업 집합이 TrimMinBytes 이상인 것을 큰 것부터, 비운 양이 needBytes 가 될 때까지만 비웁니다. 지난번 비우기 직전 값의 절반 이상을 되찾은 대상은 활발한 프로세스라 이 세션에서 다시 비우지 않습니다 (메모리는 안 생기고 비용만 듦).
    private (int Count, long Bytes, int Active) TrimAll(IEnumerable<YieldTarget> targets, DateTimeOffset now, long needBytes)
    {
        using var activity = ServiceActivity.Begin(ServiceOp.Trim); // 초 단위 기록의 동작 표시
        int count = 0;
        int active = 0;
        long bytes = 0;
        foreach (YieldTarget t in targets.Where(t => !t.InheritOnly && t.WorkingSetBytes >= TrimMinBytes && !_usage.IsInUse(t.Pid)).OrderByDescending(t => t.WorkingSetBytes))
        {
            if (bytes >= needBytes)
            {
                break;
            }

            if (_trimMemo.TryGetValue(t.Pid, out TrimMemo memo) && memo.Created == t.Created
                && (memo.Busy || YieldPressure.RegainedMost(t.WorkingSetBytes, memo.Before)))
            {
                _trimMemo[t.Pid] = memo with { Busy = true };
                active++;
            }
            else if (env.Trim(t.Pid))
            {
                (count, bytes) = (count + 1, bytes + t.WorkingSetBytes);
                _trimMemo[t.Pid] = new TrimMemo(t.Created, t.WorkingSetBytes, false);
            }
        }

        _lastTrim = now; // 비우기에 걸린 시간을 빼지 않음 (다음 유지 주기의 간격 판정이 한 주기를 건너뛰지 않게)
        return (count, bytes, active);
    }

    // 유지 (2분마다, 등록 게임이 실행 중일 때): 새로 뜬 대상에 우선순위를 낮추고, 마지막 비우기 뒤 RetrimEvery 가 지났고 직전 유지 이후 캐시가 RetrimCacheBytes 이상 재사용됐고 가용 메모리가 시작 때와 같은 조건으로 모자랄 때만 그 모자라는 만큼 다시 비웁니다.
    // 대기 목록은 매 유지마다 한 번 읽어 기준점을 갱신합니다 (틱마다가 아님).
    private void Maintain(IReadOnlyList<(int Pid, string Exe)> processes, DateTimeOffset now)
    {
        TimeSpan span = now - _lastMaintain;
        _lastMaintain = now;
        Revalidate(processes);
        HashSet<int> alive = [.. processes.Select(p => p.Pid)];
        Dictionary<int, string> names = NamesOf(processes);
        foreach (int pid in _targets.Values.Where(t => !names.TryGetValue(t.Pid, out string? now) || !string.Equals(now, t.Exe, StringComparison.OrdinalIgnoreCase)).Select(t => t.Pid).ToList())
        {
            _ = _targets.Remove(pid); // 끝났거나 같은 pid 를 다른 이름의 프로세스가 이어받음 (그러면 아래 Select 가 새 프로세스로 다시 고름)
        }

        _skipped.RemoveWhere(pid => !alive.Contains(pid));
        foreach (int pid in _trimMemo.Keys.Where(pid => !alive.Contains(pid)).ToList())
        {
            _ = _trimMemo.Remove(pid);
        }

        List<YieldTarget> fresh = Select(processes, pid => _targets.ContainsKey(pid) || _skipped.Contains(pid));
        foreach (YieldTarget t in fresh)
        {
            _targets[t.Pid] = t;
        }

        _ = Lower(fresh, processes);
        long? repurposed = QueryLists()?.CacheRepurposedBytes;
        long? delta = repurposed - _lastRepurposed;
        _lastRepurposed = repurposed;
        if (!YieldPressure.ShouldRetrim(now - _lastTrim, delta))
        {
            return;
        }

        long availableMib = env.AvailableMib();
        (long need, long expectedMib, long floorMib) = Shortfall(processes, availableMib);
        string facts = $"available {availableMib} MiB, expected game use {expectedMib} MiB, floor {floorMib} MiB";
        if (need <= 0)
        {
            log.Info($"background yield: not trimmed again, memory is sufficient ({facts}), cache repurposed {delta!.Value >> 20} MiB in {(int)span.TotalSeconds} s");
            return;
        }

        List<YieldTarget> measured = Measure(processes);
        int spared = measured.Count(t => !t.InheritOnly && _usage.IsInUse(t.Pid));
        (int count, long bytes, int active) = TrimAll(measured, now, need);
        log.Info($"background yield: trimmed again {count} processes ({bytes >> 20} MiB working set, shortfall {need >> 20} MiB), cache repurposed {delta!.Value >> 20} MiB in {(int)span.TotalSeconds} s, {active} active processes skipped, {spared} in-use processes spared, {facts}");
    }
}
