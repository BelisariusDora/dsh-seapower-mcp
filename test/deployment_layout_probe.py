import argparse
import json
from pathlib import Path
import subprocess
import sys

parser=argparse.ArgumentParser(description='Read-only deployment module/import/entry probe; no game or model calls.')
parser.add_argument('root',help='Mod installation root')
parser.add_argument('--out',help='Optional evidence JSON')
args=parser.parse_args()
root=Path(args.root).resolve()
python=sys.executable
request=json.dumps({'jsonrpc':'2.0','id':1,'method':'tools/call','params':{'name':'sp_api_find','arguments':{'domain':'plot','query':'marker','limit':1}}})+'\n'
checks=[]
for entry in (root/'mcp_server.py',root/'modules/mcp/mcp_server.py'):
    process=subprocess.run([python,str(entry)],input=request,text=True,encoding='utf-8',capture_output=True,timeout=20)
    assert process.returncode==0,process.stderr
    reply=json.loads(process.stdout)['result']
    assert not reply['isError'],reply
    data=json.loads(reply['content'][0]['text'])
    assert data['tools'][0]['name']=='sp_plot_marker'
    checks.append({'entry':str(entry.relative_to(root)),'catalogWorks':True})
for entry in (root/'ticket_scheduler.py',root/'modules/tickets/ticket_scheduler.py',root/'harness/Codex/codex_chat.py'):
    process=subprocess.run([python,str(entry),'--help'],capture_output=True,text=True,encoding='utf-8',timeout=15)
    assert process.returncode==0 and ('--executor' in process.stdout or '--codex' in process.stdout),process.stderr
    checks.append({'entry':str(entry.relative_to(root)),'cliWorks':True})
code="""
import argparse,sys,json
from pathlib import Path
parser=argparse.ArgumentParser(description='Read-only deployment module/import/entry probe; no game or model calls.')
parser.add_argument('root',help='Mod installation root')
parser.add_argument('--out',help='Optional evidence JSON')
args=parser.parse_args()
root=Path(args.root).resolve()
sys.path.insert(0,str(root))
import mcp_server,compact_replies,watch_events,api_catalog,track_briefs
import ticket_store,ticket_executor_dsh,ticket_executor_codex
assert Path(mcp_server.__file__).parent==root/'modules/mcp'
assert Path(ticket_store.__file__).parent==root/'modules/tickets'
assert Path(api_catalog.__file__).parent==root/'modules/api'
assert Path(ticket_executor_dsh.__file__).parent==root/'harness/DSH'
assert Path(ticket_executor_codex.__file__).parent==root/'harness/Codex'
assert watch_events.MOD_DIR==root
assert compact_replies.SETTINGS==root/'setting.ini'
assert track_briefs.MODEL_PATH==root/'modules/mcp/track_mlp_weights.json'
assert track_briefs.MODEL_PATH.exists()
assert len(api_catalog.helper_specs())==4
print(json.dumps({'tools':len(mcp_server.TOOLS),'importsAndResourcePaths':True}))
"""
process=subprocess.run([python,'-c',code,str(root)],capture_output=True,text=True,encoding='utf-8',timeout=20)
assert process.returncode==0,process.stderr
checks.append(json.loads(process.stdout))
result={'layout':str(root),'checks':checks}
print(json.dumps(result))
if args.out:Path(args.out).write_text(json.dumps(result,indent=2),encoding='utf-8')
