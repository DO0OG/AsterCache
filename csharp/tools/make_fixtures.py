"""Cross-check fixtures for the C# port: ground truth comes from the Python reference implementation in this repository.

  python -B csharp/tools/make_fixtures.py             write fixtures (takes the D:\\AsterBench snapshot first if there is none)
  python -B csharp/tools/make_fixtures.py --snapshot  take a new snapshot of D:\\AsterBench (file metadata only, no data reads), then write
  python -B csharp/tools/make_fixtures.py --check     regenerate in memory and compare with the files on disk (exit 1 on any difference)

Standard library only. The generator is deterministic: every fixture is derived from csharp/tests/fixtures/e3/*
and fresh/snapshot.json (the only file that depends on the live machine), so --check never touches D:.
Float sums follow Python 3.11 (naive left-to-right sum); Python 3.12+ sums floats with compensation and may differ.
"""
import argparse
import contextlib
import json
import os
import random
import struct
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
FIX = REPO / 'csharp' / 'tests' / 'fixtures'
sys.path.insert(0, str(REPO))

from prefetch import scheduler as sched  # noqa: E402
from prefetch.cli import load_ranges  # noqa: E402
from prefetch.layout import cluster_size, file_extents  # noqa: E402
from relayout import apply as rapply  # noqa: E402
from relayout import plan as rplan  # noqa: E402

ROOT = 'D:\\AsterBench'
BL = 65536
CB = 4096
MIB = 1 << 20
ZONE_E3 = 95135032
NOTES = []


def note(msg):
    NOTES.append(msg)
    print(msg)


def dump(obj):
    return (json.dumps(obj, separators=(',', ':')) + '\n').encode('ascii')


def bits(x):
    return struct.pack('>d', x).hex()


def text_mode(text):
    """What Path.write_text does on Windows: LF -> CRLF."""
    return text.replace('\r\n', '\n').replace('\n', '\r\n').encode('utf-8')


def plan_bytes(plan):
    with tempfile.TemporaryDirectory() as d:
        p = Path(d) / 'plan.json'
        rplan.save_plan(plan, p)
        got = p.read_bytes()
    want = text_mode(json.dumps(plan, indent=1))
    if os.linesep != '\r\n':  # keep the bytes identical to a Windows run
        got = got.replace(b'\n', b'\r\n')
    assert got == want
    return got


def prefetch_bytes(rows):
    return text_mode(json.dumps(rows, ensure_ascii=False) + '\n')


@contextlib.contextmanager
def patched_fingerprint(fn):
    old = rplan.fingerprint
    rplan.fingerprint = fn
    try:
        yield
    finally:
        rplan.fingerprint = old


@contextlib.contextmanager
def counting_order_tasks(counter):
    old = rplan._order_tasks

    def wrapper(tasks, sources):
        try:
            out = old(tasks, sources)
        except ValueError:
            counter['cycle'] += 1
            raise
        counter['ordered'] += 1
        return out

    rplan._order_tasks = wrapper
    try:
        yield
    finally:
        rplan._order_tasks = old


# ---------------------------------------------------------------- snapshot of the live machine (metadata only)

def take_snapshot():
    sessions = [load_ranges(FIX / 'e3' / 'trace.json', ROOT)]
    cb = cluster_size(ROOT)
    files = {}
    for p in sorted({r['path'] for r in sessions[0]}):
        st = os.stat(p)
        ext = file_extents(p)
        assert ext is not None, p
        files[p] = {'size': st.st_size, 'mtime_ns': st.st_mtime_ns, 'extents': [list(e) for e in ext]}
    return {'origin_root': ROOT, 'cluster_bytes': cb, 'files': files, 'sessions': sessions}


# ---------------------------------------------------------------- F3 / F4: real trace, real layout

def e3_pre_extents(snap, journal_rows, plan):
    """Layout before the relocation: current extents with every journaled task range put back to its 'from' pieces."""
    ext = {p: [tuple(e) for e in snap['files'][p]['extents']] for p in plan['fingerprints']}
    for r in journal_rows:
        if r['type'] == 'step' and r['state'] == 'planned':
            p, a, b = r['path'], r['vcn'], r['vcn'] + r['clusters']
            new = []
            for v, lcn, n in ext[p]:
                if v + n <= a or v >= b:
                    new.append((v, lcn, n))
                    continue
                if v < a:
                    new.append((v, lcn, a - v))
                if v + n > b:
                    new.append((b, lcn + b - v, v + n - b))
            ext[p] = sorted(new + [tuple(x) for x in r['from']])
    for p, lst in ext.items():
        out = []
        for v, lcn, n in lst:
            if out and out[-1][0] + out[-1][2] == v and out[-1][1] + out[-1][2] == lcn:
                out[-1] = (out[-1][0], out[-1][1], out[-1][2] + n)
            else:
                out.append((v, lcn, n))
        ext[p] = out
    return ext


def gen_e3(snap, out):
    plan_path = FIX / 'e3' / 'plan.json'
    want = plan_path.read_bytes()
    plan = json.loads(want.decode('utf-8'))
    rows = [json.loads(x) for x in (FIX / 'e3' / 'journal.jsonl').read_text(encoding='utf-8').splitlines() if x.strip()]
    sessions = snap['sessions']
    ext = e3_pre_extents(snap, rows, plan)
    sizes = {p: fp[0] for p, fp in plan['fingerprints'].items()}
    with patched_fingerprint(lambda p: list(plan['fingerprints'][p])):
        mine = rplan.make_plan(sessions, ROOT, 300 * MIB, CB, lambda p: ext[p], lambda p: sizes[p],
                               zone_start_lcn=None, preserve_bytes=8 * MIB, min_sessions=1)
    mine = rplan.resolve_plan(mine, ZONE_E3)
    ok = plan_bytes(mine) == want
    note('F3 e3 plan reproduced by Python from journal + trace: %s' % ('YES' if ok else 'NO (use F4)'))
    if ok:
        out['fresh/e3_pre_extents.json'] = dump({p: [list(e) for e in v] for p, v in sorted(ext.items())})
    return ok


def gen_fresh(snap, out):
    files, cb = snap['files'], snap['cluster_bytes']
    sessions = snap['sessions']
    variants = {'plan': dict(zone_start_lcn=None, preserve_bytes=8 * MIB),
                'plan_nopreserve': dict(zone_start_lcn=None, preserve_bytes=1 << 50),
                'plan_inplace': dict(zone_start_lcn=ZONE_E3, preserve_bytes=1 << 50),
                'plan_zone_preserve': dict(zone_start_lcn=ZONE_E3, preserve_bytes=8 * MIB)}
    shas = {}
    with patched_fingerprint(lambda p: [files[p]['size'], files[p]['mtime_ns']]):
        for name, kw in variants.items():
            plan = rplan.make_plan(sessions, ROOT, 300 * MIB, cb, lambda p: [tuple(e) for e in files[p]['extents']],
                                   lambda p: files[p]['size'], min_sessions=1, **kw)
            out['fresh/%s.json' % name] = plan_bytes(plan)
            shas[name] = rapply.plan_sha(plan)
            s = plan['stats']
            note('F4 %-18s tasks=%d preserved_seg=%d in_place=%d moved=%d' % (
                name, s['tasks'], s['preserved_segments'], s['in_place_bytes'], s['moved_bytes']))
            if name == 'plan':
                out['fresh/plan.prefetch.json'] = prefetch_bytes(rplan.prefetch_trace(plan, sessions))
    out['fresh/shas.json'] = dump(shas)


# ---------------------------------------------------------------- F7 schedule

def gen_schedule(snap, out):
    files, cb = snap['files'], snap['cluster_bytes']
    ranges = snap['sessions'][0]
    names = sorted(files)
    index = {p: i for i, p in enumerate(names)}
    layout = lambda p: ([tuple(e) for e in files[p]['extents']], cb) if p in files else None  # noqa: E731
    size = lambda p: files[p]['size']  # noqa: E731
    be = sched.breakeven_gap()
    plan = [(s, g, 0.3, 1024) for s in ('physical', 'path', 'none') for g in (0, be)]
    plan += [('physical', 0, 2, 1024), ('physical', be, 2, 1024), ('physical', 0, 0.05, 1024), ('path', be, 0.02, 1024), ('physical', 0, 0.3, 64), ('path', be, 0.3, 64)]
    cases = []
    for strat, gap, ratio, budget_mib in plan:
        ops, info = sched.schedule(ranges, strat, budget_mib * MIB, gap, layout, size, ratio)
        cases.append({'strategy': strat, 'gap_bytes': gap, 'full_file_ratio': ratio, 'budget_bytes': budget_mib * MIB,
                      'info': info, 'ops': [[index[p], o, n] for p, o, n in ops]})
        note('F7 %-8s gap=%-8d ratio=%-3s budget=%4d MiB ops=%d full=%d' % (strat, gap, ratio, budget_mib, len(ops), info['full_files']))
    out['fresh/schedule.json'] = dump({'files': names, 'cases': cases})


# ---------------------------------------------------------------- F5 synthetic planning cases

PATHS = ['D:\\Data\\a.bin', 'D:\\Data\\b\u00e9.bin', 'D:\\Data\\c.bin', 'D:\\Data\\d\uffff.bin',
         'D:\\Data\\d\U0001F600.bin', 'D:\\Data\\sub\\e "q".bin']
SPARSE, EMPTY, MISSING = 'D:\\Data\\sparse.bin', 'D:\\Data\\empty.bin', 'D:\\Data\\gone.bin'


def gen_sessions(rng, k, nblocks):
    paths = list(nblocks)
    base = []
    for _ in range(rng.randrange(70, 130)):
        p = rng.choice(paths)
        base.append((p, rng.randrange(nblocks[p]), rng.choice([1, 1, 1, 2, 3, 5])))
    sessions = []
    for _ in range(k):
        seq = list(base)
        for i in range(len(seq) - 1):
            if rng.random() < 0.15:
                seq[i], seq[i + 1] = seq[i + 1], seq[i]
        seq = [e for e in seq if rng.random() > 0.12]
        for _ in range(rng.randrange(0, 15)):
            p = rng.choice(paths)
            seq.insert(rng.randrange(len(seq) + 1), (p, rng.randrange(nblocks[p]), 1))
        sessions.append([event(rng, p, b, n) for p, b, n in seq])
    return sessions


def event(rng, p, b, n):
    off = b * BL + rng.choice([0, 0, 0, CB, 30000])
    length = rng.choice([CB, BL, n * BL, n * BL + CB])
    return {'path': p, 'offset': off, 'length': length, 'cost': rng.choice([8.0, 8.0, 1.5, 0.25, 12.0, 3, 0])}


def gen_extents(rng, clusters):
    """Fragmented, interleaved layout; physically adjacent fragments are merged like FSCTL reports them."""
    frags = []
    for p, total in clusters.items():
        v = 0
        while v < total:
            n = min(total - v, rng.choice([8, 16, 16, 32, 40, 64, 100, 200]))
            frags.append((p, v, n))
            v += n
    rng.shuffle(frags)
    lcn = rng.randrange(1000, 5000)
    ext = {p: [] for p in clusters}
    for p, v, n in frags:
        ext[p].append((v, lcn, n))
        lcn += n + rng.choice([0, 0, 0, 16, 100, 4096])
    for p, lst in ext.items():
        lst.sort()
        out = []
        for v, l, n in lst:
            if out and out[-1][0] + out[-1][2] == v and out[-1][1] + out[-1][2] == l:
                out[-1] = (out[-1][0], out[-1][1], out[-1][2] + n)
            else:
                out.append((v, l, n))
        ext[p] = out
    return ext


def run_alloc(units, ext, sizes, zone_start, zone_clusters, preserve, align=4):
    try:
        tasks, st, order = rplan.allocate_zone(units, ext, zone_start, zone_clusters, CB, BL, sizes,
                                              preserve_bytes=preserve, align_clusters=align)
    except ValueError:
        return {'error': 'ValueError'}
    return {'tasks': [list(t) for t in tasks], 'stats_json': json.dumps(st), 'order': [list(o) for o in order]}


def hot_variant(sessions, budget, min_sessions, contiguous):
    hot, stats = rplan.hot_set(sessions, budget, BL, contiguous=frozenset(contiguous), min_sessions=min_sessions)
    return {'contiguous': [list(c) for c in sorted(contiguous)], 'budget_bytes': budget, 'min_sessions': min_sessions,
            'hot': [list(h) for h in hot],
            'stats': [[p, b, bits(f), bits(m), bits(s), bits(sc)] for (p, b), (f, m, s, sc) in sorted(stats.items())]}


def make_base(seed, k, origin):
    rng = random.Random(seed)
    nblocks = {p: rng.randrange(10, 50) for p in PATHS}
    nblocks[SPARSE] = nblocks[EMPTY] = nblocks[MISSING] = 12
    sessions = gen_sessions(rng, k, nblocks)
    clusters = {p: nblocks[p] * (BL // CB) + rng.randrange(0, 40) for p in PATHS}
    ext = gen_extents(rng, clusters)
    ext[SPARSE], ext[EMPTY] = None, []  # sparse hole / data resident in the MFT; MISSING has no entry at all
    sizes = {p: clusters[p] * CB - rng.randrange(0, CB) - rng.choice([0, 0, 0, 2 * BL]) for p in PATHS}
    fps = {p: [sizes[p], 1790000000000000000 + 100 * i] for i, p in enumerate(sorted(PATHS))}
    distinct = {(e['path'], b) for s in sessions for e in s for b in range(e['offset'] // BL, (e['offset'] + e['length'] - 1) // BL + 1)}
    budget = (len(distinct) * 7 // 10) * BL
    min_sessions = 2 if k >= 2 else 1
    hot, _ = rplan.hot_set(sessions, budget, BL, min_sessions=min_sessions)
    units = rplan.consensus_order(sessions, hot, BL)
    variants = [hot_variant(sessions, budget, min_sessions, ())]
    variants.append(hot_variant(sessions, budget, 1, (hot[0], hot[len(hot) // 2], hot[-1])))
    variants.append(hot_variant(sessions, 5 * BL, 1, ()))
    max_lcn = max(l + n for e in ext.values() if e for _, l, n in e)
    min_lcn = min(l for e in ext.values() if e for _, l, _ in e)
    hot_clusters = sum(n for _, _, n in units) * (BL // CB)
    first_lcn = 0
    for p, sb, _ in units:
        pcs = rplan._pieces(ext[p], sb * BL // CB, sb * BL // CB + 1) if ext.get(p) else []
        if pcs:
            first_lcn = pcs[0][1]
            break
    scenarios = [
        ('rel', None, 200000, 8 * MIB), ('rel_small_preserve', None, 200000, 1 << 18),
        ('far', max_lcn + 20000, 200000, 8 * MIB), ('overflow', max_lcn + 20000, hot_clusters * 2 // 5, 8 * MIB),
        ('rel_overflow', None, hot_clusters * 3 // 5, 1 << 18),
        ('overlap_a', min_lcn + 300, 200000, 8 * MIB), ('overlap_b', (min_lcn + max_lcn) // 2, hot_clusters, 8 * MIB),
        ('overlap_small', min_lcn + 1000, hot_clusters // 2, 1 << 18), ('in_place', first_lcn, 200000, 8 * MIB),
        ('zero_zone', max_lcn + 20000, 0, 8 * MIB)]
    allocs = []
    for name, start, zc, pres in scenarios:
        allocs.append({'name': name, 'zone_start': start, 'zone_clusters': zc, 'preserve_bytes': pres,
                       'expected': run_alloc(units, ext, sizes, start, zc, pres)})
    plans = []
    for name, start, pres in (('rel', None, 8 * MIB), ('far', max_lcn + 20000, 1 << 18)):
        zone_bytes = hot_clusters * CB * 3 // 2
        with patched_fingerprint(lambda p: fps[p]):
            plan = rplan.make_plan(sessions, origin, zone_bytes, CB, ext.get, lambda p: sizes.get(p, 0),
                                   zone_start_lcn=start, min_sessions=min_sessions, preserve_bytes=pres)
        plans.append({'name': name, 'origin_root': origin, 'zone_bytes': zone_bytes, 'zone_start': start,
                      'preserve_bytes': pres, 'min_sessions': min_sessions,
                      'plan_text': plan_bytes(plan).decode('utf-8'), 'sha': rapply.plan_sha(plan)})
    return {'name': 'k%d' % k, 'sessions': sessions, 'budget_bytes': budget, 'min_sessions': min_sessions,
            'hot_variants': variants, 'units': [list(u) for u in units],
            'extents': {p: ([list(e) for e in v] if v is not None else None) for p, v in ext.items()}, 'sizes': sizes,
            'fingerprints': fps, 'allocs': allocs, 'plans': plans}


def handmade():
    """Cases from the unit tests plus extra dependency chains; hand-checked shapes the random cases rarely hit."""
    P = BL // CB

    def case(name, units, ext, start=5000, zc=1000, sizes=None, preserve=8 * MIB, align=4):
        e = {p: ([list(x) for x in v] if v is not None else None) for p, v in ext.items()}
        return {'name': name, 'units': [list(u) for u in units], 'extents': e, 'sizes': sizes, 'zone_start': start,
                'zone_clusters': zc, 'preserve_bytes': preserve, 'align_clusters': align,
                'expected': run_alloc(units, ext, sizes, start, zc, preserve, align)}
    big = 3 * 256
    return [
        case('adjacent_merge', [('A', 0, 1), ('A', 1, 1), ('B', 0, 1)], {'A': [(0, 100, 4 * P)], 'B': [(0, 900, P)]}),
        case('fragmented_source', [('A', 0, 2)], {'A': [(0, 100, P), (P, 700, P)]}),
        case('in_place', [('A', 0, 1), ('B', 0, 1)], {'A': [(0, 5000, P)], 'B': [(0, 900, P)]}),
        case('preserved', [('A', 0, 48), ('B', 0, 48), ('C', 0, 1)],
             {'A': [(0, 100, big)], 'B': [(0, 100 + big, big)], 'C': [(0, 9000, P)]}, preserve=4 * MIB),
        case('overflow_prefix', [('A', 0, 1), ('A', 2, 1), ('A', 1, 1)], {'A': [(0, 100, 3 * P)]}, zc=2 * P),
        case('size_clamp', [('A', 0, 2)], {'A': [(0, 100, 20)]}, sizes={'A': 18 * CB + 1}),
        case('duplicates_missing', [('A', 0, 1), ('A', 0, 1), ('S', 0, 1), ('Z', 0, 1)], {'A': [(0, 100, P)], 'S': None}),
        case('dependency', [('B', 0, 1), ('A', 0, 1)], {'B': [(0, 900, P)], 'A': [(0, 5000, P)]}),
        case('dependency_chain', [('A', 0, 1), ('B', 0, 1), ('C', 0, 1), ('D', 0, 1)],
             {'A': [(0, 900, P)], 'B': [(0, 5000, P)], 'C': [(0, 5000 + P, P)], 'D': [(0, 5000 + 2 * P, P)]}),
        case('dependency_two_sources', [('A', 0, 2), ('C', 0, 1), ('B', 0, 1)],
             {'A': [(0, 5000 + 3 * P, P), (P, 9100, P)], 'B': [(0, 9000, P)], 'C': [(0, 5000, P)]}, zc=4 * P),
        case('cycle', [('X', 0, 1), ('A', 0, 1)], {'X': [(0, 5000 + P, P)], 'A': [(0, 5000, P)]}),
        case('self_overlap_cycle', [('A', 0, 1)], {'A': [(0, 5004, P)]}),
        case('misaligned', [('A', 0, 1)], {'A': [(0, 101, P)]}),
        case('align_8', [('A', 0, 1), ('B', 0, 1)], {'A': [(0, 5000 - 8 * P, P)], 'B': [(0, 77, P)]}, align=8),
        case('no_units', [], {}),
        case('sparse_only', [('S', 0, 3)], {'S': None}),
        case('rel_zone', [('A', 3, 2), ('A', 0, 1)], {'A': [(0, 100, 5 * P)]}, start=None),
    ]


# ---------------------------------------------------------------- F6 floats

def gen_floats(out):
    rng = random.Random(6)
    vals = [0.0, -0.0, 1.0, -1.0, 57.0, 56.73, 0.1, 0.2, 0.1 + 0.2, 1e-5, 1e-4, 9.999999999999999e-05, 0.0001, 1e15,
            1e16, 9999999999999998.0, 999999999999999.9, 1e22, 1e23, 5e-324, 2.2250738585072014e-308,
            1.7976931348623157e308, 123456789.123456789, 123456789012345678.0, 1e-7, -1.5e-10, 2.675, 1.005, 0.125,
            0.375, 0.005, 0.015, 0.025, 0.045, 0.5, 1.5, 2.5, 100.0, 0.6666666666666666, 1.0000000000000002, 4.35, 8.345,
            1048576.0, 4503599627370496.5, 4503599627370495.5, 56.725, 56.735, 0.994999999999999, 0.995, 0.9949999999999999]
    while len(vals) < 2000:
        kind = rng.randrange(6)
        if kind == 0:
            x = rng.random() * 10 ** rng.randint(-8, 20)
        elif kind == 1:
            x = float(rng.randrange(-10 ** 9, 10 ** 9))
        elif kind == 2:
            x = rng.randrange(0, 10 ** 7) / 1000.0 + rng.choice([0.0005, 0.005, 0.0049999, 0.00500001])
        elif kind == 3:
            x = rng.randrange(0, 10 ** 6) / 100.0
        elif kind == 4:
            x = rng.randrange(1, 1000) / rng.randrange(1, 1000)
        else:
            x = struct.unpack('>d', rng.getrandbits(64).to_bytes(8, 'big'))[0]
            if x != x or x in (float('inf'), float('-inf')):
                continue
        vals.append(-x if rng.random() < 0.2 else x)
    rows = [[bits(x), repr(x), repr(round(x, 2)), bits(round(x, 2))] for x in vals]
    out['floats/repr.json'] = dump({'rows': rows, 'pow': {'decay': 0.85, 'bits': [bits(0.85 ** n) for n in range(32)]}})


# ---------------------------------------------------------------- F8 journal rows

def gen_journal(out):
    rows = [
        {'type': 'header', 'plan_sha': '7d64ea23fe801a163474da54bbb63ce1c5523dc107c4cccec5806319eff74424',
         'zone_start_lcn': 95135032, 'cluster_bytes': 4096, 'started': 1793541396.8990724},
        {'type': 'header', 'plan_sha': 'ab', 'zone_start_lcn': None, 'cluster_bytes': 4096, 'started': 100.0},
        {'type': 'file', 'path': 'D:\\AsterBench\\data\\f008.bin', 'sha_before': 'cb39'},
        {'type': 'file', 'path': 'D:\\Data\\d\U0001F600 \u00e9 \uffff "q" \t\x01\x7f.bin', 'sha_before': None},
        {'type': 'step', 'idx': 0, 'state': 'planned', 'path': 'D:\\x.bin', 'vcn': 6176, 'clusters': 16,
         'target_lcn': 95135032, 'from': [[6176, 246733158, 16], [6192, 1, 2]]},
        {'type': 'step', 'idx': 1, 'state': 'planned', 'path': 'D:\\x.bin', 'vcn': 0, 'clusters': 1, 'target_lcn': 0, 'from': []},
        {'type': 'step', 'idx': 0, 'state': 'done', 'reason': None},
        {'type': 'step', 'idx': 2, 'state': 'failed', 'reason': 'OSError: [WinError 5] \u00e9 "denied"'},
        {'type': 'end', 'stopped': None, 'written_bytes': 243531776, 'sha_mismatch': []},
        {'type': 'end', 'stopped': 'write_cap', 'written_bytes': 0, 'sha_mismatch': ['D:\\a.bin', 'D:\\\u30c6.bin']},
        {'type': 'revert', 'idx': 3, 'vcn': 12, 'state': 'reverted'},
        {'type': 'misc', 'floats': [0.30000000000000004, 1e22, 1e-07, 1e16, 1e15, -0.0, 5e-324, 123456789.0, 1e+300],
         'bools': [True, False, None], 'nested': {'z': 1, 'a': {'k': []}, 'm': {}}, 'big': 9007199254740993, 'neg': -42},
    ]
    out['synthetic/journal_rows.json'] = dump([{'row': r, 'line': json.dumps(r)} for r in rows])
    raw = (FIX / 'e3' / 'journal.jsonl').read_bytes().split(b'\r\n')
    lines = [x for x in raw if x]
    assert all(json.dumps(json.loads(x.decode('utf-8'))) == x.decode('utf-8') for x in lines)
    cut = b'\r\n'.join(lines[:50]) + b'\r\n' + lines[50][:len(lines[50]) // 2]
    out['synthetic/journal_truncated.jsonl'] = cut


def gen_synthetic(out):
    counter = {'ordered': 0, 'cycle': 0}
    with counting_order_tasks(counter):
        bases = [make_base(11, 1, 'D:\\Data'), make_base(23, 3, 'd:/Data/'), make_base(37, 5, '\\\\srv\\share\\Data')]
        hand = handmade()
    out['synthetic/cases.json'] = dump({'block': BL, 'cluster_bytes': CB, 'bases': bases, 'handmade': hand})
    errors = sum(1 for b in bases for a in b['allocs'] if 'error' in a['expected']) + sum(1 for h in hand if 'error' in h['expected'])
    note('F5 cases: bases=%d allocs=%d handmade=%d (ValueError cases %d); _order_tasks ok=%d cycle=%d' % (
        len(bases), sum(len(b['allocs']) for b in bases), len(hand), errors, counter['ordered'], counter['cycle']))
    assert counter['ordered'] >= 3 and counter['cycle'] >= 2, counter


# ---------------------------------------------------------------- driver

def build(snap):
    out = {'fresh/snapshot.json': dump(snap)}
    gen_e3(snap, out)
    gen_fresh(snap, out)
    gen_schedule(snap, out)
    gen_synthetic(out)
    gen_floats(out)
    gen_journal(out)
    return out


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--check', action='store_true', help='compare regenerated fixtures with the files on disk')
    ap.add_argument('--snapshot', action='store_true', help='take a new D:\\AsterBench snapshot first')
    a = ap.parse_args(argv)
    snap_path = FIX / 'fresh' / 'snapshot.json'
    if a.snapshot or (not snap_path.exists() and not a.check):
        snap = take_snapshot()
    elif snap_path.exists():
        snap = json.loads(snap_path.read_text(encoding='ascii'))
    else:
        print('fresh/snapshot.json is missing: run without --check first')
        return 1
    out = build(snap)
    if a.check:
        bad = [r for r, b in out.items() if not (FIX / r).exists() or (FIX / r).read_bytes() != b]
        for r in bad:
            print('DIFFERENT: %s' % r)
        print('check: %d fixtures, %d different' % (len(out), len(bad)))
        return 1 if bad else 0
    for r, b in out.items():
        p = FIX / r
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_bytes(b)
    print('wrote %d fixtures under %s' % (len(out), FIX))
    return 0


if __name__ == '__main__':
    sys.exit(main())
