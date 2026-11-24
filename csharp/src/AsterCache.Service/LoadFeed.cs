using System.Globalization;

namespace AsterCache.Service;

/// 그 초에 서비스가 게임 드라이브에서 일으킨 읽기 (계상값이며 물리 디스크 읽기의 실측이 아님): ServiceBytes 는 유지·붙들기·점검·시작 만지기·스파스 유지가 디스크에서 읽은 것으로 센 바이트,
/// StreamRequestedBytes·StreamAcceptedBytes 는 시작·단계 스트림이 낸 묶음의 요청 바이트와 받아들여진 바이트 (받아들여졌다고 상주한다는 뜻은 아님).
internal readonly record struct ServiceReadSecond(long ServiceBytes, long StreamRequestedBytes, long StreamAcceptedBytes);

/// 한 플레이의 로딩 판정 입력 공급: 표본 루프가 1초마다 부르면 그 초의 표본 (게임 드라이브 읽기 증가분 - 서비스가 그 드라이브에서 직접 읽은 증가분, 그래픽 값, 실행 파일 시작) 을 만들어 판정 (LoadPhaseBoard) 에 넣고 상태를 돌려줍니다 (표본 루프 스레드 하나에서만 부름).
/// 드라이브 읽기는 표본 루프가 이미 읽는 디스크 성능 값의 앞뒤 차이이고, 서비스의 읽기는 서비스가 읽을 때마다 세는 누계 (ServiceDiskReads) 의 앞뒤 차이입니다: 새 조회가 없습니다.
/// 그래픽 값은 raw 에 있을 때만 씁니다: raw 는 프레임 세션이 열린 (안티치트 게이트를 지난) 플레이에서 초 단위 기록이 켜져 있을 때만 읽히므로, 그렇지 않은 게임에서는 디스크 규칙만 돕니다.
/// 같은 누계의 경로별 앞뒤 차이는 그 초의 로딩 판정에 따라 안·밖으로 모아 플레이 종료 요약 (Summary) 에 씁니다 (판정에는 쓰지 않음).
internal sealed class LoadFeed(string game, string root, LoadPhaseBoard board)
{
    private static readonly ServiceReadPath[] Paths = Enum.GetValues<ServiceReadPath>();

    private readonly long[] _lastDisk = Read(board.ServiceReads, root, static (r, rt, p) => r.Disk(rt, p), new long[Paths.Length]); // 세션이 시작할 때의 누계가 기준
    private readonly long[] _lastRequested = Read(board.ServiceReads, root, static (r, rt, p) => r.Requested(rt, p), new long[Paths.Length]);
    private readonly long[] _lastAccepted = Read(board.ServiceReads, root, static (r, rt, p) => r.Accepted(rt, p), new long[Paths.Length]);
    private readonly long[] _nowDisk = new long[Paths.Length]; // 초마다 새로 읽는 값의 자리 (할당을 되풀이하지 않음)
    private readonly long[] _nowRequested = new long[Paths.Length];
    private readonly long[] _nowAccepted = new long[Paths.Length];
    private readonly long[] _loading = new long[Paths.Length]; // 경로별로 로딩 판정 구간 안에서 읽은 양 (디스크 계상 바이트, 스트림은 받아들여진 바이트)
    private readonly long[] _outside = new long[Paths.Length];
    private long _serviceRead = board.ServiceReads.Total(root);

    /// 가장 최근 초에 서비스가 이 드라이브에서 일으킨 읽기 (초 단위 기록의 service_* 열).
    public ServiceReadSecond? Last { get; private set; }

    public LoadState Next(IReadOnlyList<PlayDiskCounters> previous, IReadOnlyList<PlayDiskCounters> current, double seconds, PlaySeriesRaw? raw, bool exeStarted)
    {
        Last = null; // 이 초의 값을 만들지 못하고 끝나면 앞 초의 값을 다시 적지 않음
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

    // 경로별 누계의 앞뒤 차이를 그 초의 값으로 (한 초 동안 줄어든 누계는 없지만 0 으로 자름), 로딩 안·밖에 모음.
    private void NoteSecond(bool loading)
    {
        long[] disk = Read(board.ServiceReads, root, static (r, rt, p) => r.Disk(rt, p), _nowDisk);
        long[] requested = Read(board.ServiceReads, root, static (r, rt, p) => r.Requested(rt, p), _nowRequested);
        long[] accepted = Read(board.ServiceReads, root, static (r, rt, p) => r.Accepted(rt, p), _nowAccepted);
        long diskSum = 0;
        long requestedSum = 0;
        long acceptedSum = 0;
        for (int i = 0; i < Paths.Length; i++)
        {
            long d = Math.Max(0, disk[i] - _lastDisk[i]);
            long r = Math.Max(0, requested[i] - _lastRequested[i]);
            long a = Math.Max(0, accepted[i] - _lastAccepted[i]);
            (diskSum, requestedSum, acceptedSum) = (diskSum + d, requestedSum + r, acceptedSum + a);
            (loading ? _loading : _outside)[i] += d + a;
        }

        disk.CopyTo(_lastDisk, 0);
        requested.CopyTo(_lastRequested, 0);
        accepted.CopyTo(_lastAccepted, 0);
        Last = new ServiceReadSecond(diskSum, requestedSum, acceptedSum);
    }

    /// 플레이 종료 요약 한 줄: 경로별로 로딩 판정 구간 안 / 밖에서 서비스가 게임 드라이브에서 읽은 양 (MiB, 계상값이며 실측이 아님).
    public string Summary() => string.Create(CultureInfo.InvariantCulture,
        $"{game}: service reads on the game drive in play (counted MiB, not measured), loading / outside loading: {string.Join(", ", Paths.Where(p => p != ServiceReadPath.Other || _loading[(int)p] + _outside[(int)p] > 0).Select(p => $"{Label(p)} {Mib(_loading[(int)p])}/{Mib(_outside[(int)p])}"))}");

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

    private static long[] Read(ServiceDiskReads reads, string root, Func<ServiceDiskReads, string, ServiceReadPath, long> value, long[] into)
    {
        for (int i = 0; i < Paths.Length; i++)
        {
            into[i] = value(reads, root, Paths[i]);
        }

        return into;
    }
}
