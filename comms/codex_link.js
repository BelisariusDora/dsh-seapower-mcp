#!/usr/bin/env node
// Game-scoped owned Codex chat. Desktop UI sender retained under legacy/.
const {spawn} = require('node:child_process');
const path = require('node:path');
const args = process.argv.slice(2);
let python = process.env.SPMCP_PYTHON || 'python';
const index = args.indexOf('--python');
if (index >= 0) {
  if (!args[index + 1]) throw new Error('--python requires an executable');
  python = args[index + 1]; args.splice(index, 2);
}
const child = spawn(python, [path.join(__dirname, 'codex_chat.py'), ...args], {stdio:'inherit', windowsHide:true});
child.on('error', error => { console.error(error.message); process.exitCode = 1; });
child.on('exit', code => { process.exitCode = code === null ? 1 : code; });
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => child.kill(signal));
