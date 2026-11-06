"""Main-session adapter over the existing ticket capability and API ledger."""
import configparser
import copy
import json
from pathlib import Path
import uuid

import ticket_api


def enabled(file):
    config = configparser.ConfigParser(interpolation=None, strict=False, inline_comment_prefixes=(';', '#'))
    try:
        config.read(Path(file), encoding='utf-8-sig')
        return any(config.get(section, 'MainAgentOnly', fallback='false').strip().lower()=='true'
                   for section in config.sections() if section.lower()=='tickets')
    except (OSError, UnicodeError, configparser.Error):
        return False


def helper_specs():
    import api_catalog
    result=[]
    for spec in api_catalog.helper_specs():
        if spec['name'] not in ('sp_api_call','sp_api_finish'):
            continue
        spec=copy.deepcopy(spec)
        spec['name']=spec['name'].replace('sp_api_', 'sp_main_api_')
        spec['inputSchema']['properties'].pop('report_token',None)
        spec['inputSchema']['required']=[k for k in spec['inputSchema'].get('required',[]) if k!='report_token']
        spec['description']='Main-agent-only ticket: '+('invoke one API with its ticket_id, a UUID call_id, name and arguments; keep unknown outcomes uncertain.' if spec['name'].endswith('call') else 'report factual outcome/summary/reason, then explain the result to the player and end the turn.')
        result.append(spec)
    return result


def bind(args, root=None):
    from ticket_store import TicketStore
    root=ticket_api.location(root)
    ticket=TicketStore(root/'journal.jsonl').get(args.get('ticket_id'))
    origin=args.get('_origin')
    if (not ticket or ticket.get('executionMode')!='main' or not isinstance(origin,dict)
            or ticket.get('origin')!=origin or not origin.get('turnId')):
        raise ValueError('Main execution belongs to a different originating session/turn')
    return ticket, {**args,'ticket_id':ticket['id'],'report_token':ticket['reportToken']}


def execute(name,args,root=None,bridge=None):
    ticket,bound=bind(args,root)
    if name=='sp_main_api_call':
        return ticket_api.invoke(bound,root=root,bridge=bridge)
    if name=='sp_main_api_finish':
        return ticket_api.finish(bound,root=root,bridge=bridge)
    raise ValueError('Unknown main execution helper')


def end_turn(args,root=None,bridge=None):
    """Host lifecycle cleanup only; it never executes business APIs or retries writes."""
    from ticket_store import TicketStore
    root=ticket_api.location(root)
    ended=[]
    for ticket in TicketStore(root/'journal.jsonl').all():
        if (ticket.get('executionMode')!='main' or ticket.get('origin')!=args.get('_origin')
                or ticket['status'] not in {'starting','running'}):
            continue
        records=ticket_api.ledger(root,ticket['id'])
        unknown=any(r['status'] in {'intent','uncertain'} for r in records)
        status='uncertain' if unknown or not args.get('cancelled') else 'cancelled'
        payload={'op':'report','requestId':str(uuid.uuid5(uuid.NAMESPACE_URL,ticket['id']+'/main-end')),
            'id':ticket['id'],'worldKey':ticket['worldKey'],'reportToken':ticket['reportToken'],
            'status':status,'detail':'Main session stopped before a structured execution result; no automatic replay',
            'mainReported':False}
        ended.append((bridge or ticket_api.bridge_call)('POST','/tickets',payload))
    return {'ok':True,'ended':ended}


def mode_prompt(on):
    return ('Sea Power execution mode: '+('MAIN AGENT ONLY. Create a ticket first; when its executionMode is main, '
        'continue in THIS turn using sp_api_find/schema and sp_main_api_call/finish with its ticket_id. '
        'You execute all business interactions. No workers, delegation, shell, source reading or direct game writes. '
        'Report factual results and end; never poll for asynchronous effects. '
        if on else 'WORKER. Create a bounded ticket, acknowledge acceptance and END; the scheduler executes independently. ')
        +'The create receipt fixes executionMode for that ticket even if settings change. '
        'A seapower-notice turn is always reply-only regardless of this mode.')
