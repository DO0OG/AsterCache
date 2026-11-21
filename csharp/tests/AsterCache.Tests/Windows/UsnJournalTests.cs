using AsterCache.Core.Trace;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Windows;

/// 변경 저널 읽기: 기록 해석 (USN_RECORD_V2) 은 합성 바이트로, 파일 참조 번호는 실제 임시 파일로 확인합니다 (볼륨 열기는 권한이 필요해 시험하지 않음: 실제 저널은 Live 확인에서).
public sealed class UsnJournalTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static byte[] Record(ulong frn, DateTime time, uint reason, string name)
    {
        byte[] nameBytes = System.Text.Encoding.Unicode.GetBytes(name);
        int length = (60 + nameBytes.Length + 7) & ~7;
        var b = new byte[length];
        BitConverter.GetBytes(length).CopyTo(b, 0);
        BitConverter.GetBytes((ushort)2).CopyTo(b, 4); // 주 버전
        BitConverter.GetBytes(frn).CopyTo(b, 8);
        BitConverter.GetBytes(time.ToFileTimeUtc()).CopyTo(b, 32);
        BitConverter.GetBytes(reason).CopyTo(b, 40);
        BitConverter.GetBytes((ushort)nameBytes.Length).CopyTo(b, 56);
        BitConverter.GetBytes((ushort)60).CopyTo(b, 58);
        nameBytes.CopyTo(b, 60);
        return b;
    }

    [Fact]
    public void OnlyTheWantedFileReferencesAreKeptAndTheFirstRecordGivesTheOldestTime()
    {
        DateTime t = new(2026, 11, 21, 11, 3, 0, DateTimeKind.Utc);
        byte[] data = [.. Record(7, t, 0x2, "a.xpakdata"), .. Record(8, t.AddMinutes(1), 0x4, "b"), .. Record(7, t.AddMinutes(2), 0x4 | 0x80000000, "a.xpakdata")];
        var events = new List<UsnEvent>();
        DateTime? oldest = null;

        Assert.True(UsnJournal.ParseRecords(data, new HashSet<ulong> { 7 }, events, ref oldest));

        Assert.Equal(t, oldest);
        Assert.Equal([new UsnEvent(7, t, 0x2), new UsnEvent(7, t.AddMinutes(2), 0x4 | 0x80000000)], events);
    }

    [Fact]
    public void ARecordOfAnotherVersionWithABadLengthOrTimeOrLeftoverBytesIsAPartialResultAndReportsFailure()
    {
        byte[] good = Record(7, DateTime.UtcNow, 0x2, "a");
        byte[] v3 = Record(7, DateTime.UtcNow, 0x2, "a");
        BitConverter.GetBytes((ushort)3).CopyTo(v3, 4);
        byte[] bad = Record(7, DateTime.UtcNow, 0x2, "a");
        BitConverter.GetBytes(4096).CopyTo(bad, 0);
        byte[] noTime = Record(7, DateTime.UtcNow, 0x2, "a");
        BitConverter.GetBytes(0L).CopyTo(noTime, 32);
        var events = new List<UsnEvent>();
        DateTime? oldest = null;

        Assert.False(UsnJournal.ParseRecords([.. good, .. v3, .. good], new HashSet<ulong> { 7 }, events, ref oldest));
        _ = Assert.Single(events); // 셋째 기록은 읽지 않고 false
        Assert.False(UsnJournal.ParseRecords(bad, new HashSet<ulong> { 7 }, events, ref oldest));
        Assert.False(UsnJournal.ParseRecords(new byte[10], new HashSet<ulong> { 7 }, events, ref oldest)); // 남는 바이트
        DateTime? none = null;
        Assert.False(UsnJournal.ParseRecords(noTime, new HashSet<ulong> { 7 }, [], ref none)); // 시각 이상
        Assert.Null(none);
        Assert.True(UsnJournal.ParseRecords([], new HashSet<ulong> { 7 }, events, ref oldest)); // 빈 버퍼는 정상
    }

    [Fact]
    public void TheFileReferenceOfARealFileIsStableAndDiffersFromAnotherFileAndAMissingFileHasNone()
    {
        string a = _dir.Combine("a.bin");
        string b = _dir.Combine("b.bin");
        File.WriteAllBytes(a, new byte[10]);
        File.WriteAllBytes(b, new byte[10]);

        ulong? first = UsnJournal.FileReference(a);

        Assert.NotNull(first);
        Assert.Equal(first, UsnJournal.FileReference(a));
        Assert.NotEqual(first, UsnJournal.FileReference(b));
        Assert.Null(UsnJournal.FileReference(_dir.Combine("none.bin")));
    }
}
