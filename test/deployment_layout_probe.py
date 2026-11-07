import argparse
import json
import re
from pathlib import Path
import subprocess
import sys
from urllib.request import Request, build_opener, ProxyHandler

parser=argparse.ArgumentParser(description='Read-only deployment module/import/entry probe; no game or model calls.')
parser.add_argument('root',help='Mod installation root')
parser.add_argument('--out',help='Optional evidence JSON')
parser.add_argument('--dsh-health', action='store_true', help='Also verify the loaded local DSH plugin using the installed scheduler token')
parser.add_argument('--dsh-port', type=int, default=19389, help='Local DSH plugin port, default 19389')
args=parser.parse_args()
root=Path(args.root).resolve()
python=sys.executable
request=json.dumps({'jsonrpc':'2.0','id':1,'method':'tools/call','params':{'name':'sp_api_find','arguments':{'domain':'plot','query':'marker','limit':1}}})+'\n'
checks=[]
for relative in ('persona.md', 'harness/DSH/prompts/onceopen.md', 'harness/Codex/prompts/onceopen.md'):
    prompt = (root / relative).read_text(encoding='utf-8-sig').strip()
    assert prompt, f'Empty prompt: {relative}'
    assert '禁止调用 ask_user_question' in prompt, f'Missing text-only clarification rule: {relative}'
    checks.append({'prompt': relative, 'textOnlyClarification': True})
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
health = {'status': 'not_checked', 'reason': 'Pass --dsh-health to verify the running DSH Host; file checks do not prove plugin loading or sender mode'}
if args.dsh_health:
    assert 1 <= args.dsh_port <= 65535, 'Invalid DSH plugin port'
    plugin = (root / 'harness/DSH/dsh_ticket_plugin.mjs').read_text(encoding='utf-8')
    revision = re.search(r'export const revision\s*=\s*"([^"]+)"', plugin)
    assert revision, 'Installed plugin does not export a revision'
    token = (root / 'comms/run/tickets/auth.token').read_text(encoding='utf-8').strip()
    assert token, 'Scheduler token is empty; start the scheduler before checking DSH health'
    request = Request(f'http://127.0.0.1:{args.dsh_port}/health', headers={'Authorization': 'Bearer ' + token})
    with build_opener(ProxyHandler({})).open(request, timeout=5) as response:
        loaded = json.load(response)
    expected = {'ok': True, 'revision': revision[1], 'ticketProtocol': 2, 'apiTicketProtocol': 1, 'taskResultProtocol': 1, 'notificationProtocol': 1}
    for key, value in expected.items():
        assert loaded.get(key) == value, f'DSH health mismatch for {key}: expected {value!r}, got {loaded.get(key)!r}'
    health = {'status': 'passed', **expected}
result={'layout':str(root),'checks':checks,'dshHost':health}
print(json.dumps(result))
if args.out:Path(args.out).write_text(json.dumps(result,indent=2),encoding='utf-8')
