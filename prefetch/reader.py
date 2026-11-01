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


def _worker(ops, hint, stats, lock):
    f, cur, buf = None, None, bytearray(BUF)
    local = {'bytes': 0, 'requests': 0, 'errors': 0, 'fallback': False}
    while True:
        try:
            path, off, n = ops.get_nowait()
        except queue.Empty:
            break
        try:
            if path != cur:
                if f:
                    f.close()
                f, cur = None, path
                f = _open(path, hint, local)
            f.seek(off)
            left = n
            while left > 0:
                got = f.readinto(memoryview(buf)[:min(left, BUF)])
                if not got:
                    break
                left -= got
                local['bytes'] += got
            local['requests'] += 1
        except OSError:
            local['errors'] += 1
            f, cur = None, None
    if f:
        f.close()
    with lock:
        for key in ('bytes', 'requests', 'errors'):
            stats[key] += local[key]
        stats['fallback'] |= local['fallback']


def read_ops(ops, threads=1, sequential_hint=False):
    """ops [(path, offset, length)] -> {'bytes','requests','seconds','fallback','errors'}.
    threads=1 keeps strict op order; N (max 8) runs N workers pulling from the same ordered queue."""
    q = queue.Queue()
    for op in ops:
        q.put(op)
    stats = {'bytes': 0, 'requests': 0, 'errors': 0, 'fallback': False}
    lock = threading.Lock()
    t0 = time.perf_counter()
    ts = [threading.Thread(target=_worker, args=(q, sequential_hint, stats, lock)) for _ in range(max(1, min(threads, 8)))]
    for t in ts:
        t.start()
    for t in ts:
        t.join()
    stats['seconds'] = round(time.perf_counter() - t0, 4)
    return stats
