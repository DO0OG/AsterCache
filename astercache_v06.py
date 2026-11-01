"""v0.6 research CLI: plan/train on earlier Procmon CSV, separately evaluate.
No background tasks, no game injection, no Windows driver. All write actions opt-in.
"""
from __future__ import annotations
import argparse,json
from collections import defaultdict
from pathlib import Path
from blockcache import BlockCache
from procmon_import import parse_procmon_csv
from session_planner import plan_sessions,coverage

MIB=1<<20

def _safe_jobs(manifest,root,max_bytes):
    root=root.resolve(strict=True)
    conf=json.loads(manifest.read_text(encoding='utf-8-sig'))
    if conf.get('block_size')!=65536 or not isinstance(conf.get('ranges'),list):
        raise ValueError('require v0.6 plan with 64KiB blocks')
    jobs=[];total=0
    for item in conf['ranges']:
        absolute=Path(item['path']).resolve(strict=True)
        if not absolute.is_file() or not absolute.is_relative_to(root):
            raise ValueError('not a regular file within allowed origin root')
        offset=item['offset'];length=item['length']
        if type(offset)!=int or type(length)!=int or offset<0 or length<=0 or length>256*MIB:
            raise ValueError('bad range')
        size=absolute.stat().st_size
        if offset>=size: continue
        length=min(length,size-offset)
        total+=length
        if total>max_bytes:raise ValueError('prewarm budget exceeded')
        jobs.append((absolute.relative_to(root),offset,length))
    return jobs,total

def main():
    p=argparse.ArgumentParser(description=__doc__)
    sub=p.add_subparsers(dest='action',required=True)
    a=sub.add_parser('train',help='select cross-launch cache blocks; no source reads')
    a.add_argument('--csv',nargs='+',type=Path,required=True)
    a.add_argument('--prefix',required=True)
    a.add_argument('--budget-mib',type=int,default=256)
    a.add_argument('--out',required=True,type=Path)
    e=sub.add_parser('evaluate',help='evaluate a plan on a separate future trace')
    e.add_argument('--plan',required=True,type=Path)
    e.add_argument('--csv',required=True,type=Path)
    e.add_argument('--prefix',required=True)
    w=sub.add_parser('warm',help='prepare user-mode SSD cache, not Windows file redirection')
    w.add_argument('--plan',required=True,type=Path)
    w.add_argument('--origin-root',required=True,type=Path)
    w.add_argument('--cache-dir',required=True,type=Path)
    w.add_argument('--capacity-mib',type=int,default=256)
    w.add_argument('--execute',action='store_true')
    r=sub.add_parser('read',help='one opt-in cached read only')
    r.add_argument('--origin-root',required=True,type=Path)
    r.add_argument('--cache-dir',required=True,type=Path)
    r.add_argument('--relative',required=True)
    r.add_argument('--offset',type=int,required=True)
    r.add_argument('--length',type=int,required=True)
    r.add_argument('--capacity-mib',type=int,default=256)
    r.add_argument('--execute',action='store_true')
    args=p.parse_args()
    if args.action=='train':
        if not 1<=args.budget_mib<=8192:raise ValueError('budget 1..8192 MiB')
        sessions=[]
        for csv_path in args.csv:
            events,stats=parse_procmon_csv(csv_path,args.prefix)
            sessions.append(events)
            print(f'Imported {csv_path.name}: {len(events)} events ({stats["rows"]} rows)')
        selected=plan_sessions(sessions,args.budget_mib*MIB)
        args.out.write_text(json.dumps(selected,indent=2,ensure_ascii=False)+'\n',encoding='utf-8')
        print(json.dumps({k:v for k,v in selected.items() if k!='ranges'},indent=2))
        print('Saved paths in manifest; review before sharing.')
    elif args.action=='evaluate':
        selected=json.loads(args.plan.read_text(encoding='utf-8'))
        events,stats=parse_procmon_csv(args.csv,args.prefix)
        print(json.dumps({'coverage':coverage(selected,events),'events':len(events),'parser':stats},indent=2))
    elif args.action=='warm':
        if not 1<=args.capacity_mib<=8192:raise ValueError('capacity 1..8192 MiB')
        jobs,bytes_= _safe_jobs(args.plan,args.origin_root,args.capacity_mib*MIB)
        print(json.dumps({'jobs':len(jobs),'bytes':bytes_,'execute':args.execute}))
        if args.execute:
            with BlockCache(args.origin_root,args.cache_dir,args.capacity_mib*MIB) as c:
                for relative,offset,n in jobs:
                    while n:
                        chunk=min(n,4*MIB)
                        c.read(relative,offset,chunk)
                        offset+=chunk;n-=chunk
                print(json.dumps(c.snapshot(),indent=2))
    else:
        if not 1<=args.capacity_mib<=8192:raise ValueError('capacity 1..8192 MiB')
        print('Explicit library API; does not redirect reads made by existing games.')
        if args.execute:
            with BlockCache(args.origin_root,args.cache_dir,args.capacity_mib*MIB) as c:
                data=c.read(args.relative,args.offset,args.length)
                print(json.dumps({'returned_bytes':len(data),'statistics':c.snapshot()},indent=2))

if __name__=='__main__': main()
