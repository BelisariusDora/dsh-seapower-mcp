"""Read-only, cursor-based delivery of Bridge Watch events over MCP."""
import base64
import binascii
import hashlib
import json
import os
from pathlib import Path

from runtime_layout import deployment_root
MODULE_DIR = Path(__file__).resolve().parent
MOD_DIR = deployment_root()
OUTBOX = MOD_DIR / "comms" / "outbox.jsonl"
SCAN_LIMIT = 256 * 1024
LINE_LIMIT = 64 * 1024


def marker(stream, offset):
    info = os.fstat(stream.fileno())
    start = max(0, offset - 64)
    stream.seek(start)
    digest = hashlib.sha256(stream.read(offset - start)).hexdigest()[:24]
    return [info.st_dev, info.st_ino, offset, digest]


def encode(value):
    return base64.urlsafe_b64encode(json.dumps(value, separators=(",", ":")).encode()).decode()


def decode(cursor):
    value = json.loads(base64.urlsafe_b64decode(cursor))
    if value is None:
        return value
    if (not isinstance(value, list) or len(value) != 4 or
            any(type(x) is not int or x < 0 for x in value[:3]) or
            not isinstance(value[3], str)):
        raise ValueError("invalid outbox cursor")
    return value


def tail_cursor():
    try:
        with OUTBOX.open("rb") as stream:
            end = stream.seek(0, os.SEEK_END)
            start = max(0, end - LINE_LIMIT)
            stream.seek(start)
            tail = stream.read(end - start)
            newline = tail.rfind(b"\n")
            if newline < 0 and start:
                raise OSError("outbox tail exceeds the supported line length")
            return encode(marker(stream, start + newline + 1))
    except FileNotFoundError:
        return encode(None)


def read_events(args):
    cursor = args.get("cursor")
    if cursor is None:
        try:
            return {"ok": True, "data": {"cursor": tail_cursor(), "events": [], "more": False}}
        except OSError as exc:
            return {"ok": False, "error": {"code": "outbox_unavailable", "message": str(exc)}}
    try:
        if not isinstance(cursor, str) or len(cursor) > 256:
            raise ValueError("invalid outbox cursor")
        saved = decode(cursor)
        limit = args.get("limit", 32)
        if type(limit) is not int or not 1 <= limit <= 100:
            raise ValueError("limit must be an integer from 1 to 100")
    except (ValueError, UnicodeError, binascii.Error) as exc:
        return {"ok": False, "error": {"code": "bad_event_cursor", "message": str(exc)}}

    try:
        with OUTBOX.open("rb") as stream:
            size = os.fstat(stream.fileno()).st_size
            offset = saved[2] if saved else 0
            if saved and (offset > size or marker(stream, offset) != saved):
                return {"ok": False, "error": {"code": "outbox_reset",
                        "message": "Outbox was replaced or truncated. Reinitialize the cursor and verify active watches."}}
            stream.seek(offset)
            events = []
            seen = set()
            skipped = 0
            position = offset
            pending = False
            while position - offset < SCAN_LIMIT and len(events) < limit:
                line = stream.readline(LINE_LIMIT + 1)
                if not line:
                    break
                if len(line) > LINE_LIMIT:
                    return {"ok": False, "error": {"code": "outbox_line_too_large",
                            "message": "An outbox line exceeded the reader limit; cursor was not advanced."}}
                if not line.endswith(b"\n"):
                    pending = True
                    break
                position = stream.tell()
                try:
                    event = json.loads(line.decode("utf-8-sig"))
                except (ValueError, UnicodeError):
                    skipped += 1
                    continue
                if (not isinstance(event, dict) or event.get("k") != "event" or
                        event.get("from") != "trigger" or not isinstance(event.get("event_id"), str)):
                    continue
                if args.get("watch_id") is not None and event.get("watch_id") != args["watch_id"]:
                    continue
                if args.get("world_generation") is not None and str(event.get("world_generation")) != str(args["world_generation"]):
                    continue
                event_key = hashlib.sha256(line.rstrip(b"\r\n")).hexdigest()
                if event_key not in seen:
                    seen.add(event_key)
                    events.append({**event, "event_key": event_key})
            return {"ok": True, "data": {"cursor": encode(marker(stream, position)), "events": events,
                    "more": not pending and position < size, "pending_partial_line": pending,
                    "skipped_invalid_lines": skipped}}
    except FileNotFoundError:
        if saved is None:
            return {"ok": True, "data": {"cursor": cursor, "events": [], "more": False}}
        return {"ok": False, "error": {"code": "outbox_reset", "message": "Outbox disappeared; reinitialize the cursor."}}
    except OSError as exc:
        return {"ok": False, "error": {"code": "outbox_unavailable", "message": str(exc)}}
