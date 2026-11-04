#!/usr/bin/env node
'use strict';

const fs = require('node:fs');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { setTimeout: sleep } = require('node:timers/promises');
const { parseArgs } = require('node:util');

const commsDir = path.basename(path.dirname(__dirname)).toLowerCase() === 'harness'
  ? path.resolve(__dirname, '../../comms')
  : path.resolve(__dirname, '../../../Sea Power_Data/StreamingAssets/dsh-spmcp/comms');

const options = {
  thread: { type: 'string' },
  outbox: { type: 'string' },
  'chat-title': { type: 'string' },
  'composer-name': { type: 'string' },
  'send-name': { type: 'string' },
  check: { type: 'boolean' },
};

function desktop(config, message) {
  const args = ['-NoProfile', '-STA', '-File', path.join(__dirname, 'codex_desktop.ps1'),
    '-ChatTitle', config['chat-title']];
  if (config['composer-name']) args.push('-ComposerName', config['composer-name']);
  if (config['send-name']) args.push('-SendName', config['send-name']);
  if (config.check) args.push('-Probe');
  return new Promise((resolve, reject) => {
    const child = spawn('pwsh.exe', args, { stdio: ['pipe', 'inherit', 'inherit'], windowsHide: true });
    const timeout = setTimeout(() => child.kill(), 15000);
    child.once('error', (error) => { clearTimeout(timeout); reject(error); });
    child.once('close', (code) => {
      clearTimeout(timeout);
      resolve(code);
    });
    child.stdin.on('error', (error) => {
      if (error.code !== 'EPIPE') reject(error);
    });
    child.stdin.end(message, 'utf8');
  });
}
function playerMessage(line) {
  let row;
  try { row = JSON.parse(line); } catch { return null; }
  if (row?.from !== 'player' || (row.k && row.k !== 'dialogue')) return null;
  return typeof row.text === 'string' && row.text.trim() ? row.text : null;
}

async function forward(config, threadId) {
  const outbox = path.resolve(config.outbox || path.join(commsDir, 'codex.outbox.jsonl'));
  const cursorFile = outbox + '.cursor.json';
  // A second writer must never use the same cursor. A crash leaves the lock for inspection.
  const lock = fs.openSync(cursorFile + '.lock', 'wx');
  try {
    let cursor = null;
    if (fs.existsSync(cursorFile)) {
      const journal = fs.readFileSync(cursorFile, 'utf8');
      if (!journal.endsWith('\n')) throw new Error('Incomplete cursor journal: ' + cursorFile);
      cursor = JSON.parse(journal.trimEnd().split('\n').at(-1));
      if (cursor.threadId !== threadId) throw new Error('Queue is already bound to ' + cursor.threadId);
    }

    function save(next) {
      next = { ...next, threadId };
      fs.appendFileSync(cursorFile, JSON.stringify(next) + '\n', { flush: true });
      cursor = next;
    }

    if (cursor?.pending) {
      throw new Error('Previous delivery may already have reached Codex. Inspect ' + cursorFile);
    }

    while (!stopping) {

      let fd;
      try { fd = fs.openSync(outbox, 'r'); } catch (error) {
        if (error.code !== 'ENOENT') throw error;
      }
      try {
        if (fd !== undefined) {
          const stat = fs.fstatSync(fd);
          const identity = stat.birthtimeMs + ':' + stat.ino;
          if (!cursor) {
            save({ identity, offset: 0 });
          } else if (cursor.identity !== identity || stat.size < cursor.offset) {
            save({ identity, offset: 0 });
          }

          if (stat.size > cursor.offset) {
            const chunk = Buffer.alloc(Math.min(stat.size - cursor.offset, 1024 * 1024));
            const length = fs.readSync(fd, chunk, 0, chunk.length, cursor.offset);
            const end = chunk.subarray(0, length).indexOf(10);
            if (end === -1 && length === chunk.length && length === 1024 * 1024) {
              throw new Error('Outbox line exceeds 1 MiB at byte ' + cursor.offset);
            }
            if (end !== -1) {
              const next = { identity, offset: cursor.offset + end + 1 };
              const message = playerMessage(chunk.subarray(0, end).toString('utf8'));
              if (!message) {
                save(next);
                continue;
              }

              save({ ...cursor, pending: next.offset });
              const outcome = await desktop(config, message);
              if (outcome === 0) {
                save(next);
                console.log('Submitted through Codex desktop: ' + config['chat-title']);
                continue;
              }
              if (outcome !== 2) {
                throw new Error('Desktop submission uncertain. Inspect the chat and ' + cursorFile);
              }
              const { pending, ...retry } = cursor;
              save(retry);
              await sleep(1500);
            }
          }
        } else if (!cursor) {
          save({ identity: null, offset: 0 });
        }
      } finally {
        if (fd !== undefined) fs.closeSync(fd);
      }
      await sleep(500);
    }
  } finally {
    fs.closeSync(lock);
    fs.unlinkSync(cursorFile + '.lock');
  }
}

let stopping = false;
process.on('SIGINT', () => { stopping = true; });
process.on('SIGTERM', () => { stopping = true; });

async function main() {
  const { values } = parseArgs({ options });
  const binding = path.join(commsDir, 'codex.target.json');
  const target = fs.existsSync(binding) ? JSON.parse(fs.readFileSync(binding, 'utf8').replace(/^\uFEFF/, '')) : {};
  const threadId = values.thread || process.env.CODEX_THREAD_ID || target.thread;
  if (!values['chat-title'] && target.thread === threadId) values['chat-title'] = target.title;
  if (!threadId || !/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(threadId)) {
    throw new Error('Run in the target Codex chat terminal or pass --thread <chat UUID>');
  }

  if (!values['chat-title']) throw new Error('Pass --chat-title with the exact title of the intended chat');
  if (values.check) {
    const result = await desktop(values, '');
    if (result === 2) throw new Error('Bring the Codex desktop window to the foreground to inspect it');
    if (result !== 0) throw new Error('Desktop probe failed (' + result + ')');
    return;
  }
  await forward(values, threadId);
}

main().catch((error) => {
  console.error('codex_link: ' + error.message);
  process.exitCode = 1;
});
