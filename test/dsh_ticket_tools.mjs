import assert from 'node:assert/strict';
import http from 'node:http';
import {registerTicketTools} from '../comms/dsh_ticket_plugin.mjs';
const registry=new Map();
const requests=[];
const server=http.createServer(async(req,res)=>{
  let text=''; for await(const chunk of req)text+=chunk;
  requests.push({method:req.method,path:req.url,body:text?JSON.parse(text):null});
  res.setHeader('content-type','application/json');
  res.end(JSON.stringify({ok:true,cmd:77,data:{id:12,action:'speed',knots:5}}));
});
await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
try {
  registerTicketTools({tools:{view:()=>({visible:registry}),register:tool=>registry.set(tool.name,tool)}},
    {bridgePort:server.address().port});
  const signal=new AbortController().signal;
  const receipt=await registry.get('sp_speed').execute({unit_id:12,knots:5},{signal});
  assert.equal(receipt.cmd,77);
  assert.equal(receipt.data.knots,5);
  assert.equal(receipt.ok,true);
  assert.ok(registry.get('sp_ticket_report').parameters.properties.status.enum.includes('injected'));
  assert.ok(registry.get('sp_ticket_report').parameters.properties.status.enum.includes('uncertain'));
  assert.ok(registry.get('sp_ticket_create').parameters.properties.predicate);
  await registry.get('sp_ticket_report').execute({
    id:'ticket',requestId:'request',reportToken:'test-only',status:'injected',detail:'submitted',receipt
  },{signal});
  assert.deepEqual(requests[0],{method:'POST',path:'/units/12/speed',body:{knots:5}});
  assert.equal(requests[1].body.receipt.cmd,77);
  assert.equal(requests[1].body.op,'report');
  console.log('PASS native tools preserve raw write receipts and TR1 report/schema');
} finally {
  await new Promise(resolve=>server.close(resolve));
}
