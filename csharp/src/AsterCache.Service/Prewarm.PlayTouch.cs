using AsterCache.Core.Logging;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Windows;

namespace AsterCache.Service;

/// Prewarmer 가 게임이 실행되는 동안 그 게임이 예열해 둔 캐시를 메모리 안에서 다시 만져 유지하는 곳 (붙들지 않는 게임, 곧 안티치트 게이트가 고정을 막는 게임·파일 쓰기 정보가 없는 게임·memory.keep_hold 가 꺼진 경우).
/// 경기 중에도 게임이 새로 읽는 양 (분당 0.8~1.7 GiB, 시작 뒤 180 초에 8.5 GiB 가 재사용된 기록도 있음) 에 밀려 예열한 페이지가 대기 목록에서 빠지므로, 시작 만지기 (TouchAtGameStart) 한 번으로는 경기 중반에 이미 없습니다.
/// 회차는 대기 목록 재사용 누계가 문턱을 넘을 때 바로 돕니다 (KeepPollEvery 마다 누계만 확인). 이 플레이 동안 "메모리에 있다고 확인된 집합" 을 유지하고 회차는 그 집합만 덜 중요한 것부터 다시 만집니다 (순서 패스, 가장 중요한 것이 마지막, 속도 제한도 쉼도 없음).
/// 집합은 찾기 패스 (ResidentFinder) 가 만듭니다: 가장 중요한 것부터 걸으며 메모리에 있는 범위를 모으고, 빠진 조각은 하드 폴트 조회로 싸게 알아챕니다. 한 지점만 빠졌으면 그 조각만 구멍으로 기억하고 계속하고,
/// 이어진 두 지점이 빠졌으면 경계로 보아 그 뒤 (덜 중요한 쪽) 는 집합에 넣지 않습니다. 순서 패스에서 디스크 읽기가 난 범위는 그 범위 하나만 집합에서 뺍니다.
/// 집합 밖을 다시 확인하는 찾기는 순서 패스를 먼저 돈 뒤에, 디스크 읽기를 회차 KeepRoundDiskCapBytes·플레이 KeepPlayDiskCapBytes 안에서 중요한 순서로 조금씩 합니다 (상한에 닿아도 순서 패스는 항상 돎). 빠진 부분을 채우는 일은 게임이 끝난 뒤 재예열에 맡깁니다.
/// 목록은 플레이마다 한 번만 계산하고 입력 파일이 바뀐 때만 다시 계산합니다.
public sealed partial class Prewarmer
{
    /// 게임이 실행되는 동안 대기 목록 재사용 누계를 확인하는 주기 (조회는 프로세스를 열지 않는 시스템 통계 한 번).
    public static readonly TimeSpan KeepPollEvery = TimeSpan.FromSeconds(5);

    /// 회차 사이의 최소 간격: 재사용이 몰려도 이 간격보다 자주 돌지 않습니다.
    public static readonly TimeSpan KeepMinInterval = TimeSpan.FromSeconds(10);

    /// 대기 목록을 읽을 수 없을 때 (재사용 누계를 모름) 회차를 도는 고정 주기: 배경 양보가 세션 중 유지하는 주기 (BackgroundYield.MaintainEvery) 와 같은 2 분.
    public static readonly TimeSpan PlayTouchEvery = BackgroundYield.MaintainEvery;

    /// 회차를 부르는 재사용 문턱의 상한: 1 GiB. 문턱은 min(이 값, 우선순위 5~7 대기 양에서 유지 목록 크기를 뺀 값의 절반) 입니다: 대기 목록은 먼저 들어온 페이지부터 재사용되므로 유지 목록 앞에 있는 캐시 (다른 데이터) 가 다 먹히기 전에 다시 만져야 하고,
    /// 그 앞의 양이 작을수록 문턱도 낮아져 자주 돕니다. 반으로 둔 것은 한 회차 (최소 간격 10 초와 만지는 시간) 사이에 늘어날 재사용에 여유를 두려는 것이며 실험으로 고른 값이 아닙니다 (2026-11-21 플레이 기록에서 시작 뒤 180 초에 8.5 GiB 재사용).
    public const long KeepReuseMaxThresholdBytes = 1L << 30;

    /// 문턱의 하한: 앞의 여유가 없어도 이보다 작은 재사용마다 돌지 않습니다 (최소 간격과 함께 CPU·메모리 대역을 묶음).
    public const long KeepReuseMinThresholdBytes = 64L << 20;

    /// 한 회차의 집합 밖 다시 확인 (찾기 패스) 이 디스크에서 읽는 양의 상한: 8 MiB. 빠진 조각 하나를 알아채는 데 읽는 양이 조각 하나 (1 MiB 이하) 입니다. 넘으면 새 범위를 더 확인하지 않고 남은 범위는 이번에 만지지 않으며, 집합의 순서 패스는 상한과 무관하게 항상 돕니다.
    public const long KeepRoundDiskCapBytes = 8L << 20;

    /// 한 플레이에서 집합 밖 다시 확인이 디스크에서 읽는 양의 누적 상한: 64 MiB (보호·순서 패스의 읽기는 세지 않음). 다 쓰면 그 뒤 회차는 집합 밖을 다시 확인하지 않고 집합만 두 패스로 만집니다 (유지를 끝내지 않고, 집합도 비어 있으면 재사용 누계만 확인하고 넘어감).
    public const long KeepPlayDiskCapBytes = 64L << 20;

    /// 보호 패스 (집합을 가장 중요한 것부터 걸으며 만짐) 가 한 회차에 디스크에서 읽는 양의 상한: 8 MiB. 경계 하나를 알아채는 데 읽는 양은 수 MiB 이므로, 이 상한을 넘으면 그 지점을 경계로 보고 뒤 (덜 중요한 쪽) 를 집합에서 뺍니다.
    public const long KeepProtectDiskCapBytes = 8L << 20;

    /// 순서 패스가 한 회차에 디스크에서 읽는 양의 상한: 32 MiB. 넘으면 그 회차의 순서 패스는 거기서 끝납니다 (읽기가 난 범위는 그대로 집합에서 뺌). 근거: 보호 패스가 이미 집합 전체를 가장 중요한 것부터 만진 뒤에 도는 패스라
    /// 중간에 끝나도 가장 중요한 쪽의 보호가 깨지지 않고, 회차 안에서 대기 목록이 급히 줄어 집합이 한꺼번에 밀려난 경우 한 회차에 수백 MiB 를 디스크에서 읽는 것을 막습니다. 이 읽기는 플레이 누적 상한에는 넣지 않습니다.
    public const long KeepOrderDiskCapBytes = 32L << 20;

    /// 집합이 비어 있는 회차 (시작 만지기가 끝까지 가지 못했거나 서비스가 게임 도중 다시 시작함) 의 집합 밖 확인이 읽는 양의 상한: 32 MiB. 이때는 보호·순서 패스가 없어 집합을 처음부터 만들므로 회차 상한을 조금 크게 잡습니다 (플레이 누적 상한은 같음).
    public const long KeepEmptySetRoundDiskCapBytes = 32L << 20;

    /// 한 번에 만지는 양의 상한: 그때 사용 가능한 물리 메모리의 이 비율. 대기 목록은 만진 양과 회차 사이에 게임이 새로 읽는 양을 함께 담아야 하는데,
    /// 경기 중 조사 값은 사용 가능 메모리 약 11 GiB 와 분당 최대 1.7 GiB 라서 만지는 양이 7.6 GiB 이하여야 합니다. 절반은 그 안 (5.5 GiB) 이고 메모리가 달라져도 같은 비율로 따라갑니다.
    public const double PlayTouchAvailableFraction = 0.5;

    /// 만지는 속도의 상한 (초당): 256 MiB. 더 빠른 PC 가 메모리 대역을 한꺼번에 쓰지 않게 합니다. 실제 게임 파일 실측에서는 한 패스가 1 GiB 에 약 5 초 (초당 약 200 MiB) 라 페이지를 옮기는 속도가 이 상한보다 먼저 한도가 됩니다.
    public const long PlayTouchBytesPerSecond = 256L << 20;

    private readonly HashSet<string> _playTouching = new(StringComparer.OrdinalIgnoreCase); // 실행 중 캐시 유지가 도는 게임 (한 게임에 하나만), _gate 로 보호
    private readonly CancellationTokenSource _playStop = new(); // 서비스 종료 때 유지를 멈추는 신호 (Shutdown 이 취소)

    internal void StopPlayTouch() => _playStop.Cancel();

    /// 경기 안 로딩 (실행 파일 시작 없이 읽기·그래픽 신호로 잡힌 로딩) 중 회차의 보호 패스와 (집합이 비었을 때의) 찾기 패스가 디스크에서 읽는 양의 상한: 2 MiB. 빠진 조각 하나를 알아채는 데 읽는 양 (조각 하나가 1 MiB 이하) 정도입니다.
    /// 상한은 읽기 수를 조회할 때만 보므로 (빠진 조각이 의심 조각이면 곧바로, 아니면 FaultCheckBytes 64 MiB 마다) 실제 읽기는 보통 2~4 MiB 이고 최악은 한 조회 간격 (64 MiB) 입니다. 넘어도 아직 확인하지 못한 범위를 집합에서 빼지 않습니다.
    /// 이 로딩 중의 순서 패스는 이번 패스가 메모리에 있다고 확인한 범위에만 돌고 디스크 읽기가 난 (경계) 배치나 일반 회차와 같은 읽기 상한 (KeepOrderDiskCapBytes 32 MiB) 에서 끝납니다 (최악 배치 하나 HoldBatchBytes 16 MiB 를 더한 값).
    public const long KeepLoadingProtectDiskCapBytes = 2L << 20;

    /// 경기 안 로딩 중 붙든 범위의 점검이 줄여 간 구간을 디스크에서 다시 읽는 양의 상한: 2 MiB. 읽기 수를 조회할 때만 보므로 (ViewHold.ReadRange, 64 MiB 마다 또는 의심 조각 뒤 8 MiB 이상 만졌을 때) 실제로는 약 8~10 MiB 까지 읽을 수 있고 최악은 64 MiB 입니다.
    /// 넘어도 남은 구간을 붙들기에서 빼지 않습니다 (바뀐 파일 내놓기는 로딩 중에도 그대로 함).
    public const long KeepLoadingCheckDiskCapBytes = 2L << 20;

    /// 게임 안 로딩 판정 (서비스가 PlayRecorder 의 것을 이음, 없으면 로딩으로 보지 않음). 로딩인 동안은 게임이 도는 중의 디스크 읽기 (유지 회차의 집합 밖 찾기와 순서·보호 패스의 읽기, 붙든 범위 점검의 다시 읽기) 를 작은 상한으로 줄이거나 미룹니다:
    /// 로딩 구간의 디스크는 이미 포화이고 서비스의 읽기는 게임 요청 사이에 탐색을 더합니다. 메모리에 이미 있는 범위를 다시 만지는 일은 로딩 중에도 돕니다 (대기 목록 재사용이 가장 큰 때라 멈추면 지키던 범위가 밀려남).
    /// 시작 만지기·처음 붙들기는 게임 실행 파일 시작 때의 일이라 이 판정과 무관하게 돕니다. 서비스가 게임 드라이브에서 읽은 양은 ServiceReads 에 더해 로딩 판정이 게임의 읽기에서 뺍니다.
    internal LoadPhaseBoard? LoadPhases { get; set; }

    private void NoteServiceRead(GameSettings game, long diskBytes) => LoadPhases?.ServiceReads.Add(game.OriginRoot, diskBytes);

    /// 시작 만지기가 끝난 뒤 같은 작업 안에서 부릅니다: 등록 실행 파일이 하나도 실행 중이 아닐 때까지, 먼저 유지 목록을 뷰로 붙들 수 있으면 붙들고 (Prewarm.PlayHold), 아니면 재사용이 문턱을 넘을 때마다 그 게임 목록을 메모리 안에서 다시 만집니다.
    /// 이미 이 게임의 유지가 돌고 있으면 (런처와 본체가 이어 뜬 경우) 바로 끝납니다. 회차마다 읽기 중재의 시작 스트림과 같은 대여를 새로 받아 (그 게임의 끝·다른 게임의 시작으로 취소) 돌고, 서비스가 멈추면 기다리던 것도 바로 끝납니다.
    private void KeepCacheWhilePlaying(GameSettings started, string exe)
    {
        lock (_gate)
        {
            if (!_playTouching.Add(started.Name))
            {
                return;
            }
        }

        int rounds = 0;
        int polls = 0;
        int lastRoundPoll = 0; // 시작 만지기가 끝난 때 (이 뒤 KeepMinInterval 이 지나야 첫 회차)
        int fallbackPolls = (int)(PlayTouchEvery.Ticks / KeepPollEvery.Ticks);
        int minPolls = (int)Math.Ceiling((double)KeepMinInterval.Ticks / KeepPollEvery.Ticks);
        KeepMemory memory = KeepMemoryOf(started.Name); // 이 플레이에서 알게 된 빠진·메모리에 있는 범위 (같은 게임의 다른 실행 파일이 떠도 이어 씀)
        long? reuseAtLastRound = _env.Lists()?.CacheRepurposedBytes; // 시작 만지기가 끝난 때의 대기 목록 재사용 누계
        var keep = new KeepList(); // 이 플레이의 유지 목록 (입력이 바뀔 때만 다시 계산)
        bool noted = false; // 이 유지가 로그를 한 줄이라도 남겼는가 (끝 줄을 남길지)
        string? lastSkip = null;
        string? lastHoldSkip = null;
        string ended = string.Empty; // 끝 줄에 붙일 사유
        try
        {
            CancellationToken stop = _playStop.Token;
            // 유지 목록을 뷰로 붙들 수 있으면 (memory.keep_hold, Prewarm.PlayHold) 게임이 끝날 때까지 거기서 돌고, 붙들 수 없는 게임은 아래의 다시 만지기로 남음 (일시적 사유면 주기마다 다시 시도)
            HoldOutcome held = memory.Finished ? HoldOutcome.Done : _env.KeepHoldEnabled() ? HoldWhilePlaying(started, exe, keep, ref lastHoldSkip) : HoldOutcome.No; // 빠진 범위가 대부분이라 끝낸 플레이는 다른 실행 파일이 떠도 다시 시작하지 않음
            bool listReady = false;
            while (held != HoldOutcome.Done)
            {
                _env.Sleep(KeepPollEvery, stop);
                polls++;
                if (stop.IsCancellationRequested || GameOf(started.Name) is not { } game || !_env.GameRunning(game))
                {
                    break; // 서비스가 멈추거나 설정에서 빠지거나 등록 실행 파일이 모두 끝남
                }

                if (held == HoldOutcome.Retry && polls % fallbackPolls == 0 && _env.KeepHoldEnabled() && (held = HoldWhilePlaying(started, exe, keep, ref lastHoldSkip)) == HoldOutcome.Done)
                {
                    break;
                }

                if (!listReady)
                {
                    listReady = true;
                    using var low = new LowPriorityReader(_env);
                    _ = RefreshKeepList(game, keep); // 첫 회차의 문턱이 실제 목록 크기로 계산되게 첫 확인 때 목록을 계산 (게임이 실행 중일 때만: 그 전에 끝나면 아무것도 하지 않음)
                }

                MemoryLists? lists = _env.Lists();
                long? growth = null;
                bool due;
                if (lists is { } now && reuseAtLastRound is { } reuseBefore)
                {
                    growth = now.CacheRepurposedBytes - reuseBefore;
                    due = polls - lastRoundPoll >= minPolls && growth >= ReuseThreshold(now, keep.Bytes);
                }
                else
                {
                    reuseAtLastRound ??= lists?.CacheRepurposedBytes; // 처음 읽을 수 있게 된 값을 기준으로
                    due = polls - lastRoundPoll >= fallbackPolls; // 재사용 누계를 모르면 지금처럼 2 분마다
                }

                if (!due)
                {
                    continue; // 대기 목록에서 밀려난 것이 (거의) 없음: 유지 대상도 밀려나지 않았으니 다시 만질 까닭이 없음
                }

                bool loading = LoadPhases?.Defers(game.Name, "keep round disk reads") == true; // 로딩 중: 회차는 돌되 디스크 읽기를 줄임
                (string Reason, string Detail)? skip = KeepRound(game, exe, keep, growth, loading, out bool touched, out bool empty, out string? endNow);
                if (touched)
                {
                    (rounds, noted, lastSkip, lastRoundPoll) = (rounds + 1, true, null, polls);
                    reuseAtLastRound = _env.Lists()?.CacheRepurposedBytes;
                    // 유지는 게임이 끝날 때까지 이어집니다: 목록이 메모리에 하나도 없는 회차가 이어져도 끝내지 않음 (집합 밖 다시 확인은 플레이 누적 상한이 막고, 집합이 비고 상한도 다 쓰면 회차가 할 일이 없어 건너뜀)
                }
                else if (skip is { } why && why.Reason != lastSkip)
                {
                    _log.Info($"{game.Name}: cache keeping while playing skipped ({exe}: {why.Detail})"); // 사유가 바뀔 때만 한 줄
                    (noted, lastSkip) = (true, why.Reason);
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _ = _playTouching.Remove(started.Name);
            }

            ForgetKeepMemoryIfStopped(started.Name);

            if (noted)
            {
                _log.Info($"{started.Name}: cache keeping ended after {rounds} rounds{ended}");
            }
        }
    }

    /// 회차를 부르는 재사용 문턱: min(KeepReuseMaxThresholdBytes, 우선순위 5 대기 양에서 유지 목록 크기를 뺀 값의 절반), 하한 KeepReuseMinThresholdBytes.
    /// 만진 페이지는 우선순위 5 로 들어가고 6·7 은 5 가 다 쓰인 뒤에 재사용되므로 유지 목록 앞에 있는 양은 우선순위 5 만입니다 (우선순위 5 의 값을 모르면 5~7 의 합으로 대신함).
    internal static long ReuseThreshold(MemoryLists lists, long listBytes) =>
        Math.Clamp(((lists.Priority5StandbyBytes >= 0 ? lists.Priority5StandbyBytes : lists.CacheStandbyBytes) - listBytes) / 2, KeepReuseMinThresholdBytes, KeepReuseMaxThresholdBytes);

    // 게임이 더 실행 중이 아니거나 서비스가 멈췄으면 그 게임의 기억 (빠진·메모리에 있는 범위, 디스크 읽기 누적) 을 버립니다.
    private void ForgetKeepMemoryIfStopped(string name)
    {
        bool running = !_playStop.IsCancellationRequested && GameOf(name) is { } game && _env.GameRunning(game);
        if (!running)
        {
            lock (_gate)
            {
                _ = _keepMemory.Remove(name);
            }
        }
    }

    /// 한 플레이의 유지 목록: 입력 (계획·트레이스·순위 목록 파일, 부분 예열 범위, 스파스 표시) 이 그대로인 동안 회차마다 다시 계산하지 않고 이 목록을 씁니다.
    private sealed class KeepList
    {
        public string? Stamp; // 목록을 만든 때의 입력 표지 (null: 표지를 읽지 못해 재사용하지 않음)
        public IReadOnlyList<ReadOp> All = []; // 사다리 대상 (읽은 순서)
        public IReadOnlyList<ReadOp> Ordered = []; // All 의 중요도 순서 (순위 목록이 없으면 All 과 같은 객체)
        public long Bytes; // 목록의 크기 (재사용 문턱 계산용)
    }

    // 유지 목록이 있으면 입력이 바뀌지 않았는지 보고 (메타데이터 조회뿐), 없거나 바뀌었으면 새로 계산합니다. 계획할 수 없으면 false.
    private bool RefreshKeepList(GameSettings game, KeepList keep)
    {
        GameSettings covered = CoveredPart(game);
        string? input = _env.ListStamp(covered);
        string? stamp = input is null ? null : $"{input}|{covered.MaxStreamMib}|{covered.SkipSparse}";
        if (stamp is not null && stamp == keep.Stamp && keep.All.Count > 0)
        {
            return true;
        }

        if (LadderReads(covered) is not { Count: > 0 } all)
        {
            return false;
        }

        keep.Stamp = stamp;
        keep.All = all;
        keep.Ordered = _solar.ImportanceOrder(game, all);
        keep.Bytes = keep.Ordered.Sum(o => o.Length);
        return true;
    }

    /// 한 회차. 시작 만지기와 같은 게이트 (사다리 켜짐, 재배치 중 아님, 메모리에 둔 목록, 만질 것) 를 지나야 하고, 사용 가능 메모리가 min_free_ram_mib 아래면 건너뜁니다.
    /// 읽는 스레드는 I/O 우선순위와 CPU 우선순위를 이 호출 안에서만 낮춥니다 (목록을 다시 계산하는 일도 낮은 CPU 우선순위로 돎). 스레드 배경 모드는 페이지의 메모리 우선순위까지 낮춰 만진 페이지가 낮은 우선순위 대기 목록으로 가므로 쓰지 않습니다.
    /// 만지는 일은 시작 만지기·붙들기와 같은 직렬화 잠금 (_touchSerial) 안에서 돕니다: 이 프로세스의 하드 폴트 누계로 경계를 가리므로 다른 만지기와 겹치면 서로의 읽기가 섞입니다.
    /// 로그를 남길 사유로 건너뛰면 그 사유 (같은 사유가 이어지는지 가르는 값, 로그에 붙는 수치) 를 돌려주고, 로그 없이 건너뛰었거나 만졌으면 null. empty: 목록이 메모리에 하나도 남아 있지 않아 만진 것이 없음. endNow: 이 플레이의 유지를 끝낼 사유 (줄 끝에 붙는 말).
    private (string Reason, string Detail)? KeepRound(GameSettings game, string exe, KeepList keep, long? reuseGrowth, bool loading, out bool touched, out bool empty, out string? endNow)
    {
        (touched, empty, endNow) = (false, false, null);
        if (!_solar.Enabled || Relocating(game.Name) || _solar.HasNothingToTouch(game.Name))
        {
            return null;
        }

        lock (_gate)
        {
            if (!(_last.ContainsKey(game.Name) || _warmAtServiceStart.Contains(game.Name) || _solar.HasMark(game.Name))) // 메모리에 둔 목록이 없음
            {
                return null;
            }
        }

        KeepMemory known = KeepMemoryOf(game.Name);
        if (known.ResidentBytes() == 0 && known.DiskBytes >= KeepPlayDiskCapBytes)
        {
            return null; // 집합이 비어 있고 집합 밖 확인의 플레이 상한도 다 씀: 회차가 할 일이 없음 (재사용 누계 확인만 하고 넘어감)
        }

        long availMib = _env.AvailableRamMib();
        if (availMib < game.Prewarm.MinFreeRamMib)
        {
            return ("low memory", $"available {availMib} MiB < reserve {game.Prewarm.MinFreeRamMib} MiB");
        }

        using var low = new LowPriorityReader(_env);
        if (!RefreshKeepList(game, keep))
        {
            return null;
        }

        long cap = (long)(availMib * PlayTouchAvailableFraction) << 20;
        IReadOnlyList<ReadOp> reads = PrefetchStreamer.Head(keep.Ordered, cap); // 목록 앞쪽 (가장 중요한 부분) 만
        using DiskReadCoordinator.Lease? lease = _coordinator?.TryBegin(game.OriginRoot, "play touch", default, game.OriginRoot, allowWhileYielded: true, preserveGameKey: game.Name);
        if (_coordinator is not null && (lease is null || !lease.IsValid))
        {
            return ("disk yielded", "disk yielded");
        }

        lease?.NoteReadIssue("play touch", 0, false); // 양보 진단 로그에 이 단계가 보이게
        using var activity = ServiceActivity.Begin(ServiceOp.Keep); // 초 단위 기록의 동작 표시
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lease?.Token ?? default, _playStop.Token);
        KeepMemory memory = KeepMemoryOf(game.Name);
        RememberTally t = TouchRemembering(reads, memory, memory.DiskBytes < KeepPlayDiskCapBytes, KeepRoundDiskCapBytes, linked.Token, loading); // 직렬화 잠금은 두 패스가 묶음마다 직접 잡고 놓음. 경기 안 로딩 중이면 읽기를 줄임 (TouchRemembering 설명)
        NoteServiceRead(game, t.Disk);

        if (linked.IsCancellationRequested)
        {
            _log.Info($"{game.Name}: cache keeping while playing stopped ({exe}) after {t.InMemory >> 20} MiB");
            return null;
        }

        touched = true;
        empty = t.InMemory <= 0 && t.Total > 0 && !t.FindSkipped; // 확인할 수 있었는데 메모리에 하나도 없던 회차만 (플레이 상한 뒤 집합이 비어 할 일이 없는 회차는 아님)
        _solar.NoteKept(game.Name, t.InMemory);
        string probe;
        lock (_touchSerial)
        {
            probe = t.Boundaries > 0 && !loading ? HoldProbe(reads, t.Total, linked.Token) : string.Empty; // 표본 확인 (기록용, 디스크를 읽으므로 로딩 중에는 건너뜀): 다른 만지기와 하드 폴트 누계가 섞이지 않게 잠금 안
        }

        string trigger = reuseGrowth is { } growth ? $", called by {growth >> 20} MiB of standby cache reuse" : ", called by the fixed period (standby cache reuse unknown)";
        _log.Info($"{game.Name}: cache kept while playing ({exe}): {t.InMemory >> 20} MiB of {t.Total >> 20} MiB in memory (found {t.Found >> 20} MiB, ordered {t.Ordered >> 20} MiB), in {t.Seconds:F1} s (cpu {t.Cpu:F1} s, {t.Maps} views, {t.Checks} checks), {t.Disk >> 20} MiB read from disk ({t.HardFaults} hard faults){(t.Errors > 0 ? $", {t.Errors} errors" : string.Empty)}, {t.Boundaries} new missing stretches remembered ({memory.MissingBytes >> 20} MiB of the {memory.ListBytes >> 20} MiB list in all){TallyDetail(t)}{(t.CapReached ? ", disk read cap of the round reached" : string.Empty)}{(t.Left > 0 ? $", {t.Left >> 20} MiB left unconfirmed" : string.Empty)}{(loading ? ", disk reads limited (loading phase)" : string.Empty)}{(t.FindSkipped ? ", no new range looked for (disk reads of the play at the cap)" : string.Empty)}{trigger}{probe}");
        return null;
    }

    /// 범위를 StandbyTouch.ViewBytes 씩 끊어 만지고, 한 덩어리를 만지는 데 걸린 시간이 PlayTouchBytesPerSecond 로 만졌을 때보다 짧으면 그 차이만큼 쉽니다.
    /// 디스크에서 읽힌 양은 덩어리를 넘어 합쳐서 StandbyTouch.DiskReadStopBytes 를 넘으면 멈춥니다 (한 번에 만질 때와 같은 한도). 취소·멈춤 사유는 덩어리의 결과를 그대로 따릅니다.
    internal TouchResult ThrottledTouch(IReadOnlyList<ReadOp> ranges, CancellationToken ct)
    {
        TouchResult total = new(0, 0, 0, 0, TouchEnd.Completed);
        foreach (List<ReadOp> batch in Batches(ranges, StandbyTouch.ViewBytes))
        {
            if (ct.IsCancellationRequested)
            {
                return total with { End = TouchEnd.Canceled };
            }

            TouchResult r = _env.TouchRanges(batch, ct);
            double rest = r.End == TouchEnd.Completed ? r.Bytes / (double)PlayTouchBytesPerSecond - r.Seconds : 0;
            if (rest > 0)
            {
                _env.Sleep(TimeSpan.FromSeconds(rest), ct);
            }

            total = new TouchResult(total.Bytes + r.Bytes, total.Seconds + r.Seconds + Math.Max(0, rest), total.SlowPieces + r.SlowPieces, total.Errors + r.Errors, r.End,
                total.DiskBytes + r.DiskBytes, total.SkippedBytes + r.SkippedBytes, total.CpuSeconds + r.CpuSeconds, total.Maps + r.Maps, total.HardFaults + r.HardFaults, r.Reason, total.Checks + r.Checks, TouchErrors.Combine(total.ErrorKinds, r.ErrorKinds));
            if (total.End == TouchEnd.Completed && total.DiskBytes > StandbyTouch.DiskReadStopBytes)
            {
                return total with { End = TouchEnd.NeedsDiskReads, Reason = StopReason.HardFaults };
            }

            if (total.End != TouchEnd.Completed)
            {
                return total;
            }
        }

        TouchResult tail = StandbyTouch.Settle(); // 회차 끝 꼬리 (마지막 조회 뒤 만진 구간) 의 디스크 읽기도 셈
        total = total with { DiskBytes = total.DiskBytes + tail.DiskBytes, HardFaults = total.HardFaults + tail.HardFaults, Checks = total.Checks + tail.Checks };
        return tail.End == TouchEnd.Completed ? total : total with { End = tail.End, Reason = tail.Reason };
    }

    // 범위를 순서대로 batchBytes 씩 끊습니다 (걸친 범위는 잘라서).
    private static IEnumerable<List<ReadOp>> Batches(IReadOnlyList<ReadOp> ranges, long batchBytes)
    {
        var batch = new List<ReadOp>();
        long room = batchBytes;
        foreach (ReadOp range in ranges)
        {
            for (long done = 0; done < range.Length;)
            {
                long n = Math.Min(room, range.Length - done);
                batch.Add(new ReadOp(range.Path, range.Offset + done, n));
                (done, room) = (done + n, room - n);
                if (room == 0)
                {
                    yield return batch;
                    batch = [];
                    room = batchBytes;
                }
            }
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }
}
