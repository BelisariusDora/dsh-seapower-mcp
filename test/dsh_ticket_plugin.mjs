import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import {createWorkerService} from '../comms/dsh_ticket_plugin.mjs';
const directory=fs.mkdtempSync(path.join(os.tmpdir(),'ticket-plugin-test-'));
fs.mkdirSync(path.join(directory,'tickets'));
const token='x'.repeat(48);
fs.writeFileSync(path.join(directory,'tickets','auth.token'),token);
const settlements=[];
let starts=0;
let disposed=0;
const parent={id:'parent',session:{header:{origin:'interactive'}},followup(){assert.fail('parent woke')},steer(){assert.fail('parent woke')}};
const ctx={
  agents:{get:id=>id==='parent'?parent:undefined},
  logger:{warn:()=>{},info:()=>{}},
  tools:{view:()=>({restrictableNames:new Set(['send_message','subagent_fork','mcp__seapower__sp_ticket_create','sp_ticket_destroy','sp_ticket_report','sp_ticket_list']),visible:new Map()})},
  subagents:{
    list:()=>['spawn'],
    getProvider:()=>({name:'spawn',inheritsParentContext:false,capabilities:{toolFilter:true}}),
    resolveMaxDepth:()=>1,
    start:async(provider,request)=>{
      assert.equal(request.parent,parent);
      assert.deepEqual(request.toolFilter,{deny:['send_message','subagent_fork','mcp__seapower__sp_ticket_create','sp_ticket_destroy']});
      starts++;
      let resolve;
      const result=new Promise(done=>resolve=done);
      const id='child-'+starts;
      settlements.push(resolve);
      request.signal.addEventListener('abort',()=>resolve({stopReason:'aborted',output:[]}));
      return {id,localAgent:{},result,async dispose(){disposed++;resolve({stopReason:'aborted',output:[]})}};
    }
  }
};
const service=createWorkerService(ctx,{runtimeDir:directory,port:19399});
await service.listen();
let response=await fetch('http://127.0.0.1:19399/health');
assert.equal(response.status,401);
response=await fetch('http://127.0.0.1:19399/health',{headers:{Authorization:'Bearer '+token}});
assert.equal(response.status,200);
const body={ticketId:'ticket-1',parentSessionId:'parent',prompt:'do work'};
const [a,b]=await Promise.all([service.start(body),service.start(body)]);
assert.equal(a.childId,b.childId);
assert.equal(starts,1);
await assert.rejects(service.start({...body,prompt:'different'}),/different input/);
settlements[0]({stopReason:'completed',output:[{type:'text',text:'done'}]});
await new Promise(resolve=>setTimeout(resolve,20));
assert.equal(service.status('ticket-1').status,'completed');
await service.start({...body,ticketId:'ticket-2'});
await service.cancel('ticket-2');
assert.equal(service.status('ticket-2').status,'cancelled');
await service.close();
const reopened=createWorkerService(ctx,{runtimeDir:directory,port:19399});
assert.equal(reopened.status('ticket-1').output,'done');
await reopened.close();
assert.ok(disposed>=2);
console.log('Plugin mock checks passed: idempotence, completion, cancellation, restart, auth, no parent wake.');
