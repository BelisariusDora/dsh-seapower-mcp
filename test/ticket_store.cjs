"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { TicketStore } = require("../comms/ticket_store.js");
const root = fs.mkdtempSync(path.join(require("node:os").tmpdir(),"ticket-store-test-"));
const file = path.join(root, "tickets.jsonl");
let now = 1000;
let store = new TicketStore({ file, now: () => now });
const context = { worldKey: "world:1", ownerSessionId: "parent" };
const create = (key, options = {}) => store.apply({ op: "create", worldKey: "world:1", requestId: key,
  title: "Observe", task: "Check state", completion: "Evidence confirmed", ...options }, context);
const first = create("create-one");
const token = store.get(first.id).reportToken;
assert.equal(create("create-one").duplicate, true);
assert.equal(store.all().length, 1);
assert.equal("reportToken" in store.snapshot("world:1")[0], false);
assert.throws(() => store.apply({ op: "report", requestId: "bad-token", worldKey: "world:1",
  id: first.id, reportToken: "bad", status: "completed" }, context), /token/);
store.apply({ op: "report", requestId: "report-before-bind", worldKey: "world:1", id: first.id,
  reportToken: token, status: "completed", detail: "Done" }, context);
store.apply({ op: "bind", requestId: "bind-after-report", worldKey: "world:1", id: first.id,
  childSessionId: "child-one" }, context);
assert.equal(store.get(first.id).status, "completed");
assert.equal(store.get(first.id).cancelPending, true);
store.markCancelled(first.id);
assert.equal(store.get(first.id).cancelPending, false);
const second = create("create-two");
store.apply({ op: "destroy", requestId: "destroy-before-bind", worldKey: "world:1", id: second.id }, context);
store.apply({ op: "bind", requestId: "bind-after-destroy", worldKey: "world:1", id: second.id,
  childSessionId: "child-two" }, context);
assert.equal(store.get(second.id).hidden, true);
assert.equal(store.get(second.id).status, "cancelled");
assert.equal(store.get(second.id).cancelPending, true);
assert.throws(() => store.apply({ op: "report", requestId: "late-report", worldKey: "world:1", id: second.id,
  reportToken: store.get(second.id).reportToken, status: "running" }, context), /no longer/);
const third = create("create-timeout", { timeoutSeconds: 2 });
store.assignWorker(third.id, "child-three");
now = 3000;
store.overdue();
assert.equal(store.get(third.id).status, "timed_out");
assert.equal(store.get(third.id).cancelPending, true);
const fourth = create("create-crash");
store.markStart(fourth.id);
store = new TicketStore({ file, now: () => now });
assert.equal(store.get(fourth.id).status, "needs_review");
assert.equal(create("create-one").duplicate, true);
store.expireWorld("world:2");
assert.equal(store.snapshot("world:1").length, 0);
assert.equal(store.get(fourth.id).status, "cancelled");
fs.appendFileSync(file, '{"incomplete":');
assert.throws(() => new TicketStore({ file }), /Incomplete ticket journal/);
console.log("TicketStore regression checks passed");
