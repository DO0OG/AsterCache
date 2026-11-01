"""astercache_v07 trace: Procmon CSV (real column layout, thousands separators) -> per-run + union trace JSON."""
import contextlib
import csv
import io
import json
import os
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import astercache_v07
from prefetch.cli import load_ranges

HDR = ['Time of Day', 'Process Name', 'PID', 'Operation', 'Path', 'Result', 'Detail']


def read(path, off, n, result='SUCCESS'):
    return ['1:00:00.0000001 PM', 'game.exe', '42', 'ReadFile', path, result,
            'Offset: {:,}, Length: {:,}, I/O Flags: Non-cached, Paging I/O, Synchronous Paging I/O, Priority: Normal'.format(off, n)]


class TraceTests(unittest.TestCase):
    def test_trace_union_per_run_and_prefetch_accepts_it(self):
        with tempfile.TemporaryDirectory() as d:
            d = Path(d)
            root = d / 'Game'
            root.mkdir()
            a, b = root / 'A.bin', root / 'b.bin'
            a.write_bytes(b'\0' * 400_000)
            b.write_bytes(b'\0' * 200_000)
            spelled = str(a).lower() if os.name == 'nt' else str(a)  # Procmon may log another letter case
            runs = [[read(str(a), 1_000, 4_096), read(str(b), 70_000, 65_536), read(str(b), 0, 4_096, 'END OF FILE'),
                     read(r'C:\Windows\x.dll' if os.name == 'nt' else '/elsewhere/x', 0, 4_096)],
                    [read(spelled, 1_000, 4_096), read(str(a), 200_000, 100_000)]]
            csvs = []
            for i, rows in enumerate(runs, 1):
                f = d / ('run%d.csv' % i)
                with f.open('w', newline='', encoding='utf-8-sig') as fh:
                    w = csv.writer(fh, quoting=csv.QUOTE_ALL)
                    w.writerow(HDR)
                    w.writerows(rows)
                csvs.append(str(f))
            out = d / 'trace.json'
            with contextlib.redirect_stdout(io.StringIO()):
                astercache_v07.main(['trace', '--csv', *csvs, '--prefix', str(root) + os.sep, '--out', str(out)])
            run1 = json.loads((d / 'trace.run1.json').read_text(encoding='utf-8'))
            self.assertEqual([(e['offset'], e['length']) for e in run1], [(1_000, 4_096), (70_000, 65_536)])
            union = json.loads(out.read_text(encoding='utf-8'))
            keys = [(Path(e['path']).name, e['offset'] // 65536) for e in union]
            self.assertEqual(len(keys), len(set(keys)))  # one entry per 64 KiB block, case variants merged
            self.assertEqual({k for k in keys if k[0] == 'b.bin'}, {('b.bin', 1), ('b.bin', 2)})
            self.assertEqual({(n, i) for n, i in keys if n == 'A.bin'}, {('A.bin', 0), ('A.bin', 3), ('A.bin', 4)})
            self.assertEqual({e['length'] for e in union}, {65536})
            if os.name == 'nt':
                self.assertEqual({e['path'] for e in union if e['path'].lower() == str(a).lower()}, {str(a)})
            self.assertTrue(load_ranges(out, root))  # prefetch/relayout read the file as is


if __name__ == '__main__':
    unittest.main()
