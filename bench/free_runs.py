"""E6-2 helper: free-space runs of an NTFS volume from FSCTL_GET_VOLUME_BITMAP (read only, admin). Lists the lowest-LCN runs >= --min-mib
and the largest runs, with their position as % of the volume. Same call pattern as relayout/mover.py (1 MiB pieces, GENERIC_READ)."""
import argparse, ctypes, json
from ctypes import wintypes
from bench.lba_curve import k

FSCTL_GET_VOLUME_BITMAP = 0x9006F
ERROR_MORE_DATA = 234


def free_runs(volume):
    path = '\\\\.\\' + volume.rstrip('\\')
    h = k.CreateFileW(path, 0x80000000, 7, None, 3, 0, None)
    if h is None or h == ctypes.c_void_p(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())
    data, start, total, chunk = bytearray(), 0, None, 1 << 20
    try:
        while True:  # start LCN stays a multiple of 8
            inp, buf, n = ctypes.c_longlong(start), ctypes.create_string_buffer(16 + chunk), wintypes.DWORD(0)
            ok = k.DeviceIoControl(h, FSCTL_GET_VOLUME_BITMAP, ctypes.byref(inp), 8, buf, len(buf), ctypes.byref(n), None)
            err = ctypes.get_last_error()
            if not ok and err != ERROR_MORE_DATA:
                raise ctypes.WinError(err)
            size = int.from_bytes(buf.raw[8:16], 'little')
            payload = buf.raw[16:n.value]
            if total is None:
                total = start + size
            if ok:
                payload = payload[:(size + 7) // 8]
            data += payload
            start += len(payload) * 8
            if ok or start >= total or not payload:
                break
    finally:
        k.CloseHandle(h)
    import numpy as np  # only this helper needs it
    used = np.unpackbits(np.frombuffer(bytes(data), np.uint8), bitorder='little')[:total]  # 1 = used
    edge = np.diff(np.concatenate(([1], used, [1])).astype(np.int8))  # -1 where a free run starts, +1 where it ends
    starts, ends = np.flatnonzero(edge == -1), np.flatnonzero(edge == 1)
    free_runs.used = used  # kept for --points
    return total, [(int(s), int(e - s)) for s, e in zip(starts, ends)]


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--volume', default='D:')
    ap.add_argument('--min-mib', type=int, default=256)
    ap.add_argument('--points', help='lba_curve.json: also report the used fraction of each measured 512 MiB point')
    ap.add_argument('--out')
    a = ap.parse_args(argv)
    nbits, runs = free_runs(a.volume)
    need = a.min_mib * 256  # 4 KiB clusters
    big = [(lc, n) for lc, n in runs if n >= need]
    row = lambda r: dict(lcn=r[0], pct=r[0] / nbits * 100, mib=r[1] / 256)
    res = dict(volume=a.volume, total_clusters=nbits, free_clusters=sum(n for _, n in runs), free_runs=len(runs),
               runs_ge_min=len(big), lowest=[row(r) for r in big[:15]], largest=[row(r) for r in sorted(big, key=lambda r: -r[1])[:10]],
               lowest_ge_2gib=next((row(r) for r in runs if r[1] >= 2048 * 256), None))
    if a.points:
        pj = json.load(open(a.points, encoding='utf-8'))
        span = pj['mib_per_point'] * 256
        res['points_used_fraction'] = [dict(i=r['i'], pct=r['frac'] * 100, mbps=r['mbps'],
                                            used=float(free_runs.used[r['offset'] // 4096:r['offset'] // 4096 + span].mean())) for r in pj['rows']]
    if a.out:
        open(a.out, 'w', encoding='utf-8').write(json.dumps(res, indent=1))
    print(json.dumps(res, indent=1))


if __name__ == '__main__':
    main()
