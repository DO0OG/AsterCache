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
        n.NameObject(7, @"C:\Games\x\first.txt");
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

    private readonly List<(string Name, long Offset, long Size)> _late = [];

    private FileNames Live() => new((name, offset, size) => _late.Add((name, offset, size)));

    [Fact]
    public void CreateThenReadThenRundownDeliversTheReadImmediatelyByTheObjectName()
    {
        FileNames n = Live();
        n.NameObject(7, @"D:\Games\a.bin"); // FileIOCreate

        string? live = n.ResolveNow(100, 7, 4096, 512); // FileKey 이름은 아직 없음
        n.NameKey(100, @"D:\Games\a.bin"); // 정리 (rundown) 이벤트는 세션 끝에 옴

        Assert.Equal(@"D:\Games\a.bin", live);
        Assert.Empty(_late); // 이미 즉시 전달됐으므로 늦은 전달은 없음
        Assert.Equal((1L, 0L, 0), (n.Resolved, n.Late, n.Waiting));
    }

    [Fact]
    public void ReadBeforeAnyNameIsHeldAndDeliveredLateWhenTheKeyNameArrives()
    {
        FileNames n = Live();

        Assert.Null(n.ResolveNow(100, 7, 0, 4096));
        Assert.Null(n.ResolveNow(100, 7, 4096, 4096));
        Assert.Null(n.ResolveNow(200, 8, 0, 100)); // 다른 파일은 기다림
        Assert.Equal(3, n.Waiting);
        Assert.Empty(_late);

        n.NameKey(100, @"D:\Games\b.bin"); // FileIOName / FileIOFileCreate / Rundown

        Assert.Equal([(@"D:\Games\b.bin", 0L, 4096L), (@"D:\Games\b.bin", 4096L, 4096L)], _late);
        Assert.Equal((0L, 2L, 1), (n.Resolved, n.Late, n.Waiting)); // 200 은 끝까지 못 풀면 Waiting 으로 남음
        n.NameKey(100, @"D:\Games\b.bin");
        Assert.Equal(2, _late.Count); // 같은 이름이 다시 와도 두 번 전달하지 않음
    }

    [Fact]
    public void ReadBeforeAnyNameIsDeliveredLateWhenTheObjectNameArrives()
    {
        FileNames n = Live();
        Assert.Null(n.ResolveNow(100, 7, 8192, 4096));

        n.NameObject(7, @"D:\Games\c.bin"); // Create 가 읽기보다 늦게 도착

        Assert.Equal([(@"D:\Games\c.bin", 8192L, 4096L)], _late);
        Assert.Equal(0, n.Waiting);
    }

    [Fact]
    public void ReusedObjectAddressAfterCloseIsNeverResolvedByTheOldName()
    {
        FileNames n = Live();
        n.NameObject(7, @"D:\Games\old.bin");
        Assert.Equal(@"D:\Games\old.bin", n.ResolveNow(100, 7, 0, 10));
        n.CloseObject(7); // 닫힘: 주소 7 은 다른 파일에 재사용됨

        string? live = n.ResolveNow(300, 7, 0, 10); // 새 파일의 읽기 (Create 이벤트가 아직 없음)

        Assert.Null(live); // 옛 이름으로 풀리지 않고 보류됨
        n.NameKey(300, @"D:\Games\new.bin");
        Assert.Equal([(@"D:\Games\new.bin", 0L, 10L)], _late); // FileKey 가 알려 준 진짜 이름으로만 늦게 전달
    }

    [Fact]
    public void CloseDropsTheObjectLinkSoALaterCreateForTheReusedAddressDoesNotClaimTheHeldRead()
    {
        FileNames n = Live();
        Assert.Null(n.ResolveNow(300, 7, 0, 10)); // 주소 7 로 보류
        n.CloseObject(7);

        n.NameObject(7, @"D:\Games\other.bin"); // 같은 주소를 쓰는 다른 파일의 Create

        Assert.Empty(_late); // 보류한 읽기를 다른 파일 이름으로 내보내지 않음
        n.NameKey(300, @"D:\Games\mine.bin");
        Assert.Equal([(@"D:\Games\mine.bin", 0L, 10L)], _late);
    }

    [Fact]
    public void UnnamedKeyZeroIsNeverHeld()
    {
        FileNames n = Live();

        Assert.Null(n.ResolveNow(0, 0, 0, 10));
        Assert.Equal(0, n.Waiting);
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
