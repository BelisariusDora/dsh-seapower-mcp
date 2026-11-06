"""Ticket-bound invocation of the existing MCP handlers; no duplicate game APIs."""
import base64
import hashlib
import json
import os
from pathlib import Path
import posixpath
import secrets
import time
import urllib.parse
import uuid

import api_catalog

def location(root=None):
    if root is not None:return Path(root)
    import watch_events
    return watch_events.MOD_DIR/'comms/run/tickets'

def authorize(args,root):
    from ticket_store import TicketStore
    ticket=TicketStore(root/'journal.jsonl').get(args.get('ticket_id'))
    token=args.get('report_token')
    if not ticket or not isinstance(token,str) or not secrets.compare_digest(token,ticket.get('reportToken','')):
        raise ValueError('Invalid ticket capability')
    if (ticket['command'].get('kind')!='mcp_task' and ticket.get('executionMode')!='main') or ticket['hidden'] or ticket['status'] not in {'starting','running'}:
        raise ValueError('Generic ticket is not executing')
    if int(time.time()*1000)>=ticket['deadlineAt']:raise ValueError('Ticket deadline elapsed')
    return ticket

def write(file,value):
    temporary=file.with_suffix('.tmp')
    with temporary.open('w',encoding='utf-8') as stream:
        json.dump(value,stream,ensure_ascii=False,allow_nan=False)
        stream.flush();os.fsync(stream.fileno())
    for attempt in range(6):
        try:os.replace(temporary,file);return
        except PermissionError:
            if attempt==5:raise
            time.sleep(0.02)

def validate(value,schema,path='arguments'):
    kind=schema.get('type')
    checks={'object':lambda:isinstance(value,dict),'array':lambda:isinstance(value,list),
            'integer':lambda:type(value) is int,'number':lambda:type(value) in (int,float),
            'string':lambda:isinstance(value,str),'boolean':lambda:type(value) is bool}
    if isinstance(kind,str) and kind in checks and not checks[kind]():raise ValueError(path+' has wrong type')
    if 'enum' in schema and value not in schema['enum']:raise ValueError(path+' is not an allowed value')
    if isinstance(value,dict):
        properties=schema.get('properties',{})
        for key in schema.get('required',[]):
            if key not in value:raise ValueError(path+'.'+key+' is required')
        for key,member in value.items():
            if key in properties:validate(member,properties[key],path+'.'+key)
            elif schema.get('additionalProperties') is False:raise ValueError(path+'.'+key+' is unknown')
    if isinstance(value,list) and isinstance(schema.get('items'),dict):
        for member in value:validate(member,schema['items'],path+'[]')
    if type(value) in (int,float):
        if 'minimum' in schema and value<schema['minimum']:raise ValueError(path+' below minimum')
        if 'maximum' in schema and value>schema['maximum']:raise ValueError(path+' above maximum')
    json.dumps(value,allow_nan=False)  # rejects NaN/Infinity and non-JSON objects

def ledger(root,ticket_id):
    directory=root/'api_calls'/ticket_id
    return [json.loads(file.read_text(encoding='utf-8')) for file in sorted(directory.glob('*.json'))]

def bridge_call(method,path,body=None):
    import mcp_server
    return mcp_server.call(method,path,body)

def guard(ticket,name,args,bridge):
    read_only=api_catalog.is_read_only(name,args)
    state=bridge('GET','/comms/state')
    live=bridge('GET','/tickets')
    present=next((t for t in live.get('data',{}).get('tickets',[]) if t['id']==ticket['id']),None)
    if state.get('ok') is not True or state.get('data',{}).get('worldKey')!=ticket['worldKey']:
        raise ValueError('Game world changed or unavailable')
    if not present or present['status'] not in {'starting','running'}:raise ValueError('Ticket no longer executing')
    if not read_only and state['data'].get('hold') is not False:raise ValueError('Player HOLD prevents writes')
    from ticket_predicates import resources
    declared=resources(ticket['command'])
    if not read_only and 'world' not in declared:
        unit=args.get('unit_id',args.get('unit'))
        if type(unit) is int:
            required='unit:'+str(unit)
        elif name in {'sp_plot_route','sp_plot_clear'}:
            required='world'  # implicit selected-unit navigation needs an exclusive scope
        elif api_catalog.domain(name)=='plot':
            required='plot'
        elif name=='sp_time':
            required='time'
        else:
            required='world'
        if name=='sp_raw' or required not in declared:
            raise ValueError('API write is outside the declared resource scope')
    if name=='sp_raw':
        raw=posixpath.normpath(urllib.parse.unquote(str(args.get('path',''))).split('?')[0]).lower()
        if not raw.startswith('/') or raw.startswith(('/tickets','/comms','/ui')):
            raise ValueError('Raw cannot bypass ticket/comms control boundary')

def invoke(args,root=None,tools=None,bridge=None):
    root=location(root)
    ticket=authorize(args,root)
    tools=tools or api_catalog.registry()
    name=api_catalog.ALIASES.get(args.get('name'),args.get('name'))
    speed=ticket['command'].get('kind')=='set_speed'
    allowed=['sp_set_speed'] if speed else ticket['command']['tools']
    if name not in tools or not api_catalog.business(name) or ('*' not in allowed and name not in allowed):
        raise ValueError('API outside ticket tool scope')
    arguments=args.get('arguments')
    validate(arguments,tools[name]['inputSchema'])
    if speed:
        action=ticket['command']
        if arguments != {'unit_id':action['unit_id'],'knots':action['knots']}:
            raise ValueError('Speed arguments must match the ticket exactly')
        from ticket_predicates import check_target
        observed=(bridge or bridge_call)('GET','/units/'+str(action['unit_id']))
        check_target(observed.get('data',{}),action)
    call_id=uuid.UUID(args.get('call_id','')).hex
    directory=root/'api_calls'/ticket['id']
    directory.mkdir(parents=True,exist_ok=True)
    fingerprint=hashlib.sha256(json.dumps({'name':name,'arguments':arguments},sort_keys=True,allow_nan=False).encode()).hexdigest()
    file=directory/(call_id+'.json')
    lock=directory/'call.lock'
    try:fd=os.open(lock,os.O_CREAT|os.O_EXCL|os.O_WRONLY,0o600)
    except FileExistsError:raise ValueError('Another call is executing; no new write was attempted')
    try:
        if file.exists():
            previous=json.loads(file.read_text(encoding='utf-8'))
            if previous['fingerprint']!=fingerprint:raise ValueError('call_id belongs to different arguments')
            if previous['status'] in {'intent','uncertain'}:
                return {'ok':False,'uncertain':True,'resend':'forbidden','call_id':call_id,'error':'Previous outcome unknown; never replay'}
            return {**previous['result'],'duplicate':True}
        records=ledger(root,ticket['id'])
        if any(r['status'] in {'intent','uncertain'} for r in records):
            raise ValueError('Uncertain prior call requires review; no further execution')
        if len(records)>=(1 if speed else ticket['command']['max_calls']):raise ValueError('Ticket call budget exhausted')
        guard(ticket,name,arguments,bridge or bridge_call)
        # Recheck cancellation/deadline after read-only preflight.
        authorize(args,root)
        row={'ticket_id':ticket['id'],'call_id':call_id,'name':name,'fingerprint':fingerprint,
             'arguments':arguments,'worldKey':ticket['worldKey'],'status':'intent','at':int(time.time()*1000),
             'readOnly':api_catalog.is_read_only(name,arguments)}
        write(file,row)
        try:
            reply=tools[name]['handler'](arguments)
            if not isinstance(reply,dict):reply={'ok':True,'data':reply}
            # Preserve image support without injecting base64 walls into a tool result.
            if '__image_b64__' in reply:
                binary=base64.b64decode(reply['__image_b64__'],validate=True)
                artifact=directory/(call_id+'.png')
                artifact.write_bytes(binary)
                reply={k:v for k,v in reply.items() if k!='__image_b64__'}
                reply['artifact']={'path':str(artifact.resolve()),'mimeType':reply.get('__image_mime__','image/png'),
                                   'sha256':hashlib.sha256(binary).hexdigest(),'next':'Use read_image or view_image if visual inspection is needed'}
            uncertain=reply.get('uncertain') is True or reply.get('resend')=='forbidden'
            status='uncertain' if uncertain else 'failed' if reply.get('ok') is False else 'succeeded'
            # Audit the original receipt, but present the same projection/MLP result
            # as a direct MCP call. Formatting must never execute the handler twice.
            import mcp_server
            rendered=mcp_server.present_tool_result(name,reply)
            text_items=[item['text'] for item in rendered.get('content',[]) if item.get('type')=='text']
            presented=json.loads(text_items[0]) if len(text_items)==1 else reply
            if not isinstance(presented,dict):presented={'data':presented}
            result={**presented,'ok':reply.get('ok',True),'call_id':call_id,'api':name,'executionStatus':status}
            row['receipt']=reply
        except (Exception,) as error:
            status='uncertain'  # handler may have sent a write; never assume it did not
            result={'ok':False,'uncertain':True,'resend':'forbidden','call_id':call_id,'api':name,
                    'error':type(error).__name__+': handler did not return a confirmed outcome'}
        write(file,{**row,'status':status,'result':result,'finishedAt':int(time.time()*1000)})
        return result
    finally:
        os.close(fd)
        lock.unlink()

def finish(args,root=None,bridge=None):
    root=location(root)
    ticket=authorize(args,root)
    records=ledger(root,ticket['id'])
    outcome=args.get('outcome','unreported')  # old native plugins remain compatible
    if outcome not in {'fulfilled','partial','blocked','needs_input','unreported'}:
        raise ValueError('Invalid task outcome')
    reason=args.get('reason','')
    if not isinstance(reason,str) or len(reason)>2000:
        raise ValueError('reason must be a string of at most 2000 characters')
    if outcome in {'blocked','needs_input'} and not reason.strip():
        raise ValueError('Blocked or clarification result requires a concrete reason')
    if not records and outcome not in {'blocked','needs_input'}:
        raise ValueError('No recorded API execution; do not claim completion')
    status='uncertain' if any(r['status'] in {'intent','uncertain'} for r in records) else 'failed' if any(r['status']=='failed' for r in records) else outcome if outcome in {'blocked','needs_input'} else 'executed'
    detail=str(args.get('summary','')).strip()[:2000]
    if not detail:raise ValueError('A short factual summary is required')
    # Player-facing reason comes first; the evidence/verification has dedicated fields.
    payload={'op':'report','requestId':str(uuid.uuid5(uuid.NAMESPACE_URL,ticket['id']+'/api-finish')),
             'id':ticket['id'],'worldKey':ticket['worldKey'],'reportToken':ticket['reportToken'],
             'status':status,'detail':detail,'taskOutcome':outcome,'outcomeReason':reason.strip(),
             'receipt':{'call_ids':[r['call_id'] for r in records]}}
    if ticket.get('executionMode')=='main':
        payload['mainReported']=True
        if ticket['command'].get('kind')=='set_speed' and status=='executed':
            if len(records)!=1:raise ValueError('Speed needs one matching call')
            payload.update(status='injected',receipt=records[0]['receipt'])
    return (bridge or bridge_call)('POST','/tickets',payload)


def execute(name,args):
    if name=='sp_api_find':return api_catalog.find(args)
    if name=='sp_api_schema':return api_catalog.describe(args)
    if name=='sp_api_call':return invoke(args)
    if name=='sp_api_finish':return finish(args)
    raise ValueError('Unknown API helper')
