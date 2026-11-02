"""relayout: pure planning (hot set, consensus order, zone allocation), bitmap, journal/resume, safety gates.
Volume access is faked; nothing here needs Administrator or touches a real volume layout."""
import contextlib
import ctypes
import hashlib
import io
import json
import math
import os
import random
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from relayout import apply as rapply
from relayout import cli, mover, plan as rplan

BLK = 65536
CB = 4096
PER = BLK // CB  # clusters per block
HOT_SET_HASH = 'eaa13a3fa904ba257465c1af48ae2769ba5cd0e07d39e723e0d26c815af7b6f3'


def ev(path, block, n=1):
    return {'path': path, 'offset': block * BLK, 'length': n * BLK, 'cost': 8.0}


def run(*keys):
    return [ev(p, b) for p, b in keys]


class HotSetTests(unittest.TestCase):
    def setUp(self):
        self.s = [run(('f', 0), ('f', 1), ('f', 2), ('f', 9)), run(('f', 0), ('f', 1), ('f', 2)),
                  run(('f', 0), ('f', 2), ('f', 1), ('f', 5))]

    def test_float_sum_is_naive_left_to_right(self):
        vals = [0.1] * 10  # builtin sum() on 3.12+ gives 1.0, naive accumulation (3.11, C#) gives 0.9999999999999999
        acc = 0.0
        for v in vals:
            acc += v
        self.assertEqual(rplan._lsum(vals), acc)
        self.assertEqual(rplan._lsum(iter(vals)).hex(), acc.hex())
        self.assertEqual(rplan._lsum([]), 0.0)
        big = [1e16] + [1.0] * 10  # naive sum stays 1e16; compensated sums (3.12 sum(), fsum) reach 1e16 + 10
        acc = 0.0
        for v in big:
            acc += v
        self.assertEqual(rplan._lsum(big), acc)
        self.assertNotEqual(acc, math.fsum(big))

    def test_hot_set_and_consensus_hash_is_python_version_independent(self):
        h = hashlib.sha256()
        for seed in range(40):
            rng = random.Random(seed)
            sessions = [[ev(rng.choice('abc'), rng.randrange(30), rng.choice([1, 2])) for _ in range(rng.randrange(40, 120))]
                        for _ in range(rng.randrange(2, 33))]
            decay = rng.choice([0.85, 0.7, 0.93, 0.61])
            blocks, st = rplan.hot_set(sessions, 1 << 40, decay=decay, theta_tail=0.0)
            order = rplan.consensus_order(sessions, blocks, decay=decay)
            h.update(repr((blocks, sorted(st.items()), order)).encode())
        # computed with naive left-to-right sums (Python 3.11); 3.12 builtin sum() would give a different digest
        self.assertEqual(h.hexdigest(), HOT_SET_HASH)

    def test_core_before_tail_and_budget(self):
        blocks, st = rplan.hot_set(self.s, 3 * BLK)
        self.assertEqual(set(blocks), {('f', 0), ('f', 1), ('f', 2)})
        blocks4, _ = rplan.hot_set(self.s, 4 * BLK)
        self.assertEqual(blocks4[3], ('f', 5))  # newest tail block (f 0.39) beats the oldest (f 0.28)
        self.assertEqual(rplan.hot_set(self.s, 0)[0], [])

    def test_stable_order_scores_higher_and_contiguous_is_demoted(self):
        _, st = rplan.hot_set(self.s, 10 * BLK)
        self.assertGreater(st[('f', 0)][3], st[('f', 1)][3])
        _, st2 = rplan.hot_set(self.s, 10 * BLK, contiguous={('f', 0)})
        self.assertAlmostEqual(st2[('f', 0)][3], st[('f', 0)][3] * 0.2)

    def test_single_run_keeps_access_order(self):
        blocks, _ = rplan.hot_set([run(('b', 3), ('a', 7), ('b', 1))], 10 * BLK)
        self.assertEqual(blocks, [('b', 3), ('a', 7), ('b', 1)])

    def test_min_sessions_drops_single_run_blocks(self):
        every, _ = rplan.hot_set(self.s, 10 * BLK)
        self.assertEqual(set(every), {('f', 0), ('f', 1), ('f', 2), ('f', 5), ('f', 9)})
        blocks, st = rplan.hot_set(self.s, 10 * BLK, min_sessions=2)
        self.assertEqual(set(blocks), {('f', 0), ('f', 1), ('f', 2)})
        self.assertNotIn(('f', 9), st)
        self.assertEqual(rplan.hot_set(self.s, 10 * BLK, min_sessions=3)[0].__len__(), 3)
        one = [run(('b', 3), ('a', 7))]
        self.assertEqual(rplan.hot_set(one, 10 * BLK, min_sessions=1)[0], [('b', 3), ('a', 7)])

    def test_bad_input(self):
        with self.assertRaises(ValueError):
            rplan.hot_set([], BLK)
        with self.assertRaises(ValueError):
            rplan.hot_set([run(('f', 0))], -1)


class ConsensusTests(unittest.TestCase):
    def test_majority_order_wins(self):
        a, b = ('p1', 0), ('p2', 0)
        s = [run(a, b), run(a, b), run(b, a)]
        units = rplan.consensus_order(s, [a, b])
        self.assertEqual([u[0] for u in units], ['p1', 'p2'])
        s2 = [run(b, a), run(a, b), run(b, a)]
        self.assertEqual([u[0] for u in rplan.consensus_order(s2, [a, b])], ['p2', 'p1'])

    def test_sequential_blocks_form_one_unit(self):
        s = [run(('f', 0), ('f', 1), ('f', 2), ('g', 0))] * 3
        hot = [('g', 0), ('f', 2), ('f', 0), ('f', 1)]
        self.assertEqual(rplan.consensus_order(s, hot), [('f', 0, 3), ('g', 0, 1)])

    def test_every_hot_block_appears_once(self):
        rng = random.Random(1)
        keys = [('f%d' % rng.randrange(4), rng.randrange(40)) for _ in range(200)]
        s = [run(*rng.sample(keys, 120)) for _ in range(4)]
        hot, _ = rplan.hot_set(s, 10 ** 9)
        units = rplan.consensus_order(s, hot)
        got = sorted((p, b + i) for p, b, n in units for i in range(n))
        self.assertEqual(got, sorted(hot))


def ext(*triples):
    return [tuple(t) for t in triples]


class AllocateTests(unittest.TestCase):
    def alloc(self, units, files, start=5000, clusters=1000, **kw):
        return rplan.allocate_zone(units, files, start, clusters, CB, **kw)

    def test_places_in_access_order_and_merges_adjacent(self):
        files = {'A': ext((0, 100, 4 * PER)), 'B': ext((0, 900, PER))}
        tasks, st, order = self.alloc([('A', 0, 1), ('A', 1, 1), ('B', 0, 1)], files)
        self.assertEqual(tasks, [('A', 0, 2 * PER, 5000), ('B', 0, PER, 5000 + 2 * PER)])
        self.assertEqual((st['tasks'], st['moved_bytes'], st['expected_runs'], st['current_runs']), (2, 3 * BLK, 1, 2))

    def test_fragmented_source_is_one_task_and_contiguous_target(self):
        files = {'A': ext((0, 100, PER), (PER, 700, PER))}
        tasks, st, _ = self.alloc([('A', 0, 2)], files)
        self.assertEqual(tasks, [('A', 0, 2 * PER, 5000)])
        self.assertEqual((st['current_runs'], st['expected_runs']), (2, 1))

    def test_piece_already_in_place_is_not_moved(self):
        files = {'A': ext((0, 5000, PER)), 'B': ext((0, 900, PER))}
        tasks, st, _ = self.alloc([('A', 0, 1), ('B', 0, 1)], files)
        self.assertEqual(tasks, [('B', 0, PER, 5000 + PER)])
        self.assertEqual(st['in_place_bytes'], BLK)

    def test_zero_preserve_moves_every_hot_range_and_is_the_default(self):
        big = 3 * 256
        files = {'A': ext((0, 100, big)), 'B': ext((0, 100 + big, big)), 'C': ext((0, 9000, PER))}
        units = [('A', 0, 48), ('B', 0, 48), ('C', 0, 1)]
        tasks, st, _ = self.alloc(units, files, clusters=5000, preserve_bytes=0)
        self.assertEqual((st['preserved_segments'], st['preserved_bytes']), (0, 0))
        self.assertEqual({t[0] for t in tasks}, {'A', 'B', 'C'})
        self.assertEqual(st['expected_runs'], 1)
        self.assertEqual(self.alloc(units, files, clusters=5000), (tasks, st, _))
        args = cli.build_parser().parse_args(['plan', '--trace', 't', '--origin-root', 'r', '--zone-mib', '1'])
        self.assertEqual(args.preserve_mib, 0)

    def test_large_contiguous_run_is_preserved(self):
        big = 3 * 256  # 3 MiB of clusters per file; preserve at 4 MiB across two files
        files = {'A': ext((0, 100, big)), 'B': ext((0, 100 + big, big)), 'C': ext((0, 9000, PER))}
        units = [('A', 0, 48), ('B', 0, 48), ('C', 0, 1)]  # 48 blocks = 3 MiB each
        tasks, st, _ = self.alloc(units, files, preserve_bytes=4 << 20)
        self.assertEqual((st['preserved_segments'], st['preserved_bytes']), (1, 6 << 20))
        self.assertEqual([t[0] for t in tasks], ['C'])
        self.assertEqual(st['expected_runs'], 2)

    def test_overflow_keeps_prefix(self):
        files = {'A': ext((0, 100, 3 * PER))}
        tasks, st, _ = self.alloc([('A', 0, 1), ('A', 2, 1), ('A', 1, 1)], files, clusters=2 * PER)
        self.assertEqual([t[1] for t in tasks], [0, 2 * PER])
        self.assertEqual(st['overflow_bytes'], BLK)

    def test_end_of_file_and_size_clamp(self):
        files = {'A': ext((0, 100, 20))}  # 20 clusters allocated, file is 18 clusters + 1 byte -> 19 usable
        tasks, _, order = self.alloc([('A', 0, 2)], files, sizes={'A': 18 * CB + 1})
        self.assertEqual(order, [('A', 0, 19)])
        self.assertEqual(tasks[0][2], 19)

    def test_duplicate_and_missing_files(self):
        files = {'A': ext((0, 100, PER)), 'S': None}
        tasks, st, order = self.alloc([('A', 0, 1), ('A', 0, 1), ('S', 0, 1), ('Z', 0, 1)], files)
        self.assertEqual((len(order), st['skipped_files']), (1, 2))

    def test_sources_inside_zone_get_dependency_order(self):
        # B's target is where A currently sits, so A has to leave first.
        files = {'B': ext((0, 900, PER)), 'A': ext((0, 5000, PER))}
        tasks, _, _ = self.alloc([('B', 0, 1), ('A', 0, 1)], files)
        self.assertEqual([t[0] for t in tasks], ['A', 'B'])

    def test_cycle_is_refused(self):
        files = {'X': ext((0, 5000 + PER, PER)), 'A': ext((0, 5000, PER))}
        with self.assertRaises(ValueError):
            self.alloc([('X', 0, 1), ('A', 0, 1)], files)

    def test_misaligned_pieces_counted(self):
        files = {'A': ext((0, 101, PER))}
        _, st, _ = self.alloc([('A', 0, 1)], files)
        self.assertEqual(st['misaligned_pieces'], 1)


class BitmapTests(unittest.TestCase):
    @staticmethod
    def make(used, nbits):
        data = bytearray((nbits + 7) // 8)
        for i in used:
            data[i >> 3] |= 1 << (i & 7)
        return mover.Bitmap(0, nbits, data)

    @staticmethod
    def brute(used, nbits):
        runs, cur = [], None
        for i in range(nbits + 1):
            free = i < nbits and i not in used
            if free and cur is None:
                cur = i
            elif not free and cur is not None:
                runs.append((cur, i - cur))
                cur = None
        return runs

    def test_free_runs_match_brute_force(self):
        rng = random.Random(7)
        for nbits in (1, 8, 13, 64, 1000, 4099):
            for density in (0.0, 0.02, 0.5, 0.98, 1.0):
                used = {i for i in range(nbits) if rng.random() < density}
                self.assertEqual(list(self.make(used, nbits).free_runs()), self.brute(used, nbits), (nbits, density))

    def test_find_free_run(self):
        bm = self.make(set(range(0, 10)) | set(range(30, 40)), 100)
        self.assertEqual(mover.find_free_run(bm, 5), 10)
        self.assertEqual(mover.find_free_run(bm, 21), 40)
        self.assertEqual(mover.find_free_run(bm, 20, prefer_low_lcn=False), 40)
        self.assertEqual(mover.find_free_run(bm, 20), 10)
        self.assertEqual(mover.find_free_run(bm, 61), None)
        self.assertEqual(mover.find_free_run(bm, 5, min_lcn=12), 12)
        self.assertTrue(bm.used(5) and not bm.used(15) and bm.used(1000))

    def test_struct_sizes_and_codes(self):
        if ctypes.sizeof(ctypes.c_void_p) == 8:
            self.assertEqual(ctypes.sizeof(mover.MOVE_FILE_DATA), 32)
        self.assertEqual(ctypes.sizeof(mover.NTFS_VOLUME_DATA_BUFFER), 96)

        def ctl(func, method):
            return (9 << 16) | (func << 2) | method
        self.assertEqual((mover.FSCTL_GET_NTFS_VOLUME_DATA, mover.FSCTL_GET_VOLUME_BITMAP, mover.FSCTL_MOVE_FILE),
                         (ctl(25, 0), ctl(27, 3), ctl(29, 0)))

    def test_volume_bitmap_is_fetched_in_chunks(self):
        import struct
        nbits = 5000
        src = self.make({3, 4, 999, 2500, 4999}, nbits)

        class V(mover.Volume):
            calls = 0

            def _ioctl(self, code, inp, out_len):
                V.calls += 1
                start = struct.unpack('<q', inp)[0]
                chunk = src.data[start // 8:start // 8 + (out_len - 16)]
                head = struct.pack('<qq', start, nbits - start)
                if start // 8 + len(chunk) < len(src.data):
                    return False, mover.ERROR_MORE_DATA, head + chunk
                return True, 0, head + chunk
        bm = V(None, None).bitmap(chunk_bytes=64)
        self.assertGreater(V.calls, 5)
        self.assertEqual((bm.nbits, bm.data), (nbits, src.data))


class FakeVol:
    """Volume stand-in: extents per path, move() really re-maps clusters."""
    cluster_bytes = CB
    total = 200000
    total_clusters = total
    free_clusters = total
    mft_zone = (0, 0)

    def __init__(self, files, letter, fail_after=None):
        self.ext, self.letter, self.moves, self.fail_after, self.closed = files, letter, [], fail_after, False

    def bitmap(self):
        used = {lcn + i for e in self.ext.values() for _, lcn, n in e for i in range(n)}
        return BitmapTests.make(used, self.total)

    def move(self, path, vcn, lcn, n):
        if self.fail_after is not None and len(self.moves) >= self.fail_after:
            raise OSError('simulated power loss')
        m = {v + i: l + i for v, l, k in self.ext[path] for i in range(k)}
        for i in range(n):
            m[vcn + i] = lcn + i
        out = []
        for v in sorted(m):
            if out and out[-1][0] + out[-1][2] == v and out[-1][1] + out[-1][2] == m[v]:
                out[-1] = (out[-1][0], out[-1][1], out[-1][2] + 1)
            else:
                out.append((v, m[v], 1))
        self.ext[path] = out
        self.moves.append((path, vcn, lcn, n))

    def close(self):
        self.closed = True


class ApplyBase(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = os.path.realpath(self.tmp.name)
        self.letter = os.path.splitdrive(self.root)[0].upper()
        self.f1, self.f2 = (os.path.join(self.root, n) for n in ('f1.bin', 'f2.bin'))
        for p in (self.f1, self.f2):
            Path(p).write_bytes(os.urandom(1 << 20))
        clusters = (1 << 20) // CB
        self.files = {self.f1: [(0, 1000, 64), (64, 3000, 64), (128, 2000, 64), (192, 5000, 64)],
                      self.f2: [(0, 7000, clusters // 2), (clusters // 2, 9000, clusters // 2)]}
        self.vol = FakeVol({p: list(e) for p, e in self.files.items()}, self.letter)
        self.sess = [run((self.f1, 12), (self.f2, 0), (self.f1, 0), (self.f1, 4), (self.f2, 8))]
        self.sess = [[dict(e) for e in s] for s in self.sess]

    def make(self, start=None):
        return rplan.make_plan(self.sess, self.root, 2 << 20, CB, lambda p: self.files[p], zone_start_lcn=start,
                               preserve_bytes=64 << 20)

    def apply(self, plan, **kw):
        kw.setdefault('attr_fn', lambda p: 0x20)
        kw.setdefault('sleep', lambda s: None)
        return rapply.apply_plan(plan, self.vol, [self.root], os.path.join(self.root, 'j.jsonl'),
                                 extents_fn=lambda p: self.vol.ext[p], **kw)


class PlanFileTests(ApplyBase):
    def test_make_plan_and_json_roundtrip(self):
        p = self.make()
        out = Path(self.root) / 'plan.json'
        rplan.save_plan(p, out)
        self.assertEqual(rplan.load_plan(out), json.loads(json.dumps(p)))
        self.assertEqual(p['stats']['hot_blocks'], 5)
        self.assertEqual((p['stats']['expected_runs'], p['zone']['start_lcn']), (1, None))
        self.assertGreater(p['stats']['current_runs'], 1)

    def test_prefetch_trace_is_exactly_the_zone_in_zone_order(self):
        self.sess = self.sess + [run((self.f1, 12), (self.f2, 0), (self.f1, 0), (self.f1, 4)), run((self.f2, 3))]
        p = self.make()
        self.assertEqual(p['stats']['hot_bytes'], sum(o['clusters'] for o in p['order']) * CB)
        rows = rplan.prefetch_trace(p, self.sess)
        self.assertEqual(sum(r['length'] for r in rows), p['stats']['hot_bytes'])
        self.assertEqual([(r['path'], r['offset'] // CB, r['length'] // CB) for r in rows],
                         [(o['path'], o['vcn'], o['clusters']) for o in p['order']])
        self.assertTrue(all(set(r) == {'path', 'offset', 'length', 'cost'} and r['cost'] == 8.0 for r in rows))

    def test_resolve_plan_shifts_targets(self):
        p = self.make()
        r = rplan.resolve_plan(p, 777)
        self.assertEqual(r['zone']['start_lcn'], 777)
        self.assertEqual([t['target_lcn'] - 777 for t in r['tasks']], [t['target_lcn'] for t in p['tasks']])
        self.assertIs(rplan.resolve_plan(r, 5), r)

    def test_malformed_plan_rejected(self):
        p = self.make()
        for bad in (dict(p, version=9), dict(p, tasks=[{'path': 1}]), {k: v for k, v in p.items() if k != 'zone'},
                    dict(p, zone={'start_lcn': -3, 'clusters': 10})):
            with self.assertRaises(ValueError):
                rplan.validate_plan(bad)

    def test_verify_layout_before_and_after(self):
        p = rplan.resolve_plan(self.make(), 20000)
        v = rapply.verify_layout(p, lambda x: self.vol.ext[x])
        self.assertFalse(v['contiguous'])
        self.assertGreater(v['ordered_runs'], 1)
        self.apply(p, execute=True)
        v = rapply.verify_layout(p, lambda x: self.vol.ext[x])
        self.assertTrue(v['contiguous'])
        self.assertEqual((v['ordered_runs'], v['ascending'], v['in_zone_clusters']), (1, True, v['hot_clusters']))


class ApplyTests(ApplyBase):
    def test_without_execute_nothing_moves_or_is_written(self):
        rep = self.apply(self.make(), execute=False)
        self.assertEqual((rep['executed'], rep['tasks_todo'], self.vol.moves), (False, 5, []))
        self.assertFalse(os.path.exists(os.path.join(self.root, 'j.jsonl')))
        self.assertEqual(self.vol.ext, self.files)

    def test_execute_moves_in_chunks_and_journals(self):
        p = self.make()
        rep = self.apply(p, execute=True, chunk_bytes=8 * CB, save_to=os.path.join(self.root, 'plan.json'))
        self.assertEqual(rep['failed'], [])
        self.assertTrue(all(m[3] <= 8 for m in self.vol.moves))
        self.assertEqual(rep['moved_bytes'], p['stats']['moved_bytes'])
        base = rep['zone_start_lcn']
        self.assertEqual(base, 0)  # lowest free run of the fake volume
        by_target = sorted(self.vol.moves, key=lambda m: m[2])
        self.assertEqual(by_target, self.vol.moves)  # target written strictly upward (SMR friendly)
        rows = [json.loads(x) for x in Path(self.root, 'j.jsonl').read_text().splitlines()]
        self.assertEqual({r['type'] for r in rows}, {'header', 'file', 'step', 'end'})
        planned = [r for r in rows if r['type'] == 'step' and r['state'] == 'planned']
        self.assertTrue(all(r['from'] for r in planned))
        self.assertEqual(rapply.Journal(os.path.join(self.root, 'j.jsonl')).last_state(0), 'done')
        self.assertEqual(rplan.load_plan(os.path.join(self.root, 'plan.json'))['zone']['start_lcn'], base)

    def test_write_cap_stops_and_second_run_finishes(self):
        p = rplan.resolve_plan(self.make(), 20000)
        r1 = self.apply(p, execute=True, chunk_bytes=8 * CB, write_cap=64 * CB)
        self.assertEqual(r1['stopped'], 'write_cap')
        done = len(self.vol.moves)
        r2 = self.apply(p, execute=True, chunk_bytes=8 * CB)
        self.assertIsNone(r2['stopped'])
        self.assertGreater(len(self.vol.moves), done)
        self.assertTrue(rapply.verify_layout(p, lambda x: self.vol.ext[x])['contiguous'])
        rows = rapply.Journal(os.path.join(self.root, 'j.jsonl')).rows
        self.assertEqual(sum(r['type'] == 'header' for r in rows), 1)
        firsts = [r for r in rows if r['type'] == 'step' and r['state'] == 'planned']
        self.assertEqual(len({r['idx'] for r in firsts}), len(firsts))  # 'from' recorded once per step

    def test_resume_after_interruption_keeps_original_extents(self):
        p = rplan.resolve_plan(self.make(), 20000)
        self.vol.fail_after = 3
        rep = self.apply(p, execute=True, chunk_bytes=8 * CB)
        self.assertTrue(rep['failed'])
        self.vol.fail_after = None
        rep = self.apply(p, execute=True, chunk_bytes=8 * CB)
        self.assertEqual(rep['failed'], [])
        self.assertTrue(rapply.verify_layout(p, lambda x: self.vol.ext[x])['contiguous'])
        j = rapply.Journal(os.path.join(self.root, 'j.jsonl'))
        all_orig = [e for exts in self.files.values() for e in exts]
        recorded = [e for r in j.rows if r['type'] == 'step' and 'from' in r for e in r['from']]
        self.assertTrue(recorded)  # every recorded source lies inside an original (pre-move) extent
        self.assertTrue(all(any(o[1] <= e[1] and e[1] + e[2] <= o[1] + o[2] for o in all_orig) for e in recorded))

    def test_sha_mismatch_is_reported(self):
        calls = []

        def digest(path, size, extra=()):
            calls.append(path)
            return 'a' if len(calls) <= 2 else 'b'
        with self.assertRaises(RuntimeError) as cm:
            self.apply(rplan.resolve_plan(self.make(), 20000), execute=True, digest_fn=digest)
        self.assertIn('sha_mismatch', str(cm.exception))

    def test_path_outside_root_and_changed_file_rejected(self):
        p = self.make()
        with self.assertRaises(rapply.PlanRejected):
            rapply.apply_plan(p, self.vol, [os.path.join(self.root, 'other')], os.path.join(self.root, 'j'),
                              extents_fn=lambda q: self.vol.ext[q])
        Path(self.f1).write_bytes(b'x' * 10)
        with self.assertRaises(rapply.PlanRejected):
            self.apply(p)

    def test_attribute_filter_skips_files(self):
        rep = self.apply(self.make(), execute=False, attr_fn=lambda p: 0x800 if p == self.f2 else 0x20)
        self.assertEqual(rep['skipped_files'], {self.f2: 'compressed'})
        self.assertEqual(rep['tasks_todo'], 3)

    def test_zone_in_use_is_rejected(self):
        self.vol.ext['other'] = [(0, 20000, 8)]
        with self.assertRaises(rapply.PlanRejected):
            self.apply(rplan.resolve_plan(self.make(), 20000))

    def test_revert_restores_original_extents(self):
        p = rplan.resolve_plan(self.make(), 20000)
        self.apply(p, execute=True)
        dry = rapply.revert_journal(os.path.join(self.root, 'j.jsonl'), self.vol, [self.root],
                                    extents_fn=lambda q: self.vol.ext[q])
        self.assertFalse(dry['executed'])
        self.assertTrue(self.vol.moves)
        rep = rapply.revert_journal(os.path.join(self.root, 'j.jsonl'), self.vol, [self.root], execute=True,
                                    extents_fn=lambda q: self.vol.ext[q], sleep=lambda s: None)
        self.assertEqual(rep['skipped'], [])
        self.assertEqual(self.vol.ext, self.files)


class CliTests(ApplyBase):
    def run_cli(self, *argv):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = cli.main(list(argv))
        return code, json.loads(out.getvalue())

    def test_not_admin_for_apply_and_revert(self):
        p = self.make()
        pf = Path(self.root) / 'plan.json'
        rplan.save_plan(p, pf)
        with mock.patch.object(mover, 'is_admin', return_value=False), \
                mock.patch.object(mover, '_k32', side_effect=AssertionError('volume touched')):
            code, out = self.run_cli('apply', '--plan', str(pf), '--allowed-root', self.root, '--execute')
            self.assertEqual((code, out), (3, {'error': 'not_admin'}))
            jf = Path(self.root) / 'x.jsonl'
            jf.write_text(json.dumps({'type': 'step', 'idx': 0, 'path': self.f1}) + '\n')
            code, out = self.run_cli('revert', '--journal', str(jf), '--allowed-root', self.root, '--execute')
            self.assertEqual((code, out), (3, {'error': 'not_admin'}))

    def test_apply_without_execute_moves_nothing(self):
        pf = Path(self.root) / 'plan.json'
        rplan.save_plan(self.make(), pf)
        with mock.patch.object(cli.Volume, 'open', return_value=self.vol), \
                mock.patch.object(rapply, 'file_extents', lambda q: self.vol.ext[q]), \
                mock.patch.object(rapply, 'skip_reason', return_value=None):
            code, out = self.run_cli('apply', '--plan', str(pf), '--allowed-root', self.root)
        self.assertEqual((code, out['executed'], out['tasks_todo']), (0, False, 5))
        self.assertEqual(self.vol.moves, [])
        self.assertTrue(self.vol.closed)

    def test_plan_dry_run_prints_stats_and_writes_nothing(self):
        trace = Path(self.root) / 't.json'
        trace.write_text(json.dumps(self.sess[0]))
        code, out = self.run_cli('plan', '--trace', str(trace), '--origin-root', self.root, '--zone-mib', '2',
                                 '--cluster-bytes', str(CB), '--dry-run')
        self.assertEqual((code, out['dry_run'], out['out']), (0, True, None))
        self.assertIn('moved_bytes', out['stats'])
        self.assertFalse((Path(self.root) / 'plan.json').exists())

    def test_plan_writes_prefetch_trace_and_min_sessions_default(self):
        traces = []
        for i, sess in enumerate([self.sess[0], self.sess[0] + run((self.f2, 5)), self.sess[0]]):
            t = Path(self.root) / ('t%d.json' % i)
            t.write_text(json.dumps(sess))
            traces.append(str(t))
        out = Path(self.root) / 'plan.json'
        with mock.patch.object(cli, 'file_extents', lambda q: self.files[q]):
            code, o = self.run_cli('plan', '--trace', *traces, '--origin-root', self.root, '--zone-mib', '2',
                                   '--cluster-bytes', str(CB), '--out', str(out))
            self.assertEqual((code, o['min_sessions'], o['prefetch_trace']), (0, 2, str(out) + '.prefetch.json'))
            rows = json.loads(Path(o['prefetch_trace']).read_text(encoding='utf-8'))
            self.assertEqual(sum(r['length'] for r in rows), o['stats']['hot_bytes'])
            self.assertNotIn((self.f2, 5 * BLK), [(r['path'], r['offset']) for r in rows])  # read in one run only
            code, o1 = self.run_cli('plan', '--trace', *traces, '--origin-root', self.root, '--zone-mib', '2',
                                    '--cluster-bytes', str(CB), '--min-sessions', '1', '--dry-run')
            self.assertEqual((code, o1['prefetch_trace']), (0, None))
            self.assertGreater(o1['stats']['hot_bytes'], o['stats']['hot_bytes'])
            code, o2 = self.run_cli('plan', '--trace', traces[0], '--origin-root', self.root, '--zone-mib', '2',
                                    '--cluster-bytes', str(CB), '--dry-run')
            self.assertEqual((code, o2['min_sessions']), (0, 1))
            code, o3 = self.run_cli('plan', '--trace', *traces, '--origin-root', self.root, '--zone-mib', '2',
                                    '--cluster-bytes', str(CB), '--min-sessions', '4', '--dry-run')
            self.assertEqual((code, o3['error']), (1, 'ValueError'))

    def test_verify_reports_non_contiguous_without_privileges(self):
        pf = Path(self.root) / 'plan.json'
        rplan.save_plan(self.make(), pf)
        with mock.patch.object(rapply, 'file_extents', lambda q: self.files[q]):
            code, out = self.run_cli('verify', '--plan', str(pf))
        self.assertEqual((code, out['verdict']), (0, 'non-contiguous'))


class SafetyHelperTests(unittest.TestCase):
    def test_skip_reason_flags(self):
        self.assertEqual(mover.skip_reason('x', lambda p: 0x20), None)
        self.assertEqual(mover.skip_reason('x', lambda p: 0x20 | 0x4000), 'encrypted')
        self.assertEqual(mover.skip_reason('x', lambda p: 0x200), 'sparse')
        self.assertTrue(mover.skip_reason('x', mock.Mock(side_effect=OSError('nope'))).startswith('attrs_unreadable'))

    @unittest.skipUnless(sys.platform == 'win32', 'drive letters')
    def test_inside_drive_root_forms(self):
        with tempfile.TemporaryDirectory() as d:
            drive = os.path.splitdrive(os.path.realpath(d))[0]
            other = 'E:' if drive.upper() != 'E:' else 'F:'
            bs = os.sep
            inner = drive + bs + 'aster_inside_test' + bs + 'f.bin'
            cwd = os.getcwd()
            os.chdir(d)  # a bare 'D:' must not turn into this directory
            try:
                for root in (drive + bs, drive + '/', drive + bs + bs, drive.lower() + bs, drive.lower() + '/', drive):
                    with self.subTest(root=root):
                        self.assertTrue(rapply.inside(inner, [root]))
                        self.assertTrue(rapply.inside(inner.lower().replace(bs, '/'), [root]))
                        self.assertTrue(rapply.inside(drive + bs, [root]))
                        self.assertFalse(rapply.inside(other + bs + 'aster_inside_test' + bs + 'f.bin', [root]))
                        self.assertEqual(rapply._drive(root), drive.upper())
                self.assertFalse(rapply.inside(inner, [os.path.join(d, 'sub')]))
                self.assertEqual(rapply.drive_root(drive), drive + bs)
                self.assertEqual(rapply.drive_root(inner), inner)
            finally:
                os.chdir(cwd)

    def test_sample_digest_unbuffered_matches_buffered(self):
        with tempfile.TemporaryDirectory() as d:
            p = os.path.join(d, 'f.bin')
            data = os.urandom(3 * (1 << 20) + 12345)
            Path(p).write_bytes(data)
            a = mover.sample_digest(p, len(data), unbuffered=True)
            self.assertEqual(a, mover.sample_digest(p, len(data), unbuffered=False))
            Path(p).write_bytes(data[:-1] + bytes([data[-1] ^ 1]))  # one flipped bit in the tail sample
            self.assertNotEqual(a, mover.sample_digest(p, len(data), unbuffered=True))
            if sys.platform == 'win32':
                self.assertEqual(mover._read_unbuffered(p, 5000, 7000), Path(p).read_bytes()[5000:12000])


class ReviewFixTests(ApplyBase):
    """B1 volume identity, M1 automatic zone, M2 moved-range samples, journal/unbuffered robustness, probe."""

    def run_cli(self, *argv):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = cli.main(list(argv))
        return code, json.loads(out.getvalue())

    def test_auto_zone_with_data_at_low_lcn_has_no_cycle_or_fake_in_place(self):
        # sim.py case: every file starts at LCN 1000. Before the fix the fake zone [0, n) caused a cycle error.
        clusters = (1 << 20) // CB
        self.files = {self.f1: [(0, 1000, clusters)], self.f2: [(0, 1000 + clusters, clusters)]}
        p = self.make()
        self.assertEqual(p['stats']['in_place_bytes'], 0)
        self.assertEqual(len(p['tasks']), 5)
        self.assertEqual(p['tasks'][0]['target_lcn'], 0)  # zone-relative offsets
        # repro.py (a): a piece whose LCN equals the zone offset is NOT already in place
        tasks, st, _ = rplan.allocate_zone([('A', 0, 1)], {'A': [(0, 0, PER)]}, None, 1000, CB)
        self.assertEqual((tasks, st['in_place_bytes']), ([('A', 0, PER, 0)], 0))
        # repro.py (b): two files sitting on each other's zone offsets used to raise
        files = {'A': [(0, PER, PER)], 'B': [(0, 0, PER)]}
        tasks, _, _ = rplan.allocate_zone([('A', 0, 1), ('B', 0, 1)], files, None, 1000, CB)
        self.assertEqual([t[3] for t in tasks], [0, PER])

    def test_explicit_zone_start_zero_is_honoured(self):
        tasks, st, _ = rplan.allocate_zone([('A', 0, 1)], {'A': [(0, 0, PER)]}, 0, 1000, CB)
        self.assertEqual((tasks, st['in_place_bytes']), ([], BLK))

    def test_zone_start_is_aligned_to_16kib(self):
        used = set(range(0, 1001))
        self.assertEqual(mover.find_free_run(BitmapTests.make(used, 5000), 10, align=4), 1004)

    def test_dry_run_fixes_zone_in_plan_file_but_moves_nothing(self):
        pf = Path(self.root) / 'plan.json'
        rplan.save_plan(self.make(), pf)
        with mock.patch.object(cli.Volume, 'open', return_value=self.vol):
            code, out = self.run_cli('apply', '--plan', str(pf), '--allowed-root', self.root, '--dry-run')
        self.assertEqual((code, out['plan_saved'], out['executed']), (0, True, False))
        self.assertEqual(self.vol.moves, [])
        self.assertEqual(rplan.load_plan(pf)['zone']['start_lcn'] % 4, 0)
        self.assertFalse(Path(self.root, 'plan.json.journal.jsonl').exists())

    def test_wrong_label_refuses_before_anything(self):
        pf = Path(self.root) / 'plan.json'
        rplan.save_plan(self.make(), pf)
        with mock.patch.object(cli.Volume, 'open', return_value=self.vol), \
                mock.patch.object(cli, 'volume_label', return_value='Windows'):
            code, out = self.run_cli('apply', '--plan', str(pf), '--allowed-root', self.root, '--execute',
                                     '--require-volume-label', 'AsterTrial')
        self.assertEqual((code, out['error']), (1, 'PlanRejected'))
        self.assertEqual(self.vol.moves, [])
        self.assertTrue(self.vol.closed)
        self.assertFalse(Path(self.root, 'plan.json.journal.jsonl').exists())

    def test_right_label_passes(self):
        pf = Path(self.root) / 'plan.json'
        rplan.save_plan(self.make(), pf)
        with mock.patch.object(cli.Volume, 'open', return_value=self.vol), \
                mock.patch.object(cli, 'volume_label', return_value='AsterTrial'):
            code, out = self.run_cli('apply', '--plan', str(pf), '--allowed-root', self.root,
                                     '--require-volume-label', 'AsterTrial')
        self.assertEqual((code, out['tasks_todo']), (0, 5))

    def test_allowed_root_on_other_drive_or_plan_on_other_volume_rejected(self):
        p = self.make()
        other = 'Q:\\' if self.letter != 'Q:' else 'R:\\'
        with self.assertRaises(rapply.PlanRejected):
            self.apply_with_roots(p, [other])
        self.vol.letter = 'Q:' if self.letter != 'Q:' else 'R:'
        with self.assertRaises(rapply.PlanRejected):
            self.apply(p)  # opened volume differs from the plan volume

    def test_link_resolving_to_other_drive_rejected(self):
        p = self.make()
        real = os.path.realpath
        fake = lambda x: 'Q:\\elsewhere\\f1.bin' if str(x) == self.f1 else real(x)  # noqa: E731
        with mock.patch.object(rapply.os.path, 'realpath', fake):
            with self.assertRaises(rapply.PlanRejected):
                self.apply(p)

    def apply_with_roots(self, plan, roots):
        return rapply.apply_plan(plan, self.vol, roots, os.path.join(self.root, 'j.jsonl'),
                                 extents_fn=lambda q: self.vol.ext[q], attr_fn=lambda q: 0x20)

    def test_revert_checks_roots_drive(self):
        with self.assertRaises(rapply.PlanRejected):
            rapply.revert_journal(os.path.join(self.root, 'none.jsonl'), self.vol, ['Q:\\x'])

    def test_probe_reports_volume_and_zone_candidate(self):
        self.vol.total_clusters, self.vol.free_clusters, self.vol.mft_zone = FakeVol.total, FakeVol.total - 2048 * 3, (1, 2)
        pf = Path(self.root) / 'plan.json'
        rplan.save_plan(self.make(), pf)
        with mock.patch.object(cli.Volume, 'open', return_value=self.vol), \
                mock.patch.object(cli, 'volume_label', return_value='AsterTrial'):
            code, out = self.run_cli('probe', '--volume', self.letter, '--plan', str(pf),
                                     '--require-volume-label', 'AsterTrial')
        self.assertEqual(code, 0)
        self.assertEqual((out['label'], out['cluster_bytes'], out['bitmap_bits']), ('AsterTrial', CB, FakeVol.total))
        used = sum(n for e in self.vol.ext.values() for _, _, n in e)
        self.assertEqual(out['free_clusters_bitmap'], FakeVol.total - used)
        self.assertIsNotNone(out['zone_candidate_lcn'])
        self.assertEqual(self.vol.moves, [])

    def test_moved_ranges_are_hashed(self):
        with tempfile.TemporaryDirectory() as d:
            p = os.path.join(d, 'f.bin')
            data = bytearray(os.urandom(8 << 20))
            Path(p).write_bytes(data)
            hole = 2 * (1 << 20) + (1 << 19)  # outside the head/middle/tail 1 MiB samples
            plain = mover.sample_digest(p, len(data))
            ranged = mover.sample_digest(p, len(data), [(hole - 100, 200)])
            data[hole] ^= 1
            Path(p).write_bytes(data)
            self.assertEqual(mover.sample_digest(p, len(data)), plain)  # not seen without the moved range
            self.assertNotEqual(mover.sample_digest(p, len(data), [(hole - 100, 200)]), ranged)
        plan = self.make()
        mr = rapply.moved_ranges(plan)
        self.assertEqual(sum(len(v) for v in mr.values()), 2 * len(plan['tasks']))
        self.assertTrue(all(n <= rapply.TASK_SPAN for v in mr.values() for _, n in v))

    def test_truncated_last_journal_line_is_dropped_and_appends_stay_valid(self):
        jp = Path(self.root) / 'j.jsonl'
        jp.write_text(json.dumps({'type': 'header', 'plan_sha': 'x'}) + '\n' + '{"type": "step", "idx": 0, "sta')
        j = rapply.Journal(jp)
        self.assertEqual([r['type'] for r in j.rows], ['header'])
        j.add(type='end')
        j.close()
        self.assertEqual([r['type'] for r in rapply.Journal(jp).rows], ['header', 'end'])
        jp.write_text('{"type": "header"\n' + json.dumps({'type': 'end'}) + '\n')
        with self.assertRaises(ValueError):  # damage in the middle is not silently skipped
            rapply.Journal(jp)

    def test_unbuffered_read_failure_is_oserror_not_buffererror(self):
        k = mock.MagicMock()
        k.CreateFileW.return_value = 5
        k.SetFilePointerEx.return_value = 1
        k.ReadFile.return_value = 0
        with mock.patch.object(mover, '_k32', return_value=k):
            with self.assertRaises(OSError):
                mover._read_unbuffered('x', 0, 4096)
        with mock.patch.object(mover, '_k32', return_value=k), mock.patch('builtins.open', mock.mock_open(read_data=b'abc')):
            self.assertEqual(len(mover.sample_digest('x', 3)), 64)  # falls back to buffered reads


if __name__ == '__main__':
    unittest.main()
