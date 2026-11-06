"""Real Codex dynamic tools against a temporary fake Bridge; no game commands."""
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import argparse
import json
from pathlib import Path
import sys
import tempfile
import threading
import time
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from ticket_executor_codex import CodexExecutor

parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--out',required=True,help='Evidence JSON output; runs one real model turn against a fake Bridge')
args=parser.parse_args()
calls=[]
class Handler(BaseHTTPRequestHandler):
    def log_message(self,*args):pass
    def do_GET(self):
        if self.path=='/comms/state':data={'worldKey':'test-world','hold':False}
        elif self.path=='/tickets':data={'tickets':[{'id':'test-codex','status':'running'}]}
        else:raise ValueError(self.path)
        self.reply({'ok':True,'data':data})
    def do_POST(self):
        body=json.loads(self.rfile.read(int(self.headers['Content-Length'])))
        calls.append((self.path,body))
        if self.path=='/units/12/speed':data={'id':12,'action':'speed','knots':5}
        elif self.path=='/tickets':data={'ok':True,'id':'test-codex','status':'monitoring'}
        else:raise ValueError(self.path)
        self.reply({'ok':True,'cmd':123,'data':data})
    def reply(self,body):
        raw=json.dumps(body).encode()
        self.send_response(200);self.send_header('Content-Type','application/json');self.send_header('Content-Length',str(len(raw)));self.end_headers();self.wfile.write(raw)
server=ThreadingHTTPServer(('127.0.0.1',0),Handler)
threading.Thread(target=server.serve_forever,daemon=True).start()
with tempfile.TemporaryDirectory() as directory:
    (Path(directory)/'run/tickets').mkdir(parents=True)
    adapter=CodexExecutor(directory,server.server_port)
    ticket={'id':'test-codex','worldKey':'test-world','deadlineAt':int(time.time()*1000)+60000,
            'command':{'kind':'set_speed','unit_id':12,'knots':5},'reportToken':'fake-test-only'}
    handle=adapter.start(ticket)
    entry=adapter.runs[ticket['id']]
    assert entry['done'].wait(70),'Codex worker did not exit'
    state=adapter.status(ticket)
    result={'status':state,'speedWrites':len([p for p,b in calls if p.endswith('/speed')]),
            'reports':[{'status':b['status'],'receiptCmd':(b.get('receipt') or {}).get('cmd')} for p,b in calls if p=='/tickets'],
            'ownedProcessExited':entry['process'].poll() is not None}
    Path(args.out).write_text(json.dumps(result,indent=2),encoding='utf-8')
    print(json.dumps(result))
    assert result['speedWrites']==1,result
    assert result['reports']==[{'status':'injected','receiptCmd':123}],result
    assert state['exited'] and result['ownedProcessExited']
server.shutdown()
