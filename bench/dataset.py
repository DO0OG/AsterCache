"""Synthetic game-like dataset + loading trace (events compatible with session_planner)."""
import argparse, json, random, shutil
from pathlib import Path

BLOCK = 64 << 10
MIB = 1 << 20
GIB = 1 << 30
MARKER = 'dataset_meta.json'


def _plan_files(n_files, min_mib, max_mib, seed):
    rng = random.Random(seed)
    return [dict(rel='data/f%03d.bin' % i, size=rng.randint(min_mib, max_mib) * MIB) for i in range(n_files)]


def make_trace(root, meta):
    """Deterministic trace: ~30% of requests are sequential runs of 4..16 blocks, rest random single blocks."""
    rng = random.Random(meta['seed'] + 1)
    files = meta['files']
    ev = []
    while len(ev) < meta['trace_n']:
        f = rng.choice(files)
        nblk = f['size'] // meta['block']
        run = min(rng.randint(4, 16) if rng.random() < 0.3 else 1, nblk)
        first = rng.randrange(nblk - run + 1)
        for k in range(run):
            ev.append(dict(path=str(Path(root) / f['rel']), offset=(first + k) * meta['block'],
                           length=meta['block'], cost=8.0))
    return ev[:meta['trace_n']]


def _write_meta_trace(root, meta):
    root = Path(root)
    (root / MARKER).write_text(json.dumps(meta, indent=1))
    (root / 'trace.json').write_text(json.dumps(make_trace(root, meta)))


def _check_space(root, need, min_free_gib):
    p = Path(root)
    p.mkdir(parents=True, exist_ok=True)
    free = shutil.disk_usage(p).free
    if free < need + min_free_gib * GIB:
        raise OSError('not enough free space on %s: free=%d need=%d + %d GiB reserve' % (p.anchor, free, need, min_free_gib))


def is_complete(root, meta):
    root = Path(root)
    try:
        old = json.loads((root / MARKER).read_text())
    except (OSError, ValueError):
        return False
    return old == meta and (root / 'trace.json').is_file() and all(
        (root / f['rel']).is_file() and (root / f['rel']).stat().st_size == f['size'] for f in meta['files'])


def generate(root, n_files=64, min_mib=16, max_mib=48, trace_n=4000, seed=20261101, min_free_gib=5):
    """Create files (seeded random bytes) and trace.json under root. Idempotent: reuses a matching dataset."""
    root = Path(root)
    meta = dict(seed=seed, block=BLOCK, trace_n=trace_n, files=_plan_files(n_files, min_mib, max_mib, seed))
    meta['total_bytes'] = sum(f['size'] for f in meta['files'])
    if is_complete(root, meta):
        return meta
    _check_space(root, meta['total_bytes'], min_free_gib)
    (root / 'data').mkdir(parents=True, exist_ok=True)
    for i, f in enumerate(meta['files']):
        rng, left = random.Random(seed * 7919 + i), f['size']
        with open(root / f['rel'], 'wb') as fh:
            while left:
                n = min(4 * MIB, left)
                fh.write(rng.randbytes(n))
                left -= n
    _write_meta_trace(root, meta)
    return meta


def mirror(src, dst, min_free_gib=5):
    """Copy dataset src -> dst and write a trace with dst paths."""
    src, dst = Path(src), Path(dst)
    meta = json.loads((src / MARKER).read_text())
    if is_complete(dst, meta):
        return meta
    _check_space(dst, meta['total_bytes'], min_free_gib)
    (dst / 'data').mkdir(parents=True, exist_ok=True)
    for f in meta['files']:
        shutil.copyfile(src / f['rel'], dst / f['rel'])
        if (dst / f['rel']).stat().st_size != f['size']:
            raise OSError('mirror size mismatch: ' + f['rel'])
    _write_meta_trace(dst, meta)
    return meta


def load_trace(root):
    return json.loads((Path(root) / 'trace.json').read_text())


def cleanup(root):
    """Delete only a dataset made by this module (marker present, folder named AsterBench*)."""
    root = Path(root)
    if not root.name.lower().startswith('asterbench') or not (root / MARKER).is_file():
        raise ValueError('refusing to delete %s: not an AsterBench dataset folder' % root)
    shutil.rmtree(root / 'data')
    (root / MARKER).unlink()
    (root / 'trace.json').unlink(missing_ok=True)


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--root', required=True)
    ap.add_argument('--mirror', help='also copy the dataset to this root (e.g. C:\\AsterBench)')
    ap.add_argument('--files', type=int, default=64)
    ap.add_argument('--min-mib', type=int, default=16)
    ap.add_argument('--max-mib', type=int, default=48)
    ap.add_argument('--trace', type=int, default=4000)
    ap.add_argument('--seed', type=int, default=20261101)
    ap.add_argument('--cleanup', action='store_true', help='delete dataset at --root (and --mirror)')
    a = ap.parse_args(argv)
    if a.cleanup:
        for r in filter(None, [a.root, a.mirror]):
            cleanup(r)
            print('cleaned', r)
        return 0
    meta = generate(a.root, a.files, a.min_mib, a.max_mib, a.trace, a.seed)
    print('dataset', a.root, len(meta['files']), 'files', meta['total_bytes'], 'bytes')
    if a.mirror:
        mirror(a.root, a.mirror)
        print('mirrored to', a.mirror)
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
