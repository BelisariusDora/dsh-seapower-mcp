import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import crypto from "node:crypto";
import {spawn} from "node:child_process";
import {fileURLToPath} from "node:url";

import {createNotifier} from "./dsh_ticket_notifications.mjs";

export const name = "seapower-ticket-worker";
export const revision = "20261106-native-one-shot-v9-main";
export const inject = ["subagents", "agents", "tools"];

function errorText(error) {
  return error instanceof Error ? error.message : String(error);
}

function fail(status, message) {
  return Object.assign(new Error(message), { status });
}

export function createWorkerService(ctx, config) {
  if (!config || typeof config.runtimeDir !== "string" || !path.isAbsolute(config.runtimeDir)) {
    throw new Error("runtimeDir must be the absolute game comms/run directory");
  }
  const directory = path.join(config.runtimeDir, "tickets");
  const tokenPath = path.join(directory, "auth.token");
  const statePath = path.join(directory, "worker-state.json");
  const port = config.port ?? 19389;
  if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error("Invalid worker port");
  fs.mkdirSync(directory, { recursive: true });
  const records = new Map();
  const runs = new Map();
  const pending = new Map();
  let stopped = false;
  let server;
  const notifier = createNotifier(ctx, config);

  function save() {
    const temporary = statePath + ".tmp-" + process.pid;
    fs.writeFileSync(temporary, JSON.stringify([...records.values()]), { mode: 0o600 });
    fs.renameSync(temporary, statePath);
  }

  try {
    const saved = JSON.parse(fs.readFileSync(statePath, "utf8"));
    if (!Array.isArray(saved)) throw new Error("Invalid worker state");
    for (const record of saved) {
      if (!record || typeof record.ticketId !== "string") throw new Error("Invalid worker record");
      if (["starting", "running", "cancelling"].includes(record.status)) {
        record.status = "unknown";
        record.error = "Worker host restarted. Reconcile the child before further work.";
      }
      records.set(record.ticketId, record);
    }
    save();
  } catch (error) {
    if (error.code !== "ENOENT") throw error;
  }

  function snapshot(record) {
    const { fingerprint, ...visible } = record;
    return structuredClone(visible);
  }

  function authenticate(request) {
    if (request.headers.origin) throw fail(403, "Browser-origin requests are not supported");
    let token;
    try { token = fs.readFileSync(tokenPath, "utf8").trim(); }
    catch { throw fail(503, "Ticket runtime authentication file unavailable"); }
    if (token.length < 32) throw fail(503, "Invalid runtime authentication token");
    const actual = Buffer.from(request.headers.authorization ?? "");
    const expected = Buffer.from("Bearer " + token);
    if (actual.length !== expected.length || !crypto.timingSafeEqual(actual, expected)) {
      throw fail(401, "Unauthorized");
    }
  }

  function providerFor() {
    const providers = ctx.subagents.list()
      .map(value => ctx.subagents.getProvider(value))
      .filter(value => value && value.inheritsParentContext === false && value.capabilities?.toolFilter === true);
    const selected = config.provider
      ? providers.find(value => value.name === config.provider)
      : providers.find(value => value.name === "spawn") ?? providers[0];
    if (!selected) throw fail(503, "No context-independent subagent provider with tool filtering is available");
    return selected.name;
  }

  async function start(body) {
    if (stopped) throw fail(503, "Worker is stopping");
    const { ticketId, parentSessionId, prompt } = body;
    if (typeof ticketId !== "string" || !/^[A-Za-z0-9_-]{1,100}$/.test(ticketId) ||
        typeof parentSessionId !== "string" || !parentSessionId ||
        typeof prompt !== "string" || !prompt.trim()) throw fail(400, "Invalid start request");
    const fingerprint = crypto.createHash("sha256").update(JSON.stringify({ parentSessionId, prompt })).digest("hex");
    const previous = records.get(ticketId);
    if (previous) {
      if (previous.fingerprint !== fingerprint) throw fail(409, "Ticket already exists with different input");
      if (pending.has(ticketId)) await pending.get(ticketId);
      return snapshot(records.get(ticketId));
    }
    const parent = ctx.agents.get(parentSessionId);
    if (!parent || parent.session?.header?.origin === "subagent") throw fail(409, "Main parent agent unavailable");
    const provider = providerFor();
    // Tool names vary by profile (for example subagent_fork). Restrict only
    // names the real registry says can be masked in the child.
    const view = ctx.tools.view();
    const apiWorker = body.mode === 'api-task';
    const helpers = new Set(['sp_api_find','sp_api_schema','sp_api_call','sp_api_finish','read_image']);
    const forbidden = toolName => (apiWorker && !helpers.has(toolName)) || toolName === "send_message" ||
      /^subagent(?:_|$)/.test(toolName) ||
      /(?:^|__)(?:sp_)?ticket_(?:create|destroy|bind)$/.test(toolName);
    const deny = [...view.restrictableNames].filter(forbidden);
    const scopedForbidden = [...view.visible.keys()].filter(toolName =>
      forbidden(toolName) && !view.restrictableNames.has(toolName));
    if (scopedForbidden.length > 0) {
      throw fail(503, "Unmaskable scoped delegation tools: " + scopedForbidden.join(", "));
    }
    const record = { ticketId, parentSessionId, provider, fingerprint, revision, deniedTools: deny, status: "starting", createdAt: Date.now(), childId: null };
    records.set(ticketId, record);
    try { save(); }
    catch (error) { records.delete(ticketId); throw error; }
    const controller = new AbortController();
    const entry = { controller, run: null };
    runs.set(ticketId, entry);
    const starting = (async () => {
      try {
        const run = await ctx.subagents.start(provider, {
          parent,
          label: "Sea Power ticket " + ticketId,
          prompt: [{ type: "text", text: prompt }],
          signal: controller.signal,
          ...(deny.length > 0 ? { toolFilter: { deny } } : {}),
          maxDepth: ctx.subagents.resolveMaxDepth(undefined)
        });
        entry.run = run;
        Promise.resolve(run.result).catch(() => {});
        if (!run.localAgent || typeof run.id !== "string") {
          await run.dispose();
          throw new Error("Ticket workers require an in-process native child");
        }
        record.childId = run.id;
        record.status = controller.signal.aborted ? "cancelling" : "running";
        save();
        // One-shot runs report only through this holder. They never enqueue a
        // settlement notice in the parent's Inbox.
        void Promise.resolve(run.result).then(result => {
          record.stopReason = result.stopReason;
          record.status = controller.signal.aborted ? "cancelled" :
            result.stopReason === "completed" ? "completed" : "failed";
          record.output = (result.output ?? []).filter(block => block?.type === "text")
            .map(block => block.text).join("\n").slice(0, 8192);
        }, error => {
          record.status = controller.signal.aborted ? "cancelled" : "failed";
          record.error = errorText(error);
        }).finally(async () => {
          record.finishedAt = Date.now();
          try { save(); } catch (error) { ctx.logger.warn("Cannot persist worker result: " + errorText(error)); }
          try { await run.dispose(); } catch (error) { ctx.logger.warn("Cannot dispose worker: " + errorText(error)); }
          runs.delete(ticketId);
        });
        if (controller.signal.aborted) await run.dispose();
      } catch (error) {
        controller.abort();
        if (entry.run) {
          try { await entry.run.dispose(); } catch {}
        }
        record.status = controller.signal.aborted && record.status === "cancelling" ? "cancelled" : "failed";
        record.error = errorText(error);
        record.finishedAt = Date.now();
        save();
        runs.delete(ticketId);
      }
    })();
    pending.set(ticketId, starting);
    try { await starting; }
    finally { pending.delete(ticketId); }
    return snapshot(record);
  }

  async function cancel(ticketId) {
    const record = records.get(ticketId);
    if (!record) throw fail(404, "Unknown ticket");
    const entry = runs.get(ticketId);
    if (!entry) return snapshot(record);
    record.status = "cancelling";
    entry.controller.abort(new Error("Ticket destroyed"));
    save();
    if (pending.has(ticketId)) await pending.get(ticketId);
    if (entry.run) await entry.run.dispose();
    if (record.status === "cancelling") record.status = "cancelled";
    save();
    return snapshot(record);
  }

  function status(ticketId) {
    const record = records.get(ticketId);
    if (!record) throw fail(404, "Unknown ticket");
    return snapshot(record);
  }

  async function readBody(request) {
    const chunks = [];
    let size = 0;
    for await (const chunk of request) {
      size += chunk.length;
      if (size > 32768) throw fail(413, "Request exceeds 32 KiB");
      chunks.push(chunk);
    }
    try { return JSON.parse(Buffer.concat(chunks).toString("utf8")); }
    catch { throw fail(400, "Invalid JSON body"); }
  }

  async function handle(request, response) {
    try {
      authenticate(request);
      if (request.method === "GET" && request.url === "/health") {
        response.writeHead(200, { "content-type": "application/json" });
        response.end(JSON.stringify({ ok: true, name, revision, pid: process.pid, nativeMode: "one-shot", ticketProtocol: 2, apiTicketProtocol: 1, taskResultProtocol: 1, notificationProtocol: 1, notifications: notifier.snapshot(), port }));
        return;
      }
      if (request.method !== "POST") throw fail(405, "Use POST");
      const body = await readBody(request);
      let value;
      if (request.url === "/start") value = await start(body);
      else if (request.url === "/cancel") value = await cancel(body.ticketId);
      else if (request.url === "/status") value = status(body.ticketId);
      else throw fail(404, "Unknown endpoint");
      response.writeHead(200, { "content-type": "application/json" });
      response.end(JSON.stringify({ ok: true, data: value }));
    } catch (error) {
      response.writeHead(error.status ?? 500, { "content-type": "application/json" });
      response.end(JSON.stringify({ ok: false, error: errorText(error) }));
    }
  }

  return {
    start, cancel, status,
    async listen() {
      if (server) throw new Error("Worker already listening");
      server = http.createServer((request, response) => { void handle(request, response); });
      server.requestTimeout = 15000;
      server.headersTimeout = 5000;
      await new Promise((resolve, reject) => {
        server.once("error", reject);
        server.listen(port, "127.0.0.1", resolve);
      });
      notifier.start();
      return server.address();
    },
    async close() {
      stopped = true;
      await notifier.close();
      const closing = [...runs.keys()].map(ticketId => cancel(ticketId).catch(error => {
        ctx.logger.warn("Cannot stop ticket worker: " + errorText(error));
      }));
      if (server) {
        server.closeIdleConnections();
        await new Promise(resolve => server.close(resolve));
      }
      await Promise.allSettled(closing);
    }
  };
}


// Register the short task workflow as native DSH tools. The main agent does not
// need to inspect the Python adapter or construct a terminal transport.
export function registerTicketTools(ctx, config) {
  const port = config.bridgePort ?? 19080;
  function request(method, route, body, signal, raw = false) {
    return new Promise((resolve,reject) => {
      const text = body ? JSON.stringify(body) : '';
      const req = http.request({
        host:'127.0.0.1',port,path:route,method,timeout:10000,signal,
        headers: text ? {'content-type':'application/json','content-length':Buffer.byteLength(text)} : {}
      }, response => {
        let output='';
        response.setEncoding('utf8');
        response.on('data',chunk=>{
          output+=chunk;
          if(output.length>131072)req.destroy(new Error('Oversized Bridge reply'));
        });
        response.on('error',reject);
        response.on('end',()=>{
          try {
            const reply=JSON.parse(output);
            if(response.statusCode!==200 || reply.ok!==true)
              throw new Error(reply.error?.message || 'Sea Power Bridge request failed');
            resolve(raw ? reply : reply.data);
          } catch(error){reject(error);}
        });
      });
      req.on('error',reject);
      req.on('timeout',()=>req.destroy(new Error('Bridge timeout; retry with the same requestId')));
      req.end(text);
    });
  }
  const id = {type:'string',description:'UUID idempotency key. Reuse the same UUID if the result is uncertain.'};
  const tools = [
    {
      name:'sp_ping',
      description:'Read the Sea Power Bridge connection and build identity.',
      parameters:{type:'object',properties:{},additionalProperties:false},
      execute:(args,exec)=>request('GET','/ping',null,exec.signal)
    },
    {
      name:'sp_comms_state',
      description:'Read the current Sea Power worldKey and player HOLD state before an authorized write.',
      parameters:{type:'object',properties:{},additionalProperties:false},
      execute:(args,exec)=>request('GET','/comms/state',null,exec.signal)
    },
    {
      name:'sp_speed',
      description:'Submit one authorized speed command. Returns the complete raw Bridge receipt; this is injection, not observed completion. Never repeat an uncertain write.',
      parameters:{type:'object',properties:{
        unit_id:{type:'integer',minimum:1},knots:{type:'number',minimum:-10,maximum:80}
      },required:['unit_id','knots'],additionalProperties:false},
      execute:(args,exec)=>{
        if(!Number.isInteger(args.unit_id)||args.unit_id<=0||!Number.isFinite(args.knots))
          throw new Error('Invalid speed command');
        return request('POST','/units/'+args.unit_id+'/speed',{knots:args.knots},exec.signal,true);
      }
    },
    {
      name:'sp_ticket_create',
      description:'Main Agent: persist a Sea Power speed ticket with matching command and predicate. After success confirm and END YOUR TURN. The Python scheduler dispatches a native worker; only game observations complete the ticket. Do not write game commands, poll, or launch subagent_fork yourself.',
      parameters:{type:'object',properties:{
        requestId:id,title:{type:'string',maxLength:120},
        task:{type:'string',maxLength:12000},completion:{type:'string',maxLength:4000},
        command:{type:'object',properties:{
          kind:{type:'string',enum:['set_speed']},unit_id:{type:'integer',minimum:1},
          knots:{type:'number',minimum:0,maximum:80}
        },required:['kind','unit_id','knots'],additionalProperties:false},
        predicate:{type:'object',properties:{
          kind:{type:'string',enum:['speed_reached']},unit_id:{type:'integer',minimum:1},
          target_knots:{type:'number'},tolerance_knots:{type:'number',exclusiveMinimum:0,maximum:1}
        },required:['kind','unit_id','target_knots'],additionalProperties:false},
        timeoutSeconds:{type:'integer',minimum:10,maximum:3600}
      },required:['requestId','title','task','completion'],additionalProperties:false},
      execute:async(args,exec)=>{
        if(!exec.agent?.id)throw new Error('Main session identity unavailable');
        const game=await request('GET','/comms/state',null,exec.signal);
        const receipt=await request('POST','/tickets',{...args,op:'create',worldKey:game.worldKey,
          origin:{harness:'dsh',sessionId:exec.agent.id,worldKey:game.worldKey,turnId:String(turns.get(exec.agent.id)??'')}},exec.signal);
        if(receipt.executionMode==='main') {
          const entry=own(ctx.agents.get(exec.agent.id),turns.get(exec.agent.id),game.worldKey);
          entry.ids.add(receipt.id);
        }
        return receipt;
      }
    },
    {
      name:'sp_ticket_report',
      description:'Worker only: report injected with the full raw Bridge receipt and immediately exit. Report uncertain if the write result is unknown and never retry the write. Do not monitor or wake the main Agent. Only the scheduler observes game completion.',
      parameters:{type:'object',properties:{
        requestId:id,id:{type:'string'},reportToken:{type:'string'},receipt:{type:'object'},
        status:{type:'string',enum:['running','injected','completed','failed','uncertain']},
        detail:{type:'string',maxLength:4000}
      },required:['requestId','id','reportToken','status','detail'],additionalProperties:false},
      execute:(args,exec)=>request('POST','/tickets',{...args,op:'report'},exec.signal)
    },
    {
      name:'sp_ticket_list',
      description:'Read tickets of the current Sea Power mission. Workers can check whether their ticket is active.',
      parameters:{type:'object',properties:{},additionalProperties:false},
      execute:(args,exec)=>request('GET','/tickets',null,exec.signal)
    },
    {
      name:'sp_ticket_destroy',
      description:'Hide a ticket and stop its native worker. Requires the player to request cancellation.',
      parameters:{type:'object',properties:{requestId:id,id:{type:'string'}},
        required:['requestId','id'],additionalProperties:false},
      execute:(args,exec)=>request('POST','/tickets',{...args,op:'destroy'},exec.signal)
    }
  ];
  const modRoot = config.runtimeDir ? path.resolve(config.runtimeDir,'../..') : path.dirname(fileURLToPath(new URL('../api_tools.json',import.meta.url)));
  const modularManifest = path.join(modRoot,'modules','api','api_tools.json');
  const manifest = JSON.parse(fs.readFileSync(fs.existsSync(modularManifest) ? modularManifest : path.join(modRoot,'api_tools.json'),'utf8'));
  function pythonTool(name,args,signal) {
    return new Promise((resolve,reject)=>{
      const child=spawn(config.pythonPath || process.env.SPMCP_PYTHON || 'python', [path.join(modRoot,'mcp_server.py')],
        {windowsHide:true,stdio:['pipe','pipe','pipe'],signal,env:{...process.env,SPMCP_PORT:String(port)}});
      let output='';
      const timer=setTimeout(()=>child.kill(),60000);
      child.stdout.setEncoding('utf8');
      child.stdout.on('data',chunk=>{output+=chunk;if(output.length>4*1024*1024)child.kill();});
      child.stderr.resume();
      child.on('error',error=>{clearTimeout(timer);reject(error);});
      child.on('close',code=>{
        clearTimeout(timer);
        try {
          const message=output.trim().split(/\r?\n/).filter(Boolean).map(JSON.parse).find(row=>row.id===1);
          if(code!==0 || !message?.result || message.error)throw new Error('Python MCP invocation did not return');
          if(message.result.isError)throw new Error(message.result.content?.find(row=>row.type==='text')?.text || 'API refused');
          resolve(message.result);
        } catch(error){reject(error);}
      });
      child.stdin.end(JSON.stringify({jsonrpc:'2.0',id:1,method:'tools/call',params:{name,arguments:args}})+'\n');
    });
  }
  const turns=new Map(),owners=new Map();
  const allowed=new Set(['sp_ticket_create','sp_ticket_list','sp_ticket_destroy','sp_api_find','sp_api_schema',
    'sp_main_api_call','sp_main_api_finish','sp_ping','sp_comms_state','read_image']);
  function setting() {
    try {
      let section='';for(const line of fs.readFileSync(path.join(modRoot,'setting.ini'),'utf8').split(/\r?\n/)) {
        const header=line.trim().match(/^\[([^\]]+)\]/);if(header){section=header[1].toLowerCase();continue;}
        if(section==='tickets'&&/^\s*MainAgentOnly\s*=\s*true\s*(?:[;#].*)?$/i.test(line))return true;
      }
    }catch{}return false;
  }
  function own(parent,turn,world) {
    if(turn===undefined)throw Error('Main turn identity unavailable; no execution admitted');
    let entry=owners.get(parent.id);
    if(entry&&entry.turn===String(turn))return entry;
    if(entry)throw Error('Previous main turn has not settled');
    const disposers=[];
    entry={parent,turn:String(turn),world,ids:new Set(),disposers};
    if(!parent.ctx?.tools?.restrict||!parent.ctx?.tools?.guard)throw Error('Scoped main tool guard unavailable');
    disposers.push(parent.ctx.tools.restrict({allow:[...allowed]}));
    disposers.push(parent.ctx.tools.guard(call=>allowed.has(call.name)?undefined:'Main-only game tasks require ticket-bound API helpers'));
    owners.set(parent.id,entry);return entry;
  }
  async function settle(entry,cancelled) {
    // Remove restrictions synchronously at native turn/end. Bound helpers still reject other turns.
    owners.delete(entry.parent.id);
    for(const dispose of entry.disposers.reverse())dispose();
    if(entry.ids.size)await pythonTool('_sp_main_turn_end',{cancelled,_origin:{harness:'dsh',
      sessionId:entry.parent.id,worldKey:entry.world,turnId:entry.turn}});
  }
  let busy=false;
  async function monitorMain() {
    if(busy||!owners.size)return;busy=true;
    try {
      const game=await request('GET','/comms/state').catch(()=>({worldKey:null}));
      const pointer=JSON.parse(fs.readFileSync(path.join(config.runtimeDir,'session.pointer'),'utf8'));
      const latest=new Map();
      const journal=path.join(config.runtimeDir,'tickets','journal.jsonl');
      if(fs.existsSync(journal))for(const line of fs.readFileSync(journal,'utf8').split('\n').filter(Boolean)) {
        const t=JSON.parse(line).ticket;latest.set(t.id,t);
      }
      for(const entry of owners.values()) {
        const stale=game.worldKey!==entry.world||pointer.sessionId!==entry.parent.id||pointer.world!==entry.world;
        const cancelled=[...entry.ids].some(id=>{const t=latest.get(id);return t&&(t.hidden||(!t.mainReported&&['cancelled','timed_out','uncertain'].includes(t.status)));});
        if(stale||cancelled)entry.parent.cancel({kind:'user'},{keepInbox:true});
      }
    } catch(error) {ctx.logger?.warn('Main lifecycle check unavailable: '+errorText(error));}
    finally{busy=false;}
  }
  let eventDispose;
  if(ctx.on) eventDispose=ctx.on('session/event',(session,event)=>{
    if(event.type==='turn/start') {
      turns.set(session.id,event.data.turn);
      if(setting()&&config.runtimeDir)try {
        const pointer=JSON.parse(fs.readFileSync(path.join(config.runtimeDir,'session.pointer'),'utf8'));
        const parent=ctx.agents.get(session.id);
        if(pointer.sessionId===session.id&&parent&&session.header?.origin!=='subagent')own(parent,event.data.turn,pointer.world);
      } catch(error){ctx.logger?.warn('Main turn restriction: '+errorText(error));}
    }
    if(event.type==='turn/end') {
      const entry=owners.get(session.id);
      if(entry&&entry.turn===String(event.data.turn))void settle(entry,event.data.reason?.kind!=='completed')
        .catch(error=>ctx.logger?.warn('Main cleanup requires review: '+errorText(error)));
      if(String(turns.get(session.id))===String(event.data.turn))turns.delete(session.id);
    }
  });
  const timer=setInterval(()=>{void monitorMain();},500);timer.unref();
  const lifecycle={async close(){clearInterval(timer);eventDispose?.();
    for(const entry of [...owners.values()]) {
      entry.parent.cancel({kind:'user'},{keepInbox:true});await settle(entry,true);
    }
  }};
  for(const base of manifest.filter(t=>['sp_api_call','sp_api_finish'].includes(t.name))) {
    const spec=structuredClone(base);spec.name=spec.name.replace('sp_api_','sp_main_api_');
    delete spec.inputSchema.properties.report_token;
    spec.inputSchema.required=spec.inputSchema.required.filter(k=>k!=='report_token');
    tools.push({name:spec.name,parameters:spec.inputSchema,apiHelper:true,
      description:spec.name.endsWith('call')?'Main-only ticket: execute one bounded API with UUID call_id, name and arguments.':'Main-only ticket: finish with factual outcome/summary/reason, explain result to player and END.',
      execute:async(args,exec)=>{
        const entry=owners.get(exec.agent?.id);
        if(!entry||entry.turn!==String(turns.get(exec.agent.id)))throw Error('No originating main turn');
        return pythonTool(spec.name,{...args,_origin:{harness:'dsh',sessionId:exec.agent.id,
          worldKey:entry.world,turnId:entry.turn}},exec.signal);
      }});
  }
  for(const spec of manifest) {
    tools.push({name:spec.name,description:spec.description,parameters:spec.inputSchema,
                execute:(args,exec)=>pythonTool(spec.name,args,exec.signal),
                apiHelper:true});
  }
  const create=tools.find(tool=>tool.name==='sp_ticket_create');
  create.description='Create a bounded speed or mcp_task ticket. Follow receipt executionMode: worker means acknowledge and END; main means continue THIS turn via sp_main_api_call/finish, report and END. Never spawn workers yourself or poll.';
  create.parameters.properties.command={type:'object',properties:{kind:{type:'string',enum:['set_speed','mcp_task']},
    unit_id:{type:'integer',minimum:1},knots:{type:'number',minimum:0,maximum:80},
    tools:{type:'array',items:{type:'string'}},resources:{type:'array',items:{type:'string'}},max_calls:{type:'integer',minimum:1,maximum:64}},required:['kind'],additionalProperties:false};
  create.parameters.properties.predicate.properties.kind.enum.push('execution_recorded');
  create.parameters.properties.predicate.required=['kind'];
  const existing = ctx.tools.view().visible;
  for(const tool of tools) {
    if(existing.has(tool.name)) continue;
    ctx.tools.register({
      ...tool,
      output:{schema:{type:'object',additionalProperties:true},render:(args,result)=>tool.apiHelper ? result.content : [{type:'text',text:JSON.stringify(result)}]},
      isConcurrencySafe:()=>tool.name==='sp_ping'||tool.name==='sp_ticket_list'
    });
  }
  return lifecycle;
}


export async function apply(ctx, config) {
  const main = registerTicketTools(ctx, config);
  ctx.effect(() => () => main.close(), "Sea Power main ticket lifecycle");
  const service = createWorkerService(ctx, config);
  ctx.effect(() => () => service.close(), "Sea Power ticket worker lifecycle");
  await service.listen();
  ctx.logger.info("Sea Power native ticket worker listening on loopback");
}
