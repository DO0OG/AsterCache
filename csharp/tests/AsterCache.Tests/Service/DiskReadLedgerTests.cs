using AsterCache.Core.Json;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 드라이브별·목적별 24시간 읽기 집계: 이동 창, 시계가 뒤로 간 경우, 상태 파일 왕복, 하루 한 번 로그, 목적 범위.
public sealed class DiskReadLedgerTests
{
    private const long Mib = 1L << 20;
    private const string Root = @"D:\Games\g";
    private DateTimeOffset _now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    private DiskReadLedger Make() => new(() => _now);

    [Fact]
    public void AReadLeavesTheWindowExactlyADayAfterItWasMade()
    {
        DiskReadLedger ledger = Make();
        ledger.Add(Root, ReadPurpose.Idle, 512 * Mib);
        _now += TimeSpan.FromHours(12);
        ledger.Add(Root, ReadPurpose.Idle, 256 * Mib);

        _now += TimeSpan.FromHours(12) - TimeSpan.FromSeconds(1);
        Assert.Equal(768 * Mib, ledger.Used(Root, ReadPurpose.Idle).Bytes);

        _now += TimeSpan.FromSeconds(1); // 첫 읽기가 정확히 24 시간 지남
        Assert.Equal(256 * Mib, ledger.Used(Root, ReadPurpose.Idle).Bytes);

        _now += TimeSpan.FromHours(12);
        Assert.Equal(0, ledger.Used(Root, ReadPurpose.Idle).Bytes);
    }

    [Fact]
    public void EachDrivePurposeAndSpellingOfThePathIsCountedApart()
    {
        DiskReadLedger ledger = Make();
        ledger.Add(@"d:\games\g", ReadPurpose.Idle, 100 * Mib);
        ledger.Add("D:", ReadPurpose.Idle, 50 * Mib);
        ledger.Add(@"E:\Games\h", ReadPurpose.Idle, 7 * Mib);
        ledger.Add(Root, ReadPurpose.Trigger, 9 * Mib);

        Assert.Equal(150 * Mib, ledger.Used(Root, ReadPurpose.Idle).Bytes);
        Assert.Equal(7 * Mib, ledger.Used(@"E:\", ReadPurpose.Idle).Bytes);
        Assert.Equal(9 * Mib, ledger.Used(Root, ReadPurpose.Trigger).Bytes);
        Assert.Equal(0, ledger.Used(Root, ReadPurpose.Recovery).Bytes);
        Assert.Equal(new[] { "D:", "E:" }, ledger.Drives());
    }

    [Fact]
    public void ReadsWithinAMinuteMergeExceptRecoverySamplesWhichStayOneEntryEach()
    {
        DiskReadLedger ledger = Make();
        ledger.Add(Root, ReadPurpose.Idle, Mib);
        _now += TimeSpan.FromSeconds(30);
        ledger.Add(Root, ReadPurpose.Idle, Mib);
        ledger.Add(Root, ReadPurpose.Recovery, 192 * Mib);
        ledger.Add(Root, ReadPurpose.Recovery, 192 * Mib);

        Assert.Equal((2 * Mib, 1), ledger.Used(Root, ReadPurpose.Idle));
        Assert.Equal((384 * Mib, 2), ledger.Used(Root, ReadPurpose.Recovery));
    }

    [Fact]
    public void ASetBackClockDoesNotFreeTheWindow()
    {
        DiskReadLedger ledger = Make();
        ledger.Add(Root, ReadPurpose.Idle, 5 * Mib);
        _now -= TimeSpan.FromHours(10);

        Assert.Equal(5 * Mib, ledger.Used(Root, ReadPurpose.Idle).Bytes);
        Assert.True(ledger.FreeAt(Root, ReadPurpose.Idle, (bytes, _) => bytes < Mib) <= _now + TimeSpan.FromHours(24));
        _now += TimeSpan.FromHours(23);
        Assert.Equal(5 * Mib, ledger.Used(Root, ReadPurpose.Idle).Bytes); // 당겨 둔 시각 (뒤로 간 지금) 부터 24 시간
        _now += TimeSpan.FromHours(1);
        Assert.Equal(0, ledger.Used(Root, ReadPurpose.Idle).Bytes);
    }

    [Fact]
    public void FreeAtIsWhenTheOldestReadsHaveLeftTheWindowFarEnough()
    {
        DiskReadLedger ledger = Make();
        DateTimeOffset first = _now;
        ledger.Add(Root, ReadPurpose.Idle, 600 * Mib);
        _now += TimeSpan.FromHours(2);
        DateTimeOffset second = _now;
        ledger.Add(Root, ReadPurpose.Idle, 600 * Mib);
        _now += TimeSpan.FromHours(2);
        ledger.Add(Root, ReadPurpose.Idle, 600 * Mib);

        Assert.Equal(first + TimeSpan.FromHours(24), ledger.FreeAt(Root, ReadPurpose.Idle, (bytes, _) => bytes < 1500 * Mib));
        Assert.Equal(second + TimeSpan.FromHours(24), ledger.FreeAt(Root, ReadPurpose.Idle, (bytes, _) => bytes < 1000 * Mib));
        Assert.Equal(_now, ledger.FreeAt(Root, ReadPurpose.Idle, (bytes, _) => bytes < 2000 * Mib)); // 이미 아래
    }

    [Fact]
    public void TheRecordRoundTripsThroughTheStatusJsonAndDropsWhatIsOlderThanADayOnTheNextLookup()
    {
        DiskReadLedger first = Make();
        first.Add(Root, ReadPurpose.Idle, 300 * Mib);
        first.Add(Root, ReadPurpose.Trigger, 40 * Mib);
        first.Add(Root, ReadPurpose.Recovery, 192 * Mib);
        _now += TimeSpan.FromHours(1);
        first.Add(Root, ReadPurpose.Recovery, 192 * Mib);

        JObject saved = (JObject)JsonRead.Parse(PyJson.Dumps(first.ToJson(out _), indent: 2, ensureAscii: false))!;
        DiskReadLedger second = Make();
        second.Load(saved);

        Assert.Equal(300 * Mib, second.Used(Root, ReadPurpose.Idle).Bytes);
        Assert.Equal(40 * Mib, second.Used(Root, ReadPurpose.Trigger).Bytes);
        Assert.Equal((384 * Mib, 2), second.Used(Root, ReadPurpose.Recovery));

        _now += TimeSpan.FromHours(23); // 첫 읽기들은 24 시간이 지나고 한 시간 뒤의 표본 하나만 남음
        Assert.Equal((192 * Mib, 1), second.Used(Root, ReadPurpose.Recovery));
        Assert.Equal(0, second.Used(Root, ReadPurpose.Idle).Bytes);
    }

    [Fact]
    public void ABrokenSavedRecordIsSkippedWithoutLosingTheGoodEntries()
    {
        DiskReadLedger ledger = Make();
        JObject drives = new() { { "D:", new JObject { { "idle", "garbage;" + _now.ToString("O") + "=" + 3 * Mib + ";x=y;" + _now.ToString("O") + "=-5" }, { "nonsense", "1=2" } } }, { "E:", "not an object" } };

        ledger.Load(new JObject { { "drives", drives }, { "logged_at", "not a time" } });

        Assert.Equal(3 * Mib, ledger.Used(Root, ReadPurpose.Idle).Bytes);
        Assert.Equal(new[] { "D:" }, ledger.Drives());
    }

    [Fact]
    public void TheDailyLogIsDueOnceADayOnlyWhenSomethingWasReadAlsoAfterARestart()
    {
        DiskReadLedger ledger = Make();
        Assert.False(ledger.TakeDailyLog()); // 읽은 것이 없음

        ledger.Add(Root, ReadPurpose.Idle, Mib);
        Assert.True(ledger.TakeDailyLog());
        Assert.False(ledger.TakeDailyLog());

        DiskReadLedger restarted = Make();
        restarted.Load(ledger.ToJson(out _));
        _now += TimeSpan.FromHours(23);
        restarted.Add(Root, ReadPurpose.Idle, Mib);
        Assert.False(restarted.TakeDailyLog()); // 재시작해도 마지막 로그에서 24 시간이 안 지남

        _now += TimeSpan.FromHours(1);
        Assert.True(restarted.TakeDailyLog());
        _now -= TimeSpan.FromHours(5); // 시계가 뒤로 가면 다음 날 로그가 영영 막히지 않게 다시 남김
        Assert.True(restarted.TakeDailyLog());
    }

    [Fact]
    public void ASeparateSaveIsDueOnlyWithEnoughUnsavedBytesAndTenMinutesSinceTheLastSave()
    {
        DiskReadLedger ledger = Make();
        _now += TimeSpan.FromHours(1);
        Assert.False(ledger.HasUnsaved);

        ledger.Add(Root, ReadPurpose.Idle, DiskReadLedger.SaveEveryBytes - Mib);
        Assert.True(ledger.HasUnsaved);
        Assert.False(ledger.SaveDue()); // 바이트가 모자람

        ledger.Add(Root, ReadPurpose.Idle, Mib);
        Assert.True(ledger.SaveDue()); // 처음에는 마지막 저장이 없어 바로

        _ = ledger.ToJson(out DiskReadLedger.SavedMark mark);
        Assert.True(ledger.HasUnsaved); // 쓰기가 성공했다고 알리기 전에는 저장한 것이 아님 (실패하면 다시 시도)
        ledger.Add(Root, ReadPurpose.Idle, Mib); // 쓰는 도중에 더해진 변경
        ledger.CommitSaved(mark);
        Assert.True(ledger.HasUnsaved); // 그 변경은 아직 저장하지 않은 것으로 남음
        _ = ledger.ToJson(out mark);
        ledger.CommitSaved(mark);
        Assert.False(ledger.HasUnsaved);
        ledger.Add(Root, ReadPurpose.Idle, DiskReadLedger.SaveEveryBytes);
        Assert.False(ledger.SaveDue()); // 바이트는 충분하지만 마지막 저장 뒤 10 분이 안 지남

        _now += DiskReadLedger.SaveEvery;
        Assert.True(ledger.SaveDue());
    }

    [Fact]
    public void TheScopeSetsThePurposeAndPutsThePreviousOneBack()
    {
        Assert.Equal(ReadPurpose.Trigger, DiskReadLedger.Current); // 범위 밖은 계기 있는 읽기
        using (DiskReadLedger.Scope(ReadPurpose.Idle))
        {
            Assert.Equal(ReadPurpose.Idle, DiskReadLedger.Current);
            using (DiskReadLedger.Scope(ReadPurpose.Trigger))
            {
                Assert.Equal(ReadPurpose.Trigger, DiskReadLedger.Current);
            }

            Assert.Equal(ReadPurpose.Idle, DiskReadLedger.Current);
        }

        Assert.Equal(ReadPurpose.Trigger, DiskReadLedger.Current);
    }
}
