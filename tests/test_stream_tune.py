import contextlib, io, json, sys, tempfile, unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from bench import cache_control, concurrent_run, dataset, stream_tune

COLD = dict(cold_method='unbuffered_proxy', unbuffered=True, purge={})


class StreamTuneTests(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.root = Path(tmp.name) / 'AsterBench'
        dataset.generate(self.root, n_files=3, min_mib=1, max_mib=1, trace_n=60, min_free_gib=0)
        self.events = dataset.load_trace(self.root)
        self.ops = stream_tune.hot_ops(self.root)

    def test_hot_ops_cover_trace_bytes(self):
        self.assertGreater(len(self.ops), 0)
        self.assertLessEqual(len(self.ops), len(self.events))
        self.assertTrue(all(o[2] > 0 for o in self.ops))

    def test_cold_ops_stay_in_dataset_and_respect_budget(self):
        flush = stream_tune.cold_ops(self.ops, 1)
        self.assertLessEqual(sum(o[2] for o in flush), 1 << 20)
        self.assertEqual(stream_tune.cold_ops([], 1), [])

    def test_sweep_rows_and_table(self):
        with mock.patch.object(cache_control, 'make_cold', return_value=COLD):
            res = stream_tune.sweep(self.root, [65536, 1 << 20], [1, 2], [False], 2, 0, log=lambda *_: None)
        self.assertEqual(len(res['rows']), 8)
        self.assertEqual(len(res['table']), 4)
        self.assertEqual([t['median_s'] for t in res['table']], sorted(t['median_s'] for t in res['table']))

    def test_concurrent_run_reads_everything(self):
        r = concurrent_run.run_once(self.events, self.ops, qd=2, think_us=100)
        self.assertEqual((r['requests'], r['stream_errors']), (60, 0))
        self.assertEqual(r['stream_bytes'], sum(o[2] for o in self.ops))
        self.assertGreaterEqual(r['wall_s'], 60 * 100e-6)
        self.assertTrue(0 <= r['slow_ratio'] <= 1)

    def test_concurrent_main_writes_json(self):
        out = Path(self.root).parent / 'o' / 'c.json'
        with mock.patch.object(cache_control, 'make_cold', return_value=COLD), contextlib.redirect_stdout(io.StringIO()):
            concurrent_run.main(['--root', str(self.root), '--repeat', '1', '--think-us', '0', '50', '--flush-mib', '0',
                                 '--out', str(out)])
        self.assertEqual(sorted(json.loads(out.read_text())['series']), ['0', '50'])


if __name__ == '__main__':
    unittest.main()
