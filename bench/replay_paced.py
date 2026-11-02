"""Paced replay: like bench.replay (buffered reads) but with a busy-wait (CPU, not sleep) between requests, so the disk is idle between reads.
total_s = sum of read latencies (what the "game" waits for, comparable with bench.replay); wall_s adds the think time.
--think-ms N: fixed pause after every request. --from-trace: reproduce the pauses from per-event times in the trace
(fields 't', 'ts' or 'time' in seconds) - fails if the trace has none.
--every K: pause only before every K-th request (the Frostpunk trace has 30,843 requests: 20 ms each would add ~10 minutes per run)."""
import argparse, json, time, zlib

from bench.replay import BufferedReader, MIB, _pct


def spin(seconds):
    end = time.perf_counter() + seconds
    while time.perf_counter() < end:
        pass


def gaps_from_trace(events):
    key = next((k for k in ('t', 'ts', 'time') if k in events[0]), None)
    if key is None:
        raise SystemExit('--from-trace: trace events have no t/ts/time field (fields: %s)' % ', '.join(events[0]))
    ts = [float(e[key]) for e in events]
    return [0.0] + [max(0.0, b - a) for a, b in zip(ts, ts[1:])]


def replay_paced(events, think_ms=20.0, from_trace=False, every=1):
    r = BufferedReader()
    gaps = gaps_from_trace(events) if from_trace else [think_ms / 1e3 if i % every == 0 else 0.0 for i in range(len(events))]
    lat, crc, nbytes, missing, skipped = [], 0, 0, set(), 0
    wall0 = time.perf_counter()
    try:
        for e, gap in zip(events, gaps):
            spin(gap)
            t = time.perf_counter()
            try:
                d = r.read(e['path'], e['offset'], e['length'])
            except (FileNotFoundError, PermissionError):
                missing.add(e['path'])
                skipped += 1
                continue
            lat.append(time.perf_counter() - t)
            crc = zlib.crc32(d, crc)
            nbytes += len(d)
    finally:
        r.close()
    wall = time.perf_counter() - wall0
    total, s = sum(lat), sorted(lat)
    return dict(mode='buffered_paced', think_ms=None if from_trace else think_ms, every=every, from_trace=from_trace, requests=len(lat), bytes=nbytes, crc32=crc,
                total_s=total, wall_s=wall, throughput_mib_s=nbytes / MIB / total if total else None,
                p50_ms=_pct(s, 50) * 1e3, p95_ms=_pct(s, 95) * 1e3, p99_ms=_pct(s, 99) * 1e3,
                missing_files=len(missing), skipped_requests=skipped)


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--trace', required=True, help='trace.json (list of {path,offset,length,cost})')
    ap.add_argument('--think-ms', type=float, default=20.0)
    ap.add_argument('--from-trace', action='store_true')
    ap.add_argument('--every', type=int, default=1)
    a = ap.parse_args(argv)
    print(json.dumps(replay_paced(json.load(open(a.trace, encoding='utf-8')), a.think_ms, a.from_trace, a.every), indent=1))


if __name__ == '__main__':
    main()
