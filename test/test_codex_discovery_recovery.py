"""Protocol-level worker tests. No real model, game, or production runtime."""
import io
import json
from pathlib import Path
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import patch

sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from ticket_executor_codex import CodexExecutor


class Process:
    def __init__(self,messages):
        self.stdout=io.StringIO(''.join(json.dumps(m)+'\n' for m in messages))
        self.stdin=Sink();self.exited=False
    def poll(self):return 0 if self.exited else None
    def wait(self,**unused):self.exited=True;return 0
    def terminate(self):self.exited=True


class Sink(io.StringIO):
    def close(self):self.saved=self.getvalue();super().close()


class DiscoveryRecoveryTest(unittest.TestCase):
    def run_worker(self,name,args,fatal=False):
        temporary=tempfile.TemporaryDirectory();self.addCleanup(temporary.cleanup)
        comms=Path(temporary.name);(comms/'run/tickets').mkdir(parents=True)
        adapter=CodexExecutor(comms,binary='fake')
        ticket={'id':'unit-test','reportToken':'PRIVATE-CAPABILITY','deadlineAt':int(time.time()*1000)+10000,
                'worldKey':'world','command':{'kind':'mcp_task','tools':['*'],'resources':['world'],'max_calls':1},
                'task':'Read current fleet only'}
        def request(identity,tool,arguments):
            return {'id':identity,'method':'item/tool/call','params':{'threadId':'thread','turnId':'turn',
                    'tool':tool,'arguments':arguments}}
        messages=[{'id':1,'result':{}},{'id':2,'result':{'thread':{'id':'thread'}}},
                  {'id':3,'result':{'turn':{'id':'turn'}}},request(10,name,args),
                  request(11,'sp_api_schema',{'names':['sp_session_fleet']}),
                  request(12,'sp_api_finish',{'summary':'Protocol test finished'})]
        process=Process(messages)
        entry={'process':process,'stop':threading.Event(),'done':threading.Event(),
               'attempted':False,'report':None,'reportId':'report-test'}
        adapter.runs[ticket['id']]=entry
        adapter.records[ticket['id']]={'status':'running','exited':False}
        reports=[]
        adapter.bridge=lambda route,body=None:reports.append(body) or {'ok':True}
        original=adapter.tool
        def tool(t,e,n,a):
            if n=='sp_api_finish':e['genericFinished']=True;return {'ok':True}
            return original(t,e,n,a)
        adapter.tool=tool
        adapter.worker(ticket,entry)
        outputs=[json.loads(line) for line in process.stdin.saved.splitlines()]
        return adapter.records[ticket['id']],outputs,reports

    def test_schema_batch_over_limit_returns_error_then_worker_continues(self):
        record,outputs,reports=self.run_worker('sp_api_schema',{'names':['sp_unit']*9})
        self.assertEqual('completed',record['status']);self.assertTrue(record['exited'])
        replies={r['id']:r for r in outputs if 'result' in r}
        self.assertFalse(replies[10]['result']['success']);self.assertTrue(replies[11]['result']['success'])
        self.assertEqual(1,record['discoveryErrors']);self.assertIn('1..8',record['lastToolError'])
        self.assertEqual([],reports)

    def test_unknown_name_is_recoverable(self):
        record,outputs,reports=self.run_worker('sp_api_schema',{'names':['sp_invented']})
        self.assertEqual('completed',record['status']);self.assertIn('Unknown business tool',record['lastToolError'])

    def test_business_contract_error_is_still_fatal_and_diagnostic(self):
        record,outputs,reports=self.run_worker('sp_speed',{})
        self.assertEqual('failed',record['status']);self.assertEqual('sp_speed',record['lastTool'])
        self.assertIn('outside generic',record['lastError'])
        self.assertEqual('failed',reports[0]['status'])
        self.assertNotIn('PRIVATE-CAPABILITY',record['lastError'])


if __name__=='__main__':unittest.main()
