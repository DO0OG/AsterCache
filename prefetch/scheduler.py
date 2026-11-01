"""Pure scheduling: ranges -> ordered, merged read ops. Strategies: physical | path | none."""
import os

MIB = 1 << 20


def breakeven_gap(read_mb_s=100.0, seek_ms=12.0):
    """Bytes the disk streams in one seek time: reading a smaller hole beats seeking over it."""
    return int(read_mb_s * 1e6 * seek_ms / 1000.0)


def _union_bytes(rs):
    total, end = 0, -1
    for r in sorted(rs, key=lambda r: r['offset']):
        lo, hi = max(r['offset'], end), r['offset'] + r['length']
        total += max(0, hi - lo)
        end = max(end, hi)
    return total


def _pieces(ext, cs, off, length):
    """Split [off, off+length) at extent boundaries -> [(phys_byte|None, off, len)] sorted by off."""
    if not ext or not cs:
        return [(None, off, length)]
    out, cur, end = [], off, off + length
    for v, lcn, n in ext:
        lo, hi = max(cur, v * cs), min(end, (v + n) * cs)
        if lo >= hi:
            continue
        if lo > cur:
            out.append((None, cur, lo - cur))
        out.append((lcn * cs + lo - v * cs, lo, hi - lo))
        cur = hi
    if cur < end:
        out.append((None, cur, end - cur))
    return out


def _merge(pieces, gap):
    """Merge same-file neighbours whose file gap (and disk gap, when both known) <= gap.
    Returns [(phys_start|None, off, len)]."""
    out = []  # [phys_start, off, end, phys_end]
    for phys, off, n in sorted(pieces, key=lambda p: p[1]):
        pe = None if phys is None else phys + n
        if out:
            last = out[-1]
            fgap = off - last[2]
            ok = fgap <= gap
            if ok and (phys is None) != (last[0] is None):
                ok = False
            if ok and phys is not None:
                dgap = phys - last[3]
                ok = dgap <= gap and (dgap >= 0 or dgap == fgap)
            if ok:
                last[2] = max(last[2], off + n)
                last[3] = None if pe is None else max(last[3], pe)
                continue
        out.append([phys, off, off + n, pe])
    return [(p, o, e - o) for p, o, e, _ in out]


def schedule(ranges, strategy='physical', budget_bytes=None, gap_bytes=None, layout_fn=None,
             size_fn=None, full_file_ratio=0.3):
    """ranges: [{'path','offset','length'[, 'score']}]. layout_fn(path) -> (extents, cluster_bytes) | None.
    gap_bytes None -> breakeven_gap(). size_fn(path) -> bytes: files whose needed fraction >= full_file_ratio
    (densest first, while the budget allows) are read whole. Returns (ops [(path, offset, length)], info)."""
    gap_bytes = breakeven_gap() if gap_bytes is None else gap_bytes
    if strategy not in ('physical', 'path', 'none'):
        raise ValueError('unknown strategy')
    chosen, used = [], 0
    rows = list(ranges)
    if any('score' in r for r in rows):  # stable: unscored rows rank last
        rows.sort(key=lambda r: -r.get('score', float('-inf')))
    for r in rows:  # no score: keep input order, drop the tail over budget
        n = r['length']
        if budget_bytes is not None and used + n > budget_bytes:
            continue
        chosen.append(r)
        used += n
    info = {'strategy': strategy, 'selected_bytes': used, 'fallback': False, 'layout_ok_files': 0, 'layout_files': 0,
            'gap_bytes': gap_bytes, 'full_files': 0}
    if strategy == 'none':
        return [(r['path'], r['offset'], r['length']) for r in chosen], info
    by_path = {}
    for r in chosen:
        by_path.setdefault(r['path'], []).append(r)
    if size_fn and full_file_ratio <= 1:
        dens = []
        for path, rs in by_path.items():
            size, need = size_fn(path), _union_bytes(rs)
            if size and need / size >= full_file_ratio:
                dens.append((need / size, path, size, need))
        for _, path, size, need in sorted(dens, reverse=True):
            if budget_bytes is None or used - need + size <= budget_bytes:
                used += size - need
                by_path[path] = [{'path': path, 'offset': 0, 'length': size}]
                info['full_files'] += 1
        info['selected_bytes'] = used
    keyed, phys_mode = [], strategy == 'physical'
    for path, rs in by_path.items():
        lay = layout_fn(path) if phys_mode and layout_fn else None
        info['layout_files'] += phys_mode
        if lay and lay[0]:
            info['layout_ok_files'] += 1
            pcs = [p for r in rs for p in _pieces(lay[0], lay[1], r['offset'], r['length'])]
        else:
            info['fallback'] |= phys_mode  # unknown layout: this file sorts last, by path/offset
            pcs = [(None, r['offset'], r['length']) for r in rs]
        drive = os.path.splitdrive(path)[0].upper() if phys_mode else ''
        for phys, off, n in _merge(pcs, gap_bytes):
            keyed.append(((drive, phys is None, phys or 0, path, off), (path, off, n)))
    keyed.sort(key=lambda k: k[0])
    return [k[1] for k in keyed], info
