using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Windows;

namespace AsterCache.Service;

/// BackgroundYield 의 상태 파일 (background_yield.json): 낮춘 프로세스의 pid·생성 시각·원래 우선순위. 직전 실행이 비정상 종료해도 다음 시작 때 되돌리려고 남깁니다.
public sealed partial class BackgroundYield
{
    private const int MaxPriority = 7;

    /// 서비스 시작 때 한 번: 직전 실행이 남긴 상태 파일이 있으면 그 프로세스의 우선순위를 되돌리고 파일을 지웁니다 (설정이 꺼져 있어도). 깨진 파일은 지우고 넘어갑니다.
    public void Recover()
    {
        lock (_lock)
        {
            try
            {
                if (env.ReadState() is not { } text)
                {
                    return;
                }

                var kept = new List<LoweredProcess>();
                int restored = Restore(Parse(text), env.Processes(), kept);
                foreach (LoweredProcess k in kept)
                {
                    _lowered[k.Pid] = k; // 같은 프로세스인데 쓰기에 실패한 항목은 파일에 남겨 다음 End·Recover 가 다시 시도
                }

                _ = SaveState();
                if (restored > 0)
                {
                    log.Info($"background yield: restored memory priority for {restored} processes left by the previous run");
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.Error("background yield failed", e);
            }
        }
    }

    // 깨진 항목은 건너뛰고, 파일 전체가 JSON 이 아니면 빈 목록 (그러면 파일만 지워짐).
    private static List<LoweredProcess> Parse(string text)
    {
        var result = new List<LoweredProcess>();
        try
        {
            if (JsonRead.Parse(text) is JObject root && root.TryGetValue("lowered", out object? list) && list is List<object?> items)
            {
                foreach (JObject o in items.OfType<JObject>())
                {
                    if (o.TryGetValue("pid", out object? pid) && pid is long id && id > 4 && id <= int.MaxValue
                        && o.TryGetValue("created", out object? created) && created is long at
                        && o.TryGetValue("priority", out object? priority) && priority is long p && p is > ProcessMemory.PriorityVeryLow and <= MaxPriority)
                    {
                        string? exe = o.TryGetValue("exe", out object? name) ? name as string : null; // 선택 필드: 옛 파일에는 없음
                        result.Add(new LoweredProcess((int)id, exe, null, at, (int)p));
                    }
                }
            }
        }
        catch (PyValueException)
        {
            // 깨진 파일
        }

        return result;
    }

    // 낮춘 프로세스가 있으면 상태 파일에 쓰고 없으면 지웁니다. 파일 오류는 경고를 남기고 false (호출자가 낮추기를 포기할 수 있게).
    private bool SaveState()
    {
        try
        {
            if (_lowered.Count == 0)
            {
                env.DeleteState();
                return true;
            }

            var items = new List<object?>();
            foreach (LoweredProcess p in _lowered.Values)
            {
                var item = new JObject { { "pid", (long)p.Pid } };
                if (p.Exe is not null)
                {
                    item.Add("exe", p.Exe);
                }

                item.Add("created", p.Created);
                item.Add("priority", (long)p.Priority);
                items.Add(item);
            }

            env.WriteState(PyJson.Dumps(new JObject { { "lowered", items } }, indent: 2, ensureAscii: false) + "\n");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Warn("background yield state file not written: " + e.GetType().Name);
            return false;
        }
    }
}
