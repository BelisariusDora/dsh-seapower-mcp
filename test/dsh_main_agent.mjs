import assert from 'node:assert/strict';
import fs from 'node:fs';import path from 'node:path';import os from 'node:os';import http from 'node:http';
import {registerTicketTools} from '../comms/dsh_ticket_plugin.mjs';
const root=fs.mkdtempSync(path.join(os.tmpdir(),'sp-main-adapter-'));
const runtime=path.join(root,'comms','run');fs.mkdirSync(path.join(runtime,'tickets'),{recursive:true});
fs.copyFileSync(new URL('../api_tools.json',import.meta.url),path.join(root,'api_tools.json'));
fs.writeFileSync(path.join(root,'setting.ini'),'[Tickets]\nMainAgentOnly=true\n');
fs.writeFileSync(path.join(runtime,'session.pointer'),JSON.stringify({sessionId:'main',world:'world'}));
fs.writeFileSync(path.join(root,'mcp_server.py'),`import sys,json
r=json.loads(sys.stdin.readline());print(json.dumps({'id':1,'result':{'content':[{'type':'text','text':json.dumps(r['params'])}],'isError':False}}))
`);
const registry=new Map(),events=new Set(),guards=new Set(),restrictions=new Set(),requests=[];
let cancelled=0,mainHidden=false;
const parent={id:'main',session:{id:'main',header:{}},ctx:{tools:{
 restrict(value){restrictions.add(value);return ()=>restrictions.delete(value);},
 guard(fn){guards.add(fn);return ()=>guards.delete(fn);}
}},cancel(){cancelled++;}};
const ctx={tools:{view:()=>({visible:registry}),register:t=>registry.set(t.name,t)},agents:{get:id=>id==='main'?parent:null},
 logger:{warn(){}},on(name,fn){events.add(fn);return ()=>events.delete(fn);}};
const emit=(id,type,turn=1)=>{for(const fn of events)fn(id==='main'?parent.session:{id,header:{}},{type,data:{turn,reason:{kind:'completed'}}});};
const server=http.createServer(async(req,res)=>{
 let text='';for await(const chunk of req)text+=chunk;
 const body=text?JSON.parse(text):null;requests.push({path:req.url,body});
 const data=req.url==='/comms/state'?{worldKey:'world',hold:false}:{id:'ticket-test',executionMode:'main',status:'running'};
 res.end(JSON.stringify({ok:true,data}));
});
await new Promise(r=>server.listen(0,'127.0.0.1',r));let lifecycle;
try {
 lifecycle=registerTicketTools(ctx,{runtimeDir:runtime,bridgePort:server.address().port,
  pythonPath:process.env.SPMCP_PYTHON || 'python'});
 emit('other','turn/start');assert.equal(guards.size,0);
 emit('main','turn/start');assert.equal(guards.size,1);assert.equal(restrictions.size,1);
 const guard=[...guards][0];assert.match(guard({name:'shell'}),/ticket-bound/);assert.match(guard({name:'sp_speed'}),/ticket-bound/);
 assert.match(guard({name:'subagent_fork'}),/ticket-bound/);assert.equal(guard({name:'sp_main_api_call'}),undefined);
 const exec={agent:parent,signal:new AbortController().signal};
 const result=await registry.get('sp_ticket_create').execute({title:'readonly',task:'read'},exec);
 assert.equal(result.executionMode,'main');const sent=requests.find(r=>r.body?.op==='create');
 assert.deepEqual(sent.body.origin,{harness:'dsh',sessionId:'main',worldKey:'world',turnId:'1'});
 const helper=registry.get('sp_main_api_call');assert.ok(helper);assert.ok(!helper.parameters.properties.report_token);
 const reply=await helper.execute({ticket_id:'ticket-test',_origin:{sessionId:'wrong'}},exec);
 const bound=JSON.parse(reply.content[0].text);assert.equal(bound.arguments._origin.sessionId,'main');
 await assert.rejects(()=>helper.execute({ticket_id:'ticket-test'},{agent:{id:'other'},signal:exec.signal}),/originating/);
 fs.writeFileSync(path.join(runtime,'tickets','journal.jsonl'),JSON.stringify({ticket:{id:'ticket-test',hidden:true,status:'cancelled'}})+'\n');
 await new Promise(r=>setTimeout(r,700));assert.ok(cancelled>0);
 emit('main','turn/end');assert.equal(guards.size,0);assert.equal(restrictions.size,0);
 console.log('PASS main DSH adapter: unrelated session isolation, scoped guard, no delegation/direct writes, bound turn, helper schema, destroy cancellation, cleanup');
}finally{await lifecycle?.close();server.closeAllConnections();await new Promise(r=>server.close(r));fs.rmSync(root,{recursive:true,force:true});}
