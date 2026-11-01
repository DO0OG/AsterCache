"""Experimental block-addressed persistent read-through cache. API opt-in only.

NO transparent acceleration of existing applications, NO Windows kernel driver.
Source content safety is bounded by file stat change detection, NOT authoritative
coherence for hostile or concurrent writes. Prefer immutable private test files.
Concurrent *processes* must never write the same cache dir; exclusive lock held
for lifetime, with an intentionally manual stale lock recovery policy.
"""
from __future__ import annotations
import hashlib
import json
import os
import shutil
from pathlib import Path
import tempfile
import threading

class CacheBusy(RuntimeError): pass

class BlockCache:
    def __init__(self, origin_root, cache_dir, capacity_bytes=32<<20, block_size=64<<10,
                 max_request_bytes=8<<20, min_free_bytes=512<<20):
        for n,v in [('capacity_bytes',capacity_bytes),('block_size',block_size),('max_request_bytes',max_request_bytes)]:
            if type(v) is not int or v<=0: raise ValueError(f'{n} must be positive integer')
        if block_size < 4096 or block_size > (4<<20) or block_size & (block_size-1):
            raise ValueError('block_size must be power-of-two 4KiB..4MiB')
        if type(min_free_bytes) is not int or min_free_bytes<0:
            raise ValueError('min_free_bytes must be nonnegative integer')
        self.min_free = min_free_bytes
        self.root = Path(origin_root).resolve(strict=True)
        if not self.root.is_dir(): raise ValueError('source must be directory')
        base = Path(cache_dir).resolve(strict=False)
        if base == self.root or base.is_relative_to(self.root) or self.root.is_relative_to(base):
            raise ValueError('source and cache must be separate trees')
        self.dir = base / "astercache-v06-private"
        self.dir.mkdir(parents=True, exist_ok=True)
        self.dir = self.dir.resolve(strict=True)
        self.capacity = capacity_bytes
        self.block_size = block_size
        self.max_request = max_request_bytes
        self.lock = threading.RLock()
        self._lock_file = self.dir/'.astercache-v06.lock'
        try:
            fd=os.open(self._lock_file,os.O_CREAT|os.O_EXCL|os.O_WRONLY,0o600)
        except FileExistsError as e:
            raise CacheBusy('cache directory is held or has stale lock: inspect and remove manually only when inactive') from e
        os.write(fd, str(os.getpid()).encode());os.close(fd)
        self.closed = False
        self.stats = dict(block_hits=0,block_misses=0,origin_bytes=0,ssd_bytes=0,
                          cache_bytes_written=0,corruptions=0,evictions=0,skipped_low_space=0)
        try:
            self._cleanup()
            self._evict()
        except Exception:
            self.close()
            raise
    def close(self):
        with self.lock:
            if not self.closed:
                self.closed=True
                self._lock_file.unlink(missing_ok=True)
    def __enter__(self): return self
    def __exit__(self,*exc): self.close()
    def _check_open(self):
        if self.closed: raise RuntimeError('cache is closed')
    def _source(self,relative):
        p=(self.root/relative).resolve(strict=True)
        if not p.is_relative_to(self.root) or not p.is_file():
            raise ValueError('file outside allowed root or not regular')
        return p
    @staticmethod
    def _fp(st):
        return [st.st_dev,st.st_ino,st.st_size,st.st_mtime_ns,st.st_ctime_ns]
    @classmethod
    def _handle_fp(cls,stream):
        result=cls._fp(os.fstat(stream.fileno()))
        if os.name=='nt':
            # st_ctime_ns reports *creation* time on Windows Python 3.12+.
            # Include NTFS file ChangeTime from the open HANDLE where available.
            import ctypes
            import msvcrt
            class FileBasicInfo(ctypes.Structure):
                _fields_=[('CreationTime',ctypes.c_int64),('LastAccessTime',ctypes.c_int64),
                          ('LastWriteTime',ctypes.c_int64),('ChangeTime',ctypes.c_int64),
                          ('FileAttributes',ctypes.c_uint32)]
            api=ctypes.WinDLL('kernel32',use_last_error=True).GetFileInformationByHandleEx
            api.argtypes=[ctypes.c_void_p,ctypes.c_int,ctypes.c_void_p,ctypes.c_uint32]
            api.restype=ctypes.c_int
            info=FileBasicInfo()
            handle=msvcrt.get_osfhandle(stream.fileno())
            if not api(ctypes.c_void_p(handle),0,ctypes.byref(info),ctypes.sizeof(info)):
                raise ctypes.WinError(ctypes.get_last_error())
            result.append(int(info.ChangeTime))
        return result
    def _key(self,path,fp,index):
        spec=json.dumps([str(path),fp,index,self.block_size],separators=(',',':'))
        return hashlib.sha256(spec.encode('utf-8')).hexdigest()
    def _paths(self,key): return self.dir/(key+'.bin'),self.dir/(key+'.json')
    def _cleanup(self):
        for p in self.dir.glob('.astertmp_*'):p.unlink(missing_ok=True)
        for blob in self.dir.glob('*.bin'):
            if not (self.dir/(blob.stem+'.json')).exists():blob.unlink(missing_ok=True)
        for meta in self.dir.glob('*.json'):
            if not (self.dir/(meta.stem+'.bin')).exists():meta.unlink(missing_ok=True)
    def _evict(self):
        blobs=list(self.dir.glob('*.bin'))
        used=sum(p.stat().st_size for p in blobs)
        for p in sorted(blobs,key=lambda x:x.stat().st_mtime_ns):
            if used<=self.capacity:break
            n=p.stat().st_size
            p.unlink(missing_ok=True)
            (self.dir/(p.stem+'.json')).unlink(missing_ok=True)
            used-=n
            self.stats['evictions']+=1
    def _atomic(self,p,data):
        fd,tmp=tempfile.mkstemp(prefix='.astertmp_',dir=self.dir)
        try:
            with os.fdopen(fd,'wb') as f:
                f.write(data);f.flush();os.fsync(f.fileno())
            os.replace(tmp,p)
        finally:
            if os.path.exists(tmp):os.unlink(tmp)
    def _block(self,origin,path,fp,index):
        expected=min(self.block_size,fp[2]-index*self.block_size)
        if expected<=0:raise EOFError('block past EOF')
        key=self._key(path,fp,index)
        blob,meta=self._paths(key)
        if blob.is_file() and meta.is_file():
            try:
                info=json.loads(meta.read_text(encoding='utf-8'))
                data=blob.read_bytes()
                if (info.get('key')==key and info.get('sha256')==hashlib.sha256(data).hexdigest()
                     and info.get('size')==expected and len(data)==expected):
                    os.utime(blob,None)
                    self.stats['block_hits']+=1
                    self.stats['ssd_bytes']+=len(data)
                    return data
                self.stats['corruptions']+=1
            except (OSError,ValueError,TypeError):
                self.stats['corruptions']+=1
            blob.unlink(missing_ok=True);meta.unlink(missing_ok=True)
        origin.seek(index*self.block_size)
        data=origin.read(expected)
        if len(data)!=expected:raise RuntimeError('source truncated during read')
        self.stats['block_misses']+=1
        self.stats['origin_bytes']+=len(data)
        if expected<=self.capacity and shutil.disk_usage(self.dir).free >= self.min_free+2*expected:
            self._atomic(blob,data)
            self._atomic(meta,json.dumps({'key':key,'sha256':hashlib.sha256(data).hexdigest(),
                                           'size':expected},separators=(',',':')).encode())
            self.stats['cache_bytes_written']+=len(data)
            self._evict()
        elif expected<=self.capacity:
            self.stats['skipped_low_space']+=1
        return data
    def read(self,relative,offset,length):
        if type(offset) is not int or offset<0 or type(length) is not int or length<1 or length>self.max_request:
            raise ValueError('invalid read request')
        with self.lock:
            self._check_open()
            path=self._source(relative)
            with path.open('rb',buffering=0) as origin:
                fp=self._handle_fp(origin)
                if fp[:5]!=self._fp(path.stat()) or self._source(relative)!=path:
                    raise RuntimeError('source changed before read')
                if offset+length>fp[2]:raise EOFError('range past EOF')
                first=offset//self.block_size
                last=(offset+length-1)//self.block_size
                chunks=[]
                for index in range(first,last+1):
                    chunks.append(self._block(origin,path,fp,index))
                if fp!=self._handle_fp(origin) or self._source(relative)!=path:
                    raise RuntimeError('source changed during read')
                start=offset-first*self.block_size
                return b''.join(chunks)[start:start+length]
    def snapshot(self):
        with self.lock:
            self._check_open()
            d=dict(self.stats)
            d['cache_bytes']=sum(p.stat().st_size for p in self.dir.glob('*.bin'))
            d['block_count']=len(list(self.dir.glob('*.bin')))
            return d
