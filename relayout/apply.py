"""Execute a relayout plan with a journal, resume after interruption, revert, and verify the result.

The journal is JSON Lines (one JSON object per line, fsynced). It is only a resume/revert hint: the truth is always the
file's current extents (re-queried) plus the SHA-256 samples. Without execute=True nothing is moved or written.
"""
from __future__ import annotations

import bisect
import hashlib
import json
import os
import time
from collections import defaultdict
from pathlib import Path

from prefetch.layout import drive_root, file_extents

from .mover import find_free_run, sample_digest, skip_reason
from .plan import _pieces, count_runs, resolve_plan, save_plan, validate_plan

MOVE_RETRIES = 3
TASK_SPAN = 64 << 10  # bytes hashed at each end of every moved range
ZONE_ALIGN = 4  # clusters (16 KiB with 4 KiB clusters): shadow-copy friendly zone start


class PlanRejected(ValueError):
    """Plan refused before any move (path outside root, changed file, zone not free, ...)."""


def plan_sha(plan):
    keep = {k: plan[k] for k in ('origin_root', 'cluster_bytes', 'tasks', 'order')}
    return hashlib.sha256(json.dumps(keep, sort_keys=True).encode()).hexdigest()


def inside(path, roots):
    p = os.path.normcase(os.path.realpath(drive_root(path)))
    for r in roots:
        r = os.path.normcase(os.path.realpath(drive_root(r)))
        if p == r or p.startswith(r.rstrip('\\/') + os.sep):
            return True
    return False


def _drive(path):
    return os.path.splitdrive(os.path.realpath(drive_root(path)))[0].upper()


def check_roots(roots, letter):
    """Every allowed root must really live on the volume that is about to be opened."""
    if not roots:
        raise PlanRejected('no allowed root given')
    for r in roots:
        if _drive(str(r)) != letter:
            raise PlanRejected('allowed root %s is on %s, not on the opened volume %s' % (r, _drive(str(r)), letter))


def check_plan(plan, roots, letter):
    """Reject the whole plan when plan volume, origin root, allowed roots and the opened volume disagree, when any path
    (after resolving links) is outside the allowed roots or off the volume, or a file changed since planning."""
    letter = letter.upper().rstrip('\\')
    if plan['volume'].upper() != letter or _drive(plan['origin_root']) != letter:
        raise PlanRejected('plan volume %s / origin %s != opened volume %s' % (plan['volume'], plan['origin_root'], letter))
    check_roots(roots, letter)
    for p in {t['path'] for t in plan['tasks']} | {o['path'] for o in plan['order']}:
        if _drive(p) != letter or not inside(p, roots):
            raise PlanRejected('outside_allowed_root: %s' % p)
    for p, fp in plan['fingerprints'].items():
        st = os.stat(p)
        if [st.st_size, st.st_mtime_ns] != list(fp):
            raise PlanRejected('source_changed: %s' % p)


class Journal:
    def __init__(self, path):
        self.path, self.rows = Path(path), []
        self._fix_newline = False
        if self.path.exists():
            lines = [x for x in self.path.read_text(encoding='utf-8').splitlines() if x.strip()]
            for i, x in enumerate(lines):
                try:
                    self.rows.append(json.loads(x))
                except ValueError:
                    if i != len(lines) - 1:
                        raise
                    self._fix_newline = True  # last line cut off by a crash mid-write: drop it, appends start on a new line
        self._f = None

    def first(self, kind, key, val):
        return next((r for r in self.rows if r['type'] == kind and r.get(key) == val), None)

    def last_state(self, idx):
        st = [r for r in self.rows if r['type'] == 'step' and r['idx'] == idx and 'state' in r]
        return st[-1]['state'] if st else None

    def add(self, **row):
        if self._f is None:
            if self._fix_newline:  # drop the cut-off fragment first, or it would become a bad middle line
                self.path.write_text(''.join(json.dumps(r) + '\n' for r in self.rows), encoding='utf-8')
            self._f = open(self.path, 'a', encoding='utf-8')
        self._f.write(json.dumps(row) + '\n')
        self._f.flush()
        os.fsync(self._f.fileno())
        self.rows.append(row)

    def close(self):
        if self._f:
            self._f.close()


def done_prefix(ext, vcn, n, target):
    """Clusters from the start of [vcn, vcn+n) that already sit at target + offset."""
    done = 0
    for v, lcn, m in _pieces(ext or [], vcn, vcn + n):
        if v != vcn + done or lcn != target + done:
            break
        done += m
    return done


def _own_ranges(plan, extents_fn):
    return sorted((lcn, lcn + n) for p in {t['path'] for t in plan['tasks']} for _, lcn, n in (extents_fn(p) or []))


def check_zone_free(plan, bitmap, extents_fn):
    """Remaining target clusters must be free, or held by the plan's own files (a move vacates them first)."""
    own = _own_ranges(plan, extents_fn)
    starts = [a for a, _ in own]
    for t in plan['tasks']:
        done = done_prefix(extents_fn(t['path']), t['vcn'], t['clusters'], t['target_lcn'])
        for lcn in range(t['target_lcn'] + done, t['target_lcn'] + t['clusters']):
            j = bisect.bisect_right(starts, lcn) - 1
            if bitmap.used(lcn) and not (j >= 0 and lcn < own[j][1]):
                raise PlanRejected('zone_not_free: LCN %d is in use by another file' % lcn)


def resolve_zone(plan, vol, extents_fn):
    """Fix the zone start (free run from the bitmap when the plan has none) and check the targets."""
    bm = vol.bitmap()
    if plan['zone']['start_lcn'] is None:
        need = plan['stats']['zone_used_clusters']
        base = find_free_run(bm, need, prefer_low_lcn=True, align=ZONE_ALIGN)
        if base is None:
            raise PlanRejected('no free run of %d clusters' % need)
        plan = resolve_plan(plan, base)
    else:
        check_zone_free(plan, bm, extents_fn)
    return plan


def _move_chunk(vol, t, off, c, sleep):
    err = None
    for attempt in range(MOVE_RETRIES):
        try:
            vol.move(t['path'], t['vcn'] + off, t['target_lcn'] + off, c)
            return None
        except OSError as e:
            err = str(e)
            if attempt + 1 < MOVE_RETRIES:
                sleep(0.2)
    return err  # all retries failed


def moved_ranges(plan):
    """{path: [(byte_offset, length)]}: first and last TASK_SPAN bytes of every task's VCN range, for the SHA samples."""
    cb, out = plan['cluster_bytes'], defaultdict(list)
    for t in plan['tasks']:
        lo, hi = t['vcn'] * cb, (t['vcn'] + t['clusters']) * cb
        n = min(TASK_SPAN, hi - lo)
        out[t['path']] += [(lo, n), (hi - n, n)]
    return out


def apply_plan(plan, vol, roots, journal_path, execute=False, chunk_bytes=4 << 20, write_cap=2 << 30,
               extents_fn=file_extents, digest_fn=sample_digest, attr_fn=None, sleep=time.sleep, save_to=None,
               dry_run=False):
    """Returns a report dict. execute=False moves nothing and writes no journal. dry_run=True additionally fixes an
    automatic zone and stores it in save_to (a plan-file write, still no move)."""
    validate_plan(plan)
    check_plan(plan, roots, vol.letter)
    cb = vol.cluster_bytes
    if cb != plan['cluster_bytes']:
        raise PlanRejected('cluster size %s != plan %s' % (cb, plan['cluster_bytes']))
    auto_zone = plan['zone']['start_lcn'] is None
    plan = resolve_zone(plan, vol, extents_fn)
    chunk = max(1, chunk_bytes // cb)
    skipped = {}
    for p in sorted({t['path'] for t in plan['tasks']}):
        why = skip_reason(p, attr_fn) if attr_fn else skip_reason(p)
        if why:
            skipped[p] = why
    todo = []
    for i, t in enumerate(plan['tasks']):
        d = done_prefix(extents_fn(t['path']), t['vcn'], t['clusters'], t['target_lcn'])
        if t['path'] not in skipped and d < t['clusters']:
            todo.append((i, t, d))
    rep = {'zone_start_lcn': plan['zone']['start_lcn'], 'tasks_total': len(plan['tasks']), 'tasks_todo': len(todo),
           'bytes_todo': sum((t['clusters'] - d) * cb for _, t, d in todo), 'skipped_files': skipped,
           'executed': execute, 'moved_bytes': 0, 'failed': [], 'stopped': None}
    persist = bool(save_to and auto_zone and (execute or dry_run))  # the chosen zone must survive a resume
    if persist:
        save_plan(plan, save_to)
    rep.update(dry_run=dry_run, plan_saved=persist)
    if not execute:
        return rep
    return _execute(plan, vol, todo, rep, journal_path, chunk, write_cap, extents_fn, digest_fn, sleep)


def _execute(plan, vol, todo, rep, journal_path, chunk, write_cap, extents_fn, digest_fn, sleep):
    jr, before, state = Journal(journal_path), {}, {'written': 0}
    ranges = moved_ranges(plan)

    def digest(p):  # same ranges every run, so a resumed run can reuse the journaled 'before' value
        return digest_fn(p, os.path.getsize(p), ranges.get(p, ()))
    psha = plan_sha(plan)
    if jr.rows and not jr.first('header', 'plan_sha', psha):
        raise PlanRejected('journal belongs to a different plan')
    if not jr.rows:
        jr.add(type='header', plan_sha=psha, zone_start_lcn=plan['zone']['start_lcn'], cluster_bytes=vol.cluster_bytes,
               started=time.time())
    try:
        _run_tasks(vol, todo, rep, jr, before, state, chunk, write_cap, extents_fn, digest, sleep)
    except BaseException:
        try:
            _finish(rep, jr, before, state['written'], digest, strict=False)
        except Exception:  # never hide the original error
            pass
        raise
    return _finish(rep, jr, before, state['written'], digest)


def _run_tasks(vol, todo, rep, jr, before, state, chunk, write_cap, extents_fn, digest_fn, sleep):
    cb = vol.cluster_bytes
    for i, t, d in todo:
        p = t['path']
        if p not in before:
            row = jr.first('file', 'path', p)
            before[p] = row['sha_before'] if row else digest_fn(p)
            if not row:
                jr.add(type='file', path=p, sha_before=before[p])
        if not jr.first('step', 'idx', i):  # keep the very first 'from': a resume must not overwrite it
            src = [list(x) for x in _pieces(extents_fn(p), t['vcn'], t['vcn'] + t['clusters'])]
            jr.add(type='step', idx=i, state='planned', path=p, vcn=t['vcn'], clusters=t['clusters'],
                   target_lcn=t['target_lcn'], **{'from': src})
        off, err = d, None
        while off < t['clusters'] and err is None:
            c = min(chunk, t['clusters'] - off)
            if state['written'] + c * cb > write_cap:
                rep['stopped'] = 'write_cap'
                return
            err = _move_chunk(vol, t, off, c, sleep)
            if err is None:
                off += c
                state['written'] += c * cb
        ok = err is None and done_prefix(extents_fn(p), t['vcn'], t['clusters'], t['target_lcn']) == t['clusters']
        jr.add(type='step', idx=i, state='done' if ok else 'failed', reason=None if ok else (err or 'extent_mismatch'))
        if not ok:
            rep['failed'].append({'idx': i, 'path': p, 'reason': err or 'extent_mismatch'})


def _finish(rep, jr, before, written, digest_fn, strict=True):
    """Compare SHA samples of every touched file; a mismatch is reported loudly and never swallowed."""
    bad = [p for p, d in before.items() if digest_fn(p) != d]
    rep.update(moved_bytes=written, sha_checked=len(before), sha_mismatch=bad)
    jr.add(type='end', stopped=rep['stopped'], written_bytes=written, sha_mismatch=bad)
    jr.close()
    if bad and strict:
        raise RuntimeError('sha_mismatch: %s' % bad)
    return rep


def revert_journal(journal_path, vol, roots, execute=False, chunk_bytes=4 << 20, write_cap=2 << 30,
                   extents_fn=file_extents, sleep=time.sleep):
    """Move each journaled step back to its recorded original extents when that space is free (best effort, not
    guaranteed: freed clusters may have been reused). Steps are undone in reverse order."""
    jr = Journal(journal_path)
    letter = vol.letter
    check_roots(roots, letter)
    steps = {}
    for r in jr.rows:
        if r['type'] == 'step' and 'from' in r:
            steps[r['idx']] = r
    chunk, written, rep = max(1, chunk_bytes // vol.cluster_bytes), 0, {'executed': execute, 'restored': 0,
                                                                         'skipped': [], 'moved_bytes': 0}
    for r in steps.values():
        if _drive(r['path']) != letter.upper() or not inside(r['path'], roots):
            raise PlanRejected('outside_allowed_root: %s' % r['path'])
    for idx in sorted(steps, reverse=True):
        r = steps[idx]
        cur = extents_fn(r['path']) or []
        for v, lcn, n in r['from']:
            if done_prefix(cur, v, n, lcn) == n:
                continue
            if not execute:
                rep['restored'] += 1
                continue
            for off in range(0, n, chunk):
                c = min(chunk, n - off)
                if written + c * vol.cluster_bytes > write_cap:
                    rep['stopped'] = 'write_cap'
                    jr.close()
                    return rep
                err = _move_chunk(vol, {'path': r['path'], 'vcn': v, 'target_lcn': lcn}, off, c, sleep)
                if err:
                    rep['skipped'].append({'idx': idx, 'reason': err})
                    break
                written += c * vol.cluster_bytes
            else:
                rep['restored'] += 1
                jr.add(type='revert', idx=idx, vcn=v, state='done')
    rep['moved_bytes'] = written
    jr.close()
    return rep


def verify_layout(plan, extents_fn=file_extents):
    """Is the hot data, read in plan order, physically contiguous (as many runs as the plan promised)?"""
    ext, seq, missing = {}, [], 0
    for o in plan['order']:
        if o['path'] not in ext:
            ext[o['path']] = extents_fn(o['path'])
        if not ext[o['path']]:
            missing += 1
            continue
        seq += [(lcn, n) for _, lcn, n in _pieces(ext[o['path']], o['vcn'], o['vcn'] + o['clusters'])]
    runs, expected = count_runs(seq), plan['stats']['expected_runs']
    zone = plan['zone']
    start = zone['start_lcn']
    in_zone = None if start is None else sum(max(0, min(lcn + n, start + zone['clusters']) - max(lcn, start)) for lcn, n in seq)
    starts = [seq[i][0] for i in range(len(seq)) if i == 0 or seq[i - 1][0] + seq[i - 1][1] != seq[i][0]]
    return {'hot_clusters': sum(n for _, n in seq), 'ordered_runs': runs, 'expected_runs': expected,
            'current_runs_at_plan_time': plan['stats']['current_runs'], 'ascending': starts == sorted(starts),
            'in_zone_clusters': in_zone, 'missing_files': missing,
            'contiguous': missing == 0 and runs <= expected}
