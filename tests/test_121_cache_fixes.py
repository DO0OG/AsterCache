"""v0.7 fix checks: racy-clean admission guard and covering LRU index."""
from pathlib import Path
import random
import tempfile
import time
import unittest
from unittest.mock import patch
import sys
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from sqlite_cache import SQLiteBlockCache
B=65536

class CacheFixes121(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup)
        self.root=Path(self.temp.name)/'hdd';self.root.mkdir();self.dest=Path(self.temp.name)/'ssd'
        self.contents=random.Random(121).randbytes(B*4+17);(self.root/'a').write_bytes(self.contents)
    def cache(self):
        c=SQLiteBlockCache(self.root,self.dest,capacity_bytes=16*B,min_free_bytes=0)
        self.addCleanup(c.close);return c
    def test_121_freshly_written_file_is_not_admitted(self):
        c=self.cache();self.assertEqual(c.read('a',0,100),self.contents[:100])
        self.assertEqual(c.snapshot()['block_count'],0)
    def test_122_file_older_than_window_is_admitted(self):
        c=self.cache()
        with patch('sqlite_cache.time.time_ns',return_value=time.time_ns()+3*10**9):
            self.assertEqual(c.read('a',0,100),self.contents[:100])
        self.assertEqual(c.snapshot()['block_count'],1)
    def test_123_sum_query_uses_covering_index(self):
        c=self.cache()
        plan=' '.join(str(r) for r in c.conn.execute('EXPLAIN QUERY PLAN SELECT COALESCE(SUM(byte_count),0) FROM blocks').fetchall())
        self.assertIn('COVERING INDEX',plan)

if __name__=='__main__':unittest.main()
