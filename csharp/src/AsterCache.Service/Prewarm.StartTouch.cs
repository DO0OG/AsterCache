using AsterCache.Core.Logging;
using AsterCache.Core.Settings;
using AsterCache.Windows;

namespace AsterCache.Service;

/// Prewarmer 가 게임 실행 파일이 시작할 때 그 게임이 예열해 둔 캐시를 메모리 안에서 다시 만지는 곳.
/// 게임은 시작 뒤 스스로 9~10 GiB 를 잡아 대기 목록의 오래된 페이지부터 넘어가는데, 여러 게임의 캐시가 함께 있으면 "더 최근에 만진 쪽" 이 살아남습니다.
/// 곧 플레이할 게임의 예측 (WarmRank) 이 틀려도 실제로 시작한 게임의 캐시가 먼저 밀려나지 않게, 범위·순서·표식 갱신은 사다리 (Solar.Order) 와 같게 하고 디스크는 읽지 않습니다.
public sealed partial class Prewarmer
{
    /// 같은 게임의 시작 만지기 사이 최소 간격: 시작 스트림 쿨다운 (GameDispatcher.Cooldown)·단계 스트림 쿨다운과 같은 60 초. 런처와 본체가 이어 뜨는 사이에는 게임이 아직 메모리를 잡기 전이라 다시 만져도 순서가 거의 달라지지 않습니다.
    public static readonly TimeSpan StartTouchMinGap = TimeSpan.FromSeconds(60);

    private readonly Dictionary<string, DateTimeOffset> _startTouchedAt = new(StringComparer.OrdinalIgnoreCase); // 게임별 마지막 시작 만지기가 끝난 시각, _gate 로 보호
    private readonly HashSet<string> _startTouching = new(StringComparer.OrdinalIgnoreCase); // 시작 만지기가 도는 게임 (겹쳐 돌지 않게), _gate 로 보호
    private readonly HashSet<string> _warmAtServiceStart = new(StringComparer.OrdinalIgnoreCase); // 서비스 시작 예열이 이미 warm 이라 건너뛴 게임 (그때 예열한 기록 _last 가 없어도 메모리에 목록이 있음), _gate 로 보호

    /// 등록 실행 파일 exe 가 시작했을 때 부릅니다 (호출한 쪽이 따로 돌리고, 시작 스트림이 허용되는 게임에서만 부름). 메모리에 예열한 목록이 있는 게임 (이번 부팅 뒤 예열했거나, 서비스 시작 때 이미 warm 이라 건너뛰었거나, 사다리 표식이 있음: 서비스 시작 사다리가 도는 게임과 같음) 의 읽은 범위를 사다리 순서로 다시 만집니다.
    /// 읽기 중재의 시작 스트림과 같은 대여 안에서 돕니다: 이 게임이 끝나거나 다른 게임이 같은 디스크에서 시작하거나 서비스가 멈추면 취소됩니다. 대여는 서로 막지 않으므로 (DiskReadCoordinator.TryBegin 은 활성 대여 수를 보지 않음) 같은 게임의 시작·단계 스트림과 함께 유효하고,
    /// 시작·단계 스트림이 항상 우선하도록 읽는 스레드의 I/O 우선순위를 낮춰 (디스크 읽기가 섞여도 게임과 스트림의 읽기가 먼저) 돌립니다.
    /// 캐시에 없는 범위 (이 프로세스의 디스크 읽기 수가 허용치를 넘는 곳) 를 만나면 거기서 멈추고 (Solar.Order) 나머지는 재예열 점검에 맡기되, 빠진 것이 확인되거나 이미 알려져 있으면 메모리에 남은 앞부분만 사다리 순서로 만집니다 (Solar.TouchFront). 파일을 읽기만 하고 게임·런처 프로세스는 열지 않습니다.
    internal void TouchAtGameStart(GameSettings game, string exe)
    {
        TouchOnceAtGameStart(game, exe);
        if (_solar.Enabled)
        {
            KeepCacheWhilePlaying(game, exe); // 시작 만지기가 어떻게 끝났든 (대여 양보·재배치 중 같은 일시적 사유 포함) 같은 작업 안에서 게임이 끝날 때까지 주기마다 이어 만짐: 회차마다 게이트를 다시 봄
        }
    }

    private void TouchOnceAtGameStart(GameSettings game, string exe)
    {
        if (!_solar.Enabled || Relocating(game.Name))
        {
            return;
        }

        lock (_gate)
        {
            if (!(_last.ContainsKey(game.Name) || _warmAtServiceStart.Contains(game.Name) || _solar.HasMark(game.Name)) || _startTouching.Contains(game.Name)) // 메모리에 둔 목록이 없음
            {
                return;
            }

            if (_startTouchedAt.TryGetValue(game.Name, out DateTimeOffset endedAt) && _now() - endedAt is { } since && since >= TimeSpan.Zero && since < StartTouchMinGap)
            {
                _log.Info($"{game.Name}: cache touch at game start skipped ({exe}: last touch {(int)since.TotalSeconds} s ago, within {(int)StartTouchMinGap.TotalSeconds} s)");
                return;
            }

            _ = _startTouching.Add(game.Name);
        }

        bool touched = false;
        try
        {
            if (_solar.HasNothingToTouch(game.Name))
            {
                return; // 목록이 전부 스파스 파일
            }

            if (LadderReads(CoveredPart(game)) is not { Count: > 0 } ladder)
            {
                return;
            }

            using DiskReadCoordinator.Lease? lease = _coordinator?.TryBegin(game.OriginRoot, "start touch", default, game.OriginRoot, allowWhileYielded: true, preserveGameKey: game.Name);
            if (_coordinator is not null && (lease is null || !lease.IsValid))
            {
                _log.Info($"{game.Name}: cache touch at game start skipped ({exe}: disk yielded)");
                return;
            }

            lease?.NoteReadIssue("start touch", 0, false); // 양보 진단 로그에 이 단계가 보이게
            touched = true;
            using var activity = ServiceActivity.Begin(ServiceOp.StartTouch); // 초 단위 기록의 동작 표시
            CancellationToken stop = lease?.Token ?? default;
            _ = _env.SetReadIoLow(true); // 읽는 스레드가 이 호출 안에서만 낮은 우선순위: 시작·단계 스트림과 게임의 읽기에 양보 (이 스레드에서 되돌림)
            Thread reader = Thread.CurrentThread;
            ThreadPriority cpuBefore = reader.Priority;
            reader.Priority = ThreadPriority.BelowNormal; // 목록 끝까지 (사다리 약 12 GiB) 만지는 동안 게임 로딩과 CPU 를 다투지 않게: 이 호출 안에서만 낮추고 되돌림 (메모리 우선순위는 낮추지 않음: 만진 페이지가 먼저 버려짐)
            // 속도 제한은 두지 않습니다: 이 만지기는 게임이 메모리를 가져가기 전에 끝나야 가장 중요한 블록이 가장 새 자리에 서고, 게임 실행 직후는 화면 프레임이 중요한 구간이 아닙니다 (로딩).
            // 대신 완료 줄에 걸린 시간·CPU 시간·뷰 수·조회 횟수를 남겨 프레임 간격 기록과 대조할 수 있게 합니다.
            try
            {
                lock (_touchSerial) // 붙들기·점검과 같이 이 프로세스의 하드 폴트 누계를 쓰므로 겹치지 않게
                {
                    // 빠짐이 알려진 게임은 사다리 순서로 시도하지 않음 (덜 중요한 쪽의 빈 곳에서 먼저 멈춰 디스크를 읽고 앞부분을 만지지 못함)
                    TouchEnd? end = _solar.HasMissing(game.Name) ? TouchEnd.NeedsDiskReads : _solar.Order(game, ladder, stop, startExe: exe);
                    if (end == TouchEnd.NeedsDiskReads && _solar.HasMissing(game.Name))
                    {
                        _ = _solar.TouchFront(game, ladder, stop, exe);
                    }
                }
            }
            finally
            {
                _ = _env.SetReadIoLow(false);
                reader.Priority = cpuBefore;
            }
        }
        finally
        {
            lock (_gate)
            {
                _ = _startTouching.Remove(game.Name);
                if (touched)
                {
                    _startTouchedAt[game.Name] = _now();
                }
            }
        }
    }
}
