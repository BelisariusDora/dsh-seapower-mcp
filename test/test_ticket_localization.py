import copy,json,re,tempfile,unittest,uuid
from pathlib import Path
from ticket_store import TicketStore
from ticket_presentation import evidence,cached,compact,translator,read_language
from ticket_scheduler import Scheduler

class LocalizationTests(unittest.TestCase):
 def setUp(self):
  self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup)
  self.mod=Path(self.temp.name);self.root=self.mod/'comms/run/tickets'
  self.store=TicketStore(self.root/'journal.jsonl')
  result=self.store.apply({'op':'create','requestId':str(uuid.uuid4()),'worldKey':'w',
   'title':'Read fleet','task':'Read once','completion':'One receipt','command':{'kind':'mcp_task'}},'w')
  self.t=self.store.change(self.store.get(result['id']),status='executed',taskOutcome='fulfilled',detail='Original report',workerExited=True)
 def test_english_chrome_contains_no_generated_chinese(self):
  view=evidence(self.t,[],language='en')
  self.assertEqual('Executed · effects unverified',view['resultLabel'])
  for key in ('resultLabel','detailText','commandSummary','watchSummary'):
   self.assertIsNone(re.search('[\u4e00-\u9fff]',view[key]),key)
  self.assertEqual('Original report',view['summary'])
 def test_model_text_and_json_evidence_are_never_translated(self):
  self.t.update(detail='无：中文原始回报',task='任务结果由用户指定')
  row={'call_id':'one','name':'sp_plot_text','arguments':{'text':'受阻'},'readOnly':False,'status':'succeeded',
   'receipt':{'ok':True,'data':{'action':'无'}}}
  view=evidence(self.t,[row],language='en')
  self.assertEqual('无：中文原始回报',view['summary'])
  self.assertIn('任务结果由用户指定',view['detailText'])
  self.assertEqual({'text':'受阻'},view['confirmedCommands'][0]['arguments'])
  self.assertIn('"action":"无"',view['detailText'])
 def test_cache_switches_language_without_mutating_ticket_or_ledger(self):
  before=copy.deepcopy(self.t);cache={}
  en=cached(self.t,self.root,cache,'en');zh=cached(self.t,self.root,cache,'zh')
  self.assertNotEqual(en['resultLabel'],zh['resultLabel'])
  self.assertEqual('已处理 · 效果未验证',zh['resultLabel'])
  self.assertEqual(en,cached(self.t,self.root,cache,'en'));self.assertEqual(before,self.t)
 def test_scheduler_uses_resolved_game_language_on_live_switch(self):
  class Game:
   language='en'
   def state(g):return {'worldKey':'w','hold':False,'uiLanguage':g.language}
  game=Game();scheduler=Scheduler(self.mod/'comms',game,object())
  scheduler.tick();first=json.loads((self.root/'view.json').read_text('utf-8'))['tickets'][0]
  game.language='zh';scheduler.tick();second=json.loads((self.root/'view.json').read_text('utf-8'))['tickets'][0]
  self.assertEqual(first['revision'],second['revision'])
  self.assertEqual('Executed · effects unverified',first['resultLabel']);self.assertEqual('已处理 · 效果未验证',second['resultLabel'])
 def test_watch_boundaries_and_long_arguments_follow_language(self):
  row={'call_id':'one','name':'sp_watch_events','arguments':{},'readOnly':True,'status':'succeeded',
       'receipt':{'ok':True,'data':{'events':[{'state':'expired','reason':'field_unreadable'}]}}}
  view=evidence(self.t,[row],language='en')
  self.assertTrue(view['watchSummary'].startswith('Watch boundary: '));self.assertIn('field_unreadable',view['watchSummary'])
  self.assertIn('full host ledger retained',compact({'x':'a'*50},5,'en'))

 def test_game_style_ini_and_custom_override(self):
  en=self.mod/'language_en/ui.ini';zh=self.mod/'language_cn/ui.ini'
  en.parent.mkdir();zh.parent.mkdir()
  en.write_text('[AgentInspector]\nprobe=English\nfallback=Fallback\n',encoding='utf-8')
  zh.write_text('\ufeff; comment\n[AgentInspector]\nprobe=first\nprobe=中文自定义\nwith_equals=a=b\n',encoding='utf-8')
  t=translator('zh',self.mod)
  self.assertEqual('中文自定义',t('probe'));self.assertEqual('Fallback',t('fallback'))
  self.assertEqual('a=b',t('with_equals'));self.assertEqual('missing',t('missing'))
 def test_external_resource_edit_refreshes_without_ticket_revision(self):
  en=self.mod/'language_en/ui.ini';en.parent.mkdir()
  en.write_text('[AgentInspector]\nprobe=one\n',encoding='utf-8')
  self.assertEqual('one',translator('en',self.mod)('probe'))
  en.write_text('[AgentInspector]\nprobe=edited resource value\n',encoding='utf-8')
  self.assertEqual('edited resource value',translator('en',self.mod)('probe'))
 def test_unreadable_translation_falls_back_instead_of_stopping_scheduler(self):
  en=self.mod/'language_en/ui.ini';zh=self.mod/'language_cn/ui.ini'
  en.parent.mkdir();zh.parent.mkdir()
  en.write_text('[AgentInspector]\nprobe=Fallback\n',encoding='utf-8');zh.write_bytes(b'\xff\xff')
  self.assertEqual('Fallback',translator('zh',self.mod)('probe'))
if __name__=='__main__':unittest.main()
