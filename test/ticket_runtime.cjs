const fs=require('node:fs'), path=require('node:path'), http=require('node:http'), crypto=require('node:crypto');
const {spawn}=require('node:child_process');
const assert=require('node:assert/strict');
const root=fs.mkdtempSync(path.join(require('node:os').tmpdir(),'ticket-integration-'));
const out=path.join(root,'comms'), dir=path.join(out,'run','tickets');
fs.mkdirSync(path.join(dir,'commands'),{recursive:true});
let world='instance:1'; let cancels=0; let starts=0; let unavailable=false;
fs.writeFileSync(path.join(out,'run','session.pointer'),JSON.stringify({source:'game-world',world,sessionId:'parent'}));
const server=http.createServer((req,res)=>{
  res.statusCode=unavailable?503:200;
  res.end(JSON.stringify(unavailable?{ok:false}:{ok:true,data:{worldKey:world}}));
});
const statuses=new Map();
const plugin=http.createServer(async(req,res)=>{
  let text='';for await(const c of req)text+=c;
  const body=JSON.parse(text||'{}');let data={};
  if(req.url==='/start'){starts++;statuses.set(body.ticketId,'running');data={childId:'child-'+starts,status:'running'};}
  if(req.url==='/status') data={status:statuses.get(body.ticketId)};
  if(req.url==='/cancel'){cancels++;statuses.set(body.ticketId,'cancelled');data={status:'cancelled'};}
  res.end(JSON.stringify({ok:true,data}));
});
const wait=async check=>{for(let i=0;i<120;i++){const r=check();if(r)return r;await new Promise(r=>setTimeout(r,50));}throw Error('Timed out');};
const read=file=>{try{return JSON.parse(fs.readFileSync(file,'utf8'));}catch{return null;}};
(async()=>{
  await new Promise(r=>server.listen(0,'127.0.0.1',r));await new Promise(r=>plugin.listen(0,'127.0.0.1',r));
  const child=spawn(process.execPath,[path.resolve(__dirname,'../comms/ticket_runtime.js'),'--out',out,'--bridge-port',String(server.address().port),'--plugin-port',String(plugin.address().port)],{stdio:['ignore','pipe','pipe'],windowsHide:true});
  let errors='';child.stderr.on('data',c=>errors+=c);
  const command=async cmd=>{
    const requestId=crypto.randomUUID().replaceAll('-','');
    fs.writeFileSync(path.join(dir,'commands',requestId+'.json'),JSON.stringify({...cmd,requestId,worldKey:world}));
    return wait(()=>read(path.join(dir,'responses',requestId+'.json')));
  };
  try{
    await wait(()=>read(path.join(dir,'view.json')));
    const first=await command({op:'create',title:'test',task:'do nothing',completion:'report done'});
    assert.equal(first.ok,true);
    await wait(()=>starts===1);
    // A lost GET must not erase a live ticket or reject its same-world queue.
    unavailable=true;
    await wait(()=>read(path.join(dir,'view.json'))?.blockedReason==='game_state_unavailable');
    const delayedId=crypto.randomUUID().replaceAll('-','');
    fs.writeFileSync(path.join(dir,'commands',delayedId+'.json'),JSON.stringify({
      op:'create',requestId:delayedId,worldKey:world,title:'delayed',task:'do nothing',completion:'done'
    }));
    await new Promise(resolve=>setTimeout(resolve,650));
    assert.equal(fs.existsSync(path.join(dir,'responses',delayedId+'.json')),false);
    assert.equal(cancels,0);
    assert.equal(read(path.join(dir,'view.json')).tickets[0].status,'running');

    fs.writeFileSync(path.join(out,'run','session.pointer'),JSON.stringify({source:'game-world',world,sessionId:null}));
    unavailable=false;
    await wait(()=>read(path.join(dir,'view.json'))?.blockedReason==='session_pointer_not_ready');
    assert.equal(fs.existsSync(path.join(dir,'responses',delayedId+'.json')),false);
    fs.writeFileSync(path.join(out,'run','session.pointer'),JSON.stringify({source:'game-world',world,sessionId:'parent'}));
    const delayed=await wait(()=>read(path.join(dir,'responses',delayedId+'.json')));
    assert.equal(delayed.ok,true);
    await command({op:'destroy',id:delayed.id});
    assert.equal(starts,1);



    const records=fs.readFileSync(path.join(dir,'journal.jsonl'),'utf8').trim().split('\n').map(JSON.parse);
    const ticket=records.find(record=>record.ticket.id===first.id).ticket;
    await command({op:'report',id:first.id,reportToken:ticket.reportToken,status:'completed',detail:'done'});
    await wait(()=>cancels===1);
    await wait(()=>read(path.join(dir,'view.json'))?.tickets[0]?.status==='completed');
    await command({op:'destroy',id:first.id});
    await wait(()=>read(path.join(dir,'view.json')).tickets.length===0);
    const second=await command({op:'create',title:'cancel',task:'do nothing',completion:'report done'});
    await wait(()=>starts===2);
    await command({op:'destroy',id:second.id});
    await wait(()=>cancels===2);
    const third=await command({op:'create',title:'world',task:'do nothing',completion:'report done'});
    await wait(()=>starts===3);
    world='instance:2';
    await wait(()=>cancels===3);
    assert.equal(read(path.join(dir,'view.json')).tickets.length,0);
    assert.equal(starts,3);
    console.log('PASS runtime HTTP creation, worker report, destroy, cancellation and world isolation');
  }finally{
    child.kill('SIGTERM');
    await new Promise(r=>child.once('exit',r));
    await new Promise(r=>server.close(r));await new Promise(r=>plugin.close(r));
    const unexpected=errors.split('\n').filter(line=>line && !line.startsWith('Ticket runtime waiting:'));
    assert.deepEqual(unexpected,[],'Runtime must not report unexpected asynchronous errors');
  }
})().catch(e=>{console.error(e);process.exitCode=1;});
