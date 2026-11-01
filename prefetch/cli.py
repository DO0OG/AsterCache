"""python -m prefetch.cli --plan plan_or_trace.json --origin-root D:\Games [--execute]"""
import argparse
import json
import os
import sys
from pathlib import Path

from .layout import cluster_size, file_extents
from .reader import read_ops
from .scheduler import MIB, breakeven_gap, schedule


def load_ranges(plan_path, origin_root):
    """Accept a planner plan ({'ranges':[...]}) or a trace list ([{'path','offset','length','cost'}]).
    Drops rows outside origin_root / missing; clamps to file size."""
    data = json.loads(Path(plan_path).read_text(encoding='utf-8-sig'))
    rows = data['ranges'] if isinstance(data, dict) else data
    root = Path(origin_root).resolve(strict=True)
    sizes, out = {}, []
    for r in rows:
        p = r.get('path')
        o, n = r.get('offset'), r.get('length')
        if not isinstance(p, str) or type(o) is not int or type(n) is not int or o < 0 or n <= 0:
            raise ValueError('bad row')
        if p not in sizes:
            try:
                rp = Path(p).resolve(strict=True)
                sizes[p] = rp.stat().st_size if rp.is_file() and rp.is_relative_to(root) else None
            except OSError:
                sizes[p] = None
        if sizes[p] is None or o >= sizes[p]:
            continue
        out.append(dict(r, offset=o, length=min(n, sizes[p] - o)))
    return out


def _layout(path):
    ext = file_extents(path)
    cs = cluster_size(path) if ext is not None else None
    return (ext, cs) if ext is not None and cs else None


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--plan', '--trace', dest='plan', type=Path, help='plan or trace JSON (default: $ASTER_TRACE)')
    ap.add_argument('--origin-root', type=Path, help='default: $ASTER_ROOT')
    ap.add_argument('--budget-mib', type=int, default=1024)
    ap.add_argument('--strategy', choices=('physical', 'path', 'none'), default='physical')
    ap.add_argument('--read-mbps', type=float, default=100.0, help='assumed sequential speed for the break-even gap')
    ap.add_argument('--seek-ms', type=float, default=12.0, help='assumed seek cost for the break-even gap')
    ap.add_argument('--gap-kib', type=int, default=0, help='max hole to read through (default 0 = sweep optimum; the read-mbps x seek-ms break-even is unused)')
    ap.add_argument('--full-file-ratio', type=float, default=0.3, help='needed fraction at which a file is read whole (>1 disables)')
    ap.add_argument('--threads', type=int, default=1, help='1..8 (default 1: strict zone order, fastest on the relocated zone, results/stream_tuning_20261101)')
    ap.add_argument('--chunk-kib', type=int, default=1024, help='bytes per read call (256 is 4x slower, 1024..16384 equal)')
    ap.add_argument('--no-reuse', action='store_true', help='close each file between ops (default keeps handles open: 8-15%% faster)')
    ap.add_argument('--seq-hint', action='store_true', help='open with FILE_FLAG_SEQUENTIAL_SCAN (measured 10-17x slower on the relocated zone, do not use)')
    ap.add_argument('--execute', action='store_true')
    a = ap.parse_args(argv)
    a.plan = a.plan or (Path(os.environ['ASTER_TRACE']) if os.environ.get('ASTER_TRACE') else None)
    a.origin_root = a.origin_root or (Path(os.environ['ASTER_ROOT']) if os.environ.get('ASTER_ROOT') else None)
    if not a.plan or not a.origin_root:
        ap.error('--plan/--trace and --origin-root are required (or env ASTER_TRACE / ASTER_ROOT)')
    if a.budget_mib < 1 or (a.gap_kib or 0) < 0 or a.read_mbps <= 0 or a.seek_ms < 0 or not 1 <= a.threads <= 8 or a.chunk_kib < 4:
        ap.error('invalid budget/gap/speed/threads')
    ranges = load_ranges(a.plan, a.origin_root)
    ops, info = schedule(ranges, a.strategy, a.budget_mib * MIB, a.gap_kib * 1024 if a.gap_kib is not None
                         else breakeven_gap(a.read_mbps, a.seek_ms), _layout, os.path.getsize, a.full_file_ratio)
    out = {'strategy': a.strategy, 'seq_hint': a.seq_hint, 'fallback': info['fallback'], 'layout_ok_files': info['layout_ok_files'],
           'layout_files': info['layout_files'], 'full_files': info['full_files'],
           'gap_bytes': info['gap_bytes']}
    if a.execute:
        out.update(read_ops(ops, a.threads, a.seq_hint, a.chunk_kib * 1024, not a.no_reuse))
    else:
        out.update(dry_run=True, bytes=sum(o[2] for o in ops), requests=len(ops), ops=ops)
    print(json.dumps(out, separators=(',', ':')))
    return 0


if __name__ == '__main__':
    sys.exit(main())
