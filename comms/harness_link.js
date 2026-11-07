#!/usr/bin/env node
// Sea Power Agent Bridge - the harness side of TR2-H (outbound direction).
//
// The game process cannot talk to the harness: every /api route needs a browser session
// cookie signed with a secret that lives in the user's profile, and the game never sees it.
// So the split is:
//
//   in  : dsh_tailer.js already decodes the session log into <run>/live.jsonl
//   out : this script. It takes <comms>/outbox.jsonl (what the player typed in the
//         in-game page) and hands each line to the harness over its own HTTP API.
//
// Nothing here invents a reply, and nothing here guesses which session it is talking to.
//
// SESSION (audit S11). A session is used because it is the one that was pinned: `--new`
// (creates one, on purpose) or run/session.pointer (written when a session was created or
// adopted). A pointer the harness does not recognise is a REFUSAL, not an invitation to
// create another session or to adopt the newest one: the state file records `no-session`
// with the reason and the outbox keeps its queued lines. There is no auto-create and no
// "pick whatever is recent" path in this file.
//
// DELIVERY (audit S12). Three outcomes, and a cursor that is not a success counter.
//   sent    - the harness answered 200 with a parseable envelope and result.ok true. The
//             harness ACCEPTED the prompt; that is not the agent having answered it.
//   unknown - the POST left this process and no usable answer came back (timeout, reset,
//             truncated body). It may have been delivered. It is never retried
//             automatically; `--retry-unknown` is the explicit operator action, default off.
//   failed  - a definite rejection: 4xx/5xx, an error envelope, an unparseable outbox line,
//             or a refusal because there is no session. Nothing was accepted.
//
// A message leaves the pending window only after its outcome is on disk. outbox.jsonl is
// evidence and is never truncated or rewritten; outbox.offset is a consumption cursor (a
// line is behind it because it has a durable result, not because it succeeded); and
// outbox.state.jsonl is the append-only record of what actually happened. When appending
// that record fails we stop automatic delivery instead of advancing a cursor that would
// then claim a message was handled when it was not.
//
// Auth (reverse engineered from the asar, see docs/HARNESS_COMMS.md):
//   cookie name  = "dsh-auth-" + base64url(sha256(authority))
//   cookie value = "v1." + body + "." + base64url(HMAC-SHA256(secret, body))
//   body         = base64url(JSON{version:1, authority, issuedAt, expiresAt})
//   secret       = records["client-connection/browser-session"].payload.secret in
//                  ~/.dsh/.credentials.yaml, base64url of 32 raw bytes
"use strict";

const fs = require("fs");
const os = require("os");
const path = require("path");
const http = require("http");
const crypto = require("crypto");

const DEFAULTS = {
  outDir: "",
  host: "127.0.0.1",
  port: 19387,
  // The bridge owns where the outbox lives: it appends to comms/outbox.jsonl (see
  // OutboxFile in src/CommsBridge.cs). Keeping our copy of the path in the same place is the
  // whole point - a helper that reads a different directory shows "queued" forever while the
  // player's line sits untouched. Inbound files stay under run/ because the bridge reads
  // run/live.jsonl.
  outbox: path.join(__dirname, "outbox.jsonl"),
  offsetFile: path.join(__dirname, "outbox.offset"),
  stateFile: path.join(__dirname, "outbox.state.jsonl"),
  pointerFile: path.join(__dirname, "run", "session.pointer"),
  logFile: path.join(__dirname, "run", "link.log"),
  statusFile: path.join(__dirname, "run", "link.status.json"),
  pollMs: 700,
  // "new" creates a fresh session for the first line; otherwise the pointer's session is the
  // only session this helper will talk to, and a missing/unrecognised one is a refusal.
  mode: "reuse",
  // Refusals in a row before automatic delivery is reported as paused. Paused still polls
  // (polling is how the session coming back gets noticed); it just never POSTs.
  pauseAfter: 3,
  // Status line to stdout/log at most this often when nothing changed.
  statusEveryMs: 15000,
  cwd: process.cwd(),
  maxCookieDays: 30
};

// ---------------------------------------------------------------------------------------------
// logging
// ---------------------------------------------------------------------------------------------

function makeLogger(cfg) {
  return function log(msg) {
    const line = new Date().toISOString() + " " + msg + "\n";
    // Logging must never kill the loop, and a failed log is NOT a failed delivery: the audit
    // keeps "ordinary debug logging may be ignored" separate from "critical delivery state
    // must not fail silently". The critical path is appendState/writeAtomic below.
    try { fs.appendFileSync(cfg.logFile, line); } catch (e) { }
    try { process.stdout.write(line); } catch (e) { }
  };
}

// ---------------------------------------------------------------------------------------------
// credentials
// ---------------------------------------------------------------------------------------------

function b64u(buf) {
  return Buffer.from(buf).toString("base64").replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

function sha256(s) {
  return crypto.createHash("sha256").update(s, "utf8").digest();
}

function hmac(secretRaw, body) {
  return crypto.createHmac("sha256", secretRaw).update(body, "ascii").digest();
}

function dshHome() {
  if (process.env.DSH_HOME) return process.env.DSH_HOME;
  return path.join(os.homedir(), ".dsh");
}

// A deliberately dumb scan: the file is a small generated YAML with a fixed shape, and
// pulling a YAML dependency into a game mod folder is not worth it.
function readSecret(home) {
  const file = path.join(home, ".credentials.yaml");
  let text;
  try {
    text = fs.readFileSync(file, "utf8");
  } catch (e) {
    return { ok: false, error: "cannot read " + file + ": " + e.message };
  }
  const lines = text.split(/\r?\n/);
  let inRecord = false;
  let secret = null;
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (/^\s+client-connection\/browser-session:\s*$/.test(line)) { inRecord = true; continue; }
    if (inRecord) {
      // Leaving the record: a line that is less indented than the record key.
      if (/^\S/.test(line)) break;
      const m = /^\s+secret:\s*(\S+)\s*$/.exec(line);
      if (m) { secret = m[1]; break; }
    }
  }
  if (!secret) return { ok: false, error: "no client-connection/browser-session secret in " + file };
  const raw = Buffer.from(secret.replace(/-/g, "+").replace(/_/g, "/"), "base64");
  if (raw.length < 16) return { ok: false, error: "secret decodes to " + raw.length + " bytes, expected 32" };
  return { ok: true, secret: secret, raw: raw, file: file };
}

function cookieFor(authority, secret) {
  const now = Date.now();
  const payload = {
    version: 1,
    authority: authority,
    issuedAt: now,
    expiresAt: now + DEFAULTS.maxCookieDays * 86400000
  };
  const body = b64u(Buffer.from(JSON.stringify(payload), "utf8"));
  const sig = b64u(hmac(secret.raw, body));
  return "dsh-auth-" + b64u(sha256(authority)) + "=v1." + body + "." + sig;
}

// ---------------------------------------------------------------------------------------------
// IO: one small interface so the offline self-test can drive the real engine over a fake
// volume. Critical writes are length-checked and published by rename; nothing swallows an
// exception that the caller needs to see.
// ---------------------------------------------------------------------------------------------

const realIo = {
  mkdirp(dir) { fs.mkdirSync(dir, { recursive: true }); },
  exists(file) {
    try { fs.statSync(file); return true; } catch (e) { return false; }
  },
  size(file) { return fs.statSync(file).size; },
  readText(file) { return fs.readFileSync(file, "utf8"); },
  // The outbox cursor is a BYTE position (the bridge appends bytes, and outbox.offset has
  // always been a byte count), so the reader for it must not go through a string: a Chinese
  // player message would make a character index disagree with the byte cursor.
  readBytes(file) { return fs.readFileSync(file); },
  append(file, text) {
    let before = 0;
    try { before = fs.statSync(file).size; } catch (e) { before = 0; }
    const fd = fs.openSync(file, "a");
    try {
      fs.writeSync(fd, text);
      // A record the caller is told is durable has to survive power loss too, not just this
      // process: appendFileSync only hands the bytes to the page cache.
      fs.fsyncSync(fd);
    } finally {
      fs.closeSync(fd);
    }
    // A full disk can report success. Check the bytes actually landed before the caller is
    // told the record is durable - this is the difference between "recorded" and "maybe".
    const after = fs.statSync(file).size;
    const want = Buffer.byteLength(text, "utf8");
    if (after - before !== want) {
      throw new Error("short append to " + file + ": wrote " + (after - before) + " of " + want + " bytes");
    }
  },
  writeAtomic(file, text) {
    const tmp = file + ".tmp-" + process.pid;
    const fd = fs.openSync(tmp, "w");
    try {
      fs.writeSync(fd, text);
      fs.fsyncSync(fd);
    } finally {
      fs.closeSync(fd);
    }
    const back = fs.readFileSync(tmp, "utf8");
    if (back !== text) throw new Error("short write to " + tmp);
    fs.renameSync(tmp, file);
    // The rename itself is only durable once the directory entry is flushed as well.
    try {
      const dfd = fs.openSync(path.dirname(file), "r");
      try { fs.fsyncSync(dfd); } finally { fs.closeSync(dfd); }
    } catch (e) { /* some platforms refuse to open a directory; the file is still on disk */ }
  },
  consume(file) {
    fs.unlinkSync(file);
  },
  remove(file) {
    try { fs.unlinkSync(file); } catch (e) { }
  }
};

// ---------------------------------------------------------------------------------------------
// HTTP
// ---------------------------------------------------------------------------------------------

// One RPC. The result says HOW it failed, because "the harness rejected it" and "the POST may
// have arrived but no answer came back" must never be collapsed into one word:
//   transport "answered"  - a response arrived (2xx, 4xx, 5xx, garbage body).
//   transport "not-sent"  - the connection was never established (refused/unresolvable host).
//   transport "unknown"   - the request was flushed to the socket and then something happened.
//   transport "not-opened"- we could not even create the request.
function rpc(cfg, cookie, method, args, cb) {
  const rpcId = crypto.randomUUID ? crypto.randomUUID() : crypto.randomBytes(16).toString("hex");
  const body = JSON.stringify({
    type: "client-request",
    rpcId: rpcId,
    method: method,
    payload: { args: args }
  });
  let finished = false;
  let wrote = false;
  let answered = false;
  const done = (res) => {
    if (finished) return;
    finished = true;
    cb(res);
  };
  let req;
  try {
    req = http.request({
      host: cfg.host,
      port: cfg.port,
      method: "POST",
      path: "/api/" + method,
      headers: {
        // Host is load bearing: the cookie name and its signature are both derived from it.
        Host: cfg.host + ":" + cfg.port,
        "content-type": "application/json",
        "content-length": Buffer.byteLength(body),
        cookie: cookie
      },
      timeout: 15000
    }, (res) => {
      answered = true;
      let text = "";
      res.setEncoding("utf8");
      res.on("data", (d) => { text += d; });
      res.on("aborted", () => {
        done({
          ok: false, transport: "answered", unparseable: true,
          error: "response aborted after " + text.length + " bytes"
        });
      });
      res.on("error", (e) => {
        done({ ok: false, transport: "answered", unparseable: true, error: "response error: " + e.message });
      });
      res.on("end", () => {
        if (res.statusCode !== 200) {
          // The harness answered: this is a definite rejection, not an unknown.
          return done({
            ok: false, transport: "answered", httpStatus: res.statusCode,
            error: "http " + res.statusCode + ": " + text.slice(0, 300)
          });
        }
        let parsed;
        try { parsed = JSON.parse(text); } catch (e) {
          // 200 with an unreadable body: something happened, we cannot say what.
          return done({
            ok: false, transport: "answered", httpStatus: 200, unparseable: true,
            error: "unparseable reply: " + text.slice(0, 200)
          });
        }
        if (!parsed || parsed.type !== "server-response" || !parsed.result) {
          return done({
            ok: false, transport: "answered", httpStatus: 200, unparseable: true,
            error: "unexpected envelope: " + text.slice(0, 200)
          });
        }
        if (!parsed.result.ok) {
          const e = parsed.result.error || {};
          return done({
            ok: false, transport: "answered", httpStatus: 200,
            error: (e.code || "error") + ": " + (e.message || ""), code: e.code || null
          });
        }
        done({ ok: true, transport: "answered", httpStatus: 200, value: parsed.result.value });
      });
    });
  } catch (e) {
    return done({ ok: false, transport: "not-opened", error: String(e && e.message ? e.message : e) });
  }
  req.on("finish", () => { wrote = true; });
  req.on("timeout", () => { req.destroy(new Error("timeout after 15000 ms")); });
  req.on("error", (e) => {
    const code = e && e.code ? e.code : null;
    const hardNotSent = /^(ECONNREFUSED|ENOTFOUND|EHOSTUNREACH|ENETUNREACH|EADDRNOTAVAIL|ENETDOWN)$/.test(code || "");
    let transport;
    if (answered) transport = "answered";
    else if (hardNotSent && !wrote) transport = "not-sent";
    else transport = "unknown";
    done({
      ok: false, transport: transport, code: code,
      error: (code ? code + ": " : "") + (e && e.message ? e.message : String(e)),
      requestWritten: wrote
    });
  });
  req.write(body);
  req.end();
}

// Map one RPC result onto the three delivery outcomes.
function classifyReply(res) {
  if (res && res.ok) return { outcome: "sent", error: null, code: null };
  const error = (res && res.error) || "unknown";
  const code = (res && res.code) || null;
  if (res && res.transport === "answered") {
    if (res.unparseable) {
      // The harness spoke but we could not read the answer: it may well have accepted.
      return { outcome: "unknown", error: error, code: code || "unreadable-answer" };
    }
    return { outcome: "failed", error: error, code: code || (res.httpStatus ? "http-" + res.httpStatus : null) };
  }
  if (res && res.transport === "not-sent") return { outcome: "failed", error: error, code: code || "not-sent" };
  if (res && res.transport === "not-opened") return { outcome: "failed", error: error, code: code || "not-opened" };
  return { outcome: "unknown", error: error, code: code || "transport-unknown" };
}

// ---------------------------------------------------------------------------------------------
// small file helpers used by the engine (all through ctx.io so tests can inject failures)
// ---------------------------------------------------------------------------------------------

function readInt(io, file) {
  try {
    const n = parseInt(String(io.readText(file)).trim(), 10);
    return Number.isFinite(n) && n >= 0 ? n : 0;
  } catch (e) {
    return 0;
  }
}

function readPointer(ctx) {
  const file = ctx.cfg.pointerFile;
  let text;
  try {
    text = ctx.io.readText(file);
  } catch (e) {
    return { ok: false, kind: "no-pointer", detail: "no " + file + " (" + (e.code || e.message) + "); pass --new to create a session on purpose" };
  }
  let obj;
  // A UTF-8 BOM is whatever wrote the file (PowerShell 5.1 -Encoding utf8 adds one), not part
  // of the pin. Stripping it is not guessing a session; every byte of the id is still there.
  const body = text.charCodeAt(0) === 0xfeff ? text.slice(1) : text;
  try { obj = JSON.parse(body); } catch (e) {
    return { ok: false, kind: "pointer-invalid", detail: file + " is not JSON: " + String(text).slice(0, 80) };
  }
  if (!obj || !obj.sessionId) return { ok: false, kind: "no-pointer", detail: file + " has no sessionId" };
  return { ok: true, sessionId: String(obj.sessionId), at: obj.at || null };
}

// The same requestId for the same outbox line, on every attempt, in every process. A retry is
// then visibly the same message instead of pretending to be a first send.
function requestIdForKey(key) {
  const h = crypto.createHash("sha256").update("spmcp-outbox/" + key, "utf8").digest();
  h[6] = (h[6] & 0x0f) | 0x40;
  h[8] = (h[8] & 0x3f) | 0x80;
  const hex = h.toString("hex").slice(0, 32);
  return hex.slice(0, 8) + "-" + hex.slice(8, 12) + "-" + hex.slice(12, 16) + "-" +
    hex.slice(16, 20) + "-" + hex.slice(20, 32);
}

// ---------------------------------------------------------------------------------------------
// the engine
// ---------------------------------------------------------------------------------------------

function createLink(options) {
  const opts = options || {};
  const cfg = Object.assign({}, DEFAULTS, opts.cfg || {});
  const io = opts.io || realIo;
  const log = opts.log || makeLogger(cfg);
  const rpcFn = opts.rpc || rpc;
  const cookie = opts.cookie || "";
  const now = opts.now || (() => Date.now());
  const initializedSessions = new Set();
  let onceopenLoaded = false;
  const readGameWorld = opts.readGameWorld || ((done) => {
    const request = http.get({
      host: "127.0.0.1", port: cfg.bridgePort || 19080, path: "/comms/state", timeout: 1500
    }, (response) => {
      response.setEncoding("utf8");
      let text = "";
      response.on("data", (chunk) => {
        text += chunk;
        if (text.length > 65536) request.destroy(new Error("oversized comms state"));
      });
      response.on("error", done);
      response.on("end", () => {
        let reply;
        try { reply = JSON.parse(text); } catch (e) { return done(e); }
        if (response.statusCode !== 200 || !reply.ok || !reply.data ||
            !Object.hasOwn(reply.data, "worldKey")) return done(new Error("Bridge lacks game-world support"));
        done(null, reply.data.worldKey);
      });
    });
    request.on("error", done);
    request.on("timeout", () => request.destroy(new Error("Bridge state timed out")));
  });

  const state = {
    phase: "starting",
    reason: null,
    detail: null,
    // session
    sessionSource: null,      // "pointer" | "new" | "--trust-pointer"
    sessionVerified: null,    // true = session/list listed it; false = operator override
    // cursor
    offset: 0,
    offsetDirty: false,       // an outcome is durable but the cursor write failed
    // backpressure / visibility
    deliveryBlocked: null,    // a string explains the refusal; null means this helper may send
    paused: false,
    pauseReason: null,
    pausedAt: null,
    consecutiveNoSession: 0,
    pending: 0,
    lastPhaseKey: null,
    lastStatusAt: 0,
    statusDirty: false,       // an outcome was recorded since the last status write
    // A critical write failed at least once and has not been cleared by a successful recovery.
    persistFault: null,
    // Outcomes we know in memory but could NOT write down. Kept so a recovery pass can persist
    // them instead of posting the same prompt a second time.
    unpersisted: [],
    unpersistedKeys: Object.create(null),
    counts: { sent: 0, unknown: 0, failed: 0, refused: 0, attempts: 0 },
    attempts: Object.create(null),   // key -> attempts seen (rebuilt from the state file)
    refused: Object.create(null),    // key -> true, so one refusal is recorded once per run
    stopped: false
  };

  // ---- state records -------------------------------------------------------------------

  // Appends one durable record. THROWS on failure: every caller must treat that as "not
  // recorded" and stop, because the whole point of S12 is that a lost append can never look
  // like a completed delivery.
  function appendState(rec) {
    const entry = Object.assign({ k: "link", at: now() }, rec);
    try {
      io.append(cfg.stateFile, JSON.stringify(entry) + "\n");
    } catch (e) {
      // Name the file: "ENOSPC: no space left on device" alone does not tell the operator
      // WHICH durable record was lost, and that is the one thing they need to know.
      const wrapped = new Error("cannot append to " + cfg.stateFile + ": " + (e && e.message ? e.message : String(e)));
      wrapped.cause = e;
      wrapped.code = e && e.code ? e.code : null;
      throw wrapped;
    }
    return entry;
  }

  function tryRecord(rec) {
    try {
      appendState(rec);
      return null;
    } catch (e) {
      return e;
    }
  }

  function readStateRows() {
    let text;
    try { text = io.readText(cfg.stateFile); } catch (e) { return []; }
    const rows = [];
    for (const line of text.split("\n")) {
      if (!line.trim()) continue;
      try { rows.push(JSON.parse(line)); } catch (e) { /* a torn final line is not a record */ }
    }
    return rows;
  }

  // ---- status --------------------------------------------------------------------------

  function statusText() {
    const s = state;
    const sess = cfg.sessionId
      ? cfg.sessionId + "(" + (s.sessionSource || "?") + (s.sessionVerified === true ? ",verified" : s.sessionVerified === false ? ",unverified" : "") + ")"
      : "-";
    return "phase=" + s.phase +
      " reason=" + (s.reason || "-") +
      " session=" + sess +
      " offset=" + s.offset + (s.offsetDirty ? "!dirty" : "") +
      (s.persistFault ? " PERSIST-FAULT" : "") +
      " pending=" + s.pending +
      " sent=" + s.counts.sent + " unknown=" + s.counts.unknown +
      " failed=" + s.counts.failed + " refused=" + s.counts.refused +
      " attempts=" + s.counts.attempts;
  }

  // The status file is observability, not delivery state: if writing it fails we say so on
  // stderr and keep going. Delivery state goes through appendState above.
  function writeStatusFile() {
    // Recompute the queue depth from the file every time: a status line that says pending=0
    // while two messages are stuck in the outbox is exactly the kind of lie S12 is about.
    countPending();
    state.statusDirty = false;
    const body = {
      k: "link.status", at: now(), phase: state.phase, reason: state.reason, detail: state.detail,
      paused: state.paused, pauseReason: state.pauseReason, pausedAt: state.pausedAt,
      persistFault: state.persistFault || null,
      unpersistedOutcomes: state.unpersisted.length,
      sessionId: cfg.sessionId || null, sessionSource: state.sessionSource,
      sessionVerified: state.sessionVerified, mode: cfg.mode,
      offset: state.offset, offsetDirty: state.offsetDirty, pending: state.pending,
      counts: state.counts, line: statusText()
    };
    try {
      io.writeAtomic(cfg.statusFile, JSON.stringify(body, null, 2) + "\n");
      return null;
    } catch (e) {
      try { process.stderr.write("link: cannot write " + cfg.statusFile + ": " + e.message + "\n"); } catch (e2) { }
      return e;
    }
  }

  // Phase transition: logged, published to the status file, and recorded in the state file
  // once per distinct (phase, reason, detail).
  function setPhase(phase, reason, detail) {
    if (state.paused && phase !== "paused") {
      // A pause is the operator's only clue that delivery stopped. Letting the next phase line
      // win produced link.status.json saying phase="no-session" while paused=true (round 2).
      return;
    }
    const key = phase + "|" + (reason || "") + "|" + (detail || "");
    const changed = key !== state.lastPhaseKey;
    state.phase = phase;
    state.reason = reason || null;
    state.detail = detail || null;
    state.lastStatusAt = now();
    if (!changed) return;
    state.lastPhaseKey = key;
    log("state " + statusText());
    writeStatusFile();
    const err = tryRecord({
      phase: phase, reason: state.reason, detail: state.detail,
      outcome: phase === "no-session" || phase === "refused" ? "failed" : undefined,
      sessionId: cfg.sessionId || null, sessionSource: state.sessionSource,
      sessionVerified: state.sessionVerified, consumed: false, cursor: state.offset,
      pending: state.pending
    });
    if (err) criticalPersistFailure("record the '" + phase + "' state", err);
  }

  function pause(reason, detail) {
    if (state.paused && state.pauseReason === reason) {
      state.detail = detail || state.detail;
      return;
    }
    state.paused = true;
    state.pauseReason = reason;
    state.reason = reason;
    state.detail = detail || null;
    state.pausedAt = now();
    state.phase = "paused";
    state.lastPhaseKey = null;
    log("PAUSED automatic delivery (" + reason + "): " + (detail || "-") + " | " + statusText());
    writeStatusFile();
    const pausedRecordError = tryRecord({
      phase: "paused", reason: reason, detail: detail || null,
      note: "automatic delivery stopped; the game input and display paths are unaffected; queued lines stay queued",
      sessionId: cfg.sessionId || null, sessionSource: state.sessionSource,
      consumed: false, cursor: state.offset, pending: state.pending, paused: true
    });
    if (pausedRecordError) {
      // A pause nobody can read after a restart is worse than no pause: the operator sees a
      // healthy helper and a stalled queue. Say it in the status file (review round 2).
      state.persistFault = String((pausedRecordError && pausedRecordError.message) || pausedRecordError);
      log("WARNING could not record the pause: " + state.persistFault);
      writeStatusFile();
    }
  }

  function resume(why) {
    if (!state.paused) return;
    const was = state.pauseReason;
    state.paused = false;
    state.pauseReason = null;
    state.pausedAt = null;
    state.lastPhaseKey = null;
    log("RESUMED automatic delivery (was " + was + ": " + why + ")");
    tryRecord({ phase: "resumed", was: was, why: why, cursor: state.offset, pending: state.pending });
    setPhase("running", null, null);
  }

  // A critical state write failed. Keep the in-memory result (so this run does not resend),
  // stop sending, and say so loudly on stderr.
  function criticalPersistFailure(what, err) {
    const detail = "cannot " + what + ": " + (err && err.message ? err.message : String(err));
    state.persistFault = { what: what, detail: detail, at: now() };
    try { process.stderr.write("link: CRITICAL " + detail + "\n"); } catch (e) { }
    log("CRITICAL " + detail);
    pause("persist-failure", detail);
    return err;
  }

  function rememberUnpersisted(rec) {
    state.unpersisted.push(rec);
    if (rec.key) state.unpersistedKeys[rec.key] = true;
    state.statusDirty = true;
  }

  /**
   * Recovery after a persist failure: write the outcomes we are holding in memory, and only
   * then let the cursor move past them.
   *
   * This is the audit's "reconcile before continuing after recovery" row. Skipping it would
   * re-POST a prompt whose outcome we already know - the duplicate the 'unknown' rule exists
   * to avoid.
   */
  // Prove the state ledger accepts an ordinary record. The pause was raised by a failed write,
  // so the only honest test is another write: an empty in-memory list says nothing about
  // whether the volume works (that assumption made a paused helper flip through
  // CRITICAL/paused/RESUMED on every tick without ever recovering).
  function probeStateWritable() {
    try {
      appendState({ k: "link", phase: "persist-probe", at: now() });
      state.persistFault = null;
      return true;
    } catch (e) {
      state.persistFault = { what: "probe the state ledger", detail: String(e.message || e), at: now() };
      return false;
    }
  }

  function flushUnpersisted() {
    if (state.unpersisted.length === 0) {
      return probeStateWritable();
    }
    const pending = state.unpersisted.slice();
    let frontier = state.offset;
    for (const rec of pending) {
      try {
        appendState(rec);
      } catch (e) {
        state.persistFault = { what: "flush " + pending.length + " held outcome(s)", detail: String(e.message || e), at: now() };
        return false;
      }
      if (typeof rec.end === "number" && rec.end > frontier) frontier = rec.end;
    }
    state.unpersisted = [];
    state.unpersistedKeys = Object.create(null);
    state.persistFault = null;
    state.offset = frontier;
    state.statusDirty = true;
    log("recovery: persisted " + pending.length + " held outcome(s); cursor now " + frontier);
    return persistCursor() === null;
  }

  // ---- cursor --------------------------------------------------------------------------

  function persistCursor() {
    try {
      io.writeAtomic(cfg.offsetFile, String(state.offset));
      state.offsetDirty = false;
      return null;
    } catch (e) {
      state.offsetDirty = true;
      return criticalPersistFailure("persist the cursor to " + cfg.offsetFile, e);
    }
  }

  // Rebuild the cursor from the durable records before the first send: a record on disk means
  // that line was already handled, whatever the cursor file ended up saying.
  function reconcile() {
    state.offset = readInt(io, cfg.offsetFile);
    const rows = readStateRows();
    // Delivery evidence, not row count: start() records a `starting` phase row before it
    // gets here, so counting rows made this branch unreachable in exactly the run where a
    // lost ledger matters most.
    // "Evidence" has to be a row that says which lines were handled, and where they ended.
    // The deployed ledger writes {id,requestId,sessionId,ok,error,code,at} - no k, no phase -
    // so requiring k==="link" made every historical row invisible and paused a healthy
    // directory as ledger-lost (review round 2). A row without a span proves nothing either:
    // no-session/failed rows carry an outcome but no end, and counting them hid the loss
    // from the second start onwards.
    const hasSpan = (r) => typeof (r.end === undefined ? r.offset : r.end) === "number";
    const legacyDelivery = (r) => r && r.ok === true && hasSpan(r);
    const deliveries = rows.filter((r) =>
      r && (r.consumed === true || ((r.outcome || r.ok === true) && hasSpan(r))));
    if (deliveries.length === 0 && state.offset > 0) {
      // The cursor says lines were handled; the ledger that proves which ones is gone (truncated
      // or deleted). Continuing would silently re-send them, so stop and say so.
      const e = tryRecord({
        phase: "ledger-lost", cursor: state.offset,
        error: "the state ledger is empty but the cursor is at byte " + state.offset +
          "; which lines were already delivered is unknown - inspect " + cfg.stateFile +
          " or restart deliberately with --reset-cursor"
      });
      if (e) return e;
      pause("ledger-lost", "state ledger is empty while the cursor is at " + state.offset +
        "; refusing to guess which lines were delivered");
      return null;
    }
    let frontier = 0;
    let mapped = 0;
    const openSending = new Map();
    for (const r of rows) {
      if (!r) continue;
      if (r.k === "link" && r.phase === "sending") {
        if (typeof r.end === "number" && r.key) mapped++;
        // A 'sending' row whose outcome never got written means the process stopped between the
        // POST and the record. Leaving it invisible made the line look unsent forever.
        if (typeof r.key === "string") openSending.set(r.key, r);
        continue;
      }
      if (r.outcome) openSending.delete(r.key);
      if (r.consumed !== true && !legacyDelivery(r)) continue;
      if (typeof r.end !== "number" || typeof r.key !== "string") continue;
      mapped++;
      if (r.end > frontier) frontier = r.end;
      if (r.key && typeof r.attempt === "number") {
        state.attempts[r.key] = Math.max(state.attempts[r.key] || 0, r.attempt);
      }
    }
    for (const [key, r] of openSending) {
      const e = tryRecord({
        phase: "delivery", outcome: "unknown", code: "resumed-after-crash", key: key,
        id: r.id == null ? null : r.id, requestId: r.requestId || null,
        offset: r.offset == null ? null : r.offset, end: r.end == null ? null : r.end,
        attempt: r.attempt || 1, consumed: true,
        error: "the previous run stopped after sending this line and before recording what came back; " +
          "resend it deliberately with --retry-unknown"
      });
      if (e) return e;
      mapped++;
      if (typeof r.end === "number" && r.end > frontier) frontier = r.end;
    }
    // --reset-cursor is the documented recovery for a rewritten outbox. Reconcile used to
    // push the frontier straight back over it (frontier > 0), so the reset appeared to work
    // and the queue stayed stuck at the old cursor (review round 1).
    if (frontier > state.offset && !cfg.resetCursor) {
      const from = state.offset;
      state.offset = frontier;
      state.offsetDirty = true;
      log("cursor repaired from " + from + " to " + frontier + " using " + mapped + " durable record(s)");
      tryRecord({ phase: "reconciled", from: from, to: frontier, durableRecords: mapped });
      const err = persistCursor();
      if (err) return err;
    }
    if (state.offsetDirty) {
      const err = persistCursor();
      if (err) return err;
    }
    return null;
  }

  // ---- session -------------------------------------------------------------------------

  function adopt(sessionId, source, verified) {
    cfg.sessionId = sessionId;
    state.sessionSource = source;
    state.sessionVerified = verified;
  }

  // Write the pointer file the tailer (and therefore the page) follows. Returns null on
  // success, or the error text. An id that is not pinned is not a session this helper may
  // deliver to: the refusal has to stick until the write actually succeeds.
  function pinSession(sessionId) {
    try {
      const pointer = { sessionId, at: now(), source: "new" };
      if (cfg.mode === "game-world") {
        pointer.source = "game-world";
        pointer.world = cfg.worldKey;
      }
      io.writeAtomic(cfg.pointerFile, JSON.stringify(pointer));
    } catch (e) {
      state.pinnedSession = sessionId;
      cfg.sessionId = null;
      state.sessionSource = "new";
      state.sessionVerified = false;
      return "cannot write " + cfg.pointerFile + ": " + e.message;
    }
    state.pinnedSession = null;
    // We created it; session/list has not confirmed it yet. Calling that "verified" made the
    // status line claim a confirmation that never happened.
    adopt(sessionId, "new", false);
    return null;
  }

  function createSession(done) {
    rpcFn(cfg, cookie, "session/create", { request: { cwd: cfg.cwd } }, (res) => {
      if (!res || !res.ok) {
        return done("create-failed", "session/create failed: " + ((res && res.error) || "unknown"));
      }
      const id = res.value && res.value.sessionId;
      if (!id) return done("create-failed", "session/create returned no sessionId");
      cfg.sessionId = id;   // --new creates ONE session, not one per tick
      adopt(id, "new", false);
      log("session/create -> " + id);
      // The tailer follows this file. Not writing it strands the inbound direction, so it is a
      // critical failure, not a warning - and the created id must not survive as a delivery
      // target, or the next tick would quietly send to a session nobody is watching.
      const pinErr = pinSession(id);
      if (pinErr) {
        pause("pointer-write-failed", pinErr);
        return done("pointer-write-failed", pinErr);
      }
      done(null);
    });
  }

  // Ask the harness whether the pinned session is still one it knows. No answer and "no such
  // session" are both refusals - we never fall through to another session.
  function validateSession(sessionId, done) {
    rpcFn(cfg, cookie, "session/list", { _request: {} }, (res) => {
      if (!res || !res.ok) {
        return done("session-list-failed", "session/list could not confirm " + sessionId + ": " + ((res && res.error) || "unknown"));
      }
      const value = res.value || {};
      const items = value.items;
      if (!Array.isArray(items)) {
        return done("session-list-unusable", "session/list returned no items array; cannot confirm " + sessionId);
      }
      const found = items.some((it) => it && (it.sessionId === sessionId || it.id === sessionId));
      if (!found) {
        return done("session-not-found",
          "session " + sessionId + " is not in session/list (" + items.length + " listed); refusing to create or adopt another one (use --new deliberately, or --trust-pointer if the list is the thing that is wrong)");
      }
      done(null);
    });
  }

  // The continuation contract is done(errKind, detail): errKind null means a session is
  // adopted and its id is in cfg.sessionId. Keeping the id in cfg (rather than a third
  // argument) is what makes `--new` create one session per RUN instead of one per tick - the
  // short-circuit below is that cache, and a third positional argument here would silently
  // disagree with it.
  function ensureSession(done) {
    if (cfg.sessionId) return done(null, cfg.sessionId);
    if (cfg.mode === "new" || cfg.mode === "game-world") return createSession(done);
    const ptr = readPointer({ cfg: cfg, io: io });
    if (!ptr.ok) return done(ptr.kind, ptr.detail);
    if (cfg.trustPointer) {
      adopt(ptr.sessionId, "--trust-pointer", false);
      log("session " + ptr.sessionId + " adopted from " + cfg.pointerFile + " WITHOUT session/list confirmation (--trust-pointer)");
      return done(null);
    }
    validateSession(ptr.sessionId, (errKind, detail) => {
      if (errKind) return done(errKind, detail);
      adopt(ptr.sessionId, "pointer", true);
      log("session " + ptr.sessionId + " confirmed by session/list (pinned by " + cfg.pointerFile + ")");
      done(null);
    });
  }

  // ---- outbox helpers ------------------------------------------------------------------

  function readOutbox() {
    try { return io.readBytes(cfg.outbox); } catch (e) { return null; }
  }

  // Byte ranges of the complete (newline-terminated) lines a consumer may act on. Everything
  // upstream of this point is bytes: `start`/`end`/`outbox.offset` all count bytes.
  function completeLines(buf, from) {
    const out = [];
    if (!buf) return out;
    let pos = Math.max(0, from | 0);
    while (pos < buf.length) {
      const nl = buf.indexOf(0x0a, pos);
      if (nl < 0) break;                       // a half-written line is not a line yet
      let body = buf.subarray(pos, nl);
      // A UTF-8 BOM at the head of the file is an artifact of whatever wrote it, not part of
      // the first message. Strip it from the DECODED line only - the byte range stays exact.
      if (pos === 0 && body.length >= 3 && body[0] === 0xef && body[1] === 0xbb && body[2] === 0xbf) {
        body = body.subarray(3);
      }
      out.push({ start: pos, end: nl + 1, line: body.toString("utf8") });
      pos = nl + 1;
    }
    return out;
  }

  function countPending() {
    const buf = readOutbox();
    if (buf == null) { state.pending = 0; return 0; }
    // A cursor past the end means the outbox was rewritten underneath us. Reporting pending:0
    // there hid a queue that is still waiting; count from the top and say nothing was lost.
    const cursor = buf.length < state.offset ? 0 : state.offset;
    let n = 0;
    for (const l of completeLines(buf, cursor)) if (l.line.trim()) n++;
    // Bytes after the last newline are a line that is still being written. It is in flight, so
    // a status that reports pending:0 while it sits there under-reports the queue.
    const from = Math.max(0, cursor | 0);
    const lastNl = buf.lastIndexOf(0x0a);
    const tornStart = lastNl < 0 ? from : lastNl + 1;
    if (tornStart >= from && tornStart < buf.length && buf.subarray(tornStart).toString("utf8").trim()) n++;
    state.pending = n;
    return n;
  }

  // ---- delivery ------------------------------------------------------------------------

  function deliver(line, start, end, cb) {
    const key = "o" + start;
    if (state.unpersistedKeys[key]) {
      // We already know what happened to this line but could not write it down. Posting again
      // would be the duplicate delivery the audit tells us never to do.
      log("refusing to re-send o" + start + ": a held outcome for it is waiting for a durable write");
      return cb(new Error("unpersisted-outcome"));
    }
    const attempt = (state.attempts[key] || 0) + 1;
    state.attempts[key] = attempt;
    let msg;
    try {
      msg = JSON.parse(line);
    } catch (e) {
      // A corrupt line is a definite non-delivery. Record it and move on: it must not block
      // every later message forever (the audit's "rejected/invalid advances" row).
      const err = tryRecord({
        phase: "delivery", outcome: "failed", reason: "bad-json", key: key,
        offset: start, end: end, consumed: true, id: null, requestId: null,
        sessionId: cfg.sessionId || null, attempt: attempt,
        error: "outbox line is not JSON: " + String(line).slice(0, 80), code: "bad-json"
      });
      if (err) return cb(criticalPersistFailure("record the bad outbox line", err));
      state.counts.failed++;
      log("line o" + start + " -> failed (not JSON)");
      return cb(null);
    }
    if (cfg.mode === "game-world" && (msg.from !== "player" || msg.world !== cfg.worldKey)) {
      const err = tryRecord({
        phase: "delivery", outcome: "skipped", reason: "outside-current-game-dialogue",
        key, offset: start, end, consumed: true, id: msg.id == null ? null : msg.id
      });
      if (err) return cb(criticalPersistFailure("record skipped game dialogue", err));
      return cb(null);
    }
    let text = String(msg.text == null ? "" : msg.text);
    let onceopen = false;
    if (cfg.mode === "game-world") {
      if (!onceopenLoaded) {
        for (const row of readStateRows()) {
          if (row.phase === "sending" && row.onceopen === true) initializedSessions.add(row.sessionId);
        }
        onceopenLoaded = true;
      }
      // Style is read for every player turn, so editing the deployed persona
      // affects an existing game chat without replacing the Harness identity.
      let persona = '执行方式以建单回执 executionMode 为准：worker 确认后结束；main 本轮使用 sp_main_api_call/finish 完成接口交互后报告。不派子代理，不轮询。';
      try {
        const file = cfg.personaFile || path.join(path.dirname(path.dirname(cfg.outbox)), 'persona.md');
        const style = io.readText(file).trim();
        if (!style) log('WARNING persona.md is empty; game dialogue instructions are missing');
        persona += '\n' + style;
      } catch (e) {
        log('WARNING cannot load persona.md; game dialogue instructions are missing: ' + e.message);
      }
      if (!initializedSessions.has(cfg.sessionId)) {
        let context;
        try {
          context = io.readText(path.join(__dirname, "prompts", "onceopen.md")).trim();
          if (!context) throw new Error("empty prompt");
        } catch (e) {
          log("cannot load prompts/onceopen.md: " + e.message);
          return cb(e);
        }
        if (persona) context += '\n\n' + persona;
        text = "<seapower-onceopen>\n" + context + "\n</seapower-onceopen>\n\n" + text;
        onceopen = true;
      } else if (persona) {
        text = '<seapower-persona>\n' + persona + '\n</seapower-persona>\n\n' + text;
      }
    }
    const requestId = requestIdForKey(key);
    const request = {
      requestId: requestId,
      sessionId: cfg.sessionId,
      mode: "queue",
      content: [{ type: "text", text: text }]
    };
    if (cfg.timeZone) request.clientTimeZone = cfg.timeZone;
    // The identity is on disk BEFORE the POST, so a crash mid-flight leaves a record that says
    // "this request may have been sent", not a hole.
    const pre = tryRecord({
      phase: "sending", key: key, offset: start, end: end,
      id: msg.id == null ? null : msg.id, requestId: requestId,
      sessionId: cfg.sessionId, attempt: attempt, ...(onceopen ? { onceopen: true } : {})
    });
    if (pre) return cb(criticalPersistFailure("record the send of line o" + start, pre));
    if (onceopen) initializedSessions.add(cfg.sessionId);
    state.counts.attempts++;
    rpcFn(cfg, cookie, "session/prompt", { request: request }, (res) => {
      const cls = classifyReply(res);
      const rec = {
        phase: "delivery", outcome: cls.outcome, key: key, offset: start, end: end,
        consumed: true,                       // durable result: the cursor may move
        id: msg.id == null ? null : msg.id,
        requestId: requestId, sessionId: cfg.sessionId, attempt: attempt,
        error: cls.error, code: cls.code
      };
      if (cls.outcome === "sent") {
        rec.ok = true;
        rec.accepted = true;                  // accepted by the harness, not answered by the agent
        const v = res && res.value;
        if (v && v.requestId) rec.harnessRequestId = String(v.requestId);
        if (v && v.turnId) rec.turnId = String(v.turnId);
      } else {
        rec.ok = false;
      }
      const err = tryRecord(rec);
      if (err) {
        // The outcome is unknown to disk, so the cursor must not claim this line is handled.
        // Hold the record in memory: a recovery pass persists it rather than posting again.
        rememberUnpersisted(rec);
        if (cls.outcome === "sent") state.counts.sent++;
        else if (cls.outcome === "unknown") state.counts.unknown++;
        else state.counts.failed++;
        return cb(criticalPersistFailure("record the outcome of line o" + start, err));
      }
      state.statusDirty = true;
      if (cls.outcome === "sent") { state.counts.sent++; log("prompt id=" + rec.id + " request=" + requestId + " -> sent (accepted)"); }
      else if (cls.outcome === "unknown") {
        state.counts.unknown++;
        log("prompt id=" + rec.id + " request=" + requestId + " -> UNKNOWN (" + cls.error + "); not retried automatically, see --retry-unknown");
      } else {
        state.counts.failed++;
        log("prompt id=" + rec.id + " request=" + requestId + " -> FAILED " + cls.error);
        if (sessionLooksGone(res)) {
          // The harness no longer has the pinned session: forget it and let the next tick go
          // through the honest resolution again (which will refuse).
          log("session " + cfg.sessionId + " looks gone; re-resolving before the next line");
          cfg.sessionId = null;
          state.sessionSource = null;
          state.sessionVerified = null;
        }
      }
      cb(null);
    });
  }

  function sessionLooksGone(res) {
    const text = String((res && res.error) || "").toLowerCase();
    return /session/.test(text) && /(not.?found|no.?such|unknown session|missing)/.test(text);
  }

  // Walk the complete lines from the cursor. Each line advances the cursor only after its
  // outcome record is durable.
  function pump(done) {
    const buf = readOutbox();
    if (buf == null) { state.pending = 0; return done(null); }
    if (buf.length < state.offset) {
      const was = state.offset;
      // outbox.jsonl is append-only by contract; a shorter file means somebody else wrote it.
      // Clamping to the new length silently eats whatever the file still holds, so refuse and
      // make the operator decide (--reset-cursor starts over from byte 0, deliberately).
      const first = !(state.paused && state.pauseReason === "outbox-rewritten");
      // Count what is being refused BEFORE pausing: the pause writes the status file, and a
      // pending:0 there told the operator the queue was empty when it was not.
      countPending();
      pause("outbox-rewritten",
        "outbox shrank from " + was + " to " + buf.length + " bytes; refusing to guess which lines " +
        "were already sent (use --reset-cursor to start over from byte 0, or repair the file)");
      // One record per shrink, not one per poll: a paused helper polls forever, and the ledger
      // was growing a line every 700 ms while nothing changed.
      if (first) tryRecord({ phase: "outbox-shrank", from: was, to: buf.length, action: "paused" });
      return done(null);
    }
    const lines = completeLines(buf, state.offset);
    let i = 0;
    const step = () => {
      if (state.stopped || state.paused) return done(null);
      if (i >= lines.length) {
        // Blank/whitespace-only lines move the cursor without a record: nothing was lost.
        if (state.offset !== readInt(io, cfg.offsetFile)) {
          const e = persistCursor();
          if (e) return done(e);
        }
        countPending();
        return done(null);
      }
      const item = lines[i++];
      if (!item.line.trim()) {
        state.offset = item.end;
        return step();
      }
      deliver(item.line, item.start, item.end, (err) => {
        if (err) return done(err);
        state.offset = item.end;
        const e = persistCursor();
        if (e) return done(e);
        state.pending = Math.max(0, state.pending - 1);
        step();
      });
    };
    step();
  }

  // ---- the loop ------------------------------------------------------------------------

  function enterNoSession(kind, detail, done) {
    state.consecutiveNoSession++;
    countPending();
    state.counts.refused++;
    const pending = state.pending;
    // One record per waiting line (bounded), so "the link refused" is not just a log line: the
    // state file says WHICH messages are still queued and WHY they are not moving. Nothing is
    // dropped and the cursor does not move.
    const buf = readOutbox();
    const lines = buf == null ? [] : completeLines(buf, state.offset).filter((l) => l.line.trim());
    const budget = 10;
    for (let i = 0; i < Math.min(lines.length, budget); i++) {
      const key = "o" + lines[i].start;
      if (state.refused[key]) continue;
      state.refused[key] = true;
      let id = null;
      try { id = JSON.parse(lines[i].line).id; } catch (e) { id = null; }
      const err = tryRecord({
        phase: "delivery", outcome: "failed", reason: "no-session", cause: kind, detail: detail,
        key: key, offset: lines[i].start, end: lines[i].end, consumed: false,
        id: id == null ? null : id, requestId: null, sessionId: cfg.sessionId || null,
        error: "not sent: " + detail
      });
      if (err) {
        criticalPersistFailure("record the no-session refusal", err);
        return done(null);
      }
    }
    if (lines.length > budget) {
      const err = tryRecord({
        phase: "delivery", outcome: "failed", reason: "no-session", cause: kind,
        consumed: false, detail: "refusal list truncated to " + budget + " of " + lines.length + " queued lines",
        cursor: state.offset, pending: lines.length
      });
      if (err) {
        criticalPersistFailure("record the truncated refusal list", err);
        return done(null);
      }
    }
    if (state.consecutiveNoSession >= cfg.pauseAfter) {
      pause("no-session", kind + ": " + detail);
    } else {
      setPhase("no-session", kind, detail);
    }
    log("REFUSED delivery: " + kind + ": " + detail + " | pending=" + pending);
    // The poll loop must keep ticking after a refusal: that is how the session coming back is
    // noticed. Not calling back here stopped the whole helper after the first refusal.
    done(null);
  }

  function stopGeneration(done) {
    const file = path.join(path.dirname(cfg.pointerFile), "dsh.stop.json");
    let text;
    try {
      text = io.readText(file);
    } catch (e) {
      if (e.code !== "ENOENT") log("cannot read stop request: " + e.message);
      return done();
    }

    // Claim before calling DSH. A crash or uncertain response must never replay a stop.
    try {
      io.consume(file);
    } catch (e) {
      if (e.code !== "ENOENT") log("cannot claim stop request: " + e.message);
      return done();
    }
    let request;
    try { request = JSON.parse(text); } catch (e) {
      log("ignored invalid stop request");
      return done();
    }
    const pointer = readPointer({ cfg: cfg, io: io });
    if (!request || !Number.isFinite(request.expiresAt) ||
        request.expiresAt <= now() || request.expiresAt > now() + 5000 ||
        request.sessionId !== cfg.sessionId || !pointer.ok || pointer.sessionId !== cfg.sessionId) {
      log("ignored stale or mismatched DSH stop request");
      return done();
    }
    rpcFn(cfg, cookie, "session/cancel", { request: { sessionId: cfg.sessionId } }, (res) => {
      const accepted = res && res.ok && res.value && res.value.accepted === true;
      log(accepted ? "DSH stop accepted" : "DSH stop unconfirmed: " + ((res && res.error) || "unexpected response"));
      done();
    });
  }

  function syncGameWorld(done) {
    let settled = false;
    readGameWorld((err, world) => {
      if (settled) return;
      settled = true;
      if (err) return done(false);
      if (cfg.worldKey === world) return done(!!world);
      let pin;
      try {
        pin = JSON.parse(io.readText(cfg.pointerFile));
      } catch (e) {
        if (e.code !== "ENOENT") { log("cannot read game-world pointer: " + e.message); return done(false); }
      }
      const managed = pin && pin.source === "game-world";
      if (world && managed && pin.world === world && pin.sessionId) {
        cfg.worldKey = world;
        adopt(pin.sessionId, "game-world", false);
        return done(true);
      }
      try {
        io.writeAtomic(cfg.pointerFile, JSON.stringify({ source: "game-world", world, sessionId: null, at: now() }));
      } catch (e) {
        log("cannot switch game world: " + e.message);
        return done(false);
      }
      const previous = cfg.sessionId || (managed && pin.sessionId);
      cfg.worldKey = world;
      cfg.sessionId = null;
      state.pinnedSession = null;
      if (state.pauseReason === "pointer-write-failed") resume("mission changed");
      if (!previous) return done(!!world);
      rpcFn(cfg, cookie, "session/cancel", { request: { sessionId: previous } }, (res) => {
        log(res && res.ok && res.value && res.value.accepted === true
          ? "previous game session stop accepted" : "previous game session stop unconfirmed");
        done(!!world);
      });
    });
  }

  function tick(done) {
    if (cfg.mode !== "game-world") return tickSession(done);
    if (state.stopped || state.deliveryBlocked) return done(null);
    syncGameWorld((ready) => ready ? tickSession(done) : done(null));
  }

  function tickSession(done) {
    if (state.stopped) return done(null);
    if (state.deliveryBlocked) return done(null);
    if (state.paused) {
      // Paused still polls: that is how the reason going away gets noticed. Polling is not a
      // business retry, and it never re-sends a message that already has an outcome.
      if (state.offsetDirty) {
        const e = persistCursor();
        if (e) return done(null);
      }
      if (state.pauseReason === "pointer-write-failed") {
        // The session exists; only its pin failed. Retry the pin and resume only once it lands.
        if (state.pinnedSession && pinSession(state.pinnedSession) === null) {
          resume("pointer written; session " + state.pinnedSession + " is pinned again");
        } else if (now() - state.lastStatusAt > cfg.statusEveryMs) {
          state.lastStatusAt = now();
          log("still paused (pointer-write-failed): " + (state.detail || "-") + " | " + statusText());
          writeStatusFile();
        }
        if (state.paused) return done(null);
      }
      if (state.pauseReason === "persist-failure") {
        // Prove the volume works again before touching delivery.
        try {
          io.writeAtomic(cfg.statusFile, JSON.stringify({ k: "link.status", at: now(), probe: true }) + "\n");
          // Recovery must reconcile, not restart: the outcomes we already know are written
          // down first, and only then does the cursor move past them. Otherwise the next pump
          // would re-POST a prompt whose result we were holding in memory.
          const held = state.unpersisted.length;
          if (!flushUnpersisted()) {
            if (now() - state.lastStatusAt > cfg.statusEveryMs) {
              state.lastStatusAt = now();
              log("still paused (persist-failure): cannot flush held outcomes | " + statusText());
              writeStatusFile();
            }
            return done(null);
          }
          resume("persistence works again (" + held + " held)");
        } catch (e) {
          if (now() - state.lastStatusAt > cfg.statusEveryMs) {
            state.lastStatusAt = now();
            log("still paused (persist-failure): " + e.message + " | " + statusText());
            writeStatusFile();
          }
          return done(null);
        }
      }
    }
    ensureSession((errKind, detail) => {
      if (errKind) return enterNoSession(errKind, detail || "unknown", done);
      if (state.paused && state.pauseReason === "no-session") resume("session " + cfg.sessionId + " resolves again");
      state.consecutiveNoSession = 0;
      // A paused helper is not running. Resetting the phase unconditionally here overwrote the
      // pause detail on the very next poll, so the status file said "running" while delivery
      // was stopped - worse than the silence it replaced.
      if (state.phase !== "running" && !state.paused) setPhase("running", null, null);
      stopGeneration(() => pump((err) => {
        if (err) return done(null);           // paused/stopped by the persist failure
        // Keep the status file honest after a batch: a status that still says sent=0 after two
        // deliveries is a lie the operator would act on.
        if (state.statusDirty) writeStatusFile();
        if (now() - state.lastStatusAt > cfg.statusEveryMs) {
          state.lastStatusAt = now();
          log("status " + statusText());
          writeStatusFile();
        }
        done(null);
      }));
    });
  }

  // Two helpers on one outbox would deliver every line twice with the same requestId, and
  // nothing would say so. The lock file turns that into a refusal; a lock left by a dead pid
  // is taken over.
  function acquireLock() {
    const file = cfg.outbox + ".lock";
    for (let attempt = 0; attempt < 2; attempt++) {
      try {
        fs.writeFileSync(file, JSON.stringify({ pid: process.pid, at: now() }), { flag: "wx" });
        // "wx" is atomic, but a stale file removed by a loser between the open and our write
        // would leave two owners. Read the file back and confirm the pid is ours.
        let mine = null;
        try { mine = JSON.parse(io.readText(file)).pid; } catch (e5) { mine = null; }
        if (mine !== process.pid) {
          io.remove(file);
          continue;
        }
        state.lockFile = file;
        return null;
      } catch (e) {
        let other = null;
        try { other = JSON.parse(io.readText(file)).pid; } catch (e2) { other = null; }
        if (other) {
          // EPERM means the process exists but belongs to someone else: that is alive, not
          // dead. Only ESRCH is evidence of a dead pid (treating EPERM as dead stole a live
          // lock and put two helpers on one outbox).
          // A pid equal to ours is another instance of THIS process (two links in one
          // process); it is alive by definition, so it must be refused, not taken over.
          let alive = other === process.pid;
          if (!alive) {
            try { process.kill(other, 0); alive = true; } catch (e3) {
              alive = !(e3 && e3.code === "ESRCH");
            }
          }
          if (alive) {
            let at = null;
            try { at = JSON.parse(io.readText(file)).at; } catch (e4) { at = null; }
            const age = typeof at === "number" ? Math.round((now() - at) / 1000) + "s" : "?";
            return "another helper (pid " + other + ", locked " + age + ") already owns " + cfg.outbox +
              " (if that process is gone, delete " + file + " or run with --unlock)";
          }
        }
        io.remove(file);       // a lock left by a dead pid
      }
    }
    return "could not take " + file;
  }

  function releaseLock() {
    if (state.lockFile) io.remove(state.lockFile);
    state.lockFile = null;
  }

  function start() {
    io.mkdirp(path.dirname(cfg.logFile));
    try { io.mkdirp(path.dirname(cfg.statusFile)); } catch (e) { }
    setPhase("starting", null, null);
    const lockErr = acquireLock();
    if (lockErr) {
      state.deliveryBlocked = lockErr;
      setPhase("blocked", "lock", lockErr);
      log("REFUSED to deliver: " + lockErr);
    }
    // Only the lock owner may rewrite the cursor: doing this before the lock meant a helper
    // that then refused to deliver had already moved the shared cursor to 0, so the other
    // helper's next start would re-send the whole outbox.
    if (cfg.resetCursor && !lockErr) {
      const was = readInt(io, cfg.offsetFile);
      state.offset = 0;
      state.offsetDirty = false;
      const rerr = persistCursor();
      log("--reset-cursor: cursor " + was + " -> 0; every line is treated as unsent");
      if (rerr) log("could not reset the cursor: " + rerr.message);
    } else if (cfg.resetCursor) {
      log("--reset-cursor ignored: this helper does not own " + cfg.outbox);
    }
    // The loser of the lock race used to rewrite the winner's `sending` row into
    // unknown/resumed-after-crash, append a `reconciled` row and move the SHARED cursor past
    // lines the winner had not delivered yet. Reconciling is a write: only the owner may.
    const err = lockErr ? null : reconcile();
    if (err) log("started paused: " + err.message);
    writeStatusFile();
    log("start " + statusText() + " outbox=" + cfg.outbox + " " + (opts.rpc ? "rpc=injected" : "authority=" + cfg.host + ":" + cfg.port));
  }

  function runLoop() {
    const step = () => {
      if (state.stopped) return;
      tick(() => {
        if (cfg.once) {
          stop();                       // releases the lock; leaving it behind refused the next helper
          log("once-done " + statusText());
          return;
        }
        setTimeout(step, Math.max(120, cfg.pollMs));
      });
    };
    step();
  }

  function stop() {
    state.stopped = true;
    releaseLock();
  }

  // ---- explicit resend of 'unknown' messages ------------------------------------------

  // The operator's tool for the one outcome that may or may not have arrived. Off by default:
  // nothing in the loop calls this, only `--retry-unknown`.
  function retryUnknown(done) {
    // The lock exists so two helpers cannot deliver the same message twice. A retry is a
    // delivery: obeying the lock everywhere except here was the one path around it.
    if (state.deliveryBlocked) {
      return done("blocked", state.deliveryBlocked);
    }
    const rows = readStateRows();
    const last = new Map();
    const seen = new Map();
    for (const r of rows) {
      if (!r || !r.key) continue;
      if (r.phase === "delivery" && r.outcome) { last.set(r.key, r); seen.set(r.key, r); }
      if (r.phase === "retry" || r.phase === "retry-result") last.set(r.key, Object.assign({ outcome: "unknown" }, r));
    }
    const candidates = [];
    for (const [key, r] of last) {
      if (r.outcome !== "unknown") continue;
      candidates.push(r);
    }
    if (candidates.length === 0) {
      log("retry-unknown: no message is in the unknown state");
      return done(null, []);
    }
    const buf = readOutbox();
    const plan = [];
    for (const r of candidates) {
      let line = null;
      if (buf != null && typeof r.offset === "number" && typeof r.end === "number" && r.end <= buf.length) {
        // Byte slice, same units as the record: the message bytes never move, so the retry
        // re-sends exactly the bytes that were consumed the first time.
        line = buf.subarray(r.offset, r.end).toString("utf8").replace(/\n$/, "");
      }
      plan.push({ rec: r, line: line });
    }
    if (cfg.dryRun) {
      log("retry-unknown: dry run, " + plan.length + " candidate(s)");
      for (const p of plan) log("  would retry key=" + p.rec.key + " id=" + p.rec.id + " request=" + p.rec.requestId + (p.line == null ? " (line no longer readable)" : ""));
      return done(null, plan);
    }
    let i = 0;
    const step = () => {
      if (i >= plan.length) return done(null, plan);
      const p = plan[i++];
      if (p.line == null) {
        const err = tryRecord({
          phase: "retry-result", outcome: "failed", key: p.rec.key, requestId: p.rec.requestId,
          retriedFrom: p.rec.at || null, consumed: true,
          offset: p.rec.offset == null ? null : p.rec.offset,
          end: p.rec.end == null ? null : p.rec.end,
          id: p.rec.id == null ? null : p.rec.id,
          error: "outbox line for this message is no longer readable at " + p.rec.offset
        });
        if (err) return done(criticalPersistFailure("record the retry attempt", err), plan);
        log("retry key=" + p.rec.key + " -> cancelled (line gone)");
        return step();
      }
      // Re-pin the cursor is NOT touched: the message already has a durable result. We only
      // send it again, deliberately, with the same requestId and a higher attempt number.
      ensureSession((errKind, detail) => {
        if (errKind) {
          log("retry-unknown: no session, nothing retried: " + detail);
          return done(errKind, plan);
        }
        const key = p.rec.key;
        const attempt = (state.attempts[key] || p.rec.attempt || 1) + 1;
        state.attempts[key] = attempt;
        let msg = {};
        try { msg = JSON.parse(p.line); } catch (e) { msg = {}; }
        const requestId = p.rec.requestId || requestIdForKey(key);
        const pre = tryRecord({
          phase: "retry", key: key, id: p.rec.id == null ? null : p.rec.id,
          requestId: requestId, retriedFrom: p.rec.at || null, attempt: attempt,
          note: "explicit operator resend of an unknown delivery; same requestId on purpose"
        });
        if (pre) return done(criticalPersistFailure("record the retry of " + key, pre), plan);
        const request = {
          requestId: requestId, sessionId: cfg.sessionId, mode: "queue",
          content: [{ type: "text", text: String(msg.text == null ? "" : msg.text) }]
        };
        rpcFn(cfg, cookie, "session/prompt", { request: request }, (res) => {
          const cls = classifyReply(res);
          const err = tryRecord({
            phase: "retry-result", outcome: cls.outcome, key: key, requestId: requestId,
            retriedFrom: p.rec.at || null, attempt: attempt, consumed: true,
            offset: p.rec.offset == null ? null : p.rec.offset,
            end: p.rec.end == null ? null : p.rec.end,
            id: p.rec.id == null ? null : p.rec.id,
            error: cls.error, code: cls.code
          });
          if (err) return done(criticalPersistFailure("record the retry result of " + key, err), plan);
          log("retry key=" + key + " -> " + cls.outcome + (cls.error ? " " + cls.error : ""));
          step();
        });
      });
    };
    step();
  }

  return {
    cfg: cfg, state: state, io: io, log: log,
    tick: tick, start: start, runLoop: runLoop, stop: stop,
    retryUnknown: retryUnknown, statusText: statusText, writeStatusFile: writeStatusFile,
    acquireLock: acquireLock, releaseLock: releaseLock,
    reconcile: reconcile, countPending: countPending, readStateRows: readStateRows, setPhase: setPhase
  };
}

// ---------------------------------------------------------------------------------------------
// CLI
// ---------------------------------------------------------------------------------------------

function parseArgv(argv) {
  const cfg = {};
  const cli = { probe: false, once: false, retry: false, dryRun: false };
  for (let i = 2; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--port") cfg.port = parseInt(argv[++i], 10);
    else if (a === "--host") cfg.host = argv[++i];
    else if (a === "--cwd") cfg.cwd = argv[++i];
    else if (a === "--new") cfg.mode = "new";
    else if (a === "--game-world") cfg.mode = "game-world";
    else if (a === "--bridge-port") cfg.bridgePort = Number(argv[++i]);
    else if (a === "--timezone") cfg.timeZone = argv[++i];
    else if (a === "--out") cfg.outDir = argv[++i];
    else if (a === "--pause-after") cfg.pauseAfter = Math.max(1, parseInt(argv[++i], 10) || DEFAULTS.pauseAfter);
    else if (a === "--once") cli.once = true;
    else if (a === "--probe") cli.probe = true;
    else if (a === "--trust-pointer") cfg.trustPointer = true;
    else if (a === "--retry-unknown") cli.retry = true;
    else if (a === "--dry-run") cli.dryRun = true;
    else if (a === "--reset-cursor") cli.resetCursor = true;
  }
  return { cfg: cfg, cli: cli };
}

function main() {
  const parsed = parseArgv(process.argv);
  const cli = parsed.cli;
  const cfg = Object.assign({}, DEFAULTS, parsed.cfg);
  // The bridge appends to <ModDir>/comms/outbox.jsonl, and <ModDir> is the DEPLOYMENT folder -
  // not this dev tree. Without this rebase the link watched an empty directory while the
  // player's messages sat in the game folder, so the page said "queued for harness" forever.
  if (cfg.outDir) {
    const base = cfg.outDir;
    cfg.outbox = path.join(base, "outbox.jsonl");
    cfg.offsetFile = path.join(base, "outbox.offset");
    cfg.stateFile = path.join(base, "outbox.state.jsonl");
    cfg.pointerFile = path.join(base, "run", "session.pointer");
    cfg.logFile = path.join(base, "run", "link.log");
    cfg.statusFile = path.join(base, "run", "link.status.json");
  }
  cfg.once = cli.once;
  cfg.dryRun = cli.dryRun;
  cfg.resetCursor = cli.resetCursor === true;
  if (cfg.mode === "game-world" && (cli.retry || cli.resetCursor)) {
    throw new Error("--game-world cannot replay an old outbox or retry an old delivery");
  }
  const log = makeLogger(cfg);
  try { fs.mkdirSync(path.dirname(cfg.logFile), { recursive: true }); } catch (e) { }

  if (cfg.mode !== 'game-world' && !cli.probe && process.argv.indexOf('--unlock') < 0) {
    log('WARNING mode=' + cfg.mode + ': persona.md and onceopen.md will not be injected; use --game-world for in-game dialogue');
  }

  if (process.argv.indexOf("--unlock") >= 0) {
    // Operator escape hatch for the one refusal they cannot fix from inside the loop. It
    // answers the message the lock prints and deletes a local file, so it must not need
    // credentials - placing it after readSecret made the documented way out exit 2 (round 2).
    const lockPath = cfg.outbox + ".lock";
    try {
      realIo.remove(lockPath);
      log("--unlock: removed " + lockPath + " (only do this when no helper is running)");
    } catch (e) {
      log("--unlock: nothing to remove at " + lockPath + " (" + e.message + ")");
    }
    return;
  }

  const cred = readSecret(dshHome());
  if (!cred.ok) {
    log("ERROR " + cred.error);
    log("the outbox will keep queueing lines; nothing will be delivered until this is fixed");
    process.exitCode = 2;
    return;
  }
  const authority = cfg.host + ":" + cfg.port;
  const cookie = cookieFor(authority, cred);
  log("credentials " + cred.file + " authority " + authority);

  if (cli.probe) {
    // Read-only: proves the cookie without creating or prompting a session.
    rpc(cfg, cookie, "session/list", { _request: {} }, (res) => {
      if (!res.ok) {
        log("PROBE FAILED " + res.error);
        process.exitCode = 3;
        return;
      }
      const items = (res.value && res.value.items) || [];
      log("PROBE OK sessions=" + items.length);
      for (let i = 0; i < Math.min(items.length, 6); i++) {
        const it = items[i] || {};
        log("  " + (it.sessionId || it.id || "?") + " running=" + it.running + " updatedAt=" + it.updatedAt);
      }
      // Same honesty as the delivery path: say whether the pinned session is actually listed,
      // instead of leaving the operator to guess why nothing is being delivered.
      const ptr = readPointer({ cfg: cfg, io: realIo });
      if (!ptr.ok) {
        log("PROBE pointer: " + ptr.kind + " - " + ptr.detail);
      } else {
        const listed = items.some((it) => it && (it.sessionId === ptr.sessionId || it.id === ptr.sessionId));
        log("PROBE pointer: " + ptr.sessionId + " listed=" + listed +
          (listed ? "" : " - delivery would REFUSE (no-session/session-not-found)"));
      }
    });
    return;
  }

  const link = createLink({ cfg: cfg, cookie: cookie, log: log });
  link.start();
  if (cli.retry) {
    link.retryUnknown((errKind) => {
      const line = link.statusText();
      log("retry-unknown finished: " + (errKind || "done") + " | " + line);
      if (errKind) process.exitCode = 4;
      // This path returns without runLoop(), so nothing else releases the lock; the flag
      // file it left behind then refused the next helper (review round 1).
      try { link.stop(); } catch (e) { log("could not release the lock: " + e.message); }
    });
    return;
  }
  link.runLoop();
}

if (require.main === module) main();

module.exports = {
  DEFAULTS: DEFAULTS,
  createLink: createLink,
  classifyReply: classifyReply,
  requestIdForKey: requestIdForKey,
  cookieFor: cookieFor,
  readSecret: readSecret,
  b64u: b64u,
  dshHome: dshHome,
  realIo: realIo,
  parseArgv: parseArgv
};
