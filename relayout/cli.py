"""AsterCache relayout prototype.

  python -m relayout.cli plan   --trace t1.json [t2.json ...] --origin-root D:\\AsterBench --zone-mib 300 --out plan.json
                                [--min-sessions N] [--dry-run]   (also writes plan.json.prefetch.json)
  python -m relayout.cli probe  --volume X: [--require-volume-label L] [--plan plan.json | --zone-mib N]   (Administrator, read-only)
  python -m relayout.cli apply  --plan plan.json --allowed-root D:\\AsterBench [--require-volume-label L]
                                [--dry-run | --execute] [--journal j.jsonl]   (Administrator)
  python -m relayout.cli verify --plan plan.json
  python -m relayout.cli revert --journal j.jsonl --allowed-root D:\\AsterBench [--require-volume-label L] [--execute]

plan/verify need no privileges. probe/apply/revert exit with {"error":"not_admin"} unless elevated. Without --execute
nothing is moved (--dry-run also stores the chosen zone in the plan file). The plan's volume, origin root, every
--allowed-root and the opened volume must all be the same drive, and --require-volume-label must match the label, or
nothing is touched. The journal is JSON Lines.
"""
import argparse
import json
import os
import sys
from pathlib import Path

from prefetch.cli import load_ranges
from prefetch.layout import cluster_size, file_extents

from .apply import PlanRejected, apply_plan, revert_journal, verify_layout
from .mover import NotAdmin, Volume, find_free_run, volume_label
from .plan import load_plan, make_plan, prefetch_trace, save_plan

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
    min_sessions = a.min_sessions or (2 if len(sessions) >= 2 else 1)
    if min_sessions > len(sessions):
        raise ValueError('--min-sessions %d exceeds the %d traces given' % (min_sessions, len(sessions)))
    plan = make_plan(sessions, a.origin_root, a.zone_mib * MIB, cb, file_extents, zone_start_lcn=start,
                     preserve_bytes=a.preserve_mib * MIB, min_sessions=min_sessions)
    pf = None
    if not a.dry_run:
        if not a.out:
            raise ValueError('--out is required unless --dry-run')
        save_plan(plan, a.out)
        pf = Path(str(a.out) + '.prefetch.json')
        pf.write_text(json.dumps(prefetch_trace(plan, sessions), ensure_ascii=False) + '\n', encoding='utf-8')
    return _emit({'dry_run': a.dry_run, 'out': None if a.dry_run else str(a.out),
                  'prefetch_trace': str(pf) if pf else None, 'min_sessions': min_sessions, 'sessions': len(sessions),
                  'cluster_bytes': cb, 'zone': plan['zone'], 'stats': plan['stats']})


def open_checked(letter, label):
    """Open the volume (admin only); refuse, with nothing touched, if its label is not the required one."""
    vol = Volume.open(letter)
    if label is not None:
        found = volume_label(vol.letter)
        if found != label:
            vol.close()
            raise PlanRejected('volume label %r != required %r on %s' % (found, label, letter))
    return vol


def cmd_probe(a):
    zone_clusters = load_plan(a.plan)['stats']['zone_used_clusters'] if a.plan else None
    vol = open_checked(a.volume, a.require_volume_label)
    try:
        bm = vol.bitmap()
        free = sum(n for _, n in bm.free_runs())
        need = zone_clusters or a.zone_mib * MIB // vol.cluster_bytes
        return _emit({'volume': vol.letter, 'label': volume_label(vol.letter), 'cluster_bytes': vol.cluster_bytes,
                      'total_clusters': vol.total_clusters, 'free_clusters_ntfs': vol.free_clusters,
                      'free_clusters_bitmap': free, 'bitmap_bits': bm.nbits,
                      'free_match': abs(free - vol.free_clusters) <= max(4096, vol.total_clusters // 1000),
                      'mft_zone': list(vol.mft_zone), 'zone_clusters_needed': need,
                      'zone_candidate_lcn': find_free_run(bm, need, align=4)})
    finally:
        vol.close()


def cmd_apply(a):
    if a.execute and a.dry_run:
        raise ValueError('--execute and --dry-run are exclusive')
    plan = load_plan(a.plan)
    vol = open_checked(plan['volume'], a.require_volume_label)
    try:
        journal = a.journal or Path(str(a.plan) + '.journal.jsonl')
        return _emit(apply_plan(plan, vol, a.allowed_root, journal, a.execute, a.chunk_mib * MIB,
                                a.write_cap_mib * MIB, save_to=a.plan, dry_run=a.dry_run))
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
    vol = open_checked(os.path.splitdrive(first['path'])[0], a.require_volume_label)
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
    p.add_argument('--min-sessions', type=int, help='drop blocks read in fewer runs (default: 2 with 2+ traces, else 1)')
    p.add_argument('--out', type=Path, help='plan file; <out>.prefetch.json (prefetch input, exactly the zone blocks) is written next to it')
    p.add_argument('--dry-run', action='store_true', help='print statistics only, write nothing')
    p.set_defaults(fn=cmd_plan)
    p = sub.add_parser('apply')
    p.add_argument('--plan', type=Path, required=True)
    p.add_argument('--journal', type=Path)
    p.add_argument('--allowed-root', type=Path, action='append', required=True)
    p.add_argument('--dry-run', action='store_true', help='resolve and store the zone, move nothing')
    p.set_defaults(fn=cmd_apply)
    p = sub.add_parser('probe', description='read-only FSCTL check: volume data, bitmap, zone candidate')
    p.add_argument('--volume', required=True)
    p.add_argument('--plan', type=Path)
    p.add_argument('--zone-mib', type=int, default=300)
    p.set_defaults(fn=cmd_probe)
    p = sub.add_parser('verify')
    p.add_argument('--plan', type=Path, required=True)
    p.set_defaults(fn=cmd_verify)
    p = sub.add_parser('revert')
    p.add_argument('--journal', type=Path, required=True)
    p.add_argument('--allowed-root', type=Path, action='append', required=True)
    p.set_defaults(fn=cmd_revert)
    for name in ('probe', 'apply', 'revert'):
        sub.choices[name].add_argument('--require-volume-label', help='refuse unless the volume has exactly this label')
    for name in ('apply', 'revert'):
        q = sub.choices[name]
        q.add_argument('--execute', action='store_true', help='without it nothing is moved')
        q.add_argument('--chunk-mib', type=int, default=4, help='bytes per FSCTL_MOVE_FILE call (SMR friendly)')
        q.add_argument('--write-cap-mib', type=int, default=2048, help='stop after this much moved data per run')
    return ap


def main(argv=None):
    a = build_parser().parse_args(argv)
    if getattr(a, 'zone_mib', 1) < 1 or (getattr(a, 'min_sessions', None) or 1) < 1 or getattr(a, 'chunk_mib', 1) < 1 or getattr(a, 'write_cap_mib', 1) < 1:
        return _emit({'error': 'invalid_argument'}, 2)
    try:
        return a.fn(a)
    except NotAdmin:
        return _emit({'error': 'not_admin'}, 3)
    except (PlanRejected, ValueError, OSError, RuntimeError) as e:
        return _emit({'error': type(e).__name__, 'detail': str(e)}, 1)


if __name__ == '__main__':
    sys.exit(main())
