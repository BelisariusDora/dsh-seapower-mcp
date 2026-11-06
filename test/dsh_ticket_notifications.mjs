import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import {createNotifier} from '../comms/dsh_ticket_notifications.mjs';
import {createRequire} from 'node:module';
const require=createRequire(import.meta.url);
const {project}=require('../comms/dsh_tailer.js');
let checks=0;
function fixture() {
  const directory=fs.mkdtempSync(path.join(os.tmpdir(),'sp-notice-test-'));
  const run=path.join(directory,'comms/run'),root=path.join(run,'tickets');
  fs.mkdirSync(path.join(root,'notifications/events'),{recursive:true});
  const origin={harness:'dsh',sessionId:'main',worldKey:'world:1'};
  const event={id:'notice-test',ticketId:'ticket-test',origin,createdAt:1,
    prompt:'<seapower-notice>\nblocked test\n</seapower-notice>'};
  const ticket={id:event.ticketId,worldKey:'world:1',origin,status:'blocked',hidden:false};
  fs.writeFileSync(path.join(run,'session.pointer'),JSON.stringify({sessionId:'main',world:'world:1'}));
  fs.writeFileSync(path.join(root,'notifications/events',event.id+'.json'),JSON.stringify(event));
  function journal(){fs.writeFileSync(path.join(root,'journal.jsonl'),JSON.stringify({ticket})+'\n');}
  journal();
  const listeners=new Set(),guards=new Set(),masks=new Set();
  const parent={id:'main',status:'idle',inbox:{hasPending:false},session:{header:{origin:'interactive'}},
    ctx:{tools:{restrict(filter){assert.deepEqual(filter,{allow:[]});masks.add(filter);return ()=>masks.delete(filter);},
      guard(handler){guards.add(handler);return ()=>guards.delete(handler);}}},
    messages:[],followup(message){this.messages.push(message);this.status='running';},
    cancel(){this.cancelled=true;},async whenIdle(){this.status='idle';}};
  const ctx={agents:{get:id=>id==='main'?parent:undefined},on(name,handler){
    assert.equal(name,'session/event');listeners.add(handler);return ()=>listeners.delete(handler);}};
  let world='world:1';
  const notifier=createNotifier(ctx,{runtimeDir:run},{state:async()=>({worldKey:world})});
  const emit=(type,data)=>{for(const handler of [...listeners])handler(parent.session,{type,data});};
  const result=()=>JSON.parse(fs.readFileSync(path.join(root,'notifications/delivery',event.id+'.json'),'utf8'));
  return {directory,run,root,event,ticket,parent,guards,masks,notifier,emit,result,journal,
    setWorld:value=>world=value,async close(){await notifier.close();fs.rmSync(directory,{recursive:true,force:true});}};
}
{
  const f=fixture();
  f.parent.status='running';await f.notifier.tick();assert.equal(f.parent.messages.length,0);
  f.parent.status='idle';f.parent.inbox.hasPending=true;await f.notifier.tick();assert.equal(f.parent.messages.length,0);
  f.parent.inbox.hasPending=false;
  fs.writeFileSync(path.join(f.run,'../outbox.jsonl'),'player\n');
  fs.writeFileSync(path.join(f.run,'../outbox.offset'),'0');
  await f.notifier.tick();assert.equal(f.parent.messages.length,0);
  fs.writeFileSync(path.join(f.run,'../outbox.offset'),'7');
  await f.notifier.tick();assert.equal(f.parent.messages.length,1);assert.equal(f.result().state,'sending');
  assert.equal(f.parent.messages[0].role,'user');assert.equal(f.parent.messages[0].source.rpcId,f.event.id);
  assert.ok(Object.isFrozen(f.parent.messages[0]));
  assert.equal(f.masks.size,1);assert.equal(f.guards.size,1);
  for(const guard of f.guards)assert.match(guard({agent:f.parent,name:'sp_speed'}),/reply-only/);
  f.emit('turn/start',{turn:3});
  f.emit('turn/end',{turn:2,reason:{kind:'completed'}});assert.equal(f.masks.size,1);
  f.emit('assistant/message',{turn:3,message:{content:[{type:'thinking',text:'secret'},{type:'text',text:'请指定目标。'}]}});
  f.emit('turn/end',{turn:3,reason:{kind:'completed'}});
  assert.equal(f.result().state,'delivered');assert.equal(f.masks.size,0);assert.equal(f.guards.size,0);
  f.parent.status='idle';await f.notifier.tick();assert.equal(f.parent.messages.length,1);
  assert.equal(project({type:'user/message',data:{content:[{type:'text',text:f.event.prompt}]}}),null);
  await f.close();checks+=10;
}
for(const mode of ['destroy','world','session']) {
  const f=fixture();
  if(mode==='destroy'){f.ticket.hidden=true;f.journal();}
  if(mode==='world')f.setWorld('world:2');
  if(mode==='session')fs.writeFileSync(path.join(f.run,'session.pointer'),JSON.stringify({sessionId:'other',world:'world:1'}));
  await f.notifier.tick();assert.equal(f.parent.messages.length,0);assert.equal(f.result().state,'skipped');
  await f.close();checks+=2;
}
for(const mode of ['destroy','world','stop']) {
  const f=fixture();await f.notifier.tick();f.emit('turn/start',{turn:1});
  if(mode==='destroy'){f.ticket.hidden=true;f.journal();}
  if(mode==='world')f.setWorld('world:2');
  if(mode!=='stop'){await f.notifier.tick();assert.equal(f.parent.cancelled,true);}
  assert.equal(f.guards.size,1);
  f.emit('assistant/message',{turn:1,message:{content:[{type:'text',text:'partial'}]}});
  f.emit('turn/end',{turn:1,reason:{kind:'aborted'}});
  assert.equal(f.result().state,'cancelled');assert.equal(f.guards.size,0);
  await f.close();checks+=3;
}
{
  const f=fixture();await f.notifier.tick();await f.notifier.close();
  assert.equal(f.result().state,'cancelled');assert.equal(f.guards.size,0);await f.close();checks+=2;
}
console.log('PASS DSH notification checks: '+checks+' (queue, priority, guard, turn boundary, dedup, scope, destroy, STOP, close, hidden context)');
