"""Additional checks discovered during v0.7 code review."""
import hashlib
from pathlib import Path
import random
import sqlite3
import tempfile
import time
import unittest
from unittest.mock import patch
import sys
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import sqlite_cache
from sqlite_cache import SQLiteBlockCache
B=65536
class NewEdgeTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup)
        racy=patch('sqlite_cache.RACY_WINDOW_NS',0);racy.start();self.addCleanup(racy.stop)
        self.root=Path(self.temp.name)/'hdd';self.root.mkdir();self.dest=Path(self.temp.name)/'ssd'
        self.contents=random.Random(2).randbytes(B*8);(self.root/'a').write_bytes(self.contents)
    def cache(self,**kw):
        c=SQLiteBlockCache(self.root,self.dest,capacity_bytes=2*B,min_free_bytes=0,**kw)
        self.addCleanup(c.close);return c
    def test_116_frequently_read_block_survives_new_admission(self):
        c=self.cache();c.read('a',0,1);c.read('a',B,1);c.read('a',0,1);c.read('a',2*B,1)
        before=c.snapshot()['block_hits'];c.read('a',0,1)
        self.assertEqual(c.snapshot()['block_hits'],before+1)
    def test_116b_hot_block_survives_with_frozen_clock(self):
        frozen = time.time_ns() + 10 ** 13  # far past RACY_WINDOW_NS, so blocks are admitted; every stamp would tie
        with patch.object(sqlite_cache.time, 'time_ns', return_value=frozen):
            c=self.cache()
            for i in (0,1,0,2):c.read('a',i*B,1)
            before=c.snapshot()['block_hits'];c.read('a',0,1)
        self.assertEqual(c.snapshot()['block_hits'],before+1)
    def test_117_cross_process_like_second_connection_reads_data(self):
        c=self.cache();c.read('a',0,1)
        other=sqlite3.connect(c.dbpath)
        try:
            row=other.execute('SELECT payload,sha FROM blocks').fetchone()
            self.assertEqual(hashlib.sha256(row[0]).hexdigest(),row[1])
        finally:other.close()
    def test_118_large_capacity_with_small_cache_no_count_overflow(self):
        c=self.cache();self.assertLessEqual(c.snapshot()['cache_bytes'],2*B)
        for i in range(8):c.read('a',i*B,1)
        self.assertLessEqual(c.snapshot()['block_count'],2)
    def test_119_read_only_mode_does_not_cache(self):
        c=self.cache(cache_writes=False)
        for _ in range(3):self.assertEqual(c.read('a',0,1),self.contents[:1])
        self.assertEqual(c.snapshot()['block_count'],0)
    def test_120_evicting_hot_old_block_does_not_return_wrong_bytes(self):
        c=self.cache()
        for i in list(range(8))*3:
            self.assertEqual(c.read('a',i*B+10,100),self.contents[i*B+10:i*B+110])

if __name__=='__main__':unittest.main()
