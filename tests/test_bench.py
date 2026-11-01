import contextlib, io, json, os, sys, tempfile, unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import session_planner
from bench import cache_control, dataset, replay, run

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
