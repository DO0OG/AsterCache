"""Replay a trace with plain buffered reads or FILE_FLAG_NO_BUFFERING reads; report latency/throughput."""
import argparse, ctypes, json, time, zlib
from ctypes import wintypes

SECTOR = 4096  # satisfies 512e and 4Kn logical sector sizes
MIB = 1 << 20


class BufferedReader:
    def __init__(self):
        self.files = {}

    def read(self, path, off, n):
        f = self.files.get(path)
        if f is None:
            f = self.files[path] = open(path, 'rb', buffering=0)  # raw ReadFile, OS cache still used
        f.seek(off)
        return f.read(n)

    def close(self):
        for f in self.files.values():
            f.close()


class _Overlapped(ctypes.Structure):
    _fields_ = [('Internal', ctypes.c_size_t), ('InternalHigh', ctypes.c_size_t), ('Offset', wintypes.DWORD),
                ('OffsetHigh', wintypes.DWORD), ('hEvent', wintypes.HANDLE)]


class UnbufferedReader:
    """CreateFileW(FILE_FLAG_NO_BUFFERING); reads are sector aligned and trimmed to the requested range."""
    GENERIC_READ, SHARE_READ, OPEN_EXISTING, NO_BUFFERING = 0x80000000, 1, 3, 0x20000000
    INVALID = ctypes.c_void_p(-1).value

    def __init__(self):
        k = self.k = ctypes.WinDLL('kernel32', use_last_error=True)
        k.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p,
                                  wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
        k.CreateFileW.restype = ctypes.c_void_p
        k.ReadFile.argtypes = [ctypes.c_void_p, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD),
                               ctypes.POINTER(_Overlapped)]
        k.CloseHandle.argtypes = [ctypes.c_void_p]
        k.VirtualAlloc.argtypes = [ctypes.c_void_p, ctypes.c_size_t, wintypes.DWORD, wintypes.DWORD]
        k.VirtualAlloc.restype = ctypes.c_void_p
        k.VirtualFree.argtypes = [ctypes.c_void_p, ctypes.c_size_t, wintypes.DWORD]
        self.handles, self.buf, self.cap = {}, None, 0

    def _buffer(self, need):
        if need > self.cap:
            if self.buf:
                self.k.VirtualFree(self.buf, 0, 0x8000)
            self.cap = need
            self.buf = self.k.VirtualAlloc(None, need, 0x3000, 0x04)  # page aligned
            if not self.buf:
                raise ctypes.WinError(ctypes.get_last_error())

    def _open(self, path):
        h = self.k.CreateFileW(path, self.GENERIC_READ, self.SHARE_READ, None, self.OPEN_EXISTING, self.NO_BUFFERING, None)
        if h is None or h == self.INVALID:
            raise ctypes.WinError(ctypes.get_last_error())
        return h

    def read(self, path, off, n):
        h = self.handles.get(path)
        if h is None:
            h = self.handles[path] = self._open(path)
        start = off - off % SECTOR
        length = (off + n - start + SECTOR - 1) // SECTOR * SECTOR
        self._buffer(length)
        ov = _Overlapped(Offset=start & 0xFFFFFFFF, OffsetHigh=start >> 32)
        got = wintypes.DWORD(0)
        if not self.k.ReadFile(h, self.buf, length, ctypes.byref(got), ctypes.byref(ov)):
            if ctypes.get_last_error() != 38:  # ERROR_HANDLE_EOF -> 0 bytes
                raise ctypes.WinError(ctypes.get_last_error())
        data = ctypes.string_at(self.buf, got.value)
        return data[off - start:off - start + n]

    def close(self):
        for h in self.handles.values():
            self.k.CloseHandle(h)
        if self.buf:
            self.k.VirtualFree(self.buf, 0, 0x8000)
            self.buf = None


def _pct(sorted_lat, q):
    return sorted_lat[min(len(sorted_lat) - 1, max(0, -(-q * len(sorted_lat) // 100) - 1))]


def replay(events, unbuffered=False):
    """total_s = sum of per-request read latencies (file open on first use is included, crc is not)."""
    r = UnbufferedReader() if unbuffered else BufferedReader()
    lat, crc, nbytes = [], 0, 0
    missing, skipped, skipped_bytes = set(), 0, 0  # files the trace names but the disk no longer has
    try:
        for e in events:
            t = time.perf_counter()
            try:
                d = r.read(e['path'], e['offset'], e['length'])
            except (FileNotFoundError, PermissionError):
                missing.add(e['path'])
                skipped += 1
                skipped_bytes += e['length']
                continue
            lat.append(time.perf_counter() - t)
            crc = zlib.crc32(d, crc)
            nbytes += len(d)
    finally:
        r.close()
    total, s = sum(lat), sorted(lat)
    return dict(mode='unbuffered' if unbuffered else 'buffered', requests=len(lat), bytes=nbytes, crc32=crc,
                total_s=total, throughput_mib_s=nbytes / MIB / total if total else None,
                p50_ms=_pct(s, 50) * 1e3, p95_ms=_pct(s, 95) * 1e3, p99_ms=_pct(s, 99) * 1e3,
                missing_files=len(missing), skipped_requests=skipped, skipped_bytes=skipped_bytes)


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--trace', required=True, help='trace.json (list of {path,offset,length,cost})')
    ap.add_argument('--unbuffered', action='store_true')
    a = ap.parse_args(argv)
    print(json.dumps(replay(json.load(open(a.trace)), a.unbuffered), indent=1))


if __name__ == '__main__':
    main()
