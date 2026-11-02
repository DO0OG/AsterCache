"""E6-1 WOF-style compression ratio of a trace's hot ranges (no admin). Reads each hot range once, compresses it in 8 KiB and 16 KiB
chunks with the Windows Compression API (cabinet.dll, XPRESS Huffman, raw blocks). A chunk that does not shrink counts as stored raw
(as WOF does). Table overhead: 4 bytes per chunk offset (added in the *_with_table figures)."""
import argparse, ctypes, json, os
from concurrent.futures import ThreadPoolExecutor
from ctypes import wintypes
from threading import BoundedSemaphore, local

from bench.raw_hot_read import hot_ranges

COMPRESS_ALGORITHM_XPRESS_HUFF, COMPRESS_RAW = 4, 1 << 29
PIECE = 16 << 20
CHUNKS = (8192, 16384)
cab = ctypes.WinDLL('cabinet', use_last_error=True)
cab.CreateCompressor.argtypes = [wintypes.DWORD, ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p)]
cab.Compress.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_size_t, ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
cab.Compress.restype = wintypes.BOOL
tl = local()


def _comp():
    if not hasattr(tl, 'h'):
        h = ctypes.c_void_p()
        if not cab.CreateCompressor(COMPRESS_ALGORITHM_XPRESS_HUFF | COMPRESS_RAW, None, ctypes.byref(h)):
            raise ctypes.WinError(ctypes.get_last_error())
        tl.h, tl.out = h, ctypes.create_string_buffer(64 << 10)
    return tl.h, tl.out


def compress_piece(data):
    """{chunk: (stored_bytes, chunk_count)} for one piece."""
    h, out = _comp()
    res = {}
    for c in CHUNKS:
        stored = cnt = 0
        for o in range(0, len(data), c):
            piece = data[o:o + c]
            n = ctypes.c_size_t(0)
            ok = cab.Compress(h, piece, len(piece), out, len(out), ctypes.byref(n))
            stored += min(n.value, len(piece)) if ok else len(piece)
            cnt += 1
        res[c] = (stored, cnt)
    return res


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--trace', required=True)
    ap.add_argument('--threads', type=int, default=4)
    ap.add_argument('--out')
    a = ap.parse_args(argv)
    ranges = hot_ranges(json.load(open(a.trace, encoding='utf-8')))
    per_file = {}
    sem = BoundedSemaphore(a.threads * 2)
    futs = []
    with ThreadPoolExecutor(a.threads) as ex:
        for path, rs in ranges.items():
            if not os.path.exists(path):
                per_file[path] = dict(missing=True, orig=0)
                continue
            with open(path, 'rb', buffering=0) as f:
                for s, e in rs:
                    f.seek(s)
                    left = e - s
                    while left > 0:
                        data = f.read(min(PIECE, left))
                        if not data:
                            break
                        left -= len(data)
                        sem.acquire()
                        fu = ex.submit(compress_piece, data)
                        fu.add_done_callback(lambda _f: sem.release())
                        futs.append((path, len(data), fu))
        for path, n, fu in futs:
            d = per_file.setdefault(path, dict(orig=0, **{'c%d' % c: 0 for c in CHUNKS}, **{'n%d' % c: 0 for c in CHUNKS}))
            d['orig'] += n
            for c, (st, cnt) in fu.result().items():
                d['c%d' % c] += st
                d['n%d' % c] += cnt
    exts, tot = {}, dict(orig=0, **{'c%d' % c: 0 for c in CHUNKS}, **{'n%d' % c: 0 for c in CHUNKS})
    for path, d in per_file.items():
        if d.get('missing'):
            continue
        for tgt in (exts.setdefault(os.path.splitext(path)[1].lower() or '(none)', dict(files=0, **{x: 0 for x in tot})), tot):
            for x in tot:
                tgt[x] += d[x]
            if tgt is not tot:
                tgt['files'] += 1
    def fin(d):
        for c in CHUNKS:
            d['ratio_%dk' % (c // 1024)] = d['orig'] / d['c%d' % c] if d['c%d' % c] else None
            w = d['c%d' % c] + 4 * d['n%d' % c]
            d['ratio_%dk_with_table' % (c // 1024)] = d['orig'] / w if w else None
        return d
    for d in per_file.values():
        if not d.get('missing'):
            fin(d)
    res = dict(trace=a.trace, algorithm='XPRESS_HUFF raw blocks (cabinet.dll)', total=fin(tot), by_ext={e: fin(d) for e, d in sorted(exts.items())}, by_file=per_file)
    if a.out:
        open(a.out, 'w', encoding='utf-8').write(json.dumps(res, indent=1))
    print(json.dumps(dict(total=res['total'], by_ext=res['by_ext']), indent=1))


if __name__ == '__main__':
    main()
