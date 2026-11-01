"""Concurrent mode: the stream (prefetch.reader over the physical-order ops) and the trace replay start together.
Wall clock from the start to the last replay request is the felt loading time; stream_done_s is when the stream ended.
python -m bench.concurrent_run --root D:\AsterBench --repeat 3 [--think-us 250] [--out file.json]"""
import argparse, json, statistics, threading, time
from pathlib import Path

from prefetch.reader import read_ops

from . import cache_control, dataset, replay, stream_tune

SLOW_S = 1e-3  # a request slower than this did not hit RAM: the replay got ahead of the stream and went to disk


def run_once(events, ops, qd=1, chunk=1 << 20, hint=False, reuse=True, think_us=0):
    """Start the stream thread, then replay at once. think_us = game compute time slept after every request."""
    box, lat = {}, []
    r = replay.BufferedReader()

    def stream():
        box.update(read_ops(ops, qd, hint, chunk, reuse))
        box['done_at'] = time.perf_counter()

    th = threading.Thread(target=stream)
    t0 = time.perf_counter()
    th.start()
    try:
        for e in events:
            t = time.perf_counter()
            r.read(e['path'], e['offset'], e['length'])
            lat.append(time.perf_counter() - t)
            if think_us:
                time.sleep(think_us / 1e6)
    finally:
        wall = time.perf_counter() - t0
        r.close()
    th.join()
    s = sorted(lat)
    return dict(wall_s=wall, stream_done_s=box['done_at'] - t0, stream_s=box['seconds'], stream_bytes=box['bytes'],
                stream_errors=box['errors'], requests=len(lat), slow_requests=sum(x > SLOW_S for x in lat),
                slow_ratio=sum(x > SLOW_S for x in lat) / len(lat), replay_sum_s=sum(lat), p50_ms=replay._pct(s, 50) * 1e3,
                p95_ms=replay._pct(s, 95) * 1e3, p99_ms=replay._pct(s, 99) * 1e3, think_us=think_us)


def measure(root, repeat, thinks, flush_mib, qd=1, chunk=1 << 20, hint=False, reuse=True, log=print):
    events = dataset.load_trace(root)
    ops = stream_tune.hot_ops(root)
    flush = stream_tune.cold_ops(ops, flush_mib)
    res = {}
    for think in thinks:
        runs = []
        for _ in range(repeat):
            cold = stream_tune.make_cold(ops, flush)
            runs.append(dict(run_once(events, ops, qd, chunk, hint, reuse, think), cold_method=cold['cold_method']))
            log('think_us=%d wall=%.3fs stream_done=%.3fs slow=%.1f%%' % (think, runs[-1]['wall_s'], runs[-1]['stream_done_s'],
                                                                      100 * runs[-1]['slow_ratio']))
        res[str(think)] = dict(runs=runs, **{'median_' + k: statistics.median(r[k] for r in runs)
                                             for k in ('wall_s', 'stream_done_s', 'slow_ratio', 'replay_sum_s')})
    return res


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--root', required=True)
    ap.add_argument('--repeat', type=int, default=3)
    ap.add_argument('--think-us', type=int, nargs='+', default=[0], help='per-request game compute time(s) to model, one series each')
    ap.add_argument('--threads', type=int, default=1)
    ap.add_argument('--chunk-kib', type=int, default=1024)
    ap.add_argument('--seq-hint', action='store_true')
    ap.add_argument('--no-reuse', action='store_true')
    ap.add_argument('--flush-mib', type=int, default=300)
    ap.add_argument('--out', type=Path)
    a = ap.parse_args(argv)
    res = dict(root=a.root, is_admin=cache_control.is_admin(), time=time.strftime('%Y%m%d_%H%M%S'),
               series=measure(a.root, a.repeat, a.think_us, a.flush_mib, a.threads, a.chunk_kib * 1024, a.seq_hint, not a.no_reuse))
    if a.out:
        a.out.parent.mkdir(parents=True, exist_ok=True)
        a.out.write_text(json.dumps(res, indent=1))
    print(json.dumps({k: {m: v[m] for m in v if m.startswith('median_')} for k, v in res['series'].items()}, indent=1))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
