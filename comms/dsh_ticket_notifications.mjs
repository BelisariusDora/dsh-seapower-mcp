import fs from 'node:fs';
import path from 'node:path';
import http from 'node:http';
import {randomUUID} from 'node:crypto';

const read = file => JSON.parse(fs.readFileSync(file, 'utf8'));
function atomic(file, value) {
  fs.mkdirSync(path.dirname(file), {recursive:true});
  const temporary = file + '.' + process.pid + '.tmp';
  const fd = fs.openSync(temporary, 'w', 0o600);
  try { fs.writeFileSync(fd, JSON.stringify(value)); fs.fsyncSync(fd); }
  finally { fs.closeSync(fd); }
  fs.renameSync(temporary, file);
}
function state(port) {
  return new Promise((resolve,reject) => {
    const req=http.get({host:'127.0.0.1',port,path:'/comms/state',timeout:2000},response=>{
      let text=''; response.setEncoding('utf8');
      response.on('data',chunk=>{text+=chunk;if(text.length>65536)req.destroy(Error('Oversized state'));});
      response.on('error',reject);
      response.on('end',()=>{try { const value=JSON.parse(text);
        if(response.statusCode!==200||value.ok!==true)throw Error('State unavailable');
        resolve(value.data); } catch(error){reject(error);} });
    });
    req.on('error',reject);req.on('timeout',()=>req.destroy(Error('State timeout')));
  });
}

// The existing DSH session/agent API is used locally. No worker wakes its parent.
export function createNotifier(ctx, config, dependencies={}) {
  const root=path.join(config.runtimeDir,'tickets');
  const directory=path.join(root,'notifications');
  const events=path.join(directory,'events'),delivery=path.join(directory,'delivery');
  fs.mkdirSync(events,{recursive:true});fs.mkdirSync(delivery,{recursive:true});
  const getState=dependencies.state ?? (()=>state(config.bridgePort??19080));
  // DSH UserMessage is a plain immutable value (id/role/content/source).
  // Match the installed SDK value contract without importing its private bundle
  // from the external plugin package, where SDK modules are not resolvable.
  const makeMessage=dependencies.message ?? (async (text,rpcId)=>Object.freeze({
    id:randomUUID(),role:'user',content:Object.freeze([Object.freeze({type:'text',text})]),
    source:Object.freeze({kind:'user',rpcId})
  }));
  let active=null,busy=false,timer=null,closed=false,lastError='';
  const status=event=>{
    const file=path.join(delivery,event.id+'.json');return fs.existsSync(file)?read(file):null;
  };
  function settle(event,value,reason='') {
    atomic(path.join(delivery,event.id+'.json'),{id:event.id,state:value,reason,at:Date.now(),harness:'dsh'});
  }
  const list=()=>fs.readdirSync(events).filter(n=>n.endsWith('.json')).map(n=>read(path.join(events,n)))
    .filter(e=>e.origin.harness==='dsh').sort((a,b)=>a.createdAt-b.createdAt||a.id.localeCompare(b.id));
  for(const event of list())if(status(event)?.state==='sending')
    settle(event,'unknown','Consumer restarted after delivery intent; never replay');

  function current(event,world) {
    const pointer=read(path.join(config.runtimeDir,'session.pointer'));
    if(world!==event.origin.worldKey||pointer.world!==world||pointer.sessionId!==event.origin.sessionId)return false;
    let latest;
    for(const line of fs.readFileSync(path.join(root,'journal.jsonl'),'utf8').split('\n').filter(Boolean)) {
      const t=JSON.parse(line).ticket;if(t?.id===event.ticketId)latest=t;
    }
    return !!(latest&&!latest.hidden&&latest.worldKey===world&&
      latest.origin?.harness===event.origin.harness&&latest.origin?.sessionId===event.origin.sessionId&&latest.origin?.worldKey===event.origin.worldKey&&
      ['blocked','needs_input','failed','uncertain','timed_out'].includes(latest.status));
  }
  function finish(value,reason='') {
    const owned=active;if(!owned)return;
    // Called synchronously at turn/end, before a queued ordinary turn is assembled.
    active=null;
    try {settle(owned.event,value,reason);} finally {
      for(const dispose of owned.disposers.reverse())dispose();
    }
  }
  function playerPending() {
    const outbox=path.join(config.runtimeDir,'..','outbox.jsonl');
    const cursor=path.join(config.runtimeDir,'..','outbox.offset');
    if(!fs.existsSync(outbox))return false;
    if(!fs.existsSync(cursor))return fs.statSync(outbox).size>0;
    return fs.statSync(outbox).size>Number(fs.readFileSync(cursor,'utf8').trim());
  }
  async function tick() {
    if(busy||closed)return;busy=true;
    try {
      const game=await getState();
      if(active) {
        let valid=false;
        try{valid=current(active.event,game.worldKey);}catch{}
        if(!valid||Date.now()-active.at>120000) {
          active.cancelled=true;
          active.parent.cancel({kind:'user'},{keepInbox:true});
          // Guards stay installed until the native turn has actually ended.
          if(active?.parent.status==='idle')finish('cancelled','World/session/task changed or notice timed out');
        }
        return;
      }
      if(!game.worldKey||playerPending())return;
      for(const event of list()) {
        if(status(event))continue;
        if(!fs.existsSync(path.join(config.runtimeDir,'session.pointer')))return;
        if(!current(event,game.worldKey)) {settle(event,'skipped','Origin session/world changed or task destroyed');continue;}
        const parent=ctx.agents.get(event.origin.sessionId);
        if(!parent)return; // Attached main may not be ready yet; never create a replacement.
        if(parent.status!=='idle'||parent.inbox?.hasPending)return;
        if(parent.session?.header?.origin==='subagent') {settle(event,'skipped','Origin is not a main session');continue;}
        const persona=path.join(config.runtimeDir,'..','..','persona.md');
        const text=event.prompt+'\n'+(fs.existsSync(persona)?fs.readFileSync(persona,'utf8'):'');
        const message=await makeMessage(text,event.id);
        // Import/admission yields: recheck before installing per-agent restrictions.
        if(closed||playerPending()||parent.status!=='idle'||parent.inbox?.hasPending||!current(event,(await getState()).worldKey))return;
        if(!parent.ctx?.tools?.restrict||!parent.ctx?.tools?.guard)throw Error('Scoped tools restriction unavailable');
        const disposers=[];
        try {
          disposers.push(parent.ctx.tools.restrict({allow:[]}));
          disposers.push(parent.ctx.tools.guard(()=> 'Sea Power status notification is reply-only; all tools denied'));
          const owned={event,parent,disposers,turn:null,text:'',at:Date.now(),cancelled:false};
          disposers.push(ctx.on('session/event',(session,entry)=>{
            if(active!==owned||session!==parent.session)return;
            if(entry.type==='turn/start'&&owned.turn===null)owned.turn=entry.data.turn;
            if(owned.turn===null||entry.data?.turn!==owned.turn)return;
            if(entry.type==='assistant/message')owned.text+=(entry.data.message?.content??[])
              .filter(p=>p.type==='text').map(p=>p.text).join('\n');
            if(entry.type==='turn/end')finish(owned.cancelled?'cancelled':
              (entry.data.reason?.kind==='aborted'?'cancelled':entry.data.reason?.kind==='completed'&&owned.text.trim()?'delivered':'failed'),
              owned.cancelled?'Origin changed or notice cancelled':owned.text.trim()?'':'No visible reply');
          }));
          settle(event,'sending','Delivery intent persisted; automatic replay disabled');
          active=owned;
          try{parent.followup(message);}catch(error){finish('unknown','Prompt admission outcome unknown');return;}
        } catch(error) {
          if(!active)for(const dispose of disposers.reverse())dispose();
          throw error;
        }
        return;
      }
      lastError='';
    } catch(error) {lastError=String(error.message??error);}
    finally {busy=false;}
  }
  return {
    tick,
    snapshot:()=>({active:active?.event.id??null,lastError}),
    start(){timer=setInterval(()=>void tick(),500);timer.unref();},
    async close(){
      closed=true;clearInterval(timer);
      if(active){active.cancelled=true;active.parent.cancel({kind:'user'},{keepInbox:true});
        await active.parent.whenIdle();if(active)finish('cancelled','Notifier stopped');}
    }
  };
}
