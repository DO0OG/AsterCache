"""E6-0a raw LBA transfer-rate curve: read-only, needs admin. N points over the volume, 512 MiB each, 4 MiB requests, queue depth 4
(one overlapped handle, requests issued in address order). Before each point a 512 MiB block from another point is read to push the
drive cache out. Output JSON (MB/s per point, ratios, Spearman)."""
import argparse, collections, ctypes, json, sys, time
from ctypes import wintypes

MIB = 1 << 20
SECTOR = 4096
GENERIC_READ, SHARE_RW, OPEN_EXISTING, NO_BUFFERING, OVERLAPPED = 0x80000000, 3, 3, 0x20000000, 0x40000000
IOCTL_DISK_GET_LENGTH_INFO = 0x7405C
ERROR_IO_PENDING = 997


class _Ov(ctypes.Structure):
    _fields_ = [('Internal', ctypes.c_size_t), ('InternalHigh', ctypes.c_size_t), ('Offset', wintypes.DWORD),
                ('OffsetHigh', wintypes.DWORD), ('hEvent', ctypes.c_void_p)]


k = ctypes.WinDLL('kernel32', use_last_error=True)
k.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p]
k.CreateFileW.restype = ctypes.c_void_p
k.ReadFile.argtypes = [ctypes.c_void_p, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD), ctypes.POINTER(_Ov)]
k.GetOverlappedResult.argtypes = [ctypes.c_void_p, ctypes.POINTER(_Ov), ctypes.POINTER(wintypes.DWORD), wintypes.BOOL]
k.CreateEventW.argtypes = [ctypes.c_void_p, wintypes.BOOL, wintypes.BOOL, wintypes.LPCWSTR]
k.CreateEventW.restype = ctypes.c_void_p
k.CloseHandle.argtypes = [ctypes.c_void_p]
k.VirtualAlloc.argtypes = [ctypes.c_void_p, ctypes.c_size_t, wintypes.DWORD, wintypes.DWORD]
k.VirtualAlloc.restype = ctypes.c_void_p
k.DeviceIoControl.argtypes = [ctypes.c_void_p, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD,
                              ctypes.POINTER(wintypes.DWORD), ctypes.c_void_p]


def open_raw(volume, overlapped=False):
    flags = NO_BUFFERING | (OVERLAPPED if overlapped else 0)
    h = k.CreateFileW('\\\\.\\' + volume.rstrip('\\'), GENERIC_READ, SHARE_RW, None, OPEN_EXISTING, flags, None)
    if h is None or h == ctypes.c_void_p(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())
    return h


def volume_bytes(volume):
    h = open_raw(volume)
    try:
        out, n = ctypes.c_longlong(0), wintypes.DWORD(0)
        if not k.DeviceIoControl(h, IOCTL_DISK_GET_LENGTH_INFO, None, 0, ctypes.byref(out), 8, ctypes.byref(n), None):
            raise ctypes.WinError(ctypes.get_last_error())
        return out.value
    finally:
        k.CloseHandle(h)


def read_ranges(volume, ranges, qd=4, chunk=4 * MIB):
    """ranges: [(byte_offset, length)] sector aligned, read in the given order, split into `chunk` requests, `qd` overlapped requests
    in flight on one handle (issued in order, the oldest is waited for first). Returns (bytes, seconds, request_sizes)."""
    reqs = []
    for off, n in ranges:
        for o in range(off, off + n, chunk):
            reqs.append((o, min(chunk, off + n - o)))
    h = open_raw(volume, True)
    slots = [(k.VirtualAlloc(None, chunk, 0x3000, 0x04), _Ov(hEvent=k.CreateEventW(None, True, False, None))) for _ in range(qd)]
    free, flying = collections.deque(slots), collections.deque()

    def finish():
        buf, ov, n = flying.popleft()
        got = wintypes.DWORD(0)
        if not k.GetOverlappedResult(h, ctypes.byref(ov), ctypes.byref(got), True) or got.value != n:
            raise OSError('raw read failed: len %d got %d winerror %d' % (n, got.value, ctypes.get_last_error()))
        free.append((buf, ov))

    t0 = time.perf_counter()
    try:
        for o, n in reqs:
            if not free:
                finish()
            buf, ov = free.popleft()
            ov.Offset, ov.OffsetHigh = o & 0xFFFFFFFF, o >> 32
            if not k.ReadFile(h, buf, n, None, ctypes.byref(ov)) and ctypes.get_last_error() != ERROR_IO_PENDING:
                raise ctypes.WinError(ctypes.get_last_error())
            flying.append((buf, ov, n))
        while flying:
            finish()
        sec = time.perf_counter() - t0
    finally:
        k.CloseHandle(h)  # cancels nothing in the normal path; on error the process ends anyway
    return sum(n for _, n in reqs), sec, [n for _, n in reqs]


def spearman(xs, ys):
    def rank(v):
        order = sorted(range(len(v)), key=lambda i: v[i])
        r = [0.0] * len(v)
        i = 0
        while i < len(order):  # average rank for ties
            j = i
            while j + 1 < len(order) and v[order[j + 1]] == v[order[i]]:
                j += 1
            for m in range(i, j + 1):
                r[order[m]] = (i + j) / 2 + 1
            i = j + 1
        return r
    rx, ry = rank(xs), rank(ys)
    n = len(xs)
    mx, my = sum(rx) / n, sum(ry) / n
    num = sum((a - mx) * (b - my) for a, b in zip(rx, ry))
    den = (sum((a - mx) ** 2 for a in rx) * sum((b - my) ** 2 for b in ry)) ** 0.5
    return num / den if den else None


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--volume', default='D:')
    ap.add_argument('--points', type=int, default=20)
    ap.add_argument('--mib', type=int, default=512)
    ap.add_argument('--qd', type=int, default=4)
    ap.add_argument('--chunk-mib', type=int, default=4)
    ap.add_argument('--passes', type=int, default=2, help='pass 2+ walk the points in reverse order; MB/s is the mean over passes')
    ap.add_argument('--out')
    a = ap.parse_args(argv)
    size, span, chunk = volume_bytes(a.volume), a.mib * MIB, a.chunk_mib * MIB
    # 16 MiB margin: the last sectors of a volume are not always readable
    pts = [(size - span - 16 * MIB) * i // (a.points - 1) // SECTOR * SECTOR for i in range(a.points)]
    rows = [dict(i=i, frac=pts[i] / size, offset=pts[i], mbps_passes=[]) for i in range(a.points)]
    for p in range(a.passes):
        for i in (range(a.points) if p % 2 == 0 else reversed(range(a.points))):
            read_ranges(a.volume, [(pts[(i + a.points // 2) % a.points], span)], a.qd, chunk)  # cache flush: far-away block
            n, sec, _ = read_ranges(a.volume, [(pts[i], span)], a.qd, chunk)
            rows[i]['mbps_passes'].append(n / 1e6 / sec)
            print('pass %d point %2d %5.1f%% %7.1f MB/s' % (p + 1, i, rows[i]['frac'] * 100, rows[i]['mbps_passes'][-1]), file=sys.stderr, flush=True)
    for r in rows:
        r['mbps'] = sum(r['mbps_passes']) / len(r['mbps_passes'])
    lo = [r['mbps'] for r in rows if r['frac'] <= 0.10]
    hi = [r['mbps'] for r in rows if r['frac'] >= 0.90]
    res = dict(volume=a.volume, volume_bytes=size, points=a.points, mib_per_point=a.mib, passes=a.passes, request_mib=a.chunk_mib, queue_depth=a.qd,
               rows=rows, mbps_0_10pct=sum(lo) / len(lo), mbps_90_100pct=sum(hi) / len(hi))
    res['ratio_0_10_over_90_100'] = res['mbps_0_10pct'] / res['mbps_90_100pct']
    res['spearman_frac_vs_mbps'] = spearman([r['frac'] for r in rows], [r['mbps'] for r in rows])
    txt = json.dumps(res, indent=1)
    if a.out:
        open(a.out, 'w', encoding='utf-8').write(txt)
    print(txt)


if __name__ == '__main__':
    main()
