import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT))
spec=importlib.util.spec_from_file_location('codex_chat', ROOT/'comms/codex_chat.py')
module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
from ticket_executor import ExecutorRouter


class Server:
    serial=0
    def __init__(self,binary):
        Server.serial+=1
        self.identity=str(Server.serial)
        self.messages=[];self.calls=[];self.closed=False
    def rpc(self,method,params,**kw):
        self.calls.append((method,params))
        if method=='thread/start':return {'thread':{'id':self.identity}}
        if method=='turn/start':return {'turn':{'id':'turn-'+str(len(self.calls))}}
        return {}
    def send(self,value):self.calls.append(('send',value))
    def poll(self):return self.messages.pop(0) if self.messages else None
    def close(self):self.closed=True


class ChatTest(unittest.TestCase):
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory()
        self.root=Path(self.tmp.name)
        self.chat=module.Chat(self.root/'comms','fake',factory=Server)
        self.world='game:1'
        self.chat.state=lambda:{'worldKey':self.world,'hold':False}
    def tearDown(self):self.chat.close();self.tmp.cleanup()
    def line(self,text='hello',world=None,identity=1):
        with self.chat.outbox.open('ab') as stream:
            stream.write((json.dumps({'id':identity,'text':text,'from':'player','world':world or self.world})+'\n').encode())
    def finish(self):
        self.chat.handle({'method':'turn/completed','params':{'threadId':self.chat.session,
            'turn':{'id':self.chat.turn,'status':'completed'}}})

    def test_mcp_send_preserves_codex_target(self):
        seen=[]
        with patch.object(module.mcp_server,'call',side_effect=lambda *a:seen.append(a) or {'ok':True}):
            module.mcp_server.t_comms_send({'text':'hello','target':'codex'})
        self.assertEqual('codex',seen[0][2]['target'])

    def test_menu_heartbeat_without_session(self):
        self.world=None;self.chat.tick()
        state=json.loads((self.chat.run/'codex.chat.json').read_text('utf-8'))
        self.assertEqual('no-session',state['link']);self.assertIsNone(state['sessionId'])

    def test_dsh_resets_never_clear_codex_records(self):
        source=(ROOT/'src/CommsBridge.cs').read_text('utf-8')
        before_clear=source.split('public static JObject Clear()')[0]
        self.assertNotIn('_items.Clear()',before_clear)
        self.assertNotIn('_out.Clear()',before_clear)

    def test_ephemeral_same_world_and_new_world(self):
        self.line();self.chat.tick();first=self.chat.server;identity=self.chat.session
        settings=next(p for m,p in first.calls if m=='thread/start')
        self.assertTrue(settings['ephemeral']);self.assertEqual('read-only',settings['sandbox'])
        self.finish();self.line(identity=2);self.chat.tick();self.assertEqual(identity,self.chat.session)
        self.world='game:2';self.chat.tick();self.assertTrue(first.closed)
        self.assertEqual([],self.chat.items);self.line(identity=3);self.chat.tick()
        self.assertNotEqual(identity,self.chat.session)
    def test_old_queue_and_partial_line_not_replayed(self):
        self.line(world='old');self.chat.tick();self.assertIsNone(self.chat.turn)
        with self.chat.outbox.open('ab') as stream:stream.write(b'{"world":')
        offset=self.chat.cursor['offset'];self.chat.tick();self.assertEqual(offset,self.chat.cursor['offset'])
    def test_hidden_injection_only_first_and_persona_hot_reload(self):
        (self.root/'persona.md').write_text('NAVY ONE',encoding='utf-8')
        self.line();self.chat.tick();turns=[p for m,p in self.chat.server.calls if m=='turn/start']
        self.assertIn('NAVY ONE',turns[-1]['input'][0]['text'])
        self.assertEqual('hello',self.chat.items[0]['text'])
        self.assertNotIn('seapower-context',json.dumps(self.chat.items))
        self.finish();(self.root/'persona.md').write_text('NAVY TWO',encoding='utf-8')
        self.line(identity=2);self.chat.tick()
        turns=[p for m,p in self.chat.server.calls if m=='turn/start']
        self.assertIn('NAVY TWO',turns[-1]['input'][0]['text'])
    def test_stop_drops_partial_and_late_reply(self):
        self.line();self.chat.tick()
        params={'threadId':self.chat.session,'turnId':self.chat.turn,'itemId':'answer','delta':'partial'}
        self.chat.handle({'method':'item/agentMessage/delta','params':params})
        module.atomic(self.chat.run/'codex.stop.json',{'sessionId':self.chat.session,'turnId':self.chat.turn,
            'worldKey':self.world,'expiresAt':module.now()+5000})
        self.chat.stop();self.chat.handle({'method':'item/agentMessage/delta','params':params})
        self.assertFalse(any(x['kind']=='assistant' for x in self.chat.items))
        self.assertTrue(self.chat.cancelled)
        self.assertTrue(any(m=='turn/interrupt' for m,p in self.chat.server.calls))
    def test_stale_stop_cannot_stop_current_turn(self):
        self.line();self.chat.tick()
        module.atomic(self.chat.run/'codex.stop.json',{'sessionId':self.chat.session,'turnId':'old',
            'expiresAt':module.now()+5000})
        self.chat.stop();self.assertFalse(self.chat.cancelled)
    def test_world_guard_and_executor_injected(self):
        self.line();self.chat.tick()
        spec=next(t for t in module.mcp_server.TOOLS if t['name']=='sp_ticket_create')
        seen=[]
        with patch.dict(spec,handler=lambda a:seen.append(a) or {'ok':True}):
            self.chat.tool('sp_ticket_create',{'executor':'dsh'})
        self.assertEqual('codex',seen[0]['executor']);self.assertEqual(self.world,seen[0]['worldKey'])
        self.world='changed'
        with self.assertRaises(ValueError):self.chat.tool('sp_ticket_create',{})
    def test_unknown_tool_and_reasoning_not_exposed(self):
        self.line();self.chat.tick()
        with self.assertRaises(ValueError):self.chat.tool('sp_speed',{})
        self.chat.handle({'method':'item/reasoning/textDelta','params':{'threadId':self.chat.session,
            'turnId':self.chat.turn,'delta':'SECRET'}})
        self.assertNotIn('SECRET',json.dumps(self.chat.items))
    def test_disconnect_keeps_session_without_draining_events(self):
        self.line();self.chat.tick();server=self.chat.server
        self.chat.state=lambda:(_ for _ in ()).throw(OSError('offline'))
        self.chat.tick();self.assertIs(server,self.chat.server);self.assertFalse(server.closed)
        self.assertEqual('down',json.loads((self.chat.run/'codex.chat.json').read_text('utf-8'))['link'])
    def test_cursor_prevents_restart_replay(self):
        self.line();self.chat.tick();self.chat.close()
        restarted=module.Chat(self.root/'comms','fake',factory=Server)
        restarted.state=self.chat.state;restarted.tick()
        self.assertIsNone(restarted.turn);restarted.close()
    def test_main_helper_lookup(self):
        self.line();self.chat.tick()
        result=self.chat.tool('sp_api_find',{'query':'plot'})
        self.assertIsInstance(result,dict)


class RouterTest(unittest.TestCase):
    def test_routes_lifecycle_and_default(self):
        class Adapter:
            def __init__(self,name):self.name=name;self.called=[]
            def context(self,world):return {'harness':self.name}
            def start(self,t):self.called.append('start');return self.name
            def status(self,t):self.called.append('status');return self.name
            def stop(self,t):self.called.append('stop');return True
        ds,cx=Adapter('dsh'),Adapter('codex');router=ExecutorRouter({'dsh':ds,'codex':cx})
        self.assertEqual('dsh',router.context('world')['harness'])
        ticket={'executorContext':router.select_context('world','codex')}
        self.assertEqual('codex',router.start(ticket));router.status(ticket);router.stop(ticket)
        self.assertEqual(['start','status','stop'],cx.called);self.assertEqual([],ds.called)
        with self.assertRaises(ValueError):router.select_context('world','unknown')


class RoutingIntegrationTest(unittest.TestCase):
    def test_shared_slot_cancel_and_pending_backend_preserved(self):
        import uuid
        from test_ticket_scheduler import Game, Executor
        from ticket_scheduler import Scheduler
        class Named(Executor):
            def __init__(self,name):super().__init__();self.name=name
            def context(self,world):return {'harness':self.name} if self.ready else None
        with tempfile.TemporaryDirectory() as tmp:
            game=Game();ds,cx=Named('dsh'),Named('codex')
            router=ExecutorRouter({'dsh':ds,'codex':cx})
            scheduler=Scheduler(tmp,game,router)
            ds.scheduler=cx.scheduler=scheduler
            def send(body):
                body={'requestId':uuid.uuid4().hex,'worldKey':game.world,**body}
                file=scheduler.commands/(body['requestId']+'.json')
                file.write_text(json.dumps(body),encoding='utf-8');scheduler.tick()
                return json.loads((scheduler.responses/file.name).read_text('utf-8'))
            def create(executor,unit):
                return send({'op':'create','executor':executor,'title':'test','task':'speed only',
                    'completion':'observed speed','command':{'kind':'set_speed','unit_id':unit,'knots':5},
                    'predicate':{'kind':'speed_reached','unit_id':unit,'target_knots':5}})
            ds.ready=False
            pending=create('dsh',11)['id']
            self.assertEqual('dsh',scheduler.store.get(pending)['executorContext']['harness'])
            first=create('codex',12)['id']
            self.assertEqual([first],cx.starts);self.assertEqual([],ds.starts)
            second=create('codex',13)['id'];self.assertEqual([first],cx.starts)
            send({'op':'destroy','id':first})
            self.assertIn(first,cx.stops);self.assertEqual([],ds.stops)
            self.assertEqual([first,second],cx.starts)
            send({'op':'destroy','id':second})
            # Changing the default cannot steal a queued, explicitly DSH ticket.
            router.default='codex';ds.ready=True;scheduler.tick()
            self.assertEqual([pending],ds.starts)
            self.assertEqual('dsh',scheduler.store.get(pending)['executorContext']['harness'])

if __name__=='__main__':unittest.main()
