#!/usr/bin/env node
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const { parseArgs } = require('node:util');
const { TicketStore } = require('./ticket_store.js');
const crypto = require('node:crypto');

function atomic(file, value) {
  const temp = file + '.' + process.pid + '.tmp';
  const fd = fs.openSync(temp, 'w');
  try { fs.writeFileSync(fd, JSON.stringify(value)); fs.fsyncSync(fd); }
  finally { fs.closeSync(fd); }
  for (let attempt = 0; ; attempt++) {
    try { fs.renameSync(temp, file); break; }
    catch (error) {
      if (!['EPERM','EACCES','EBUSY'].includes(error.code) || attempt >= 5) throw error;
      // Windows readers or scanners may briefly hold the previous view file.
      Atomics.wait(new Int32Array(new SharedArrayBuffer(4)),0,0,20);
    }
  }
}
function game(port) {
  return new Promise((resolve, reject) => {
    const request = http.get({host:'127.0.0.1', port, path:'/comms/state', timeout:2000}, response => {
      let text = '';
      response.setEncoding('utf8');
      response.on('data', chunk => {
        text += chunk;
        if (text.length > 65536) request.destroy(new Error('Oversized game state'));
      });
      response.on('error', reject);
      response.on('end', () => {
        try {
          const reply = JSON.parse(text);
          if (response.statusCode !== 200 || !reply.ok || !reply.data ||
              !Object.hasOwn(reply.data,'worldKey')) throw new Error('Invalid game state response: HTTP '+response.statusCode);
          resolve(reply.data);
        } catch (error) { reject(error); }
      });
    });
    request.on('error', reject);
    request.on('timeout', () => request.destroy(new Error('Game read timed out')));
  });
}
async function main() {
  const {values} = parseArgs({options:{
    out:{type:'string'}, port:{type:'string',default:'19387'},
    'bridge-port':{type:'string',default:'19080'}, 'plugin-port':{type:'string',default:'19389'}
  }});
  if (!values.out) throw new Error('Pass --out <MOD_ROOT>/comms');
  const root = path.join(path.resolve(values.out), 'run', 'tickets');
  const commands = path.join(root, 'commands');
  const responses = path.join(root, 'responses');
  fs.mkdirSync(commands, {recursive:true});
  fs.mkdirSync(responses, {recursive:true});
  const lockFile = path.join(root, 'runtime.lock');
  if (fs.existsSync(lockFile)) {
    const previous = JSON.parse(fs.readFileSync(lockFile, 'utf8'));
    try {
      process.kill(previous.pid, 0);
      throw new Error('Ticket runtime already running');
    } catch (error) { if (error.code !== 'ESRCH') throw error; }
    fs.unlinkSync(lockFile);
  }
  const lock = fs.openSync(lockFile, 'wx');
  fs.writeFileSync(lock, JSON.stringify({pid:process.pid}));
  const store = new TicketStore({file:path.join(root,'journal.jsonl')});
  const authFile = path.join(root,'auth.token');
  if (!fs.existsSync(authFile)) fs.writeFileSync(authFile,crypto.randomBytes(32).toString('hex'),{flag:'wx'});
  const token = fs.readFileSync(authFile,'utf8').trim();
  const call = (route, payload) => new Promise((resolve,reject) => {
    const body = JSON.stringify(payload);
    const request = http.request({
      host:'127.0.0.1',port:Number(values['plugin-port']),path:route,method:route === '/health' ? 'GET' : 'POST',timeout:4000,
      headers:{authorization:'Bearer '+token,'content-type':'application/json','content-length':Buffer.byteLength(body)}
    }, response => {
      let text='';
      response.setEncoding('utf8');
      response.on('data',chunk=>{text+=chunk;if(text.length>65536) request.destroy(new Error('Oversized plugin response'));});
      response.on('error',reject);
      response.on('end',()=>{
        try {
          const result=JSON.parse(text);
          if(response.statusCode!==200 || result.ok===false) throw new Error(result.error || 'DSH ticket plugin unavailable');
          resolve(result.data || result);
        } catch(error){reject(error);}
      });
    });
    request.on('error',reject);
    request.on('timeout',()=>request.destroy(new Error('DSH ticket plugin timeout')));
    request.end(body);
  });
  let confirmedWorld = null;
  let lastWorldError = null;
  let lastDiagnostic = '';
  let stopped = false;
  process.on('SIGTERM', () => { stopped = true; });
  process.on('SIGINT', () => { stopped = true; });
  try {
    while (!stopped) {
      let state;
      let worldReadable = false;
      try {
        state = await game(Number(values['bridge-port']));
        confirmedWorld = state.worldKey;
        lastWorldError = null;
        worldReadable = true;
      } catch (error) {
        lastWorldError = error.code || error.message;
      }
      const world = confirmedWorld;
      let owner = null;
      let pointer = null;
      let pointerError = null;
      try {
        pointer = JSON.parse(fs.readFileSync(path.join(path.resolve(values.out),'run','session.pointer'),'utf8'));
        if (pointer.source === 'game-world' && pointer.world === world) owner = pointer.sessionId;
      } catch (error) { pointerError = error.code || error.message; }
      const ready = worldReadable && !!world && !!owner;
      const blockedReason = !worldReadable ? 'game_state_unavailable' :
        !world ? 'no_mission' : !owner ? 'session_pointer_not_ready' : null;
      const diagnostic = JSON.stringify({
        blockedReason,worldKey:world,lastWorldError,
        pointerSource:pointer?.source ?? null,pointerWorld:pointer?.world ?? null,
        pointerHasSession:!!pointer?.sessionId,pointerError
      });
      if (diagnostic !== lastDiagnostic) {
        if (blockedReason) console.error('Ticket runtime waiting: '+diagnostic);
        lastDiagnostic = diagnostic;
      }
      // A failed read means identity is unknown, not that the world disappeared.
      if (worldReadable) store.expireWorld(world);
      store.overdue();
      const publish = () => atomic(path.join(root,'view.json'),{
        at:Date.now(),worldKey:world,available:ready,blockedReason,lastWorldError,
        ownerReady:!!owner,tickets:store.snapshot(world)
      });
      publish();
      for (const file of fs.readdirSync(commands).filter(name => /^[a-f0-9]{32}\.json$/.test(name))) {
        const requestFile = path.join(commands,file);
        const responseFile = path.join(responses,file);
        const rawCommand = fs.readFileSync(requestFile,'utf8');
        const fingerprint = crypto.createHash('sha256').update(rawCommand).digest('hex');
        if (fs.existsSync(responseFile)) {
          const previous = JSON.parse(fs.readFileSync(responseFile,'utf8'));
          if (previous.requestFingerprint !== fingerprint) console.error('Conflicting ticket request ID: '+file);
          fs.unlinkSync(requestFile);
          continue;
        }
        let command;
        let result;
        try {
          command = JSON.parse(rawCommand);
          if (!['create','destroy','report'].includes(command.op)) throw new Error('Invalid ticket operation');
          if (!worldReadable) continue;
          if (command.worldKey !== world) throw new Error('Ticket command belongs to a changed game world');
          // Session creation publishes a temporary null pointer. Keep the durable request
          // until that same world's parent session becomes available.
          if (!owner) continue;
          if (command.op === 'create') await call('/health',{});
          result = store.apply(command,{worldKey:world,ownerSessionId:owner});
        } catch (error) { result = {ok:false,error:error.message}; }
        atomic(responseFile,{...result,requestFingerprint:fingerprint});
        fs.unlinkSync(requestFile);
      }
      // Recover the crash window between native worker creation and local binding.
      // A hidden/expired ticket may still own a worker whose start receipt was lost.
      for (const ticket of store.all()) {
        if (ticket.workerSessionId || ticket.status === 'queued') continue;
        if (!['needs_review','failed','cancelled','timed_out'].includes(ticket.status)) continue;
        try {
          const native = await call('/status',{ticketId:ticket.id});
          if (native.childId) store.assignWorker(ticket.id,native.childId);
        } catch (error) {}
      }
      // Destruction is processed before starting queued work.
      for (const ticket of store.all()) {
        if (!ticket.cancelPending || !ticket.workerSessionId) continue;
        try {
          const response = await call('/cancel',{ticketId:ticket.id});
          if (['cancelled','completed','failed'].includes(response.status)) store.markCancelled(ticket.id);
        } catch (error) { console.error('Cancellation pending for ticket ' + ticket.id); }
      }
      const active = ready ? store.all().filter(ticket => ['starting','running'].includes(ticket.status) && !ticket.hidden) : [];
      for (const ticket of active) {
        if (!ticket.workerSessionId) continue;
        try {
          const status = await call('/status',{ticketId:ticket.id});
          if (['failed','completed','cancelled','unknown'].includes(status.status))
            store.fail(ticket.id,'Worker ended without an explicit ticket completion report: '+status.status);
        } catch(error) {}
      }
      let busy = store.all().filter(ticket => ['starting','running'].includes(ticket.status) && !ticket.hidden).length;
      for (const ticket of ready ? store.pending(world) : []) {
        if (busy >= 1 || ticket.status !== 'queued') continue;
        // Polling and plugin calls can span a load transition. Confirm identity again
        // before starting a native worker, without turning a transient read into failure.
        let beforeStart;
        try { beforeStart = await game(Number(values['bridge-port'])); }
        catch (error) { break; }
        if (beforeStart.worldKey !== ticket.worldKey) {
          store.expireWorld(beforeStart.worldKey);
          break;
        }
        store.markStart(ticket.id);
        busy++;
        const prompt = [
          '你是 Sea Power 工单执行子代理。只执行本工单，不向主 Agent 发消息。',
          '工单 ID: '+ticket.id,
          'worldKey: '+ticket.worldKey,
          '私有 reportToken: '+ticket.reportToken,
          '任务: '+ticket.task,
          '完成标准: '+ticket.completion,
          'Sea Power MCP adapter: '+path.join(path.dirname(path.resolve(values.out)),'mcp_server.py'),
          '如果没有直接的 sp_ 工具，通过 pwsh 启动上述 Python MCP adapter，标准输入传入 tools/call JSON-RPC 调用相同工具。',
          '先调用 sp_ticket_list 确认工单仍处于当前对局的活动状态，再按任务执行。只做玩家授权范围内动作。',
          '进度用 sp_ticket_report(status=running) 更新，完成或失败用 completed/failed 更新并结束。',
          '所有 report 调用带 id、私有 reportToken、UUID requestId 和 detail。销毁或取消后立即停止。',
          '不要创建新工单，不要调用 send_message 唤醒主 Agent。'
        ].join('\n');
        try {
          const started = await call('/start',{ticketId:ticket.id,parentSessionId:ticket.ownerSessionId,prompt});
          if (!started.childId) throw new Error('DSH worker identity missing');
          store.assignWorker(ticket.id,started.childId);
          try {
            const latest = await game(Number(values['bridge-port']));
            if (latest.worldKey !== ticket.worldKey) store.expireWorld(latest.worldKey);
          } catch (error) {
            // The worker is bound. Reconcile identity next poll instead of cancelling
            // an accepted start because one subsequent read was delayed.
          }
        } catch(error) {
          // A start may have been accepted. The plugin owns idempotence by ticket ID.
          // Do not automatically spawn another worker after an uncertain result.
          store.fail(ticket.id,('Worker start failed or uncertain: '+error.message).slice(0,3900));
          try { await call('/cancel',{ticketId:ticket.id}); } catch(cancelError) {}
        }
      }
      publish();
      await new Promise(resolve => setTimeout(resolve,500));
    }
  } finally {
    fs.closeSync(lock);
    fs.unlinkSync(lockFile);
  }
}
if (require.main === module) main().catch(error => { console.error(error.message); process.exitCode=1; });
module.exports = {game};
