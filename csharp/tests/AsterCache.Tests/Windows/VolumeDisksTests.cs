using System.Buffers.Binary;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Windows;

/// 물리 디스크 판정의 순수 부분: 집합 교집합, 조회 실패 = 같은 디스크, 캐시. 실제 IOCTL 은 부르지 않습니다 (가짜 조회만).
public sealed class VolumeDisksTests
{
    private long _ms;
    private readonly Dictionary<string, IReadOnlySet<uint>?> _disks = new()
    {
        ["C:"] = new HashSet<uint> { 0 },
        ["D:"] = new HashSet<uint> { 0 },
        ["E:"] = new HashSet<uint> { 1 },
        ["F:"] = new HashSet<uint> { 1, 2 },
    };
    private readonly List<string> _queried = [];

    private VolumeDisks Make() => new(letter =>
    {
        _queried.Add(letter);
        return _disks.GetValueOrDefault(letter);
    }, () => _ms);

    private static byte[] Extents(params uint[] disks)
    {
        byte[] b = new byte[8 + (disks.Length * 24)];
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)disks.Length);
        for (int i = 0; i < disks.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8 + (i * 24)), disks[i]);
        }

        return b;
    }

    [Fact]
    public void ParsesTheDiskNumbersOfEveryExtent()
    {
        Assert.Equal(new HashSet<uint> { 1, 3 }, VolumeDisks.Parse(Extents(1, 3, 1)));
        Assert.Null(VolumeDisks.Parse(Extents()));
        Assert.Null(VolumeDisks.Parse(Extents(1, 2)[..40])); // 잘림
        Assert.Null(VolumeDisks.Parse(new byte[4]));
    }

    [Fact]
    public void TwoVolumesOnTheSamePhysicalDiskAreTheSameAndOthersAreNot()
    {
        VolumeDisks v = Make();

        Assert.True(v.AreSame("C:", "D:"));
        Assert.True(v.AreSame(@"d:\Games\x", "C:"));
        Assert.False(v.AreSame("D:", "E:"));
        Assert.True(v.AreSame("E:", "F:")); // 스팬 볼륨: 하나라도 겹치면 같음
    }

    [Fact]
    public void AVolumeWhoseDiskCannotBeReadCountsAsTheSameDiskAsAnything()
    {
        VolumeDisks v = Make();

        Assert.True(v.AreSame("D:", "G:")); // G: 조회 실패
        Assert.True(v.AreSame("G:", "E:"));
        Assert.True(v.AreSame(@"\\server\share", "E:")); // 드라이브 문자 아님
    }

    [Fact]
    public void ResultsAreCachedForFiveMinutesThenReadAgain()
    {
        VolumeDisks v = Make();
        Assert.True(v.AreSame("C:", "D:"));
        Assert.True(v.AreSame("D:", "C:"));
        Assert.Equal(["C:", "D:"], _queried);

        _disks["D:"] = new HashSet<uint> { 1 }; // 재연결된 다른 디스크
        _ms += (long)TimeSpan.FromSeconds(299).TotalMilliseconds;
        Assert.True(v.AreSame("C:", "D:")); // 5 분 안: 옛 매핑 유지
        Assert.Equal(["C:", "D:"], _queried);

        _ms += (long)TimeSpan.FromSeconds(1).TotalMilliseconds;
        Assert.False(v.AreSame("C:", "D:")); // 5 분 만료: 새 매핑 반영
        Assert.Equal(["C:", "D:", "C:", "D:"], _queried);
    }

    [Fact]
    public void ForgetMakesTheNextLookupAskAgainBeforeFiveMinutesEnd()
    {
        VolumeDisks v = Make();
        Assert.False(v.AreSame("D:", "E:"));

        _disks["D:"] = new HashSet<uint> { 1 }; // 같은 시각에 다른 디스크로 재할당
        v.Forget("D:");
        Assert.True(v.AreSame("D:", "E:"));
        Assert.Equal(["D:", "E:", "D:"], _queried);
    }

    [Fact]
    public void AFailedLookupIsNotCachedAndDropsTheEarlierMapping()
    {
        VolumeDisks v = Make();
        Assert.False(v.AreSame("D:", "E:"));

        _disks.Remove("D:"); // 분리: 다음 조회 실패
        _ms += (long)TimeSpan.FromMinutes(5).TotalMilliseconds;
        Assert.True(v.AreSame("D:", "E:")); // 판정 불가 = 같은 디스크

        _disks["D:"] = new HashSet<uint> { 2 }; // 다시 붙음, 시각은 그대로
        Assert.False(v.AreSame("D:", "E:")); // 실패가 캐시되지 않아 바로 새 매핑
        Assert.Equal(["D:", "E:", "D:", "E:", "D:"], _queried);
    }

    [Fact]
    public void PhysicalKeyJoinsTheDiskNumbersAndIsNullWhenUnknown()
    {
        VolumeDisks v = Make();

        Assert.Equal("disk0", v.PhysicalKeyOf(@"D:\Games\Foo"));
        Assert.Equal("disk1+2", v.PhysicalKeyOf("F:"));
        Assert.Null(v.PhysicalKeyOf("Z:")); // 조회 실패
        Assert.Null(v.PhysicalKeyOf(@"\\server\share")); // 드라이브 문자 없음
    }
}
