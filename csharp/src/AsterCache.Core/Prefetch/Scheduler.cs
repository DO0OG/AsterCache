using AsterCache.Core.Json;
using AsterCache.Core.Model;

namespace AsterCache.Core.Prefetch;

/// 범위 -> 정렬·병합된 읽기 작업. 전략: physical | path | none (Python prefetch/scheduler.py).
public static class Scheduler
{
    public const long Mib = 1L << 20;

    /// 디스크가 탐색 한 번 시간 동안 흘려 읽는 바이트: 이보다 작은 구멍은 건너뛰기보다 읽는 쪽이 낫습니다.
    public static long BreakevenGap(double readMbPerSec = 100.0, double seekMs = 12.0) =>
        (long)(readMbPerSec * 1e6 * seekMs / 1000.0);

    public static (List<ReadOp> Ops, ScheduleInfo Info) Schedule(
        IEnumerable<ReadRange> ranges,
        string strategy = "physical",
        long? budgetBytes = null,
        long? gapBytes = null,
        Func<string, FileLayoutInfo?>? layoutFn = null,
        Func<string, long>? sizeFn = null,
        double fullFileRatio = 0.3,
        long? headBytes = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long gap = gapBytes ?? BreakevenGap();
        if (strategy is not ("physical" or "path" or "none"))
        {
            throw new ArgumentException("unknown strategy", nameof(strategy));
        }

        var rows = new List<ReadRange>();
        foreach (ReadRange range in ranges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(range);
        }

        if (rows.Any(r => r.Score is not null))
        {
            // 안정 정렬: 점수 없는 행은 맨 뒤.
            rows = rows.OrderByDescending(r => r.Score ?? double.NegativeInfinity).ToList();
            cancellationToken.ThrowIfCancellationRequested();
        }

        var chosen = new List<ReadRange>();
        long used = 0;
        foreach (ReadRange r in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (budgetBytes is not null && used + r.Length > budgetBytes)
            {
                continue;
            }

            chosen.Add(r);
            used += r.Length;
        }

        var info = new ScheduleInfo { Strategy = strategy, SelectedBytes = used, GapBytes = gap };
        if (strategy == "none")
        {
            return (chosen.Select(r => new ReadOp(r.Path, r.Offset, r.Length)).ToList(), info);
        }

        // 게임 시작에 먼저 필요한 접근 순서 앞부분은 디스크 순으로 섞지 않고 순서대로 앞에 둠 (범위 단위로 자르므로 headBytes 를 조금 넘길 수 있음)
        var head = new List<ReadOp>();
        if (headBytes is > 0)
        {
            int k = 0;
            for (long taken = 0; k < chosen.Count && taken < headBytes; k++)
            {
                head.Add(new ReadOp(chosen[k].Path, chosen[k].Offset, chosen[k].Length));
                taken += chosen[k].Length;
            }

            chosen = chosen.GetRange(k, chosen.Count - k);
        }

        var byPath = new OrderedDictionary<string, List<ReadRange>>();
        foreach (ReadRange r in chosen)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byPath.TryGetValue(r.Path, out List<ReadRange>? list))
            {
                byPath[r.Path] = list = new List<ReadRange>();
            }

            list.Add(r);
        }

        if (sizeFn is not null && fullFileRatio <= 1)
        {
            var dens = new List<(double Ratio, string Path, long Size, long Need)>();
            foreach ((string path, List<ReadRange> rs) in byPath)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long size = sizeFn(path);
                long need = UnionBytes(rs);
                if (size != 0 && (double)need / size >= fullFileRatio)
                {
                    dens.Add(((double)need / size, path, size, need));
                }
            }

            // Python sorted(dens, reverse=True): 튜플 전체 내림차순.
            dens.Sort((a, b) => CompareDens(b, a));
            foreach ((_, string path, long size, long need) in dens)
            {
                if (budgetBytes is null || used - need + size <= budgetBytes)
                {
                    used += size - need;
                    byPath[path] = new List<ReadRange> { new(path, 0, size) };
                    info.FullFiles++;
                }
            }

            info.SelectedBytes = used;
        }

        var keyed = new List<(string Drive, bool Unknown, long Phys, string Path, long Off, ReadOp Op)>();
        bool physMode = strategy == "physical";
        foreach ((string path, List<ReadRange> rs) in byPath)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileLayoutInfo? lay = physMode && layoutFn is not null ? layoutFn(path) : null;
            if (physMode)
            {
                info.LayoutFiles++;
            }

            List<(long? Phys, long Off, long Len)> pcs;
            if (lay is not null && lay.Extents.Count > 0)
            {
                info.LayoutOkFiles++;
                pcs = rs.SelectMany(r => Pieces(lay.Extents, lay.ClusterBytes, r.Offset, r.Length)).ToList();
            }
            else
            {
                info.Fallback |= physMode; // 배치를 모르는 파일은 경로·오프셋 순으로 맨 뒤.
                pcs = rs.Select(r => ((long?)null, r.Offset, r.Length)).ToList();
            }

            string drive = physMode ? Drive(path) : string.Empty;
            foreach ((long? phys, long off, long n) in Merge(pcs, gap))
            {
                keyed.Add((drive, phys is null, phys ?? 0, path, off, new ReadOp(path, off, n)));
            }
        }

        // OrderBy 는 안정 정렬이라 Python list.sort(key=...) 와 같습니다.
        var cmp = Comparer<(string Drive, bool Unknown, long Phys, string Path, long Off, ReadOp Op)>.Create(CompareKeys);
        List<ReadOp> ops = keyed.OrderBy(k => k, cmp).Select(k => k.Op).ToList();
        head.AddRange(ops);
        return (head, info);
    }

    private static int CompareDens((double Ratio, string Path, long Size, long Need) a, (double Ratio, string Path, long Size, long Need) b)
    {
        int c = a.Ratio.CompareTo(b.Ratio);
        if (c == 0)
        {
            c = CodePointComparer.Compare(a.Path, b.Path);
        }

        if (c == 0)
        {
            c = a.Size.CompareTo(b.Size);
        }

        return c != 0 ? c : a.Need.CompareTo(b.Need);
    }

    private static int CompareKeys(
        (string Drive, bool Unknown, long Phys, string Path, long Off, ReadOp Op) a,
        (string Drive, bool Unknown, long Phys, string Path, long Off, ReadOp Op) b)
    {
        int c = CodePointComparer.Compare(a.Drive, b.Drive);
        if (c == 0)
        {
            c = a.Unknown.CompareTo(b.Unknown);
        }

        if (c == 0)
        {
            c = a.Phys.CompareTo(b.Phys);
        }

        if (c == 0)
        {
            c = CodePointComparer.Compare(a.Path, b.Path);
        }

        return c != 0 ? c : a.Off.CompareTo(b.Off);
    }

    private static long UnionBytes(List<ReadRange> rs)
    {
        long total = 0;
        long end = -1;
        foreach (ReadRange r in rs.OrderBy(r => r.Offset))
        {
            long lo = Math.Max(r.Offset, end);
            long hi = r.Offset + r.Length;
            total += Math.Max(0, hi - lo);
            end = Math.Max(end, hi);
        }

        return total;
    }

    /// [off, off + length) 를 extent 경계에서 잘라 (물리 바이트 | null, off, len) 로. off 순.
    private static List<(long? Phys, long Off, long Len)> Pieces(IReadOnlyList<Extent> ext, int cs, long off, long length)
    {
        var output = new List<(long? Phys, long Off, long Len)>();
        if (ext.Count == 0 || cs == 0)
        {
            output.Add((null, off, length));
            return output;
        }

        long cur = off;
        long end = off + length;
        foreach (Extent e in ext)
        {
            long lo = Math.Max(cur, e.Vcn * cs);
            long hi = Math.Min(end, (e.Vcn + e.Clusters) * cs);
            if (lo >= hi)
            {
                continue;
            }

            if (lo > cur)
            {
                output.Add((null, cur, lo - cur));
            }

            output.Add((e.Lcn * cs + lo - e.Vcn * cs, lo, hi - lo));
            cur = hi;
        }

        if (cur < end)
        {
            output.Add((null, cur, end - cur));
        }

        return output;
    }

    /// 같은 파일의 이웃 중 파일 간격(둘 다 알면 디스크 간격도)이 gap 이하면 합칩니다.
    private static List<(long? Phys, long Off, long Len)> Merge(List<(long? Phys, long Off, long Len)> pieces, long gap)
    {
        var runs = new List<Run>();
        foreach ((long? phys, long off, long n) in pieces.OrderBy(p => p.Off))
        {
            long? pe = phys + n;
            if (runs.Count > 0)
            {
                Run last = runs[^1];
                long fgap = off - last.End;
                bool ok = fgap <= gap;
                if (ok && (phys is null) != (last.Phys is null))
                {
                    ok = false;
                }

                if (ok && phys is not null)
                {
                    long dgap = phys.Value - last.PhysEnd!.Value;
                    ok = dgap <= gap && (dgap >= 0 || dgap == fgap);
                }

                if (ok)
                {
                    last.End = Math.Max(last.End, off + n);
                    last.PhysEnd = pe is null ? null : Math.Max(last.PhysEnd!.Value, pe.Value);
                    continue;
                }
            }

            runs.Add(new Run { Phys = phys, Off = off, End = off + n, PhysEnd = pe });
        }

        return runs.Select(r => (r.Phys, r.Off, r.End - r.Off)).ToList();
    }

    // shortcut: 드라이브 문자와 \\서버\공유 만 처리 (os.path.splitdrive 의 \\?\ 접두는 다루지 않음), 로컬 드라이브 경로 외가 필요해지면 확장.
    private static string Drive(string path)
    {
        if (path.Length >= 2 && path[1] == ':')
        {
            return path[..2].ToUpperInvariant();
        }

        if (path.Length > 2 && IsSep(path[0]) && IsSep(path[1]) && !IsSep(path[2]))
        {
            int i = path.AsSpan(2).IndexOfAny('\\', '/');
            if (i >= 0)
            {
                int j = path.AsSpan(2 + i + 1).IndexOfAny('\\', '/');
                return (j < 0 ? path : path[..(2 + i + 1 + j)]).ToUpperInvariant();
            }

            return path.ToUpperInvariant();
        }

        return string.Empty;
    }

    private static bool IsSep(char c) => c is '\\' or '/';

    private sealed class Run
    {
        public long? Phys;
        public long Off;
        public long End;
        public long? PhysEnd;
    }
}
