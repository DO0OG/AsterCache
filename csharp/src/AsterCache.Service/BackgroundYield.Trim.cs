using AsterCache.Core.Logging;
using AsterCache.Windows;

namespace AsterCache.Service;

/// BackgroundYield 의 세션 시작과 비우기 판정·실행. 판정 논리는 YieldPressure 의 순수 함수입니다.
public sealed partial class BackgroundYield
{
    // 낮추기는 항상, 비우기는 여유분이 목표보다 모자랄 때만 (조회가 안 되면 동작을 잃지 않게 비움).
    private YieldStart StartSession(DateTimeOffset now)
    {
        _lastBegin = now;
        (_active, _lastActivity, _lastMaintain, _lastTrim) = (true, now, now, now);
        long before = env.AvailableMib();
        _skipped.Clear();
        IReadOnlyList<(int Pid, string Exe)> processes = env.Processes();
        Revalidate(processes);
        List<YieldTarget> found = Select(processes, _ => false);
        _targets.Clear();
        foreach (YieldTarget t in found)
        {
            _targets[t.Pid] = t;
        }

        int lowered = Lower(found);
        MemoryLists? lists = QueryLists();
        _lastRepurposed = lists?.CacheRepurposedBytes;
        _beginTrimmed = YieldPressure.NeedsTrim(lists, env.PhysicalBytes());
        long? headroomMib = lists is { } l ? l.HeadroomBytes >> 20 : null;
        if (!_beginTrimmed)
        {
            return new YieldStart(before, 0, 0, lowered, false, headroomMib);
        }

        (int trimmed, long bytes, _) = TrimAll(found, now);
        return new YieldStart(before, trimmed, bytes, lowered, true, headroomMib);
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

    // 작업 집합이 TrimMinBytes 이상인 대상만 비웁니다. 지난번 비우기 직전 값의 절반 이상을 되찾은 대상은 활발한 프로세스라 이 세션에서 다시 비우지 않습니다 (메모리는 안 생기고 비용만 듦).
    private (int Count, long Bytes, int Active) TrimAll(IEnumerable<YieldTarget> targets, DateTimeOffset now)
    {
        int count = 0;
        int active = 0;
        long bytes = 0;
        foreach (YieldTarget t in targets.Where(t => t.WorkingSetBytes >= TrimMinBytes))
        {
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

    // 유지 (2분마다, 등록 게임이 실행 중일 때): 새로 뜬 대상에 우선순위를 낮추고, 마지막 비우기 뒤 RetrimEvery 가 지났고 직전 유지 이후 캐시가 RetrimCacheBytes 이상 재사용됐을 때만 다시 비웁니다.
    // 대기 목록은 매 유지마다 한 번 읽어 기준점을 갱신합니다 (틱마다가 아님).
    private void Maintain(IReadOnlyList<(int Pid, string Exe)> processes, DateTimeOffset now)
    {
        TimeSpan span = now - _lastMaintain;
        _lastMaintain = now;
        Revalidate(processes);
        HashSet<int> alive = [.. processes.Select(p => p.Pid)];
        foreach (int pid in _targets.Keys.Where(pid => !alive.Contains(pid)).ToList())
        {
            _ = _targets.Remove(pid);
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

        _ = Lower(fresh);
        long? repurposed = QueryLists()?.CacheRepurposedBytes;
        long? delta = repurposed - _lastRepurposed;
        _lastRepurposed = repurposed;
        if (!YieldPressure.ShouldRetrim(now - _lastTrim, delta))
        {
            return;
        }

        (int count, long bytes, int active) = TrimAll(Measure(processes), now);
        log.Info($"background yield: trimmed again {count} processes ({bytes >> 20} MiB working set), cache repurposed {delta!.Value >> 20} MiB in {(int)span.TotalSeconds} s, {active} active processes skipped");
    }
}
