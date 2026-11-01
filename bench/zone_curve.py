"""LCN position vs sequential/random unbuffered read speed on an NTFS volume (no admin needed).

Samples existing big files, finds a >=32MiB contiguous extent in each, reads it with FILE_FLAG_NO_BUFFERING,
and reports MiB/s per LCN-percentile decile. Read-only on the measured volume.
"""
import argparse, ctypes, json, os, random, statistics, subprocess, sys, time
from ctypes import wintypes

from bench.replay import MIB, SECTOR, UnbufferedReader, _Overlapped
from prefetch.layout import file_extents

SKIP = {'$recycle.bin', 'system volume information', '$extend'}
CHUNK, SPAN, RND = 4 * MIB, 32 * MIB, 64 * 1024


def volume(root):
    k = ctypes.WinDLL('kernel32', use_last_error=True)
    k.GetDiskFreeSpaceW.argtypes = [ctypes.c_wchar_p] + [ctypes.POINTER(ctypes.c_uint32)] * 4
    spc, bps, _, total = (ctypes.c_uint32() for _ in range(4))
    if not k.GetDiskFreeSpaceW(root, *(ctypes.byref(x) for x in (spc, bps, _, total))):
        raise ctypes.WinError(ctypes.get_last_error())
    return spc.value * bps.value, total.value


def big_files(root, max_depth, deadline, min_size=64 * MIB):
    out, stack = [], [(root, 0)]
    while stack and time.time() < deadline:
        d, depth = stack.pop()
        try:
            with os.scandir(d) as it:
                for e in it:
                    try:
                        if e.is_dir(follow_symlinks=False):
                            if depth < max_depth and e.name.lower() not in SKIP:
                                stack.append((e.path, depth + 1))
                        elif e.is_file(follow_symlinks=False) and e.stat().st_size >= min_size:
                            out.append((e.path, e.stat().st_size))
                    except OSError:
                        pass
        except OSError:
            pass
    return out


def runs_of(extents, cluster, size):
    """Merge LCN-contiguous extents -> [(file_off, lcn, usable_bytes)] with usable >= SPAN."""
    merged = []
    for vcn, lcn, n in extents:
        if merged and merged[-1][0] + merged[-1][2] == vcn and merged[-1][1] + merged[-1][2] == lcn:
            merged[-1][2] += n
        else:
            merged.append([vcn, lcn, n])
    res = []
    for vcn, lcn, n in merged:
        usable = min(n * cluster, size - vcn * cluster)
        if usable >= SPAN:
            res.append((vcn * cluster, lcn, usable))
    return res


def read_at(r, path, off, n):
    """One unbuffered ReadFile of n bytes at sector-aligned off; returns seconds."""
    h = r.handles.get(path)
    if h is None:
        h = r.handles[path] = r._open(path)
    r._buffer(n)
    ov = _Overlapped(Offset=off & 0xFFFFFFFF, OffsetHigh=off >> 32)
    got = wintypes.DWORD(0)
    t = time.perf_counter()
    ok = r.k.ReadFile(h, r.buf, n, ctypes.byref(got), ctypes.byref(ov))
    dt = time.perf_counter() - t
    if not ok or got.value != n:
        raise OSError(f'short/failed read {path} off={off} got={got.value}: {ctypes.get_last_error()}')
    return dt


def disk_bps(counter):
    """Mean Disk Bytes/sec over 3 s via Get-Counter, or None."""
    cmd = f"(Get-Counter '{counter}' -SampleInterval 1 -MaxSamples 3).CounterSamples | % {{$_.CookedValue}}"
    try:
        o = subprocess.run(['powershell', '-NoProfile', '-Command', cmd], capture_output=True, text=True, timeout=30).stdout
        v = [float(x) for x in o.split()]
        return sum(v) / len(v) if v else None
    except (OSError, ValueError, subprocess.SubprocessError):
        return None


def wait_idle(counter, log, limit=50 * 1e6, tries=12):
    for _ in range(tries):
        v = disk_bps(counter)
        log.append(v)
        if v is None or v < limit:
            return v
        time.sleep(5)
    return log[-1]


def rank(xs):
    o = sorted(range(len(xs)), key=xs.__getitem__)
    r = [0.0] * len(xs)
    for i, j in enumerate(o):
        r[j] = float(i)
    return r


def spearman(a, b):
    ra, rb = rank(a), rank(b)
    ma, mb = statistics.mean(ra), statistics.mean(rb)
    num = sum((x - ma) * (y - mb) for x, y in zip(ra, rb))
    den = (sum((x - ma) ** 2 for x in ra) * sum((y - mb) ** 2 for y in rb)) ** .5
    return num / den if den else None


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--root', default='D:\\')
    ap.add_argument('--out', default='results/zone_curve_20261101/zone_curve.json')
    ap.add_argument('--counter', default='\\PhysicalDisk(0 D:)\\Disk Bytes/sec')
    ap.add_argument('--per-bin', type=int, default=3)
    ap.add_argument('--depth', type=int, default=6)
    ap.add_argument('--scan-seconds', type=int, default=120)
    ap.add_argument('--reps', type=int, default=3)
    ap.add_argument('--random-n', type=int, default=200)
    ap.add_argument('--ref-mib-s', type=float, default=179.0, help='E1 contiguous-file rate to match against')
    a = ap.parse_args(argv)
    rng = random.Random(20261101)
    cluster, total = volume(a.root)

    files = big_files(a.root, a.depth, time.time() + a.scan_seconds)
    rng.shuffle(files)
    bins, probe = [[] for _ in range(10)], UnbufferedReader()
    queried = 0
    for path, size in files:
        if all(len(b) >= a.per_bin for b in bins):
            break
        ext = file_extents(path)
        queried += 1
        if not ext:
            continue
        for off, lcn, usable in runs_of(ext, cluster, size):
            b = min(9, lcn * 10 // total)
            if len(bins[b]) < a.per_bin:
                try:
                    read_at(probe, path, off, SECTOR)  # sharing/permission check
                except OSError:
                    break
                bins[b].append(dict(abs_path=path, file_off=off, lcn_start=lcn, usable=usable))
                break
    probe.close()
    samples = [s for b in bins for s in b]
    print('samples per bin', [len(b) for b in bins], file=sys.stderr, flush=True)
    for s in samples:
        s['name'] = os.path.basename(os.path.dirname(s['abs_path'])) + '\\' + os.path.basename(s['abs_path'])
        s['lcn_pct'] = round(s['lcn_start'] * 100 / total, 3)
        s['mb_s_runs'] = []

    idle_log = []
    wait_idle(a.counter, idle_log)
    r, lap_idle = UnbufferedReader(), []
    for rep in range(a.reps):
        order = samples[:]
        rng.shuffle(order)
        while rep and order[0] is samples_last:  # no same sample back-to-back across laps
            rng.shuffle(order)
        for s in order:
            dt = sum(read_at(r, s['abs_path'], s['file_off'] + i, CHUNK) for i in range(0, SPAN, CHUNK))
            s['mb_s_runs'].append(round(SPAN / MIB / dt, 2))
        samples_last = order[-1]
        lap_idle.append(disk_bps(a.counter))
    for s in samples:
        s['mib_s'] = s['mb_s'] = round(statistics.median(s['mb_s_runs']), 2)

    summary = []
    for b in range(10):
        v = [s['mib_s'] for s in samples if min(9, s['lcn_start'] * 10 // total) == b]
        summary.append(dict(bin=f'{b * 10}-{b * 10 + 10}%', n=len(v), median_mib_s=round(statistics.median(v), 2) if v else None,
                            min=min(v) if v else None, max=max(v) if v else None))

    # random 64KiB: low/mid/high decile, requests interleaved; "spread" = across all runs of the decile,
    # "single" = inside the longest run of the decile
    hi = max(b for b in range(10) if bins[b])  # highest decile that has samples (top of a part-full volume is empty)
    groups = {'low_0-10%': bins[0], 'mid_40-50%': bins[4], f'high_{hi * 10}-{hi * 10 + 10}%': bins[hi]}
    rnd = {}
    for mode in ('spread', 'single'):
        lat = {g: [] for g, bs in groups.items() if bs}
        pools = {g: ([max(bs, key=lambda x: x['usable'])] if mode == 'single' else bs) for g, bs in groups.items() if bs}
        for _ in range(a.random_n):
            for g, pool in pools.items():
                if not pool:
                    continue
                s = rng.choice(pool)
                off = s['file_off'] + rng.randrange(0, (s['usable'] - RND) // SECTOR) * SECTOR
                lat[g].append(read_at(r, s['abs_path'], off, RND) * 1e3)
        rnd[mode] = {g: dict(n=len(l), mean_ms=round(statistics.mean(l), 3), median_ms=round(statistics.median(l), 3),
                             p95_ms=round(sorted(l)[int(len(l) * .95) - 1], 3)) for g, l in lat.items() if l}
    r.close()
    idle_after = disk_bps(a.counter)

    ok = [x for x in summary if x['n']]
    meds = [x['median_mib_s'] for x in ok]
    rho = spearman([s['lcn_start'] for s in samples], [s['mib_s'] for s in samples])
    near = min(ok, key=lambda x: abs(x['median_mib_s'] - a.ref_mib_s)) if ok else None
    conclusion = dict(
        empty_bins=[x['bin'] for x in summary if not x['n']],
        outer_over_inner=round(ok[0]['median_mib_s'] / ok[-1]['median_mib_s'], 3) if len(ok) > 1 else None,
        outer_bin=ok[0]['bin'] if ok else None, inner_bin=ok[-1]['bin'] if ok else None,
        spearman_lcn_vs_speed=round(rho, 3) if rho is not None else None,
        fastest_bin=max(ok, key=lambda x: x['median_mib_s'])['bin'] if ok else None,
        ref_mib_s=a.ref_mib_s, ref_closest_bin=near['bin'] if near else None,
        ref_closest_bin_median=near['median_mib_s'] if near else None,
        max_sample_mib_s=max(s['mib_s'] for s in samples) if samples else None,
        within_bin_spread_ratio=[round(x['max'] / x['min'], 2) for x in ok],
        monotonic_bin_medians=all(x >= y for x, y in zip(meds, meds[1:])))
    for s in samples:
        s.pop('abs_path'); s.pop('usable')
    out = dict(volume=dict(root=a.root, cluster_bytes=cluster, total_clusters=total), unit='MiB/s (mb_s == mib_s)',
               params=dict(span_mib=SPAN // MIB, chunk_mib=CHUNK // MIB, reps=a.reps, per_bin=a.per_bin, random_bytes=RND,
                           random_n=a.random_n, candidates=len(files), extents_queried=queried),
               idle_bytes_per_s=dict(before=idle_log, between_laps=lap_idle, after=idle_after),
               samples=sorted(samples, key=lambda s: s['lcn_start']), bins=summary, random_64k=rnd, conclusion=conclusion)
    os.makedirs(os.path.dirname(a.out), exist_ok=True)
    with open(a.out, 'w', encoding='utf-8') as f:
        json.dump(out, f, indent=1, ensure_ascii=False)
    print(json.dumps(dict(bins=summary, random_64k=rnd, conclusion=conclusion, idle=out['idle_bytes_per_s']), indent=1))


if __name__ == '__main__':
    main()
