"""E7: same synthetic dataset as bench.dataset (same meta/trace format), but the files are written round-robin in slices so their clusters interleave on disk.
Prints the physical fragmentation (files' extents, merged hot runs of the trace)."""
import argparse, ctypes, json, os, random
from pathlib import Path

from bench import dataset
from bench.raw_hot_read import extents, hot_ranges, hot_runs


def generate_scattered(root, slice_mib=1, n_files=64, min_mib=16, max_mib=48, trace_n=4000, seed=20261101, min_free_gib=10):
    root = Path(root)
    meta = dict(seed=seed, block=dataset.BLOCK, trace_n=trace_n, files=dataset._plan_files(n_files, min_mib, max_mib, seed))
    meta['total_bytes'] = sum(f['size'] for f in meta['files'])
    dataset._check_space(root, meta['total_bytes'], min_free_gib)
    (root / 'data').mkdir(parents=True, exist_ok=True)
    rngs = [random.Random(seed * 7919 + i) for i in range(len(meta['files']))]
    left = [f['size'] for f in meta['files']]
    handles = [open(root / f['rel'], 'wb') for f in meta['files']]
    try:
        while any(left):
            for i, h in enumerate(handles):
                n = min(slice_mib * dataset.MIB, left[i])
                if n:
                    h.write(rngs[i].randbytes(n))
                    h.flush()
                    left[i] -= n
    finally:
        for h in handles:
            h.close()
    dataset._write_meta_trace(root, meta)
    return meta


BS = chr(92)
FSCTL_SET_SPARSE = 0x900C4


def _set_sparse(f):
    import msvcrt
    n = ctypes.c_ulong(0)
    if not ctypes.windll.kernel32.DeviceIoControl(msvcrt.get_osfhandle(f.fileno()), FSCTL_SET_SPARSE, None, 0, None, 0, ctypes.byref(n), None):
        raise ctypes.WinError(ctypes.get_last_error())


def generate_from_trace(src_trace, src_root, dst_root, slice_mib=1, limit_mib=0):
    """Mirror a real game's trace: same relative paths, same file sizes (sparse), real random bytes only in the trace's hot ranges. The hot data
    is written in slice_mib pieces round-robin over the files, each piece fsync'ed so the clusters really interleave. Writes <dst_root>/trace_fp.json
    (the trace with src_root replaced by dst_root). limit_mib > 0 stops after that many MiB of hot data (tests)."""
    src_root, dst_root = str(src_root).rstrip(BS), str(dst_root).rstrip(BS)
    trace = json.load(open(src_trace, encoding='utf-8'))
    ranges = hot_ranges(trace)
    plan = []  # (rel, size, pieces)
    for path, rs in sorted(ranges.items()):
        if not os.path.isfile(path):
            continue  # the trace names a file the disk no longer has; replay skips it the same way
        size = os.path.getsize(path)
        pieces = []
        for a, b in rs:
            b = min(b, size)
            for o in range(a, b, slice_mib * dataset.MIB):
                pieces.append((o, min(o + slice_mib * dataset.MIB, b) - o))
        plan.append((os.path.relpath(path, src_root), size, pieces))
    hot = sum(n for _, _, ps in plan for _, n in ps)
    dataset._check_space(Path(dst_root), hot, 10)
    handles = []
    for rel, size, _ in plan:
        dst = Path(dst_root) / rel
        dst.parent.mkdir(parents=True, exist_ok=True)
        f = open(dst, 'wb')
        _set_sparse(f)
        f.truncate(size)
        handles.append(f)
    rngs = [random.Random(20261102 + i) for i in range(len(plan))]
    written, cursor = 0, [0] * len(plan)
    try:
        while any(cursor[i] < len(plan[i][2]) for i in range(len(plan))) and not (limit_mib and written >= limit_mib * dataset.MIB):
            for i, f in enumerate(handles):
                if cursor[i] < len(plan[i][2]):
                    o, n = plan[i][2][cursor[i]]
                    cursor[i] += 1
                    f.seek(o)
                    f.write(rngs[i].randbytes(n))
                    f.flush()
                    os.fsync(f.fileno())
                    written += n
    finally:
        for f in handles:
            f.close()
    out = []
    for e in trace:
        if e['path'].lower().startswith(src_root.lower() + BS):
            out.append(dict(e, path=dst_root + e['path'][len(src_root):]))
    json.dump(out, open(Path(dst_root) / 'trace_fp.json', 'w', encoding='utf-8'))
    return dict(files=len(plan), hot_bytes=hot, written_bytes=written, events=len(out), skipped_events=len(trace) - len(out))


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--root', required=True)
    ap.add_argument('--slice-mib', type=int, default=1)
    ap.add_argument('--from-trace', help='mirror this game trace (needs --src-root): sparse files, hot ranges only')
    ap.add_argument('--src-root')
    ap.add_argument('--limit-mib', type=int, default=0)
    ap.add_argument('--out', help='fragmentation summary json')
    a = ap.parse_args(argv)
    if a.from_trace:
        res = generate_from_trace(a.from_trace, a.src_root, a.root, a.slice_mib, a.limit_mib)
        root = Path(a.root)
        runs, files = hot_runs(json.load(open(root / 'trace_fp.json', encoding='utf-8')), 4096)
        res.update(extents_total=sum(v['extents'] for v in files.values()), hot_runs=len(runs), lcn_min=runs[0][0] if runs else None, lcn_max=runs[-1][0] + runs[-1][1] if runs else None)
        if a.out:
            open(a.out, 'w', encoding='utf-8').write(json.dumps(res, indent=1))
        print(json.dumps(res, indent=1))
        return
    meta = generate_scattered(a.root, a.slice_mib)
    root = Path(a.root)
    trace = json.load(open(root / 'trace.json', encoding='utf-8'))
    runs, files = hot_runs(trace, 4096)
    ext = [len(extents(str(root / f['rel']))) for f in meta['files']]
    res = dict(root=a.root, files=len(meta['files']), total_bytes=meta['total_bytes'], slice_mib=a.slice_mib, extents_total=sum(ext),
               extents_per_file_min=min(ext), extents_per_file_max=max(ext), hot_runs=len(runs), hot_bytes=sum(n for _, n in runs) * 4096,
               lcn_min=runs[0][0], lcn_max=runs[-1][0] + runs[-1][1])
    if a.out:
        open(a.out, 'w', encoding='utf-8').write(json.dumps(res, indent=1))
    print(json.dumps(res, indent=1))


if __name__ == '__main__':
    main()
