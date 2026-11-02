using AsterCache.Tracer;
using Xunit;

namespace AsterCache.Tests.Tracer;

/// 읽기 이름 해석 규칙 (실제 .etl 에서 FileObject 재사용으로 읽기의 절반이 오분류된 결함의 회귀 시험).
public sealed class FileNamesTests
{
    [Fact]
    public void KeyNameKnownAtReadIsFinalAndIgnoresObjectName()
    {
        var n = new FileNames();
        n.NameObject(7, @"C:\old\stale.bin");
        n.NameKey(100, @"D:\Games\a.bin");

        Assert.Equal((@"D:\Games\a.bin", null), n.AtRead(100, 7));
    }

    [Fact]
    public void ReusedObjectAddressDoesNotKeepTheClosedFilesName()
    {
        var n = new FileNames();
        n.NameObject(7, @"C:\Users\x\first.txt");
        n.CloseObject(7); // 닫힘: 주소는 다른 파일에 재사용됨

        (string? name, string? fallback) = n.AtRead(200, 7);

        Assert.Null(name);
        Assert.Null(fallback);
        Assert.Null(n.Settle(200, fallback)); // 이름을 끝내 모르면 버려짐 (Unresolved)
    }

    [Fact]
    public void DeferredReadPrefersTheLateKeyNameOverTheObjectNameCapturedAtReadTime()
    {
        var n = new FileNames();
        n.NameObject(7, @"C:\stale\wrong.bin"); // Create 가 유실돼 옛 이름이 남은 경우
        (string? name, string? fallback) = n.AtRead(300, 7);
        Assert.Null(name);
        Assert.Equal(@"C:\stale\wrong.bin", fallback);

        n.NameKey(300, @"D:\Games\right.bin"); // 세션 끝의 정리 이벤트

        Assert.Equal(@"D:\Games\right.bin", n.Settle(300, fallback));
        Assert.Equal(@"C:\stale\wrong.bin", n.Settle(999, fallback)); // FileKey 이름이 없으면 읽을 때의 FileObject 이름
    }

    [Fact]
    public void EmptyNamesAndZeroIdsAreIgnored()
    {
        var n = new FileNames();
        n.NameKey(0, "x");
        n.NameKey(5, "");
        n.NameObject(0, "y");

        Assert.Equal((null, null), n.AtRead(0, 0));
        Assert.Equal((null, null), n.AtRead(5, 5));
    }
}
