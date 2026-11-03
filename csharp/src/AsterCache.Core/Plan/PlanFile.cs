using System.Security.Cryptography;
using System.Text;
using AsterCache.Core.Json;
using AsterCache.Core.Model;
using AsterCache.Core.Trace;

namespace AsterCache.Core.Plan;

public sealed record PlanZone(long? StartLcn, long Clusters);

/// 계획 시점의 파일 지문 [size, mtime_ns]. MOVE_FILE 은 둘 다 바꾸지 않으므로 계획 뒤 다시 쓴 파일을 알아챕니다.
public sealed record Fingerprint(string Path, long Size, long MtimeNs)
{
    /// 1970-01-01 UTC 의 .NET Ticks (MtimeNs 를 시각으로 되돌릴 때).
    public const long UnixEpochTicks = 621355968000000000L;
}

/// plan.json 한 벌. 키 순서와 바이트 표기는 Python 과 같습니다 (<see cref="Dumps"/>, <see cref="Save"/>).
/// Python 과 다른 점: block 과 지문 값 ([size, mtime_ns] 정수 둘) 도 읽을 때 검사하고, 알려진 키 밖의 키는 버립니다.
public sealed record PlanFile(
    long Version, string OriginRoot, string Volume, long ClusterBytes, long Block, PlanZone Zone,
    IReadOnlyList<PlanTask> Tasks, IReadOnlyList<PlanRange> Order, IReadOnlyList<Fingerprint> Fingerprints, JObject Stats)
{
    public const long CurrentVersion = 1;

    public static PlanFile Make(IReadOnlyList<IReadOnlyList<TraceEvent>> sessions, string originRoot, long zoneBytes, long clusterBytes,
        Func<string, IReadOnlyList<Extent>?> extentsFn, Func<string, long>? sizeFn = null, Func<string, Fingerprint>? fingerprintFn = null,
        long? zoneStartLcn = null, long block = HotSet.Block, int minSessions = 1, long preserveBytes = 0, long alignClusters = 4, long fixedClusters = 0)
    {
        sizeFn ??= p => new FileInfo(p).Length;
        fingerprintFn ??= FingerprintOf;
        (List<BlockKey> hot, _) = HotSet.Select(sessions, zoneBytes, block, minSessions: minSessions);
        List<Unit> units = Consensus.Order(sessions, hot, block);
        var paths = units.Select(u => u.Path).Distinct(StringComparer.Ordinal).ToList();
        paths.Sort(CodePointComparer.Instance);
        var ext = paths.ToDictionary(p => p, extentsFn, StringComparer.Ordinal);
        var sizes = paths.ToDictionary(p => p, sizeFn, StringComparer.Ordinal);
        long zc = zoneBytes / clusterBytes;
        AllocResult alloc = ZoneAllocator.Allocate(units, ext, zoneStartLcn, zc, clusterBytes, block, sizes, preserveBytes, alignClusters, fixedClusters);
        JObject stats = alloc.Stats.ToJson();
        stats.Add("hot_blocks", (long)hot.Count);
        stats.Add("units", (long)units.Count);
        var orderPaths = alloc.Order.Select(o => o.Path).Distinct(StringComparer.Ordinal).ToList();
        orderPaths.Sort(CodePointComparer.Instance);
        return new PlanFile(CurrentVersion, originRoot, SplitDrive(originRoot).ToUpperInvariant(), clusterBytes, block,
            new PlanZone(zoneStartLcn, zc), alloc.Tasks, alloc.Order, orderPaths.Select(fingerprintFn).ToList(), stats);
    }

    /// size 와 mtime_ns. Windows 의 Python st_mtime_ns 는 FILETIME x 100 이라 Ticks 로 정확히 같습니다. 심볼릭 링크 (SSD 보조 저장) 는 대상의 값을 봅니다.
    public static Fingerprint FingerprintOf(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null && info.ResolveLinkTarget(true) is FileInfo target)
        {
            info = target;
        }

        return new Fingerprint(path, info.Length, (info.LastWriteTimeUtc.Ticks - Fingerprint.UnixEpochTicks) * 100);
    }

    /// ntpath.splitdrive 의 드라이브 부분 ("D:" 또는 "\\host\share", 없으면 빈 문자열).
    public static string SplitDrive(string p)
    {
        string n = p.Replace('/', '\\');
        if (n.StartsWith(@"\\", StringComparison.Ordinal) && (n.Length < 3 || n[2] != '\\'))
        {
            int i = n.IndexOf('\\', 2);
            if (i == -1)
            {
                return string.Empty;
            }

            int j = n.IndexOf('\\', i + 1);
            if (j == -1)
            {
                j = p.Length;
            }

            return j == i + 1 ? string.Empty : p[..j];
        }

        return n.Length >= 2 && n[1] == ':' ? p[..2] : string.Empty;
    }

    public JObject ToJson()
    {
        var fingerprints = new JObject();
        foreach (Fingerprint f in Fingerprints)
        {
            fingerprints.Add(f.Path, new List<object?> { f.Size, f.MtimeNs });
        }

        return new JObject
        {
            { "version", Version },
            { "origin_root", OriginRoot },
            { "volume", Volume },
            { "cluster_bytes", ClusterBytes },
            { "block", Block },
            { "zone", new JObject { { "start_lcn", Zone.StartLcn }, { "clusters", Zone.Clusters } } },
            { "tasks", Tasks.Select(t => t.ToJson()).ToList() },
            { "order", Order.Select(o => o.ToJson()).ToList() },
            { "fingerprints", fingerprints },
            { "stats", Stats },
        };
    }

    /// json.dumps(plan, indent=1): LF 줄바꿈, 끝 줄바꿈 없음.
    public string Dumps() => PyJson.Dumps(ToJson(), indent: 1);

    /// Path.write_text(json.dumps(plan, indent=1)) 와 같은 바이트: UTF-8, BOM 없음, Windows 텍스트 모드라 CRLF.
    public void Save(string path) => File.WriteAllBytes(path, PyJson.Utf8.GetBytes(PyJson.ToCrlf(Dumps())));

    public static PlanFile Load(string path) => FromJson(JsonRead.ReadFile(path));

    /// 계획 지문 (journal header 의 plan_sha): origin_root, cluster_bytes, tasks, order 만 키 정렬 json.dumps 한 SHA-256, 소문자 hex.
    public string Sha()
    {
        var keep = new JObject
        {
            { "origin_root", OriginRoot },
            { "cluster_bytes", ClusterBytes },
            { "tasks", Tasks.Select(t => t.ToJson()).ToList() },
            { "order", Order.Select(o => o.ToJson()).ToList() },
        };
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(PyJson.Dumps(keep, sortKeys: true))));
    }

    /// 존 시작이 아직 없으면 baseLcn 으로 정하고 목표를 그만큼 옮긴 계획. 이미 있으면 같은 객체.
    public PlanFile Resolve(long baseLcn) => Zone.StartLcn is not null
        ? this
        : this with
        {
            Zone = Zone with { StartLcn = baseLcn },
            Tasks = Tasks.Select(t => t with { TargetLcn = t.TargetLcn + baseLcn }).ToList(),
        };

    /// 계획의 핫 범위를 존 (= 접근) 순서로 그대로 읽는 선읽기 입력. 크기 합은 stats.hot_bytes.
    public List<TraceEvent> PrefetchTrace(IReadOnlyList<IReadOnlyList<TraceEvent>> sessions)
    {
        var cost = new Dictionary<BlockKey, double>();
        foreach (IReadOnlyList<TraceEvent> s in sessions)
        {
            foreach (BlockEvent e in TraceEvents.Validated(s, Block))
            {
                for (long b = e.First; b <= e.Last; b++)
                {
                    var key = new BlockKey(e.Path, b);
                    cost[key] = Math.Max(cost.GetValueOrDefault(key, 0.0), e.Cost);
                }
            }
        }

        var rows = new List<TraceEvent>(Order.Count);
        foreach (PlanRange o in Order)
        {
            long off = o.Vcn * ClusterBytes;
            long n = o.Clusters * ClusterBytes;
            long first = off / Block;
            long end = FloorDiv(off + n - 1, Block) + 1;
            if (end <= first)
            {
                throw new PyValueException("max() arg is an empty sequence");
            }

            double c = 0.0;
            for (long b = first; b < end; b++)
            {
                c = Math.Max(c, cost.GetValueOrDefault(new BlockKey(o.Path, b), 0.0));
            }

            rows.Add(new TraceEvent(o.Path, off, n, c));
        }

        return rows;
    }

    private static long FloorDiv(long a, long b) => a / b - (a % b < 0 ? 1 : 0);

    /// Python validate_plan: 계획 파일은 믿을 수 없는 입력이라 모양과 형을 검사합니다. 틀리면 PyValueException("malformed plan").
    public static PlanFile FromJson(object? root)
    {
        JObject plan = Obj(root);
        if (Field(plan, "version") is not long version || version != CurrentVersion)
        {
            throw Malformed();
        }

        JObject zone = Obj(Field(plan, "zone"));
        object? start = Field(zone, "start_lcn");
        long? startLcn = start is null ? null : UInt(start);
        var tasks = new List<PlanTask>();
        foreach (object? t in Arr(Field(plan, "tasks")))
        {
            JObject o = Obj(t);
            long clusters = UInt(Field(o, "clusters"));
            tasks.Add(new PlanTask(Str(Field(o, "path")), UInt(Field(o, "vcn")), clusters > 0 ? clusters : throw Malformed(), UInt(Field(o, "target_lcn"))));
        }

        var order = new List<PlanRange>();
        foreach (object? t in Arr(Field(plan, "order")))
        {
            JObject o = Obj(t);
            order.Add(new PlanRange(Str(Field(o, "path")), UInt(Field(o, "vcn")), UInt(Field(o, "clusters"))));
        }

        var fingerprints = new List<Fingerprint>();
        foreach ((string path, object? value) in Obj(Field(plan, "fingerprints")))
        {
            List<object?> pair = Arr(value);
            if (pair.Count != 2)
            {
                throw Malformed();
            }

            fingerprints.Add(new Fingerprint(path, SInt(pair[0]), SInt(pair[1])));
        }

        long clusterBytes = UInt(Field(plan, "cluster_bytes"));
        long block = UInt(Field(plan, "block"));
        if (clusterBytes <= 0 || block <= 0)
        {
            throw Malformed();
        }

        return new PlanFile(version, Str(Field(plan, "origin_root")), Str(Field(plan, "volume")), clusterBytes, block,
            new PlanZone(startLcn, UInt(Field(zone, "clusters"))), tasks, order, fingerprints, Obj(Field(plan, "stats")));
    }

    private static PyValueException Malformed() => new("malformed plan");

    private static object? Field(JObject o, string key) => o.TryGetValue(key, out object? v) ? v : throw Malformed();

    private static JObject Obj(object? v) => v as JObject ?? throw Malformed();

    private static List<object?> Arr(object? v) => v as List<object?> ?? throw Malformed();

    private static string Str(object? v) => v as string ?? throw Malformed();

    private static long UInt(object? v) => v is long l && l >= 0 ? l : throw Malformed();

    private static long SInt(object? v) => v is long l ? l : throw Malformed();
}
