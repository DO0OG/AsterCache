using System.Text;
using AsterCache.Core.Prefetch;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Windows;

/// 실제 C:\Windows\Prefetch 의 .pf 를 읽어 해석합니다. 폴더 읽기는 관리자가 필요해서 (SYSTEM 서비스와 같은 조건) 기본 실행에서는 제외되고
/// `--filter "Category=Live"` 로 승격된 터미널에서만 돌립니다. 폴더를 읽을 수 없으면 아무것도 확인하지 않고 통과합니다.
/// ASTER_PF_SUMMARY 에 경로를 주면 요약 (개수·블록 수 분포·플래그 분포) 을 그 파일에 씁니다. 파일 경로 자체는 쓰지 않습니다.
[Trait("Category", "Live")]
public sealed class PrefetchFilesLiveTests
{
    [Fact]
    public void RealPrefetchFilesParseAndSummarize()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");
        string[] files;
        try
        {
            files = Directory.GetFiles(dir, "*.pf");
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return; // 관리자가 아님
        }

        var sb = new StringBuilder();
        int parsed = 0;
        int mam = 0;
        var versions = new SortedDictionary<uint, int>();
        var flagCounts = new SortedDictionary<uint, int>();
        long fileTotal = 0;
        long mapped = 0;
        long missing = 0;
        IReadOnlyDictionary<uint, string> letters = PrefetchFiles.DriveSerials();
        foreach (string f in files)
        {
            byte[] raw = File.ReadAllBytes(f);
            mam += raw.Length > 3 && raw[0] == 'M' ? 1 : 0;
            if (PrefetchFiles.Read(f) is not { } info)
            {
                sb.AppendLine("unparsed: " + Path.GetFileName(f));
                continue;
            }

            parsed++;
            versions[info.Version] = versions.GetValueOrDefault(info.Version) + 1;
            foreach (PfFile pf in info.Files)
            {
                fileTotal++;
                flagCounts[pf.Flags] = flagCounts.GetValueOrDefault(pf.Flags) + 1;
                if (PrefetchFiles.DosPath(pf.Path, info.Volumes, letters) is not { } dos)
                {
                    continue;
                }

                mapped++;
                if (!File.Exists(dos))
                {
                    missing++;
                    continue;
                }

            }
        }

        sb.AppendLine($"pf files: {files.Length}, mam: {mam}, parsed: {parsed}");
        sb.AppendLine("versions: " + string.Join(", ", versions.Select(kv => $"v{kv.Key}={kv.Value}")));
        sb.AppendLine($"file entries: {fileTotal}, mapped to a drive letter: {mapped}, file missing: {missing}");
        sb.AppendLine("flags: " + string.Join(", ", flagCounts.Select(kv => $"0x{kv.Key:X}={kv.Value}")));
        sb.AppendLine("drive serials known: " + letters.Count);

        // 메모장처럼 흔한 프로그램 하나: 파일 수와 볼륨
        foreach (string f in files.Where(f => Path.GetFileName(f).StartsWith("NOTEPAD.EXE-", StringComparison.OrdinalIgnoreCase)).Take(2))
        {
            if (PrefetchFiles.Read(f) is { } n)
            {
                sb.AppendLine($"NOTEPAD: v{n.Version} exe={n.ExeName} files={n.Files.Count} volumes={n.Volumes.Count} runs={n.RunCount} lastRun={n.LastRun:u}");
                sb.AppendLine("  first entries: " + string.Join(" | ", n.Files.Take(3).Select(x => $"flags=0x{x.Flags:X} nt={x.Path.Split('\\').ElementAtOrDefault(1) ?? string.Empty}")));
                sb.AppendLine("  volume 0: device=" + (n.Volumes.Count > 0 ? n.Volumes[0].DevicePath : "none") + $" serial=0x{(n.Volumes.Count > 0 ? n.Volumes[0].Serial : 0):X8}");
            }
        }

        // 끝에서 끝까지: 기록마다 exe 가 놓인 폴더를 설치 루트로 삼아 BootTrace 가 맞는 기록을 찾고 변환하는지 (경로는 쓰지 않고 개수·바이트만). 시스템 밖 드라이브 3 개, 시스템 드라이브 2 개.
        string systemDrive = Path.GetPathRoot(dir)![..2];
        int other = 0;
        int system = 0;
        foreach (string f in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (other >= 3 && system >= 2 || PrefetchFiles.Read(f) is not { } info)
            {
                continue;
            }

            string? exe = info.Files.Select(x => PrefetchFiles.DosPath(x.Path, info.Volumes, letters))
                .FirstOrDefault(d => d is not null && Path.GetFileName(d).Equals(info.ExeName, StringComparison.OrdinalIgnoreCase) && File.Exists(d));
            bool onSystem = exe is not null && exe.StartsWith(systemDrive, StringComparison.OrdinalIgnoreCase);
            if (exe is null || (onSystem ? system >= 2 : other >= 3))
            {
                continue;
            }

            _ = onSystem ? system++ : other++;
            List<AsterCache.Core.Trace.TraceEvent>? boot = PrefetchFiles.BootTrace([info.ExeName], Path.GetDirectoryName(exe)!);
            sb.AppendLine($"end-to-end {(onSystem ? "system-drive" : "other-drive")} exe: record files={info.Files.Count} (flag 0x1: {info.Files.Count(x => (x.Flags & 1) != 0)}), boot ranges={boot?.Count.ToString() ?? "no match"}, bytes={boot?.Sum(r => r.Length)}, runs={info.RunCount}");
        }

        if (Environment.GetEnvironmentVariable("ASTER_PF_SUMMARY") is { Length: > 0 } outPath)
        {
            File.WriteAllText(outPath, sb.ToString());
        }

        Assert.True(files.Length == 0 || parsed > 0, sb.ToString());
    }
}
