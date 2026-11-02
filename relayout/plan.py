"""Pure planning: hot block selection, access-order consensus, hot-zone allocation. No I/O except plan JSON.

Design: docs/design_v1_relayout.md 3.1-3.3. A block is (path, block_index) with 64 KiB blocks.
"""
from __future__ import annotations

import bisect
import heapq
import json
import os
from collections import defaultdict
from pathlib import Path

from session_planner import _validated_events  # event validation shared with the prewarm planner

BLOCK = 65536
PLAN_VERSION = 1
REL_BASE = 1 << 60  # stand-in zone start when the real one is chosen at apply time: never collides with a real LCN


def _runs(sessions, block):
    """Per session: ordered unique block keys, first access order."""
    if not sessions or len(sessions) > 32:
        raise ValueError('expect 1..32 sessions')
    out = []
    for s in sessions:
        seen = {}
        for path, first, last, _ in _validated_events(s, block):
            for b in range(first, last + 1):
                seen.setdefault((path, b), len(seen))
        out.append(list(seen))
    return out


def _lsum(values):
    """Naive left-to-right float sum, identical on every Python version (3.12+ builtin sum() compensates)
    and in the C# port. Do not replace with sum() or math.fsum()."""
    acc = 0.0
    for v in values:
        acc += v
    return acc


def _weights(k, decay):
    return [decay ** (k - 1 - i) for i in range(k)]


def _wmedian(pairs):
    """Weighted median of [(value, weight)]; lower median on ties."""
    pairs = sorted(pairs)
    half = _lsum(w for _, w in pairs) / 2.0
    acc = 0.0
    for v, w in pairs:
        acc += w
        if acc >= half:
            return v
    return pairs[-1][0]


def _positions(runs):
    """Per run: key -> normalized first-access position in [0, 1)."""
    return [{key: i / len(r) for i, key in enumerate(r)} for r in runs]


def hot_set(sessions, budget_bytes, block=BLOCK, decay=0.85, theta_core=0.5, theta_tail=0.2, contiguous=frozenset(),
             min_sessions=1):
    """Pick hot blocks. score = weighted frequency x (0.5 + 0.5 x order stability) x (0.2 if already contiguous).
    Returns (blocks, stats): blocks in selection order (core by score, then tail), stats[key] = (f, m, s, score).
    With K=1 every block has f=1, s=1: ties break by earliest consensus position.
    min_sessions: blocks seen in fewer runs than this are dropped (1 = keep everything)."""
    if type(budget_bytes) is not int or budget_bytes < 0 or block < 4096 or block & (block - 1):
        raise ValueError('invalid budget/block')
    runs = _runs(sessions, block)
    pos = _positions(runs)
    w = _weights(len(runs), decay)
    total_w = _lsum(w)
    seen_in = defaultdict(int)
    for r in runs:
        for key in r:
            seen_in[key] += 1
    keys = {key for key, n in seen_in.items() if n >= min_sessions}
    stats = {}
    for key in keys:
        obs = [(p[key], wk) for p, wk in zip(pos, w) if key in p]
        f = _lsum(wk for _, wk in obs) / total_w
        m = _wmedian(obs)
        mad = _wmedian([(abs(v - m), wk) for v, wk in obs])
        s = 1.0 - min(1.0, 2.0 * mad)
        score = f * (0.5 + 0.5 * s) * (0.2 if key in contiguous else 1.0)
        stats[key] = (f, m, s, score)

    def rank(key):
        return (-stats[key][3], stats[key][1], key)

    core = sorted((k for k in keys if stats[k][0] >= theta_core), key=rank)
    tail = sorted((k for k in keys if theta_tail <= stats[k][0] < theta_core), key=rank)
    return (core + tail)[:budget_bytes // block], stats


def _make_units(hot, runs, w, theta_core):
    """Merge (path,b),(path,b+1) into one unit when b+1 directly follows b in >= theta_core of b's weight."""
    adj, pres = defaultdict(float), defaultdict(float)
    hotset = set(hot)
    for r, wk in zip(runs, w):
        for i, key in enumerate(r):
            if key in hotset:
                pres[key] += wk
                if i + 1 < len(r) and r[i + 1] == (key[0], key[1] + 1) and r[i + 1] in hotset:
                    adj[key] += wk
    units = []
    for key in sorted(hotset):
        prev = (key[0], key[1] - 1)
        if units and units[-1][0] == key[0] and units[-1][1] + units[-1][2] == key[1] \
                and adj[prev] / pres[prev] >= theta_core:
            units[-1][2] += 1
        else:
            units.append([key[0], key[1], 1])
    return [tuple(u) for u in units]


def consensus_order(sessions, hot_blocks, block=BLOCK, decay=0.85, theta_core=0.5, max_passes=64):
    """Single physical order for the hot blocks -> [(path, start_block, n_blocks)].
    Units (step 1), median-position sort (step 3), adjacent swaps when more weighted runs prefer the swap (step 4).
    Step 2 (split at >1 s idle gaps) is skipped: traces carry no timestamps. Swaps stop after max_passes
    because pairwise preference is not transitive."""
    runs = _runs(sessions, block)
    pos = _positions(runs)
    w = _weights(len(runs), decay)
    units = _make_units(hot_blocks, runs, w, theta_core)

    def first(u):
        return (u[0], u[1])

    def m(u):
        return _wmedian([(p[first(u)], wk) for p, wk in zip(pos, w) if first(u) in p])

    def pref(x, y):  # weight of runs where x is read before y
        return _lsum(wk for p, wk in zip(pos, w) if first(x) in p and first(y) in p and p[first(x)] < p[first(y)])

    units.sort(key=lambda u: (m(u), u))
    for _ in range(max_passes):
        swapped = False
        for i in range(len(units) - 1):
            x, y = units[i], units[i + 1]
            if pref(y, x) > pref(x, y):
                units[i], units[i + 1] = y, x
                swapped = True
        if not swapped:
            break
    return units


def _subtract(rng, claimed):
    """Parts of [a, b) not covered by the claimed ranges."""
    parts, cur = [], rng[0]
    for a, b in sorted(claimed):
        if b <= cur or a >= rng[1]:
            continue
        if a > cur:
            parts.append((cur, a))
        cur = max(cur, b)
    if cur < rng[1]:
        parts.append((cur, rng[1]))
    return parts


def _pieces(ext, a, b):
    """Extents intersected with VCN range [a, b) -> [(vcn, lcn, n)]."""
    out = []
    for v, lcn, n in ext:
        lo, hi = max(a, v), min(b, v + n)
        if lo < hi:
            out.append((lo, lcn + lo - v, hi - lo))
    return out


def count_runs(lcns):
    """Physically contiguous runs in a sequence of (lcn, n)."""
    runs = 0
    for i, (lcn, _) in enumerate(lcns):
        if i == 0 or lcns[i - 1][0] + lcns[i - 1][1] != lcn:
            runs += 1
    return runs


def _order_tasks(tasks, sources):
    """Tasks whose target overlaps another task's source wait for that task (Kahn, stable by zone order).
    sources[i] = [(lcn, n)] currently held by task i. Cycle -> ValueError."""
    iv = sorted((lcn, lcn + n, i) for i, src in enumerate(sources) for lcn, n in src)
    starts = [x[0] for x in iv]
    deps, blocked_by = defaultdict(set), defaultdict(set)
    for i, (_, _, n, tgt) in enumerate(tasks):
        j = max(0, bisect.bisect_right(starts, tgt) - 1)
        while j < len(iv) and iv[j][0] < tgt + n:
            if iv[j][1] > tgt:
                deps[i].add(iv[j][2])
                blocked_by[iv[j][2]].add(i)
            j += 1
    heap = [i for i in range(len(tasks)) if not deps[i]]
    heapq.heapify(heap)
    done = []
    while heap:
        i = heapq.heappop(heap)
        done.append(i)
        for k in blocked_by[i]:
            deps[k].discard(i)
            if not deps[k]:
                heapq.heappush(heap, k)
    if len(done) != len(tasks):
        raise ValueError('zone overlaps hot data in a cycle; choose another zone')
    return [tasks[i] for i in done]


def _hot_ranges(units, extents_by_file, cb, block, sizes):
    """Units -> access-ordered VCN ranges [(path, vcn, n)], deduplicated per file; also the skipped paths."""
    claimed, order, skipped = defaultdict(list), [], set()
    for path, sb, nb in units:
        ext = extents_by_file.get(path)
        if not ext:
            skipped.add(path)
            continue
        v1 = min(-(-(sb + nb) * block // cb), ext[-1][0] + ext[-1][2])
        if sizes and path in sizes:
            v1 = min(v1, -(-sizes[path] // cb))
        for a, b in _subtract((sb * block // cb, v1), claimed[path]):
            claimed[path].append((a, b))
            order.append((path, a, b - a))
    return order, skipped


def allocate_zone(units, extents_by_file, zone_start_lcn, zone_clusters, cluster_bytes, block=BLOCK,
                  sizes=None, preserve_bytes=8 << 20, align_clusters=4):
    """Place units, in access order, at consecutive zone clusters.
    extents_by_file: {path: [(vcn, lcn, n)]} (missing/None/[] -> file skipped). sizes: {path: bytes} clamps the file end
    (clusters past the size-rounded end cannot be moved). Physically contiguous runs of the access order that are
    >= preserve_bytes stay in place (one extra seek is cheaper than moving them). Pieces already at their target stay.
    zone_start_lcn None = zone not placed yet: targets are returned as offsets from the zone start, and no piece can
    count as 'already in place' or overlap the zone (apply picks a completely free run).
    Returns (tasks, stats, order): tasks = [(path, vcn, clusters, target_lcn)], order = [(path, vcn, clusters)]
    (all hot ranges in access order, including unmoved ones)."""
    cb = cluster_bytes
    rel = zone_start_lcn is None
    if rel:
        zone_start_lcn = REL_BASE
    order, skipped = _hot_ranges(units, extents_by_file, cb, block, sizes)
    flat = [(p, v, lcn, n) for p, a, n0 in order for v, lcn, n in _pieces(extents_by_file[p], a, a + n0)]
    groups = []  # physically contiguous runs of consecutive pieces
    for piece in flat:
        if groups and groups[-1][-1][2] + groups[-1][-1][3] == piece[2]:
            groups[-1].append(piece)
        else:
            groups.append([piece])
    tasks, sources, final, cur_off, full = [], [], [], 0, False
    st = dict(preserved_segments=0, preserved_bytes=0, in_place_bytes=0, overflow_bytes=0, misaligned_pieces=0)
    for g in groups:
        total = sum(p[3] for p in g)
        if total * cb >= preserve_bytes:
            st['preserved_segments'] += 1
            st['preserved_bytes'] += total * cb
            final += [(p[2], p[3]) for p in g]
            continue
        for path, vcn, lcn, n in g:
            if full or cur_off + n > zone_clusters:
                full = True  # keep the access-order prefix; later pieces stay put
                st['overflow_bytes'] += n * cb
                final.append((lcn, n))
                continue
            target = zone_start_lcn + cur_off
            cur_off += n
            final.append((target, n))
            if lcn == target:
                st['in_place_bytes'] += n * cb
                continue
            st['misaligned_pieces'] += (target - lcn) % align_clusters != 0
            last = tasks[-1] if tasks else None
            if last and last[0] == path and last[1] + last[2] == vcn and last[3] + last[2] == target:
                tasks[-1] = (path, last[1], last[2] + n, last[3])
                sources[-1].append((lcn, n))
            else:
                tasks.append((path, vcn, n, target))
                sources.append([(lcn, n)])
    if any(zone_start_lcn < lcn + n and lcn < zone_start_lcn + zone_clusters for s in sources for lcn, n in s):
        tasks = _order_tasks(tasks, sources)
    if rel:
        tasks = [(p, v, n, t - REL_BASE) for p, v, n, t in tasks]
    moved = sum(t[2] for t in tasks)
    stats = dict(st, hot_bytes=sum(p[3] for p in flat) * cb, moved_bytes=moved * cb, tasks=len(tasks),
                 avg_clusters_per_task=round(moved / len(tasks), 2) if tasks else 0,
                 zone_used_clusters=cur_off, skipped_files=len(skipped),
                 current_runs=count_runs([(p[2], p[3]) for p in flat]), expected_runs=count_runs(final))
    return tasks, stats, order


def fingerprint(path):
    """[size, mtime_ns]: detects a file rewritten after planning (MOVE_FILE changes neither)."""
    st = os.stat(path)
    return [st.st_size, st.st_mtime_ns]


def make_plan(sessions, origin_root, zone_bytes, cluster_bytes, extents_fn, size_fn=os.path.getsize,
              zone_start_lcn=None, block=BLOCK, min_sessions=1, **kw):
    """Full plan dict. zone_start_lcn None = chosen at apply time from the volume bitmap (targets are zone offsets)."""
    hot, _ = hot_set(sessions, zone_bytes, block, min_sessions=min_sessions)
    units = consensus_order(sessions, hot, block)
    paths = sorted({u[0] for u in units})
    ext = {p: extents_fn(p) for p in paths}
    sizes = {p: size_fn(p) for p in paths}
    zc = zone_bytes // cluster_bytes
    tasks, stats, order = allocate_zone(units, ext, zone_start_lcn, zc, cluster_bytes, block, sizes, **kw)
    stats.update(hot_blocks=len(hot), units=len(units))
    return {'version': PLAN_VERSION, 'origin_root': str(origin_root),
            'volume': os.path.splitdrive(str(origin_root))[0].upper(), 'cluster_bytes': cluster_bytes, 'block': block,
            'zone': {'start_lcn': zone_start_lcn, 'clusters': zc},
            'tasks': [dict(path=p, vcn=v, clusters=n, target_lcn=t) for p, v, n, t in tasks],
            'order': [dict(path=p, vcn=v, clusters=n) for p, v, n in order],
            'fingerprints': {p: fingerprint(p) for p in sorted({o[0] for o in order})}, 'stats': stats}


def prefetch_trace(plan, sessions):
    """Prefetch input for exactly the planned hot ranges, in zone (= consensus access) order: the plan's `order`
    as [{'path','offset','length','cost'}] (same shape as a trace; prefetch clamps the file end). Bytes = stats.hot_bytes."""
    block, cb = plan['block'], plan['cluster_bytes']
    cost = {}
    for s in sessions:
        for path, first, last, c in _validated_events(s, block):
            for b in range(first, last + 1):
                cost[(path, b)] = max(cost.get((path, b), 0.0), c)
    rows = []
    for o in plan['order']:
        off, n = o['vcn'] * cb, o['clusters'] * cb
        c = max(cost.get((o['path'], b), 0.0) for b in range(off // block, (off + n - 1) // block + 1))
        rows.append(dict(path=o['path'], offset=off, length=n, cost=c))
    return rows


def resolve_plan(plan, base):
    """Plan with an absolute zone: start_lcn None -> base, target offsets shifted by base."""
    if plan['zone']['start_lcn'] is not None:
        return plan
    return dict(plan, zone=dict(plan['zone'], start_lcn=base),
                tasks=[dict(t, target_lcn=t['target_lcn'] + base) for t in plan['tasks']])


def _uint(x):
    return type(x) is int and x >= 0


def validate_plan(plan):
    """Plan files are untrusted input: check shape and types, raise ValueError."""
    try:
        start = plan['zone']['start_lcn']
        ok = (plan['version'] == PLAN_VERSION and _uint(plan['cluster_bytes']) and plan['cluster_bytes'] > 0
              and _uint(plan['zone']['clusters']) and (start is None or _uint(start))
              and isinstance(plan['origin_root'], str) and isinstance(plan['stats'], dict)
              and isinstance(plan['volume'], str) and isinstance(plan['fingerprints'], dict)
              and all(isinstance(t['path'], str) and _uint(t['vcn']) and _uint(t['clusters']) and t['clusters'] > 0
                      and _uint(t['target_lcn']) for t in plan['tasks'])
              and all(isinstance(o['path'], str) and _uint(o['vcn']) and _uint(o['clusters']) for o in plan['order']))
    except (KeyError, TypeError):
        ok = False
    if not ok:
        raise ValueError('malformed plan')
    return plan


def save_plan(plan, path):
    Path(path).write_text(json.dumps(plan, indent=1), encoding='utf-8')


def load_plan(path):
    return validate_plan(json.loads(Path(path).read_text(encoding='utf-8-sig')))
