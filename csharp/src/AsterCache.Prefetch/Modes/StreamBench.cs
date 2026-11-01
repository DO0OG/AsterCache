using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AsterCache.Core.Prefetch;
using Microsoft.Win32.SafeHandles;

namespace AsterCache.Prefetch.Modes;

/// 재생할 읽기 한 건 (trace.json 의 path, offset, length).
public sealed record ReplayEvent(string Path, long Offset, long Length);

/// 버퍼드 재생 결과. Python bench/replay.replay 와 같이 total 은 요청별 지연의 합 (첫 열기 포함, crc 계산 제외).
public sealed record ReplayResult(double TotalSeconds, long Bytes, uint Crc32, int Requests, double SlowRatio);

/// 한 번의 시도 결과 (스트림 + 바로 이어 버퍼드 재생).
public sealed record BenchRow(int Rep, string Mode, double StreamS, double CpuS, long Bytes, long Requests, long Errors, ReplayResult Replay);

/// 측정할 방식. Run 은 ops 를 읽고 (통계, 사용한 CPU 초) 를 돌려줍니다.
public sealed record BenchMode(string Name, Func<IReadOnlyList<ReadOp>, (StreamStats Stats, double CpuSeconds)> Run);

/// 같은 세션에서 방식을 번갈아 콜드 -> 스트림 -> 재생을 반복 측정합니다 (Python bench/prefetch_methods.py).
public static partial class StreamBench
{
    public const double SlowMs = 1.0;

    [GeneratedRegex("^ovl([0-9]+)$")]
    private static partial Regex OvlName();

    /// qd1 (읽기, 큐 깊이 1), pvm, ovl1..ovl64 (오버랩 큐 깊이).
    public static BenchMode ParseMode(string name)
    {
        if (name == "pvm")
        {
            return Managed(name, StreamMethod.Pvm, 1);
        }

        if (name == "qd1")
        {
            return Managed(name, StreamMethod.Read, 1);
        }

        if (OvlName().Match(name) is { Success: true } m && int.TryParse(m.Groups[1].Value, out int depth) && depth is >= 1 and <= 64)
        {
            return Managed(name, StreamMethod.Overlapped, depth);
        }

        throw new ArgumentException($"unknown mode: {name} (use qd1, pvm, ovlN)");
    }

    /// Python `prefetch.cli --execute` 를 자식 프로세스로 돌리는 같은 이름의 방식 (이름 앞에 py-). CPU 는 자식 프로세스 전체 (계획 작성 포함).
    public static BenchMode ParsePythonMode(string name, string python, string repoDir, string traceJson, string root)
    {
        _ = ParseMode(name); // 이름 검사
        (string method, int threads) = name switch
        {
            "pvm" => ("pvm", 1),
            "qd1" => ("read", 1),
            _ => ("overlapped", int.Parse(name[3..], CultureInfo.InvariantCulture)),
        };
        return new BenchMode("py-" + name, _ => RunPython(python, repoDir, traceJson, root, method, threads));
    }

    private static BenchMode Managed(string name, StreamMethod method, int depth) => new(name, ops =>
    {
        using Process self = Process.GetCurrentProcess();
        TimeSpan c0 = self.TotalProcessorTime;
        StreamStats s = ZoneStreamer.Run(ops, method, ZoneStreamer.DefaultChunkBytes, depth);
        self.Refresh();
        return (s, (self.TotalProcessorTime - c0).TotalSeconds);
    });

    private static (StreamStats, double) RunPython(string python, string repoDir, string traceJson, string root, string method, int threads)
    {
        var psi = new ProcessStartInfo(python)
        {
            WorkingDirectory = repoDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string arg in new[] { "-B", "-m", "prefetch.cli", "--trace", traceJson, "--origin-root", root, "--method", method, "--threads", threads.ToString(CultureInfo.InvariantCulture), "--execute" })
        {
            psi.ArgumentList.Add(arg);
        }

        using Process p = Process.Start(psi) ?? throw new InvalidOperationException("python did not start");
        Task<string> err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        double cpu = p.TotalProcessorTime.TotalSeconds;
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"python prefetch failed ({p.ExitCode}): {err.GetAwaiter().GetResult().Trim()}");
        }

        using JsonDocument doc = JsonDocument.Parse(output);
        JsonElement r = doc.RootElement;
        return (new StreamStats(r.GetProperty("bytes").GetInt64(), r.GetProperty("requests").GetInt64(), r.GetProperty("errors").GetInt64(), r.GetProperty("seconds").GetDouble()), cpu);
    }

    public static List<ReplayEvent> LoadEvents(string traceJson)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(traceJson));
        return doc.RootElement.EnumerateArray()
            .Select(e => new ReplayEvent(e.GetProperty("path").GetString()!, e.GetProperty("offset").GetInt64(), e.GetProperty("length").GetInt64()))
            .ToList();
    }

    /// 파일 캐시를 거치는 일반 ReadFile 로 이벤트를 순서대로 읽습니다. 핸들은 처음 쓸 때 열어 끝까지 유지.
    public static ReplayResult Replay(IReadOnlyList<ReplayEvent> events)
    {
        var handles = new Dictionary<string, SafeFileHandle>();
        var lat = new List<double>(events.Count);
        byte[] buf = [];
        uint crc = 0;
        long bytes = 0;
        try
        {
            foreach (ReplayEvent e in events)
            {
                if (buf.Length < e.Length)
                {
                    buf = new byte[e.Length];
                }

                long t0 = Stopwatch.GetTimestamp();
                if (!handles.TryGetValue(e.Path, out SafeFileHandle? h))
                {
                    handles[e.Path] = h = File.OpenHandle(e.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                }

                int got = 0;
                int n;
                while (got < e.Length && (n = RandomAccess.Read(h, buf.AsSpan(got, (int)e.Length - got), e.Offset + got)) > 0)
                {
                    got += n;
                }

                lat.Add(Stopwatch.GetElapsedTime(t0).TotalSeconds);
                crc = Crc32.Update(crc, buf.AsSpan(0, got));
                bytes += got;
            }
        }
        finally
        {
            foreach (SafeFileHandle h in handles.Values)
            {
                h.Dispose();
            }
        }

        return new ReplayResult(lat.Sum(), bytes, crc, lat.Count, lat.Count == 0 ? 0 : lat.Count(x => x * 1000 > SlowMs) / (double)lat.Count);
    }

    /// repeat 번 반복, 매번 방식 순서를 (고정 시드로) 섞어 번갈아 돕니다. 시도마다 콜드 -> 스트림 -> 재생. 오류나 0 바이트면 던집니다.
    public static List<BenchRow> Run(IReadOnlyList<ReadOp> ops, IReadOnlyList<ReplayEvent> events, IReadOnlyList<ReadOp> flush, IReadOnlyList<BenchMode> modes, int repeat, Action<string>? log = null)
    {
        var rows = new List<BenchRow>();
        var rng = new Random(1);
        for (int rep = 0; rep < repeat; rep++)
        {
            foreach (BenchMode mode in modes.OrderBy(_ => rng.Next()).ToList())
            {
                ColdMaker.Make(ops, flush);
                (StreamStats s, double cpu) = mode.Run(ops);
                if (s.Errors > 0 || s.Bytes == 0)
                {
                    throw new InvalidOperationException($"{mode.Name} failed: bytes={s.Bytes} errors={s.Errors}");
                }

                ReplayResult r = Replay(events);
                rows.Add(new BenchRow(rep, mode.Name, s.Seconds, Math.Round(cpu, 4), s.Bytes, s.Requests, s.Errors, r));
                log?.Invoke($"{rep} {mode.Name,-8} stream={s.Seconds:F3}s replay={r.TotalSeconds:F3}s cpu={cpu:F3}s slow={r.SlowRatio:P1}");
            }
        }

        return rows;
    }

    public static double Median(IEnumerable<double> values)
    {
        double[] v = values.Order().ToArray();
        return v.Length == 0 ? 0 : v.Length % 2 == 1 ? v[v.Length / 2] : (v[(v.Length / 2) - 1] + v[v.Length / 2]) / 2;
    }
}

/// zlib.crc32 와 같은 CRC-32 (재생 바이트 정확성 비교용).
internal static class Crc32
{
    private static readonly uint[] Table = Build();

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        uint c = ~crc;
        foreach (byte b in data)
        {
            c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        }

        return ~c;
    }

    private static uint[] Build()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            t[i] = c;
        }

        return t;
    }
}
