"""Compare ways of bringing the hot zone into the OS file cache: read loop, PrefetchVirtualMemory (one call / per file),
overlapped ReadFile (queue depth N), mmap page touch. Each trial: cold recipe (stream_tune.make_cold), stream, then a buffered
trace replay (a RAM hit is ~0.05 s) so a method that returns before its I/O is done shows up as a slow replay.
python -m bench.prefetch_methods --root D:\\AsterBench --repeat 3 --out results\\prefetch_methods_20261102\\methods.json"""
import argparse, json, random, statistics, time
from pathlib import Path

from prefetch.reader import read_ops
from prefetch.scheduler import MIB

from . import cache_control, replay, stream_tune

# label -> (method, threads); threads is the queue depth for overlapped
VARIANTS = {'read': ('read', 1), 'pvm': ('pvm', 1), 'pvm-file': ('pvm-file', 1), 'overlapped-qd2': ('overlapped', 2),
            'overlapped-qd3': ('overlapped', 3), 'overlapped-qd4': ('overlapped', 4), 'mmap-touch': ('mmap-touch', 1)}


def trial(label, ops, events, flush, chunk=MIB):
    method, threads = VARIANTS[label]
    cold = stream_tune.make_cold(ops, flush)
    c0 = time.process_time()
    r = read_ops(ops, threads, False, chunk, True, method)
    cpu = time.process_time() - c0
    if r['errors'] or not r['bytes']:
        raise RuntimeError('%s failed: %r' % (label, r))
    p = replay.replay(events)
    return dict(label=label, stream_s=r['seconds'], cpu_s=round(cpu, 4), bytes=r['bytes'], requests=r['requests'],
                replay_s=round(p['total_s'], 4), replay_crc32=p['crc32'], replay_bytes=p['bytes'],
                cold_method=cold['cold_method'])


def table(rows):
    out = []
    for label in dict.fromkeys(r['label'] for r in rows):
        v = [r for r in rows if r['label'] == label]
        med = lambda k: round(statistics.median(r[k] for r in v), 4)
        out.append(dict(label=label, stream_s=med('stream_s'), min_s=min(r['stream_s'] for r in v),
                        max_s=max(r['stream_s'] for r in v), replay_s=med('replay_s'), cpu_s=med('cpu_s'),
                        bytes=v[0]['bytes'], crc_ok=len({r['replay_crc32'] for r in v}) == 1))
    return sorted(out, key=lambda t: t['stream_s'])


def run(root, labels, repeat, flush_mib, log=print):
    ops = stream_tune.hot_ops(root)
    events = json.loads((Path(root) / 'trace.json').read_text())
    flush = stream_tune.cold_ops(ops, flush_mib)
    rows, rng = [], random.Random(1)
    for rep in range(repeat):
        order = list(labels)
        rng.shuffle(order)
        for label in order:
            rows.append(dict(rep=rep, **trial(label, ops, events, flush)))
            log('%d %-15s stream=%.3fs replay=%.3fs cpu=%.3fs' % (rep, label, rows[-1]['stream_s'], rows[-1]['replay_s'], rows[-1]['cpu_s']))
    return dict(ops=len(ops), hot_bytes=sum(o[2] for o in ops), flush_bytes=sum(o[2] for o in flush), rows=rows, table=table(rows))


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--root', required=True)
    ap.add_argument('--methods', nargs='+', choices=sorted(VARIANTS), default=list(VARIANTS))
    ap.add_argument('--repeat', type=int, default=3)
    ap.add_argument('--flush-mib', type=int, default=300, help='cold data read before each trial to push the zone out of the drive cache')
    ap.add_argument('--out', type=Path)
    a = ap.parse_args(argv)
    res = run(a.root, a.methods, a.repeat, a.flush_mib)
    res.update(is_admin=cache_control.is_admin(), time=time.strftime('%Y%m%d_%H%M%S'))
    if a.out:
        a.out.parent.mkdir(parents=True, exist_ok=True)
        a.out.write_text(json.dumps(res, indent=1))
    for t in res['table']:
        print(t)
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
