"""Structured outcomes and receipt-backed details. No live game or model calls."""
import copy
import json
from pathlib import Path
import sys
import tempfile
import unittest
import uuid

sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from ticket_store import TicketStore, FINAL
import ticket_api
from ticket_presentation import evidence, cached


class ResultTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup)
        self.root=Path(self.temp.name)
        self.store=TicketStore(self.root/'journal.jsonl')
        result=self.store.apply({'op':'create','requestId':str(uuid.uuid4()),'worldKey':'w',
            'title':'SH-2 attack','task':'Attack the specified contact; clarify if ambiguous',
            'completion':'Authorized attack command submitted','command':{'kind':'mcp_task'}},'w')
        self.t=self.store.change(self.store.get(result['id']),status='running')
    def row(self,name='sp_tracks',read=True,status='succeeded',**fields):
        row={'call_id':uuid.uuid4().hex,'worldKey':'w','name':name,'arguments':{},'readOnly':read,
             'status':status,'at':1,'receipt':{'ok':True,'data':{}},**fields}
        directory=self.root/'api_calls'/self.t['id'];directory.mkdir(parents=True,exist_ok=True)
        (directory/(row['call_id']+'.json')).write_text(json.dumps(row),encoding='utf-8')
        return row
    def finish(self,outcome=None,summary='未下达攻击：目标有歧义',reason='目标存在多个候选'):
        args={'ticket_id':self.t['id'],'report_token':self.t['reportToken'],'summary':summary}
        if outcome is not None:args.update(outcome=outcome,reason=reason)
        def bridge(method,path,body):
            return {'ok':True,'data':self.store.apply(body,'w')}
        reply=ticket_api.finish(args,self.root,bridge)
        self.t=self.store.get(self.t['id']);return reply
    def test_readonly_blocker_is_not_executed(self):
        self.row();self.finish('blocked')
        self.assertEqual('blocked',self.t['status']);self.assertEqual('blocked',self.t['taskOutcome'])
        view=self.store.snapshot('w')[0]
        self.assertEqual('受阻',view['resultLabel']);self.assertEqual([],view['confirmedCommands'])
        self.assertEqual('目标存在多个候选',view['summary']);self.assertFalse(self.t['resultVerified'])
    def test_clarification_without_any_game_call_is_terminal(self):
        self.finish('needs_input')
        self.assertEqual('needs_input',self.t['status']);self.assertIn(self.t['status'],FINAL)
        self.assertEqual(0,self.t['callCount'])
    def test_reason_does_not_replace_full_worker_report_in_details(self):
        self.row();self.finish('needs_input',summary='已核对武器，未下达攻击；需要目标编号',reason='指定航迹号')
        view=self.store.snapshot('w')[0]
        self.assertEqual('指定航迹号',view['summary'])
        self.assertIn('已核对武器，未下达攻击',view['detailText'])

    def test_empty_success_cannot_be_claimed(self):
        with self.assertRaises(ValueError):self.finish('fulfilled')
    def test_blocker_requires_reason(self):
        with self.assertRaises(ValueError):self.finish('blocked',reason='')
    def test_unknown_outcome_is_refused(self):
        self.row()
        with self.assertRaises(ValueError):self.finish('victory')
    def test_unknown_write_overrides_worker_claim(self):
        self.row('sp_fire',False,'uncertain',receipt={'ok':False,'uncertain':True})
        self.finish('fulfilled')
        self.assertEqual('uncertain',self.t['status'])
        view=self.store.snapshot('w')[0]
        self.assertEqual([],view['confirmedCommands']);self.assertEqual(1,len(view['unconfirmedWrites']))
    def test_failed_api_overrides_blocker(self):
        self.row(status='failed',receipt={'ok':False,'error':'unavailable'})
        self.finish('blocked');self.assertEqual('failed',self.t['status'])
    def test_readonly_inspection_can_legitimately_be_fulfilled(self):
        self.row('sp_session_fleet');self.finish('fulfilled',summary='编成读取完毕')
        self.assertEqual('executed',self.t['status']);self.assertEqual('fulfilled',self.t['taskOutcome'])
        self.assertFalse(self.t['resultVerified'])
    def test_partial_preserves_actual_submitted_command(self):
        self.row('sp_move',False,arguments={'unit_id':329,'lat':31.6,'lon':26.8},
                 receipt={'ok':True,'data':{'executed':True,'confirmation':'unverified'}})
        self.finish('partial',summary='仅完成机动，攻击受阻')
        view=self.store.snapshot('w')[0]
        self.assertEqual('部分处理',view['resultLabel']);self.assertEqual(1,len(view['confirmedCommands']))
        self.assertIn('unverified',view['detailText'])
    def test_legacy_readonly_task_does_not_infer_success_from_prose(self):
        self.row();self.finish(summary='APIs executed; game effects not independently verified. 未下达攻击')
        view=self.store.snapshot('w')[0]
        self.assertEqual('unreported',view['taskOutcome']);self.assertIn('结果未报告',view['resultLabel'])
        self.assertEqual('未下达攻击',view['summary'])
        self.assertIn('未记录游戏写入',view['commandSummary'])
    def test_summary_cannot_forge_host_commands(self):
        self.row();self.finish('fulfilled',summary='已发射全部鱼雷')
        view=self.store.snapshot('w')[0]
        self.assertEqual([],view['confirmedCommands']);self.assertIn('未记录游戏写入',view['commandSummary'])
    def test_cached_view_refreshes_when_ledger_changes(self):
        cache={};a=cached(self.t,self.root,cache)
        self.row();b=cached(self.t,self.root,cache)
        self.assertEqual(0,a['readCount']);self.assertEqual(1,b['readCount'])
    def test_malformed_ledger_is_not_presented_as_no_writes(self):
        row=self.row();p=self.root/'api_calls'/self.t['id']/(row['call_id']+'.json');p.write_text('{',encoding='utf-8')
        view=cached(self.t,self.root,{})
        self.assertTrue(view['evidenceUnavailable']);self.assertIn('不能据此判断',view['commandSummary'])
    def test_private_arguments_redacted(self):
        row=self.row(arguments={'report_token':'PRIVATE','nested':{'password':'SECRET'}})
        view=evidence(self.t,[row]);encoded=json.dumps(view,ensure_ascii=False)
        self.assertNotIn('PRIVATE',encoded);self.assertNotIn('SECRET',encoded)
    def test_validation_only_or_explicit_nonexecution_is_not_injected(self):
        row=self.row('sp_attack',False,arguments={'validate_only':True},receipt={'ok':True,'data':{'executed':False}})
        self.assertEqual([],evidence(self.t,[row])['confirmedCommands'])
        row['arguments']={}
        view=evidence(self.t,[row])
        self.assertEqual([],view['confirmedCommands']);self.assertEqual(1,len(view['unconfirmedWrites']))

    def test_watch_creation_is_not_watch_hit(self):
        row=self.row('sp_watch_create',False,arguments={'field':'Velocity','ttl_s':30},
                     receipt={'ok':True,'data':{'id':'w1','status':'active','world_generation':2}})
        view=evidence(self.t,[row]);self.assertEqual('active',view['watchEvidence'][0]['evidence']['status'])
        self.assertIn('仅显示已记录证据',view['detailText']);self.assertNotIn('已验证达标',view['detailText'])
    def test_watch_failure_and_boundary_reason_preserved(self):
        row=self.row('sp_watch_get',True,receipt={'ok':True,'data':{'state':'expired','reason':'world_changed','error':'field_unreadable'}})
        view=evidence(self.t,[row]);self.assertIn('world_changed',view['detailText']);self.assertIn('field_unreadable',view['detailText']);self.assertIn('world_changed',view['watchSummary'])
    def test_expired_event_in_batch_is_visible_in_card_summary(self):
        row=self.row('sp_watch_events',True,receipt={'ok':True,'data':{'events':[{'kind':'expired','reason':'deadline reached','evidence':False}]}})
        view=evidence(self.t,[row])
        self.assertIn('expired',view['watchSummary']);self.assertIn('deadline reached',view['watchSummary'])
        self.assertFalse(view['watchEvidence'][0]['evidence']['events'][0]['evidence'])

    def test_speed_completion_retains_two_sample_evidence(self):
        t={**self.t,'status':'completed','resultVerified':True,'command':{'kind':'set_speed','unit_id':12,'knots':5},
           'predicate':{'kind':'speed_reached','target_knots':5},'stableSamples':2,
           'observation':{'sampleId':'fresh:2','speedKts':5},'receipt':{'ok':True,'data':{'id':12,'knots':5}}}
        view=evidence(t,[]);self.assertEqual('已验证达标',view['resultLabel']);self.assertEqual(1,len(view['confirmedCommands']))
        self.assertIn('fresh:2',view['detailText']);self.assertIn('2/2',view['detailText'])
    def test_old_world_records_are_not_in_new_world_snapshot(self):
        self.row();self.finish('blocked')
        self.assertEqual([],self.store.snapshot('different-world'))
    def test_forged_empty_receipt_cannot_hide_existing_calls(self):
        self.row()
        with self.assertRaises(ValueError):
            self.store.apply({'op':'report','id':self.t['id'],'worldKey':'w','requestId':str(uuid.uuid4()),
                'reportToken':self.t['reportToken'],'status':'blocked','taskOutcome':'blocked',
                'outcomeReason':'target unknown','detail':'blocked','receipt':{'call_ids':[]}},'w')

class BlockedLifecycleTest(unittest.TestCase):
    def test_blocked_worker_is_stopped_and_slot_waits_for_native_exit(self):
        from test_ticket_scheduler import Game, Executor
        from ticket_scheduler import Scheduler
        with tempfile.TemporaryDirectory() as temporary:
            game=Game();executor=Executor();scheduler=Scheduler(temporary,game,executor);executor.scheduler=scheduler
            def create(unit):
                return scheduler.store.apply({'op':'create','requestId':uuid.uuid4().hex,'worldKey':game.world,
                    'title':'test','task':'test only','completion':'record result',
                    'command':{'kind':'mcp_task','resources':['unit:'+str(unit)]}},game.world,executor.context(game.world))['id']
            first=create(12);second=create(13);scheduler.tick()
            ticket=scheduler.store.get(first)
            scheduler.store.apply({'op':'report','id':first,'requestId':uuid.uuid4().hex,'worldKey':game.world,
                'reportToken':ticket['reportToken'],'status':'blocked','taskOutcome':'blocked',
                'outcomeReason':'No target','detail':'No order submitted','receipt':{'call_ids':[]}},game.world)
            attempts=[];exited=[False]
            executor.stop=lambda row:attempts.append(row['id']) or exited[0]
            scheduler.tick()
            self.assertEqual([first],attempts);self.assertEqual([first],executor.starts)
            self.assertEqual('blocked',scheduler.store.get(first)['status'])
            exited[0]=True;scheduler.tick()
            self.assertTrue(scheduler.store.get(first)['workerExited'])
            self.assertEqual([first,second],executor.starts)

if __name__=='__main__':unittest.main()
