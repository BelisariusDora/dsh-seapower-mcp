"""Independent single-writer scheduler using the existing C# ticket file protocol."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import secrets
import signal
import sys
import time
import uuid


# Both modular deployments and the flat source checkout use the same imports.
_layout = Path(__file__).resolve().parents[1]
if _layout.name == 'modules':
    sys.path.insert(0, str(_layout))
from runtime_layout import activate
activate()

import mcp_server
from ticket_executor import ExecutorUnavailable, process_alive
from ticket_predicates import check_target, evaluate_speed
from ticket_store import TicketStore


def atomic(file, value):
    file = Path(file)
    temporary = file.with_name(file.name + f".{os.getpid()}.tmp")
    with temporary.open("wb") as stream:
        stream.write(json.dumps(value, ensure_ascii=False, separators=(",", ":"), allow_nan=False).encode())
        stream.flush()
        os.fsync(stream.fileno())
    for attempt in range(6):
        try:
            os.replace(temporary, file)
            return
        except PermissionError:
            if attempt == 5:
                raise
            time.sleep(0.02)




class RuntimeLock:
    def __init__(self, file):
        self.file = Path(file)

    def __enter__(self):
        if self.file.exists():
            previous = json.loads(self.file.read_text(encoding="utf-8"))
            if process_alive(previous["pid"]):
                raise RuntimeError("Another ticket runtime owns this directory")
            self.file.unlink()
        self.fd = os.open(self.file, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
        os.write(self.fd, json.dumps({"pid": os.getpid(), "runtime": "python-tr1"}).encode())
        return self

    def __exit__(self, *args):
        os.close(self.fd)
        self.file.unlink()


class Game:
    def read(self, path):
        # Scheduler-only GETs may time out and retry without changing write semantics.
        url = f"http://127.0.0.1:{mcp_server.PORT}{path}"
        with mcp_server.BRIDGE_OPENER.open(url, timeout=1) as response:
            raw = response.read(1024 * 1024 + 1)
        if len(raw) > 1024 * 1024:
            raise ValueError("Oversized game read")
        return self.data(json.loads(raw.decode("utf-8")))

    def state(self):
        return self.read("/comms/state")

    def unit(self, unit_id):
        return self.read(f"/units/{int(unit_id)}")

    @staticmethod
    def data(reply):
        if reply.get("ok") is not True or not isinstance(reply.get("data"), dict):
            raise ValueError((reply.get("error") or {}).get("message", "Game read unavailable"))
        return reply["data"]


class Scheduler:
    def __init__(self, comms, game, executor, now=None, sample_interval=1500):
        self.root = Path(comms) / "run" / "tickets"
        self.commands = self.root / "commands"
        self.responses = self.root / "responses"
        self.commands.mkdir(parents=True, exist_ok=True)
        self.responses.mkdir(parents=True, exist_ok=True)
        self.store = TicketStore(self.root / "journal.jsonl", now)
        self.now = self.store.now
        from ticket_notifications import Notifications
        self.notifications = Notifications(self.root)
        self.notifications.initialize(self.store.all())
        self.game = game
        self.executor = executor
        self.world = None
        self.readable = False
        self.error = None
        self.sample_interval = sample_interval
        self.next_sample = {}
        self.instance = uuid.uuid4().hex
        self.sample_seq = 0
        for ticket in self.store.all():
            if ticket.get('executionMode')=='main' and ticket['status']=='running':
                self.store.change(ticket,status='uncertain',workerStatus='uncertain',
                                  detail='Scheduler restarted during main execution; never replay')
            elif ticket.get("schemaVersion") == 2 and ticket["status"] == "starting":
                self.store.change(ticket, status="uncertain", workerStatus="uncertain",
                                  detail="Scheduler restarted during dispatch; never replay")
            elif ticket["status"] == "monitoring" and ticket.get("stableSamples"):
                self.store.change(ticket, stableSamples=0, evidence=[])

    def read_state(self):
        try:
            state = self.game.state()
            if "worldKey" not in state:
                raise ValueError("Game response lacks world identity")
            self.world = state["worldKey"]
            self.readable = True
            self.error = None
            return state
        except (OSError, ValueError, TimeoutError) as error:
            self.readable = False
            self.error = str(error)
            return None

    def publish(self):
        reason = "game_state_unavailable" if not self.readable else "no_mission" if not self.world else None
        atomic(self.root / "view.json", {
            "at": self.now(), "worldKey": self.world, "available": self.readable and bool(self.world),
            "blockedReason": reason, "lastWorldError": self.error,
            "runtime": "python-tr1", "tickets": self.store.snapshot(self.world),
        })

    def consume(self):
        for file in sorted(self.commands.glob("*.json")):
            if len(file.stem) != 32 or any(ch not in "0123456789abcdef" for ch in file.stem):
                continue
            raw = file.read_bytes()
            fingerprint = hashlib.sha256(raw).hexdigest()
            response_file = self.responses / file.name
            if response_file.exists():
                # C# compares the persisted fingerprint and reports conflicts to the caller.
                file.unlink()
                continue
            try:
                command = json.loads(raw)
                if not self.readable:
                    continue
                if not self.world or command.get("worldKey") != self.world:
                    raise ValueError("Ticket command belongs to a changed game world")
                from ticket_main import enabled
                main_only = command.get('op')=='create' and enabled(self.root.parents[2]/'setting.ini')
                context = None
                if command.get("op") == "create" and not main_only:
                    if command.get("executor") is not None:
                        context = self.executor.select_context(self.world, command["executor"])
                    else:
                        context = self.executor.context(self.world)
                result = self.store.apply(command, self.world, context, main_only=main_only)
            except (ValueError, TypeError, KeyError) as error:
                result = {"ok": False, "error": str(error)}
            atomic(response_file, {**result, "requestFingerprint": fingerprint})
            file.unlink()

    def reconcile_workers(self):
        for ticket in self.store.all():
            if ticket.get('executionMode') == 'main':
                continue
            if ticket.get("schemaVersion") != 2:
                if ticket.get("workerHandle") and not ticket.get("workerExited"):
                    try:
                        if self.executor.stop(ticket):
                            self.store.change(ticket, cancelPending=False, workerExited=True)
                    except (OSError, ValueError, TimeoutError):
                        pass
                continue
            handle = ticket.get("workerHandle")
            if not handle and ticket["status"] in {"starting", "uncertain", "failed", "cancelled", "timed_out"}:
                try:
                    status = self.executor.status(ticket)
                    if status.get("childId"):
                        ticket = self.store.change(ticket, workerHandle={
                            "ticketId": ticket["id"], "childId": status["childId"]})
                        handle = ticket["workerHandle"]
                except (OSError, ValueError, TimeoutError):
                    continue
                if ticket["status"] == "starting":
                    # A lost start receipt cannot authorize re-executing the command.
                    ticket = self.store.change(ticket, status="uncertain", workerStatus="uncertain",
                                               detail="Recovered worker; injection outcome requires review")
            needs_stop = ticket.get("cancelPending") or (not ticket.get("workerExited") and
                (ticket["status"] in {"uncertain", "failed", "cancelled", "timed_out"}
                 or ticket.get("workerStatus") in {"injected", "executed", "blocked", "needs_input"}))
            if handle and needs_stop:
                try:
                    if self.executor.stop(ticket):
                        self.store.change(ticket, cancelPending=False, workerExited=True)
                except (OSError, ValueError, TimeoutError):
                    pass
            elif handle and not ticket.get("workerExited"):
                try:
                    status = self.executor.status(ticket)
                except (OSError, ValueError, TimeoutError):
                    continue
                if status.get("status") in {"completed", "failed", "cancelled", "unknown"}:
                    if ticket["status"] in {"starting", "running"}:
                        self.store.end(ticket, "uncertain", "Worker ended without a verified injection report")
                    else:
                        self.store.change(ticket, workerExited=True)

    def dispatch(self, state):
        if not state or not self.world:
            return
        # Hiding/cancelling a ticket or accepting its report does not stop the native
        # worker. Keep the slot until the adapter confirms that worker has exited.
        if any((t["status"] in {"starting", "running", "uncertain"} and not t["hidden"])
               or (t.get("workerHandle") and not t.get("workerExited"))
               or t.get("cancelPending")
               for t in self.store.all()):
            return
        for ticket in self.store.all():
            if ticket.get("schemaVersion") != 2 or ticket["status"] != "queued" or ticket["worldKey"] != self.world:
                continue
            if self.now() < ticket.get("nextDispatchAt", 0):
                continue
            context = (self.executor.resolve_context(self.world, ticket) if hasattr(self.executor, "resolve_context")
                       else ticket.get("executorContext") or self.executor.context(self.world))
            if context is None:
                continue
            try:
                before = self.game.state()
                unit = self.game.unit(ticket["command"]["unit_id"]) if ticket["command"]["kind"] == "set_speed" else {}
                after = self.game.state()
            except (OSError, ValueError, TimeoutError):
                return
            if before.get("worldKey") != ticket["worldKey"] or after.get("worldKey") != ticket["worldKey"]:
                return
            if after.get("hold") is not False and ticket["command"]["kind"] == "set_speed":
                self.store.end(ticket, "failed", "Player HOLD prevents injection")
                return
            try:
                if ticket["command"]["kind"] == "set_speed":
                    check_target(unit, ticket["command"])
            except ValueError as error:
                self.store.end(ticket, "failed", str(error))
                return
            ticket = self.store.change(ticket, status="starting", workerStatus="running",
                                       executorContext=context, initialObservation=unit,
                                       detail="Starting execution worker")
            try:
                handle = self.executor.start(ticket)
            except ExecutorUnavailable as error:
                self.store.change(ticket, status="queued", workerStatus="pending",
                                  detail=str(error), nextDispatchAt=self.now() + 3000)
                return
            except (OSError, ValueError, TimeoutError) as error:
                self.store.end(ticket, "uncertain", "Worker start outcome unknown: " + str(error)[:500])
                return
            # Never rewrite starting after a crash. A restarted scheduler reconciles /status.
            self.store.change(ticket, status="running", workerHandle=handle, detail="Worker injecting command")
            return

    def monitor(self):
        if not self.readable or not self.world:
            return
        for ticket in self.store.all():
            if ticket["status"] != "monitoring" or ticket["worldKey"] != self.world:
                continue
            if self.now() < self.next_sample.get(ticket["id"], 0):
                continue
            self.next_sample[ticket["id"]] = self.now() + self.sample_interval
            try:
                before = self.game.state()
                unit = self.game.unit(ticket["predicate"]["unit_id"])
                after = self.game.state()
            except (OSError, ValueError, TimeoutError):
                if ticket.get("stableSamples"):
                    self.store.change(ticket, stableSamples=0, detail="Observation temporarily unavailable")
                continue
            if before.get("worldKey") != ticket["worldKey"] or after.get("worldKey") != ticket["worldKey"]:
                continue
            outcome = evaluate_speed(unit, ticket["predicate"])
            self.sample_seq += 1
            observation = {
                "sampleId": f"{self.instance}:{self.sample_seq}", "at": self.now(),
                "worldKey": self.world, "unit_id": unit.get("id"), "speedKts": unit.get("speedKts"),
                "predicate": ticket["predicate"], "outcome": outcome,
            }
            stable = ticket.get("stableSamples", 0) + 1 if outcome == "satisfied" else 0
            evidence = (ticket.get("evidence", []) + [observation])[-2:] if stable else []
            fields = dict(observation=observation, stableSamples=stable, evidence=evidence)
            if outcome == "impossible":
                fields.update(status="failed", detail="Observed target destroyed")
            elif stable >= 2:
                fields.update(status="completed", resultVerified=True, detail="Observed target speed in two fresh samples")
            else:
                fields["detail"] = "Monitoring game speed: " + outcome
            self.store.change(ticket, **fields)

    def tick(self):
        state = self.read_state()
        self.store.maintain(self.world, self.readable)
        self.publish()
        self.consume()
        self.reconcile_workers()
        self.monitor()
        self.dispatch(state)
        if self.readable:
            self.notifications.emit(self.store, self.world)
        self.publish()


def main():
    # Composition root only; scheduler/store/predicates consume an opaque adapter.
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", required=True, help="MOD_ROOT/comms")
    parser.add_argument("--bridge-port", type=int, default=19080)
    parser.add_argument("--plugin-port", type=int, default=19389)
    parser.add_argument("--executor", choices=("dsh", "codex"), default="dsh")
    args = parser.parse_args()
    comms = Path(args.out).resolve()
    root = comms / "run" / "tickets"
    root.mkdir(parents=True, exist_ok=True)
    mcp_server.PORT = args.bridge_port
    token = root / "auth.token"
    if not token.exists():
        with token.open("x", encoding="utf-8") as stream:
            stream.write(secrets.token_hex(32))
    stopped = False

    def stop(*unused):
        nonlocal stopped
        stopped = True

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    with RuntimeLock(root / "runtime.lock"):
        from ticket_executor_codex import CodexExecutor
        from ticket_executor_dsh import DshExecutor
        from ticket_executor import ExecutorRouter
        executor = ExecutorRouter({'dsh': DshExecutor(comms, args.plugin_port),
                                   'codex': CodexExecutor(comms, args.bridge_port)}, args.executor)
        scheduler = Scheduler(comms, Game(), executor)
        while not stopped:
            scheduler.tick()
            time.sleep(0.5)


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, RuntimeError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
