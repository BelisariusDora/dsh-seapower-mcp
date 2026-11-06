#!/usr/bin/env node
/*
 * dsh_tailer.js — read-only bridge between a DeepSeek Harness session log and the
 * Sea Power comms page.
 *
 * Why this exists
 * ---------------
 * DSH's HTTP API needs a session cookie that an outside process cannot obtain, and the
 * project rule is "no fabricated agent text". The session log on disk is the one place
 * where the harness already publishes, incrementally and without authentication, the
 * very things the comms page must show: the visible assistant text (not the hidden
 * reasoning), the tool it is executing, and when a turn finishes.
 *
 * The log is a concatenation of independent zstd frames (one fsync'd append each), so a
 * whole-file decompress only ever yields the first frame. This tailer keeps a byte
 * offset, splits on the zstd magic, decodes only complete frames, and holds a partial
 * trailing frame until the next append completes it.
 *
 * WHICH SESSION (audit S11)
 * -------------------------
 * The transcript this process follows is either PINNED or AUTO, and the status line always
 * says which. There is no third, quiet option any more:
 *
 *   pinned - run/session.pointer (written by harness_link.js when it creates or adopts a
 *            session) or an explicit --session id. If that log is not on disk, the tailer
 *            reports `no-session` with reason `log-not-found` and WAITS for it to appear.
 *            It never substitutes "the newest log in the workspace": a substitute would put
 *            someone else's conversation on the player's page while the panel claimed to be
 *            attached to their session.
 *   auto   - nothing is pinned, so the newest INTERACTIVE session is followed, and the status
 *            says `auto` so the operator can see it was a choice by recency. Subagent
 *            transcripts (bare-uuid directories) are never followed in this mode.
 *
 * Output: JSON lines, appended to <out> and mirrored on stdout. One line per event the
 * UI cares about. `reasoning` content is never copied into player-visible text; it only
 * rides along as a one-row thinking tail.
 *
 * Usage:
 *   node dsh_tailer.js --out <dir> [--workspace <path>] [--session <id>]
 *                      [--from-begin] [--poll 400] [--once] [--strict-pin]
 *
 *   --from-begin moves the read offset to 0 and lets the POLL loop replay the whole
 *   transcript; with --once there is no next poll, so the run attaches and prints
 *   no events. To dump a whole transcript once, run without --once and stop it.
 */

'use strict';

const fs = require('fs');
const path = require('path');
const os = require('os');
const zlib = require('zlib');

const MAGIC = Buffer.from([0x28, 0xb5, 0x2f, 0xfd]);

function parseArgs(argv) {
  const out = { poll: 400, once: false, fromBegin: false, backlog: 40, strictPin: false };
  for (let i = 2; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--out') out.out = argv[++i];
    else if (a === '--workspace') out.workspace = argv[++i];
    else if (a === '--session') out.session = argv[++i];
    else if (a === '--poll') out.poll = parseInt(argv[++i], 10);
    else if (a === '--from-begin') out.fromBegin = true;
    else if (a === '--backlog') out.backlog = Math.max(0, parseInt(argv[++i], 10) || 0);
    else if (a === '--once') out.once = true;
    // Exit non-zero instead of waiting when a pinned session has no log yet. Off by default:
    // a pinned session that is not running yet is a normal state, not a crash.
    else if (a === '--strict-pin') out.strictPin = true;
    else if (a === '--game-world') out.gameWorld = true;
  }
  return out;
}

function dshHome() {
  return process.env.DSH_HOME || path.join(os.homedir(), '.dsh');
}

/** Workspace directory names look like `--E-steam-steamapps-common-Sea~0020Power--`. */
function slugHint(workspacePath) {
  if (!workspacePath) return null;
  const base = path.basename(path.resolve(workspacePath));
  // Spaces become ~0020 in the observed slug; keep the tail so we can match on it.
  return base.replace(/\s/g, '~0020');
}

function listSessionRoots() {
  const sessionsDir = path.join(dshHome(), 'sessions');
  let names;
  try {
    names = fs.readdirSync(sessionsDir);
  } catch (e) {
    return [];
  }
  const roots = [];
  for (const name of names) {
    const full = path.join(sessionsDir, name);
    let st;
    try {
      st = fs.statSync(full);
    } catch (e) {
      continue;
    }
    if (!st.isDirectory()) continue;
    roots.push({ name, dir: full });
  }
  return roots;
}

/** Every `<root>/<sessionId>/session.v4.jsonl.zstd`, newest first. */
function listSessionLogs(hint) {
  const logs = [];
  for (const root of listSessionRoots()) {
    let kids;
    try {
      kids = fs.readdirSync(root.dir);
    } catch (e) {
      continue;
    }
    for (const kid of kids) {
      const dir = path.join(root.dir, kid);
      const log = path.join(dir, 'session.v4.jsonl.zstd');
      let st;
      try {
        st = fs.statSync(log);
      } catch (e) {
        continue;
      }
      if (!st.isFile()) continue;
      const slugMatch = hint ? root.name.indexOf(hint) !== -1 : false;
      // Subagent transcripts live in the same workspace root but their directories are
      // bare ids (`38ef9098-…`), while the session a player talks to is `session-<uuid>`.
      // The comms page must follow the interactive session, never a worker's log.
      const interactive = kid.indexOf('session-') === 0;
      logs.push({ root: root.name, sessionId: kid, dir, log, size: st.size, mtime: st.mtimeMs, slugMatch, interactive });
    }
  }
  logs.sort((a, b) => b.mtime - a.mtime);
  return logs;
}

/**
 * The session harness_link.js pinned, as a structured answer.
 *
 * `ok:false` distinguishes "no pointer file at all" from "a pointer we cannot read": the
 * second one is a fault (someone wrote a broken file or the file is being replaced right
 * now), and silently treating it as "not pinned" would switch the page to auto mode
 * without anyone asking. In auto mode that would be a visible change; here it is reported.
 */
function readPointer(outDir) {
  if (!outDir) return { ok: false, kind: 'no-out-dir', sessionId: null };
  const file = path.join(outDir, 'session.pointer');
  let text;
  try {
    text = fs.readFileSync(file, 'utf8');
  } catch (e) {
    if (e && e.code === 'ENOENT') return { ok: false, kind: 'no-pointer', sessionId: null, file };
    return { ok: false, kind: 'pointer-unreadable', sessionId: null, file, detail: String(e && e.message ? e.message : e) };
  }
  let obj;
  // A UTF-8 BOM is an artifact of whatever wrote the file (PowerShell 5.1 -Encoding utf8 adds
  // one); the id bytes are all there, so refusing it would break a working pin for no reason.
  const body = text.charCodeAt(0) === 0xfeff ? text.slice(1) : text;
  try {
    obj = JSON.parse(body);
  } catch (e) {
    return { ok: false, kind: 'pointer-invalid', sessionId: null, file, detail: String(text).slice(0, 80) };
  }
  if (!obj || !obj.sessionId) return { ok: false, kind: 'pointer-invalid', sessionId: null, file, detail: 'no sessionId field' };
  return { ok: true, kind: 'pointer', sessionId: String(obj.sessionId), file, at: obj.at || null, source: obj.source, world: obj.world };
}

/**
 * Decide which log to follow, and say why - without ever inventing a substitute.
 *
 * Returns one of:
 *   { ok: true,  pick, source: 'pointer'|'pinned'|'auto', note }
 *   { ok: false, kind, source, detail }   // caller reports no-session and waits
 *
 * Explicit pins (pointer file, --session) are hard requirements. Only when NOTHING is pinned
 * does recency choose, and then only among `session-` interactive logs.
 */
function resolveLog(opts, deps) {
  const d = deps || {};
  const listLogs = d.listLogs || listSessionLogs;
  const readPtr = d.readPointer || readPointer;
  const hint = slugHint(opts.workspace);
  const logs = listLogs(hint);

  const pointer = readPtr(opts.out);
  // A broken pointer file is not "no pin": it must not silently demote us to auto mode.
  if (!pointer.ok && pointer.kind !== 'no-pointer') {
    return {
      ok: false, source: 'pointer', kind: pointer.kind,
      detail: (pointer.file || 'session.pointer') + ': ' + (pointer.detail || pointer.kind)
    };
  }

  const pinnedId = pointer.ok ? pointer.sessionId : null;
  if (opts.gameWorld && (!pinnedId || pointer.source !== 'game-world' || !pointer.world)) {
    return { ok: false, source: 'pointer', kind: 'awaiting-game-world' };
  }
  const explicitId = opts.session ? String(opts.session) : null;

  // A subagent transcript is machinery, not the conversation: refuse it on every path, not
  // only when recency is doing the choosing.
  function machinery(l, source) {
    return {
      ok: false, source: source, pinned: l.sessionId, kind: 'log-not-found',
      detail: l.sessionId + ' is a subagent transcript (bare id), not an interactive session; not followed'
    };
  }

  if (pinnedId) {
    const hit = logs.find((l) => l.sessionId === pinnedId);
    if (hit && !hit.interactive) return machinery(hit, 'pointer');
    if (!hit) {
      // The pinned session has no transcript on this machine (different DSH_HOME, a cleaned
      // directory, or a session that was never run). Refuse to substitute.
      return {
        ok: false, source: 'pointer', pinned: pinnedId, kind: 'log-not-found',
        detail: 'pinned session ' + pinnedId + ' (' + (pointer.file || 'session.pointer') + ') has no transcript among ' +
          logs.length + ' log(s); NOT falling back to another session'
      };
    }
    return { ok: true, source: 'pointer', pinned: pinnedId, pick: hit };
  }

  if (explicitId) {
    const exact = logs.find((l) => l.sessionId === explicitId);
    if (exact) {
      if (!exact.interactive) return machinery(exact, 'pinned');
      return { ok: true, source: 'pinned', pinned: explicitId, pick: exact };
    }
    const partial = logs.filter((l) => l.sessionId.indexOf(explicitId) !== -1);
    if (partial.length === 1) {
      if (!partial[0].interactive) return machinery(partial[0], 'pinned');
      return { ok: true, source: 'pinned', pinned: explicitId, pick: partial[0], note: 'matched by prefix' };
    }
    if (partial.length > 1) {
      return {
        ok: false, source: 'pinned', pinned: explicitId, kind: 'log-ambiguous',
        detail: '--session ' + explicitId + ' matches ' + partial.length + ' logs (' +
          partial.slice(0, 4).map((l) => l.sessionId).join(', ') + '); refusing to guess'
      };
    }
    return {
      ok: false, source: 'pinned', pinned: explicitId, kind: 'log-not-found',
      detail: '--session ' + explicitId + ' has no transcript among ' + logs.length + ' log(s); NOT falling back to another session'
    };
  }

  // Nothing pinned: recency may choose, but only among interactive sessions. A subagent log
  // (bare uuid) is never "the conversation the player is in".
  const interactive = logs.filter((l) => l.interactive);
  if (interactive.length === 0) {
    return {
      ok: false, source: 'auto', kind: 'no-session',
      detail: logs.length === 0
        ? 'no session logs under ' + path.join(dshHome(), 'sessions')
        : 'no interactive (session-*) transcript among ' + logs.length + ' log(s); subagent logs are not followed'
    };
  }
  const matching = interactive.filter((l) => l.slugMatch);
  if (hint && matching.length === 0) {
    // Another project's transcript is not this conversation: recency must not reach across.
    return {
      ok: false, source: 'auto', kind: 'no-session',
      detail: 'no interactive transcript for workspace ' + hint + ' among ' + logs.length +
        ' log(s); not following another workspace'
    };
  }
  const pool = matching.length > 0 ? matching : interactive;
  return { ok: true, source: 'auto', pick: pool[0], note: 'newest interactive session (nothing pinned)' };
}

// Prefixes of harness-injected context that must never be shown as player speech.
const INJECTED_PREFIXES = [
  'Current runtime context.',
  '<system-reminder>',
  'This snapshot supersedes earlier',
  // DSH folds old turns into a summary and replays it as a user/message. It is harness
  // narration, not the player talking, so it must stay off the page exactly like the
  // runtime snapshot above. (The audit flagged this as a live defect: it was projected.)
  '[Compressed conversation section]'
];

// Harness narration that arrives in the USER role and must never be shown as the player
// speaking. Measured on a real transcript (2026-11-03): of 24 user/message events in one
// session, 12 were subagent machinery -- "Agent <uuid> sent a message: ..." and
// "Background subagent <uuid> finished ..." -- running 8-16 KB each. The compressed-summary
// prefix below catches one shape of harness text; these are the other two, and they are the
// bigger ones. A player line in this product is a short command, not an agent transcript.
// Shapes measured on real transcripts: the id is a bare uuid in most rows but
// `session-<uuid>` in at least one, and the row can start with whitespace or a blank line.
const AGENT_ID = '(?:session-)?[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}';
const INJECTED_PATTERNS = [
  new RegExp('^\\s*(?:Agent|Background subagent)\\s+' + AGENT_ID + '\\s+(?:sent a message|finished)', 'i'),
  /^\s*Its closing message:\s*$/i
];

function isInjected(text) {
  if (!text) return true;
  if (String(text).trimStart().startsWith('<seapower-notice>')) return true;
  // Leading blank lines and CRLF show up in the transcript; a marker alone on its line, with
  // whitespace around it, is still harness text. A player line that merely mentions an agent
  // keeps its position later in the sentence and still projects.
  const probe = String(text).replace(/^\s+/, '').replace(/\r/g, '');
  for (const prefix of INJECTED_PREFIXES) {
    if (text.indexOf(prefix) === 0 || probe.indexOf(prefix) === 0) return true;
  }
  text = probe;
  for (const pattern of INJECTED_PATTERNS) {
    if (pattern.test(text)) return true;
  }
  return false;
}

/** Extract the parts of an event a comms page may show. Returns null when uninteresting. */
function project(evt) {
  const t = evt && evt.type;
  const seq = evt && evt.seq;
  const time = evt && evt.time;
  const data = (evt && evt.data) || {};
  if (t === 'user/message') {
    const text = contentText(data.content).replace(/^<seapower-(onceopen|persona)>\r?\n[\s\S]*?\r?\n<\/seapower-\1>\r?\n\r?\n/, '');
    // The harness records its own injected context as user/message: the runtime snapshot,
    // the skill-catalogue reminder, and compressed-history summaries. A player line never
    // starts with these, and showing them as speech would misreport who spoke.
    if (isInjected(text)) return null;
    return { k: 'user', seq, time, text };
  }
  if (t === 'assistant/message') {
    const message = data.message || {};
    const parts = Array.isArray(message.content) ? message.content : [];
    const text = contentText(parts);
    const calls = parts.filter((p) => p && p.type === 'tool-call').map((p) => p.name);
    // The player asked to see the agent thinking instead of the tool chatter, and to get the
    // next line only when the agent actually replies. So the reasoning tail rides along on the
    // assistant event: the page renders ONE live thinking row from it, never as speech.
    return {
      k: 'assistant', seq, time, turn: data.turn, step: data.step,
      text: text || '', tools: calls, thinking: thoughtText(parts),
    };
  }
  if (t === 'tool/call') {
    return { k: 'exec', seq, time, turn: data.turn, step: data.step, name: data.name, args: preview(data.arguments) };
  }
  if (t === 'tool/result') {
    const message = data.message || {};
    return { k: 'execdone', seq, time, turn: data.turn, step: data.step, isError: message.isError === true };
  }
  if (t === 'step/start') return { k: 'step', seq, time, turn: data.turn, step: data.step, phase: 'start' };
  if (t === 'step/end') return { k: 'step', seq, time, turn: data.turn, step: data.step, phase: 'end' };
  if (t === 'turn/start') return { k: 'turn', seq, time, turn: data.turn, phase: 'start' };
  if (t === 'turn/end') return { k: 'turn', seq, time, turn: data.turn, phase: 'end' };
  return null;
}

function contentText(parts) {
  if (!Array.isArray(parts)) return '';
  const out = [];
  for (const p of parts) {
    if (!p) continue;
    if (p.type === 'text' && typeof p.text === 'string') out.push(p.text);
    // 'reasoning' is intentionally ignored here: the page must never show hidden thinking as
    // speech. It reaches the UI only through the single-row tail built by thoughtText.
  }
  return out.join('');
}

function thoughtText(parts) {
  if (!Array.isArray(parts)) return '';
  const out = [];
  for (const p of parts) {
    if (p && p.type === 'reasoning' && typeof p.text === 'string' && p.text.trim()) out.push(p.text.trim());
  }
  // One live row, so keep the tail: that is where the agent is right now.
  const flat = out.join(' ').replace(/\s+/g, ' ').trim();
  return flat.length > 400 ? '…' + flat.slice(flat.length - 400) : flat;
}

function preview(raw) {
  if (typeof raw !== 'string') return '';
  const flat = raw.replace(/\s+/g, ' ').trim();
  return flat.length > 220 ? flat.slice(0, 220) + '…' : flat;
}

function isFrameAt(buf, at) {
  if (at + 4 > buf.length) return false;
  for (let k = 0; k < 4; k++) {
    if (buf[at + k] !== MAGIC[k]) return false;
  }
  return true;
}

/**
 * Length of the zstd frame starting at `at`, or null when it is not fully on disk yet.
 *
 * The header declares the frame size (RFC 8878 §3.1.1), so the boundary is read, never guessed.
 * Scanning for the magic instead split a frame whenever its payload happened to contain
 * `28 b5 2f fd` - raw blocks carry that kind of incompressible byte verbatim - and a split frame
 * used to rewind the read offset forever.
 */
function frameLength(buf, at) {
  if (!isFrameAt(buf, at) || at + 5 > buf.length) return null;
  const fhd = buf[at + 4];
  const fcsFlag = (fhd >> 6) & 0x03;
  const single = (fhd >> 5) & 0x01;
  const checksum = (fhd >> 2) & 0x01;
  const didFlag = fhd & 0x03;
  const didSize = didFlag === 3 ? 4 : didFlag;
  const fcsSize = fcsFlag === 0 ? (single ? 1 : 0) : (fcsFlag === 1 ? 2 : fcsFlag === 2 ? 4 : 8);
  // Frame_Content_Size is the DECOMPRESSED size: slicing with it overshoots every compressed
  // frame (and lands short on nothing). The frame's real length comes from its block headers.
  let pos = at + 4 + 1 + (single ? 0 : 1) + didSize + fcsSize;
  for (;;) {
    if (pos + 3 > buf.length) return null;
    const bh = buf[pos] | (buf[pos + 1] << 8) | (buf[pos + 2] << 16);
    const last = bh & 1;
    const type = (bh >> 1) & 0x03;
    const blockSize = bh >> 3;
    pos += 3;
    pos += type === 1 ? 1 : blockSize;   // an RLE block carries a single byte
    if (pos > buf.length) return null;
    if (last) break;
  }
  if (checksum) pos += 4;
  return pos > buf.length ? null : pos - at;
}

function decodeFrames(buf) {
  // Returns { events, rest, skipped } where rest begins at the first frame that is not fully
  // on disk yet (an append still in progress) and skipped counts leading bytes that were not
  // a frame start (a rewritten log can begin mid-frame).
  const events = [];
  let badFrames = 0;
  let at = 0;
  while (at < buf.length && !isFrameAt(buf, at)) at++;
  const skipped = at;
  while (at < buf.length) {
    const total = frameLength(buf, at);
    if (total === null) {
      // An unsizable header means an append that is not finished yet: wait for the next poll.
      // Searching the payload for the next magic looked helpful and was a regression (review
      // round 2): a half-written frame can CONTAIN a magic, so the cursor jumped into the
      // payload and every later frame in the transcript was lost. Sized-but-undecodable
      // frames are handled below; reserved block types are sized and never reach this arm.
      break;
    }
    let text;
    try {
      text = zlib.zstdDecompressSync(buf.subarray(at, at + total)).toString('utf8');
    } catch (e) {
      // Sized but undecodable (a body that is not zstd): skip past it. Freezing here meant
      // every later frame in the transcript was lost for the rest of the run.
      badFrames++;
      at += total;
      continue;
    }
    events.push(text);
    at += total;
  }
  return { events, rest: buf.subarray(at), skipped: skipped, badFrames: badFrames };
}

function appendEvents(texts, sink) {
  let emitted = 0;
  let bad = 0;
  for (const text of texts) {
    for (const line of text.split('\n')) {
      if (!line.trim()) continue;
      let evt;
      try {
        evt = JSON.parse(line);
      } catch (e) {
        bad++;   // report it rather than let the text disappear inside a decoded frame
        continue;
      }
      const proj = project(evt);
      if (!proj) continue;
      sink(JSON.stringify(proj));
      emitted++;
    }
  }
  return { emitted: emitted, bad: bad };
}

/**
 * The tailer engine, with its IO and log discovery injectable so the offline self-test can
 * drive it over a fake sessions tree without touching the real ~/.dsh.
 */
function createTailer(options) {
  const opts = Object.assign({ poll: 400, once: false, fromBegin: false, backlog: 40 }, options && options.opts);
  const deps = Object.assign({}, (options && options.deps) || {});
  const fsx = deps.fs || fs;
  const sinkStdout = deps.stdout || ((s) => process.stdout.write(s));
  const nowFn = deps.now || (() => Date.now());
  const resolve = deps.resolveLog || resolveLog;

  let livePath = null;
  let statePath = null;
  let current = null;
  let offset = 0;
  let lastSeq = 0;
  let lastNoSessionKey = null;
  // The (offset, size, window) of an undecodable frame already reported, so a frozen cursor
  // is announced once instead of on every poll.
  let lastStuckKey = null;
  // Set when a strict pin cannot be honoured: the run must not continue (and must not report a
  // clean once-done exit) after the operator asked to fail loudly.
  let fatal = null;

  const write = (line) => {
    if (livePath) fsx.appendFileSync(livePath, line + '\n');
    sinkStdout(line + '\n');
  };

  const status = (phase, extra) => {
    const obj = Object.assign({ k: 'tailer', phase, at: nowFn() }, extra || {});
    write(JSON.stringify(obj));
  };

  /**
   * Emit the last N projected events of the transcript, newest last.
   *
   * Called AFTER the attach line on purpose: the panel clears its rows on "attached" (a
   * helper restart must not leave a dead transcript looking live), so a backlog written
   * before that line would be wiped the moment it was read.
   */
  const emitBacklog = (picked) => {
    const backlog = opts.fromBegin ? 0 : (opts.backlog | 0);
    if (backlog <= 0) return;
    try {
      const windowBytes = 400 * 1024;
      const from = Math.max(0, picked.size - windowBytes);
      const tail = fsx.readFileSync(picked.log).subarray(from);
      const decoded = decodeFrames(tail);
      if (decoded.skipped > 0) status('backlog-misaligned', { bytes: decoded.skipped });
      const all = [];
      for (const text of decoded.events) {
        for (const line of text.split('\n')) {
          if (!line.trim()) continue;
          let evt;
          try { evt = JSON.parse(line); } catch (e) { continue; }
          const proj = project(evt);
          if (proj) all.push(JSON.stringify({ ...proj, sessionId: picked.sessionId }));
        }
      }
      for (const line of all.slice(Math.max(0, all.length - backlog))) write(line);
    } catch (e) {
      // A missing backlog is not a failure: the link still attaches and stays live.
    }
  };

  const attach = (picked, replayAll) => {
    const source = picked.source || null;
    current = picked;
    current.source = source;
    // Start at the end of the file unless asked for history: the page shows what happens
    // from now on, and replaying a long session would flood it. A replay re-reads from byte 0
    // because the file was rewritten underneath us and its content is the conversation now.
    offset = replayAll || opts.fromBegin ? 0 : picked.size;
    lastSeq = 0;
    // A transcript deleted and recreated at the same path can come back LONGER than the old
    // cursor, so size alone cannot see the replacement: remember the head bytes and the mtime
    // this cursor belongs to and compare them on every poll.
    let headHash = null;
    let mtimeMs = null;
    try {
      const all = fsx.readFileSync(picked.log);
      headHash = all.subarray(0, 16).toString('hex');
      mtimeMs = fsx.statSync(picked.log).mtimeMs;
    } catch (e) { headHash = null; mtimeMs = null; }
    current.headHash = headHash;
    current.mtimeMs = mtimeMs;
    lastStuckKey = null;
    try {
      fsx.writeFileSync(
        statePath,
        JSON.stringify(
          {
            sessionId: picked.sessionId,
            workspaceSlug: picked.root,
            log: picked.log,
            source: source,
            startedAt: new Date(nowFn()).toISOString(),
            fromBegin: !!opts.fromBegin
          },
          null,
          2
        )
      );
    } catch (e) {
      // The panel reads tailer.state.json for the session name; failing to write it must not
      // stop the transcript, but it must be visible rather than silent.
      status('state-write-failed', { path: statePath, message: String(e && e.message ? e.message : e) });
    }
    status('attached', {
      sessionId: picked.sessionId,
      workspaceSlug: picked.root,
      size: picked.size,
      fromBegin: !!opts.fromBegin,
      source: source,
      replay: !!replayAll
    });
    // A replay emits the whole file through the normal path, so a backlog would duplicate it.
    if (!replayAll) emitBacklog(picked);
  };

  const tick = () => {
    const resolved = resolve(opts, deps);
    if (!resolved.ok) {
      // Repeat the same refusal only once per distinct reason: a 400 ms poll would otherwise
      // write 2 lines/second of identical noise into live.jsonl forever.
      const key = resolved.kind + '|' + (resolved.pinned || '') + '|' + (resolved.detail || '');
      if (key !== lastNoSessionKey) {
        lastNoSessionKey = key;
        status('no-session', {
          source: resolved.source || 'auto',
          reason: resolved.kind,
          sessionId: resolved.pinned || null,
          detail: resolved.detail || null,
          waiting: true
        });
        if (opts.strictPin && resolved.source !== 'auto' && !opts.gameWorld) {
          status('pin-unresolved', { reason: resolved.kind, sessionId: resolved.pinned || null });
          // deps.exit is what the offline self-test injects; the real CLI falls back to the
          // process exit that start_harness.ps1 already expects (exit code 3 = pin unresolved).
          // `fatal` keeps the run loop from emitting once-done/exit 0 afterwards: with an
          // injected exit (which returns) the code would otherwise overwrite 3 with 0.
          fatal = 3;
          if (deps.exit) return deps.exit(3);
          process.exit(3);
        }
      }
      return;
    }
    lastNoSessionKey = null;
    const picked = resolved.pick;
    // Carry the provenance on the entry itself so attach() can report it: the panel and the
    // operator both need to know whether this transcript was pinned or picked by recency.
    picked.source = resolved.source;
    if (!current || current.sessionId !== picked.sessionId) {
      const switched = !!current;
      // Order matters. The bridge treats both 'switched' and 'attached' as "start a new
      // transcript" and clears its rows for each, so 'switched' has to arrive FIRST; emitted
      // after attach() it wiped the backlog that attach() had just written, and the page showed
      // a single assistant line instead of the conversation.
      if (switched) status('switched', { sessionId: picked.sessionId, from: current.sessionId });
      attach(picked);
      return;
    }
    let st;
    try {
      st = fsx.statSync(picked.log);
    } catch (e) {
      status('log-vanished');
      return;
    }
    let headNow = null;
    try {
      headNow = fsx.readFileSync(picked.log).subarray(0, 16).toString('hex');
    } catch (e) { headNow = null; }
    if (current && current.headHash && headNow !== null && headNow !== current.headHash) {
      status('log-reset', { reason: 'transcript-replaced', previousOffset: offset, size: st.size, restart: true });
      attach(picked, true);
    }
    // Same length, different bytes: rewritten in place. Nothing may be delivered from it
    // again without the page rebuilding, so anchor on the mtime seen at attach time.
    if (current && st.size === offset && current.mtimeMs && st.mtimeMs !== current.mtimeMs && st.size > 0) {
      status('log-touched', {
        size: st.size,
        detail: 'the transcript was rewritten in place at the same length; re-reading from the top'
      });
      attach(picked, true);
    }
    if (current) current.mtimeMs = st.mtimeMs;
    if (st.size < offset) {
      // The transcript was rewritten (compaction, /clear, a new file at the same path). Parking
      // the cursor at the new end would keep the whole rewritten conversation invisible, so
      // re-attach: the page drops its dead rows and the file is decoded from byte 0 below.
      status('log-reset', { previousOffset: offset, size: st.size, restart: true });
      attach(picked, true);
    }
    if (st.size === offset) return;

    // Only bytes that are on disk: the cursor is rewound to the start of an incomplete frame,
    // so a carry would deliver that region twice and drive the offset negative.
    const buf = fsx.readFileSync(picked.log).subarray(offset, st.size);
    const res = decodeFrames(buf);
    if (res.skipped > 0) status('log-misaligned', { bytes: res.skipped });
    // A frame whose declared length parses but whose bytes do not decode (a reserved block
    // type, or a body that is not zstd) leaves `rest` equal to the whole window and the cursor
    // frozen. Silence there made the panel look simply dead, so name it once per window.
    const stuckKey = offset + ':' + buf.length;
    if (res.badFrames > 0) {
      if (stuckKey !== lastStuckKey) {
        lastStuckKey = stuckKey;
        status('frame-undecodable', {
          offset: offset, frames: res.badFrames,
          detail: 'a frame in this window could not be decoded and was stepped over; its text is lost, the rest of the transcript is not'
        });
      }
    } else {
      lastStuckKey = null;
    }
    if (res.rest.length === 0) {
      offset = st.size;
    } else {
      // Rewind to the start of the incomplete frame so the next poll re-reads it whole.
      offset = st.size - res.rest.length;
    }
    const counted = appendEvents(res.events, (line) => {
      let seq = 0;
      try {
        const event = JSON.parse(line);
        seq = event.seq || 0;
        line = JSON.stringify({ ...event, sessionId: picked.sessionId });
      } catch (e) { }
      if (seq) lastSeq = seq;
      write(line);
    });
    if (counted.bad > 0) status('bad-record', { lines: counted.bad });
    if (counted.emitted > 0) status('progress', { events: counted.emitted, lastSeq });
  };

  const run = () => {
    try {
      tick();
    } catch (e) {
      status('error', { message: String(e && e.message ? e.message : e) });
    }
    if (fatal !== null) return;      // pin-unresolved already asked for its exit code
    if (opts.once) {
      status('once-done', { lastSeq });
      if (deps.exit) return deps.exit(0);
      process.exit(0);
    }
    setTimeout(run, Math.max(120, opts.poll));
  };

  const start = () => {
    if (!opts.out) {
      process.stderr.write('missing --out <dir>\n');
      if (deps.exit) return deps.exit(2);
      process.exit(2);
    }
    try {
      fsx.mkdirSync(opts.out, { recursive: true });
    } catch (e) {
      process.stderr.write('cannot create ' + opts.out + ': ' + (e && e.message) + '\n');
      if (deps.exit) return deps.exit(2);
      process.exit(2);
    }
    livePath = path.join(opts.out, 'live.jsonl');
    statePath = path.join(opts.out, 'tailer.state.json');
    // Say up front whether this run will follow a pin or pick by recency: the operator should
    // not have to infer it from the first 'attached'. A pointer file that EXISTS but cannot be
    // read is a broken pin, not a licence to fall back to auto - only a missing file is auto.
    const startPtr = (deps.readPointer || readPointer)(opts.out);
    const startPin = opts.session
      ? { mode: 'pinned', pin: 'requested' }
      : (startPtr && startPtr.ok ? { mode: 'pinned', pin: 'pointer' }
        : (startPtr && startPtr.kind === 'no-pointer' ? { mode: 'auto', pin: null }
          : { mode: 'pinned', pin: 'unreadable', pinError: startPtr && startPtr.kind, pinDetail: startPtr && startPtr.detail }));
    status('start', {
      out: livePath,
      workspace: opts.workspace || null,
      session: opts.session || null,
      mode: startPin.mode,
      pin: startPin.pin,
      pinError: startPin.pinError || null,
      pinDetail: startPin.pinDetail || null
    });
    run();
  };

  return { tick, start, run, status, getCurrent: () => current, getOffset: () => offset };
}

function main() {
  const opts = parseArgs(process.argv);
  createTailer({ opts }).start();
}

if (require.main === module) main();

module.exports = {
  parseArgs,
  dshHome,
  slugHint,
  listSessionRoots,
  listSessionLogs,
  readPointer,
  resolveLog,
  isInjected,
  project,
  contentText,
  thoughtText,
  decodeFrames,
  appendEvents,
  createTailer,
  INJECTED_PREFIXES,
  INJECTED_PATTERNS
};
