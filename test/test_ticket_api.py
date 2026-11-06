"""Generic API tickets: capability, replay and evidence boundaries."""
import json
from pathlib import Path
import sys
import tempfile
import unittest
import uuid
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import api_catalog
import mcp_server
import ticket_api
from ticket_store import TicketStore
from ticket_predicates import conflicts

class ApiTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup)
        self.root=Path(self.temp.name)
        self.store=TicketStore(self.root/'journal.jsonl')
        result=self.store.apply({'op':'create','requestId':str(uuid.uuid4()),'worldKey':'world-1',
            'title':'api','task':'Read the current unit and do only the authorized operation',
            'completion':'Record API execution only','command':{'kind':'mcp_task','resources':['unit:12']}},'world-1')
        self.id=result['id'];self.store.change(self.store.get(self.id),status='running')
        self.ticket=self.store.get(self.id)
        self.calls=[]
        self.tools={name:dict(mcp_server.TOOLS_BY_NAME[name]) for name in ('sp_unit','sp_set_speed','sp_raw')}
        for name in self.tools:
            self.tools[name]['handler']=lambda args,name=name:self.handler(name,args)
        self.hold=False;self.world='world-1'
    def handler(self,name,args):
        self.calls.append((name,args))
        return {'ok':True,'cmd':17,'data':{'id':12}}
    def bridge(self,method,path,body=None):
        if path=='/comms/state':return {'ok':True,'data':{'worldKey':self.world,'hold':self.hold}}
        if path=='/tickets' and method=='GET':return {'ok':True,'data':{'tickets':self.store.snapshot(self.world)}}
        if path=='/tickets' and method=='POST':
            self.store=TicketStore(self.root/'journal.jsonl')
            return {'ok':True,'data':self.store.apply(body,self.world)}
        self.fail('Unexpected route')
    def args(self,**changes):
        return {'ticket_id':self.id,'report_token':self.ticket['reportToken'],'call_id':str(uuid.uuid4()),
                'name':'sp_unit','arguments':{'unit_id':12},**changes}
    def invoke(self,args):return ticket_api.invoke(args,self.root,self.tools,self.bridge)
    def finish(self):return ticket_api.finish(self.args(summary='Read current unit once'),self.root,self.bridge)
    def test_registry_catalog_covers_every_tool_without_copying_handlers(self):
        described=[]
        names=[name for name in mcp_server.TOOLS_BY_NAME if not name.startswith('sp_api_')]
        for offset in range(0,len(names),8):
            described+=api_catalog.describe({'names':names[offset:offset+8]})['tools']
        self.assertEqual(len(described),len(names))
        for row in described:
            self.assertIn('inputSchema',row)
            self.assertTrue(callable(mcp_server.TOOLS_BY_NAME[row['name']]['handler']))
        self.assertIn('loadout',mcp_server.TOOLS_BY_NAME['sp_launch_aircraft']['inputSchema']['required'])
    def test_duplicate_call_id_returns_receipt_without_second_execution(self):
        args=self.args();self.invoke(args);duplicate=self.invoke(args)
        self.assertTrue(duplicate['duplicate']);self.assertEqual(len(self.calls),1)
        with self.assertRaises(ValueError):self.invoke({**args,'arguments':{'unit_id':13}})
    def test_host_receipts_end_as_executed_not_verified_game_complete(self):
        self.invoke(self.args());self.finish()
        ticket=self.store.get(self.id)
        self.assertEqual(ticket['status'],'executed')
        self.assertEqual(ticket['verification'],'execution_only')
        self.assertFalse(ticket['resultVerified'])
        self.assertEqual(ticket['callCount'],1)
    def test_no_execution_and_forged_receipts_cannot_finish(self):
        with self.assertRaises(ValueError):self.finish()
        with self.assertRaises(ValueError):
            self.store.apply({'op':'report','requestId':str(uuid.uuid4()),'worldKey':self.world,'id':self.id,
                'reportToken':self.ticket['reportToken'],'status':'executed','detail':'fake',
                'receipt':{'call_ids':[uuid.uuid4().hex]}},self.world)
    def test_capability_control_and_raw_bypass_are_refused(self):
        for change in ({'report_token':'wrong'},{'name':'sp_ticket_create'},{'name':'sp_raw','arguments':{'path':'/x/../tickets','method':'POST'}}):
            with self.assertRaises(ValueError):self.invoke(self.args(**change))
        self.assertFalse(self.calls)
    def test_hold_allows_read_but_not_write_and_world_change_blocks(self):
        self.hold=True;self.invoke(self.args())
        with self.assertRaises(ValueError):self.invoke(self.args(name='sp_set_speed',arguments={'unit_id':12,'knots':5}))
        self.world='world-2'
        with self.assertRaises(ValueError):self.invoke(self.args())
        self.assertEqual(len(self.calls),1)
    def test_uncertain_handler_is_never_replayed_and_blocks_further_calls(self):
        def lost(args):self.calls.append('sent');raise TimeoutError('Reply lost')
        self.tools['sp_set_speed']['handler']=lost
        args=self.args(name='sp_set_speed',arguments={'unit_id':12,'knots':5})
        self.assertTrue(self.invoke(args)['uncertain'])
        self.assertTrue(self.invoke(args)['uncertain'])
        with self.assertRaises(ValueError):self.invoke(self.args())
        self.assertEqual(self.calls,['sent'])
        self.finish();self.assertEqual(self.store.get(self.id)['status'],'uncertain')
    def test_resource_conflicts_keep_world_exclusive(self):
        self.assertTrue(conflicts({'kind':'mcp_task','resources':['world']},{'unit_id':12}))
        self.assertFalse(conflicts({'kind':'mcp_task','resources':['plot']},{'unit_id':12}))
        self.assertTrue(conflicts({'kind':'mcp_task','resources':['unit:12']},{'unit_id':12}))

    def test_write_scope_and_call_budget_are_host_enforced(self):
        with self.assertRaises(ValueError):self.invoke(self.args(name='sp_set_speed',arguments={'unit_id':13,'knots':5}))
        self.assertFalse(self.calls)
        command={**self.ticket['command'],'max_calls':1}
        self.store.change(self.store.get(self.id),command=command)
        self.invoke(self.args())
        with self.assertRaises(ValueError):self.invoke(self.args())

    def test_recorded_calls_do_not_store_private_capability(self):
        self.invoke(self.args())
        file=next((self.root/'api_calls'/self.id).glob('*.json'))
        self.assertNotIn(self.ticket['reportToken'],file.read_text())

    def test_mixed_read_write_tools_are_classified_by_actual_arguments(self):
        self.assertFalse(api_catalog.is_read_only('sp_weapon_status',{}))
        self.assertFalse(api_catalog.is_read_only('sp_radars',{}))
        self.assertTrue(api_catalog.is_read_only('sp_map_view',{}))
        self.assertTrue(api_catalog.is_read_only('sp_plot_route',{'unit':12}))
        self.assertFalse(api_catalog.is_read_only('sp_plot_route',{'unit':12,'points':[]}))
        self.assertFalse(api_catalog.business('sp_agent_panel'))

    def test_existing_mcp_projection_is_reused_and_raw_receipt_is_retained(self):
        rendered={'content':[{'type':'text','text':json.dumps({'ok':True,'data':{'brief':True}})}],'isError':False}
        with patch('mcp_server.present_tool_result',return_value=rendered) as formatter:
            result=self.invoke(self.args())
        self.assertTrue(result['data']['brief'])
        self.assertEqual(len(self.calls),1)
        self.assertEqual(formatter.call_count,1)
        row=ticket_api.ledger(self.root,self.id)[0]
        self.assertEqual(row['receipt']['cmd'],17)
        self.assertEqual(row['receipt']['data']['id'],12)

if __name__=='__main__':unittest.main()
