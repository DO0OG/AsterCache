using AsterCache.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 회복 표본의 하루 상한: 간격 4 분, 드라이브당 24시간 8회·1.5 GiB. 상한에 닿으면 표본을 쉬고, 재배치 보류는 시간 상한이 풀어 줍니다. 가짜 시계·가짜 표본만 씁니다.
public sealed class RecoverySampleBudgetTests
{
    private const string Drive = @"E:\";
    private const long Mib = 1L << 20;
    private static readonly DriveBaseline Both = new(new ProbeSpot(10, 100), new ProbeSpot(20, 80));
    private readonly FakeWorld _w = new();
    private readonly ListLog _log = new();
    private readonly DiskReadLedger _reads;
    private readonly DriveRecovery _r;
    private int _samples; // 표본 (존·참조 두 곳을 읽는 한 회차) 수
    private long _bytesPerRead = 96 * Mib; // 한 곳을 읽을 때 읽는 양 (두 곳이 한 표본: 기본 192 MiB)
    private bool _failRead; // 참이면 읽다가 실패함 (읽은 바이트는 남고 표본 결과는 없음)

    public RecoverySampleBudgetTests()
    {
        _w.ProbeMbps = (_, lcn, round) =>
        {
            if (lcn % 64 == 10)
            {
                _samples++;
            }

            round.BytesRead += _bytesPerRead;
            return _failRead ? null : lcn % 64 == 10 ? 10 : 8; // 기준의 10%: 회복되지 않음
        };
        _w.ProbeStepLcn = _ => 64;
        _reads = new DiskReadLedger(() => _w.Now);
        AutoEnv env = _w.Env();
        env.GameRunningOnDisk = _ => false; // 게임 실행 판정이 연결되지 않으면 보호 중으로 보아 표본을 읽지 않음
        _r = new DriveRecovery(env, _log) { Reads = _reads };
    }

    private void Pass(int minutes)
    {
        _w.Now += TimeSpan.FromMinutes(minutes);
        _r.Poll();
    }

    [Fact]
    public void TheSampleIntervalIsFourMinutesAndASampleStaysAt192MiB()
    {
        Assert.Equal(TimeSpan.FromMinutes(4), DriveRecovery.SampleEvery);
        Assert.Equal(192L << 20, ProbeRound.MaxBytes);
        Assert.Equal(DriveRecovery.MaxSamplesPerDay * ProbeRound.MaxBytes, DriveRecovery.MaxSampleBytesPerDay); // 8 회가 곧 1.5 GiB
        Assert.Equal(1536L << 20, DriveRecovery.MaxSampleBytesPerDay);

        _r.Begin(Drive, Both, TimeSpan.FromMinutes(60));
        Pass(3);
        Assert.Equal(0, _samples);
        Pass(1);
        Assert.Equal(1, _samples);
        Assert.Equal((192 * Mib, 1), _reads.Used(Drive, ReadPurpose.Recovery));
    }

    [Fact]
    public void AfterEightSamplesADayTheDriveIsNotReadAgainUntilTheOldestSampleLeavesTheWindow()
    {
        _r.Begin(Drive, Both, TimeSpan.FromHours(30));

        for (int minute = 0; minute < 24 * 60 - 5; minute++) // 첫 표본 (4 분) 에서 24 시간이 지나기 직전까지
        {
            Pass(1);
        }

        Assert.Equal(DriveRecovery.MaxSamplesPerDay, _samples);
        Assert.Equal((8 * 192 * Mib, 8), _reads.Used(Drive, ReadPurpose.Recovery));
        Assert.Single(_log.Lines, line => line.Contains("recovery samples paused: 8 samples and 1536 MiB read in the last 24 h", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Lines, line => line.Contains("recovery samples resumed", StringComparison.Ordinal));
        Assert.True(_r.IsRecovering(Drive)); // 시간 상한 (30 시간) 안이라 보류는 그대로

        for (int minute = 0; minute < 10; minute++)
        {
            Pass(1);
        }

        Assert.True(_samples > DriveRecovery.MaxSamplesPerDay); // 가장 오래된 표본이 창에서 빠진 뒤 다시 읽음
        Assert.Contains(_log.Lines, line => line.Contains("recovery samples resumed", StringComparison.Ordinal));
    }

    [Fact]
    public void TheByteLimitStopsTheSamplesBeforeTheEighthWhenTheyReadMoreThanExpected()
    {
        _bytesPerRead = 200 * Mib; // 한 표본 400 MiB: 4 번째에서 1.5 GiB 를 넘음
        _r.Begin(Drive, Both, TimeSpan.FromHours(30));

        for (int minute = 0; minute < 3 * 60; minute++)
        {
            Pass(1);
        }

        Assert.Equal(4, _samples);
        Assert.Contains(_log.Lines, line => line.Contains("recovery samples paused: 4 samples and 1600 MiB", StringComparison.Ordinal));
    }

    [Fact]
    public void PausedSamplesLeaveTheRelocationHoldToTheSoftAndHardTimeLimits()
    {
        _r.Begin(Drive, Both, TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(180));

        for (int minute = 0; minute < 61; minute++)
        {
            Pass(1);
        }

        Assert.Equal(DriveRecovery.MaxSamplesPerDay, _samples); // 40 분이면 8 회를 다 읽음
        Assert.False(_r.IsRecovering(Drive)); // 예열·고정이 보는 값은 60 분에 풀림
        Assert.True(_r.IsRelocationHeld(Drive)); // 재배치는 회복이 확인되지 않은 채 계속 보류

        for (int minute = 0; minute < 119; minute++)
        {
            Pass(1);
        }

        Assert.False(_r.IsRelocationHeld(Drive)); // 하드 상한 (180 분) 에 표본 없이도 풀림
        Assert.Equal(DriveRecovery.MaxSamplesPerDay, _samples);
        Assert.Contains(_log.Lines, line => line.Contains("recovery not confirmed after the 180 min hard limit, relocation hold released", StringComparison.Ordinal));
    }

    [Fact]
    public void ASampleCutShortCountsItsBytesButNotOneOfTheEightSamples()
    {
        _failRead = true; // 읽다가 실패한 표본: 판정에 쓰이지 않음
        _r.Begin(Drive, Both, TimeSpan.FromHours(30));

        for (int minute = 0; minute < 3 * 60; minute++)
        {
            Pass(1);
        }

        Assert.Equal(0, _reads.Used(Drive, ReadPurpose.Recovery).Count); // 끝까지 읽은 표본이 없어 횟수는 그대로
        long cutBytes = _reads.Used(Drive, ReadPurpose.RecoveryCut).Bytes;
        Assert.True(cutBytes > 0 && cutBytes < DriveRecovery.MaxSampleBytesPerDay + 192 * Mib); // 바이트 합계 1.5 GiB 가 상한 구실을 함
        Assert.Contains(_log.Lines, line => line.Contains("recovery samples paused: 0 samples and", StringComparison.Ordinal));

        _failRead = false; // 바이트 상한이 풀리면 (24시간 뒤) 정상 표본이 다시 8회까지 셈
        for (int minute = 0; minute < 24 * 60; minute++)
        {
            Pass(1);
        }

        Assert.True(_reads.Used(Drive, ReadPurpose.Recovery).Count > 0);
    }

    [Fact]
    public void ASampleThatReadNothingIsNotCountedAndTheCountIsPerDrive()
    {
        _bytesPerRead = 0; // 읽지 못한 회차 (읽기 전에 미룬 것과 같음)
        _r.Begin(Drive, Both, TimeSpan.FromHours(3));
        for (int minute = 0; minute < 60; minute++)
        {
            Pass(1);
        }

        Assert.True(_samples > DriveRecovery.MaxSamplesPerDay);
        Assert.Equal((0, 0), _reads.Used(Drive, ReadPurpose.Recovery));

        _reads.Add(@"F:\", ReadPurpose.Recovery, 192 * Mib); // 다른 드라이브의 표본은 이 드라이브의 상한에 들지 않음
        Assert.Equal((0, 0), _reads.Used(Drive, ReadPurpose.Recovery));
    }

    [Theory]
    [InlineData(192, 1)] // 6 x 192 + 192 (끊긴 표본) + 192 (새 표본) = 1536 MiB: 시작함
    [InlineData(193, 0)] // 한 MiB 라도 더 읽어 둔 상태면 새 표본이 1.5 GiB 를 넘으므로 시작하지 않음
    public void ASampleStartsOnlyWhenAWholeSampleStillFitsInTheByteLimit(long cutMib, int started)
    {
        _r.Begin(Drive, Both, TimeSpan.FromHours(30));
        for (int i = 0; i < 6; i++)
        {
            _reads.Add(Drive, ReadPurpose.Recovery, ProbeRound.MaxBytes);
        }

        _reads.Add(Drive, ReadPurpose.RecoveryCut, cutMib * Mib); // 읽다 끊긴 표본: 횟수는 아니고 바이트만 셈

        Pass(4);

        Assert.Equal(started, _samples);
    }
}
