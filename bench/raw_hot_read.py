"""E6-0b physical extents of a trace's hot ranges (no admin) and a raw read of them in LCN order (--read, admin).
Hot ranges = union of the trace's (path, offset, length) per file, mapped to LCNs with FSCTL_GET_RETRIEVAL_POINTERS."""
import argparse, ctypes, json, struct, sys
from ctypes import wintypes
from bench.lba_curve import k, MIB, read_ranges, volume_bytes

FSCTL_GET_RETRIEVAL_POINTERS = 0x90073
ERROR_MORE_DATA = 234


def extents(path):
    """[(vcn, lcn, clusters)] of a file; holes (lcn -1) skipped."""
    h = k.CreateFileW(path, 0x80, 7, None, 3, 0, None)
    if h is None or h == ctypes.c_void_p(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())
    out, vcn = [], 0
    try:
        while True:
            inp, buf, n = ctypes.c_longlong(vcn), ctypes.create_string_buffer(1 << 16), wintypes.DWORD(0)
            ok = k.DeviceIoControl(h, FSCTL_GET_RETRIEVAL_POINTERS, ctypes.byref(inp), 8, buf, len(buf), ctypes.byref(n), None)
            err = ctypes.get_last_error()
            if not ok and err != ERROR_MORE_DATA:
                raise ctypes.WinError(err)
            cnt = struct.unpack_from('<I', buf.raw, 0)[0]
            start = struct.unpack_from('<q', buf.raw, 8)[0]
            for i in range(cnt):
                nxt, lcn = struct.unpack_from('<qq', buf.raw, 16 + 16 * i)
                if lcn != -1:
                    out.append((start, lcn, nxt - start))
                start = nxt
            vcn = start
            if ok:
                return out
    finally:
        k.CloseHandle(h)


def hot_ranges(trace):
    by = {}
    for e in trace:
        by.setdefault(e['path'], []).append((e['offset'], e['offset'] + e['length']))
    res = {}
    for p, rs in by.items():
        rs.sort()
        m = [list(rs[0])]
        for s, e in rs[1:]:
            if s <= m[-1][1]:
                m[-1][1] = max(m[-1][1], e)
            else:
                m.append([s, e])
        res[p] = m
    return res


def hot_runs(trace, cluster):
    """LCN-sorted merged runs [(lcn, clusters)] of the hot bytes, plus per-file totals."""
    runs, files = [], {}
    for p, rng in hot_ranges(trace).items():
        try:
            ext = extents(p)
        except OSError:  # file gone (the trace names it, the disk no longer has it)
            files[p] = dict(hot_bytes=0, extents=0, missing=True)
            continue
        tot = 0
        for s, e in rng:
            c0, c1 = s // cluster, -(-e // cluster)
            tot += (c1 - c0) * cluster
            for vcn, lcn, n in ext:
                a, b = max(c0, vcn), min(c1, vcn + n)
                if a < b:
                    runs.append((lcn + a - vcn, b - a))
        files[p] = dict(hot_bytes=tot, extents=len(ext))
    runs.sort()
    merged = []
    for lcn, n in runs:
        if merged and lcn <= merged[-1][0] + merged[-1][1]:
            merged[-1][1] = max(merged[-1][1], lcn + n - merged[-1][0])
        else:
            merged.append([lcn, n])
    return [tuple(m) for m in merged], files


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--trace', required=True)
    ap.add_argument('--volume', default='D:')
    ap.add_argument('--cluster', type=int, default=4096)
    ap.add_argument('--read', type=int, default=0, help='raw read repetitions in LCN order (admin)')
    ap.add_argument('--qd', type=int, default=4)
    ap.add_argument('--chunk-mib', type=int, default=4)
    ap.add_argument('--out')
    a = ap.parse_args(argv)
    runs, files = hot_runs(json.load(open(a.trace, encoding='utf-8')), a.cluster)
    total_c = sum(n for _, n in runs)
    vol_clusters = volume_bytes(a.volume) // a.cluster if a.read else 488374271  # probe total_clusters when not admin
    buckets = {}
    for lcn, n in runs:
        b = int(lcn * 20 / vol_clusters) * 5
        buckets[b] = buckets.get(b, 0) + n * a.cluster
    sizes = sorted(n * a.cluster for _, n in runs)
    res = dict(runs=len(runs), hot_bytes=total_c * a.cluster, min_lcn_pct=runs[0][0] / vol_clusters * 100,
               max_lcn_pct=(runs[-1][0] + runs[-1][1]) / vol_clusters * 100,
               bytes_by_lcn_pct_bucket={'%d-%d%%' % (b, b + 5): v for b, v in sorted(buckets.items())},
               run_mib_p50=sizes[len(sizes) // 2] / MIB, run_mib_max=sizes[-1] / MIB, runs_ge_8mib=sum(1 for s in sizes if s >= 8 * MIB),
               files=files, runs_list=[(lc, n) for lc, n in runs])
    if a.read:
        rr = [(lc * a.cluster, n * a.cluster) for lc, n in runs]
        res['raw_reps'] = []
        for _ in range(a.read):
            nb, sec, rs = read_ranges(a.volume, rr, a.qd, a.chunk_mib * MIB)
            res['raw_reps'].append(dict(bytes=nb, seconds=sec, mb_s=nb / 1e6 / sec, requests=len(rs)))
            print('raw read %.2f s %.1f MB/s' % (sec, nb / 1e6 / sec), file=sys.stderr, flush=True)
        t = sorted(r['seconds'] for r in res['raw_reps'])
        res['t_raw_median_s'] = t[len(t) // 2]
    if a.out:
        open(a.out, 'w', encoding='utf-8').write(json.dumps(res, indent=1))
    print(json.dumps({x: v for x, v in res.items() if x not in ('files', 'runs_list')}, indent=1))


if __name__ == '__main__':
    main()
