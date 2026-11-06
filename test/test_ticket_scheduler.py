"""TR1 regression tests. No game commands, models or production data are used."""
import copy
import json
from pathlib import Path
import sys
import tempfile
import unittest
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from ticket_scheduler import Scheduler, RuntimeLock, process_alive
from ticket_store import TicketStore
from ticket_predicates import evaluate_speed
from ticket_executor_dsh import DshExecutor, ExecutorUnavailable


class Game:
    def __init__(self):
        self.world = "process:1"
        self.speed = 0
        self.available = True
        self.hold = False

    def state(self):
        if not self.available:
            raise OSError("Disconnected")
        return {"worldKey": self.world, "hold": self.hold}

    def unit(self, unit_id):
        if not self.available:
            raise OSError("Disconnected")
        return {"id": unit_id, "mine": True, "surface": True, "sub": False,
                "air": False, "isLand": False, "destroyed": False,
                "maxForwardKnots": 30, "speedKts": self.speed}


class Executor:
    def __init__(self):
        self.starts = []
        self.stops = []
        self.states = {}
        self.ready = True
        self.scheduler = None

    def context(self, world):
        return {"harness": "test", "parent": "parent"} if self.ready else None

    def start(self, ticket):
        persisted = self.scheduler.store.get(ticket["id"])
        assert persisted["status"] == "starting"
        self.starts.append(ticket["id"])
        self.states[ticket["id"]] = "running"
        return {"ticketId": ticket["id"], "childId": "child-" + ticket["id"]}

    def status(self, ticket):
        if ticket["id"] not in self.states:
            raise ValueError("Unknown worker")
        return {"status": self.states[ticket["id"]], "childId": "child-" + ticket["id"]}

    def stop(self, ticket):
        self.stops.append(ticket["id"])
        self.states[ticket["id"]] = "cancelled"
        return True


class SchedulerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.time = 1000
        self.game = Game()
        self.executor = Executor()
        self.scheduler = Scheduler(self.temp.name, self.game, self.executor, lambda: self.time, 1000)
        self.executor.scheduler = self.scheduler

    def command(self, **changes):
        return {
            "op": "create", "requestId": uuid.uuid4().hex, "worldKey": self.game.world,
            "title": "speed", "task": "Player authorized only this speed change",
            "completion": "Observe actual speed", "timeoutSeconds": 30,
            "command": {"kind": "set_speed", "unit_id": 12, "knots": 5},
            "predicate": {"kind": "speed_reached", "unit_id": 12, "target_knots": 5},
            **changes,
        }

    def send(self, command):
        file = self.scheduler.commands / (command["requestId"] + ".json")
        file.write_text(json.dumps(command, ensure_ascii=False), encoding="utf-8")
        self.scheduler.tick()
        return json.loads((self.scheduler.responses / file.name).read_text(encoding="utf-8"))

    def report(self, ticket_id, status, receipt=None):
        ticket = self.scheduler.store.get(ticket_id)
        command = self.command(op="report", id=ticket_id, status=status,
                               reportToken=ticket["reportToken"], detail="worker report", receipt=receipt)
        return self.send(command)

    def receipt(self):
        return {"ok": True, "cmd": 42, "data": {"id": 12, "action": "speed", "knots": 5}}

    def test_injection_does_not_complete_and_worker_exits_before_monitoring(self):
        result = self.send(self.command())
        ticket_id = result["id"]
        self.assertEqual(result["status"], "queued")
        self.assertEqual(self.executor.starts, [ticket_id])
        self.report(ticket_id, "injected", self.receipt())
        self.assertEqual(self.scheduler.store.get(ticket_id)["status"], "monitoring")
        self.assertIn(ticket_id, self.executor.stops)
        self.game.speed = 5
        self.time += 1000
        self.scheduler.tick()
        self.assertEqual(self.scheduler.store.get(ticket_id)["status"], "monitoring")
        self.time += 1000
        self.scheduler.tick()
        ticket = self.scheduler.store.get(ticket_id)
        self.assertEqual(ticket["status"], "completed")
        self.assertTrue(ticket["resultVerified"])
        self.assertEqual(len(ticket["evidence"]), 2)
        self.assertNotEqual(ticket["evidence"][0]["sampleId"], ticket["evidence"][1]["sampleId"])

    def test_idempotency_and_conflicting_input(self):
        command = self.command()
        first = self.send(command)
        self.assertEqual(self.send(command)["id"], first["id"])
        self.assertEqual(len(self.executor.starts), 1)
        changed = {**command, "title": "different"}
        # Transport cache returns the original fingerprint for C# to detect the conflict.
        response = self.send(changed)
        import hashlib
        self.assertNotEqual(response["requestFingerprint"],
                            hashlib.sha256(json.dumps(changed).encode()).hexdigest())
        with self.assertRaises(ValueError):
            self.scheduler.store.apply(changed, self.game.world, self.executor.context(self.game.world))

    def test_worker_cannot_claim_game_completion(self):
        ticket_id = self.send(self.command())["id"]
        self.assertFalse(self.report(ticket_id, "completed")["ok"])
        self.assertEqual(self.scheduler.store.get(ticket_id)["status"], "running")
        bad_receipt = {**self.receipt(), "uncertain": True}
        self.assertFalse(self.report(ticket_id, "injected", bad_receipt)["ok"])
        self.report(ticket_id, "completed", self.receipt())
        self.assertEqual(self.scheduler.store.get(ticket_id)["status"], "monitoring")

    def test_uncertain_write_is_not_replayed(self):
        ticket_id = self.send(self.command())["id"]
        self.report(ticket_id, "uncertain", {"ok": False, "uncertain": True, "resend": "forbidden"})
        for _ in range(4):
            self.scheduler.tick()
        self.assertEqual(self.scheduler.store.get(ticket_id)["status"], "uncertain")
        self.assertEqual(len(self.executor.starts), 1)
        self.assertIn(ticket_id, self.executor.stops)

    def test_timeout_cancel_and_world_change(self):
        ticket_id = self.send(self.command())["id"]
        self.time += 31000
        self.scheduler.tick()
        self.assertEqual(self.scheduler.store.get(ticket_id)["status"], "timed_out")
        self.assertIn(ticket_id, self.executor.stops)
        second = self.send(self.command())["id"]
        self.send(self.command(op="destroy", id=second))
        self.assertTrue(self.scheduler.store.get(second)["hidden"])
        third = self.send(self.command())["id"]
        self.game.world = "process:2"
        self.scheduler.tick()
        self.assertEqual(self.scheduler.store.get(third)["status"], "cancelled")
        self.assertEqual(self.scheduler.store.snapshot(self.game.world), [])

    def test_transient_disconnect_preserves_monitoring(self):
        ticket_id = self.send(self.command())["id"]
        self.report(ticket_id, "injected", self.receipt())
        self.game.available = False
        self.scheduler.tick()
        self.assertEqual(self.scheduler.store.get(ticket_id)["status"], "monitoring")
        self.game.available = True
        self.game.speed = None
        self.scheduler.tick()
        self.assertFalse(self.scheduler.store.get(ticket_id)["resultVerified"])

    def test_legacy_and_crash_recovery_do_not_replay(self):
        command = self.command()
        ticket_id = self.send(command)["id"]
        ticket = self.scheduler.store.get(ticket_id)
        self.scheduler.store.change(ticket, status="starting", workerHandle=None)
        restored = Scheduler(self.temp.name, self.game, self.executor, lambda: self.time)
        self.executor.scheduler = restored
        restored.tick()
        self.assertEqual(restored.store.get(ticket_id)["status"], "uncertain")
        self.assertEqual(len(self.executor.starts), 1)

    def test_invalid_or_missing_predicate_rejected(self):
        self.assertFalse(self.send(self.command(predicate=None))["ok"])
        self.assertFalse(self.send(self.command(command={"kind": "attack"}))["ok"])
        self.assertFalse(self.executor.starts)

    def test_hold_does_not_inject(self):
        self.game.hold = True
        ticket_id = self.send(self.command())["id"]
        self.assertEqual(self.scheduler.store.get(ticket_id)["status"], "failed")
        self.assertFalse(self.executor.starts)

    def test_adapter_health_never_starts_incompatible_worker(self):
        adapter = DshExecutor(self.temp.name)
        calls = []
        def old_service(route, body=None):
            calls.append(route)
            return {"ticketProtocol": 1}
        adapter.call = old_service
        with self.assertRaises(ExecutorUnavailable):
            adapter.start({})
        self.assertEqual(calls, ["/health"])

    def test_legacy_records_remain_unverified_and_are_never_dispatched(self):
        old = {"id": "old", "worldKey": self.game.world, "status": "completed",
               "revision": 1, "hidden": False, "deadlineAt": 99999,
               "detail": "old worker said done"}
        file = Path(self.temp.name) / "old.jsonl"
        file.write_text(json.dumps({"version": 1, "ticket": old}) + "\n", encoding="utf-8")
        store = TicketStore(file)
        self.assertTrue(store.get("old")["legacy"])
        self.assertFalse(store.snapshot(self.game.world)[0]["resultVerified"])

    def test_corrupt_journal_and_existing_runtime_are_not_overwritten(self):
        import os
        file = Path(self.temp.name) / "broken.jsonl"
        file.write_bytes(b'{"partial":')
        with self.assertRaises(ValueError):
            TicketStore(file)
        lock_file = Path(self.temp.name) / "runtime.lock"
        with RuntimeLock(lock_file):
            self.assertTrue(process_alive(os.getpid()))
            with self.assertRaises(RuntimeError):
                with RuntimeLock(lock_file):
                    self.fail("Second runtime was allowed")

    def test_destroy_retries_native_stop_and_rejects_late_report(self):
        ticket_id = self.send(self.command())["id"]
        original_stop = self.executor.stop
        attempts = []
        def transient_stop(ticket):
            attempts.append(ticket["id"])
            if len(attempts) == 1:
                raise OSError("Temporary adapter disconnect")
            return original_stop(ticket)
        self.executor.stop = transient_stop
        self.send(self.command(op="destroy", id=ticket_id))
        ticket = self.scheduler.store.get(ticket_id)
        self.assertTrue(ticket["hidden"])
        self.assertTrue(ticket["cancelPending"])
        self.assertEqual(ticket["status"], "cancelled")
        self.scheduler.tick()
        self.assertTrue(self.scheduler.store.get(ticket_id)["workerExited"])
        self.assertFalse(self.scheduler.store.get(ticket_id)["cancelPending"])
        self.assertFalse(self.report(ticket_id, "injected", self.receipt())["ok"])
        self.assertEqual(len(self.executor.starts), 1)

    def test_monitoring_does_not_block_commands_or_on_demand_snapshot(self):
        first = self.send(self.command())["id"]
        self.report(first, "injected", self.receipt())
        baseline = self.scheduler.store.file.read_bytes()
        snapshot = self.scheduler.store.snapshot(self.game.world)
        self.assertEqual(snapshot[0]["status"], "monitoring")
        self.assertEqual(self.scheduler.store.file.read_bytes(), baseline)
        second = self.send(self.command(
            command={"kind": "set_speed", "unit_id": 13, "knots": 5},
            predicate={"kind": "speed_reached", "unit_id": 13, "target_knots": 5}))
        self.assertTrue(second["ok"])
        self.assertEqual(self.executor.starts, [first, second["id"]])
        self.assertEqual(self.scheduler.store.get(first)["status"], "monitoring")

    def test_wall_clock_timeout_while_observation_frozen_and_world_rejects_old_report(self):
        ticket_id = self.send(self.command())["id"]
        self.report(ticket_id, "injected", self.receipt())
        self.time += 31000  # game speed stays frozen at zero
        self.scheduler.tick()
        ticket = self.scheduler.store.get(ticket_id)
        self.assertEqual(ticket["status"], "timed_out")
        self.assertFalse(ticket["resultVerified"])
        second = self.send(self.command())["id"]
        old = self.scheduler.store.get(second)
        self.game.world = "process:next"
        self.scheduler.tick()
        late = self.command(op="report", id=second, status="injected", receipt=self.receipt(),
                            reportToken=old["reportToken"], detail="late reply")
        self.assertFalse(self.send(late)["ok"])
        self.assertEqual(self.scheduler.store.get(second)["status"], "cancelled")

    def second_command(self):
        return self.command(command={"kind":"set_speed","unit_id":13,"knots":8},
                            predicate={"kind":"speed_reached","unit_id":13,"target_knots":8})

    def test_two_workers_are_serial_and_two_predicates_are_independent(self):
        first=self.send(self.command())["id"]
        second=self.send(self.second_command())["id"]
        self.assertEqual(self.executor.starts,[first])
        self.assertEqual(self.scheduler.store.get(second)["status"],"queued")
        self.report(first,"injected",self.receipt())
        self.assertEqual(self.executor.starts,[first,second])
        second_receipt={"ok":True,"cmd":43,"data":{"id":13,"action":"speed","knots":8}}
        self.report(second,"injected",second_receipt)
        original=self.game.unit
        def units(unit_id):
            unit=original(unit_id)
            unit['speedKts']=5 if unit_id==12 else 0
            return unit
        self.game.unit=units
        for _ in range(2):
            self.time+=1000
            self.scheduler.tick()
        self.assertEqual(self.scheduler.store.get(first)['status'],'completed')
        self.assertEqual(self.scheduler.store.get(second)['status'],'monitoring')

    def test_cancelled_worker_holds_slot_until_native_stop_confirmed(self):
        first=self.send(self.command())["id"]
        second=self.send(self.second_command())["id"]
        original=self.executor.stop
        def offline(ticket):
            raise OSError('Cannot stop native worker yet')
        self.executor.stop=offline
        self.send(self.command(op='destroy',id=first))
        self.assertEqual(self.executor.starts,[first])
        self.assertEqual(self.scheduler.store.get(second)['status'],'queued')
        self.executor.stop=original
        self.scheduler.tick()
        self.assertEqual(self.executor.starts,[first,second])

    def test_injected_worker_holds_slot_until_native_exit_confirmed(self):
        first=self.send(self.command())["id"]
        second=self.send(self.second_command())["id"]
        original=self.executor.stop
        self.executor.stop=lambda ticket: False
        self.report(first,'injected',self.receipt())
        self.assertEqual(self.executor.starts,[first])
        self.executor.stop=original
        self.scheduler.tick()
        self.assertEqual(self.executor.starts,[first,second])

    def test_same_unit_conflicts_are_refused_without_overwriting(self):
        first=self.send(self.command())["id"]
        self.assertFalse(self.send(self.command())["ok"])
        self.report(first,'injected',self.receipt())
        self.assertFalse(self.send(self.command())["ok"])
        self.assertEqual(self.scheduler.store.get(first)['status'],'monitoring')
        self.assertEqual(len(self.executor.starts),1)

    def test_legacy_worker_is_reconciled_before_releasing_slot(self):
        old={'id':'old','worldKey':self.game.world,'status':'completed','revision':1,
             'hidden':True,'deadlineAt':99999,'workerSessionId':'old-child'}
        self.scheduler.store.file.write_text(json.dumps({'version':1,'ticket':old})+'\n',encoding='utf-8')
        self.executor.states['old']='completed'
        self.scheduler=Scheduler(self.temp.name,self.game,self.executor,lambda:self.time)
        self.executor.scheduler=self.scheduler
        new=self.send(self.command())['id']
        self.assertIn('old',self.executor.stops)
        self.assertTrue(self.scheduler.store.get('old')['workerExited'])
        self.assertEqual(self.executor.starts,[new])

    def test_uncertain_holds_slot_even_after_worker_exits(self):
        first=self.send(self.command())['id']
        self.report(first,'uncertain')
        second=self.send(self.second_command())['id']
        self.assertTrue(self.scheduler.store.get(first)['workerExited'])
        self.assertEqual(self.executor.starts,[first])
        self.send(self.command(op='destroy',id=first))
        self.assertEqual(self.executor.starts,[first,second])


if __name__ == "__main__":
    unittest.main()
