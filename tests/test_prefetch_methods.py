import contextlib, io, json, sys, tempfile, unittest, zlib
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from bench import cache_control, dataset, prefetch_methods, replay, stream_tune
from prefetch import cli, reader

COLD = dict(cold_method='unbuffered_proxy', unbuffered=True, purge={})
NEW_METHODS = ('pvm', 'pvm-file', 'overlapped', 'mmap-touch')


class PrefetchMethodTests(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.root = Path(tmp.name) / 'AsterBench'
        dataset.generate(self.root, n_files=3, min_mib=1, max_mib=1, trace_n=60, min_free_gib=0)
        self.events = dataset.load_trace(self.root)
        self.ops = stream_tune.hot_ops(self.root)
        self.nbytes = sum(o[2] for o in self.ops)

    def test_every_method_reads_all_bytes_and_leaves_data_intact(self):
        before = replay.replay(self.events)['crc32']
        for m in reader.METHODS:
            with self.subTest(method=m):
                r = reader.read_ops(self.ops, 2, False, 64 << 10, True, m)
                self.assertEqual((r['errors'], r['bytes']), (0, self.nbytes))
                self.assertEqual(replay.replay(self.events)['crc32'], before)

    def test_overlapped_delivers_exact_file_bytes(self):
        got = {}
        ops = [(str(self.root / 'data' / 'f000.bin'), 1000, 300000), (str(self.root / 'data' / 'f001.bin'), 0, 70000)]
        stats = {'bytes': 0, 'requests': 0, 'errors': 0}
        reader._read_overlapped(ops, 3, 65536, stats, lambda p, o, d: got.__setitem__((p, o), d))
        self.assertEqual((stats['errors'], stats['bytes']), (0, 370000))
        self.assertEqual(sum(map(len, got.values())), 370000)
        for (p, o), d in got.items():
            with open(p, 'rb') as f:
                f.seek(o)
                self.assertEqual(zlib.crc32(d), zlib.crc32(f.read(len(d))))
        self.assertEqual([o for (_, o) in got], [1000, 66536, 132072, 197608, 263144, 0, 65536])

    def test_ranges_past_eof_are_clamped(self):
        p = str(self.root / 'data' / 'f000.bin')
        size = Path(p).stat().st_size
        for m in NEW_METHODS:
            with self.subTest(method=m):
                r = reader.read_ops([(p, size - 5000, 100000)], 2, False, 65536, True, m)
                self.assertEqual((r['errors'], r['bytes']), (0, 5000))

    def test_missing_file_counts_error(self):
        r = reader.read_ops([(str(self.root / 'nope.bin'), 0, 4096)], 1, False, 65536, True, 'overlapped')
        self.assertEqual((r['errors'], r['bytes']), (1, 0))

    def test_unknown_method_rejected(self):
        with self.assertRaises(ValueError):
            reader.read_ops(self.ops, method='bogus')

    def test_cli_method_option(self):
        argv = ['--plan', str(self.root / 'trace.json'), '--origin-root', str(self.root), '--execute']
        for m in ('pvm', 'overlapped'):
            buf = io.StringIO()
            with contextlib.redirect_stdout(buf):
                cli.main(argv + ['--method', m, '--threads', '4'])
            out = json.loads(buf.getvalue())
            self.assertEqual((out['method'], out['errors'], out['bytes']), (m, 0, self.nbytes))
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            cli.main(argv)
        self.assertEqual(json.loads(buf.getvalue())['method'], 'pvm')

    def test_bench_rows_and_table(self):
        with mock.patch.object(cache_control, 'make_cold', return_value=COLD):
            res = prefetch_methods.run(self.root, ['read', 'pvm', 'overlapped-qd2', 'mmap-touch'], 2, 0, log=lambda *_: None)
        self.assertEqual(len(res['rows']), 8)
        self.assertEqual(len(res['table']), 4)
        self.assertTrue(all(t['crc_ok'] and t['bytes'] == self.nbytes for t in res['table']))
        self.assertEqual([t['stream_s'] for t in res['table']], sorted(t['stream_s'] for t in res['table']))


if __name__ == '__main__':
    unittest.main()
