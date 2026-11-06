"""DSH-only session binding, prompt construction and native one-shot lifecycle."""
import json
from pathlib import Path
import urllib.request


from ticket_executor import ExecutorUnavailable


class DshExecutor:
    def __init__(self, comms, port=19389, timeout=3):
        self.comms = Path(comms)
        self.root = self.comms / "run" / "tickets"
        self.port = port
        self.timeout = timeout
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def context(self, world):
        try:
            pin = json.loads((self.comms / "run" / "session.pointer").read_text(encoding="utf-8-sig"))
        except (OSError, ValueError):
            return None
        if pin.get("source") == "game-world" and pin.get("world") == world and pin.get("sessionId"):
            return {"harness": "dsh", "parent": pin["sessionId"]}
        return None

    def call(self, route, body=None):
        token = (self.root / "auth.token").read_text(encoding="utf-8").strip()
        encoded = json.dumps(body, ensure_ascii=False).encode() if body is not None else None
        request = urllib.request.Request(
            f"http://127.0.0.1:{self.port}{route}", data=encoded,
            headers={"Authorization": "Bearer " + token, "Content-Type": "application/json"})
        with self.opener.open(request, timeout=self.timeout) as response:
            raw = response.read(131073)
        if len(raw) > 131072:
            raise ValueError("Oversized DSH reply")
        result = json.loads(raw)
        if result.get("ok") is not True:
            raise ValueError("DSH worker request failed")
        return result.get("data", result)

    def start(self, ticket):
        try:
            health = self.call("/health")
        except (OSError, ValueError, TimeoutError) as error:
            raise ExecutorUnavailable("DSH ticket adapter is unavailable") from error
        if health.get("ticketProtocol") != 2:
            raise ExecutorUnavailable("DSH plugin must load ticket protocol 2")
        action = ticket["command"]
        if action['kind'] == 'mcp_task':
            if health.get('apiTicketProtocol') != 1:
                raise ExecutorUnavailable('DSH plugin must load generic API ticket helpers')
            prompt = "\n".join((
                'You are the Sea Power API execution worker. Use only sp_api_find, sp_api_schema, sp_api_call, sp_api_finish and read_image for returned artifacts.',
                'The existing APIs are already wrapped. Never read project source, manuals, use shell, delegate, or contact the main agent.',
                f"Authorized task and limits: {ticket['task']}",
                f"Ticket capability: ticket_id={ticket['id']}, report_token={ticket['reportToken']}",
                f"Tool scope: {action['tools']}; resources: {action['resources']}; maximum business calls: {action['max_calls']}",
                'Discover a small relevant domain, obtain exact schemas, then execute only the authorized task. Never guess arguments.',
                'Domains: plot (map/drawing), deck (launch/recover), units (navigation/sensors/weapons), watch, time, inspect, comms.',
                'sp_api_call takes ticket_id, report_token, fresh UUID call_id, name and arguments. Reuse a call_id only for the identical invocation.',
                'Unknown write outcomes are uncertain and cannot be replayed. Do not make another call to work around uncertainty.',
                'sp_api_finish takes ticket_id, report_token, outcome=fulfilled/partial/blocked/needs_input and summary; include reason for blocked/needs_input. Reading alone does not fulfill an attack/movement task. It validates host receipts and ends the ticket; immediately exit.',
                'Executed means APIs ran, not verified battle results. Do not monitor, sleep, poll, alter game time to wait, or add work.',
            ))
            context = ticket['executorContext']
            result = self.call('/start', {'ticketId':ticket['id'],'parentSessionId':context['parent'],
                                         'prompt':prompt,'mode':'api-task'})
            if not result.get('childId'):
                raise ValueError('Native worker identity unavailable')
            return {'ticketId':ticket['id'],'childId':result['childId']}
        adapter = self.comms.parent / "mcp_server.py"
        prompt = "\n".join((
            "You are the Sea Power execution worker. Inject the single authorized command, then exit.",
            "Do not monitor results, wait for simulation time, change time compression, add tasks or wake the parent.",
            f"Ticket: {ticket['id']} / world: {ticket['worldKey']}",
            f"Private reportToken: {ticket['reportToken']}",
            f"Authorization and constraints: {ticket['task']}",
            f"Only command: sp_speed(unit_id={action['unit_id']}, knots={action['knots']})",
            "Immediately before writing, read sp_comms_state and require the same worldKey and hold=false.",
            f"Expected worldKey: {ticket['worldKey']}",
            "If identity changed or HOLD is on, report failed without a game write.",
            "Call sp_speed exactly once. On a successful reply, report injected and include the complete raw Bridge",
            "envelope (ok, cmd, data) in receipt. Do not interpret the write receipt as game completion.",
            "If the write outcome is unknown, report uncertain and do not retry.",
            "If definitely refused, report failed. End after the report; the scheduler monitors the game.",
            f"Use sp_ticket_report with id={ticket['id']}, private token, fresh UUID requestId, status, detail and receipt.",
            f"MCP adapter: {adapter}",
            "If native sp_ tools are absent, run this Python adapter with a UTF-8 JSON-RPC tools/call line on stdin.",
            "Never read adapter source, create other tickets, call subagent tools or send_message.",
        ))
        context = ticket["executorContext"]
        result = self.call("/start", {"ticketId": ticket["id"], "parentSessionId": context["parent"], "prompt": prompt})
        if not result.get("childId"):
            raise ValueError("Native worker identity unavailable")
        return {"ticketId": ticket["id"], "childId": result["childId"]}

    def status(self, ticket):
        return self.call("/status", {"ticketId": ticket["id"]})

    def stop(self, ticket):
        result = self.call("/cancel", {"ticketId": ticket["id"]})
        return result.get("status") in {"completed", "cancelled", "failed"}
