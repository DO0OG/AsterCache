"""prefetch package: scheduler (pure), layout (real NTFS or None, never raises), reader."""
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from prefetch import cli
from prefetch.layout import cluster_size, file_extents, physical_key
from prefetch.reader import read_ops
from prefetch.scheduler import MIB, breakeven_gap, schedule

CS = 4096
# fake file: file clusters 0-9 at disk 1000.., clusters 10-19 at disk 100..
EXT = [(0, 1000, 10), (10, 100, 10)]
LAY = {'A': (EXT, CS), 'B': ([(0, 500, 20)], CS)}


def rng(path, off, n):
    return {'path': path, 'offset': off, 'length': n}


class SchedulerTests(unittest.TestCase):
    def test_physical_key_maps_offsets(self):
        self.assertEqual(physical_key(EXT, 0, CS), 1000)
        self.assertEqual(physical_key(EXT, 12 * CS + 5, CS), 102)
        self.assertIsNone(physical_key(EXT, 99 * CS, CS))
        self.assertIsNone(physical_key(None, 0, CS))

    def test_physical_sorts_by_disk_position_and_splits_at_extent(self):
        ops, info = schedule([rng('A', 0, CS), rng('B', 0, CS), rng('A', 15 * CS, CS)], 'physical',
                             gap_bytes=0, layout_fn=LAY.get)
        self.assertEqual(ops, [('A', 15 * CS, CS), ('B', 0, CS), ('A', 0, CS)])
        self.assertFalse(info['fallback'])
        ops, _ = schedule([rng('A', 8 * CS, 4 * CS)], 'physical', gap_bytes=0, layout_fn=LAY.get)
        self.assertEqual(ops, [('A', 10 * CS, 2 * CS), ('A', 8 * CS, 2 * CS)])  # disk 100 before 1008

    def test_merge_within_gap_and_not_beyond(self):
        rs = [rng('B', 0, CS), rng('B', 3 * CS, CS), rng('B', 15 * CS, CS)]
        ops, _ = schedule(rs, 'physical', gap_bytes=2 * CS, layout_fn=LAY.get)
        self.assertEqual(ops, [('B', 0, 4 * CS), ('B', 15 * CS, CS)])
        ops, _ = schedule(rs, 'physical', gap_bytes=0, layout_fn=LAY.get)
        self.assertEqual(len(ops), 3)

    def test_overlap_merges(self):
        ops, _ = schedule([rng('B', 0, 2 * CS), rng('B', CS, 2 * CS)], 'path', gap_bytes=0)
        self.assertEqual(ops, [('B', 0, 3 * CS)])

    def test_unknown_layout_sorts_last_and_flags_fallback(self):
        ops, info = schedule([rng('Z', 0, CS), rng('B', 0, CS)], 'physical', gap_bytes=0, layout_fn=LAY.get)
        self.assertEqual(ops, [('B', 0, CS), ('Z', 0, CS)])
        self.assertTrue(info['fallback'])

    def test_path_and_none_strategies(self):
        rs = [rng('b', 5, 10), rng('a', 50, 10), rng('a', 0, 10)]
        self.assertEqual(schedule(rs, 'none')[0], [('b', 5, 10), ('a', 50, 10), ('a', 0, 10)])
        self.assertEqual(schedule(rs, 'path', gap_bytes=0)[0], [('a', 0, 10), ('a', 50, 10), ('b', 5, 10)])

    def test_budget_drops_low_score_then_tail(self):
        rs = [dict(rng('a', 0, 100), score=1), dict(rng('a', 1000, 100), score=9), dict(rng('a', 2000, 100), score=5)]
        ops, info = schedule(rs, 'path', budget_bytes=200, gap_bytes=0)
        self.assertEqual(ops, [('a', 1000, 100), ('a', 2000, 100)])
        self.assertEqual(info['selected_bytes'], 200)
        ops, _ = schedule([rng('a', 0, 100), rng('a', 1000, 100), rng('a', 2000, 100)], 'none', budget_bytes=250)
        self.assertEqual(ops, [('a', 0, 100), ('a', 1000, 100)])

    def test_breakeven_gap(self):
        self.assertEqual(breakeven_gap(), 1_200_000)
        self.assertEqual(breakeven_gap(200, 10), 2_000_000)
        ops, info = schedule([rng('a', 0, 10), rng('a', 1_000_000, 10)], 'path')  # default gap merges 1MB hole
        self.assertEqual((ops, info['gap_bytes']), ([('a', 0, 1_000_010)], 1_200_000))

    def test_full_file_when_dense_enough_and_budget_allows(self):
        rs = [rng('a', 0, 400), rng('a', 600, 100), rng('b', 0, 100)]  # a: 50% of 1000, b: 10% of 1000
        ops, info = schedule(rs, 'path', gap_bytes=0, size_fn=lambda p: 1000)
        self.assertEqual(ops, [('a', 0, 1000), ('b', 0, 100)])
        self.assertEqual((info['full_files'], info['selected_bytes']), (1, 1100))
        ops, info = schedule(rs, 'path', budget_bytes=700, gap_bytes=0, size_fn=lambda p: 1000)  # no room
        self.assertEqual((info['full_files'], len(ops)), (0, 3))
        ops, _ = schedule(rs, 'path', gap_bytes=0, size_fn=lambda p: 1000, full_file_ratio=2)  # disabled
        self.assertEqual(len(ops), 3)

    def test_bad_strategy(self):
        with self.assertRaises(ValueError):
            schedule([], 'bogus')


class IoTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.f = os.path.join(self.tmp.name, 'f.bin')
        with open(self.f, 'wb') as fh:
            fh.write(os.urandom(3 * MIB))

    def test_file_extents_returns_list_or_none_never_raises(self):
        ext = file_extents(self.f)
        self.assertTrue(ext is None or isinstance(ext, list))
        if ext:
            self.assertTrue(all(len(e) == 3 and e[2] > 0 for e in ext))
            self.assertTrue(cluster_size(self.f) > 0)
        self.assertIsNone(file_extents(os.path.join(self.tmp.name, 'missing.bin')))

    def test_reader_bytes_equal_request_sum(self):
        ops = [(self.f, 0, 100000), (self.f, MIB, 70000), (self.f, 2 * MIB + 5, 12345)]
        for threads, hint in ((1, False), (2, False), (1, True)):
            r = read_ops(ops, threads=threads, sequential_hint=hint)
            self.assertEqual(r['bytes'], sum(o[2] for o in ops))
            self.assertEqual((r['requests'], r['errors']), (3, 0))

    def test_reader_missing_file_counts_error(self):
        r = read_ops([(os.path.join(self.tmp.name, 'nope'), 0, 10)])
        self.assertEqual((r['bytes'], r['errors']), (0, 1))

    def test_reader_does_not_modify_file(self):
        before = Path(self.f).read_bytes()
        read_ops([(self.f, 0, 3 * MIB)])
        self.assertEqual(Path(self.f).read_bytes(), before)

    def test_cli_trace_and_plan_inputs(self):
        trace = Path(self.tmp.name) / 't.json'
        trace.write_text(json.dumps([{'path': self.f, 'offset': 0, 'length': 65536, 'cost': 8.},
                                     {'path': self.f, 'offset': 2 * MIB, 'length': 10 * MIB, 'cost': 8.}]))
        plan = Path(self.tmp.name) / 'p.json'
        plan.write_text(json.dumps({'block_size': 65536, 'ranges': [{'path': self.f, 'offset': 0, 'length': 65536}]}))
        for src, expect in ((trace, 65536 + MIB), (plan, 65536)):
            rs = cli.load_ranges(src, self.tmp.name)
            ops, _ = schedule(rs, 'physical', gap_bytes=0, layout_fn=cli._layout)
            self.assertEqual(sum(o[2] for o in ops), expect)  # length clamped to file size
        with tempfile.TemporaryDirectory() as other:  # outside origin-root -> dropped
            self.assertEqual(cli.load_ranges(trace, other), [])


    def test_cli_reads_env_when_no_options(self):
        trace = Path(self.tmp.name) / 't.json'
        trace.write_text(json.dumps([{'path': self.f, 'offset': 0, 'length': 65536, 'cost': 8.}]))
        env = dict(os.environ, ASTER_TRACE=str(trace), ASTER_ROOT=self.tmp.name)
        r = subprocess.run([sys.executable, '-m', 'prefetch.cli', '--strategy', 'path', '--execute'],
                           cwd=str(Path(__file__).resolve().parents[1]), env=env, capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stderr)
        d = json.loads(r.stdout)
        self.assertEqual((d['bytes'], d['requests'], d['strategy']), (65536, 1, 'path'))


if __name__ == '__main__':
    unittest.main()
