"""Microbenchmark: Linux container, same filesystem, OS page cache unisolated.
Use only to find API overhead, *not* HDD-vs-SSD performance.
"""
from __future__ import annotations
import json
import random
import statistics
import tempfile
import time
from pathlib import Path
from sqlite_cache import SQLiteBlockCache
from blockcache_v06 import BlockCache
B=65536

def sample(cls,root,cachebase,jobs):
    times=[]
    with cls(root,cachebase,capacity_bytes=8*B,min_free_bytes=0) as c:
        for epoch in range(2):
            t=time.perf_counter()
            for off,n in jobs:
                data=c.read('assets',off,n)
                assert len(data)==n
            times.append(round((time.perf_counter()-t)*1000,3))
        snap=c.snapshot()
    return {'initial_batch_ms':times[0],'repeat_batch_ms':times[1], 'stats':snap}

def run(trials=5):
    runs=[]
    with tempfile.TemporaryDirectory() as td:
        base=Path(td);root=base/'source';root.mkdir()
        payload=random.Random(123).randbytes(16*B)
        (root/'assets').write_bytes(payload)
        jobs=[(i*B+123,4096) for i in (0,1,2,3,4,5,6,7)]*5
        for trial in range(trials):
            row={}
            # alternate call order for fairness on warm OS cache
            order=[('v06',BlockCache),('v07',SQLiteBlockCache)]
            if trial%2:order.reverse()
            for name,cls in order:
                row[name]=sample(cls,root,base/(name+str(trial)),jobs)
            runs.append(row)
    return {'environment':'container filesystem, Linux, buffered read; NOT physical HDD vs SSD',
            'trials':trials,
            'median_ms':{k:{phase:round(statistics.median(x[k][phase] for x in runs),3)
                            for phase in ('initial_batch_ms','repeat_batch_ms')} for k in ('v06','v07')},'runs':runs}

if __name__=='__main__':
    r=run()
    (Path(__file__).parent/'results'/'api_microbenchmark.json').write_text(json.dumps(r,indent=2)+'\n')
    print(json.dumps(r['median_ms'],indent=2))
