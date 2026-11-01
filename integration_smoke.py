"""End-to-end CLI smoke with real temp file and 4 synthetic Procmon-style CSVs."""
import csv
import hashlib
import json
import tempfile
from pathlib import Path
from astercache_v07 import main
import sqlite_cache
from sqlite_cache import SQLiteBlockCache
B=65536

def run():
    sqlite_cache.RACY_WINDOW_NS=0  # source is created moments before warm/read; guard disabled for this smoke test
    with tempfile.TemporaryDirectory() as temp:
        base=Path(temp);root=base/'games';root.mkdir()
        source=root/'gameassets.dat';source.write_bytes(b'A'*B+b'B'*B+b'C'*B)
        source_before=hashlib.sha256(source.read_bytes()).hexdigest()
        plans=[]
        for run in range(4):
            csvpath=base/f'launch{run}.csv'
            with csvpath.open('w',encoding='utf-8-sig',newline='') as f:
                writer=csv.DictWriter(f,fieldnames=['Operation','Path','Result','Detail'])
                writer.writeheader()
                for idx in ([0,1] if run!=3 else [0,1,2]):
                    writer.writerow({'Operation':'ReadFile','Path':str(source),'Result':'SUCCESS',
                                     'Detail':f'Offset: {idx*B}, Length: {B}'})
            plans.append(csvpath)
        manifest=base/'plan.json'
        main(['plan','--csv',str(plans[0]),str(plans[1]),str(plans[2]),'--prefix',str(root),
              '--budget-mib','1','--out',str(manifest)])
        main(['evaluate','--plan',str(manifest),'--csv',str(plans[3]),'--prefix',str(root)])
        main(['inspect','--plan',str(manifest),'--origin-root',str(root)])
        main(['ram-warm','--plan',str(manifest),'--origin-root',str(root),'--execute'])
        main(['ssd-warm','--plan',str(manifest),'--origin-root',str(root),'--cache-dir',str(base/'ssd'),
              '--execute','--budget-mib','1'])
        with SQLiteBlockCache(root,base/'ssd',capacity_bytes=B*16,min_free_bytes=0) as c:
            data=c.read(source.name,123,99)
            assert data==b'A'*99
            assert c.snapshot()['block_hits']>=1
        assert hashlib.sha256(source.read_bytes()).hexdigest()==source_before
        return {'result':'PASS','read_data_matches':True,'source_sha256_unchanged':True,
                'plan_blocks':json.loads(manifest.read_text())['selected_blocks'],
                'future_trace_blocks':3}

if __name__=='__main__':
    result=run()
    (Path(__file__).parent/'results'/'integration.json').write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps(result))
