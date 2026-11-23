using System.ComponentModel;
using AsterCache.Core.Logging;

namespace AsterCache.Service;

/// Windows 예약 드라이브 최적화 (defrag.exe) 가 도는 동안 서비스의 재배치를 미루기 위한 실행 감지.
/// 프로세스 목록의 이름만 봅니다 (프로세스를 열지 않음). 시작·끝 모두 같은 목록 조회로 알아채므로 서비스가 시작할 때 이미 돌고 있던 실행도 잡고,
/// 그 경우 시작 시각은 처음 본 때로 칩니다. 어느 드라이브를 처리하는지는 이름만으로 알 수 없어 모든 드라이브의 재배치를 미룹니다.
/// 미루는 시간은 한 번의 실행에 MaxDefer 까지입니다: 최적화가 유휴 시간 대부분을 차지해도 재배치가 계속 밀려나지 않게, 그 뒤에는 기존 조건만으로 판단합니다.
/// 끝난 직후 재배치를 앞당기지는 않습니다 (범위 방식 배치는 최적화가 돌 때마다 되돌려지므로 하루 한도를 같은 파일에 되풀이해 씀).
/// 예외: 통째 배치 계획이 적용된 게임의 배치 검사만 끝난 직후로 앞당깁니다 (LayoutCheckAdvance, 재배치 시작 조건은 그대로).
public sealed class DriveOptimization
{
    public const string ExeName = "defrag.exe";

    /// 한 번의 실행에 대해 재배치를 미루는 최대 시간 (관찰된 실행은 9분 안팎).
    public static readonly TimeSpan MaxDefer = TimeSpan.FromMinutes(30);

    /// 프로세스 목록을 다시 조회하는 최소 간격: 재배치 도중 조각마다 불려도 목록은 이 간격으로만 읽음.
    public static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(10);

    /// 서비스가 쓰는 실제 감지 (Program 이 만듦). 만들기 전이나 시험에서는 null: 미루지 않음.
    public static DriveOptimization? Current { get; set; }

    private readonly Func<bool> _running;
    private readonly Func<DateTimeOffset> _now; // 로그에 찍는 시각 전용 (벽시계는 UTC 보정에 흔들려 간격·상한 판정에는 쓰지 않음)
    private readonly Func<long> _uptimeMs; // 간격·상한 판정용 단조 시계 (BootQuiet 와 같음)
    private readonly ILog _log;
    private readonly object _gate = new();
    private long? _polledMs;
    private bool _looked; // 조회에 성공한 적이 있는가 (없으면 처음 보는 실행은 서비스 시작 때 이미 돌던 것)
    private DateTimeOffset? _startedAt; // 도는 중인 실행을 처음 본 시각 (안 도는 중이면 null), 로그용
    private long _startedMs;
    private bool _startSeen; // 시작을 직접 봤는가 (false = 서비스가 시작할 때 이미 돌고 있었음)
    private bool _capLogged;
    private DateTimeOffset? _lastEnded;

    /// running: 프로세스 목록에 defrag.exe 가 있는가 (조회가 실패하면 예외 Win32Exception, 직전 상태를 유지). now: 로그용 벽시계. uptimeMs: 단조 시계 (없으면 Environment.TickCount64).
    public DriveOptimization(Func<bool> running, ILog log, Func<DateTimeOffset>? now = null, Func<long>? uptimeMs = null)
    {
        _running = running;
        _log = log;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _uptimeMs = uptimeMs ?? (() => Environment.TickCount64);
    }

    /// 실제 환경: 이미 쓰는 프로세스 이름 목록 조회로 봅니다.
    public static DriveOptimization Real(ILog log) => new(() => ProcessSnapshot.IsRunning([ExeName]), log);

    /// 지금 재배치를 미뤄야 하는가: 실행 중이고 처음 본 지 MaxDefer 가 안 지났음. 부를 때 목록을 (간격이 지났으면) 다시 읽음.
    public bool Blocking
    {
        get
        {
            Refresh();
            lock (_gate)
            {
                return _startedAt is not null && Elapsed(_startedMs) < MaxDefer;
            }
        }
    }

    /// 서비스가 본 가장 최근 실행이 끝난 시각 (본 적 없거나 아직 도는 중이면 null). 도는 중에도 직전 실행의 값은 남음.
    public DateTimeOffset? LastEnded
    {
        get
        {
            lock (_gate)
            {
                return _lastEnded;
            }
        }
    }

    /// 시작·끝을 알아채고 로그를 남깁니다 (재적용 틱과 Blocking 이 부름). PollEvery 안에 다시 부르면 아무것도 하지 않습니다.
    public void Refresh()
    {
        lock (_gate)
        {
            DateTimeOffset now = _now();
            long ms = _uptimeMs();
            if (_polledMs is { } polled && Elapsed(polled) < PollEvery)
            {
                return;
            }

            _polledMs = ms;
            bool running;
            try
            {
                running = _running();
            }
            catch (Win32Exception)
            {
                return; // 목록을 못 읽으면 직전 상태 그대로 (다음 간격에 다시)
            }

            bool first = !_looked;
            _looked = true;

            if (running && _startedAt is null)
            {
                (_startedAt, _startedMs, _startSeen, _capLogged) = (now, ms, !first, false);
                _log.Info(_startSeen
                    ? $"drive optimization started at {Stamp(now)} ({ExeName} running), relocation waits for it up to {MaxDefer.TotalMinutes:F0} min"
                    : $"drive optimization already running when the service started (seen at {Stamp(now)}, start time unknown), relocation waits for it up to {MaxDefer.TotalMinutes:F0} min");
            }
            else if (!running && _startedAt is { } started)
            {
                _lastEnded = now;
                _startedAt = null;
                _log.Info($"drive optimization ended at {Stamp(now)} after {(_startSeen ? string.Empty : "at least ")}{Elapsed(_startedMs).TotalSeconds:F0} s (started {Stamp(started)})");
            }
            else if (running && !_capLogged && Elapsed(_startedMs) >= MaxDefer)
            {
                _capLogged = true;
                _log.Info($"drive optimization still running after {MaxDefer.TotalMinutes:F0} min, relocation no longer waits for it");
            }
        }
    }

    private TimeSpan Elapsed(long sinceMs) => TimeSpan.FromMilliseconds(_uptimeMs() - sinceMs);

    public static string Stamp(DateTimeOffset t) => $"{t.ToUniversalTime():HH:mm:ss} UTC";
}
