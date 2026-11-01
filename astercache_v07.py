"""AsterCache v0.7 opt-in research CLI (does not intercept games).

Examples:
  python astercache_v07.py plan --csv run1.csv run2.csv --prefix D:\\Games --out plan.json
  python astercache_v07.py inspect --plan plan.json --origin-root D:\\Games
  python astercache_v07.py ram-warm --plan plan.json --origin-root D:\\Games --execute
  python astercache_v07.py ssd-warm --plan plan.json --origin-root D:\\Games --cache-dir C:\\AsterCacheTest --execute
"""
from __future__ import annotations
import argparse
import json
from pathlib import Path
import time
from sqlite_cache import SQLiteBlockCache
from session_planner import coverage, plan_sessions
from procmon_import import parse_procmon_csv
MIB=1<<20

def safe_jobs(plan_path:Path, origin_root:Path, budget:int):
    if type(budget) is not int or budget<0:
        raise ValueError('invalid budget')
    root=origin_root.resolve(strict=True)
    plan=json.loads(plan_path.read_text(encoding='utf-8-sig'))
    if plan.get('block_size')!=65536 or not isinstance(plan.get('ranges'),list) or len(plan['ranges'])>200_000:
        raise ValueError('invalid plan')
    jobs=[];total=0
    for row in plan['ranges']:
        if not isinstance(row,dict) or not isinstance(row.get('path'),str):
            raise ValueError('invalid plan path')
        p=Path(row['path']).resolve(strict=True)
        if not p.is_relative_to(root) or not p.is_file():
            raise ValueError('plan includes path outside origin')
        off=row.get('offset');n=row.get('length')
        if (type(off) is not int or type(n) is not int or off<0 or n<=0
                or n>(256*MIB) or off%65536 or n%65536):
            raise ValueError('invalid block-aligned prewarm range')
        file_size=p.stat().st_size
        if off>=file_size:continue
        n=min(n,file_size-off)
        total+=n
        if total>budget:
            raise ValueError('prewarm plan exceeds byte budget')
        jobs.append((p.relative_to(root),off,n))
    return jobs,total

def main(argv=None):
    p=argparse.ArgumentParser(description=__doc__)
    sub=p.add_subparsers(dest='action',required=True)
    train=sub.add_parser('plan')
    train.add_argument('--csv',type=Path,nargs='+',required=True)
    train.add_argument('--prefix',required=True)
    train.add_argument('--out',type=Path,required=True)
    train.add_argument('--budget-mib',type=int,default=256)
    check=sub.add_parser('evaluate')
    check.add_argument('--plan',type=Path,required=True)
    check.add_argument('--csv',type=Path,required=True)
    check.add_argument('--prefix',required=True)
    for name in ('inspect','ram-warm','ssd-warm'):
        s=sub.add_parser(name)
        s.add_argument('--plan',type=Path,required=True)
        s.add_argument('--origin-root',type=Path,required=True)
        s.add_argument('--budget-mib',type=int,default=256)
        if name!='inspect':s.add_argument('--execute',action='store_true')
        if name=='ssd-warm':s.add_argument('--cache-dir',type=Path,required=True)
    args=p.parse_args(argv)
    if args.action in ('plan','inspect','ram-warm','ssd-warm') and not 1<=args.budget_mib<=8192:
        p.error('budget-mib must be in 1..8192')
    if args.action=='plan':
        sessions=[]
        for file in args.csv:
            events,stats=parse_procmon_csv(file,args.prefix)
            sessions.append(events)
            print(file.name,stats)
        plan=plan_sessions(sessions,args.budget_mib*MIB)
        args.out.write_text(json.dumps(plan,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
        print('Plan saved:',args.out,'bytes=',plan['bytes'])
    elif args.action=='evaluate':
        plan=json.loads(args.plan.read_text(encoding='utf-8'))
        events,_=parse_procmon_csv(args.csv,args.prefix)
        print(json.dumps(coverage(plan,events),ensure_ascii=False,indent=2))
    else:
        jobs,total=safe_jobs(args.plan,args.origin_root,args.budget_mib*MIB)
        dry=args.action=='inspect' or not args.execute
        print(json.dumps({'action':args.action,'jobs':len(jobs),'bytes':total,'executing':not dry}))
        if dry:return
        t=time.perf_counter()
        if args.action=='ssd-warm':
            with SQLiteBlockCache(args.origin_root,args.cache_dir,capacity_bytes=args.budget_mib*MIB) as c:
                for rel,off,n in jobs:
                    while n:
                        length=min(n,4*MIB)
                        c.read(rel,off,length)
                        off+=length;n-=length
                print(json.dumps(c.snapshot(),indent=2))
        elif args.action=='ram-warm':
            # Windows buffered reads may warm OS cache. Nothing is pinned or guaranteed.
            root=args.origin_root.resolve(strict=True)
            total_read=0
            for rel,off,n in jobs:
                p=(root/rel).resolve(strict=True)
                if not p.is_relative_to(root):
                    raise ValueError('path escaped origin during warm')
                with p.open('rb') as f:
                    f.seek(off)
                    while n:
                        block=f.read(min(n,MIB))
                        if not block:raise RuntimeError('source file shortened')
                        total_read+=len(block);n-=len(block)
            print(json.dumps({'read_bytes':total_read, 'note':'OS page cache residency is not guaranteed'}))
        print('elapsed_seconds=',round(time.perf_counter()-t,6))

if __name__=='__main__':main()
