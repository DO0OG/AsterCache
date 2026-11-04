using AsterCache.Core.Json;
using AsterCache.Core.Profiles;
using Xunit;

namespace AsterCache.Tests.Core;

/// 엔진 기본 프로필: engines.json (임베디드 리소스) 읽기, glob 매칭, 감지, 선택. 파일 시스템에는 닿지 않습니다.
public sealed class EngineProfilesTests
{
    private const long Mib = 1L << 20;

    private static IReadOnlyList<(string Path, long Size)> Files(params (string, long)[] files) => files;

    [Theory]
    [InlineData("UnityPlayer.dll", "unityplayer.DLL", true)]
    [InlineData("<name>_Data/globalgamemanagers*", "Game_Data/globalgamemanagers.assets", true)]
    [InlineData("<name>_Data/globalgamemanagers*", "Game_Data/Managed/globalgamemanagers", false)]
    [InlineData("<name>_Data/Managed/*.dll", @"Game_Data\Managed\UnityEngine.dll", true)]
    [InlineData("MonoBleedingEdge/**/*.dll", "MonoBleedingEdge/EmbedRuntime/mono.dll", true)]
    [InlineData("MonoBleedingEdge/**/*.dll", "MonoBleedingEdge/a/b/c/mono.dll", true)]
    [InlineData("MonoBleedingEdge/**/*.dll", "MonoBleedingEdge/mono.dll", true)] // ** 는 폴더 0 개도 됨
    [InlineData("MonoBleedingEdge/**/*.dll", "Other/mono.dll", false)]
    [InlineData("**/*_dir.vpk", "hl2_dir.vpk", true)]
    [InlineData("**/*_dir.vpk", "hl2/hl2_pak_dir.vpk", true)]
    [InlineData("**/*_dir.vpk", "hl2/hl2_pak_000.vpk", false)]
    [InlineData("<Game>/Content/Paks/*.pak", "MyGame/Content/Paks/pakchunk0.pak", true)]
    [InlineData("<Game>/Content/Paks/*.pak", "MyGame/Content/Paks/sub/pakchunk0.pak", false)]
    [InlineData("<Game>/Content/Paks/*.pak", "Content/Paks/pakchunk0.pak", false)] // <Game> 은 폴더 한 칸이 있어야 함
    [InlineData("a?c.txt", "abc.txt", true)]
    [InlineData("a?c.txt", "ac.txt", false)]
    [InlineData("*", "any", true)]
    [InlineData("*.dll", "dir/x.dll", false)] // * 는 폴더 칸을 넘지 않음
    public void GlobMatching(string glob, string path, bool expected) => Assert.Equal(expected, EngineProfiles.Match(glob, path));

    [Fact]
    public void BuiltinResourceHasUnityUnrealSourceAndCommonRule()
    {
        EngineProfiles p = EngineProfiles.Builtin;

        Assert.Equal(["Unity", "Unreal", "Source", "Riot", "MapleStory", "CASC", "SCS", "EA FC Online"], p.Engines.Select(e => e.Name));
        Assert.Contains(p.Engines[1].Include, i => i.Glob.EndsWith("*.pak", StringComparison.Ordinal) && i is { HeadMib: 256, TailMib: 64 }); // 인덱스·푸터가 파일 끝에 있음
        Assert.Contains(p.Engines[1].Include, i => i.Glob.EndsWith("*.utoc", StringComparison.Ordinal) && i is { HeadMib: 0, TailMib: 0 }); // .utoc 는 전체
        Assert.Contains(p.Engines[1].Include, i => i.Glob.EndsWith("*.ucas", StringComparison.Ordinal) && i is { HeadMib: 64, TailMib: 0 });
        Assert.Contains(p.Common, c => c.Glob.StartsWith(EngineProfiles.ExeDir, StringComparison.Ordinal));
    }

    [Fact]
    public void ParseRejectsBadShapes()
    {
        Assert.Throws<PyValueException>(() => EngineProfiles.Parse("[]"));
        Assert.Throws<PyValueException>(() => EngineProfiles.Parse("""{"engines":[{"name":"X","detect":"a","include":[]}]}"""));
        Assert.Throws<PyValueException>(() => EngineProfiles.Parse("""{"engines":[{"name":"X","detect":[],"include":[{"glob":"a","head_mib":-1}]}]}"""));
        Assert.Throws<PyValueException>(() => EngineProfiles.Parse("""{"engines":[{"name":"X","detect":[],"include":[{"glob":"a","tail_mib":-1}]}]}"""));
        Assert.Throws<PyValueException>(() => EngineProfiles.Parse("""{"engines":[{"name":"X","detect":[],"include":[{"glob":"a","tail_mib":"1"}]}]}"""));
        Assert.Empty(EngineProfiles.Parse("{}").Engines);
    }

    [Fact]
    public void DetectIsPerEngineAndAnyRuleSuffices()
    {
        EngineProfiles p = EngineProfiles.Builtin;

        Assert.Equal(["Unity"], p.Detect(Files(("UnityPlayer.dll", 1))).Select(e => e.Name));
        Assert.Equal(["Unity"], p.Detect(Files(("X_Data/globalgamemanagers", 1))).Select(e => e.Name));
        Assert.Equal(["Unreal"], p.Detect(Files(("G/Content/Paks/a.utoc", 1))).Select(e => e.Name));
        Assert.Equal(["Source"], p.Detect(Files(("hl2/hl2_dir.vpk", 1))).Select(e => e.Name));
        Assert.Equal(["Unity", "Source"], p.Detect(Files(("UnityPlayer.dll", 1), ("a_dir.vpk", 1))).Select(e => e.Name));
        Assert.Empty(p.Detect(Files(("game.exe", 1), ("data/big.dat", 1))));
    }

    [Fact]
    public void NoEngineMeansNothingIsPickedEvenForTheCommonRule()
    {
        ProfileSelection sel = EngineProfiles.Builtin.Select(Files(("game.exe", 1), ("helper.dll", 5)), string.Empty, 100 * Mib);

        Assert.Empty(sel.Engines);
        Assert.Empty(sel.Picks);
    }

    [Fact]
    public void CommonRuleComesFirstAndFollowsTheExeFolder()
    {
        IReadOnlyList<(string, long)> files = Files(("a.dll", 1), ("sub/b.dll", 2), ("sub/Game.exe", 3), ("UnityPlayer.dll", 4));

        ProfileSelection root = EngineProfiles.Builtin.Select(files, string.Empty, 100 * Mib);
        ProfileSelection sub = EngineProfiles.Builtin.Select(files, "sub", 100 * Mib);

        Assert.Equal(["a.dll", "UnityPlayer.dll"], root.Picks.Select(x => x.Path)); // 공통 (루트의 *.dll, 경로 순) 에 UnityPlayer.dll 도 있어 한 번만
        Assert.Equal(["sub/b.dll", "UnityPlayer.dll"], sub.Picks.Select(x => x.Path));
        Assert.Equal(["UnityPlayer.dll"], EngineProfiles.Builtin.Select(files, null, 100 * Mib).Picks.Select(x => x.Path)); // exe 폴더를 모르면 공통 규칙 없음
    }

    [Fact]
    public void UnrealTakesChunk0WholeAndOthersTheirHeadAndTail()
    {
        long big = 300 * Mib;
        long huge = 400 * Mib;
        IReadOnlyList<(string, long)> files = Files(
            ("G/Content/Paks/pakchunk0-Win.pak", big), ("G/Content/Paks/pakchunk0-Win.ucas", 10 * Mib),
            ("G/Content/Paks/pakchunk1-Win.pak", huge), ("G/Content/Paks/pakchunk2-Win.pak", 20 * Mib), ("G/Binaries/Win64/x.dll", 5));

        ProfileSelection sel = EngineProfiles.Builtin.Select(files, null, 10_000 * Mib);

        Assert.Equal(["Unreal"], sel.Engines);
        Assert.Equal(big, sel.Picks.Single(x => x.Path.EndsWith("pakchunk0-Win.pak", StringComparison.Ordinal)).Bytes);
        List<ProfilePick> chunk1 = [.. sel.Picks.Where(x => x.Path.EndsWith("pakchunk1-Win.pak", StringComparison.Ordinal))];
        Assert.Equal([new ProfilePick("G/Content/Paks/pakchunk1-Win.pak", 256 * Mib), new ProfilePick("G/Content/Paks/pakchunk1-Win.pak", 64 * Mib, huge - 64 * Mib)], chunk1); // 앞 256 MiB + 끝 64 MiB
        Assert.Equal(20 * Mib, sel.Picks.Single(x => x.Path.EndsWith("pakchunk2-Win.pak", StringComparison.Ordinal)).Bytes); // 앞+뒤보다 작으면 전체 한 번
        Assert.Equal(5, sel.Picks.Single(x => x.Path.EndsWith("x.dll", StringComparison.Ordinal)).Bytes);
    }

    [Theory]
    [InlineData(100, 1)] // 앞+뒤 (320 MiB) 이하: 겹치지 않게 전체 한 번
    [InlineData(320, 1)] // 딱 맞닿아도 한 번
    [InlineData(321, 2)]
    public void HeadAndTailNeverOverlap(long sizeMib, int picks)
    {
        ProfileSelection sel = EngineProfiles.Builtin.Select(Files(("G/Content/Paks/other.pak", sizeMib * Mib)), null, 10_000 * Mib);

        Assert.Equal(picks, sel.Picks.Count);
        Assert.Equal(picks == 1 ? sizeMib * Mib : 320 * Mib, sel.TotalBytes);
        if (picks == 2)
        {
            Assert.Equal(sizeMib * Mib - 64 * Mib, sel.Picks[1].Offset);
        }
    }

    [Fact]
    public void TailIsCutByTheBudgetToTheVeryEndOfTheFile()
    {
        IReadOnlyList<(string, long)> files = Files(("G/Content/Paks/other.pak", 1000 * Mib));

        ProfileSelection sel = EngineProfiles.Builtin.Select(files, null, 260 * Mib);

        Assert.Equal(260 * Mib, sel.TotalBytes);
        Assert.Equal(new ProfilePick("G/Content/Paks/other.pak", 4 * Mib, 996 * Mib), sel.Picks[1]); // 머리 256 + 꼬리 4 (파일의 맨 끝쪽)
    }

    [Fact]
    public void ParseReadsTailMib()
    {
        EngineProfiles p = EngineProfiles.Parse("""{"engines":[{"name":"X","detect":[],"include":[{"glob":"a","head_mib":4,"tail_mib":2}]}]}""");

        Assert.Equal(new ProfileInclude("a", 4, 2), p.Engines[0].Include[0]);
    }

    [Fact]
    public void TotalIsCutFromTheFrontAndTheOverflowingFileIsTakenPartially()
    {
        IReadOnlyList<(string, long)> files = Files(
            ("UnityPlayer.dll", 10), ("X_Data/globalgamemanagers", 20), ("X_Data/resources.assets", 1000), ("X_Data/level0", 50));

        ProfileSelection sel = EngineProfiles.Builtin.Select(files, null, 100);

        Assert.Equal(100, sel.TotalBytes);
        Assert.Equal(["X_Data/globalgamemanagers", "X_Data/resources.assets"], sel.Picks.Select(x => x.Path)); // 규칙 순서: globalgamemanagers → resources.assets (80 B 만) → 끝
        Assert.Equal(80, sel.Picks[1].Bytes);
        Assert.Empty(EngineProfiles.Builtin.Select(files, null, 0).Picks);
    }

    [Fact]
    public void SeveralEnginesAreMergedInProfileOrder()
    {
        IReadOnlyList<(string, long)> files = Files(("UnityPlayer.dll", 4), ("hl2/a_dir.vpk", 7), ("bin/engine.dll", 3));

        ProfileSelection sel = EngineProfiles.Builtin.Select(files, null, 1000);

        Assert.Equal(["Unity", "Source"], sel.Engines);
        Assert.Equal(["UnityPlayer.dll", "hl2/a_dir.vpk", "bin/engine.dll"], sel.Picks.Select(x => x.Path));
    }

    [Fact]
    public void EmptyFilesAreNotPicked()
    {
        ProfileSelection sel = EngineProfiles.Builtin.Select(Files(("UnityPlayer.dll", 0), ("X_Data/level0", 5)), null, 100);

        Assert.Equal(["X_Data/level0"], sel.Picks.Select(x => x.Path));
    }

    [Fact]
    public void RiotCommonUiMap11AndMap12AreReadWholeBeforeTheOtherMapsGetAHead()
    {
        IReadOnlyList<(string, long)> files = Files(
            ("Game/DATA/FINAL/Global.wad.client", 1 * Mib),
            ("Game/DATA/FINAL/UI.wad.client", 431 * Mib),
            ("Game/DATA/FINAL/Maps/Shipping/Map30.wad.client", 617 * Mib),
            ("Game/DATA/FINAL/Maps/Shipping/Map11.wad.client", 2412 * Mib),
            ("Game/DATA/FINAL/Maps/Shipping/Map11.ko_KR.wad.client", 61 * Mib),
            ("Game/DATA/FINAL/Maps/Shipping/Map12.wad.client", 1328 * Mib),
            ("Game/DATA/FINAL/Maps/Shipping/Map12.ko_KR.wad.client", 138 * Mib),
            ("Game/DATA/FINAL/Maps/Shipping/Common.wad.client", 106 * Mib),
            ("Game/DATA/FINAL/Champions/Ahri.wad.client", 174 * Mib));

        ProfileSelection sel = EngineProfiles.Builtin.Select(files, null, 100L * 1024 * Mib);

        Assert.Equal(["Riot"], sel.Engines);
        long[] whole = [1 * Mib, 431 * Mib, 106 * Mib, 1328 * Mib, 138 * Mib, 2412 * Mib, 61 * Mib, 617 * Mib];
        Assert.Equal(whole, sel.Picks.Where(p => p.Path != "Game/DATA/FINAL/Champions/Ahri.wad.client").Select(p => p.Bytes));
        Assert.Equal(64 * Mib, sel.Picks.Single(p => p.Path == "Game/DATA/FINAL/Champions/Ahri.wad.client").Bytes);
    }

    [Fact]
    public void RiotCommonAndMap11ComeBeforeTheMapsHeadRule()
    {
        IReadOnlyList<(string, long)> files = Files(
            ("Game/DATA/FINAL/Global.wad.client", 1 * Mib),
            ("Game/DATA/FINAL/Maps/Shipping/Map30.wad.client", 617 * Mib),
            ("Game/DATA/FINAL/Maps/Shipping/Map11.wad.client", 2412 * Mib),
            ("Game/DATA/FINAL/Maps/Shipping/Common.wad.client", 106 * Mib));

        ProfileSelection sel = EngineProfiles.Builtin.Select(files, null, 100L * 1024 * Mib);

        Assert.Equal(["Game/DATA/FINAL/Global.wad.client", "Game/DATA/FINAL/Maps/Shipping/Common.wad.client", "Game/DATA/FINAL/Maps/Shipping/Map11.wad.client", "Game/DATA/FINAL/Maps/Shipping/Map30.wad.client"], sel.Picks.Select(p => p.Path));
    }

    [Fact]
    public void RiotFullMapRulesComeAfterChampionsAndTheOtherMapsGetAHead()
    {
        IReadOnlyList<(string, long)> files = Files(
            ("Game/DATA/FINAL/Global.wad.client", 1 * Mib),
            ("Game/DATA/FINAL/Maps/Shipping/Map21.wad.client", 300 * Mib),
            ("Game/DATA/FINAL/Maps/Shipping/Map22.wad.client", 2625 * Mib),
            ("Game/DATA/FINAL/Maps/Shipping/Map453.wad.client", 470 * Mib),
            ("Game/DATA/FINAL/Companions.wad.client", 50 * Mib),
            ("Game/DATA/FINAL/Champions/Ahri.wad.client", 174 * Mib),
            ("Game/League of Legends.exe", 50 * Mib));

        ProfileSelection sel = EngineProfiles.Builtin.Select(files, null, 100L * 1024 * Mib);

        Assert.Equal(
            ["Game/DATA/FINAL/Global.wad.client", "Game/DATA/FINAL/Maps/Shipping/Map453.wad.client", "Game/League of Legends.exe", "Game/DATA/FINAL/Champions/Ahri.wad.client", "Game/DATA/FINAL/Maps/Shipping/Map22.wad.client", "Game/DATA/FINAL/Companions.wad.client", "Game/DATA/FINAL/Maps/Shipping/Map21.wad.client"],
            sel.Picks.Select(p => p.Path));
        Assert.Equal(2625 * Mib, sel.Picks.Single(p => p.Path.EndsWith("Map22.wad.client", StringComparison.Ordinal)).Bytes);
        Assert.Equal(470 * Mib, sel.Picks.Single(p => p.Path.EndsWith("Map453.wad.client", StringComparison.Ordinal)).Bytes);
        Assert.Equal(50 * Mib, sel.Picks.Single(p => p.Path.EndsWith("Companions.wad.client", StringComparison.Ordinal)).Bytes);
        Assert.Equal(64 * Mib, sel.Picks.Single(p => p.Path.EndsWith("Map21.wad.client", StringComparison.Ordinal)).Bytes); // 그 밖의 맵은 64 MiB 머리
    }

    [Fact]
    public void TftLayoutIsDetectedAsUnrealAndNotRiot()
    {
        // TFT 설치 구조 (루트 Engine·TFT 폴더, TFTClient.exe). 가정: 파크 파일은 TFT/Content/Paks 에 있음 (실제 폴더는 읽지 않음)
        IReadOnlyList<(string, long)> files = Files(
            ("TFTClient.exe", 100 * Mib), ("Engine/Binaries/Win64/EpicWebHelper.exe", 900 * Mib), ("Engine/Binaries/Win64/UnrealEngine.dll", 30 * Mib),
            ("TFT/Binaries/Win64/TFTClient-Win64-Shipping.exe", 50 * Mib), ("TFT/Content/Paks/pakchunk0-WindowsClient.pak", 2000 * Mib),
            ("TFT/Content/Paks/pakchunk1-WindowsClient.pak", 1000 * Mib));

        Assert.Equal(["Unreal"], EngineProfiles.Builtin.Detect(files).Select(e => e.Name));

        ProfileSelection sel = EngineProfiles.Builtin.Select(files, null, 10_000 * Mib);

        Assert.Equal(["Unreal"], sel.Engines);
        Assert.Equal(2000 * Mib, sel.Picks.Single(p => p.Path == "TFT/Content/Paks/pakchunk0-WindowsClient.pak").Bytes);
        Assert.Equal(30 * Mib, sel.Picks.Single(p => p.Path == "Engine/Binaries/Win64/UnrealEngine.dll").Bytes);
        Assert.Equal(320 * Mib, sel.Picks.Where(p => p.Path == "TFT/Content/Paks/pakchunk1-WindowsClient.pak").Sum(p => p.Bytes)); // 앞 256 + 끝 64
    }

    [Theory]
    [InlineData("UnityPlayer.dll", "Unity")]
    [InlineData("G_Data/globalgamemanagers", "Unity")]
    [InlineData("G/Content/Paks/pakchunk0-Win.pak", "Unreal")]
    [InlineData("hl2/hl2_dir.vpk", "Source")]
    [InlineData("Game/DATA/FINAL/Global.wad.client", "Riot")]
    [InlineData("Data.wz", "MapleStory")]
    [InlineData("Data/Map.wz", "MapleStory")]
    [InlineData("data/casc/data/0000000001.idx", "CASC")]
    [InlineData("base.scs", "SCS")]
    [InlineData("fczf.exe", "EA FC Online")]
    [InlineData("data/data_default.big", "EA FC Online")]
    public void EachMarkerDetectsOnlyItsOwnEngine(string path, string engine) =>
        Assert.Equal([engine], EngineProfiles.Builtin.Detect(Files((path, 1L))).Select(e => e.Name));

    [Fact]
    public void UnityReadsLevelsStreamingAssetsAndIl2cppDlls()
    {
        IReadOnlyList<(string, long)> files = Files(
            ("UnityPlayer.dll", 1), ("G_Data/globalgamemanagers", 1), ("G_Data/level3", 10), ("G_Data/level3.resS", 20),
            ("G_Data/sharedassets0.resS", 30), ("G_Data/StreamingAssets/video.bundle", 900 * Mib), ("G_Data/il2cpp_data/Managed/x.dll", 2));

        ProfileSelection sel = EngineProfiles.Builtin.Select(files, null, 10_000 * Mib);

        Assert.Equal(["Unity"], sel.Engines);
        Assert.Equal(10, sel.Picks.Single(p => p.Path == "G_Data/level3").Bytes);
        Assert.Equal(20, sel.Picks.Single(p => p.Path == "G_Data/level3.resS").Bytes);
        Assert.Equal(30, sel.Picks.Single(p => p.Path == "G_Data/sharedassets0.resS").Bytes);
        Assert.Equal(256 * Mib, sel.Picks.Single(p => p.Path == "G_Data/StreamingAssets/video.bundle").Bytes);
        Assert.Equal(2, sel.Picks.Single(p => p.Path == "G_Data/il2cpp_data/Managed/x.dll").Bytes);
    }

    [Fact]
    public void SourcePak01DirIsWholeAndPak01ChunksAreHeadOnly()
    {
        IReadOnlyList<(string, long)> files = Files(("hl2/pak01_dir.vpk", 50 * Mib), ("hl2/pak01_000.vpk", 900 * Mib), ("bin/engine.dll", 3));

        ProfileSelection sel = EngineProfiles.Builtin.Select(files, null, 10_000 * Mib);

        Assert.Equal(["Source"], sel.Engines);
        Assert.Equal(50 * Mib, sel.Picks.Single(p => p.Path == "hl2/pak01_dir.vpk").Bytes);
        Assert.Equal(256 * Mib, sel.Picks.Single(p => p.Path == "hl2/pak01_000.vpk").Bytes);
        Assert.Equal(3, sel.Picks.Single(p => p.Path == "bin/engine.dll").Bytes);
    }

    [Fact]
    public void MapleStoryReadsDataWzWholeAndOtherWzAndImgHeads()
    {
        IReadOnlyList<(string, long)> files = Files(
            ("MapleStory.exe", 5 * Mib), ("Data.wz", 2000 * Mib), ("Map.wz", 3000 * Mib), ("Data/Mob.wz", 4000 * Mib), ("Data/Skill.img", 500 * Mib));

        ProfileSelection sel = EngineProfiles.Builtin.Select(files, null, 10_000 * Mib);

        Assert.Equal(["MapleStory"], sel.Engines);
        Assert.Equal(2000 * Mib, sel.Picks.Single(p => p.Path == "Data.wz").Bytes);
        Assert.Equal(1024 * Mib, sel.Picks.Single(p => p.Path == "Map.wz").Bytes);
        Assert.Equal(1024 * Mib, sel.Picks.Single(p => p.Path == "Data/Mob.wz").Bytes);
        Assert.Equal(500 * Mib, sel.Picks.Single(p => p.Path == "Data/Skill.img").Bytes);
        Assert.Equal(5 * Mib, sel.Picks.Single(p => p.Path == "MapleStory.exe").Bytes);
    }

    [Fact]
    public void CascReadsIndexesWholeAndDataFilesHeadOnly()
    {
        IReadOnlyList<(string, long)> files = Files(
            ("Overwatch.exe", 5 * Mib), ("data/casc/data/0000000001.idx", 2 * Mib), ("data/casc/data/data.000", 1000 * Mib),
            ("data/casc/data/data.001", 1000 * Mib), ("data/casc/data/data.config", 1 * Mib));

        ProfileSelection sel = EngineProfiles.Builtin.Select(files, null, 10_000 * Mib);

        Assert.Equal(["CASC"], sel.Engines);
        Assert.Equal(2 * Mib, sel.Picks.Single(p => p.Path == "data/casc/data/0000000001.idx").Bytes);
        Assert.Equal(256 * Mib, sel.Picks.Single(p => p.Path == "data/casc/data/data.000").Bytes);
        Assert.Equal(256 * Mib, sel.Picks.Single(p => p.Path == "data/casc/data/data.001").Bytes);
        Assert.Equal(5 * Mib, sel.Picks.Single(p => p.Path == "Overwatch.exe").Bytes);
        Assert.DoesNotContain(sel.Picks, p => p.Path == "data/casc/data/data.config");
    }

    [Fact]
    public void ScsReadsTheBaseArchivesHeadOnly()
    {
        IReadOnlyList<(string, long)> files = Files(("base.scs", 3000 * Mib), ("def.scs", 100 * Mib), ("dlc_x.scs", 300 * Mib));

        ProfileSelection sel = EngineProfiles.Builtin.Select(files, null, 10_000 * Mib);

        Assert.Equal(["SCS"], sel.Engines);
        Assert.Equal(512 * Mib, sel.Picks.Single(p => p.Path == "base.scs").Bytes);
        Assert.Equal(100 * Mib, sel.Picks.Single(p => p.Path == "def.scs").Bytes);
        Assert.DoesNotContain(sel.Picks, p => p.Path == "dlc_x.scs");
    }

    [Fact]
    public void EaFcReadsDefaultBigFirstThenExeDllThenBigHeadsAndNeverTheCipherFolder()
    {
        IReadOnlyList<(string, long)> files = Files(
            ("fczf.exe", 30 * Mib), ("fclauncher.exe", 1 * Mib), ("libcef.dll", 5 * Mib), ("data/data_default.big", 128 * Mib),
            ("data/kor_kr.big", 790 * Mib), ("data/data_graphic1.big", 1500 * Mib), ("BlackCipher/BlackCall64.aes", 1 * Mib));

        ProfileSelection sel = EngineProfiles.Builtin.Select(files, null, 10_000 * Mib);

        Assert.Equal(["EA FC Online"], sel.Engines);
        Assert.Equal("data/data_default.big", sel.Picks[0].Path);
        Assert.Equal(128 * Mib, sel.Picks[0].Bytes);
        Assert.Equal(30 * Mib, sel.Picks.Single(p => p.Path == "fczf.exe").Bytes);
        Assert.Equal(256 * Mib, sel.Picks.Single(p => p.Path == "data/kor_kr.big").Bytes);
        Assert.Equal(256 * Mib, sel.Picks.Single(p => p.Path == "data/data_graphic1.big").Bytes);
        Assert.DoesNotContain(sel.Picks, p => p.Path.StartsWith("BlackCipher/", StringComparison.Ordinal));
    }
}
