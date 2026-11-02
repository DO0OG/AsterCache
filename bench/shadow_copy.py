"""Build a sparse shadow of the files a trace reads: only the 64 KiB blocks the trace touches are copied, the rest of
every file stays a hole. Replaying the rewritten trace on the shadow (SSD) gives the "same reads on an SSD" baseline
without copying the whole game."""
import argparse, ctypes, json, os, time
from ctypes import wintypes

BLOCK = 65536
CHUNK = 8 << 20
FSCTL_SET_SPARSE = 0x900C4


def _set_sparse(f):
    """Mark an open file sparse. Returns False when the file system refuses (the file is then simply dense)."""
    import msvcrt
    k = ctypes.WinDLL('kernel32', use_last_error=True)
    k.DeviceIoControl.argtypes = [ctypes.c_void_p, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD, ctypes.c_void_p,
                                  wintypes.DWORD, ctypes.POINTER(wintypes.DWORD), ctypes.c_void_p]
    got = wintypes.DWORD(0)
    return bool(k.DeviceIoControl(msvcrt.get_osfhandle(f.fileno()), FSCTL_SET_SPARSE, None, 0, None, 0,
                                  ctypes.byref(got), None))


def _set_size(f, size):
    """SetEndOfFile at size. File.truncate() would extend by writing zeros and allocate every cluster, defeating sparse."""
    import msvcrt
    k = ctypes.WinDLL('kernel32', use_last_error=True)
    k.SetFilePointerEx.argtypes = [ctypes.c_void_p, ctypes.c_longlong, ctypes.c_void_p, wintypes.DWORD]
    k.SetEndOfFile.argtypes = [ctypes.c_void_p]
    h = msvcrt.get_osfhandle(f.fileno())
    f.flush()
    if not (k.SetFilePointerEx(h, size, None, 0) and k.SetEndOfFile(h)):
        raise ctypes.WinError(ctypes.get_last_error())


def allocated_bytes(path):
    """Bytes the file really occupies on disk (GetCompressedFileSizeW: holes of a sparse file are not counted)."""
    k = ctypes.WinDLL('kernel32', use_last_error=True)
    k.GetCompressedFileSizeW.argtypes = [wintypes.LPCWSTR, ctypes.POINTER(wintypes.DWORD)]
    k.GetCompressedFileSizeW.restype = wintypes.DWORD
    high = wintypes.DWORD(0)
    low = k.GetCompressedFileSizeW(path, ctypes.byref(high))
    if low == 0xFFFFFFFF and ctypes.get_last_error() != 0:
        raise ctypes.WinError(ctypes.get_last_error())
    return (high.value << 32) | low


def block_ranges(events, size):
    """Union of the events' [offset, offset+length) ranges, widened to BLOCK boundaries and clamped to size."""
    spans = sorted((e['offset'] // BLOCK * BLOCK, min(size, -(-(e['offset'] + e['length']) // BLOCK) * BLOCK))
                   for e in events if e['offset'] < size)
    merged = []
    for s, t in spans:
        if merged and s <= merged[-1][1]:
            merged[-1][1] = max(merged[-1][1], t)
        else:
            merged.append([s, t])
    return merged


def shadow_copy(events, src_root, dst_root):
    """Returns (rewritten events, summary). Events of files that are missing under src_root are kept (they point at the
    shadow path, which does not exist either, so a replay skips them); events outside src_root are dropped."""
    t0 = time.perf_counter()
    by_file, out, outside = {}, [], 0
    for e in events:
        try:
            rel = os.path.relpath(e['path'], src_root)
        except ValueError:  # other drive
            rel = '..'
        if rel == '.' or rel.split(os.sep)[0] == '..' or os.path.isabs(rel):
            outside += 1
            continue
        by_file.setdefault(rel, []).append(e)
        out.append(dict(e, path=os.path.join(dst_root, rel)))
    files, written, alloc, missing, sparse_ok = 0, 0, 0, 0, True
    for rel, evs in by_file.items():
        src, dst = os.path.join(src_root, rel), os.path.join(dst_root, rel)
        try:
            size = os.stat(src).st_size
            fs = open(src, 'rb')
        except (FileNotFoundError, PermissionError):
            missing += 1
            continue
        with fs:
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            with open(dst, 'wb') as fd:  # overwrites an earlier shadow
                sparse_ok = _set_sparse(fd) and sparse_ok
                _set_size(fd, size)
                for s, t in block_ranges(evs, size):
                    fs.seek(s)
                    fd.seek(s)
                    left = t - s
                    while left > 0:
                        data = fs.read(min(left, CHUNK))
                        if not data:
                            break
                        fd.write(data)
                        left -= len(data)
                        written += len(data)
                fd.flush()
                os.fsync(fd.fileno())  # allocation is lazy: without this allocated_bytes can read low
        files += 1
        alloc += allocated_bytes(dst)
    summary = dict(files=files, missing_files=missing, outside_root_requests=outside, written_bytes=written,
                   allocated_bytes=alloc, sparse=sparse_ok, seconds=time.perf_counter() - t0)
    return out, summary


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--trace', required=True, help='trace.json (list of {path,offset,length,cost})')
    ap.add_argument('--src-root', required=True)
    ap.add_argument('--dst-root', required=True)
    ap.add_argument('--out', required=True, help='trace with paths moved under --dst-root')
    a = ap.parse_args(argv)
    with open(a.trace, encoding='utf-8') as f:
        events = json.load(f)
    out, summary = shadow_copy(events, a.src_root, a.dst_root)
    with open(a.out, 'w', encoding='utf-8') as f:
        json.dump(out, f)
    print(json.dumps(summary, indent=1))


if __name__ == '__main__':
    main()
