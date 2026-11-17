using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AsterCache.Core.Apply;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using AsterCache.Core.Trace;
using AsterCache.Windows;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace AsterCache.Tests.Windows;

/// 실제 NTFS 볼륨의 임시 폴더에 만든 스파스 시험 파일로 계획 → 적용 → 배치 검사 → 되돌리기를 돌립니다.
/// 기본 실행에서는 제외되고 승격된 (관리자) 프로세스에서 `--filter "Category=Live"` 로 따로 돌립니다. 게임 파일과 설치된 서비스는 건드리지 않습니다.
[Trait("Category", "Live")]
public sealed class SparseRelocationLiveTests
{
    private const int Units = 8;
    private static readonly int[] WrittenUnits = [0, 2, 3, 6]; // 나머지 단위는 구멍

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, nint input, uint inputSize, nint output, uint outputSize, out uint returned, nint overlapped);

    // 스파스 속성을 주고, 16클러스터 단위로 알려진 내용을 지정한 단위에만 쓴 뒤 논리 크기를 맞춤.
    private static string CreateSparseFile(TempDir dir, long unitBytes)
    {
        string path = dir.Combine("sparse-test.bin");
        using SafeFileHandle h = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, FileOptions.WriteThrough);
        Assert.True(DeviceIoControl(h, 0x900C4, 0, 0, 0, 0, out _, 0)); // FSCTL_SET_SPARSE
        foreach (int unit in WrittenUnits)
        {
            RandomAccess.Write(h, RandomNumberGenerator.GetBytes((int)unitBytes), unit * unitBytes);
        }

        RandomAccess.SetLength(h, Units * unitBytes);
        RandomAccess.FlushToDisk(h);
        return path;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static IReadOnlyList<AllocatedRange> Allocation(string path) => SparseAllocation.Normalize(FileLayout.AllocatedExtents(path)!);

    private static Dictionary<long, long> Map(string path)
    {
        var map = new Dictionary<long, long>();
        foreach (Extent e in FileLayout.AllocatedExtents(path)!)
        {
            for (long i = 0; i < e.Clusters; i++)
            {
                map[e.Vcn + i] = e.Lcn + i;
            }
        }

        return map;
    }

    private static ApplyHooks Hooks() => new()
    {
        Extents = FileLayout.RelocationExtents,
        Digest = (path, size, ranges) => SampleDigest.Compute(path, size, ranges),
        EdgeDigest = (path, size, ranges) => SampleDigest.Compute(path, size, ranges, span: 0),
        SkipReason = path => FileGuards.RelocationSkipReason(path),
    };

    [Fact]
    public void PlanApplyVerifyAndRevertKeepContentSizeSparseAttributeAndAllocatedRanges()
    {
        Assert.True(Elevation.IsAdmin(), "run elevated: the move needs a volume handle");
        using var dir = new TempDir();
        string letter = Path.GetPathRoot(dir.Path)![..2].ToUpperInvariant();
        using NtfsVolume vol = NtfsVolume.Open(letter);
        long cb = vol.ClusterBytes;
        string path = CreateSparseFile(dir, 16 * cb);
        string sha = Hash(path);
        long size = new FileInfo(path).Length;
        DateTime mtime = File.GetLastWriteTimeUtc(path);
        IReadOnlyList<AllocatedRange> allocation = Allocation(path);
        Dictionary<long, long> before = Map(path);
        Assert.True(FileGuards.IsSparse(path));
        Assert.Equal(WrittenUnits.Length * 16, before.Count); // 쓴 단위만 할당됨

        List<TraceEvent> events = [new(path, 0, size, 8.0)];
        PlanFile plan = PlanFile.Make([events], dir.Path, 4L << 20, cb, FileLayout.RelocationExtents, preserveBytes: 0, sparseFn: p => FileGuards.IsSparse(p));
        Assert.NotEmpty(plan.Tasks);
        Assert.All(plan.Tasks, t => Assert.True(t.Vcn % 16 == 0 && t.Clusters % 16 == 0));
        Assert.All(plan.Order, o => Assert.True(Enumerable.Range(0, (int)o.Clusters).All(i => before.ContainsKey(o.Vcn + i)))); // 구멍은 계획에 없음
        string planPath = dir.Combine("plan.json");
        string journal = dir.Combine("journal.jsonl");

        ApplyReport rep = Applier.Apply(plan, vol, [dir.Path], journal, Hooks(), new ApplyOptions(Execute: true, SaveTo: planPath, Verify: VerifyMode.All));

        Assert.Empty(rep.Failed);
        Assert.Null(rep.Stopped);
        Assert.True(rep.SparseMovedBytes > 0);
        PlanFile applied = PlanFile.Load(planPath);
        Dictionary<long, long> after = Map(path);
        foreach (PlanTask t in applied.Tasks) // 대상 구간의 위치가 계획대로 바뀜
        {
            Assert.Equal(t.Clusters, PlanGuards.DonePrefix(FileLayout.RelocationExtents(path), t.Vcn, t.Clusters, t.TargetLcn));
        }

        Assert.True(LayoutVerifier.Verify(applied, FileLayout.RelocationExtents, FileGuards.RelocationUnmovable).Contiguous);
        Assert.Equal(sha, Hash(path));
        Assert.Equal(size, new FileInfo(path).Length);
        Assert.True(FileGuards.IsSparse(path));
        Assert.Equal(allocation, Allocation(path));
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(path));
        Assert.NotEqual(before, after);

        RevertReport reverted = Reverter.Revert(journal, vol, [dir.Path], Hooks(), new ApplyOptions(Execute: true));

        Assert.Empty(reverted.Skipped);
        Assert.Equal(sha, Hash(path));
        Assert.True(FileGuards.IsSparse(path));
        Assert.Equal(allocation, Allocation(path));
        Assert.Equal(before, Map(path)); // 원래 위치로 돌아옴
    }
}
