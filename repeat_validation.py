"""Repeat the whole test suite in fresh Python processes 10 times."""
from __future__ import annotations
import json
import re
from pathlib import Path
import subprocess
import sys
import time

def run(count=10):
    root=Path(__file__).resolve().parent
    iterations=[]
    for n in range(count):
        t=time.perf_counter()
        proc=subprocess.run([sys.executable,'-m','unittest','discover','-s','tests','-q'],
                            cwd=root,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
        m=re.search(r'Ran (\d+) tests',proc.stderr)
        ran=int(m.group(1)) if m else 0
        passed=proc.returncode==0 and ran>0 and 'OK' in proc.stderr and 'skipped' not in proc.stderr
        iterations.append({'round':n+1,'passed':passed,'seconds':round(time.perf_counter()-t,4),
                           'summary':proc.stderr.strip().splitlines()[-1] if proc.stderr else ''})
        if not passed:
            raise AssertionError(f'Round {n+1} failed: {proc.stdout} {proc.stderr}')
    return {'repetitions':count,'tests_per_repetition':ran,
            'total_test_executions':count*ran,'all_pass':True,'rounds':iterations}

if __name__=='__main__':
    result=run()
    path=Path(__file__).parent/'results'/'repeat10.json'
    path.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    print('PASS:',result['repetitions'],'rounds;',result['total_test_executions'],'test executions')
