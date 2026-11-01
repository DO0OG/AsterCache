"""Prefetch tuning sweep on the existing AsterBench datasets: HDD floor numbers, then prefetch.cli parameter sweeps.
Each pass = evict (full unbuffered read of the dataset, or standby purge if admin) -> prefetch.cli via bench.run.cmd_prewarm
-> buffered trace replay. Interleaved repeats, JSON written after every stage. Run from anywhere: python -m bench.sweep"""
import argparse, ctypes, json, os, random, statistics, subprocess, sys, time
from ctypes import wintypes
from pathlib import Path

from . import cache_control, dataset, replay
from .run import cmd_prewarm, environment

REPO = Path(__file__).resolve().parent.parent
OUT = REPO / 'results' / 'prefetch_sweep_20261101'
MIB = 1 << 20
CHUNK = 4 * MIB
T0 = time.time()


def log(*a):
    print('[%6.0fs]' % (time.time() - T0), *a, flush=True)


def med(v):
    return statistics.median(v) if v else None


def disk_activity(counter=r'\PhysicalDisk(0 D:)\Disk Bytes/sec', n=5):
    ps = "(Get-Counter '%s' -SampleInterval 1 -MaxSamples %d).CounterSamples | %% { $_.CookedValue }" % (counter, n)
    r = subprocess.run(['powershell', '-NoProfile', '-Command', ps], capture_output=True, text=True)
    try:
        return dict(counter=counter, bytes_per_s=[float(x) for x in r.stdout.split()])
    except ValueError:
        return dict(counter=counter, error=(r.stderr or r.stdout)[:300])


def seq_read(paths, limit=None, buffered=False):
    """Read files back to back in CHUNK reads; unbuffered path avoids the per-read copy of replay.UnbufferedReader.read."""
    left, got_total, t0 = limit, 0, time.perf_counter()
    if buffered:
        buf = bytearray(CHUNK)
        for p in paths:
            with open(p, 'rb', buffering=0) as f:
                while (n := f.readinto(buf)):
                    got_total += n
        return dict(bytes=got_total, seconds=time.perf_counter() - t0)
    r = replay.UnbufferedReader()
    try:
        for p in paths:
            size, off, h = os.path.getsize(p), 0, None
            h = r.handles[p] = r._open(p)
            while off < size and (left is None or left > 0):
                n = min(CHUNK, size - off, left if left is not None else CHUNK)
                n = -(-n // replay.SECTOR) * replay.SECTOR
                r._buffer(n)
                ov = replay._Overlapped(Offset=off & 0xFFFFFFFF, OffsetHigh=off >> 32)
                got = wintypes.DWORD(0)
                if not r.k.ReadFile(h, r.buf, n, ctypes.byref(got), ctypes.byref(ov)):
                    raise ctypes.WinError(ctypes.get_last_error())
                off += got.value
                got_total += got.value
                if left is not None:
                    left -= got.value
            if left is not None and left <= 0:
                break
    finally:
        r.close()
    return dict(bytes=got_total, seconds=time.perf_counter() - t0)


def evict(files):
    c = cache_control.make_cold()
    if c['cold_method'] == 'standby_purge':
        return dict(method='standby_purge', seconds=0.0)
    s = seq_read(files)
    return dict(method='full_unbuffered_read', seconds=s['seconds'], mb_s=s['bytes'] / 1e6 / s['seconds'])


def rand_read(files, sizes, n, seed, bs=64 * 1024):
    rng, r, lat = random.Random(seed), replay.UnbufferedReader(), []
    try:
        for _ in range(n):
            i = rng.randrange(len(files))
            off = rng.randrange(sizes[i] // bs) * bs
            t = time.perf_counter()
            r.read(files[i], off, bs)
            lat.append((time.perf_counter() - t) * 1e3)
    finally:
        r.close()
    return dict(mean_ms=statistics.fmean(lat), median_ms=statistics.median(lat), p95_ms=sorted(lat)[int(.95 * n)])


def trace_stats(events):
    merged = []
    for p, s, t in sorted((e['path'], e['offset'], e['offset'] + e['length']) for e in events):
        if merged and merged[-1][0] == p and s <= merged[-1][2]:
            merged[-1][2] = max(merged[-1][2], t)
        else:
            merged.append([p, s, t])
    return dict(events=len(events), merged_ranges=len(merged), unique_bytes=sum(t - s for _, s, t in merged),
                files=len({m[0] for m in merged}))


def floors(files, sizes, events, reps):
    out = dict(seq_1gib=[], rand_64k=[], trace=trace_stats(events))
    for i in range(reps):
        evict(files)  # position/cache state irrelevant for unbuffered, but keeps the sequence uniform
        s = seq_read(files, limit=1 << 30)
        out['seq_1gib'].append(dict(s, mb_s=s['bytes'] / 1e6 / s['seconds']))
        out['rand_64k'].append(rand_read(files, sizes, 500, 100 + i))
        log('floor rep', i, out['seq_1gib'][-1], out['rand_64k'][-1])
    out['seq_mb_s'] = med([x['mb_s'] for x in out['seq_1gib']])
    out['rand_mean_ms'] = med([x['mean_ms'] for x in out['rand_64k']])
    t = out['trace']
    out['lower_bound_s'] = dict(
        stream_only=t['unique_bytes'] / 1e6 / out['seq_mb_s'],
        stream_plus_one_random_cost_per_merged_range=t['unique_bytes'] / 1e6 / out['seq_mb_s'] + t['merged_ranges'] * out['rand_mean_ms'] / 1e3,
        note='random cost is for a full-dataset random span; a monotonic sweep pays less per hop, so the 2nd value is conservative')
    return out


def cli_cmd(threads, gap_kib, ratio=None):
    c = '"%s" -m prefetch.cli --strategy physical --threads %d --gap-kib %d' % (sys.executable, threads, gap_kib)
    return c + (' --full-file-ratio %s' % ratio if ratio is not None else '') + ' --execute'


def run_configs(cfgs, reps, events, root, files, ssd_cold, deadline):
    """cfgs: {name: cmd}. Interleaves repeats across configs. Returns {name: row}."""
    rows = {n: dict(cmd=c, passes=[]) for n, c in cfgs.items()}
    for rep in range(reps):
        for n, c in cfgs.items():
            if time.time() > deadline:
                rows[n].setdefault('skipped_rep', []).append(rep)
                continue
            ev = evict(files)
            pw = cmd_prewarm(c, root)
            info = json.loads(pw['stdout_tail']) if pw['returncode'] == 0 else {}
            rp = replay.replay(events)
            rows[n]['passes'].append(dict(prewarm_s=pw['seconds'], read_s=info.get('seconds'), bytes=info.get('bytes'),
                                          requests=info.get('requests'), errors=info.get('errors'), full_files=info.get('full_files'),
                                          gap_bytes=info.get('gap_bytes'), returncode=pw['returncode'], stderr=pw['stderr_tail'] if pw['returncode'] else '',
                                          replay_s=rp['total_s'], replay_p95_ms=rp['p95_ms'], evict_s=ev['seconds']))
            log(n, 'rep', rep, 'prewarm %.1fs read %s replay %.3fs' % (pw['seconds'], info.get('seconds'), rp['total_s']))
    for r in rows.values():
        ps = r['passes']
        if not ps or any(p['returncode'] for p in ps):
            continue
        r.update(prewarm_s=med([p['prewarm_s'] for p in ps]), read_s=med([p['read_s'] for p in ps]),
                 mib=ps[0]['bytes'] / MIB, requests=ps[0]['requests'], replay_s=med([p['replay_s'] for p in ps]),
                 total_s=med([p['prewarm_s'] + p['replay_s'] for p in ps]), n=len(ps),
                 replay_cache_ok=all(p['replay_s'] < 0.25 for p in ps))
        r['prewarm_cmd_total_over_ssd_cold'] = r['total_s'] / ssd_cold
    return rows


def control_reads(hot, ctl_files, reps):
    """Alternate a full unbuffered pass of hot (also pushes older data out of the drive DRAM cache) with
    32MiB unbuffered reads from the start of old files; decimal MB/s."""
    out = dict(hot_full_unbuffered_mb_s=[], old_32mib_mb_s={Path(p).name: [] for p in ctl_files})
    for _ in range(reps):
        s = seq_read([hot])
        out['hot_full_unbuffered_mb_s'].append(s['bytes'] / 1e6 / s['seconds'])
        for p in ctl_files:
            s = seq_read([p], limit=32 * MIB)
            out['old_32mib_mb_s'][Path(p).name].append(s['bytes'] / 1e6 / s['seconds'])
    out['hot_median'] = med(out['hot_full_unbuffered_mb_s'])
    out['old_median_per_file'] = {k: med(v) for k, v in out['old_32mib_mb_s'].items()}
    out['old_median_all'] = med([x for v in out['old_32mib_mb_s'].values() for x in v])
    return out


def contig(root_hdd, root_ssd, reps, out_dir, keep=False, remeasure=None, tag=None):
    """Relocation upper bound: unique trace blocks (first-access order) written into one file; time full read, RAM replay, cold replay.
    keep: leave hot_contig.bin; remeasure=<path>: reuse that file and redo only (a) 4MiB sequential read and (c) direct replay."""
    global CHUNK
    from prefetch.layout import file_extents
    ev_h, ev_s = dataset.load_trace(root_hdd), dataset.load_trace(root_ssd)
    ssd = [replay.replay(ev_s, True)['total_s'] for _ in range(reps)]
    ssd_cold = med(ssd)
    order = {}
    for e in ev_h:
        order.setdefault((e['path'], e['offset'], e['length']), len(order))
    offs, off = {}, 0
    for k in order:
        offs[k] = off
        off += k[2]
    hot = Path(remeasure) if remeasure else Path(root_hdd) / 'hot_contig.bin'
    mapped = [dict(e, path=str(hot), offset=offs[(e['path'], e['offset'], e['length'])]) for e in ev_h]
    meta = json.loads((Path(root_hdd) / dataset.MARKER).read_text())
    files = [str(Path(root_hdd) / f['rel']) for f in meta['files']]
    ctl = [str(Path(root_hdd) / f['rel']) for f in sorted(meta['files'], key=lambda f: -f['size']) if f['size'] >= 32 * MIB][:5]
    out = dict(timestamp=time.strftime('%Y%m%d_%H%M%S'), ssd_cold_s=dict(runs=ssd, median=ssd_cold), unique_blocks=len(order),
               file_bytes=off, disk_activity_before=disk_activity(n=3))
    try:
        if remeasure:
            if hot.stat().st_size != off:
                raise SystemExit('%s is %d bytes, trace expects %d' % (hot, hot.stat().st_size, off))
        else:
            t0 = time.perf_counter()
            r = replay.BufferedReader()
            try:
                with open(hot, 'wb') as f:
                    for k in order:
                        f.write(r.read(k[0], k[1], k[2]))
                    f.flush()
                    os.fsync(f.fileno())
            finally:
                r.close()
            out['build_s'] = time.perf_counter() - t0
            time.sleep(2)
        ext = file_extents(str(hot))
        out['extents'] = None if ext is None else len(ext)
        out['first_extent_lcn'] = ext[0][1] if ext else None
        out['age_s_at_start'] = time.time() - hot.stat().st_mtime
        # persistence probe: first pass as-is, then again after pushing other data through the drive cache
        s = seq_read([str(hot)])
        out['first_pass_unbuffered'] = dict(s, mb_s=s['bytes'] / 1e6 / s['seconds'], age_s=time.time() - hot.stat().st_mtime)
        out['drive_cache_flush'] = evict(files)
        s = seq_read([str(hot)])
        out['after_flush_unbuffered'] = dict(s, mb_s=s['bytes'] / 1e6 / s['seconds'], age_s=time.time() - hot.stat().st_mtime)
        a, b, c, cb = [], [], [], []
        for chunk_mib in ((4,) if remeasure else (1, 4, 8)):
            for _ in range(reps):
                seq_read([str(hot)])  # unbuffered pass evicts the file cached pages
                CHUNK = chunk_mib * MIB
                s = seq_read([str(hot)], buffered=True)
                a.append(dict(chunk_mib=chunk_mib, seconds=s['seconds']))
                if chunk_mib == 4 and not remeasure:
                    b.append(replay.replay(mapped)['total_s'])  # same pages now in RAM
        CHUNK = 4 * MIB
        for _ in range(reps):
            seq_read([str(hot)])
            c.append(replay.replay(mapped, True)['total_s'])
            if not remeasure:
                seq_read([str(hot)])
                cb.append(replay.replay(mapped)['total_s'])
        m4 = med([x['seconds'] for x in a if x['chunk_mib'] == 4])
        out.update(
            a_full_read_buffered=dict(runs=a, median_4mib_s=m4, over_ssd_cold=m4 / ssd_cold,
                                      mb_s=off / 1e6 / m4),
            c_cold_replay_unbuffered=dict(runs=c, median_s=med(c), over_ssd_cold=med(c) / ssd_cold),
            note='a+b = read hot file then replay from RAM; c = no prefetch, trace played straight from the contiguous file')
        if not remeasure:
            out.update(b_ram_replay=dict(runs=b, median_s=med(b), over_ssd_cold=med(b) / ssd_cold),
                       c_cold_replay_buffered=dict(runs=cb, median_s=med(cb), over_ssd_cold=med(cb) / ssd_cold))
        out['control'] = control_reads(str(hot), ctl, reps)
        out['age_s_at_end'] = time.time() - hot.stat().st_mtime
    finally:
        if not (keep or remeasure):
            hot.unlink(missing_ok=True)
        out['hot_contig_deleted'] = not hot.exists()
    name = 'contig_persistence_%s.json' % (tag or ('remeasure' if remeasure else 't0')) if (keep or remeasure) else 'contig_upper_bound.json'
    path = Path(out_dir) / name
    path.write_text(json.dumps(out, indent=1))
    print(path, json.dumps({k: v for k, v in out.items() if k.startswith(('a_', 'b_', 'c_', 'first_pass', 'after_flush', 'control'))}, default=str)[:3000])


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--root-hdd', default=r'D:\AsterBench')
    ap.add_argument('--root-ssd', default=r'C:\AsterBench')
    ap.add_argument('--repeat', type=int, default=3)
    ap.add_argument('--budget-min', type=float, default=40, help='stop starting new passes after this many minutes')
    ap.add_argument('--out-dir', default=str(OUT))
    ap.add_argument('--contig', action='store_true', help='only run the relocation upper-bound experiment (hot_contig.bin)')
    ap.add_argument('--contig-keep', action='store_true', help='with --contig: keep hot_contig.bin and write contig_persistence_<tag>.json')
    ap.add_argument('--contig-remeasure', metavar='PATH', help='reuse an existing hot_contig.bin; redo only (a) 4MiB read and (c) direct replay')
    ap.add_argument('--tag', help='time-point label for contig_persistence_<tag>.json (default t0 / remeasure)')
    a = ap.parse_args(argv)
    os.chdir(REPO)
    if a.contig or a.contig_keep or a.contig_remeasure:
        Path(a.out_dir).mkdir(parents=True, exist_ok=True)
        contig(a.root_hdd, a.root_ssd, a.repeat, a.out_dir, a.contig_keep, a.contig_remeasure, a.tag)
        return 0
    deadline = T0 + a.budget_min * 60
    meta = json.loads((Path(a.root_hdd) / dataset.MARKER).read_text())
    files = [str(Path(a.root_hdd) / f['rel']) for f in meta['files']]
    sizes = [f['size'] for f in meta['files']]
    ev_hdd, ev_ssd = dataset.load_trace(a.root_hdd), dataset.load_trace(a.root_ssd)
    out_dir = Path(a.out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)
    res = dict(timestamp=time.strftime('%Y%m%d_%H%M%S'), argv=sys.argv[1:], roots=dict(hdd=a.root_hdd, ssd=a.root_ssd),
               repeat=a.repeat, budget_min=a.budget_min, environment=environment(dict(hdd=a.root_hdd, ssd=a.root_ssd)),
               disk_activity_before=disk_activity(), dataset_bytes=meta['total_bytes'])
    path = out_dir / ('sweep_%s.json' % res['timestamp'])

    def save(stage):
        res['stage_done'], res['elapsed_s'] = stage, time.time() - T0
        path.write_text(json.dumps(res, indent=1))
        log('saved', stage)

    log('disk activity before', res['disk_activity_before'])
    # stage 0: SSD cold reference and same-session HDD cold baseline (unbuffered proxy, as bench.run)
    ssd, hdd = [], []
    for _ in range(a.repeat):
        ssd.append(replay.replay(ev_ssd, True)['total_s'])
        hdd.append(replay.replay(ev_hdd, True)['total_s'])
    res['ssd_cold_s'], res['hdd_cold_s'] = dict(runs=ssd, median=med(ssd)), dict(runs=hdd, median=med(hdd))
    ssd_cold = med(ssd)
    log('ssd cold', ssd, 'hdd cold', hdd)
    save('stage0')
    # step 1: floors and the read-everything upper bound
    res['floors'] = floors(files, sizes, ev_hdd, a.repeat)
    ub = []
    for _ in range(a.repeat):
        evict(files)
        ub.append(seq_read(files, buffered=True))
        log('read-all buffered', ub[-1])
    res['read_all_buffered'] = dict(runs=ub, median_s=med([u['seconds'] for u in ub]), bytes=ub[0]['bytes'],
                                    over_ssd_cold=med([u['seconds'] for u in ub]) / ssd_cold)
    save('floors')
    # step 2: threads (the CLI and reader accept 1..4 only; 8 is recorded as unsupported)
    t8 = subprocess.run(cli_cmd(8, 1200).replace(' --execute', ''), shell=True, capture_output=True, text=True,
                        env=dict(os.environ, ASTER_TRACE=str(Path(a.root_hdd) / 'trace.json'), ASTER_ROOT=a.root_hdd))
    res['threads_8'] = dict(returncode=t8.returncode, stderr=t8.stderr[-300:])
    res['threads'] = run_configs({'threads=%d,gap=1200' % t: cli_cmd(t, 1200) for t in (1, 2, 4)}, a.repeat, ev_hdd,
                                 a.root_hdd, files, ssd_cold, deadline)
    ok = {k: v for k, v in res['threads'].items() if 'total_s' in v}
    best_t = int(min(ok, key=lambda k: ok[k]['total_s']).split(',')[0].split('=')[1])
    res['best_threads'] = best_t
    save('threads')
    # step 3: gaps at threads=1 and best threads (gap 1200 at threads=1 reused from step 2)
    cfgs = {'threads=1,gap=%d' % g: cli_cmd(1, g) for g in (0, 256, 4096, 16384)}
    if best_t != 1:
        cfgs.update({'threads=%d,gap=%d' % (best_t, g): cli_cmd(best_t, g) for g in (0, 256, 1200, 4096, 16384)})
    res['gaps'] = run_configs(cfgs, a.repeat, ev_hdd, a.root_hdd, files, ssd_cold, deadline)
    save('gaps')
    # step 4: full-file-ratio, no seq hint, 1 pass each, threads=1 gap=1200
    res['full_file_ratio'] = run_configs({'ratio=%s,threads=1,gap=1200' % r: cli_cmd(1, 1200, r) for r in (0.05, 0.1)}, 1,
                                         ev_hdd, a.root_hdd, files, ssd_cold, deadline)
    res['disk_activity_after'] = disk_activity(n=2)
    save('done')
    print(path)
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
