"""Codex tool host invariants; no model, game, or production state is used."""
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

class CodexTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        root=Path(self.temp.name)/'run/tickets'
        root.mkdir(parents=True)
        self.adapter=CodexExecutor(self.temp.name,binary='test-only')
        self.ticket={'id':'ticket-test','worldKey':'world-1','deadlineAt':int(time.time()*1000)+60000,
                     'command':{'kind':'set_speed','unit_id':12,'knots':5}}
        self.adapter.records['ticket-test']={'pid':0,'exited':False,'status':'running'}
        self.entry={'attempted':False,'stop':threading.Event(),'report':None}
        self.calls=[]
        def bridge(route,body=None):
            self.calls.append((route,body))
            if route=='/comms/state':return {'ok':True,'data':{'worldKey':'world-1','hold':False}}
            if route=='/tickets':return {'ok':True,'data':{'tickets':[{'id':'ticket-test','status':'running'}]}}
            if route=='/units/12/speed':return {'ok':True,'cmd':42,'data':{'id':12,'action':'speed','knots':5}}
            self.fail('Unexpected endpoint')
        self.adapter.bridge=bridge

    def test_bound_command_once_and_raw_receipt(self):
        reply=self.adapter.tool(self.ticket,self.entry,'sp_speed',{'unit_id':12,'knots':5})
        self.assertEqual(reply['cmd'],42)
        self.assertEqual(self.entry['report'][0],'injected')
        self.assertTrue(json.loads(self.adapter.file.read_text())['ticket-test']['writeAttempted'])
        with self.assertRaises(ValueError):self.adapter.tool(self.ticket,self.entry,'sp_speed',{'unit_id':12,'knots':5})
        self.assertEqual(len([r for r,b in self.calls if r.endswith('/speed')]),1)

    def test_arguments_cannot_expand_scope(self):
        for arguments in ({'unit_id':13,'knots':5},{'unit_id':12,'knots':6},{'unit_id':12,'knots':5,'extra':True}, {'unit_id':True,'knots':5}):
            with self.assertRaises(ValueError):self.adapter.tool(self.ticket,self.entry,'sp_speed',arguments)
        self.assertEqual(self.calls,[])

    def test_lost_write_never_retries(self):
        original=self.adapter.bridge
        def lost(route,body=None):
            if route.endswith('/speed'):raise TimeoutError('Reply lost')
            return original(route,body)
        self.adapter.bridge=lost
        with self.assertRaises(TimeoutError):self.adapter.tool(self.ticket,self.entry,'sp_speed',{'unit_id':12,'knots':5})
        self.assertEqual(self.entry['report'][0],'uncertain')
        with self.assertRaises(ValueError):self.adapter.tool(self.ticket,self.entry,'sp_speed',{'unit_id':12,'knots':5})

    def test_world_hold_hidden_and_deadline_prevent_write(self):
        original=self.adapter.bridge
        for mode in ('world','hold','hidden','deadline','cancel'):
            self.entry['stop'].clear()
            self.ticket['deadlineAt']=int(time.time()*1000)+60000
            def guarded(route,body=None):
                reply=original(route,body)
                if route=='/comms/state' and mode=='world':reply['data']['worldKey']='world-2'
                if route=='/comms/state' and mode=='hold':reply['data']['hold']=True
                if route=='/tickets' and mode=='hidden':reply['data']['tickets']=[]
                return reply
            self.adapter.bridge=guarded
            if mode=='deadline':self.ticket['deadlineAt']=0
            if mode=='cancel':self.entry['stop'].set()
            with self.assertRaises(ValueError):self.adapter.tool(self.ticket,self.entry,'sp_speed',{'unit_id':12,'knots':5})
        self.assertFalse(any(r.endswith('/speed') for r,b in self.calls))

    def test_unknown_tools_and_old_worker_identity_never_spawn(self):
        with self.assertRaises(ValueError):self.adapter.tool(self.ticket,self.entry,'shell',{})
        with self.assertRaises(ValueError):self.adapter.start(self.ticket)
        self.assertFalse(self.calls)

    def test_cancel_requires_exit_confirmation(self):
        entry={'stop':threading.Event(),'done':threading.Event()}
        self.adapter.runs['ticket-test']=entry
        self.assertFalse(self.adapter.stop(self.ticket))
        self.assertTrue(entry['stop'].is_set())
        entry['done'].set()
        self.assertTrue(self.adapter.stop(self.ticket))

    def test_restart_never_signals_or_replays_unowned_process(self):
        self.adapter.save('ticket-test',pid=12345,exited=False)
        restored=CodexExecutor(self.temp.name,binary='test-only')
        with patch('ticket_executor_codex.process_alive',return_value=True):
            self.assertEqual(restored.status(self.ticket)['status'],'unknown')
            self.assertFalse(restored.stop(self.ticket))
        with patch('ticket_executor_codex.process_alive',return_value=False):
            self.assertEqual(restored.status(self.ticket)['status'],'failed')
            self.assertTrue(restored.stop(self.ticket))
        with self.assertRaises(ValueError):restored.start(self.ticket)

if __name__=='__main__':unittest.main()
