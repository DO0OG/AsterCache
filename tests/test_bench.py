import contextlib, io, json, os, sys, tempfile, unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import session_planner
from bench import cache_control, dataset, replay, run, shadow_copy

SMALL = dict(n_files=2, min_mib=1, max_mib=1, trace_n=50, min_free_gib=0)


class BenchTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name) / 'AsterBench'
        self.meta = dataset.generate(self.root, **SMALL)
        self.events = dataset.load_trace(self.root)

    def test_dataset_and_trace(self):
        self.assertEqual(len(self.events), 50)
        self.assertEqual(len(list((self.root / 'data').iterdir())), 2)
        for e in self.events:
            self.assertTrue(os.path.isfile(e['path']))
            self.assertLessEqual(e['offset'] + e['length'], 1 << 20)
        # deterministic: regenerating the trace gives the same events
        self.assertEqual(self.events, dataset.make_trace(self.root, self.meta))
        # the existing planner consumes the trace as-is
        plan = session_planner.plan_sessions([self.events], 1 << 20)
        self.assertGreater(session_planner.coverage(plan, self.events)['coverage_pct'], 0)

    def test_unbuffered_matches_buffered(self):
        path = self.events[0]['path']
        b, u = replay.BufferedReader(), replay.UnbufferedReader()
        try:
            for off, n in [(0, 65536), (1000, 5000), (4096, 8192), ((1 << 20) - 100, 100), ((1 << 20) - 100, 5000)]:
                self.assertEqual(u.read(path, off, n), b.read(path, off, n), (off, n))
        finally:
            b.close()
            u.close()
        rb, ru = replay.replay(self.events), replay.replay(self.events, unbuffered=True)
        self.assertEqual((rb['crc32'], rb['bytes']), (ru['crc32'], ru['bytes']))

    def test_replay_skips_missing_files(self):
        gone = str(Path(self.tmp.name) / 'gone' / 'msasn1.pdb')
        events = self.events + [dict(path=gone, offset=0, length=4096, cost=8.0)] * 2
        base = replay.replay(self.events)
        for unbuffered in (False, True):
            r = replay.replay(events, unbuffered)
            self.assertEqual((r['missing_files'], r['skipped_requests'], r['skipped_bytes']), (1, 2, 8192))
            self.assertEqual((r['requests'], r['bytes'], r['crc32']), (base['requests'], base['bytes'], base['crc32']))
        self.assertEqual((base['missing_files'], base['skipped_requests'], base['skipped_bytes']), (0, 0, 0))

    def test_shadow_copy_sparse_hot_blocks_only(self):
        B = shadow_copy.BLOCK
        src, dst = Path(self.tmp.name) / 'src', Path(self.tmp.name) / 'dst'
        (src / 'sub').mkdir(parents=True)
        da, db = os.urandom(3 * B + 100), os.urandom(2 * B)
        (src / 'a.bin').write_bytes(da)
        (src / 'sub' / 'b.bin').write_bytes(db)
        ev = lambda p, o, n: dict(path=str(p), offset=o, length=n, cost=8.0)
        trace = [ev(src / 'a.bin', B + 10, 20), ev(src / 'sub' / 'b.bin', 0, 5), ev(src / 'a.bin', 3 * B + 50, 500),
                 ev(src / 'gone.bin', 0, 10), ev(Path(self.tmp.name) / 'elsewhere.bin', 0, 10)]
        for _ in range(2):  # a second run overwrites the first shadow
            out, sm = shadow_copy.shadow_copy(trace, str(src), str(dst))
        self.assertEqual((sm['files'], sm['missing_files'], sm['outside_root_requests']), (2, 1, 1))
        self.assertEqual(sm['written_bytes'], B + B + 100)  # a: block 1 and the 100-byte tail block; b: block 0
        self.assertEqual([e['path'] for e in out], [str(dst / 'a.bin'), str(dst / 'sub' / 'b.bin'), str(dst / 'a.bin'),
                                                    str(dst / 'gone.bin')])
        self.assertEqual([e['cost'] for e in out], [8.0] * 4)
        ra, rb = (dst / 'a.bin').read_bytes(), (dst / 'sub' / 'b.bin').read_bytes()
        self.assertEqual((len(ra), len(rb)), (len(da), len(db)))
        self.assertEqual(ra[B:2 * B], da[B:2 * B])
        self.assertEqual(ra[3 * B:], da[3 * B:])
        self.assertEqual(ra[:B] + ra[2 * B:3 * B], bytes(2 * B))
        self.assertEqual(rb[:B], db[:B])
        self.assertEqual(rb[B:], bytes(B))
        if sm['sparse']:
            self.assertLess(sm['allocated_bytes'], len(da) + len(db))

    def test_shadow_copy_allocates_only_hot_blocks(self):
        B = shadow_copy.BLOCK
        src, dst = Path(self.tmp.name) / 'src', Path(self.tmp.name) / 'dst'
        src.mkdir()
        (src / 'big.bin').write_bytes(b'a' * (64 << 20))
        trace = [dict(path=str(src / 'big.bin'), offset=o, length=4096, cost=1.0) for o in (0, 20 << 20, 50 << 20)]
        _, sm = shadow_copy.shadow_copy(trace, str(src), str(dst))
        self.assertEqual(sm['written_bytes'], 3 * B)
        self.assertEqual((dst / 'big.bin').stat().st_size, 64 << 20)
        if sm['sparse']:
            self.assertLessEqual(sm['allocated_bytes'], 2 * sm['written_bytes'])  # cluster rounding only, no zero fill

    def test_cache_control_non_admin(self):
        with mock.patch.object(cache_control, 'is_admin', return_value=False):
            p = cache_control.purge_standby()
            c = cache_control.make_cold()
        self.assertEqual(p, dict(ok=False, is_admin=False, error='not_admin'))
        self.assertEqual((c['cold_method'], c['unbuffered']), ('unbuffered_proxy', True))

    def test_run_writes_summary_json(self):
        out = Path(self.tmp.name) / 'out'
        ssd = Path(self.tmp.name) / 'ssd' / 'AsterBench'
        argv = ['--root-hdd', str(self.root), '--root-ssd', str(ssd), '--repeat', '1', '--out-dir', str(out),
                '--files', '2', '--min-mib', '1', '--max-mib', '1', '--trace', '50', '--min-free-gib', '0']
        with mock.patch.object(run, 'environment', return_value={'stub': True}), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(run.main(argv), 0)
        res = json.loads(next(out.glob('*.json')).read_text())
        for k in ('summary', 'verdict', 'runs', 'environment'):
            self.assertIn(k, res)
        self.assertIn('hdd_cold_over_ssd_cold', res['verdict'])
        self.assertIn('prewarm_naive_total_over_ssd_cold', res['verdict'])
        self.assertEqual(res['summary']['hdd']['cold']['cold_method'], 'unbuffered_proxy'
                         if not cache_control.is_admin() else 'standby_purge')

    def test_cleanup_guard(self):
        dataset.cleanup(self.root)
        self.assertFalse((self.root / 'data').exists())
        with self.assertRaises(ValueError):
            dataset.cleanup(Path(self.tmp.name))


if __name__ == '__main__':
    unittest.main()
