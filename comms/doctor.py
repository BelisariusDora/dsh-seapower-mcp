"""Read-only diagnostics for a deployed Sea Power Harness installation."""
import argparse
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time
from urllib.request import ProxyHandler, Request, build_opener


def read_json(file):
    return json.loads(file.read_text(encoding="utf-8-sig"))


def require(condition, message):
    if not condition:
        raise ValueError(message)


def file_check(root, files):
    missing = [name for name in files if not (root / name).is_file() or not (root / name).stat().st_size]
    require(not missing, "Missing or empty files: " + ", ".join(missing))
    return f"{len(files)} required files present"


def prompt_check(file):
    text = file.read_text(encoding="utf-8-sig")
    require("禁止调用 ask_user_question" in text, "Text-only clarification rule missing; update this prompt")
    return "Text-only clarification rule present"


def mcp_check(root):
    request = {"jsonrpc": "2.0", "id": 1, "method": "tools/list", "params": {}}
    reply = subprocess.run(
        [sys.executable, "-B", str(root / "mcp_server.py")], input=json.dumps(request) + "\n",
        capture_output=True, text=True, encoding="utf-8", timeout=20, cwd=root,
    )
    require(reply.returncode == 0, "MCP entry failed; check Python dependencies and deployed modules")
    result = json.loads(reply.stdout)
    tools = {tool["name"] for tool in result.get("result", {}).get("tools", [])}
    expected = {"sp_ping", "sp_ticket_create", "sp_api_find", "sp_api_schema"}
    require(expected <= tools, "MCP tool catalog missing: " + ", ".join(sorted(expected - tools)))
    return f"MCP entry starts and advertises {len(tools)} tools; target Host injection still needs verification"


def cli_check(command, argument, expected):
    result = subprocess.run([*command, argument], capture_output=True, text=True, encoding="utf-8", timeout=15)
    require(result.returncode == 0 and expected in result.stdout, "CLI startup failed; check its interpreter and dependencies")
    return "CLI starts and recognizes " + expected


def node_check():
    node = shutil.which("node")
    require(node, "Node.js 24+ must be on the sender PATH")
    result = subprocess.run([node, "--version"], capture_output=True, text=True, timeout=5)
    match = re.fullmatch(r"v(\d+)\.\d+\.\d+", result.stdout.strip())
    require(result.returncode == 0 and match and int(match[1]) >= 24, "Node.js 24+ required for DSH transcript decompression")
    return result.stdout.strip() + "; sender and tailer must use this runtime"


def get_json(port, route, token=None):
    headers = {"Authorization": "Bearer " + token} if token else {}
    request = Request(f"http://127.0.0.1:{port}{route}", headers=headers)
    with build_opener(ProxyHandler({})).open(request, timeout=5) as response:
        data = response.read(1024 * 1024 + 1)
    require(len(data) <= 1024 * 1024, "Diagnostic response exceeds 1 MiB")
    return json.loads(data)


def bridge_check(port):
    reply = get_json(port, "/ping")
    require(reply.get("ok") is True, "Bridge ping failed")
    build = reply.get("data", {}).get("build", {})
    require(build.get("staleDll") is False, "Bridge DLL identity is missing or stale; reload the game after deployment")
    require(build.get("stamp") and build["stamp"] == build.get("onDiskStamp"), "Bridge build stamps differ or are missing")
    return "Loaded Bridge matches disk build " + build["stamp"]


def dsh_check(root, port):
    plugin = (root / "harness/DSH/dsh_ticket_plugin.mjs").read_text(encoding="utf-8-sig")
    revision = re.search(r'export const revision\s*=\s*"([^"]+)"', plugin)
    require(revision, "Installed DSH plugin has no revision")
    token = (root / "comms/run/tickets/auth.token").read_text(encoding="utf-8-sig").strip()
    require(len(token) >= 32, "Scheduler token missing or invalid; start the shared scheduler")
    loaded = get_json(port, "/health", token)
    expected = {"ok": True, "revision": revision[1], "ticketProtocol": 2,
                "apiTicketProtocol": 1, "taskResultProtocol": 1, "notificationProtocol": 1}
    for key, value in expected.items():
        require(loaded.get(key) == value, f"DSH {key} mismatch; check the active profile, loaded entry and Host reload")
    return "Loaded DSH plugin matches installed revision " + revision[1]


def recent_snapshot(file):
    record = read_json(file)
    age = time.time() - file.stat().st_mtime
    require(0 <= age <= 60, "Status file is older than 60 seconds or has a future timestamp; check its owning process")
    return record


def scheduler_check(root):
    record = recent_snapshot(root / "comms/run/tickets/view.json")
    require(record.get("runtime") == "python-tr1", "Unexpected scheduler runtime")
    require(record.get("available") is True and record.get("worldKey"), "Scheduler has no current game world")
    return "Recent scheduler snapshot with an available game world"


def dsh_sender_check(root):
    run = root / "comms/run"
    state = recent_snapshot(run / "link.status.json")
    require(state.get("mode") == "game-world", "Sender must run with --game-world")
    require(not state.get("paused") and not state.get("persistFault"), "Sender is paused or cannot persist delivery state")
    require(state.get("sessionVerified") is True and state.get("sessionId"), "Sender has no verified session; send a greeting in game")
    pointer = read_json(run / "session.pointer")
    world = recent_snapshot(run / "tickets/view.json").get("worldKey")
    require(world and pointer.get("source") == "game-world" and pointer.get("world") == world,
            "Session pointer is not bound to the current scheduler world")
    require(pointer.get("sessionId") == state["sessionId"], "Pointer and sender session differ")
    return "Recent game-world sender evidence and matching session pointer"


def codex_check(root):
    state = recent_snapshot(root / "comms/run/codex.chat.json")
    world = recent_snapshot(root / "comms/run/tickets/view.json").get("worldKey")
    require(state.get("link") == "attached" and state.get("sessionId"), "Codex has no attached game session; send a greeting in game")
    require(world and state.get("worldKey") == world, "Codex and scheduler worlds differ")
    require(not state.get("lastError"), "Codex reports an error; inspect its process log")
    return "Recent Codex snapshot attached to the scheduler world"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, help="Mod root; defaults to the parent of the installed harness directory")
    parser.add_argument("--backend", choices=("mcp", "dsh", "codex", "both"), default="both")
    parser.add_argument("--live", action="store_true", help="Also read local Bridge/DSH HTTP endpoints and runtime snapshots")
    parser.add_argument("--bridge-port", type=int, default=19080)
    parser.add_argument("--dsh-port", type=int, default=19389)
    parser.add_argument("--json", action="store_true", help="Print a JSON report")
    args = parser.parse_args()
    for port in (args.bridge_port, args.dsh_port):
        if not 1 <= port <= 65535:
            parser.error("Ports must be between 1 and 65535")
    root = args.root.resolve() if args.root else Path(__file__).resolve().parent.parent
    checks = []

    def check(name, action=None, pending=None):
        status, detail = "NOT_CHECKED", pending
        if action:
            try:
                detail = action()
                status = "PASS"
            except Exception as error:
                status = "FAIL"
                # Do not print HTTP bodies, command output or local credentials.
                detail = str(error) if isinstance(error, ValueError) else type(error).__name__ + "; inspect the component path, dependencies or local endpoint"
        checks.append({"check": name, "status": status, "detail": detail})

    check("python", lambda: (require(sys.version_info >= (3, 11), "Python 3.11+ required") or sys.version.split()[0]))
    check("mcp.files", lambda: file_check(root, ["mcp_server.py", "modules/runtime_layout.py",
          "modules/mcp/mcp_server.py", "modules/mcp/track_mlp_weights.json", "modules/api/api_catalog.py", "modules/api/api_tools.json"]))
    check("mcp.catalog", lambda: mcp_check(root))
    if args.backend != "mcp":
        check("tickets.files", lambda: file_check(root, ["ticket_scheduler.py", "modules/tickets/ticket_scheduler.py",
              "modules/tickets/ticket_store.py", "modules/tickets/ticket_main.py", "modules/api/ticket_api.py"]))
        check("scheduler.cli", lambda: cli_check([sys.executable, "-B", str(root / "ticket_scheduler.py")], "--help", "--executor"))
        check("persona", lambda: prompt_check(root / "persona.md"))
    for backend in ("dsh", "codex"):
        if args.backend not in (backend, "both"):
            continue
        folder = "DSH" if backend == "dsh" else "Codex"
        files = ["harness/DSH/harness_link.js", "harness/DSH/dsh_tailer.js", "harness/DSH/dsh_ticket_plugin.mjs",
                 "harness/DSH/dsh_ticket_notifications.mjs", "harness/DSH/ticket_executor_dsh.py"] if backend == "dsh" else [
                 "harness/Codex/codex_chat.py", "harness/Codex/ticket_executor_codex.py"]
        check(backend + ".files", lambda files=files: file_check(root, files))
        check(backend + ".prompt", lambda folder=folder: prompt_check(root / "harness" / folder / "prompts/onceopen.md"))
        if backend == "dsh":
            check("dsh.node", node_check)
        else:
            check("codex.chat_cli", lambda: cli_check([sys.executable, "-B", str(root / "harness/Codex/codex_chat.py")], "--help", "--codex"))
            check("codex.worker_cli", lambda: (require(shutil.which("codex"), "Codex CLI must be on the scheduler PATH") or "Codex executable found on this process PATH; verify the scheduler inherits it"))
    runtime = [("bridge.identity", lambda: bridge_check(args.bridge_port))]
    if args.backend != "mcp":
        runtime.append(("scheduler.snapshot", lambda: scheduler_check(root)))
    if args.backend in ("dsh", "both"):
        runtime.extend([("dsh.host", lambda: dsh_check(root, args.dsh_port)), ("dsh.sender", lambda: dsh_sender_check(root))])
    if args.backend in ("codex", "both"):
        runtime.append(("codex.session", lambda: codex_check(root)))
    for name, action in runtime:
        check(name, action if args.live else None, "Run again with --live after starting the selected components and loading a game world")
    check("session.tools", pending="Verify sp_* tools in the actual target Harness session and its scoped permissions; file and health checks cannot prove injection")
    check("game.roundtrip", pending="In game, send a greeting and one explicitly read-only fleet query; verify the real reply and API ledger")
    failed = any(row["status"] == "FAIL" for row in checks)
    code = 1 if failed else 2
    report = {"root": str(root), "backend": args.backend, "checks": checks,
              "installationStatus": "FAILED" if failed else "NOT_FULLY_VERIFIED", "exitCode": code}
    if args.json:
        print(json.dumps(report, ensure_ascii=True))
    else:
        print(f"Sea Power Harness doctor | {args.backend} | {root}")
        for row in checks:
            print(f"[{row['status']}] {row['check']}: {row['detail']}")
        print(f"Installation: {report['installationStatus']} (exit {code})")
    return code


if __name__ == "__main__":
    raise SystemExit(main())
