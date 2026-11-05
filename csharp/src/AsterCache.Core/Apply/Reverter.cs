using AsterCache.Core.Json;
using AsterCache.Core.Model;

namespace AsterCache.Core.Apply;

/// relayout/apply.py 의 revert_journal: 저널에 기록된 각 단계를 원래 extent 로 되돌립니다. 최선 노력이고 보장은 없습니다
/// (비워진 클러스터를 다른 파일이 이미 썼을 수 있음 → 그 조각은 건너뜀). 단계는 역순.
/// 취소 토큰은 조각 사이에서만 봅니다 (Stopped = "cancelled", 저널은 그대로 남아 다음 되돌리기가 이어받음).
/// skipRows: 저널 앞쪽 이만큼의 줄은 없는 것으로 봅니다 (이번 실행이 쓴 줄만 되돌릴 때).
public static class Reverter
{
    /// 예외로 끝나도 (예: 이동 뒤 저널 쓰기 실패) 그때까지 옮긴 바이트를 예외의 Data 에 싣습니다 (호출자의 쓰기 집계용).
    public const string MovedBytesDataKey = "MovedBytes";

    public static RevertReport Revert(string journalPath, IVolume vol, IReadOnlyList<string> roots, ApplyHooks hooks, ApplyOptions? options = null,
        CancellationToken cancellationToken = default, int skipRows = 0)
    {
        long written = 0;
        try
        {
            return RevertCore(journalPath, vol, roots, hooks, options, cancellationToken, skipRows, ref written);
        }
        catch (Exception e) when (written > 0)
        {
            e.Data[MovedBytesDataKey] = written;
            throw;
        }
    }

    private static RevertReport RevertCore(string journalPath, IVolume vol, IReadOnlyList<string> roots, ApplyHooks hooks, ApplyOptions? options,
        CancellationToken cancellationToken, int skipRows, ref long written)
    {
        options ??= new ApplyOptions();
        using var jr = new Journal(journalPath);
        string letter = vol.Letter;
        PlanGuards.CheckRoots(roots, letter, hooks.RealPath);
        var steps = new SortedDictionary<long, JObject>();
        foreach (JObject r in jr.Rows.Skip(skipRows))
        {
            if (r.TryGetValue("type", out object? t) && Equals(t, "step") && r.ContainsKey("from"))
            {
                steps[(long)r["idx"]!] = r;
            }
        }

        long cb = vol.ClusterBytes;
        long chunk = Math.Max(1, options.ChunkBytes / cb);
        var rep = new RevertReport { Executed = options.Execute };
        foreach (JObject r in steps.Values)
        {
            string path = (string)r["path"]!;
            if (PlanGuards.Drive(path, hooks.RealPath) != letter.ToUpperInvariant() || !PlanGuards.Inside(path, roots, hooks.RealPath))
            {
                throw new PlanRejectedException("outside_allowed_root: " + path);
            }
        }

        foreach ((long idx, JObject r) in steps.Reverse())
        {
            string path = (string)r["path"]!;
            IReadOnlyList<Extent> cur = hooks.Extents(path) ?? [];
            foreach (object? piece in (List<object?>)r["from"]!)
            {
                var p = (List<object?>)piece!;
                long v = (long)p[0]!;
                long lcn = (long)p[1]!;
                long n = (long)p[2]!;
                long done = PlanGuards.DonePrefix(cur, v, n, lcn); // 앞에서부터 이미 원래 자리에 있는 클러스터 (이전 되돌리기가 상한 등으로 멈춘 자리): 그 뒤부터 이어 이동량·상한에서 뺌
                if (done == n)
                {
                    continue;
                }

                if (!options.Execute)
                {
                    rep.Restored++;
                    continue;
                }

                bool failed = false;
                for (long off = done; off < n; off += chunk)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        rep.Stopped = "cancelled";
                        rep.MovedBytes = written;
                        return rep;
                    }

                    long c = Math.Min(chunk, n - off);
                    if (written + (c * cb) > options.WriteCap)
                    {
                        rep.Stopped = "write_cap";
                        rep.MovedBytes = written;
                        return rep;
                    }

                    string? err = Applier.MoveChunk(vol, path, v, lcn, off, c, hooks.Sleep);
                    if (err is not null)
                    {
                        rep.Skipped.Add(new SkippedStep(idx, err));
                        failed = true;
                        break;
                    }

                    written += c * cb;
                }

                if (!failed)
                {
                    rep.Restored++;
                    jr.Add(new JObject { { "type", "revert" }, { "idx", idx }, { "vcn", v }, { "state", "done" } });
                }
            }
        }

        rep.MovedBytes = written;
        return rep;
    }
}
