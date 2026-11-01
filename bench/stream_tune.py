"""Stream read tuning on a relocated hot zone: chunk x queue depth x SEQUENTIAL_SCAN sweep with a cold-start recipe that
works without admin (unbuffered evict of the hot ranges, then an unbuffered read of cold data to flush the drive cache).
python -m bench.stream_tune --root D:\AsterBench --repeat 3 --out results\stream_tuning_20261101\sweep.json"""
import argparse, itertools, json, os, random, statistics, time
from pathlib import Path

from prefetch import cli
from prefetch.layout import physical_key
from prefetch.reader import read_ops
from prefetch.scheduler import MIB, schedule

from . import cache_control, replay


def hot_ops(root, strategy='physical'):
    """The prefetch op list exactly as prefetch.cli builds it for root\trace.json (gap 0, budget 1 GiB)."""
    ranges = cli.load_ranges(Path(root) / 'trace.json', root)
    return schedule(ranges, strategy, 1024 * MIB, 0, cli._layout, os.path.getsize, 0.3)[0]


def cold_ops(ops, mib):
    """Up to mib MiB of 1 MiB reads from the ops' files, only from extents outside the hot ops' LCN span."""
    lays = {p: cli._layout(p) for p in {o[0] for o in ops}}
    keys = [physical_key(lays[p][0], o, lays[p][1]) for p, o, _ in ops if lays[p]]
    if not keys:
        return []
    lo, hi = min(keys), max(keys)
    out, left = [], mib * MIB
    for path in sorted(lays):
        cs = lays[path][1]
        for v, lcn, n in lays[path][0]:
            if lcn <= hi and lcn + n >= lo:
                continue
            for o in range(0, n * cs, MIB):
                m = min(MIB, n * cs - o, left)
                if m > 0:
                    out.append((path, v * cs + o, m))
                    left -= m
    return out


def read_unbuffered(ops):
    r = replay.UnbufferedReader()
    try:
        for p, o, n in ops:
            r.read(p, o, n)
    finally:
        r.close()


def make_cold(ops, flush):
    """Standby purge when admin; otherwise evict the hot ranges with an unbuffered pass. Then flush the drive cache."""
    cold = cache_control.make_cold()
    if cold['unbuffered']:
        read_unbuffered(ops)
    read_unbuffered(flush)
    return cold


def sweep(root, chunks, qds, hints, repeat, flush_mib, reuse=False, log=print):
    ops = hot_ops(root)
    flush = cold_ops(ops, flush_mib)
    combos = list(itertools.product(chunks, qds, hints))
    rows, rng = [], random.Random(1)
    for rep in range(repeat):
        rng.shuffle(combos)
        for chunk, qd, hint in combos:
            cold = make_cold(ops, flush)
            r = read_ops(ops, qd, hint, chunk, reuse)
            if r['errors'] or not r['bytes']:
                raise RuntimeError('prefetch read failed: %r' % r)
            rows.append(dict(rep=rep, chunk=chunk, qd=qd, hint=hint, reuse=reuse, seconds=r['seconds'], bytes=r['bytes'],
                             cold_method=cold['cold_method']))
            log('%d chunk=%7d qd=%d hint=%d reuse=%d %.3fs' % (rep, chunk, qd, hint, reuse, r['seconds']))
    return dict(ops=len(ops), hot_bytes=sum(o[2] for o in ops), flush_bytes=sum(o[2] for o in flush), rows=rows,
                table=table(rows))


def table(rows):
    keys = sorted({(r['chunk'], r['qd'], r['hint'], r['reuse']) for r in rows})
    out = []
    for k in keys:
        v = [r['seconds'] for r in rows if (r['chunk'], r['qd'], r['hint'], r['reuse']) == k]
        out.append(dict(chunk_kib=k[0] // 1024, qd=k[1], seq_hint=k[2], reuse=k[3], median_s=round(statistics.median(v), 4),
                        min_s=min(v), max_s=max(v), mib_s=round(rows[0]['bytes'] / MIB / statistics.median(v), 1)))
    return sorted(out, key=lambda t: t['median_s'])


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--root', required=True)
    ap.add_argument('--chunks-kib', type=int, nargs='+', default=[256, 1024, 4096, 8192, 16384])
    ap.add_argument('--qds', type=int, nargs='+', default=[1, 2, 4])
    ap.add_argument('--hints', type=int, nargs='+', default=[0, 1])
    ap.add_argument('--repeat', type=int, default=3)
    ap.add_argument('--flush-mib', type=int, default=400, help='cold data read before each trial to push the zone out of the drive cache')
    ap.add_argument('--reuse', action='store_true', help='keep file handles open across ops')
    ap.add_argument('--out', type=Path)
    a = ap.parse_args(argv)
    res = sweep(a.root, [c * 1024 for c in a.chunks_kib], a.qds, [bool(h) for h in a.hints], a.repeat, a.flush_mib, a.reuse)
    res.update(is_admin=cache_control.is_admin(), time=time.strftime('%Y%m%d_%H%M%S'))
    if a.out:
        a.out.parent.mkdir(parents=True, exist_ok=True)
        a.out.write_text(json.dumps(res, indent=1))
    for t in res['table']:
        print(t)
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
