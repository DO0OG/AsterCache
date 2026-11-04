using AsterCache.Core.Settings;
using AsterCache.Service;
using Xunit;
using Xunit.Abstractions;

namespace AsterCache.Tests.Service;

/// 실제 읽기 경로 확인 (관리자·ETW 없음): C: 의 임시 폴더에 Unity 구조의 합성 파일 (수십 MiB) 을 만들고, 실제 Prewarmer + 실제 폴더 열거 + 실제 PrefetchVirtualMemory 로 기록 없이 예열합니다.
/// 기본 실행에서는 제외되고 `--filter "Category=Live"` 로 따로 돌립니다.
[Trait("Category", "Live")]
public sealed class ColdPrewarmLiveTests(ITestOutputHelper output) : IDisposable
{
    private const long Mib = 1L << 20;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aster_cold_live_" + Guid.NewGuid().ToString("N"));

    public void Dispose() => Directory.Delete(_root, true);

    private void Write(string rel, long size)
    {
        string path = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using FileStream fs = File.Create(path);
        byte[] block = new byte[Mib];
        Random.Shared.NextBytes(block);
        for (long done = 0; done < size; done += Mib)
        {
            fs.Write(block, 0, (int)Math.Min(Mib, size - done));
        }
    }

    [Fact]
    public void RealFolderIsReadWholeThenByTheUnityProfile()
    {
        Write("Game.exe", 1 * Mib);
        Write("UnityPlayer.dll", 5 * Mib);
        Write("Game_Data/globalgamemanagers", 2 * Mib);
        Write("Game_Data/resources.assets", 40 * Mib);
        Write("Game_Data/Managed/Assembly-CSharp.dll", 3 * Mib);
        Write("Game_Data/StreamingAssets/movie.bin", 30 * Mib); // 프로필에 없음
        GameSettings game = Games.Make("live", "Game.exe") with { OriginRoot = _root, AllowedRoots = [_root], Trace = Path.Combine(_root, "none.prefetch.json") };
        PrewarmEnv real = PrewarmEnv.Create();
        var env = new PrewarmEnv
        {
            AvailableRamMib = SystemMemory.AvailableMib,
            PhysicalRamBytes = SystemMemory.TotalBytes,
            GameRunning = _ => false,
            ZoneBytes = _ => null,
            TraceExists = _ => false,
            RawTraceExists = real.RawTraceExists,
            DiskBytesPerSecond = _ => 0, // 이 PC 의 디스크 상태와 무관하게 읽기 경로만 봄
            ColdPlanner = real.ColdPlanner,
        };
        var log = new ListLog();

        // 폴더 합 81 MiB 가 상한 (200 MiB) 이하: 통째
        PrewarmResult whole = new Prewarmer([game], new FakeStreamer(), env, log).Also(p => p.SetAuto(AutoSettings.Default with { FullPrewarmMaxMib = 200 })).Run(game, "live");
        // 상한 60 MiB 보다 크면 프로필: 합 80 MiB 를 60 MiB 에서 자름 (movie.bin 은 StreamingAssets 규칙으로 뒤에 읽힘, Game.exe 는 .dll 이 아니라 공통 규칙 밖)
        PrewarmResult profile = new Prewarmer([game], new FakeStreamer(), env, log).Also(p => p.SetAuto(AutoSettings.Default with { FullPrewarmMaxMib = 60 })).Run(game, "live");

        output.WriteLine($"whole: {whole.Mode} {whole.Stats}");
        output.WriteLine($"profile: {profile.Mode} {profile.Stats}");
        foreach (string line in log.Lines)
        {
            output.WriteLine(line);
        }

        Assert.True(whole.Ran, whole.SkipReason);
        Assert.Equal(("full", 81 * Mib, 0L), (whole.Mode, whole.Stats!.Bytes, whole.Stats.Errors));
        Assert.True(whole.Stats.Requests > 0);
        Assert.True(profile.Ran, profile.SkipReason);
        Assert.Equal(("profile Unity", 60 * Mib, 0L), (profile.Mode, profile.Stats!.Bytes, profile.Stats.Errors));
    }
}

internal static class PrewarmerExtensions
{
    public static Prewarmer Also(this Prewarmer p, Action<Prewarmer> set)
    {
        set(p);
        return p;
    }
}
