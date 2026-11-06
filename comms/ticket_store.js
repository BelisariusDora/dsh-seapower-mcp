"use strict";

const fs = require("node:fs");
const path = require("node:path");
const crypto = require("node:crypto");

const ACTIVE = new Set(["queued", "starting", "running", "needs_review"]);
const REPORT_STATUS = new Set(["running", "completed", "failed"]);

function copy(value) {
  return JSON.parse(JSON.stringify(value));
}

function text(value, name, max, optional = false) {
  if (optional && (value === undefined || value === "")) return "";
  if (typeof value !== "string" || !value.trim() || value.length > max) {
    throw new Error(name + " must be a nonempty string of at most " + max + " characters");
  }
  return value.trim();
}

class TicketStore {
  constructor({ file, now = Date.now }) {
    this.file = path.resolve(file);
    this.now = now;
    this.tickets = new Map();
    this.requests = new Map();
    this.poisoned = false;
    fs.mkdirSync(path.dirname(this.file), { recursive: true });
    this.load();
    for (const ticket of this.all()) {
      if (ticket.status === "starting") {
        this.change(ticket, { status: "needs_review", detail: "Worker launch was interrupted. Inspect the DSH child before retrying." });
      }
    }
  }

  load() {
    let journal;
    try {
      journal = fs.readFileSync(this.file, "utf8");
    } catch (error) {
      if (error.code === "ENOENT") return;
      throw error;
    }
    if (journal && !journal.endsWith("\n")) throw new Error("Incomplete ticket journal. Inspect before starting: " + this.file);
    for (const line of journal.split("\n")) {
      if (!line) continue;
      const record = JSON.parse(line);
      if (record.version !== 1 || !record.ticket || typeof record.ticket.id !== "string") {
        throw new Error("Invalid ticket journal record");
      }
      this.tickets.set(record.ticket.id, record.ticket);
      if (record.requestId) {
        this.requests.set(record.requestId, { fingerprint: record.fingerprint, result: record.result });
      }
    }
  }

  commit(ticket, request = null) {
    if (this.poisoned) throw new Error("Ticket persistence failed. Restart after inspecting the journal.");
    const record = { version: 1, ticket };
    if (request) Object.assign(record, request);
    let descriptor;
    try {
      descriptor = fs.openSync(this.file, "a");
      fs.writeFileSync(descriptor, JSON.stringify(record) + "\n", "utf8");
      fs.fsyncSync(descriptor);
    } catch (error) {
      this.poisoned = true;
      throw error;
    } finally {
      if (descriptor !== undefined) fs.closeSync(descriptor);
    }
    this.tickets.set(ticket.id, copy(ticket));
    if (request) this.requests.set(request.requestId, copy(request));
    return copy(ticket);
  }

  change(ticket, fields) {
    return this.commit({ ...ticket, ...fields, revision: ticket.revision + 1, updatedAt: this.now() });
  }

  get(id) {
    const ticket = this.tickets.get(id);
    return ticket ? copy(ticket) : null;
  }

  all() {
    return Array.from(this.tickets.values(), copy);
  }

  pending(worldKey) {
    return this.all().filter(ticket => ticket.worldKey === worldKey && ticket.status === "queued" && !ticket.hidden);
  }

  snapshot(worldKey) {
    return this.all().filter(ticket => ticket.worldKey === worldKey && !ticket.hidden).map(ticket => ({
      id: ticket.id, worldKey: ticket.worldKey, title: ticket.title, status: ticket.status,
      detail: ticket.detail, revision: ticket.revision, createdAt: ticket.createdAt,
      updatedAt: ticket.updatedAt, deadlineAt: ticket.deadlineAt, canDestroy: true,
    }));
  }

  apply(command, { worldKey, ownerSessionId }) {
    if (!command || typeof command !== "object" || Array.isArray(command)) throw new Error("Invalid ticket command");
    const requestId = text(command.requestId, "requestId", 128);
    const world = text(worldKey, "worldKey", 256);
    if (command.worldKey !== world) throw new Error("Ticket command belongs to another world");
    const fingerprint = crypto.createHash("sha256").update(JSON.stringify(command)).digest("hex");
    const previous = this.requests.get(requestId);
    if (previous) {
      if (previous.fingerprint !== fingerprint) throw new Error("requestId was already used by a different command");
      return { ...copy(previous.result), duplicate: true };
    }

    let ticket;
    if (command.op === "create") {
      ticket = this.create(command, world, ownerSessionId);
    } else {
      ticket = this.get(text(command.id, "id", 128));
      if (!ticket || ticket.worldKey !== world) throw new Error("Ticket does not exist in this world");
      if (command.op === "destroy") {
        ticket = {
          ...ticket, hidden: true,
          status: ACTIVE.has(ticket.status) ? "cancelled" : ticket.status,
          cancelPending: ticket.cancelPending || (ACTIVE.has(ticket.status) && !!ticket.workerSessionId),
          detail: "Destroyed by player",
        };
      } else if (command.op === "bind") {
        ticket = this.workerBinding(ticket, text(command.childSessionId, "childSessionId", 256));
      } else if (command.op === "report") {
        ticket = this.report(ticket, command);
      } else {
        throw new Error("Unknown ticket operation");
      }
      ticket.revision++;
      ticket.updatedAt = this.now();
    }
    const result = { ok: true, id: ticket.id, status: ticket.status, revision: ticket.revision };
    this.commit(ticket, { requestId, fingerprint, result });
    return result;
  }

  create(command, worldKey, ownerSessionId) {
    const title = text(command.title, "title", 160);
    const task = text(command.task, "task", 12000);
    const completion = text(command.completion, "completion", 4000);
    const owner = text(ownerSessionId, "ownerSessionId", 256);
    const seconds = command.timeoutSeconds === undefined ? 900 : command.timeoutSeconds;
    if (!Number.isInteger(seconds) || seconds < 1 || seconds > 3600) throw new Error("timeoutSeconds must be an integer from 1 to 3600");
    const current = this.all().filter(ticket => ticket.worldKey === worldKey);
    if (current.filter(ticket => ACTIVE.has(ticket.status)).length >= 64) throw new Error("Active ticket limit reached");
    if (current.filter(ticket => !ticket.hidden).length >= 256) throw new Error("Visible ticket limit reached. Destroy completed tickets first.");
    const now = this.now();
    return {
      id: "ticket-" + crypto.randomUUID(), worldKey, ownerSessionId: owner,
      title, task, completion, status: "queued", detail: "Waiting for worker",
      workerSessionId: null, reportToken: crypto.randomBytes(24).toString("hex"),
      hidden: false, cancelPending: false, revision: 1,
      createdAt: now, updatedAt: now, deadlineAt: now + seconds * 1000,
    };
  }

  report(ticket, command) {
    if (ticket.hidden || !ACTIVE.has(ticket.status)) throw new Error("Ticket is no longer accepting worker reports");
    if (typeof command.reportToken !== "string" || command.reportToken !== ticket.reportToken) throw new Error("Invalid worker report token");
    if (ticket.workerSessionId && command.workerSessionId !== undefined && command.workerSessionId !== ticket.workerSessionId) throw new Error("Report worker does not match ticket");
    if (!REPORT_STATUS.has(command.status)) throw new Error("Invalid worker report status");
    const detail = text(command.detail, "detail", 4000, true);
    return { ...ticket, status: command.status, detail,
      cancelPending: ticket.cancelPending || (command.status !== "running" && !!ticket.workerSessionId) };
  }

  markStart(id) {
    const ticket = this.require(id);
    if (ticket.hidden || ticket.status !== "queued") throw new Error("Only a queued ticket can start");
    return this.change(ticket, { status: "starting", detail: "Starting DSH worker" });
  }

  assignWorker(id, workerSessionId) {
    const ticket = this.require(id);
    const worker = text(workerSessionId, "workerSessionId", 256);
    const bound = this.workerBinding(ticket, worker);
    if (bound === ticket) return ticket;
    return this.change(bound, {});
  }

  workerBinding(ticket, worker) {
    if (ticket.workerSessionId && ticket.workerSessionId !== worker) throw new Error("Ticket already belongs to another worker");
    if (ticket.workerSessionId === worker) return ticket;
    if (!ACTIVE.has(ticket.status) || ticket.hidden) {
      return { ...ticket, workerSessionId: worker, cancelPending: true };
    }
    return { ...ticket, workerSessionId: worker, status: "running", detail: "DSH worker running" };
  }

  fail(id, detail) {
    const ticket = this.require(id);
    if (!ACTIVE.has(ticket.status) || ticket.hidden) return ticket;
    return this.change(ticket, { status: "failed", detail: text(detail, "detail", 4000), cancelPending: !!ticket.workerSessionId });
  }

  markCancelled(id) {
    const ticket = this.require(id);
    if (!ticket.cancelPending) return ticket;
    return this.change(ticket, { cancelPending: false });
  }

  expireWorld(worldKey) {
    for (const ticket of this.all()) {
      if (ticket.worldKey === worldKey || ticket.hidden) continue;
      this.change(ticket, {
        hidden: true, status: ACTIVE.has(ticket.status) ? "cancelled" : ticket.status,
        detail: "Game world changed",
        cancelPending: ticket.cancelPending || (ACTIVE.has(ticket.status) && !!ticket.workerSessionId),
      });
    }
  }

  overdue() {
    const now = this.now();
    for (const ticket of this.all()) {
      if (ticket.hidden || !ACTIVE.has(ticket.status) || ticket.deadlineAt > now) continue;
      this.change(ticket, { status: "timed_out", detail: "Worker deadline elapsed", cancelPending: !!ticket.workerSessionId });
    }
  }

  require(id) {
    const ticket = this.get(id);
    if (!ticket) throw new Error("Unknown ticket");
    return ticket;
  }
}

module.exports = { TicketStore };
