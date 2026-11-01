"""Cold-ish measurement of prefetch strategies on a fragmented 4x256MiB scratch set (D: HDD).
Cache eviction trick: rewrite identical bytes with FILE_FLAG_NO_BUFFERING|WRITE_THROUGH (same clusters,
cache pages for the range are invalidated). Limits are recorded in the output JSON."""
import ctypes, json, mmap, msvcrt, os, random, statistics, subprocess, sys, time
sys.path.insert(0, r'D:\Git\AsterCache')
from prefetch.layout import cluster_size, file_extents

ROOT = r'D:\AsterPrefetchTmp'
OUT = r'D:\Git\AsterCache\results\prefetch_20261101\prefetch_measure.json'
MIB = 1 << 20
NFILES, FSIZE, CHUNK = 4, 256 * MIB, MIB
GENERIC_WRITE, SHARE_ALL, CREATE_ALWAYS, OPEN_EXISTING = 0x40000000, 7, 2, 3
NOBUF, WTHRU, INV = 0x20000000, 0x80000000, ctypes.c_void_p(-1).value
k = ctypes.WinDLL('kernel32', use_last_error=True)
k.CreateFileW.restype = ctypes.c_void_p
k.CreateFileW.argtypes = [ctypes.c_wchar_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p]
paths = [os.path.join(ROOT, f'g{i}.bin') for i in range(NFILES)]
rnd = random.Random(1)
blob = rnd.randbytes(CHUNK)

def chunk(i, j):  # deterministic content per (file, chunk)
    b = mmap.mmap(-1, CHUNK)  # page aligned for NO_BUFFERING
    b.write(blob[:8] + bytes([i, j & 255, j >> 8]) + blob[11:])
    return b

def opener(p, disp, flags):
    h = k.CreateFileW(p, GENERIC_WRITE, SHARE_ALL, None, disp, flags, None)
    if h in (None, INV): raise OSError(ctypes.get_last_error())
    return os.fdopen(msvcrt.open_osfhandle(h, os.O_WRONLY | os.O_BINARY), 'wb', buffering=0)

def write_set(create):
    """Interleave 1MiB chunks round-robin over the files (fragments NTFS allocation); unbuffered."""
    fs = [opener(p, CREATE_ALWAYS if create else OPEN_EXISTING, NOBUF | WTHRU) for p in paths]
    try:
        for j in range(FSIZE // CHUNK):
            for i, f in enumerate(fs):
                f.write(chunk(i, j))
                if create and j % 8 == 7 and i == 0:  # a foreign file between our extents
                    with open(os.path.join(ROOT, f'filler{j}.tmp'), 'wb') as ff: ff.write(b'x' * 64 * 1024)
    finally:
        for f in fs: f.close()

def make_trace(n=1000, seed=7):
    r = random.Random(seed)
    blocks = [(p, b) for p in paths for b in range(FSIZE // 65536)]
    return [{'path': p, 'offset': b * 65536, 'length': 65536, 'cost': 8.0} for p, b in r.sample(blocks, n)]

def run(trace_path, strategy, gap_kib):
    t = time.perf_counter()
    o = subprocess.run([sys.executable, '-m', 'prefetch.cli', '--plan', trace_path, '--origin-root', ROOT, '--strategy', strategy,
                        '--gap-kib', str(gap_kib), '--execute'], cwd=r'D:\Git\AsterCache', capture_output=True, text=True)
    if o.returncode: raise SystemExit(o.stderr)
    d = json.loads(o.stdout); d['wall_seconds'] = round(time.perf_counter() - t, 3); d['gap_kib'] = gap_kib
    return d

if __name__ == '__main__':
    t0 = time.time(); write_set(True)
    info = {'create_seconds': round(time.time() - t0, 1), 'cluster_bytes': cluster_size(paths[0]),
            'extents': {os.path.basename(p): (None if (e := file_extents(p)) is None else len(e)) for p in paths}}
    trace = make_trace(); tp = os.path.join(ROOT, 'trace.json'); json.dump(trace, open(tp, 'w'))
    configs = [('none', 0), ('path', 1024), ('physical', 1024), ('path', 0), ('physical', 0)]
    runs = []
    for rep in range(2):
        order = configs[rep:] + configs[:rep]
        for s, g in order:
            write_set(False)  # evict
            d = run(tp, s, g); d['rep'] = rep; runs.append(d); print(d, flush=True)
    info['runs'] = runs
    info['trace'] = {'requests': len(trace), 'bytes': sum(r['length'] for r in trace)}
    info['limits'] = ('Non-admin: standby list not purged. Eviction = unbuffered same-bytes rewrite of the 4 files before each run; '
                      'coldness is judged only from throughput. 32GiB RAM so writing 2-3GiB does not evict. Device cache (HDD DRAM) not cleared. Single machine, 2 reps.')
    json.dump(info, open(OUT, 'w'), indent=1)
