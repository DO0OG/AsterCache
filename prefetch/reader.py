"""Sequential/threaded buffered reads that populate the OS file cache. Never writes."""
import collections
import ctypes
import mmap
import msvcrt
import os
import queue
import threading
import time
from ctypes import wintypes

GENERIC_READ = 0x80000000
SHARE_ALL = 7
OPEN_EXISTING = 3
FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000
INVALID_HANDLE = ctypes.c_void_p(-1).value
BUF = 1 << 20
METHODS = ('read', 'pvm', 'pvm-file', 'overlapped', 'mmap-touch')
PAGE = 4096
FILE_FLAG_OVERLAPPED = 0x40000000
ERROR_IO_PENDING, ERROR_HANDLE_EOF = 997, 38


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


class _Overlapped(ctypes.Structure):
    _fields_ = [('Internal', ctypes.c_size_t), ('InternalHigh', ctypes.c_size_t), ('Offset', wintypes.DWORD),
                ('OffsetHigh', wintypes.DWORD), ('hEvent', ctypes.c_void_p)]


class _Range(ctypes.Structure):  # WIN32_MEMORY_RANGE_ENTRY
    _fields_ = [('VirtualAddress', ctypes.c_void_p), ('NumberOfBytes', ctypes.c_size_t)]


def _k32():
    k = ctypes.WinDLL('kernel32', use_last_error=True)
    vp, u32 = ctypes.c_void_p, ctypes.c_uint32
    for name, res, args in (
            ('CreateFileW', vp, [ctypes.c_wchar_p, u32, u32, vp, u32, u32, vp]),
            ('CloseHandle', ctypes.c_int, [vp]),
            ('CreateFileMappingW', vp, [vp, vp, u32, u32, u32, ctypes.c_wchar_p]),
            ('MapViewOfFile', vp, [vp, u32, u32, u32, ctypes.c_size_t]),
            ('UnmapViewOfFile', ctypes.c_int, [vp]),
            ('GetCurrentProcess', vp, []),
            ('CreateEventW', vp, [vp, ctypes.c_int, ctypes.c_int, vp]),
            ('ReadFile', ctypes.c_int, [vp, vp, u32, vp, ctypes.POINTER(_Overlapped)]),
            ('GetOverlappedResult', ctypes.c_int, [vp, ctypes.POINTER(_Overlapped), ctypes.POINTER(u32), ctypes.c_int])):
        f = getattr(k, name)
        f.restype, f.argtypes = res, args
    return k


def _create(k, path, flags=0):
    h = k.CreateFileW(path, GENERIC_READ, SHARE_ALL, None, OPEN_EXISTING, flags, None)
    if h in (None, INVALID_HANDLE):
        raise ctypes.WinError(ctypes.get_last_error())
    return h


def _map(k, path):
    """Read-only view of the whole file; the view keeps the section alive after both handles are closed."""
    h = _create(k, path)
    try:
        m = k.CreateFileMappingW(h, None, 2, 0, 0, None)  # PAGE_READONLY
        if not m:
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            base = k.MapViewOfFile(m, 4, 0, 0, 0)  # FILE_MAP_READ
            if not base:
                raise ctypes.WinError(ctypes.get_last_error())
        finally:
            k.CloseHandle(m)
    finally:
        k.CloseHandle(h)
    return base


def _clamped(ops):
    """(path, offset, length) with length cut at the current file size; empty ranges dropped."""
    sizes = {}
    for p, o, n in ops:
        if p not in sizes:
            sizes[p] = os.path.getsize(p)
        n = min(n, sizes[p] - o)
        if n > 0:
            yield p, o, n


def _read_pvm(ops, per_file, stats):
    """Map every file, then PrefetchVirtualMemory the hot ranges: one call for all (zone order), or one per file."""
    k = _k32()
    pvm = k.PrefetchVirtualMemory
    pvm.restype, pvm.argtypes = ctypes.c_int, [ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(_Range), ctypes.c_uint32]
    views, rows = {}, []  # rows: (path, range) in op order
    try:
        for p, o, n in _clamped(ops):
            if p not in views:
                views[p] = _map(k, p)
            a = o - o % PAGE
            rows.append((p, _Range(views[p] + a, o + n - a), n))
        groups = {}
        for p, r, n in rows:
            groups.setdefault(p if per_file else None, []).append((r, n))
        for g in groups.values():
            rs = [r for r, _ in g]
            arr = (_Range * len(rs))(*rs)
            if pvm(k.GetCurrentProcess(), len(rs), arr, 0):
                stats['bytes'] += sum(n for _, n in g)
                stats['requests'] += len(rs)
            else:
                stats['errors'] += 1
    except (OSError, AttributeError):
        stats['errors'] += 1
    finally:
        for v in views.values():
            k.UnmapViewOfFile(v)


def _read_touch(ops, stats):
    """Map each file and read one byte per page: page-fault driven read, the floor for the mapped-file route."""
    maps = {}
    try:
        for p, o, n in _clamped(ops):
            try:
                if p not in maps:
                    with open(p, 'rb') as f:
                        maps[p] = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
                maps[p][o - o % PAGE:o + n:PAGE]  # the stepped slice touches each page, result is dropped
                stats['bytes'] += n
                stats['requests'] += 1
            except (OSError, ValueError):
                stats['errors'] += 1
    finally:
        for m in maps.values():
            m.close()


def _read_overlapped(ops, depth, chunk, stats, sink=None):
    """Buffered overlapped ReadFile with `depth` requests in flight, issued in op order (chunk-sized pieces),
    waited oldest-first. sink(path, offset, data) sees each completed piece (tests)."""
    k = _k32()
    handles, events, pending = {}, [], collections.deque()
    free = []
    try:
        for _ in range(depth):
            ev = k.CreateEventW(None, 1, 0, None)
            events.append(ev)
            free.append([_Overlapped(hEvent=ev), (ctypes.c_char * chunk)(), None])

        def finish():
            s = pending.popleft()
            got = ctypes.c_uint32(0)
            if k.GetOverlappedResult(s[2][0], ctypes.byref(s[0]), ctypes.byref(got), 1):
                stats['bytes'] += got.value
                stats['requests'] += 1
                if sink:
                    sink(s[2][1], s[2][2], s[1].raw[:got.value])
            elif ctypes.get_last_error() != ERROR_HANDLE_EOF:
                stats['errors'] += 1
            free.append(s)

        for p, o, n in ops:
            if p not in handles:
                try:
                    handles[p] = _create(k, p, FILE_FLAG_OVERLAPPED)
                except OSError:
                    handles[p] = None
            h = handles[p]
            while n > 0:
                m = min(n, chunk)
                if h is None:
                    stats['errors'] += 1
                    break
                if not free:
                    finish()
                s = free.pop()
                s[0].Internal = s[0].InternalHigh = 0
                s[0].Offset, s[0].OffsetHigh = o & 0xFFFFFFFF, o >> 32
                s[2] = (h, p, o)
                if k.ReadFile(h, s[1], m, None, ctypes.byref(s[0])) or ctypes.get_last_error() == ERROR_IO_PENDING:
                    pending.append(s)
                else:
                    stats['errors'] += 1
                    free.append(s)
                o, n = o + m, n - m
        while pending:
            finish()
    finally:
        while pending:  # an exception left reads in flight: they must finish before the buffers go away
            finish()
        for h in handles.values():
            if h is not None:
                k.CloseHandle(h)
        for ev in events:
            k.CloseHandle(ev)


def read_ops(ops, threads=1, sequential_hint=False, chunk=BUF, reuse=False, method='read'):
    """ops [(path, offset, length)] -> {'bytes','requests','seconds','fallback','errors'}.
    threads=1 keeps strict op order; N (max 8) runs N workers pulling from the same ordered queue.
    chunk = bytes per read call; reuse = each worker keeps its file handles open until the end.
    method: read (default) | pvm | pvm-file | mmap-touch | overlapped (threads = requests in flight, up to 64)."""
    if method not in METHODS:
        raise ValueError('unknown method: %r' % (method,))
    if method != 'read':
        stats = {'bytes': 0, 'requests': 0, 'errors': 0, 'fallback': False}
        t0 = time.perf_counter()
        if method == 'overlapped':
            _read_overlapped(ops, max(1, min(threads, 64)), chunk, stats)
        elif method == 'mmap-touch':
            _read_touch(ops, stats)
        else:
            _read_pvm(ops, method == 'pvm-file', stats)
        stats['seconds'] = round(time.perf_counter() - t0, 4)
        return stats
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
