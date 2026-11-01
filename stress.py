"""Seeded randomized real-byte correctness testing, *not* hardware timings."""
from __future__ import annotations
import json
import random
import tempfile
from pathlib import Path
from sqlite_cache import SQLiteBlockCache
B=65536

def run(seeds=50,operations=250):
    outputs=[]
    for seed in range(seeds):
        r=random.Random(27000+seed)
        reads=writes=replacements=0
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp)/'hdd';root.mkdir()
            data=bytearray(r.randbytes(B*8+13))
            f=root/'gamefile.bin';f.write_bytes(data)
            with SQLiteBlockCache(root,Path(temp)/'ssd',capacity_bytes=3*B,min_free_bytes=0) as c:
                for _ in range(operations):
                    a=r.random()
                    if a<.06:
                        at=r.randrange(len(data));n=r.randrange(1,min(1000,len(data)-at)+1)
                        data[at:at+n]=r.randbytes(n)
                        with f.open('r+b') as fp:fp.seek(at);fp.write(data[at:at+n])
                        writes += 1
                    elif a<.10:
                        # atomic replacement, same length (inode changes)
                        data[:]=r.randbytes(len(data))
                        swap=root/'replacement';swap.write_bytes(data);swap.replace(f)
                        replacements += 1
                    else:
                        off=r.randrange(len(data))
                        n=r.randrange(1,min(B*2,len(data)-off)+1)
                        result=c.read('gamefile.bin',off,n)
                        assert result==data[off:off+n],(seed,off,n)
                        reads += 1
                outputs.append({'seed':seed,'stats':c.snapshot(),'verified_reads':reads,'inplace_writes':writes,'replacements':replacements})
    return {'seeds':seeds,'operations_per_seed':operations,'verified_api_operations':seeds*operations,
            'verified_reads':sum(v['verified_reads'] for v in outputs),
            'inplace_writes':sum(v['inplace_writes'] for v in outputs),
            'replacements':sum(v['replacements'] for v in outputs),
            'source_read_bytes':sum(v['stats']['origin_bytes'] for v in outputs),
            'cache_read_bytes':sum(v['stats']['cache_bytes_read'] for v in outputs),
            'corruption_events':sum(v['stats']['invalid_blocks'] for v in outputs),
            'results':outputs}

if __name__=='__main__':
    result=run()
    p=Path(__file__).parent/'results'/'stress.json'
    p.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    print('OK:',result['seeds'],'seeds;',result['verified_api_operations'],'operations')
