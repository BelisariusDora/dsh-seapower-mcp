import copy,json,tempfile,time,unittest,uuid
from pathlib import Path
from unittest.mock import patch
import mcp_server,ticket_api,ticket_main
from ticket_store import TicketStore
from ticket_scheduler import Scheduler
from ticket_notifications import Notifications

class MainTests(unittest.TestCase):
 def setUp(self):
  self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup)
  self.mod=Path(self.temp.name);self.root=self.mod/'comms/run/tickets'
  self.store=TicketStore(self.root/'journal.jsonl')
  self.origin={'harness':'dsh','sessionId':'main','worldKey':'world','turnId':'1'}
  self.cmd={'op':'create','requestId':str(uuid.uuid4()),'worldKey':'world','title':'read',
   'task':'Read only','completion':'Read once','origin':self.origin,
   'command':{'kind':'mcp_task','tools':['sp_unit'],'resources':['unit:12'],'max_calls':1}}
  self.result=self.store.apply(self.cmd,'world',main_only=True);self.id=self.result['id'];self.hold=False;self.world='world'
  self.calls=[]
 def latest(self):return TicketStore(self.root/'journal.jsonl')
 def args(self,**kw):return {'ticket_id':self.id,'_origin':self.origin,'call_id':str(uuid.uuid4()),
  'name':'sp_unit','arguments':{'unit_id':12},**kw}
 def bridge(self,method,path,body=None):
  if path=='/comms/state':return {'ok':True,'data':{'worldKey':self.world,'hold':self.hold}}
  if method=='GET' and path=='/tickets':return {'ok':True,'data':{'tickets':self.latest().snapshot(self.world)}}
  if method=='GET' and path=='/units/12':return {'ok':True,'data':{'id':12,'mine':True,'surface':True,'sub':False,'air':False,'isLand':False,'destroyed':False,'maxForwardKnots':25,'speedKts':0}}
  if method=='POST' and path=='/tickets':return {'ok':True,'data':self.latest().apply(body,self.world)}
  raise AssertionError((method,path))
 def invoke(self,args=None):
  spec=copy.copy(mcp_server.TOOLS_BY_NAME['sp_unit'])
  def handler(a):self.calls.append(a);return {'ok':True,'data':{'id':12}}
  spec['handler']=handler
  with patch('api_catalog.registry',return_value={'sp_unit':spec}):
   return ticket_main.execute('sp_main_api_call',args or self.args(),self.root,self.bridge)
 def finish(self,**kw):return ticket_main.execute('sp_main_api_finish',self.args(summary='Read once',outcome='fulfilled',**kw),self.root,self.bridge)
 def test_mode_frozen_duplicate_does_not_dispatch(self):
  self.assertEqual(self.result['executionMode'],'main');self.assertEqual(self.result['nextAction'],'execute_in_this_turn')
  self.assertTrue(self.latest().apply(self.cmd,'world',main_only=False)['duplicate'])
  self.assertEqual(self.latest().get(self.id)['executionMode'],'main')
  self.assertTrue(self.latest().get(self.id)['workerExited'])
  class Executor:
   def __getattr__(self,key):raise AssertionError('Worker must not be used: '+key)
  class Game:
   def state(s):return {'worldKey':'world','hold':False}
  scheduler=Scheduler(self.mod/'comms',Game(),Executor())
  self.assertEqual(scheduler.store.get(self.id)['status'],'uncertain')
  scheduler.tick()
 def test_exact_origin_and_worker_capability_not_exposed(self):
  for field in ('sessionId','worldKey','turnId','harness'):
   with self.assertRaises(ValueError):self.invoke(self.args(_origin={**self.origin,field:'wrong'}))
  with self.assertRaises(ValueError):self.invoke(self.args(_origin=None))
  for spec in ticket_main.helper_specs():
   self.assertNotIn('report_token',spec['inputSchema']['properties'])
  with self.assertRaises(ValueError):self.latest().apply({**self.cmd,'requestId':str(uuid.uuid4()),'origin':None,'command':{'kind':'mcp_task','resources':['plot']}},'world',main_only=True)
 def test_receipts_budget_and_finish(self):
  args=self.args();self.invoke(args);self.assertTrue(self.invoke(args)['duplicate'])
  with self.assertRaises(ValueError):self.invoke()
  self.finish();ticket=self.latest().get(self.id)
  self.assertEqual(ticket['status'],'executed');self.assertTrue(ticket['mainReported']);self.assertFalse(ticket['resultVerified'])
  self.assertEqual(len(self.calls),1)
 def test_needs_input_returns_once_without_waking_main(self):
  notices=Notifications(self.root);notices.initialize(self.latest().all())
  ticket_main.execute('sp_main_api_finish',self.args(summary='Target unclear',outcome='needs_input',reason='Which target?'),self.root,self.bridge)
  notices.emit(self.latest(),'world');self.assertFalse(list(notices.events.glob('*.json')))
 def test_hold_destroy_and_changed_world(self):
  self.hold=True;self.invoke() # reads survive HOLD
  self.latest().apply({'op':'destroy','requestId':str(uuid.uuid4()),'worldKey':'world','id':self.id},'world')
  with self.assertRaises(ValueError):self.invoke()
  self.assertEqual(len(self.calls),1)
 def test_stopped_turn_and_unknown_write_never_replay(self):
  ticket_main.end_turn({'_origin':self.origin,'cancelled':True},self.root,self.bridge)
  self.assertEqual(self.latest().get(self.id)['status'],'cancelled')
  with self.assertRaises(ValueError):self.invoke()
 def test_natural_end_without_finish_is_uncertain(self):
  ticket_main.end_turn({'_origin':self.origin},self.root,self.bridge)
  self.assertEqual(self.latest().get(self.id)['status'],'uncertain')
 def test_settings_default_and_inline_comment(self):
  file=self.mod/'setting.ini';self.assertFalse(ticket_main.enabled(file))
  file.write_text('[Tickets]\nMainAgentOnly=true ; live\n');self.assertTrue(ticket_main.enabled(file))
  file.write_text('[Tickets]\nMainAgentOnly=false\n');self.assertFalse(ticket_main.enabled(file))
 def test_scheduler_reads_setting_and_skips_executor(self):
  # Remove only the test ticket's active scope so a new task can be admitted.
  self.latest().apply({'op':'destroy','requestId':str(uuid.uuid4()),'worldKey':'world','id':self.id},'world')
  (self.mod/'setting.ini').write_text('[Tickets]\nMainAgentOnly=true\n')
  class Executor:
   def __getattr__(s,key):raise AssertionError('Worker used: '+key)
  class Game:
   def state(s):return {'worldKey':'world','hold':False}
  scheduler=Scheduler(self.mod/'comms',Game(),Executor())
  req=uuid.uuid4().hex;command={**self.cmd,'requestId':req}
  (scheduler.commands/(req+'.json')).write_text(json.dumps(command))
  scheduler.tick();response=json.loads((scheduler.responses/(req+'.json')).read_text())
  self.assertEqual(response['executionMode'],'main');self.assertEqual(response['status'],'running')
 def test_speed_requires_exact_command_and_retains_monitoring(self):
  self.latest().apply({'op':'destroy','requestId':str(uuid.uuid4()),'worldKey':'world','id':self.id},'world')
  cmd={**self.cmd,'requestId':str(uuid.uuid4()),'command':{'kind':'set_speed','unit_id':12,'knots':5},
       'predicate':{'kind':'speed_reached','unit_id':12,'target_knots':5}}
  self.id=self.latest().apply(cmd,'world',main_only=True)['id']
  spec=copy.copy(mcp_server.TOOLS_BY_NAME['sp_set_speed']);writes=[]
  def handler(a):writes.append(a);return {'ok':True,'cmd':23,'data':{'id':12,'action':'speed','knots':5}}
  spec['handler']=handler
  with patch('api_catalog.registry',return_value={'sp_set_speed':spec}):
   for arguments in ({'unit_id':13,'knots':5},{'unit_id':12,'knots':6}):
    with self.assertRaises(ValueError):ticket_main.execute('sp_main_api_call',self.args(name='sp_set_speed',arguments=arguments),self.root,self.bridge)
   self.hold=True
   with self.assertRaises(ValueError):ticket_main.execute('sp_main_api_call',self.args(name='sp_set_speed',arguments={'unit_id':12,'knots':5}),self.root,self.bridge)
   self.hold=False
   ticket_main.execute('sp_main_api_call',self.args(name='sp_set_speed',arguments={'unit_id':12,'knots':5}),self.root,self.bridge)
  self.finish();self.assertEqual(self.latest().get(self.id)['status'],'monitoring');self.assertEqual(len(writes),1)

if __name__=='__main__':unittest.main()
