using System.ComponentModel;
using AsterCache.Core.Apply;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using AsterCache.Core.Trace;
using AsterCache.Core.Volume;

namespace AsterCache.Tests.Core;

/// 볼륨 대역: 경로별 extent 를 들고 있고 Move 가 실제로 클러스터를 다시 매핑합니다 (Python FakeVol).
/// Strict 면 목표 클러스터가 어떤 파일에든 쓰이고 있을 때 실패합니다 (진짜 FSCTL_MOVE_FILE 처럼).
public sealed class FakeVolume : IVolume
{
    public FakeVolume(Dictionary<string, List<Extent>> files, string letter)
    {
        Ext = files;
        Letter = letter;
    }

    public Dictionary<string, List<Extent>> Ext { get; }

    public List<(string Path, long Vcn, long Lcn, long N)> Moves { get; } = [];

    public int? FailAfter { get; set; }

    /// 이 횟수만큼 Move 호출이 (이동 없이) 실패한 뒤 정상으로 돌아옵니다.
    public int FailNext { get; set; }

    /// 이 번째 (0 부터) 이동 호출에서 IOException 이 아닌 예외를 던집니다.
    public int? CrashAt { get; set; }

    /// 이동이 성공한 직후 (지금까지 성공한 이동 수) 불립니다. 시험이 이 시점에 취소를 겁니다.
    public Action<int>? AfterMove { get; set; }

    public bool Strict { get; set; } = true;

    public bool Closed { get; private set; }

    public string Letter { get; set; }

    public int ClusterBytes { get; set; } = 4096;

    public long TotalClusters { get; set; } = 200000;

    public long FreeClusters => TotalClusters;

    public (long Start, long End) MftZone => (0, 0);

    public VolumeBitmap Bitmap()
    {
        var data = new byte[(TotalClusters + 7) / 8];
        foreach (Extent e in Ext.Values.SelectMany(x => x))
        {
            for (long i = 0; i < e.Clusters; i++)
            {
                long l = e.Lcn + i;
                if (l >= TotalClusters)
                {
                    continue;
                }

                data[l >> 3] |= (byte)(1 << (int)(l & 7));
            }
        }

        return new VolumeBitmap(0, TotalClusters, data);
    }

    public void Move(string path, long vcn, long lcn, uint clusters)
    {
        if (CrashAt == Moves.Count)
        {
            throw new InvalidOperationException("simulated crash");
        }

        if (FailAfter is { } n && Moves.Count >= n)
        {
            throw new IOException("simulated power loss");
        }

        if (FailNext > 0)
        {
            FailNext--;
            throw new Win32Exception(5);
        }

        if (Strict && Ext.Values.SelectMany(x => x).Any(e => e.Lcn < lcn + clusters && lcn < e.Lcn + e.Clusters))
        {
            throw new Win32Exception(87); // 목표가 쓰이는 중
        }

        var m = new SortedDictionary<long, long>();
        foreach (Extent e in Ext[path])
        {
            for (long i = 0; i < e.Clusters; i++)
            {
                m[e.Vcn + i] = e.Lcn + i;
            }
        }

        for (long i = 0; i < clusters; i++)
        {
            m[vcn + i] = lcn + i;
        }

        var outList = new List<Extent>();
        foreach ((long v, long l) in m)
        {
            if (outList.Count > 0 && outList[^1].Vcn + outList[^1].Clusters == v && outList[^1].Lcn + outList[^1].Clusters == l)
            {
                outList[^1] = outList[^1] with { Clusters = outList[^1].Clusters + 1 };
            }
            else
            {
                outList.Add(new Extent(v, l, 1));
            }
        }

        Ext[path] = outList;
        Moves.Add((path, vcn, lcn, clusters));
        AfterMove?.Invoke(Moves.Count);
    }

    public void Dispose() => Closed = true;
}

/// 실제 임시 파일 2개 (각 1 MiB, 내용은 난수) 와 가짜 볼륨을 갖춘 시험 바탕 (Python ApplyBase).
public abstract class ApplyTestBase : IDisposable
{
    protected const long Blk = 65536;
    protected const long Cb = 4096;
    protected const long Per = Blk / Cb;

    protected ApplyTestBase()
    {
        Root = Path.Combine(Path.GetTempPath(), "aster_apply_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Letter = Root[..2].ToUpperInvariant();
        F1 = Path.Combine(Root, "f1.bin");
        F2 = Path.Combine(Root, "f2.bin");
        foreach (string p in new[] { F1, F2 })
        {
            File.WriteAllBytes(p, System.Security.Cryptography.RandomNumberGenerator.GetBytes(1 << 20));
        }

        long clusters = (1 << 20) / Cb;
        Files = new Dictionary<string, List<Extent>>(StringComparer.Ordinal)
        {
            [F1] = [new(0, 1000, 64), new(64, 3000, 64), new(128, 2000, 64), new(192, 5000, 64)],
            [F2] = [new(0, 7000, clusters / 2), new(clusters / 2, 9000, clusters / 2)],
        };
        Vol = new FakeVolume(Files.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.Ordinal), Letter);
        Sessions = [Run((F1, 12), (F2, 0), (F1, 0), (F1, 4), (F2, 8))];
        JournalPath = Path.Combine(Root, "j.jsonl");
        Sleeps = [];
    }

    protected string Root { get; }

    protected string Letter { get; }

    protected string F1 { get; }

    protected string F2 { get; }

    protected Dictionary<string, List<Extent>> Files { get; set; }

    protected FakeVolume Vol { get; }

    protected IReadOnlyList<IReadOnlyList<TraceEvent>> Sessions { get; set; }

    protected string JournalPath { get; }

    protected List<TimeSpan> Sleeps { get; }

    public void Dispose()
    {
        Directory.Delete(Root, recursive: true);
        GC.SuppressFinalize(this);
    }

    protected static List<TraceEvent> Run(params (string Path, long Block)[] keys) => keys.Select(k => new TraceEvent(k.Path, k.Block * Blk, Blk, 8.0)).ToList();

    protected PlanFile Make(long? start = null) =>
        PlanFile.Make(Sessions, Root, 2 << 20, Cb, p => Files[p], zoneStartLcn: start, preserveBytes: 64 << 20);

    protected ApplyHooks Hooks(Func<string, long, IReadOnlyList<(long Offset, long Length)>, string>? digest = null, Func<string, string?>? skip = null) => new()
    {
        Extents = p => Vol.Ext[p],
        Digest = digest ?? ((p, size, extra) => SampleHasher.Compute(p, size, extra, null)),
        SkipReason = skip ?? (_ => null),
        Sleep = Sleeps.Add,
    };

    protected ApplyReport Apply(PlanFile plan, ApplyOptions? options = null, ApplyHooks? hooks = null) =>
        Applier.Apply(plan, Vol, [Root], JournalPath, hooks ?? Hooks(), options);

    protected LayoutReport Verify(PlanFile plan) => LayoutVerifier.Verify(plan, p => Vol.Ext[p]);

    protected static bool SameExtents(Dictionary<string, List<Extent>> a, Dictionary<string, List<Extent>> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out List<Extent>? o) && kv.Value.SequenceEqual(o));
}
