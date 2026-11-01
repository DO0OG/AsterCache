"""Leak-free cross-launch block prewarm planner. Train on earlier sessions,
score on held-out later launches. Does NOT claim physical storage timing.
Events: {'path': absolute-path, 'offset': int, 'length': int, 'cost': float?}
"""
from __future__ import annotations
from collections import defaultdict
import math


def _validated_events(session, block_size):
    if len(session)>500_000: raise ValueError('session too large')
    for e in session:
        path=e.get('path');o=e.get('offset');n=e.get('length');c=e.get('cost',8.)
        if not isinstance(path,str) or not path or len(path)>32767 or type(o)!=int or o<0 or type(n)!=int or n<=0 or n>(64<<20):
            raise ValueError('bad event')
        if isinstance(c,bool) or not isinstance(c,(int,float)) or not math.isfinite(c) or c<0:
            raise ValueError('bad cost')
        first=o//block_size;last=(o+n-1)//block_size
        if last-first>1024:raise ValueError('too many blocks in one request')
        yield path, first,last,float(c)


def plan_sessions(sessions,budget_bytes,block_size=65536,decay=0.85):
    if type(budget_bytes)!=int or budget_bytes<0 or type(block_size)!=int or block_size<4096 or block_size&(block_size-1):
        raise ValueError('invalid budget/block size')
    if not isinstance(decay,(float,int)) or not 0<decay<=1:raise ValueError('invalid decay')
    if not sessions or len(sessions)>32:raise ValueError('expect 1..32 training sessions')
    record=defaultdict(lambda:[0.,0,0])
    denom=sum(decay**i for i in range(len(sessions)))
    for i,session in enumerate(sessions):
        weight=decay**(len(sessions)-i-1)
        seen={}
        for path,first,last,cost in _validated_events(session,block_size):
            for block in range(first,last+1):
                key=(path,block)
                seen[key]=max(seen.get(key,0.),cost)
        for key,cost in seen.items():
            entry=record[key];entry[0]+=weight;entry[1]+=weight*cost;entry[2]+=1
    candidates=[]
    for (path,idx),(weights,cost,seen) in record.items():
        if seen < min(2,len(sessions)):
            continue  # reject single-session scans when multiple runs are available
        prob=weights/denom
        score=prob*(cost/max(weights,1e-12))
        candidates.append((score,path,idx,prob,seen))
    candidates.sort(key=lambda r:(-r[0],r[1],r[2]))
    chosen=[];used=0
    for score,path,idx,prob,seen in candidates:
        if score<=0 or used+block_size>budget_bytes:break
        chosen.append((path,idx,score,prob));used+=block_size
    merged=[]
    for path,idx,score,prob in sorted(chosen,key=lambda r:(r[0],r[1])):
        off=idx*block_size
        if merged and merged[-1]['path']==path and merged[-1]['offset']+merged[-1]['length']==off:
            merged[-1]['length']+=block_size
        else:merged.append(dict(path=path,offset=off,length=block_size))
    return {'train_sessions':len(sessions),'block_size':block_size,'bytes':used,
            'candidate_blocks':len(candidates),'selected_blocks':len(chosen),'ranges':merged}


def coverage(plan,events):
    b=plan['block_size']
    resident=set()
    for r in plan['ranges']:
        for i in range(r['offset']//b,(r['offset']+r['length']-1)//b+1):resident.add((r['path'],i))
    total=hits=0
    # count unique block accesses per event, not distinct unique blocks globally
    for path,first,last,_ in _validated_events(events,b):
        for i in range(first,last+1):
            total+=1;hits+=(path,i) in resident
    return dict(request_blocks=total,covered_blocks=hits,
                coverage_pct=round(100*hits/max(total,1),3))
