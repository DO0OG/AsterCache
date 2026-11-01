"""AsterCache relayout prototype.

  python -m relayout.cli plan   --trace t1.json [t2.json ...] --origin-root D:\\AsterBench --zone-mib 300 --out plan.json [--dry-run]
  python -m relayout.cli apply  --plan plan.json [--execute] [--journal j.jsonl]   (Administrator)
  python -m relayout.cli verify --plan plan.json
  python -m relayout.cli revert --journal j.jsonl --allowed-root D:\\AsterBench [--execute]   (Administrator)

plan/verify need no privileges. apply/revert exit with {"error":"not_admin"} unless elevated, and without --execute
they move nothing. The journal is JSON Lines.
"""
import argparse
import json
import os
import sys
from pathlib import Path

from prefetch.cli import load_ranges
from prefetch.layout import cluster_size, file_extents

from .apply import PlanRejected, apply_plan, revert_journal, verify_layout
from .mover import NotAdmin, Volume
from .plan import load_plan, make_plan, save_plan

MIB = 1 << 20


def _emit(obj, code=0):
    print(json.dumps(obj, separators=(',', ':')))
    return code


def cmd_plan(a):
    cb = cluster_size(a.origin_root) or a.cluster_bytes
    if not cb:
        raise ValueError('cluster size unknown: pass --cluster-bytes')
    sessions = [load_ranges(t, a.origin_root) for t in a.trace]
    start = None if a.zone_start_lcn == 'auto' else int(a.zone_start_lcn)
    plan = make_plan(sessions, a.origin_root, a.zone_mib * MIB, cb, file_extents, zone_start_lcn=start,
                     preserve_bytes=a.preserve_mib * MIB)
    if not a.dry_run:
        if not a.out:
            raise ValueError('--out is required unless --dry-run')
        save_plan(plan, a.out)
    return _emit({'dry_run': a.dry_run, 'out': None if a.dry_run else str(a.out), 'sessions': len(sessions),
                  'cluster_bytes': cb, 'zone': plan['zone'], 'stats': plan['stats']})


def cmd_apply(a):
    plan = load_plan(a.plan)
    vol = Volume.open(plan['volume'])
    try:
        journal = a.journal or Path(str(a.plan) + '.journal.jsonl')
        roots = a.allowed_root or [plan['origin_root']]
        return _emit(apply_plan(plan, vol, roots, journal, a.execute, a.chunk_mib * MIB, a.write_cap_mib * MIB,
                                save_to=a.plan))
    finally:
        vol.close()


def cmd_verify(a):
    r = verify_layout(load_plan(a.plan))
    r['verdict'] = 'contiguous' if r['contiguous'] else 'non-contiguous'
    return _emit(r)


def cmd_revert(a):
    first = next((json.loads(x) for x in Path(a.journal).read_text(encoding='utf-8').splitlines()
                  if '"type": "step"' in x), None)
    if not first:
        raise ValueError('journal has no steps')
    vol = Volume.open(os.path.splitdrive(first['path'])[0])
    try:
        return _emit(revert_journal(a.journal, vol, a.allowed_root, a.execute, a.chunk_mib * MIB, a.write_cap_mib * MIB))
    finally:
        vol.close()


def build_parser():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest='cmd', required=True)
    p = sub.add_parser('plan')
    p.add_argument('--trace', nargs='+', type=Path, required=True, help='one trace.json per recorded run, oldest first')
    p.add_argument('--origin-root', type=Path, required=True)
    p.add_argument('--zone-mib', type=int, required=True)
    p.add_argument('--zone-start-lcn', default='auto', help="'auto' = pick a free run on the volume at apply time")
    p.add_argument('--cluster-bytes', type=int, help='only when the volume cannot be queried')
    p.add_argument('--preserve-mib', type=int, default=8, help='contiguous runs this long stay in place')
    p.add_argument('--out', type=Path)
    p.add_argument('--dry-run', action='store_true', help='print statistics only, write nothing')
    p.set_defaults(fn=cmd_plan)
    p = sub.add_parser('apply')
    p.add_argument('--plan', type=Path, required=True)
    p.add_argument('--journal', type=Path)
    p.add_argument('--allowed-root', type=Path, action='append', help='default: the plan origin root')
    p.set_defaults(fn=cmd_apply)
    p = sub.add_parser('verify')
    p.add_argument('--plan', type=Path, required=True)
    p.set_defaults(fn=cmd_verify)
    p = sub.add_parser('revert')
    p.add_argument('--journal', type=Path, required=True)
    p.add_argument('--allowed-root', type=Path, action='append', required=True)
    p.set_defaults(fn=cmd_revert)
    for name in ('apply', 'revert'):
        q = sub.choices[name]
        q.add_argument('--execute', action='store_true', help='without it nothing is moved')
        q.add_argument('--chunk-mib', type=int, default=4, help='bytes per FSCTL_MOVE_FILE call (SMR friendly)')
        q.add_argument('--write-cap-mib', type=int, default=2048, help='stop after this much moved data per run')
    return ap


def main(argv=None):
    a = build_parser().parse_args(argv)
    if getattr(a, 'zone_mib', 1) < 1 or getattr(a, 'chunk_mib', 1) < 1 or getattr(a, 'write_cap_mib', 1) < 1:
        return _emit({'error': 'invalid_argument'}, 2)
    try:
        return a.fn(a)
    except NotAdmin:
        return _emit({'error': 'not_admin'}, 3)
    except (PlanRejected, ValueError, OSError, RuntimeError) as e:
        return _emit({'error': type(e).__name__, 'detail': str(e)}, 1)


if __name__ == '__main__':
    sys.exit(main())
