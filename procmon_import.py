"""Import user-exported Process Monitor CSV (not raw ETW .etl files).
Only successful ReadFile events with explicit offset/length are used.
No live process inspection, no network, no writes to tracked game files.
"""
import csv
import ntpath
import re
from pathlib import Path

RE_OFF=re.compile(r'\bOffset:\s*(0x[0-9A-Fa-f]+|[0-9,]+)',re.I)
RE_LEN=re.compile(r'\bLength:\s*(0x[0-9A-Fa-f]+|[0-9,]+)',re.I)

def _num(s):
    s=s.replace(',','')
    return int(s,16 if s.lower().startswith('0x') else 10)

def parse_procmon_csv(path:Path, drive_prefix:str='', max_events:int=500000):
    if max_events<1 or max_events>10000000:raise ValueError('max_events invalid')
    prefix=ntpath.normcase(ntpath.normpath(drive_prefix)) if drive_prefix else ''
    stats={'rows':0,'read_success_rows':0,'parsed':0,'ignored':0}
    events=[]
    with Path(path).open('r',encoding='utf-8-sig',newline='') as f:
        reader=csv.DictReader(f)
        required={'Operation','Path','Result','Detail'}
        if not reader.fieldnames or not required.issubset(reader.fieldnames):
            raise ValueError('Expected Procmon CSV with Operation, Path, Result, Detail columns')
        for row in reader:
            stats['rows']+=1
            if row.get('Operation','').casefold()!='readfile' or row.get('Result','').casefold()!='success':
                stats['ignored']+=1;continue
            stats['read_success_rows']+=1
            name=row.get('Path','')
            canon=ntpath.normcase(ntpath.normpath(name))
            # e.g. D:\Games, also avoid matching D:\GamesOther
            if prefix and not (canon==prefix or canon.startswith(prefix+'\\')):
                stats['ignored']+=1;continue
            mo=RE_OFF.search(row.get('Detail',''))
            ml=RE_LEN.search(row.get('Detail',''))
            if not mo or not ml:
                stats['ignored']+=1;continue
            off=_num(mo.group(1));length=_num(ml.group(1))
            if off<0 or not 0<length<=64*1024*1024:
                stats['ignored']+=1;continue
            events.append({'path':name,'offset':off,'length':length,'cost':8.})
            stats['parsed']+=1
            if len(events)>=max_events:break
    return events,stats
