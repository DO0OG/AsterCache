using AsterCache.Core.Json;
using AsterCache.Core.Model;

namespace AsterCache.Core.Apply;

/// relayout/apply.py 의 revert_journal: 저널에 기록된 각 단계를 원래 extent 로 되돌립니다. 최선 노력이고 보장은 없습니다
/// (비워진 클러스터를 다른 파일이 이미 썼을 수 있음 → 그 조각은 건너뜀). 단계는 역순.
/// 취소 토큰은 조각 사이에서만 봅니다 (Stopped = "cancelled", 저널은 그대로 남아 다음 되돌리기가 이어받음).
/// skipRows: 저널 앞쪽 이만큼의 줄은 없는 것으로 봅니다 (이번 실행이 쓴 줄만 되돌릴 때).
public static class Reverter
{
    public static RevertReport Revert(string journalPath, IVolume vol, IReadOnlyList<string> roots, ApplyHooks hooks, ApplyOptions? options = null,
        CancellationToken cancellationToken = default, int skipRows = 0)
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
        long written = 0;
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
                if (PlanGuards.DonePrefix(cur, v, n, lcn) == n)
                {
                    continue;
                }

                if (!options.Execute)
                {
                    rep.Restored++;
                    continue;
                }

                bool failed = false;
                for (long off = 0; off < n; off += chunk)
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
