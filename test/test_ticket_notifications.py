import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest
import uuid
from unittest.mock import patch

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT))
sys.path.insert(0,str(ROOT/'test'))
from ticket_store import TicketStore
from ticket_notifications import Notifications, event_id
import test_codex_chat as base
module=base.module


def create(store,world='game:1',session='parent',harness='codex'):
    result=store.apply({'op':'create','requestId':uuid.uuid4().hex,'worldKey':world,
        'title':'缺少目标','task':'不得自行选择目标','completion':'报告缺少目标',
        'command':{'kind':'mcp_task','tools':['sp_session_fleet'],'resources':['world'],'max_calls':1},
        'origin':{'harness':harness,'sessionId':session,'worldKey':world}},world,{'harness':'dsh'})
    return store.get(result['id'])


class NoticeStoreTest(unittest.TestCase):
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory();self.addCleanup(self.tmp.cleanup)
        self.store=TicketStore(Path(self.tmp.name)/'journal.jsonl')
        self.queue=Notifications(self.store.file.parent)
    def test_baseline_and_once_per_problem(self):
        old=self.store.change(create(self.store),status='failed',detail='old')
        self.queue.initialize(self.store.all())
        new=create(self.store)
        self.store.change(new,status='needs_input',detail='请指定目标',outcomeReason='需要目标')
        self.queue.emit(self.store,'game:1');self.queue.emit(self.store,'game:1')
        self.assertEqual(1,len(list(self.queue.events.glob('*.json'))))
        event=self.queue.next('codex','game:1','parent')
        self.assertNotEqual(old['id'],event['ticketId'])
        self.assertNotIn('reportToken',json.dumps(event))
        self.assertIn('需要目标',event['facts']['reason'])
        self.assertEqual('codex',event['origin']['harness']) # Origin, not execution backend.
        self.store.change(self.store.get(new['id']),workerExited=True)
        self.queue.emit(self.store,'game:1')
        self.assertEqual(1,len(list(self.queue.events.glob('*.json'))))
    def problem(self):
        self.queue.initialize([])
        ticket=self.store.change(create(self.store),status='blocked',detail='test')
        self.queue.emit(self.store,'game:1')
        return ticket
    def test_claim_crash_no_replay(self):
        self.problem();event=self.queue.next('codex','game:1','parent')
        self.queue.settle(event,'sending');self.queue.recover('dsh')
        self.assertEqual('sending',self.queue.status(event)['state'])
        self.queue.recover('codex')
        self.assertEqual('unknown',self.queue.status(event)['state'])
        self.assertIsNone(self.queue.next('codex','game:1','parent'))
    def test_wrong_session_skips_no_retarget(self):
        self.problem()
        self.assertIsNone(self.queue.next('codex','game:1','another'))
        self.assertIsNone(self.queue.next('codex','game:1','parent'))
    def test_destroy_before_send(self):
        ticket=self.problem();self.store.change(ticket,hidden=True)
        self.assertIsNone(self.queue.next('codex','game:1','parent'))
    def test_world_and_unbound_never_guessed(self):
        self.queue.initialize([])
        t=create(self.store);self.store.change(t,status='blocked',origin=None)
        self.queue.emit(self.store,'game:1')
        self.assertEqual([],list(self.queue.events.glob('*.json')))
        t=create(self.store);self.store.change(t,status='needs_input')
        self.queue.emit(self.store,'game:2')
        self.assertEqual([],list(self.queue.events.glob('*.json')))
    def test_invalid_origin_rejected(self):
        from ticket_notifications import validate_origin
        with self.assertRaises(ValueError):validate_origin({'harness':'codex','sessionId':'x','worldKey':'old'},'new')


class CodexNoticeTest(unittest.TestCase):
    setUp=base.ChatTest.setUp
    tearDown=base.ChatTest.tearDown
    line=base.ChatTest.line
    finish=base.ChatTest.finish
    def problem(self):
        store=TicketStore(self.chat.notices.root/'journal.jsonl')
        self.chat.notices.initialize([])
        t=create(store,session=self.chat.session)
        store.change(t,status='needs_input',detail='目标不明确',outcomeReason='请指定目标')
        self.chat.notices.emit(store,self.world)
        return store,t
    def answer(self,text='请指定攻击目标。'):
        self.chat.handle({'method':'item/agentMessage/delta','params':{'threadId':self.chat.session,
            'turnId':self.chat.turn,'itemId':'notice-answer','delta':text}})
    def test_notice_queues_main_busy_and_player_priority(self):
        self.line();self.chat.tick();self.problem();initial=self.chat.turn
        self.chat.tick();self.assertEqual(initial,self.chat.turn);self.assertIsNone(self.chat.notification)
        self.finish();self.line('new order',identity=2);self.chat.tick()
        self.assertIsNone(self.chat.notification)
        self.finish();self.chat.tick();self.assertIsNotNone(self.chat.notification)
    def test_tools_denied_and_normal_tools_restored(self):
        self.line();self.chat.tick();self.finish();self.problem();self.chat.tick()
        event=self.chat.notification
        with patch.object(self.chat,'state',side_effect=AssertionError('Tool tried reading game')):
            with self.assertRaisesRegex(ValueError,'reply-only'):self.chat.tool('sp_comms_state',{})
        self.answer();self.finish()
        self.assertEqual('delivered',self.chat.notices.status(event)['state'])
        self.assertEqual(1,len([i for i in self.chat.items if i['kind']=='user']))
        self.assertNotIn('seapower-notice',json.dumps(self.chat.items))
        self.assertIsNone(self.chat.notification)
        self.assertIsInstance(self.chat.tool('sp_api_find',{'query':'plot'}),dict)
        self.chat.tick();self.assertIsNone(self.chat.turn)
    def test_stop_notice_suppresses_late_reply_and_no_replay(self):
        self.line();self.chat.tick();self.finish();self.problem();self.chat.tick();event=self.chat.notification
        self.answer('partial')
        module.atomic(self.chat.run/'codex.stop.json',{'sessionId':self.chat.session,
            'turnId':self.chat.turn,'worldKey':self.world,'expiresAt':module.now()+5000})
        self.chat.stop();self.answer('late');self.finish()
        self.assertFalse(any(i['kind']=='assistant' for i in self.chat.items))
        self.assertEqual('cancelled',self.chat.notices.status(event)['state'])
        self.chat.tick();self.assertIsNone(self.chat.turn)
    def test_world_change_cancels_notice_and_clears_reply(self):
        self.line();self.chat.tick();self.finish();self.problem();self.chat.tick();event=self.chat.notification
        self.answer();self.world='game:2';self.chat.tick()
        self.assertEqual([],self.chat.items)
        self.assertNotEqual('delivered',self.chat.notices.status(event)['state'])
    def test_destroy_active_notice_interrupts(self):
        self.line();self.chat.tick();self.finish();store,t=self.problem();self.chat.tick();event=self.chat.notification
        self.answer();store.change(store.get(t['id']),hidden=True)
        self.chat.tick();self.assertTrue(self.chat.cancelled)
        self.finish();self.assertEqual('cancelled',self.chat.notices.status(event)['state'])
    def test_host_origin_binding_cannot_be_overridden(self):
        self.line();self.chat.tick()
        spec=next(t for t in module.mcp_server.TOOLS if t['name']=='sp_ticket_create')
        seen=[]
        with patch.dict(spec,handler=lambda a:seen.append(a) or {'ok':True}):
            self.chat.tool('sp_ticket_create',{'origin':{'sessionId':'spoof'}})
        self.assertEqual(self.chat.session,seen[0]['origin']['sessionId'])


if __name__=='__main__':unittest.main()
