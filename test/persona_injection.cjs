const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),os=require('node:os');
const {createLink}=require('../comms/harness_link.js');
const {project}=require('../comms/dsh_tailer.js');
const root=fs.mkdtempSync(path.join(os.tmpdir(),'seapower-persona-'));
const file=name=>path.join(root,name);
const cfg={mode:'game-world',pointerFile:file('pointer'),outbox:file('outbox'),offsetFile:file('offset'),
 stateFile:file('ledger'),statusFile:file('status'),personaFile:file('persona.md')};
fs.writeFileSync(cfg.outbox,'');fs.writeFileSync(cfg.personaFile,'STYLE_VERSION_ONE');
const prompts=[];
const link=createLink({cfg,readGameWorld:cb=>cb(null,'world:1'),log:()=>{},rpc(c,k,method,args,done){
 if(method==='session/prompt')prompts.push(args.request.content[0].text);
 done({ok:true,value:method==='session/create'?{sessionId:'persona-chat'}:{accepted:true}});
}});
function tick(){return new Promise((resolve,reject)=>link.tick(error=>error?reject(error):resolve()));}
function player(text){fs.appendFileSync(cfg.outbox,JSON.stringify({from:'player',world:'world:1',text})+'\n');}
function displayed(text){return project({type:'user/message',data:{content:[{type:'text',text}]}}).text;}
(async()=>{
 player('first');await tick();assert.match(prompts[0],/^<seapower-onceopen>/);assert.match(prompts[0],/STYLE_VERSION_ONE/);assert.equal(displayed(prompts[0]),'first');
 fs.writeFileSync(cfg.personaFile,'STYLE_VERSION_TWO');player('followup');await tick();
 assert.match(prompts[1],/^<seapower-persona>/);assert.match(prompts[1],/STYLE_VERSION_TWO/);assert.doesNotMatch(prompts[1],/STYLE_VERSION_ONE/);assert.equal(displayed(prompts[1]),'followup');
 link.cfg.personaFile=file('missing.md');player('without overlay');await tick();assert.equal(displayed(prompts[2]),'without overlay');assert.match(prompts[2],/executionMode/);assert.doesNotMatch(prompts[2],/STYLE_VERSION_ONE|STYLE_VERSION_TWO/);
 await tick();assert.equal(prompts.length,3);
 console.log('Persona checks passed: first turn, live edit, hidden transcript, missing-file compatibility, no replay.');
})().catch(error=>{console.error(error);process.exitCode=1});
