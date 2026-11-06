"""Append-only ticket authority. Version 1 rows remain readable as legacy history."""
import copy
import hashlib
import json
import math
import os
from pathlib import Path
import secrets
import time
import uuid

from ticket_predicates import task_contract, conflicts
from ticket_notifications import validate_origin

ACTIVE = {"queued", "starting", "running", "monitoring", "uncertain"}
FINAL = {"completed", "executed", "failed", "timed_out", "cancelled", "blocked", "needs_input"}


def text(value, name, maximum):
    if not isinstance(value, str) or not value.strip() or len(value) > maximum:
        raise ValueError(f"{name} must contain 1..{maximum} characters")
    return value.strip()


class TicketStore:
    def __init__(self, file, now=None):
        self.file = Path(file)
        self.now = now or (lambda: int(time.time() * 1000))
        self.tickets = {}
        self.requests = {}
        self.poisoned = False
        self._presentation_cache = {}
        self.file.parent.mkdir(parents=True, exist_ok=True)
        if self.file.exists():
            raw = self.file.read_bytes()
            if raw and not raw.endswith(b"\n"):
                raise ValueError("Incomplete ticket journal. Inspect before starting.")
            for line in raw.splitlines():
                row = json.loads(line)
                if row.get("version") not in (1, 2) or not isinstance(row.get("ticket"), dict):
                    raise ValueError("Unsupported ticket journal")
                ticket = row["ticket"]
                if row["version"] == 1:
                    ticket = {**ticket, "schemaVersion": 1, "legacy": True, "resultVerified": False}
                    if ticket.get("workerSessionId"):
                        ticket["workerHandle"] = {"ticketId": ticket["id"], "childId": ticket["workerSessionId"]}
                    if ticket["status"] in ACTIVE | {"needs_review"}:
                        ticket.update(status="uncertain", detail="Legacy execution requires review; never replay")
                self.tickets[ticket["id"]] = ticket
                if row.get("requestId"):
                    self.requests[row["requestId"]] = row

    def commit(self, ticket, request=None):
        if self.poisoned:
            raise OSError("Ticket storage failed; inspect the journal before restart")
        row = {"version": 2, "ticket": ticket}
        if request:
            row.update(request)
        try:
            with self.file.open("ab") as stream:
                stream.write((json.dumps(row, ensure_ascii=False, separators=(",", ":"), allow_nan=False) + "\n").encode("utf-8"))
                stream.flush()
                os.fsync(stream.fileno())
        except OSError:
            self.poisoned = True
            raise
        self.tickets[ticket["id"]] = copy.deepcopy(ticket)
        if request:
            self.requests[request["requestId"]] = copy.deepcopy(row)
        return copy.deepcopy(ticket)

    def change(self, ticket, **fields):
        return self.commit({**ticket, **fields, "revision": ticket["revision"] + 1, "updatedAt": self.now()})

    def all(self):
        return copy.deepcopy(list(self.tickets.values()))

    def get(self, ticket_id):
        return copy.deepcopy(self.tickets.get(ticket_id))

    def snapshot(self, world, language="zh"):
        keys = ("id", "title", "status", "workerStatus", "detail", "revision", "worldKey",
                "createdAt", "updatedAt", "deadlineAt", "resultVerified", "legacy", "verification", "callCount", "executionMode")
        from ticket_presentation import cached
        from ticket_notifications import presentation
        return [{**{k: t[k] for k in keys if k in t}, **cached(t, self.file.parent, self._presentation_cache, language), "notificationState": presentation(self.file.parent, t["id"]), "canDestroy": True}
                for t in self.all() if t["worldKey"] == world and not t["hidden"]]

    def apply(self, command, world, context=None, main_only=False):
        request_id = text(command.get("requestId"), "requestId", 128)
        if command.get("worldKey") != world:
            raise ValueError("Ticket belongs to a changed game world")
        fingerprint = hashlib.sha256(json.dumps(command, ensure_ascii=False, separators=(",", ":"),
                                                allow_nan=False).encode()).hexdigest()
        previous = self.requests.get(request_id)
        if previous:
            if previous["fingerprint"] != fingerprint:
                raise ValueError("requestId belongs to different input")
            return {**previous["result"], "duplicate": True}
        op = command.get("op")
        if op == "create":
            action, predicate = task_contract(command.get("command"), command.get("predicate"))
            seconds = command.get("timeoutSeconds", 900)
            if type(seconds) is not int or not 10 <= seconds <= 3600:
                raise ValueError("timeoutSeconds must be 10..3600 wall-clock seconds")
            if len([t for t in self.all() if t["worldKey"] == world and not t["hidden"]]) >= 256:
                raise ValueError("Visible ticket limit reached")
            if len([t for t in self.all() if t["worldKey"] == world and t["status"] in ACTIVE]) >= 64:
                raise ValueError("Active ticket limit reached")
            if any(t["worldKey"] == world and t["status"] in ACTIVE and not t["hidden"]
                   and conflicts(t.get("command", {}), action) for t in self.all()):
                raise ValueError("An active ticket already controls this unit/resource; cancel it before overriding")
            origin = validate_origin(command.get('origin'), world)
            if main_only and (not origin or not origin.get('turnId')):
                raise ValueError('Main-only tickets require a host-bound session and turn')
            now = self.now()
            ticket = {
                "schemaVersion": 2, "id": "ticket-" + str(uuid.uuid4()), "worldKey": world,
                "title": text(command.get("title"), "title", 120),
                "task": text(command.get("task"), "task", 12000),
                "completion": text(command.get("completion"), "completion", 4000),
                "command": action, "predicate": predicate, "executorContext": context,
                "origin": origin, "executionMode": "main" if main_only else "worker",
                "status": "queued", "workerStatus": "pending", "detail": "Accepted; awaiting dispatch",
                "revision": 1, "hidden": False, "resultVerified": False,
                "createdAt": now, "updatedAt": now, "deadlineAt": now + seconds * 1000,
                "reportToken": secrets.token_hex(24), "workerHandle": None, "cancelPending": False,
                "receipt": None, "observation": None, "stableSamples": 0,
            }
            if main_only:
                ticket.update(status='running', workerStatus='main_running', workerExited=True,
                              detail='Executing in the originating main turn', executorContext=None)
        else:
            ticket = self.get(command.get("id"))
            if not ticket or ticket["worldKey"] != world:
                raise ValueError("Ticket does not exist in this world")
            ticket["revision"] += 1
            ticket["updatedAt"] = self.now()
            if op == "destroy":
                ticket.update(hidden=True, detail="Destroyed by player")
                if ticket["status"] in ACTIVE:
                    ticket["status"] = "cancelled"
                    ticket["cancelPending"] = bool(ticket.get("workerHandle"))
            elif op == "report":
                if ticket.get("schemaVersion") != 2 or ticket["hidden"] or ticket["status"] not in {"running", "starting"}:
                    raise ValueError("Ticket is no longer accepting execution reports")
                token = command.get("reportToken")
                if not isinstance(token, str) or not secrets.compare_digest(token, ticket["reportToken"]):
                    raise ValueError("Invalid worker report token")
                status = command.get("status")
                detail = text(command.get("detail"), "detail", 4000)
                if status == "completed":
                    status = "injected"  # legacy spelling never grants game completion
                if status not in {"running", "injected", "executed", "failed", "uncertain", "blocked", "needs_input"} | ({"cancelled"} if ticket.get("executionMode")=="main" else set()):
                    raise ValueError("Invalid worker status")
                receipt = command.get("receipt")
                if status in {"executed", "blocked", "needs_input"}:
                    if (ticket['command'].get('kind') != 'mcp_task' and not (ticket.get('executionMode')=='main' and status in {'blocked','needs_input'})) or not isinstance(receipt,dict):
                        raise ValueError('Executed requires a generic task with host call evidence')
                    from ticket_api import ledger
                    records = ledger(self.file.parent, ticket['id'])
                    expected = sorted(r['call_id'] for r in records)
                    if (not records and status == 'executed') or sorted(receipt.get('call_ids', [])) != expected or any(
                            r['status'] != 'succeeded' or r.get('worldKey') != world for r in records):
                        raise ValueError('Executed requires all persisted calls to have confirmed receipts')
                    outcome = command.get('taskOutcome', 'unreported')
                    if outcome not in {'fulfilled', 'partial', 'blocked', 'needs_input', 'unreported'}:
                        raise ValueError('Invalid structured task outcome')
                    if (status in {'blocked', 'needs_input'} and outcome != status) or (status == 'executed' and outcome in {'blocked', 'needs_input'}):
                        raise ValueError('Task outcome and lifecycle status disagree')
                    reason = command.get('outcomeReason', '')
                    if not isinstance(reason, str) or len(reason) > 2000:
                        raise ValueError('Invalid outcome reason')
                    if status in {'blocked', 'needs_input'} and not reason.strip():
                        raise ValueError('Blocked or clarification result requires a reason')
                    ticket.update(status=status, taskOutcome=outcome, outcomeReason=reason,
                                  verification='execution_only', callCount=len(records),
                                  receipt=receipt, resultVerified=False)
                elif status == "injected":
                    action = ticket["command"]
                    data = receipt.get("data", {}) if isinstance(receipt, dict) else {}
                    valid = (isinstance(receipt, dict) and isinstance(data, dict)
                             and receipt.get("ok") is True and receipt.get("uncertain") is not True
                             and receipt.get("resend") != "forbidden"
                             and data.get("id") == action["unit_id"] and data.get("action") == "speed"
                             and data.get("knots") == action["knots"] and type(receipt.get("cmd")) is int)
                    if not valid:
                        raise ValueError("Injected requires the matching successful raw Bridge receipt")
                    ticket.update(status="monitoring", injectedAt=self.now(), receipt=receipt)
                elif status in {"failed", "uncertain", "cancelled"}:
                    ticket["status"] = status
                    ticket["receipt"] = receipt
                if ticket.get('executionMode') == 'main':
                    ticket.update(mainReported=bool(command.get('mainReported')), workerExited=True)
                ticket.update(workerStatus=status, detail=detail)
            else:
                raise ValueError("Unknown ticket operation")
        result = {"ok": True, "id": ticket["id"], "status": ticket["status"], "revision": ticket["revision"]}
        if op == 'create':
            result.update(executionMode=ticket['executionMode'],
                          nextAction='execute_in_this_turn' if main_only else 'end_turn')
        self.commit(ticket, {"requestId": request_id, "fingerprint": fingerprint, "result": result})
        return result

    def end(self, ticket, status, detail):
        return self.change(ticket, status=status, detail=detail,
                           cancelPending=bool(ticket.get("workerHandle")), stableSamples=0)

    def maintain(self, world, confirmed):
        for ticket in self.all():
            if ticket["status"] not in ACTIVE:
                continue
            if confirmed and ticket["worldKey"] != world:
                self.end(ticket, "cancelled", "Game world changed")
            elif self.now() >= ticket["deadlineAt"]:
                self.end(ticket, "timed_out", "Wall-clock deadline elapsed")
