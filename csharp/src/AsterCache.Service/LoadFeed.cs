using System.Globalization;

namespace AsterCache.Service;

/// 그 초에 서비스가 게임 드라이브에서 일으킨 읽기 (계상값이며 물리 디스크 읽기의 실측이 아님): ServiceBytes 는 유지·붙들기·점검·시작 만지기·스파스 유지와 스트림의 스파스 버퍼드 읽기가 디스크에서 읽은 것으로 센 바이트,
/// StreamRequestedBytes·StreamAcceptedBytes 는 시작·단계 스트림이 낸 묶음의 요청 바이트와 선읽기 호출이 받아들인 바이트 (받아들여졌다고 상주한다는 뜻은 아님).
internal readonly record struct ServiceReadSecond(long ServiceBytes, long StreamRequestedBytes, long StreamAcceptedBytes);

/// 한 플레이의 로딩 판정 입력 공급: 표본 루프가 1초마다 부르면 그 초의 표본 (게임 드라이브 읽기 증가분 - 서비스가 그 드라이브에서 직접 읽은 증가분, 그래픽 값, 실행 파일 시작) 을 만들어 판정 (LoadPhaseBoard) 에 넣고 상태를 돌려줍니다 (표본 루프 스레드 하나에서만 부름).
/// 드라이브 읽기는 표본 루프가 이미 읽는 디스크 성능 값의 앞뒤 차이이고, 서비스의 읽기는 서비스가 읽을 때마다 세는 누계 (ServiceDiskReads, 드라이브별) 의 앞뒤 차이입니다: 새 조회가 없습니다.
/// 그래픽 값은 raw 에 있을 때만 씁니다: raw 는 프레임 세션이 열린 (안티치트 게이트를 지난) 플레이에서 초 단위 기록이 켜져 있을 때만 읽히므로, 그렇지 않은 게임에서는 디스크 규칙만 돕니다.
/// 기록용으로는 이 게임의 경로별 계상 (ServiceReadCounts) 의 앞뒤 차이를 따로 구해 (판정에는 쓰지 않음) 그 초의 값 (Last) 과 로딩 판정의 안·밖 합계 (Summary) 를 만듭니다.
/// 표본 루프는 디스크를 읽은 직후 Mark 로 계상값을 읽어 둡니다: 그 초의 디스크 표본과 같은 시각 경계에서 읽어야 그 사이에 생긴 읽기가 앞 초에 들어가지 않습니다.
internal sealed class LoadFeed(string game, string root, LoadPhaseBoard board)
{
    private static readonly ServiceReadPath[] Paths = ServiceReadCounts.Paths;

    private readonly ServiceReadCounts _counts = board.ServiceReads.CountsOf(game);
    private readonly long[] _lastDisk = Read(board.ServiceReads.CountsOf(game), static (c, p) => c.Disk(p), new long[Paths.Length]); // 세션이 시작할 때의 계상값이 기준
    private readonly long[] _lastRequested = Read(board.ServiceReads.CountsOf(game), static (c, p) => c.Requested(p), new long[Paths.Length]);
    private readonly long[] _lastAccepted = Read(board.ServiceReads.CountsOf(game), static (c, p) => c.Accepted(p), new long[Paths.Length]);
    private readonly long[] _nowDisk = new long[Paths.Length]; // Mark 가 읽어 둔 값 (초마다 할당하지 않음)
    private readonly long[] _nowRequested = new long[Paths.Length];
    private readonly long[] _nowAccepted = new long[Paths.Length];
    private readonly long[] _loading = new long[Paths.Length]; // 경로별로 로딩 판정 구간 안에서 읽은 양 (디스크 계상 바이트, 스트림은 받아들여진 바이트)
    private readonly long[] _outside = new long[Paths.Length];
    private long _serviceRead = board.ServiceReads.Total(root); // 세션이 시작할 때의 누계가 기준
    private bool _marked;
    private string? _frozen;

    /// 가장 최근 초에 서비스가 이 게임으로 일으킨 읽기 (초 단위 기록의 service_* 열). 그 초의 값을 만들지 못했으면 null.
    public ServiceReadSecond? Last { get; private set; }

    /// 게임 프로세스가 처음 사라진 시각 (플레이 시작 뒤 초, 종료 유예 중): 그 뒤의 초는 요약에 넣지 않습니다 (기존 플레이 기록이 그 시각까지만 세는 것과 같게). 돌아왔으면 null.
    public double? GoneSeconds { get; set; }

    /// 디스크를 읽은 직후에 부릅니다: 이 시각의 계상값을 읽어 둡니다 (Next 가 그 초의 값으로 씀).
    public void Mark()
    {
        _ = Read(_counts, static (c, p) => c.Disk(p), _nowDisk);
        _ = Read(_counts, static (c, p) => c.Requested(p), _nowRequested);
        _ = Read(_counts, static (c, p) => c.Accepted(p), _nowAccepted);
        _marked = true;
    }

    public LoadState Next(IReadOnlyList<PlayDiskCounters> previous, IReadOnlyList<PlayDiskCounters> current, double seconds, PlaySeriesRaw? raw, bool exeStarted)
    {
        Last = null; // 이 초의 값을 만들지 못하고 끝나면 앞 초의 값을 다시 적지 않음
        if (!_marked)
        {
            Mark(); // 표본 루프가 아닌 호출 (시험): 지금 읽음
        }

        _marked = false;
        if (GoneSeconds is { } gone)
        {
            if (seconds > gone)
            {
                _frozen ??= Build(); // 사라진 시각 뒤의 첫 표본: 그 앞까지의 합계로 요약을 굳힘
            }
        }
        else
        {
            _frozen = null;
        }

        long service = board.ServiceReads.Total(root);
        long serviceDelta = Math.Max(0, service - _serviceRead);
        _serviceRead = service;
        PlaySeriesDisk? disk = PlaySeriesBuilder.Disk(previous, current);
        long drive = disk?.ReadBytes ?? 0;
        LoadState state = board.Add(game, new LoadingSample(seconds, Math.Max(0, drive - serviceDelta), raw?.GpuGameMib, exeStarted)).State;
        board.NoteQueue(game, current.Sum(c => (long)c.QueueDepth)); // 이미 읽은 디스크 성능 값의 대기열: 다시 붙들기가 디스크를 읽어도 되는지 봄
        if (disk is { } d)
        {
            board.NoteDiskSample(game, d.Queue, d.ReadCount, d.ReadMeanMs * d.ReadCount); // 같은 값의 누계: 스트림 종료 로그가 그 구간의 대기열 비율과 읽기 지연을 구함
        }

        NoteSecond(state == LoadState.Loading);
        return state;
    }

    // Mark 가 읽어 둔 경로별 계상값의 앞뒤 차이를 그 초의 값으로 (줄어든 값은 0 으로 자름), 로딩 안·밖에 모음.
    private void NoteSecond(bool loading)
    {
        long diskSum = 0;
        long requestedSum = 0;
        long acceptedSum = 0;
        for (int i = 0; i < Paths.Length; i++)
        {
            long d = Math.Max(0, _nowDisk[i] - _lastDisk[i]);
            long r = Math.Max(0, _nowRequested[i] - _lastRequested[i]);
            long a = Math.Max(0, _nowAccepted[i] - _lastAccepted[i]);
            (diskSum, requestedSum, acceptedSum) = (diskSum + d, requestedSum + r, acceptedSum + a);
            (loading ? _loading : _outside)[i] += d + a;
        }

        _nowDisk.CopyTo(_lastDisk, 0);
        _nowRequested.CopyTo(_lastRequested, 0);
        _nowAccepted.CopyTo(_lastAccepted, 0);
        Last = new ServiceReadSecond(diskSum, requestedSum, acceptedSum);
    }

    /// 플레이 종료 요약 한 줄: 경로별로 로딩 판정 구간 안 / 밖에서 서비스가 이 게임으로 읽은 양 (MiB, 계상값이며 실측이 아님). 게임 프로세스가 처음 사라진 시각 (GoneSeconds) 이 있으면 그 시각 앞의 초까지의 합계.
    public string Summary() => _frozen ?? Build();

    private string Build() => string.Create(CultureInfo.InvariantCulture,
        $"{game}: service reads on the game drive in play (counted MiB, not measured), loading / outside loading: {string.Join(", ", Paths.Where(p => p != ServiceReadPath.Other || _loading[(int)p] + _outside[(int)p] > 0).Select(p => $"{Label(p)} {Mib(_loading[(int)p])}/{Mib(_outside[(int)p])}"))}");

    private static long[] Read(ServiceReadCounts counts, Func<ServiceReadCounts, ServiceReadPath, long> value, long[] into)
    {
        for (int i = 0; i < Paths.Length; i++)
        {
            into[i] = value(counts, Paths[i]);
        }

        return into;
    }

    private static string Label(ServiceReadPath path) => path switch
    {
        ServiceReadPath.StartStream => "start stream",
        ServiceReadPath.StageStream => "stage stream",
        ServiceReadPath.Hold => "hold",
        ServiceReadPath.Keep => "keep rounds",
        ServiceReadPath.Check => "check",
        ServiceReadPath.Sparse => "sparse keep",
        ServiceReadPath.Touch => "start touch",
        _ => "other",
    };

    private static string Mib(long bytes) => (bytes / 1_048_576d).ToString("F1", CultureInfo.InvariantCulture);
}
