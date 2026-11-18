using AsterCache.Core.Prefetch;

namespace AsterCache.Core.Trace;

/// 서비스가 파일 캐시로 읽어 들인 묶음 하나: 읽은 구간 목록과 그 읽기를 시작한 때·끝난 때.
public readonly record struct SelfReadBatch(DateTimeOffset StartedUtc, DateTimeOffset EndedUtc, ReadOp[] Ops);

/// 관찰 하나 (디스크 읽기 집계 하나) 동안 서비스가 읽은 묶음을 모읍니다. 관찰이 끝날 때 (End) 한 번만 내줍니다.
/// 관찰마다 따로 모으므로 다른 게임의 묶음이나 관찰 시작 전의 묶음이 상한을 차지하지 않고, 상한에 닿으면 이른 묶음을 지키고 늦은 것을 버립니다 (시작 스트림이 앞쪽이고, 창 밖의 읽기는 어차피 빼지 않음).
public sealed class SelfReadObservation
{
    private readonly object _gate = new();
    private List<SelfReadBatch>? _batches = [];
    private long _ops;
    private long _dropped;

    internal void Add(SelfReadBatch batch)
    {
        lock (_gate)
        {
            if (_batches is null)
            {
                return;
            }

            if (_ops + batch.Ops.Length > SelfReadLog.MaxOps && _batches.Count > 0)
            {
                _dropped += batch.Ops.Length;
                return;
            }

            _batches.Add(batch);
            _ops += batch.Ops.Length;
        }
    }

    /// 모음을 멈추고 지금까지의 묶음과 상한 때문에 버린 구간 수를 돌려줍니다 (두 번째부터는 빈 목록).
    public (IReadOnlyList<SelfReadBatch> Batches, long DroppedOps) End()
    {
        lock (_gate)
        {
            List<SelfReadBatch> batches = _batches ?? [];
            _batches = null;
            return (batches, _dropped);
        }
    }
}

/// 서비스(시작 스트림·예열)가 스스로 읽은 구간의 기록. 디스크 읽기 이벤트는 읽은 프로세스를 알려 주지 않고 (캐시 관리자의 미리 읽기와 매핑 읽기는 System 이 발행),
/// 서비스의 버퍼드·매핑 읽기는 페이징 읽기로 내려가므로 이벤트만으로는 게임의 읽기와 구분되지 않습니다. 그래서 읽는 쪽이 (시각, 파일, 구간) 을 남기고 요약을 만들 때 SelfReadFilter 가 뺍니다.
/// 프로세스 안에서만 쓰며 저장하지 않습니다. 관찰이 없으면 아무것도 모으지 않습니다. 관찰은 약한 참조로만 잡아, 끝내지 않고 버려져도 남지 않습니다.
public static class SelfReadLog
{
    /// 관찰 하나가 보관하는 읽기 구간 수 상한. 구간 하나가 24 B 라 상한에서 약 12 MB 입니다.
    public const int MaxOps = 1 << 19;

    private static readonly object Gate = new();
    private static readonly List<WeakReference<SelfReadObservation>> Observations = [];

    public static SelfReadObservation Begin()
    {
        var observation = new SelfReadObservation();
        lock (Gate)
        {
            Observations.RemoveAll(w => !w.TryGetTarget(out _));
            Observations.Add(new WeakReference<SelfReadObservation>(observation));
        }

        return observation;
    }

    public static void Note(IReadOnlyList<ReadOp> ops, DateTimeOffset startedUtc, DateTimeOffset endedUtc)
    {
        if (ops.Count == 0)
        {
            return;
        }

        SelfReadObservation[] active;
        lock (Gate)
        {
            active = [.. Observations.Select(w => w.TryGetTarget(out SelfReadObservation? o) ? o : null).OfType<SelfReadObservation>()];
        }

        if (active.Length == 0)
        {
            return;
        }

        var batch = new SelfReadBatch(startedUtc, endedUtc, [.. ops]);
        foreach (SelfReadObservation observation in active)
        {
            observation.Add(batch);
        }
    }
}
