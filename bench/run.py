"""Benchmark driver: HDD/SSD cold + warm replay, optional prewarm, JSON result with environment and verdict ratios."""
import argparse, ctypes, json, os, platform, statistics, subprocess, sys, time
from pathlib import Path

from . import cache_control, dataset, replay

OUT_DIR = Path(__file__).resolve().parent.parent / 'results' / 'bench_20261101'


def _drive_model(letter):
    ps = ("$p=Get-Partition -DriveLetter %s -ErrorAction Stop;"
          "$d=Get-PhysicalDisk | Where-Object {$_.DeviceId -eq [string]$p.DiskNumber};"
          "$d | Select-Object DeviceId,FriendlyName,MediaType,BusType | ConvertTo-Json -Compress" % letter)
    try:
        r = subprocess.run(['powershell', '-NoProfile', '-Command', ps], capture_output=True, text=True, timeout=60)
        return json.loads(r.stdout) if r.returncode == 0 and r.stdout.strip() else dict(error=(r.stderr or 'empty').strip())
    except (OSError, ValueError, subprocess.TimeoutExpired) as e:
        return dict(error=repr(e))


def _ram_bytes():
    class MS(ctypes.Structure):
        _fields_ = [('len', ctypes.c_ulong), ('load', ctypes.c_ulong)] + [(n, ctypes.c_ulonglong) for n in
                                                                        ('total', 'avail', 'tpf', 'apf', 'tv', 'av', 'ae')]
    m = MS()
    m.len = ctypes.sizeof(MS)
    return m.total if ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(m)) else None


def environment(roots):
    drives = {Path(r).drive[:1]: _drive_model(Path(r).drive[:1]) for r in roots.values()}
    return dict(python=platform.python_version(), platform=platform.platform(), is_admin=cache_control.is_admin(),
                ram_bytes=_ram_bytes(), cpu_count=os.cpu_count(), drive_to_disk=drives)


def naive_prewarm(events):
    """Read the trace's blocks once, sorted by (file, offset), contiguous ranges merged, through the OS cache."""
    merged = []
    for p, s, t in sorted((e['path'], e['offset'], e['offset'] + e['length']) for e in events):
        if merged and merged[-1][0] == p and s <= merged[-1][2]:
            merged[-1][2] = max(merged[-1][2], t)
        else:
            merged.append([p, s, t])
    r, nbytes, t0 = replay.BufferedReader(), 0, time.perf_counter()
    try:
        for p, s, t in merged:
            nbytes += len(r.read(p, s, t - s))
    finally:
        r.close()
    return dict(kind='naive', seconds=time.perf_counter() - t0, bytes=nbytes, ranges=len(merged))


def cmd_prewarm(cmd, root):
    env = dict(os.environ, ASTER_TRACE=str(Path(root) / 'trace.json'), ASTER_ROOT=str(root))
    t0 = time.perf_counter()
    r = subprocess.run(cmd, shell=True, env=env, capture_output=True, text=True)
    return dict(kind='cmd', cmd=cmd, seconds=time.perf_counter() - t0, returncode=r.returncode,
                stdout_tail=r.stdout[-500:], stderr_tail=r.stderr[-500:])


def _prewarmed(events, pre):
    """Apply cold condition, run the prewarm hook (timed separately), then buffered replay."""
    cold = cache_control.make_cold()
    if cold['unbuffered']:  # no purge: an unbuffered pass over the trace ranges evicts their cached pages (observed on this NTFS, not an API guarantee)
        cold['evict_pass_s'] = replay.replay(events, True)['total_s']
    info = pre()
    return dict(prewarm=info, replay=replay.replay(events), cold=cold,
                cache_condition='standby_purge' if cold['cold_method'] == 'standby_purge' else 'unbuffered_evict_observed')


def one_pass(events, root, naive, cmd):
    cold = cache_control.make_cold()
    out = dict(cold=dict(cold, **replay.replay(events, cold['unbuffered'])))
    populated = not cold['unbuffered']
    hooks = ([('prewarm_naive', lambda: naive_prewarm(events))] if naive else []) + \
            ([('prewarm_cmd', lambda: cmd_prewarm(cmd, root))] if cmd else [])
    for name, pre in hooks:
        out[name] = _prewarmed(events, pre)
        populated = True
    if not populated:  # unbuffered cold pass left the OS cache empty: populate, then measure warm
        out['populate'] = replay.replay(events)
    out['warm'] = replay.replay(events)
    return out


def _med(passes, path):
    vals = []
    for p in passes:
        v = p
        for k in path:
            v = v.get(k) if isinstance(v, dict) else None
        vals.append(v)
    return statistics.median(vals) if vals and all(v is not None for v in vals) else None


def summarize(runs):
    s = {}
    for drive, passes in runs.items():
        d = {}
        for name in ('cold', 'warm'):
            d[name] = {m: _med(passes, (name, m)) for m in ('total_s', 'p50_ms', 'p95_ms', 'p99_ms', 'throughput_mib_s')}
        d['cold']['cold_method'] = passes[0]['cold']['cold_method']
        for name in ('prewarm_naive', 'prewarm_cmd'):
            if name in passes[0]:
                d[name] = dict(prewarm_s=_med(passes, (name, 'prewarm', 'seconds')),
                               replay_total_s=_med(passes, (name, 'replay', 'total_s')),
                               replay_p50_ms=_med(passes, (name, 'replay', 'p50_ms')),
                               replay_p95_ms=_med(passes, (name, 'replay', 'p95_ms')),
                               cache_condition=passes[0][name]['cache_condition'])
                d[name]['total_s'] = d[name]['prewarm_s'] + d[name]['replay_total_s']
        s[drive] = d
    return s


def verdict(summary):
    h, c = summary['hdd'], summary['ssd']
    v = dict(hdd_cold_over_ssd_cold=h['cold']['total_s'] / c['cold']['total_s'],
             hdd_warm_over_ssd_warm=h['warm']['total_s'] / c['warm']['total_s'],
             cold_method=h['cold']['cold_method'], caveats=[])
    for name in ('prewarm_naive', 'prewarm_cmd'):
        if name in h:
            v[name + '_total_over_ssd_cold'] = h[name]['total_s'] / c['cold']['total_s']
            v[name + '_replay_only_over_ssd_cold'] = h[name]['replay_total_s'] / c['cold']['total_s']
            if h[name]['cache_condition'] != 'standby_purge':
                v['caveats'].append(name + ': no standby purge; start state = trace ranges evicted by an unbuffered pass '
                                    '(observed on NTFS, verified by timing on D: and C:, not an API guarantee)')
    if v['cold_method'] != 'standby_purge':
        v['caveats'].append('cold = unbuffered proxy (FILE_FLAG_NO_BUFFERING), not a standby-list purge; drive/controller caches remain')
    return v


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--root-hdd', required=True)
    ap.add_argument('--root-ssd', required=True)
    ap.add_argument('--repeat', type=int, default=3)
    ap.add_argument('--prewarm', choices=('none', 'naive'), default='naive')
    ap.add_argument('--prewarm-cmd', help='external prewarm command; gets env ASTER_TRACE (trace.json path) and ASTER_ROOT')
    ap.add_argument('--files', type=int, default=64)
    ap.add_argument('--min-mib', type=int, default=16)
    ap.add_argument('--max-mib', type=int, default=48)
    ap.add_argument('--trace', type=int, default=4000)
    ap.add_argument('--min-free-gib', type=int, default=5, help='free space that must remain after writing the dataset')
    ap.add_argument('--out-dir', default=str(OUT_DIR))
    ap.add_argument('--cleanup', action='store_true', help='delete both datasets and exit')
    a = ap.parse_args(argv)
    if a.cleanup:
        for r in (a.root_hdd, a.root_ssd):
            dataset.cleanup(r)
        print('cleaned')
        return 0
    meta = dataset.generate(a.root_hdd, a.files, a.min_mib, a.max_mib, a.trace, min_free_gib=a.min_free_gib)
    dataset.mirror(a.root_hdd, a.root_ssd, a.min_free_gib)
    roots = dict(hdd=a.root_hdd, ssd=a.root_ssd)
    runs = {}
    for label, root in roots.items():
        events = dataset.load_trace(root)
        # prewarm hooks run on HDD only (that is the device being improved)
        runs[label] = [one_pass(events, root, a.prewarm == 'naive' and label == 'hdd',
                                a.prewarm_cmd if label == 'hdd' else None) for _ in range(a.repeat)]
    summary = summarize(runs)
    result = dict(timestamp=time.strftime('%Y%m%d_%H%M%S'), argv=sys.argv[1:], dataset=dict(
        files=len(meta['files']), total_bytes=meta['total_bytes'], trace_n=meta['trace_n'], block=meta['block'],
        seed=meta['seed']), roots=roots, repeat=a.repeat, environment=environment(roots), summary=summary,
        verdict=verdict(summary), runs=runs)
    out = Path(a.out_dir)
    out.mkdir(parents=True, exist_ok=True)
    path = out / (result['timestamp'] + '.json')
    path.write_text(json.dumps(result, indent=1))
    print(json.dumps(dict(result_file=str(path), summary=summary, verdict=result['verdict']), indent=1))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
