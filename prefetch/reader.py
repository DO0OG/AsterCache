"""Sequential/threaded buffered reads that populate the OS file cache. Never writes."""
import ctypes
import msvcrt
import os
import queue
import threading
import time

GENERIC_READ = 0x80000000
SHARE_ALL = 7
OPEN_EXISTING = 3
FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000
INVALID_HANDLE = ctypes.c_void_p(-1).value
BUF = 1 << 20


def _open(path, hint, stats):
    """Read-only handle; SEQUENTIAL_SCAN via CreateFileW, plain open() on any failure."""
    if hint and os.name == 'nt':
        try:
            k = ctypes.WinDLL('kernel32', use_last_error=True)
            k.CreateFileW.restype = ctypes.c_void_p
            k.CreateFileW.argtypes = [ctypes.c_wchar_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p,
                                      ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p]
            h = k.CreateFileW(path, GENERIC_READ, SHARE_ALL, None, OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, None)
            if h not in (None, INVALID_HANDLE):
                try:
                    return os.fdopen(msvcrt.open_osfhandle(h, os.O_RDONLY | os.O_BINARY), 'rb', buffering=0)
                except OSError:
                    k.CloseHandle.argtypes = [ctypes.c_void_p]
                    k.CloseHandle(h)
        except (OSError, AttributeError):
            pass
        stats['fallback'] = True
    return open(path, 'rb', buffering=0)


def _worker(ops, hint, stats, lock, chunk=BUF, reuse=False):
    f, cur, buf, handles = None, None, bytearray(chunk), {}
    local = {'bytes': 0, 'requests': 0, 'errors': 0, 'fallback': False}
    while True:
        try:
            path, off, n = ops.get_nowait()
        except queue.Empty:
            break
        try:
            if path != cur:
                if f and not reuse:
                    f.close()
                f, cur = None, path
                f = handles.get(path) if reuse else None
                if f is None:
                    f = _open(path, hint, local)
                    if reuse:
                        handles[path] = f
            f.seek(off)
            left = n
            while left > 0:
                got = f.readinto(memoryview(buf)[:min(left, chunk)])
                if not got:
                    break
                left -= got
                local['bytes'] += got
            local['requests'] += 1
        except OSError:
            local['errors'] += 1
            if reuse and handles.get(cur):
                handles.pop(cur).close()
            f, cur = None, None
    for h in (handles.values() if reuse else [f] if f else []):
        h.close()
    with lock:
        for key in ('bytes', 'requests', 'errors'):
            stats[key] += local[key]
        stats['fallback'] |= local['fallback']


def read_ops(ops, threads=1, sequential_hint=False, chunk=BUF, reuse=False):
    """ops [(path, offset, length)] -> {'bytes','requests','seconds','fallback','errors'}.
    threads=1 keeps strict op order; N (max 8) runs N workers pulling from the same ordered queue.
    chunk = bytes per read call; reuse = each worker keeps its file handles open until the end."""
    q = queue.Queue()
    for op in ops:
        q.put(op)
    stats = {'bytes': 0, 'requests': 0, 'errors': 0, 'fallback': False}
    lock = threading.Lock()
    t0 = time.perf_counter()
    ts = [threading.Thread(target=_worker, args=(q, sequential_hint, stats, lock, chunk, reuse)) for _ in range(max(1, min(threads, 8)))]
    for t in ts:
        t.start()
    for t in ts:
        t.join()
    stats['seconds'] = round(time.perf_counter() - t0, 4)
    return stats
