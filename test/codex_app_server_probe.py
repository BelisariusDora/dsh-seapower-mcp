"""Probe a separately owned Codex app-server; never touch desktop chats."""
import argparse
import json
from pathlib import Path
import queue
import shutil
import subprocess
import threading
import time

parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--out',required=True,help='Evidence JSON path; uses a model turn and a cancellation test')
parser.add_argument('--cwd',default=str(Path(__file__).resolve().parent),help='Read-only probe working directory')
args=parser.parse_args()
binary=shutil.which('codex')
assert binary,'codex executable unavailable'
process=subprocess.Popen([binary,'app-server','--stdio'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,
                         stderr=subprocess.PIPE,text=True,encoding='utf-8',creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
messages=queue.Queue()
def read():
    for line in process.stdout:
        try: messages.put(json.loads(line))
        except ValueError: pass
threading.Thread(target=read,daemon=True).start()
threading.Thread(target=lambda:list(process.stderr),daemon=True).start()
seen=[]
def send(method,params,request_id=None):
    body={'method':method,'params':params}
    if request_id is not None:body['id']=request_id
    process.stdin.write(json.dumps(body)+'\n');process.stdin.flush()
def until(predicate,timeout=35):
    deadline=time.monotonic()+timeout
    while time.monotonic()<deadline:
        msg=messages.get(timeout=max(0.01,deadline-time.monotonic()))
        seen.append(msg)
        if msg.get('method') and 'id' in msg:
            # No automatic approval, shell or external tool handling in this probe.
            process.stdin.write(json.dumps({'id':msg['id'],'error':{'code':-32601,'message':'Probe does not authorize tool or approval requests'}})+'\n');process.stdin.flush()
        if predicate(msg):return msg
    raise TimeoutError('Codex probe timed out')
def rpc(method,params,request_id):
    send(method,params,request_id)
    reply=until(lambda m:m.get('id')==request_id)
    if reply.get('error'):raise RuntimeError(str(reply['error']))
    return reply['result']
result={'transport':'owned stdio app-server','usesDesktopThread':False,'modelTurnTested':False,'gameCommandsTested':False}
try:
    initialized=rpc('initialize',{'clientInfo':{'name':'seapower-tr3-probe','version':'0.1.0'},'capabilities':{'experimentalApi':True}},1)
    result['initialized']=True
    result['serverUserAgent']=initialized.get('userAgent')
    send('initialized',{})
    thread=rpc('thread/start',{'cwd':str(Path(args.cwd).resolve()),'ephemeral':True,
                             'sandbox':'read-only','approvalPolicy':'on-request'},2)['thread']
    result['ephemeralAccepted']=thread.get('ephemeral') is True
    thread_id=thread['id']
    turn=rpc('turn/start',{'threadId':thread_id,'input':[{'type':'text','text':'Infrastructure test. Reply exactly TR3_CODEX_SMOKE. Do not call tools, inspect files, or issue commands.'}]},3)['turn']
    done=until(lambda m:m.get('method')=='turn/completed' and m.get('params',{}).get('turn',{}).get('id')==turn['id'],50)
    result['completionStatus']=done['params']['turn']['status']
    result['smokeMarkerObserved']=any('TR3_CODEX_SMOKE' in str(m.get('params',{}).get('item',{}).get('text','')) for m in seen if m.get('method')=='item/completed')
    result['modelTurnTested']=True
    if result['completionStatus']=='completed' and result['smokeMarkerObserved']:
        second=rpc('turn/start',{'threadId':thread_id,'input':[{'type':'text','text':'Infrastructure cancellation test. Reply CANCEL_TEST only; do not call tools or inspect files.'}]},4)['turn']
        rpc('turn/interrupt',{'threadId':thread_id,'turnId':second['id']},5)
        ended=next((m for m in seen if m.get('method')=='turn/completed' and m.get('params',{}).get('turn',{}).get('id')==second['id']),None)
        if ended is None:ended=until(lambda m:m.get('method')=='turn/completed' and m.get('params',{}).get('turn',{}).get('id')==second['id'],20)
        result['interruptStatus']=ended['params']['turn']['status']
    result['serverToolRequests']=len([m for m in seen if m.get('method') and 'id' in m])
except Exception as error:
    result['error']=str(error)[:1000]
finally:
    process.stdin.close()
    try:process.wait(timeout=5)
    except subprocess.TimeoutExpired:process.terminate();process.wait(timeout=5)
    result['ownedProcessExited']=process.poll() is not None
    Path(args.out).write_text(json.dumps(result,indent=2),encoding='utf-8')
print(json.dumps(result))
