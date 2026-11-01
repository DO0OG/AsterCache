"""AsterCache v0.7: opted-in transactional, *read-only-source* block cache.

This does not intercept Windows processes. SQLite transactions cover data AND
hash metadata to avoid orphaned .bin/.json pairs. For immutable/trusted test
inputs only: file attributes are not an authoritative filesystem change journal.
"""
from __future__ import annotations
import ctypes
from collections import OrderedDict
import hashlib
import json
import os
from pathlib import Path
import shutil
import sqlite3
import threading
import time

RACY_WINDOW_NS = 2_000_000_000  # files modified this recently may share a timestamp tick with the bytes read
if os.name == 'nt':
    class FileBasicInfo(ctypes.Structure):
        _fields_ = [('CreationTime',ctypes.c_int64), ('LastAccessTime',ctypes.c_int64),
                    ('LastWriteTime',ctypes.c_int64), ('ChangeTime',ctypes.c_int64),
                    ('FileAttributes',ctypes.c_uint32)]
    _GetFileInfo = ctypes.WinDLL('kernel32', use_last_error=True).GetFileInformationByHandleEx
    _GetFileInfo.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p, ctypes.c_uint32]
    _GetFileInfo.restype = ctypes.c_int

class SourceChanged(RuntimeError):
    pass

class SQLiteBlockCache:
    def __init__(self, origin_root, cache_dir, capacity_bytes=32 << 20,
                 block_size=64 << 10, max_request_bytes=8 << 20,
                 min_free_bytes=512 << 20, cache_writes=True):
        for label, value in [('capacity_bytes',capacity_bytes),('block_size',block_size),
                             ('max_request_bytes',max_request_bytes)]:
            if type(value) is not int or value <= 0:
                raise ValueError(f'{label} must be a positive int')
        if block_size < 4096 or block_size > 4 << 20 or block_size & (block_size-1):
            raise ValueError('block_size must be power-of-two in 4KiB..4MiB')
        if type(min_free_bytes) is not int or min_free_bytes < 0:
            raise ValueError('min_free_bytes must be a nonnegative int')
        if type(cache_writes) is not bool:
            raise ValueError('cache_writes must be bool')
        self.root = Path(origin_root).resolve(strict=True)
        if not self.root.is_dir():
            raise ValueError('origin_root must be a directory')
        base = Path(cache_dir).resolve(strict=False)
        if base == self.root or base.is_relative_to(self.root) or self.root.is_relative_to(base):
            raise ValueError('source/cache trees must not overlap')
        self.dir = base / 'astercache-v07-private'
        self.dir.mkdir(parents=True, exist_ok=True)
        self.dir = self.dir.resolve(strict=True)
        if self.dir == self.root or self.dir.is_relative_to(self.root) or self.root.is_relative_to(self.dir):
            raise ValueError('cache directory resolves inside origin')
        self.dbpath = self.dir / 'blocks-v07.sqlite3'
        if self.dbpath.is_symlink():
            raise ValueError('cache database cannot be a symlink')
        self.capacity = capacity_bytes
        self.block_size = block_size
        self.max_request = max_request_bytes
        self.min_free = min_free_bytes
        self.cache_writes = cache_writes
        self.lock = threading.RLock()
        # Defer SQL atime writes: batch recent cache hits into the next insert transaction.
        self._recent_hits = OrderedDict()
        self.closed = False
        self.stats = dict(block_hits=0, block_misses=0, origin_bytes=0,
                          cache_bytes_read=0, cache_bytes_written=0, evictions=0,
                          invalid_blocks=0, skipped_low_space=0, skipped_write_error=0,
                          db_read_errors=0)
        self.conn = sqlite3.connect(self.dbpath, timeout=4, check_same_thread=False)
        try:
            self.conn.execute('PRAGMA busy_timeout=4000')
            self.conn.execute('PRAGMA journal_mode=WAL')
            # WAL+NORMAL: safe against corruption; a power loss may roll back the last commit (cache refills).
            self.conn.execute('PRAGMA synchronous=NORMAL')
            self.conn.execute('PRAGMA trusted_schema=OFF')
            self.conn.execute('''CREATE TABLE IF NOT EXISTS blocks (
                cache_key TEXT PRIMARY KEY, payload BLOB NOT NULL,
                sha TEXT NOT NULL, touched INTEGER NOT NULL,
                byte_count INTEGER NOT NULL CHECK(byte_count > 0))''')
            self.conn.execute('DROP INDEX IF EXISTS ix_blocks_touched')
            self.conn.execute('CREATE INDEX IF NOT EXISTS ix_blocks_lru ON blocks(touched, cache_key, byte_count)')
            self.conn.commit()
        except Exception:
            self.conn.close()
            raise

    def __enter__(self): return self
    def __exit__(self, *_): self.close()
    def close(self):
        with self.lock:
            if not self.closed:
                self.closed = True
                self.conn.close()

    @staticmethod
    def _fingerprint(stream):
        st = os.fstat(stream.fileno())
        fp = [st.st_dev, st.st_ino, st.st_size, st.st_mtime_ns, st.st_ctime_ns]
        if os.name == 'nt':
            import msvcrt
            info = FileBasicInfo()
            h = msvcrt.get_osfhandle(stream.fileno())
            if not _GetFileInfo(ctypes.c_void_p(h), 0, ctypes.byref(info), ctypes.sizeof(info)):
                raise ctypes.WinError(ctypes.get_last_error())
            fp.append(int(info.ChangeTime))
        return fp

    def _source(self, relative):
        if not isinstance(relative, (str, Path)) or not str(relative):
            raise ValueError('relative path required')
        p = Path(relative)
        if p.is_absolute() or '..' in p.parts:
            raise ValueError('absolute or parent-traversing path not allowed')
        actual = (self.root / p).resolve(strict=True)
        if not actual.is_relative_to(self.root) or not actual.is_file():
            raise ValueError('source must be regular file within allowed root')
        return actual

    @staticmethod
    def _path_stat_mismatch(path, fp):
        try:
            s = path.stat()
            n = 4 if os.name == 'nt' else 5  # Windows stat() st_ctime is creation time; ChangeTime is fp[5]
            return [s.st_dev,s.st_ino,s.st_size,s.st_mtime_ns,s.st_ctime_ns][:n] != fp[:n]
        except OSError:
            return True

    def _key(self, actual, fp, block):
        packed = json.dumps([str(actual), fp, block, self.block_size], separators=(',',':'))
        return hashlib.sha256(packed.encode('utf-8')).hexdigest()

    def _lookup(self, key, expected):
        try:
            row = self.conn.execute('SELECT payload, sha, byte_count FROM blocks WHERE cache_key=?', (key,)).fetchone()
            if row is None:
                return None
            data, digest, declared = row
            if declared == expected and len(data) == expected and hashlib.sha256(data).hexdigest() == digest:
                self._recent_hits[key] = time.time_ns()
                self._recent_hits.move_to_end(key)
                if len(self._recent_hits)>512:
                    self._recent_hits.popitem(last=False)
                self.stats['block_hits'] += 1
                self.stats['cache_bytes_read'] += len(data)
                return data
            self.stats['invalid_blocks'] += 1
            # Delete corrupt entry only. If SQLite itself is failing, fail open to source.
            with self.conn:
                self.conn.execute('DELETE FROM blocks WHERE cache_key=?', (key,))
        except (sqlite3.Error, OSError, ValueError, TypeError):
            self.stats['db_read_errors'] += 1
        return None

    def _write_batch(self, pending):
        """One transaction per completed, fingerprint-stable API request.
        On any SSD/DB failure, caller still returns verified source bytes.
        """
        if not pending or not self.cache_writes:
            return
        try:
            max_in = sum(len(data) for _, data in pending)
            if shutil.disk_usage(self.dir).free < self.min_free + 2*max_in + (1 << 20):
                self.stats['skipped_low_space'] += len(pending)
                return
            with self.conn:
                self.conn.execute('BEGIN IMMEDIATE')
                # Refresh LRU for recently-read blocks *only* when another write occurs.
                # This avoids one SSD metadata transaction per cache hit.
                for hot_key, stamp in self._recent_hits.items():
                    self.conn.execute('UPDATE blocks SET touched=? WHERE cache_key=?',(stamp,hot_key))
                # A different writer/process may have committed meanwhile.
                used = self.conn.execute('SELECT COALESCE(SUM(byte_count),0) FROM blocks').fetchone()[0]
                inserted = 0
                evicted = 0
                for key, data in pending:
                    n = len(data)
                    if n > self.capacity:
                        continue
                    present = self.conn.execute('SELECT 1 FROM blocks WHERE cache_key=?', (key,)).fetchone()
                    if present:
                        continue
                    while used+n > self.capacity:
                        victim = self.conn.execute('SELECT cache_key,byte_count FROM blocks ORDER BY touched,cache_key LIMIT 1').fetchone()
                        if victim is None: break
                        self.conn.execute('DELETE FROM blocks WHERE cache_key=?',(victim[0],))
                        used -= victim[1]
                        evicted += 1
                    self.conn.execute('INSERT INTO blocks VALUES (?,?,?,?,?)',
                                      (key,data,hashlib.sha256(data).hexdigest(),time.time_ns(),n))
                    inserted += n
                    used += n
            self._recent_hits.clear()
            self.stats['cache_bytes_written'] += inserted
            self.stats['evictions'] += evicted
        except (sqlite3.Error, OSError):
            self.stats['skipped_write_error'] += len(pending)
            # with conn rolls back transaction. Stats are approximate attempts on failure.

    def read(self, relative, offset, length):
        if type(offset) is not int or offset < 0 or type(length) is not int or not 1 <= length <= self.max_request:
            raise ValueError('invalid offset/length')
        with self.lock:
            if self.closed: raise RuntimeError('cache is closed')
            path = self._source(relative)
            with path.open('rb', buffering=0) as fd:
                before = self._fingerprint(fd)
                if self._source(relative) != path or self._path_stat_mismatch(path, before):
                    raise SourceChanged('source replaced before read')
                if offset+length > before[2]:
                    raise EOFError('range beyond EOF')
                chunks = []
                pending = []
                first = offset // self.block_size
                last = (offset + length - 1) // self.block_size
                for idx in range(first, last+1):
                    expected = min(self.block_size, before[2]-idx*self.block_size)
                    key = self._key(path,before,idx)
                    data = self._lookup(key,expected)
                    if data is None:
                        fd.seek(idx*self.block_size)
                        data = fd.read(expected)
                        if len(data) != expected:
                            raise SourceChanged('source truncated mid-request')
                        self.stats['block_misses'] += 1
                        self.stats['origin_bytes'] += expected
                        pending.append((key,data))
                    chunks.append(data)
                if (before != self._fingerprint(fd)
                    or self._path_stat_mismatch(path, before)):
                    raise SourceChanged('source changed during read')
                result = b''.join(chunks)[offset-first*self.block_size:offset-first*self.block_size+length]
                # Do not admit cache data until fully validated against same source generation.
                newest = max(before[3], before[4])
                if os.name == 'nt':
                    newest = max(newest, (before[5] - 116444736000000000) * 100)  # FILETIME -> Unix ns
                if time.time_ns() - newest < RACY_WINDOW_NS:
                    pending = []  # same-tick rewrites cannot be told apart by fp
                self._write_batch(pending)
                return result

    def snapshot(self):
        with self.lock:
            if self.closed: raise RuntimeError('cache closed')
            result = dict(self.stats)
            try:
                used, n = self.conn.execute('SELECT COALESCE(SUM(byte_count),0), COUNT(*) FROM blocks').fetchone()
                footprint = sum(p.stat().st_size for p in self.dir.glob('blocks-v07.sqlite3*') if p.is_file())
                result.update(cache_bytes=used, block_count=n, on_disk_db_bytes=footprint)
            except sqlite3.Error:
                result.update(cache_bytes=None,block_count=None,on_disk_db_bytes=None)
            return result
