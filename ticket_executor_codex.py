"""Owned, ephemeral Codex app-server workers with one bounded game write."""
import json
import os
from pathlib import Path
import queue
import shutil
import subprocess
import threading
import time
import urllib.error
import urllib.request
import uuid

from ticket_executor import ExecutorUnavailable, process_alive


class CodexExecutor:
    def __init__(self, comms, port=19080, binary=None):
        self.comms = Path(comms)
        self.root = self.comms / 'run' / 'tickets'
        self.file = self.root / 'codex-workers.json'
        self.port = port
        self.binary = binary or shutil.which('codex')
        self.lock = threading.RLock()
        self.runs = {}
        self.records = json.loads(self.file.read_text(encoding='utf-8')) if self.file.exists() else {}
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def context(self, world):
        return {'harness': 'codex'}

    def save(self, ticket_id, **fields):
        with self.lock:
            self.records[ticket_id].update(fields)
            temporary = self.file.with_suffix('.tmp')
            with temporary.open('w', encoding='utf-8') as stream:
                json.dump(self.records, stream, ensure_ascii=False)
                stream.flush()
                os.fsync(stream.fileno())
            for attempt in range(6):
                try:
                    os.replace(temporary, self.file)
                    return
                except PermissionError:
                    if attempt == 5:
                        raise
                    time.sleep(0.02)

    def bridge(self, route, body=None):
        request = urllib.request.Request(f'http://127.0.0.1:{self.port}{route}',
            data=json.dumps(body).encode() if body is not None else None,
            headers={'Content-Type': 'application/json'})
        try:
            with self.opener.open(request, timeout=7) as response:
                return json.load(response)
        except urllib.error.HTTPError as error:
            return json.load(error)  # definite Bridge refusal, not a lost write receipt

    def status(self, ticket):
        with self.lock:
            record = dict(self.records.get(ticket['id'], {}))
            run = self.runs.get(ticket['id'])
        if not record:
            raise ValueError('Unknown Codex worker')
        if run is None and record.get('pid') and not record.get('exited'):
            # After scheduler recovery, never signal a PID whose ownership is lost.
            record['status'] = 'unknown' if process_alive(record['pid']) else 'failed'
        return record

    def stop(self, ticket):
        with self.lock:
            run = self.runs.get(ticket['id'])
        if run:
            run['stop'].set()
            return run['done'].is_set()
        state = self.status(ticket)
        return bool(state.get('exited')) or state['status'] == 'failed'

    def start(self, ticket):
        if not self.binary:
            raise ExecutorUnavailable('Codex executable unavailable')
        if ticket['id'] in self.records:
            # Never recreate an existing native worker, including after a crash.
            raise ValueError('Codex worker already exists; inspect its original outcome')
        arguments = [self.binary, 'app-server', '--stdio']
        for feature in ('shell_tool', 'unified_exec', 'multi_agent', 'apps'):
            arguments += ['--disable', feature]
        try:
            process = subprocess.Popen(arguments, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                stderr=subprocess.DEVNULL, text=True, encoding='utf-8',
                creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        except OSError as error:
            raise ExecutorUnavailable('Cannot launch Codex app-server') from error
        entry = {'process': process, 'stop': threading.Event(), 'done': threading.Event(),
                 'attempted': False, 'report': None, 'reportId': str(uuid.uuid4())}
        with self.lock:
            self.runs[ticket['id']] = entry
            self.records[ticket['id']] = {'ticketId': ticket['id'], 'childId': str(process.pid),
                'pid': process.pid, 'status': 'running', 'exited': False, 'createdAt': int(time.time()*1000)}
        try:
            self.save(ticket['id'])
        except OSError:
            process.terminate()
            process.wait(timeout=5)
            entry['done'].set()
            raise
        threading.Thread(target=self.worker, args=(ticket, entry), daemon=True).start()
        return {'ticketId': ticket['id'], 'childId': str(process.pid), 'harness': 'codex'}

    def tool(self, ticket, entry, name, arguments):
        if ticket['command']['kind'] == 'mcp_task':
            import ticket_api
            bound = {**arguments, 'ticket_id':ticket['id'], 'report_token':ticket['reportToken']}
            bridge = lambda method, route, body=None: self.bridge(route, body)
            if name in ('sp_api_find','sp_api_schema'):
                return ticket_api.execute(name, arguments)
            if name == 'sp_api_call':
                if entry['stop'].is_set():
                    raise InterruptedError('Worker cancelled')
                result = ticket_api.invoke(bound, root=self.root, bridge=bridge)
                if any(not row['readOnly'] for row in ticket_api.ledger(self.root,ticket['id'])):
                    entry['attempted'] = True
                    self.save(ticket['id'], writeAttempted=True)
                return result
            if name == 'sp_api_finish':
                result = ticket_api.finish(bound, root=self.root, bridge=bridge)
                if result.get('ok') is True:
                    entry['genericFinished'] = True
                return result
            raise ValueError('Tool is outside generic API execution helpers')
        if name == 'sp_comms_state':
            return self.bridge('/comms/state')
        if name != 'sp_speed':
            raise ValueError('Tool is outside the execution contract')
        action = ticket['command']
        if (arguments != {'unit_id': action['unit_id'], 'knots': action['knots']}
                or type(arguments.get('unit_id')) is not int
                or type(arguments.get('knots')) not in (int, float)):
            raise ValueError('Speed command differs from ticket authorization')
        if entry['attempted'] or entry['stop'].is_set():
            raise ValueError('Cancelled or already attempted; never resend')
        state = self.bridge('/comms/state')
        listing = self.bridge('/tickets')
        active = next((t for t in listing.get('data', {}).get('tickets', []) if t['id'] == ticket['id']), None)
        if (state.get('ok') is not True or state['data'].get('worldKey') != ticket['worldKey']
                or state['data'].get('hold') is not False or not active
                or active['status'] not in ('starting', 'running')
                or int(time.time()*1000) >= ticket['deadlineAt'] or entry['stop'].is_set()):
            raise ValueError('World, HOLD, cancellation or deadline prevents injection')
        # Persist intent before sending; a timeout/crash cannot authorize replay.
        entry['attempted'] = True
        self.save(ticket['id'], writeAttempted=True)
        try:
            receipt = self.bridge(f"/units/{action['unit_id']}/speed", {'knots': action['knots']})
        except (OSError, ValueError, TimeoutError):
            entry['report'] = ('uncertain', None, 'Speed write outcome unknown; never retry')
            raise
        if receipt.get('ok') is True:
            entry['report'] = ('injected', receipt, 'Codex injected the single authorized command')
        else:
            entry['report'] = ('uncertain' if receipt.get('uncertain') else 'failed', receipt,
                               'Bridge did not confirm successful injection')
        return receipt

    def worker(self, ticket, entry):
        process = entry['process']
        messages = queue.Queue()
        thread_id = turn_id = None
        outcome = 'failed'

        def read():
            for line in process.stdout:
                try:
                    messages.put(json.loads(line))
                except ValueError:
                    messages.put({'readerError': True})
            messages.put({'readerError': True})
        threading.Thread(target=read, daemon=True).start()

        def send(value):
            process.stdin.write(json.dumps(value)+'\n')
            process.stdin.flush()

        def receive():
            while True:
                if entry['stop'].is_set() or int(time.time()*1000) >= ticket['deadlineAt']:
                    raise InterruptedError('Worker cancelled or wall deadline elapsed')
                try:
                    message = messages.get(timeout=0.2)
                except queue.Empty:
                    continue
                if message.get('readerError'):
                    raise OSError('Codex transport ended')
                return message

        def rpc(method, params, request_id):
            send({'id': request_id, 'method': method, 'params': params})
            while True:
                reply = receive()
                if reply.get('id') == request_id:
                    if reply.get('error'):
                        raise ValueError('Codex rejected '+method)
                    return reply['result']
                if reply.get('method') and 'id' in reply:
                    raise ValueError('Unexpected Codex server request during initialization')

        try:
            rpc('initialize', {'clientInfo': {'name': 'seapower-executor', 'version': '0.1.0'},
                              'capabilities': {'experimentalApi': True}}, 1)
            send({'method': 'initialized', 'params': {}})
            tools = [
                {'type': 'function', 'name': 'sp_comms_state', 'description': 'Read current game world and HOLD.',
                 'inputSchema': {'type': 'object', 'properties': {}, 'additionalProperties': False}},
                {'type': 'function', 'name': 'sp_speed', 'description': 'Inject the one authorized speed command once, then stop.',
                 'inputSchema': {'type': 'object', 'properties': {'unit_id': {'type': 'integer'}, 'knots': {'type': 'number'}},
                                 'required': ['unit_id', 'knots'], 'additionalProperties': False}}]
            generic = ticket['command']['kind'] == 'mcp_task'
            if generic:
                import api_catalog
                tools = []
                for spec in api_catalog.helper_specs():
                    schema = json.loads(json.dumps(spec['inputSchema']))
                    for private in ('ticket_id','report_token'):
                        schema.get('properties',{}).pop(private,None)
                    schema['required'] = [key for key in schema.get('required',[]) if key not in ('ticket_id','report_token')]
                    tools.append({'type':'function','name':spec['name'],'description':spec['description'], 'inputSchema':schema})
            config = {'web_search': 'disabled'}
            # Disable inherited MCP tools for this ephemeral worker only.
            import tomllib
            config_path = Path(os.environ.get('CODEX_HOME', str(Path.home()/'.codex'))) / 'config.toml'
            if config_path.exists():
                with config_path.open('rb') as stream:
                    inherited = tomllib.load(stream).get('mcp_servers', {})
                for server in inherited:
                    config[f'mcp_servers.{server}.enabled'] = False
            result = rpc('thread/start', {'cwd': str(self.root.resolve()), 'ephemeral': True,
                'sandbox': 'read-only', 'approvalPolicy': 'on-request', 'dynamicTools': tools,
                'config': config, 'developerInstructions': ('Use only sp_api_find, sp_api_schema, sp_api_call, sp_api_finish and view_image on returned artifacts. Never read source or manuals. No shell, delegation, other tools or monitoring.' if generic else 'Use only sp_comms_state and sp_speed. No shell, files, delegation, other tools or monitoring.')}, 2)
            thread_id = result['thread']['id']
            action = ticket['command']
            prompt = "" if generic else (f"Execute exactly one Sea Power order: sp_speed(unit_id={action['unit_id']}, knots={action['knots']}). "
                      f"First read sp_comms_state; worldKey must equal {ticket['worldKey']} and HOLD false. "
                      'The tool host checks authorization and reports the receipt to the scheduler automatically. '
                      'Do not retry, monitor, change time, call other tools, or contact any main chat. Stop after injection.')
            if generic:
                prompt = (f"Execute this authorized Sea Power task and no other work: {ticket['task']}\n"
                          f"Tools allowed: {action['tools']}; max calls: {action['max_calls']}.\n"
                          'Use sp_api_find by domain/query, then sp_api_schema for exact parameters, then sp_api_call with UUID call_id. '
                          'Domains: plot, deck, units, watch, time, inspect, comms. Authorization is injected by the host. '
                          'Do not read project files or docs. Do not guess APIs. Unknown writes cannot be retried. '
                          'Finish with sp_api_finish: outcome=fulfilled/partial/blocked/needs_input, factual summary, and concrete reason for blocked/needs_input. Only reading is not fulfillment of an attack or movement task. Immediately exit. '
                          'Executed is not independently verified game effects. No polling, waiting, extra tasks or time changes to wait.')
            result = rpc('turn/start', {'threadId': thread_id, 'input': [{'type': 'text', 'text': prompt}]}, 3)
            turn_id = result['turn']['id']
            self.save(ticket['id'], threadId=thread_id, turnId=turn_id)
            while not entry['report'] and not entry.get('genericFinished'):
                message = receive()
                if message.get('method') == 'item/tool/call' and 'id' in message:
                    params = message['params']
                    entry.setdefault('toolNames', []).append(params.get('tool'))
                    name = params.get('tool')
                    self.save(ticket['id'], toolNames=entry['toolNames'][-128:], lastTool=name)
                    if params['threadId'] != thread_id or params['turnId'] != turn_id:
                        raise ValueError('Wrong Codex tool-call identity')
                    try:
                        answer = self.tool(ticket, entry, name, params['arguments'])
                    except (ValueError, KeyError, TypeError) as error:
                        # Discovery has no game effects. Return parameter errors to the
                        # model so it can select valid names or split a schema batch.
                        # Never apply this recovery to business execution or reporting.
                        if not generic or name not in ('sp_api_find', 'sp_api_schema'):
                            raise
                        detail = (type(error).__name__ + ': ' + str(error)).replace(ticket['reportToken'], '[redacted]')[:500]
                        self.save(ticket['id'], lastToolError=detail,
                                  discoveryErrors=self.records[ticket['id']].get('discoveryErrors', 0) + 1)
                        answer = {'ok': False, 'error': {'code': 'invalid_discovery_request', 'message': detail},
                                  'hint': 'Use exact names returned by sp_api_find; sp_api_schema accepts 1..8 names per call. Correct the request; no game command was executed.'}
                    send({'id': message['id'], 'result': {'success': answer.get('ok') is True,
                          'contentItems': [{'type': 'inputText', 'text': json.dumps(answer)}]}})
                elif message.get('method') and 'id' in message:
                    send({'id': message['id'], 'error': {'code': -32601, 'message': 'Request is outside the ticket contract'}})
                    raise ValueError('Worker requested an unauthorized tool or approval')
                elif message.get('method') == 'turn/completed':
                    raise ValueError('Codex ended without recording the authorized execution')
            if entry.get('genericFinished'):
                outcome = 'completed'
        except InterruptedError:
            outcome = 'cancelled'
        except (OSError, ValueError, KeyError, TypeError, TimeoutError) as error:
            detail = (type(error).__name__ + ': ' + str(error)).replace(ticket['reportToken'], '[redacted]')[:500]
            self.save(ticket['id'], lastError=detail, lastTool=(entry.get('toolNames') or [None])[-1])
            if entry['report'] is None:
                entry['report'] = ('uncertain' if entry['attempted'] else 'failed', None,
                                   'Codex worker failed: ' + detail)
        finally:
            if entry['report'] and not entry['stop'].is_set():
                status, receipt, detail = entry['report']
                report = {'op': 'report', 'id': ticket['id'], 'requestId': entry['reportId'],
                          'worldKey': ticket['worldKey'], 'reportToken': ticket['reportToken'],
                          'status': status, 'detail': detail, 'receipt': receipt}
                for attempt in range(2):
                    try:
                        reply = self.bridge('/tickets', report)
                        self.save(ticket['id'], reportAccepted=reply.get('ok') is True,
                                  injectionStatus=status)
                        outcome = 'completed' if status == 'injected' else 'failed'
                        break
                    except (OSError, ValueError, TimeoutError):
                        outcome = 'unknown'
            try:
                if thread_id and turn_id and process.poll() is None:
                    send({'id': 99, 'method': 'turn/interrupt', 'params': {'threadId': thread_id, 'turnId': turn_id}})
                process.stdin.close()
                process.wait(timeout=3)
            except (OSError, subprocess.TimeoutExpired):
                if process.poll() is None:
                    process.terminate()
                    process.wait(timeout=5)
            self.save(ticket['id'], status=outcome, exited=True, finishedAt=int(time.time()*1000))
            entry['done'].set()
