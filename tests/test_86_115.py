"""30 additional deterministic checks: test numbers 86..115.

These tests verify the opted-in API and SQLite contents using real temp files.
They do NOT verify actual HDD/SSD hardware or transparent NTFS interception.
"""
import concurrent.futures
import hashlib
import os
from pathlib import Path
import random
import sqlite3
import tempfile
import threading
import unittest
from unittest.mock import patch
import sys
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from sqlite_cache import SQLiteBlockCache, SourceChanged
from session_planner import plan_sessions,coverage
B = 65536

class Validation86_115(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        racy = patch('sqlite_cache.RACY_WINDOW_NS', 0); racy.start(); self.addCleanup(racy.stop)
        self.root = Path(self.tmp.name)/'HDD'; self.root.mkdir()
        self.ssd = Path(self.tmp.name)/'SSD'
        self.data = random.Random(42).randbytes(B*12+111)
        self.path = self.root/'test.bin'; self.path.write_bytes(self.data)
        self.to_close = []
    def cache(self, **kwargs):
        c=SQLiteBlockCache(self.root,self.ssd,capacity_bytes=6*B,min_free_bytes=0,**kwargs)
        self.addCleanup(c.close)
        return c
    def test_86_exact_bytes_first_read(self):
        c=self.cache();self.assertEqual(c.read('test.bin',1,250),self.data[1:251]);self.assertEqual(c.snapshot()['block_misses'],1)
    def test_87_overlap_different_lengths(self):
        c=self.cache();c.read('test.bin',0,20000);self.assertEqual(c.read('test.bin',300,321),self.data[300:621]);self.assertEqual(c.snapshot()['block_hits'],1)
    def test_88_cross_boundary(self):
        c=self.cache();self.assertEqual(c.read('test.bin',B-5,12),self.data[B-5:B+7])
    def test_89_last_partial_block(self):
        c=self.cache();self.assertEqual(c.read('test.bin',len(self.data)-111,111),self.data[-111:])
    def test_90_past_eof_fails(self):
        c=self.cache()
        with self.assertRaises(EOFError):c.read('test.bin',len(self.data),1)
    def test_91_oversized_read_rejected(self):
        c=self.cache()
        with self.assertRaises(ValueError):c.read('test.bin',0,9<<20)
    def test_92_bool_offsets_rejected(self):
        c=self.cache()
        for a,b in [(True,3),(0,True),(0,0),(-1,10)]:
            with self.assertRaises(ValueError):c.read('test.bin',a,b)
    def test_93_non_power_of_two_block(self):
        with self.assertRaises(ValueError):self.cache(block_size=5000)
    def test_94_overlap_directories_rejected(self):
        with self.assertRaises(ValueError):SQLiteBlockCache(self.root,self.root/'something')
    def test_95_parent_traversal_denied(self):
        c=self.cache()
        with self.assertRaises(ValueError):c.read('../outside',0,1)
    def test_96_symlink_to_outside_denied(self):
        extra=Path(self.tmp.name)/'outside.bin';extra.write_bytes(b'a')
        try:(self.root/'link').symlink_to(extra)
        except OSError:self.skipTest('symlink unavailable')
        c=self.cache()
        with self.assertRaises(ValueError):c.read('link',0,1)
    def test_97_reopen_persists(self):
        c=self.cache();c.read('test.bin',0,64);c.close();d=self.cache();d.read('test.bin',4,32);self.assertEqual(d.snapshot()['block_hits'],1)
    def test_98_byte_capacity(self):
        c=self.cache()
        for i in range(12):c.read('test.bin',B*i,50)
        self.assertLessEqual(c.snapshot()['cache_bytes'],B*6)
    def test_99_evicted_block_refetch(self):
        c=SQLiteBlockCache(self.root,self.ssd,capacity_bytes=B,min_free_bytes=0);self.addCleanup(c.close)
        c.read('test.bin',0,30);c.read('test.bin',B,30);c.read('test.bin',0,30)
        self.assertEqual(c.snapshot()['block_misses'],3)
    def test_100_corrupted_payload_falls_back(self):
        c=self.cache();c.read('test.bin',0,100)
        with c.conn:c.conn.execute('UPDATE blocks SET payload=?',(b'X'*B,))
        self.assertEqual(c.read('test.bin',0,100),self.data[:100]);self.assertEqual(c.snapshot()['invalid_blocks'],1)
    def test_101_corrupted_digest_falls_back(self):
        c=self.cache();c.read('test.bin',0,100)
        with c.conn:c.conn.execute('UPDATE blocks SET sha=?',('BAD',))
        self.assertEqual(c.read('test.bin',0,100),self.data[:100])
    def test_102_missing_entry_falls_back(self):
        c=self.cache();c.read('test.bin',0,100)
        with c.conn:c.conn.execute('DELETE FROM blocks')
        self.assertEqual(c.read('test.bin',0,100),self.data[:100]);self.assertEqual(c.snapshot()['block_misses'],2)
    def test_103_mtime_change_new_generation(self):
        c=self.cache();c.read('test.bin',0,10);self.path.write_bytes(b'Z'+self.data[1:]);self.assertEqual(c.read('test.bin',0,10),b'Z'+self.data[1:10])
    def test_104_restore_mtime_still_invalidate(self):
        c=self.cache();c.read('test.bin',0,10);st=self.path.stat();self.path.write_bytes(b'Z'+self.data[1:]);os.utime(self.path,ns=(st.st_atime_ns,st.st_mtime_ns));self.assertEqual(c.read('test.bin',0,1),b'Z')
    def test_105_atomic_rename_changed_generation(self):
        c=self.cache();c.read('test.bin',0,1);p=self.root/'new';p.write_bytes(b'Y'*len(self.data));p.replace(self.path);self.assertEqual(c.read('test.bin',0,1),b'Y')
    def test_106_truncation_rejected(self):
        c=self.cache();self.path.write_bytes(b'T')
        with self.assertRaises(EOFError):c.read('test.bin',10,1)
    def test_107_growth_invalidates(self):
        c=self.cache();c.read('test.bin',0,1);self.path.write_bytes(b'K'*len(self.data)+b'Q');self.assertEqual(c.read('test.bin',0,1),b'K')
    def test_108_mid_read_change_rejected(self):
        c=self.cache();orig=c._lookup;flag=[0]
        def hook(key,expected):
            result=orig(key,expected)
            if not flag[0]:
                flag[0]=1
                with self.path.open('r+b') as f:f.seek(0);f.write(b'Q'*B)
            return result
        c._lookup=hook
        with self.assertRaises(SourceChanged):c.read('test.bin',0,2*B)
        self.assertEqual(c.snapshot()['block_count'],0)
    def test_109_ssd_insert_fails_open_to_source(self):
        c=self.cache()
        with patch('sqlite_cache.shutil.disk_usage',side_effect=OSError('fake disk failure')):
            self.assertEqual(c.read('test.bin',0,10),self.data[:10])
    def test_110_low_space_skips_write(self):
        c=self.cache()
        usage=type('D',(),{'free':10})()
        with patch('sqlite_cache.shutil.disk_usage',return_value=usage):
            self.assertEqual(c.read('test.bin',0,10),self.data[:10])
        self.assertEqual(c.snapshot()['block_count'],0)
    def test_111_read_db_error_falls_back(self):
        c=self.cache();c.read('test.bin',0,30)
        with patch.object(c,'_lookup',return_value=None):
            self.assertEqual(c.read('test.bin',0,30),self.data[:30])
    def test_112_parallel_thread_reads_correct(self):
        c=self.cache()
        rng=random.Random(123)
        jobs=[(rng.randrange(0,len(self.data)-200),rng.randrange(1,200)) for _ in range(150)]
        with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
            out=list(pool.map(lambda pair:c.read('test.bin',*pair),jobs))
        self.assertEqual(out,[self.data[o:o+n] for o,n in jobs])
    def test_113_two_sqlite_cache_handles_consistent(self):
        c=self.cache();d=self.cache()
        c.read('test.bin',0,10);self.assertEqual(d.read('test.bin',2,5),self.data[2:7]);self.assertEqual(d.snapshot()['block_hits'],1)
    def test_114_aborted_sqlite_transaction_no_partial_blob(self):
        c=self.cache()
        try:
            with c.conn:
                c.conn.execute('INSERT INTO blocks VALUES (?,?,?,?,?)',('a',b'a','a',1,1))
                raise RuntimeError('simulated crash before commit')
        except RuntimeError:pass
        self.assertEqual(c.snapshot()['block_count'],0)
    def test_115_planner_heldout_and_budget(self):
        p='asset.bin';mk=lambda n:{'path':p,'offset':B*n,'length':B,'cost':8.}
        plan=plan_sessions([[mk(2),mk(88)],[mk(2),mk(99)]],B)
        self.assertEqual(plan['selected_blocks'],1)
        self.assertEqual(coverage(plan,[mk(2),mk(99)])['covered_blocks'],1)

if __name__=='__main__':unittest.main()
