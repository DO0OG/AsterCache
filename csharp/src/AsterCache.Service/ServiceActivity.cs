namespace AsterCache.Service;

/// 서비스가 게임이 실행되는 동안 하는 일의 종류 (플레이의 초 단위 기록이 그 초에 돌던 것을 짧은 표시로 남김).
[Flags]
internal enum ServiceOp
{
    None = 0,
    StartTouch = 1, // 게임 시작 때의 만지기 (touch)
    Keep = 2, // 실행 중 캐시 유지 회차 (keep)
    Stream = 4, // 시작 스트림·이후 실행 파일의 단계 스트림 (stream)
    Trim = 8, // 배경 양보의 작업 집합 비우기 (trim)
    Supplement = 16, // 보충 예열 (supp)
    Hold = 32, // 유지 목록을 뷰로 붙드는 중 (hold)
    Check = 64, // 붙든 범위를 다시 읽어 점검하는 중 (check)
    Release = 128, // 가용 메모리가 하한 아래라 붙든 것을 내놓는 중 (release)
}

/// 서비스의 동작 표시: 하는 일의 앞뒤를 `using` 으로 감싸면, 표본 루프가 `Take` 로 지난 호출 뒤 한 번이라도 돌았던 것을 읽습니다. 기록만 하고 동작에는 영향이 없습니다.
/// 같은 종류가 겹쳐 돌 수 있어 (예: 두 게임의 만지기) 종류마다 횟수를 셉니다. 잠금 없이 Interlocked 만 씁니다. 서비스는 `Shared` 하나를 쓰고, 시험은 따로 만든 것을 씁니다.
internal sealed class ServiceActivity
{
    private static readonly (ServiceOp Op, string Label)[] Ops =
    [
        (ServiceOp.StartTouch, "touch"),
        (ServiceOp.Keep, "keep"),
        (ServiceOp.Stream, "stream"),
        (ServiceOp.Trim, "trim"),
        (ServiceOp.Supplement, "supp"),
        (ServiceOp.Hold, "hold"),
        (ServiceOp.Check, "check"),
        (ServiceOp.Release, "release"),
    ];

    public static readonly ServiceActivity Shared = new(); // Ops 다음에 만듦 (생성자가 Ops 의 길이를 씀)

    private readonly int[] _active = new int[Ops.Length];
    private int _seen; // 지난 Collect 뒤 시작했거나 돌고 있던 종류 (비트)

    internal readonly struct Scope(ServiceActivity owner, int index) : IDisposable
    {
        public void Dispose() => _ = Interlocked.Decrement(ref owner._active[index]);
    }

    /// 이 종류의 일이 시작됐음을 표시합니다: 반환값을 `using` 으로 감싸 끝에서 풀리게 하십시오.
    public static Scope Begin(ServiceOp op) => Shared.Enter(op);

    /// 지난 호출 뒤 돌았던 종류를 `keep+stream` 처럼 붙여 돌려주고 (없으면 빈 문자열), 지금 돌고 있는 것은 다음 호출에서도 다시 나옵니다.
    public static string Take() => Shared.Collect();

    public Scope Enter(ServiceOp op)
    {
        int index = Array.FindIndex(Ops, o => o.Op == op);
        _ = Interlocked.Increment(ref _active[index]);
        _ = Interlocked.Or(ref _seen, (int)op);
        return new Scope(this, index);
    }

    public string Collect()
    {
        int seen = Interlocked.Exchange(ref _seen, 0);
        for (int i = 0; i < Ops.Length; i++)
        {
            if (Volatile.Read(ref _active[i]) > 0)
            {
                seen |= (int)Ops[i].Op;
            }
        }

        return seen == 0 ? string.Empty : string.Join('+', Ops.Where(o => (seen & (int)o.Op) != 0).Select(o => o.Label));
    }
}
