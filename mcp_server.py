#!/usr/bin/env python3
"""MCP server for Sea Power.

Speaks MCP over stdio (newline-delimited JSON-RPC) and forwards everything to
the in-game bridge mod listening on 127.0.0.1:19080.

Run:  python tools/spmcp_v2/mcp_server.py
Env:  SPMCP_PORT (default 19080), SPMCP_TIMEOUT (default 25 s -- the floor for
      every route, not the cap), SPMCP_SHOT_TIMEOUT (frame wait, 15 s)

Deployed by build.ps1 with the v2 bridge. The transport and
tool mappings preserve bridge replies for the caller. This direct adapter is
intended for development and integration before the policy layer is connected.
"""

import base64
import decimal
import http.client
import json
import os
import socket
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from urllib.parse import quote

import compact_replies
import watch_events
from track_briefs import TrackBriefs
from track_motion import TrackMotion

# Requests target a loopback game bridge, including on hosts with a system proxy.
BRIDGE_OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))

# The MCP wire format is UTF-8 JSON. On Windows sys.stdout defaults to the ANSI
# code page (cp936 on a Chinese install), so any non-ASCII character -- game unit
# names, Chinese error strings from the bridge -- would be written in the wrong
# encoding and break the host's reader. Force UTF-8 before anything else runs.
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace", newline="\n")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace", newline="\n")
except Exception:
    pass

PORT = int(os.environ.get("SPMCP_PORT", "19080"))

# TRANSPORT BUDGETS (S03) ---------------------------------------------------
# The bridge enforces one wait budget per route (src/Api.cs DeadlineMs, applied
# once around the main-thread hop). That number is "how long the main thread may
# legitimately take", NOT "how long the reply will take": /plot/draw can spend
# ~45 s on a cold plot table. A client budget at or below the bridge's turns
# "the main thread is still working" into "bridge unreachable" -- and, worse,
# into a failure the caller may retry. So every value below is deliberately
# LARGER than the matching deadline. If DeadlineMs changes, change this table.
#
#   route                       bridge ms   here s
#   plot draw|drawings|erase      45000        50
#   plot course                   30000        35
#   plot clear|ruler|other        15000-20000  30
#   group                         25000        30
#   shot, shot/png                20000        25 (never below 25)
#   everything else               4000-20000   25
# Every number is also floored at TIMEOUT, so raising SPMCP_TIMEOUT can only widen
# these budgets. Lowering it is clamped at 25 for the default bucket, because the
# invariant that matters is "client budget > bridge deadline" (the previous default
# was 20, and tests/test_shot_offline.py runs with SPMCP_TIMEOUT=10).
TIMEOUT_FLOOR = 25.0
TIMEOUT = float(os.environ.get("SPMCP_TIMEOUT", "25"))
# Not a socket budget: how long the screenshot tool polls for a frame to appear.
SHOT_TIMEOUT = float(os.environ.get("SPMCP_SHOT_TIMEOUT", "15"))


def timeout_for(path):
    """Client socket budget for one bridge path, in seconds (S03).

    Kept in sync with service/bridge_client.py and with src/Api.cs DeadlineMs.
    """
    seg = [s for s in path.split("?", 1)[0].split("/") if s]
    head = seg[0].lower() if seg else ""
    op = seg[1].lower() if len(seg) > 1 else ""
    if head == "plot":
        if op in ("draw", "drawings", "erase"):
            return max(50.0, TIMEOUT)
        if op == "course":
            return max(35.0, TIMEOUT)
        return max(30.0, TIMEOUT)
    if head == "group":
        return max(30.0, TIMEOUT)
    if head == "shot":
        return max(TIMEOUT_FLOOR, TIMEOUT)
    return max(TIMEOUT_FLOOR, TIMEOUT)


SERVER_NAME = "seapower"
SERVER_VERSION = "0.1.0"
PROTOCOL_VERSION = "2024-11-05"


def log(msg):
    sys.stderr.write("[spmcp] " + str(msg) + "\n")
    sys.stderr.flush()


def _uncertain_payload(path, detail, method="POST"):
    """A request that may already have reached the main thread (S09 at the tool layer).

    "No reply" is not "nothing happened": the request can be sitting in the main-thread
    queue while the socket gives up. The model must verify with a read tool instead of
    sending a write twice.

    A read is the exception: asking again cannot change the world, so a lost reply to a GET
    is just a lost reply. Saying "uncertain" there would teach a caller not to retry a
    question it may simply ask again.
    """
    if method.upper() in ("GET", "HEAD"):
        return {
            "ok": False,
            "resend": "allowed",
            "path": path,
            "error": "no reply: %s" % (detail,),
            "hint": "this was a read; asking again cannot change anything",
        }
    return {
        "ok": False,
        "uncertain": True,
        "resend": "forbidden",
        "path": path,
        "error": "no confirmed reply: %s" % (detail,),
        "hint": "the game may have applied this already; check with a read tool before sending it again",
    }


def _mark_partial_batch(result):
    """A 200 that says ok:false with failed>0 is a HALF-EXECUTED batch (S09).

    ``src/Api.cs`` answers a batch that ran some of its items with 200 + ok:false +
    ``data.failed > 0``. The interactive layer already reads that as "uncertain"; the
    tool face used to pass it through as a plain refusal, which tells the model nothing
    happened -- and some of the salvo did. Mark it instead.
    """
    if not isinstance(result, dict) or result.get("ok") is not False:
        return result
    data = result.get("data")
    failed = 0
    if isinstance(data, dict):
        try:
            failed = int(data.get("failed") or 0)
        except (TypeError, ValueError):
            failed = 0
    if failed <= 0:
        return result
    result["uncertain"] = True
    result["partial"] = failed
    result["resend"] = "forbidden"
    result.setdefault("hint", "the game may have applied some of this already; "
                              "check with a read tool before sending it again")
    return result


def _mark_error_outcome(result, http_status):
    """Carry the bridge's own retry verdict through to the model (S09).

    A 5xx means the main thread had already been handed the work, so whether it took
    effect is exactly what `error.retryable` answers. When a 5xx does not say, treat it as
    uncertain: silence about a half-executed command is the failure this round is about.
    4xx is settled -- the bridge refused before doing anything.
    """
    if http_status is None:
        return result
    err = result.get("error")
    retryable = err.get("retryable") if isinstance(err, dict) else None
    if http_status < 500:
        # Refused before the game did anything, so the only question left is whether the
        # same request could help -- which is exactly what the bridge's retryable answers.
        if retryable is True:
            result["resend"] = "allowed"
        return result
    if retryable is True:
        result["resend"] = "allowed"
        return result
    result["uncertain"] = True
    result["resend"] = "forbidden"
    if "hint" not in result:
        result["hint"] = "the game may have applied this already; check with a read tool before sending it again"
    return result


def call(method, path, body=None):
    url = "http://127.0.0.1:%d%s" % (PORT, path)
    data = None
    if body is not None:
        data = json.dumps(body).encode("utf-8")
    req = urllib.request.Request(url, data=data, method=method)
    if data is not None:
        req.add_header("Content-Type", "application/json")
    try:
        with BRIDGE_OPENER.open(req, timeout=timeout_for(path)) as resp:
            return _mark_partial_batch(json.loads(resp.read().decode("utf-8")))
    except urllib.error.HTTPError as e:
        raw = e.read().decode("utf-8", "replace")
        # The bridge answers 4xx/5xx with {"ok":false,"error":{code,message,retryable}}.
        # Surface that structure instead of collapsing it to a bare HTTP status, and let
        # the retry verdict survive: a 504 is not the same news as a 400.
        try:
            parsed = json.loads(raw)
            if isinstance(parsed, dict) and "ok" in parsed:
                parsed["http_status"] = e.code
                return _mark_error_outcome(parsed, e.code)
        except Exception:
            pass
        return _mark_error_outcome(
            {"ok": False, "error": "HTTP %s" % (e.code,), "body": raw}, e.code)
    except (TimeoutError, socket.timeout) as e:
        return _uncertain_payload(path, "waited the whole route budget (%s)" % (e,), method)
    except urllib.error.URLError as e:
        reason = getattr(e, "reason", e)
        if isinstance(reason, (ConnectionRefusedError, FileNotFoundError)):
            # Nothing was delivered: a refused connection happens before the request lands.
            return {
                "ok": False,
                "sent": False,
                "resend": "allowed",
                "error": "bridge unreachable: %s" % (e,),
                "hint": "Start Sea Power and make sure the 'dsh-spmcp-v2' mod is enabled in the AnchorChain mod menu.",
            }
        return _uncertain_payload(path, "the connection failed mid-request: %s" % (e,), method)
    except (ConnectionError, http.client.HTTPException) as e:
        return _uncertain_payload(path, "the connection dropped before a reply: %s" % (e,), method)
    except Exception as e:  # noqa: BLE001
        return _uncertain_payload(path, "the call failed after the request was built: %s" % (e,), method)


def call_raw(method, path, timeout=None):
    """Fetch a non-JSON body (the screenshot PNG) and return (status, bytes).

    Kept separate from call() on purpose: an image must not travel as JSON, because
    anything inside a JSON field reaches the model as text. Returns status 0 when the
    bridge could not be reached at all, so the caller can tell "no server" apart from
    "server said no".
    """
    url = "http://127.0.0.1:%d%s" % (PORT, path)
    req = urllib.request.Request(url, method=method)
    try:
        # An explicit timeout may WIDEN the socket budget, never narrow it: the route floor
        # is the bridge's own deadline plus headroom, and going under it turns "waiting"
        # into a client-side failure that looks like an unresponsive game.
        floor = timeout_for(path)
        budget = floor if timeout is None else max(float(timeout), floor)
        with BRIDGE_OPENER.open(req, timeout=budget) as resp:
            return resp.getcode(), resp.read()
    except urllib.error.HTTPError as e:
        return e.code, e.read()
    except Exception as e:  # noqa: BLE001
        log("call_raw %s %s failed: %r" % (method, path, e))
        return 0, b""


# --------------------------------------------------------------------- tools


def t_state(args):
    return call("GET", "/state")

def t_session(args):
    return call("GET", "/session")


def t_session_fleet(args):
    # Own fleet of the world that is loaded right now. Keyed on the world generation, so an
    # id from the previous scenario is not reused.
    return call("GET", "/session/fleet")


def t_ping(args):
    return call("GET", "/ping")


def t_units(args):
    q = []
    for key in ("side", "kind"):
        if args.get(key):
            q.append("%s=%s" % (key, args[key]))
    if args.get("include_non_units"):
        q.append("all=1")
    path = "/units"
    if q:
        path += "?" + "&".join(q)
    return call("GET", path)


def t_unit(args):
    return call("GET", "/units/%d" % int(args["unit_id"]))


def t_ammo(args):
    return call("GET", "/units/%d/ammo" % int(args["unit_id"]))


def t_waypoints(args):
    return call("GET", "/units/%d/waypoints" % int(args["unit_id"]))


def t_taskforces(args):
    return call("GET", "/taskforces")


def t_time(args):
    body = {}
    if "paused" in args and args["paused"] is not None:
        body["paused"] = bool(args["paused"])
    if "compression" in args and args["compression"] is not None:
        body["compression"] = float(args["compression"])
    return call("POST", "/time", body)


def _strict_bool(args, name):
    """A boolean field, refused rather than coerced.

    bool("false") is True and bool("off") is True, so the old bool(arg) call turned a
    type error into the OPPOSITE order for five fields (formation, sync_flag, message,
    formation_attack, attack_only_detected). The C# side only accepts a JSON boolean and
    would have answered 400 bad_type; the Python layer is the only schema check a model
    ever meets, so it has to be the one that refuses.
    """
    value = args[name]
    if not isinstance(value, bool):
        raise ValueError("%s must be true or false, got %r" % (name, value))
    return value


def _unit_op(op, args, extra=None):
    body = dict(extra or {})
    return call("POST", "/units/%d/%s" % (int(args["unit_id"]), op), body)


def t_land(args):
    part = args.get("part") or "summary"
    return call("GET", "/units/%d/land?part=%s"
                % (int(args["unit_id"]), quote(part, safe="")), None)


def t_fire(args):
    body = {"lat": float(args["lat"]), "lon": float(args["lon"])}
    if args.get("ammo"):
        body["ammo"] = args["ammo"]
    if args.get("shots") is not None:
        body["shots"] = int(args["shots"])
    if args.get("priority") is not None:
        body["priority"] = int(args["priority"])
    if args.get("validate_only"):
        body["validate_only"] = True
    return _unit_op("fire", args, body)


def t_comms_state(args):
    return call("GET", "/comms/state", None)


def t_comms_page(args):
    since = int(args.get("since") or 0)
    limit = int(args.get("limit") or 200)
    return call("GET", "/comms/page?since=%d&limit=%d" % (since, limit), None)


def t_comms_send(args):
    return call("POST", "/comms/send", {"text": args["text"]})


def t_comms_window(args):
    op = args.get("op") or "state"
    if op == "state":
        return call("GET", "/comms/window", None)
    body = {"op": op}
    if args.get("on") is not None:
        body["on"] = _strict_bool(args, "on")
    return call("POST", "/comms/window", body)



def t_weapon_status(args):
    body = {"status": args["status"]}
    if args.get("formation") is not None:
        body["formation"] = _strict_bool(args, "formation")
    if args.get("validate_only"):
        body["validate_only"] = True
    return _unit_op("weapon_status", args, body)


def t_telegraph(args):
    body = {"telegraph": int(args["telegraph"])}
    if args.get("validate_only"):
        body["validate_only"] = True
    return _unit_op("telegraph", args, body)


def t_rudder(args):
    body = {"angle": float(args["angle"])}
    if args.get("validate_only"):
        body["validate_only"] = True
    return _unit_op("rudder", args, body)


def t_heading(args):
    body = {"heading": float(args["heading"])}
    if args.get("max_rudder") is not None:
        body["max_rudder"] = float(args["max_rudder"])
    if args.get("smoothness") is not None:
        body["smoothness"] = float(args["smoothness"])
    if args.get("validate_only"):
        body["validate_only"] = True
    return _unit_op("heading", args, body)


def t_radars(args):
    body = {"role": args["role"], "state": args["state"]}
    if args.get("formation") is not None:
        body["formation"] = _strict_bool(args, "formation")
    if args.get("sync_flag") is not None:
        body["sync_flag"] = _strict_bool(args, "sync_flag")
    if args.get("validate_only"):
        body["validate_only"] = True
    return _unit_op("radars", args, body)


def t_countermeasures(args):
    # kind defaults to "both" in Weapons.cs (ReqStr(.., "kind", "both")); the schema has
    # always said "Default both", so sending it unconditionally was the handler disagreeing
    # with its own contract. Omitted means omitted - the default belongs on one side only.
    body = {}
    if args.get("kind"):
        body["kind"] = args["kind"]
    if args.get("ammo"):
        body["ammo"] = args["ammo"]
    if args.get("message") is not None:
        body["message"] = _strict_bool(args, "message")
    if args.get("validate_only"):
        body["validate_only"] = True
    return _unit_op("countermeasures", args, body)


def t_attack_waypoint(args):
    body = {"ammo": args["ammo"], "lat": float(args["lat"]), "lon": float(args["lon"])}
    for key in ("waypoint_lat", "waypoint_lon", "area_radius"):
        if args.get(key) is not None:
            body[key] = float(args[key])
    if args.get("target_unit") is not None:
        body["target_unit"] = int(args["target_unit"])
    if args.get("salvo") is not None:
        body["salvo"] = int(args["salvo"])
    if args.get("salvo_type"):
        body["salvo_type"] = args["salvo_type"]
    for key in ("formation_attack", "attack_only_detected", "validate_only"):
        if args.get(key) is not None:
            body[key] = _strict_bool(args, key)
    return _unit_op("attack_waypoint", args, body)


def t_speed(args):
    return _unit_op("speed", args, {"knots": float(args["knots"])})


def t_course(args):
    extra = {"degrees": float(args["degrees"])}
    if args.get("height_state"):
        extra["height_state"] = args["height_state"]
    return _unit_op("course", args, extra)


def t_move(args):
    extra = {"lat": float(args["lat"]), "lon": float(args["lon"])}
    if args.get("height_state"):
        extra["height_state"] = args["height_state"]
    return _unit_op("move", args, extra)


def t_vertical_read(args):
    return call("GET", "/units/%d/vertical" % int(args["unit_id"]), None)


def t_vertical(args):
    body = {"mode": args["mode"]}
    if args.get("meters") is not None:
        body["meters"] = float(args["meters"])
    if args.get("preset") is not None:
        body["preset"] = int(args["preset"])
    if args.get("update_waypoints"):
        body["update_waypoints"] = True
    if args.get("index") is not None:
        body["index"] = int(args["index"])
    if args.get("allow_beyond_measured"):
        body["allow_beyond_measured"] = True
    return call("POST", "/units/%d/vertical" % int(args["unit_id"]), body)


def t_attack(args):
    body = {"target": int(args["target_id"])}
    if args.get("ammo"):
        body["ammo"] = args["ammo"]
    # The schema promised shots but this handler never sent it, so a caller asking for a
    # four-missile salvo still got exactly one missile.
    if args.get("shots") is not None:
        body["shots"] = int(args["shots"])
    if args.get("auto_ammo"):
        body["auto_ammo"] = True
    if args.get("validate_only"):
        body["validate_only"] = True
    if args.get("allow_neutral"):
        body["allow_neutral"] = True
    if args.get("force"):
        body["force"] = True
    return _unit_op("attack", args, body)


def t_validate_attack(args):
    body = {"target": int(args["target_id"]), "validate_only": True}
    if args.get("ammo"):
        body["ammo"] = args["ammo"]
    if args.get("auto_ammo"):
        body["auto_ammo"] = True
    if args.get("allow_neutral"):
        body["allow_neutral"] = True
    if args.get("force"):
        body["force"] = True
    return _unit_op("attack", args, body)


def t_tracks(args):
    return call("GET", "/tracks", None)


TRACK_BRIEFS = TrackBriefs()
TRACK_MOTION = TrackMotion()


def t_tracks_brief(args):
    def session_generation(reply):
        if reply.get("ok") is not True:
            return None
        data = reply.get("data") or {}
        current = data.get("current") or {}
        return data.get("worldGeneration") if current.get("sceneName") == "mission" else None

    generation = session_generation(t_session({}))
    if generation is None:
        return {"ok": False, "error": "No verified mission session; use sp_session to inspect."}
    tracks = t_tracks({})
    if tracks.get("ok") is not True:
        return tracks
    if session_generation(t_session({})) != generation:
        return {"ok": False, "error": "Mission changed or could not be confirmed during track read; retry."}
    return TRACK_BRIEFS.build(TRACK_MOTION.annotate(tracks, generation), generation)


def t_weapons(args):
    return call("GET", "/units/%d/weapons" % int(args["unit_id"]), None)


def t_deck(args):
    return call("GET", "/units/%d/deck" % int(args["unit_id"]), None)


def t_sub(args):
    # T19.4: read-only submarine systems view. There is deliberately no write counterpart -
    # depth/periscope/preset commands go through sp_vertical, and mast writes are not exposed.
    return call("GET", "/units/%d/sub" % int(args["unit_id"]), None)


def t_identify(args):
    return _unit_op("identify", args, {"target": int(args["target_id"])})


def t_rtb(args):
    return _unit_op("rtb", args)


def t_ceasefire(args):
    extra = {}
    for key in ("report", "clear_order", "formation_check", "stop_jamming",
                "air_formation_check", "clear_attack_tasks"):
        if args.get(key) is not None:
            extra[key] = bool(args[key])
    return _unit_op("ceasefire", args, extra)


def t_emcon(args):
    return _unit_op("emcon", args, {"on": bool(args["on"])})


def t_radar(args):
    return _unit_op("radar", args, {"on": bool(args["on"])})


def t_sonar(args):
    return _unit_op("sonar", args, {"on": bool(args["on"])})


def t_launch(args):
    # T19.5: 'aircraft' used to be advertised and then dropped here, exactly like the R1.1 class
    # of bug. Forward every field the bridge understands, and let the bridge do the validating.
    body = {}
    for key in ("aircraft", "loadout", "squadron", "mission"):
        if args.get(key):
            body[key] = args[key]
    if args.get("require_exact_loadout"):
        body["require_exact_loadout"] = True
    if args.get("launch_count") is not None:
        body["launch_count"] = int(args["launch_count"])
    return call("POST", "/units/%d/launch" % int(args["unit_id"]), body)


def t_airborne(args):
    return call("GET", "/units/%d/airborne" % int(args["unit_id"]), None)


def t_decktasks(args):
    return call("GET", "/units/%d/decktasks" % int(args["unit_id"]), None)


def t_recover(args):
    body = {}
    if args.get("all"):
        body["all"] = True
    else:
        body["aircraft"] = int(args["aircraft"])
        # B25: createReturnTask has been observed to answer 200 and queue nothing, while
        # RecoverAll raised a ReturnTask for the whole airgroup. Opting into that wider
        # action is the caller's decision, so the flag is passed through and never implied.
        if args.get("fallback_all"):
            body["fallback_all"] = True
    return call("POST", "/units/%d/recover" % int(args["unit_id"]), body)


def t_abort(args):
    body = {}
    if args.get("task"):
        body["task"] = args["task"]
    if args.get("all"):
        body["all"] = True
    return call("POST", "/units/%d/abort" % int(args["unit_id"]), body)



def t_rearm(args):
    extra = {}
    if args.get("reload_broken") is not None:
        extra["reload_broken"] = bool(args["reload_broken"])
    return _unit_op("rearm", args, extra)


def t_clear_waypoints(args):
    return _unit_op("waypointClear", args)


def t_screenshot(args):
    """One picture of the frame the player is actually looking at.

    Three separate claims have to hold for this to be useful, and they are reported
    separately rather than blurred into one "success": the game produced a picture, the
    bridge handed the bytes to this process, and the bytes reach the model as an image
    content block. If any step fails the caller gets an error naming that step instead of
    a file path pretending to be a picture.
    """
    body = {"op": "take"}
    if args.get("full"):
        body["full"] = True
    if args.get("save"):
        body["save"] = True

    started = call("POST", "/shot", body)
    if not isinstance(started, dict):
        return {"ok": False, "error": {"code": "shot_failed", "message": "bridge returned no reply", "retryable": True}}
    if started.get("ok") is False:
        return started

    data = started.get("data") or {}
    shot_id = data.get("id")
    deadline = time.time() + SHOT_TIMEOUT
    status = None
    # Bound before the loop: with SPMCP_SHOT_TIMEOUT <= 0 the loop body never runs, and
    # reading sd afterwards raised UnboundLocalError - which the generic handler turned into
    # "tool failed: ...", losing the shot_timeout code a caller needs to branch on.
    sd = None
    while time.time() < deadline:
        status = call("GET", "/shot")
        sd = status.get("data") if isinstance(status, dict) else None
        if not isinstance(sd, dict):
            return {"ok": False, "error": {"code": "shot_failed", "message": "bridge returned no shot status", "retryable": True}}
        if sd.get("id") != shot_id:
            # Never serve a frame that belongs to a different request: an old picture with
            # a fresh timestamp is exactly the failure mode this tool must not have.
            return {"ok": False, "error": {"code": "shot_superseded",
                                           "message": "shot %s was replaced by %s while waiting"
                                                      % (shot_id, sd.get("id")),
                                           "retryable": True}}
        if sd.get("status") in ("done", "error"):
            break
        time.sleep(0.1)

    if sd is None:
        # The wait elapsed before a single status read happened (SHOT_TIMEOUT <= 0). That
        # is still a timeout, not a crash: the caller must be able to branch on the code.
        return {"ok": False, "error": {"code": "shot_timeout",
                                       "message": "no frame arrived within %.1f s (no status was read)" % SHOT_TIMEOUT,
                                       "retryable": True}}
    if sd.get("status") == "error":
        return {"ok": False, "data": sd,
                "error": {"code": "shot_failed", "message": sd.get("error") or "the game could not capture a frame",
                          "retryable": True}}
    if sd.get("status") != "done":
        return {"ok": False, "data": sd,
                "error": {"code": "shot_timeout",
                          "message": "no frame arrived within %.1f s (status %s)" % (SHOT_TIMEOUT, sd.get("status")),
                          "retryable": True}}

    code, png = call_raw("GET", "/shot/png")
    if code != 200 or not png:
        return {"ok": False, "data": sd,
                "error": {"code": "shot_fetch_failed",
                          "message": "GET /shot/png answered %s with %d bytes" % (code, len(png)),
                          "retryable": code == 0 or code >= 500}}

    meta = dict(sd)
    meta.pop("ok", None)
    meta["served_bytes"] = len(png)
    meta["note"] = (
        "This is the frame the player can see, including whatever UI is drawn on it. "
        "It is NOT an IntelPolicy-filtered context: any debug overlay or omniscient "
        "readout that was on screen is in the picture too."
    )
    return {
        "__image_b64__": base64.b64encode(png).decode("ascii"),
        "__image_mime__": "image/png",
        "__image_meta__": meta,
    }


def t_raw(args):
    path = args["path"]
    if not isinstance(path, str) or not path.startswith("/"):
        return {"ok": False, "error": {"code": "bad_path",
                "message": "path must start with / and target the local bridge",
                "retryable": False}}
    return call(args.get("method", "GET"), path, args.get("body"))


def t_watch_create(args):
    body = {key: args[key] for key in ("unit_id", "field", "mode", "op", "value", "once", "ttl_s")
            if key in args}
    if body.get("op") == "changes":
        body.update(mode="edge", op="changed")
    cursor = watch_events.read_events({})
    if not cursor["ok"]:
        return cursor
    reply = call("POST", "/watch", body)
    if reply.get("ok") is True and isinstance(reply.get("data"), dict):
        reply["data"]["event_cursor"] = cursor["data"]["cursor"]
    return reply


def t_watch_list(args):
    return call("GET", "/watch")


def watch_path(watch_id):
    if type(watch_id) is int and watch_id > 0:
        watch_id = "w" + str(watch_id)
    return "/watch/" + quote(str(watch_id), safe="")


def t_watch_get(args):
    return call("GET", watch_path(args["watch_id"]))


def t_watch_delete(args):
    return call("DELETE", watch_path(args["watch_id"]))


# ------------------------------------------------------- plotting table (map / plot)
# Nine routes from PLOT_CONTRACT.md appendix A, wrapped one-to-one. The rules that
# shaped every handler below:
#
#   * The bridge is the authority. Nothing is range-checked or type-checked here a
#     second time: a Python copy of "lat must be within +-90" drifts from the C# copy
#     the first time either side is edited, and then the MCP layer refuses a call the
#     game would have accepted (or accepts one it refuses). The JSON Schema below is
#     for the model's benefit, not a gate.
#   * A key whose value is None is DROPPED, never sent as null. `{"height": null}`
#     is not the same thing as "the caller said nothing about height" - the bridge
#     types every field it receives, so a stray null comes back as 400 bad_type and
#     reads like the caller made a mistake.
#   * Failures are not swallowed. call() already returns {"ok":false,"error":{...}}
#     plus http_status for 4xx/5xx, and handle_tools_call turns ok:false into
#     isError. A handler that "fixed up" a failure would hide the only signal the
#     caller can branch on.


def _num(value):
    """Decimal text for a number in a query string, never exponent notation.

    C# double.Parse would accept 1e-07, but a wire form that quietly switches to
    exponent for small values is a bad thing to debug by eye in a browser. repr()
    is the shortest round-tripping form in Python 3, and Decimal expands it to plain
    digits without inventing or losing a significant figure.
    """
    if isinstance(value, bool):
        # bool is an int in Python; "true"/"false" is what a URL parameter should say.
        return "true" if value else "false"
    if isinstance(value, int):
        return str(value)
    text = repr(float(value))
    if "e" in text or "E" in text:
        try:
            text = format(decimal.Decimal(text), "f")
        except Exception:  # noqa: BLE001 - inf/nan keep their repr
            pass
    if text.endswith(".0"):
        text = text[:-2]
    return text


def _query(params):
    """Build "?a=1&b=2", skipping every parameter the caller left unset."""
    pairs = []
    for key, value in params.items():
        if value is None:
            continue
        pairs.append((key, value if isinstance(value, str) else _num(value)))
    return urllib.parse.urlencode(pairs)


def _read(path, params):
    qs = _query(params)
    return call("GET", path + ("?" + qs if qs else ""))


def _write(path, fields):
    body = dict((k, v) for k, v in fields.items() if v is not None)
    return call("POST", path, body)


def _points(points):
    """Drop None fields inside each point too, for the same reason as _write."""
    out = []
    for point in points or []:
        if isinstance(point, dict):
            out.append(dict((k, v) for k, v in point.items() if v is not None))
        else:
            # Not a dict: forward it untouched so the bridge, not this layer, says so.
            out.append(point)
    return out


def t_map_view(args):
    return call("GET", "/map")


def t_map_focus(args):
    return _write("/map/focus", {
        "lat": args.get("lat"),
        "lon": args.get("lon"),
        "height": args.get("height"),
        "validate_only": args.get("validate_only"),
    })


def t_map_rotate(args):
    return _write("/map/rotate", {
        "yaw": args.get("yaw"),
        "pitch": args.get("pitch"),
        "validate_only": args.get("validate_only"),
    })


def t_map_zoom(args):
    return _write("/map/zoom", {
        "distance": args.get("distance"),
        "steps": args.get("steps"),
        "validate_only": args.get("validate_only"),
    })


def t_map_units(args):
    return _read("/map/units", {"unit": args.get("unit")})


def t_map_to_geo(args):
    return _read("/map/view-to-geo", {"x": args.get("x"), "y": args.get("y")})


def t_plot_route(args):
    # Read/write in one tool, picked by the shape of the call rather than by a mode
    # flag: `points` present means the caller is describing a route, absent means they
    # want the route that exists. An empty list is NOT treated as "read" - it is
    # forwarded, and the bridge answers whether an empty route is legal.
    points = args.get("points")
    if points is None:
        return _read("/plot/route", {"unit": args.get("unit")})
    return _write("/plot/course", {
        "unit": args.get("unit"),
        "points": _points(points),
        "replace": args.get("replace"),
        "height_state": args.get("height_state"),
        "validate_only": args.get("validate_only"),
    })


def t_plot_clear(args):
    return _write("/plot/clear", {
        "unit": args.get("unit"),
        "validate_only": args.get("validate_only"),
    })


def t_plot_measure(args):
    return _read("/plot/measure", {
        "fromLat": args.get("fromLat"),
        "fromLon": args.get("fromLon"),
        "toLat": args.get("toLat"),
        "toLon": args.get("toLon"),
    })


# ------------------------------------------------------------ plot drawings (draw / list / erase)
# Seven tools on the drawing family: five draw tools that all POST /plot/draw with a
# different "kind", one read of /plot/drawings, and one erase. The mechanism and the
# traps behind it are written up in research/PLOT_DRAW.md. The rules from the comment
# above still hold, with two additions that are about this family specifically:
#
#   * "kind" is chosen by the TOOL, never by the caller. sp_plot_ruler always sends
#     kind=ruler, so a caller cannot draw a ring by asking for one under the ruler tool
#     and get a half-validated mixed request past the bridge's schema.
#   * The one place this layer DOES check something is the shape of its own arguments
#     (a polygon string that does not parse, an erase call naming two selectors). Those
#     are not bridge rules being copied - the bridge never sees a malformed polygon
#     string or a two-selector erase in a form it could answer - so they are answered
#     here with the same {"ok":false,"error":{...}} envelope the bridge would use.


def t_plot_marker(args):
    return _write("/plot/draw", {
        "kind": "marker",
        "lat": args.get("lat"),
        "lon": args.get("lon"),
        "label": args.get("label"),
        "validate_only": args.get("validate_only"),
        "persist": args.get("persist"),
    })


def t_plot_ruler(args):
    return _write("/plot/draw", {
        "kind": "ruler",
        "from_lat": args.get("from_lat"),
        "from_lon": args.get("from_lon"),
        "to_lat": args.get("to_lat"),
        "to_lon": args.get("to_lon"),
        "thickness": args.get("thickness"),
        "validate_only": args.get("validate_only"),
        "persist": args.get("persist"),
    })


def t_plot_ring(args):
    return _write("/plot/draw", {
        "kind": "ring",
        "center_lat": args.get("center_lat"),
        "center_lon": args.get("center_lon"),
        "radius_nm": args.get("radius_nm"),
        "validate_only": args.get("validate_only"),
        "persist": args.get("persist"),
    })


def _polygon_points(text):
    """Parse "lat,lon;lat,lon;lat,lon" into [{"lat":..,"lon":..}, ...], or None if unreadable.

    A string instead of a nested array because that is what a model emits reliably in one
    argument; the array the bridge wants is rebuilt here. None (rather than an exception)
    is the signal that the caller's string did not follow the documented shape - the
    handler turns it into a bad_points envelope, so a typo answers like a bridge refusal
    instead of like a crash in the MCP layer.
    """
    if not isinstance(text, str):
        return None
    out = []
    for chunk in text.split(";"):
        chunk = chunk.strip()
        if not chunk:
            continue
        parts = chunk.split(",")
        if len(parts) != 2:
            return None
        try:
            lat = float(parts[0].strip())
            lon = float(parts[1].strip())
        except (TypeError, ValueError):
            return None
        # NaN and infinity survive float() but cannot be written as JSON by the bridge,
        # so they belong to "the string did not parse", not to "the value is out of range".
        if lat != lat or lon != lon or abs(lat) > 1e308 or abs(lon) > 1e308:
            return None
        out.append({"lat": lat, "lon": lon})
    return out or None


def t_plot_polygon(args):
    points = _polygon_points(args.get("points"))
    if points is None:
        return {"ok": False, "error": {
            "code": "bad_points",
            "message": "points must be a string of vertices separated by ';', each vertex "
                       "'lat,lon' - expected 'lat,lon;lat,lon;lat,lon'",
            "retryable": False}}
    return _write("/plot/draw", {
        "kind": "polygon",
        "points": points,
        "validate_only": args.get("validate_only"),
        "persist": args.get("persist"),
    })


def t_plot_text(args):
    return _write("/plot/draw", {
        "kind": "text",
        "lat": args.get("lat"),
        "lon": args.get("lon"),
        "text": args.get("text"),
        "validate_only": args.get("validate_only"),
    })


def t_drop_sonobuoy(args):
    # The unit id lives in the path; the body deliberately carries no second id.
    uid = args.get("unit")
    if uid is None:
        return {"ok": False, "error": {
            "code": "missing_field",
            "message": "drop_sonobuoy needs unit: the hull that drops it",
            "retryable": False}}
    body = {
        "lat": args.get("lat"),
        "lon": args.get("lon"),
        "ammo": args.get("ammo"),
        "count": args.get("count"),
        "strategy": args.get("strategy"),
        "depth": args.get("depth"),
        "validate_only": args.get("validate_only"),
    }
    return _write("/units/%d/sonobuoy" % int(uid), body)


def t_plot_drawings(args):
    return _read("/plot/drawings", {"limit": args.get("limit")})

def t_plot_erase(args):
    # Exactly one selector, checked here because the bridge cannot tell "the caller sent
    # two" from "the caller sent one it should ignore" once the body is JSON: it would
    # take whichever key it happens to read first and delete something unexpected.
    given = [key for key in ("id", "index", "all_mine") if args.get(key) is not None]
    if not given:
        return {"ok": False, "error": {
            "code": "missing_field",
            "message": "erase needs exactly one of id / index / all_mine",
            "retryable": False}}
    if len(given) > 1:
        return {"ok": False, "error": {
            "code": "bad_value",
            "message": "erase takes exactly one selector, got %s - refusing to guess which "
                       "drawings were meant" % " and ".join(given),
            "retryable": False}}
    key = given[0]
    value = args[key]
    if key == "all_mine":
        if not isinstance(value, bool):
            return {"ok": False, "error": {
                "code": "bad_value",
                "message": "all_mine must be true or false, not %r" % (value,),
                "retryable": False}}
        return call("POST", "/plot/erase", {"all_mine": value})
    # int() on a JSON bool succeeds, so bools are excluded before the conversion rather
    # than after; a fractional index is a bad_value, not something to floor silently.
    if isinstance(value, bool) or (isinstance(value, float) and value != int(value)):
        return {"ok": False, "error": {
            "code": "bad_value",
            "message": "%s must be an integer, not %r" % (key, value),
            "retryable": False}}
    try:
        number = int(value)
    except (TypeError, ValueError):
        return {"ok": False, "error": {
            "code": "bad_value",
            "message": "%s must be an integer, not %r" % (key, value),
            "retryable": False}}
    return call("POST", "/plot/erase", {key: number})


def t_group(args):
    body = {}
    # Forwarded FIRST, before the scope is resolved. These are safety switches: dropping any
    # of them silently downgrades a dry run into a live order (rework review 2.1). The bridge
    # owns their semantics; the MCP layer must never quietly refuse to pass one on.
    for key in ("validate_only", "allow_neutral", "auto_ammo", "force"):
        if args.get(key) is not None:
            body[key] = args[key]
    assign = args.get("assign")
    if assign is not None:
        # assign wins over ids/tf/side: the bridge ignores the scope when assign is present,
        # and testing ids first used to swallow the whole assign array.
        if not isinstance(assign, list) or not assign:
            return {"ok": False, "error": {"code": "empty_assign",
                    "message": "assign was provided but is empty; refusing to fan out over nothing",
                    "retryable": False}}
        body["assign"] = assign
    elif args.get("ids") is not None:
        ids = [int(x) for x in args["ids"]]
        # An empty ids list used to fall through to the side branch and fan the command
        # out over the whole fleet. Refuse it here as well as in the bridge.
        if not ids:
            return {"ok": False, "error": {"code": "empty_scope",
                    "message": "ids was provided but is empty; refusing to widen the scope",
                    "retryable": False}}
        body["ids"] = ids
    elif args.get("tf"):
        body["tf"] = args["tf"]
    elif args.get("side"):
        body["side"] = args["side"]
    else:
        # No implicit side=mine: a caller that forgot the scope must be told, not obeyed.
        return {"ok": False, "error": {"code": "missing_scope",
                "message": "group command needs one of ids / tf / side", "retryable": False}}
    for key in ("knots", "degrees", "nm", "lat", "lon", "target", "ammo", "shots", "on",
                "assign", "telegraph"):
        if args.get(key) is not None and key not in body:
            body[key] = args[key]
    return call("POST", "/group/%s" % args["op"], body)


U = {"unit_id": {"type": "integer", "description": "ObjectBase.UniqueID from sp_units."}}

# T19.2: the only legal values are the members of Seapower.WaypointData.WaypointHeightState.
# The bridge rejects an unknown name with 400 bad_value and lists the legal set, but keeping
# the enum here lets a client fail fast without a round trip.
HEIGHT_STATE_NOTE = {
    "type": "string",
    "enum": [
        "Number", "Periscope", "Current", "Parent", "VeryLow", "Low",
        "Medium", "High", "VeryHigh", "Cruise", "Ceiling", "Surface", "NoChange",
    ],
    "description": "Altitude/depth preset for the new waypoint. Periscope and Surface matter for submarines, VeryLow..Ceiling for aircraft, NoChange keeps whatever the mission set.",
}

# Plotting-table schema fragments, shared by name so the nine tools below cannot drift
# apart. These bounds are here to let a caller fail fast; the bridge enforces them for
# real, and they are deliberately identical to what the bridge documents.
LAT = {
    "type": "number", "minimum": -90, "maximum": 90,
    "description": "Latitude in degrees, positive north. An out-of-range value is refused by the bridge (400 bad_value), never silently clipped.",
}
LON = {
    "type": "number", "minimum": -180, "maximum": 180,
    "description": "Longitude in degrees, positive east.",
}
UNIT_REF = {
    "type": "integer", "minimum": 1,
    "description": "ObjectBase.UniqueID - the number sp_units reports as unit_id. Not a track id, and not a formation id.",
}
VALIDATE_ONLY = {
    "type": "boolean", "default": False,
    "description": "true = run every check and report what would happen, change nothing. Default false, which means the call really moves the camera / really rewrites the route.",
}
CAMERA_HEIGHT = {
    "type": "number",
    "description": "Camera altitude in metres above sea level. Omit it to keep the current height and only move the centre.",
}
PLOT_POINT = {
    "type": "object",
    "properties": {
        "lat": LAT,
        "lon": LON,
        "height": {"type": "number", "description": "Altitude/depth for this leg in metres. Omit to keep whatever the mission set for that leg."},
    },
    "required": ["lat", "lon"],
}

# Drawing-table schema fragments. Same contract as above: a caller reads these to avoid a
# round trip, and the bridge does the real checking.
DRAW_PERSIST = {
    "type": "boolean",
    "description": "Whether the new drawing is written into what the game saves. Omit it to use the bridge default; pass false for scratch work you intend to erase in the same session. The reply's savedIntoAutoSave flag reports what was actually written.",
}
DRAW_LABEL = {
    "type": "string",
    "description": "Text drawn with the marker: a name, a bearing/range note, anything short. Omit for an unlabelled point.",
}
DRAW_THICKNESS = {
    "type": "number",
    "description": "Optional line thickness for the ruler. Omit to accept the plotting layer's own default; the bridge refuses a value the drawing type cannot take (400 out_of_range).",
}
RADIUS_NM = {
    "type": "number", "exclusiveMinimum": 0,
    "description": "Ring radius in nautical miles, positive. The bridge converts it into the drawing's own units and refuses a non-positive radius.",
}
POLYGON_POINTS_TEXT = {
    "type": "string",
    "description": "Vertices as 'lat,lon' pairs separated by ';' - e.g. \"54.6,-26.3;54.7,-26.2;54.6,-26.1\". A string rather than a nested array so the whole shape stays one argument; this layer parses it into the point list the bridge expects and answers bad_points if it cannot.",
}
DRAW_LIMIT = {
    "type": "integer", "minimum": 1,
    "description": "Return at most this many drawings, newest or lowest first as the bridge orders them. Omit for everything it reports.",
}


# --------------------------------------------------- what the player can see on screen
# Two tools for the agent's own surface. Without them an agent could command the game but
# never show the human anything: the Agent Inspector existed only on the HTTP layer, so
# "the player can watch what the agent is doing" depended on the player pressing N. Who
# decides to narrate the work is the agent, so opening the window belongs here too.
#
#   * /inspector/window is the AGENT INSPECTOR: event log, detail pane, command column.
#     install mounts it into the tactical view; show/hide only toggle visibility and keep
#     the geometry; state reports sizes, the three readout groups and the loaded strings.
#     install needs a mission view, so at the main menu it answers 409 no_ui_host - that is
#     a fact about the game, not something to retry in a loop.
#   * /ui/* is the older one-line panel. Kept because it is the surface that also accepts
#     typed text, which the inspector does not.
#
# Both are idempotent and neither is a game command. The op whitelists are not bridge rules
# being copied - an op outside them is not a request the bridge could answer, it is a typo
# that would otherwise become a 404 on a path built by string concatenation.

WINDOW_OPS = ("install", "show", "hide", "remount", "geometry", "split", "maximize",
              "restore", "collapse", "expand", "select", "detail_page", "language")
PANEL_OPS = ("install", "remove", "hold", "type", "submit", "typing")


def _op_error(what, op, allowed):
    return {"ok": False, "error": {
        "code": "bad_value",
        "message": "%s does not take op %r; allowed: %s" % (what, op, ", ".join(allowed)),
        "retryable": False}}


def t_inspector_window(args):
    op = args.get("op") or "state"
    if op == "state":
        return call("GET", "/inspector/window")
    if op not in WINDOW_OPS:
        return _op_error("inspector window", op, ("state",) + WINDOW_OPS)
    body = {"op": op}
    for key in ("left", "top", "width", "height", "x", "y", "index", "page", "language",
                "save"):
        value = args.get(key)
        if value is not None:
            body[key] = value
    return call("POST", "/inspector/window", body)


def t_agent_panel(args):
    op = args.get("op") or "state"
    if op == "state":
        return call("GET", "/ui/state")
    if op not in PANEL_OPS:
        return _op_error("agent panel", op, ("state",) + PANEL_OPS)
    body = {}
    for key in ("text", "on"):
        value = args.get(key)
        if value is not None:
            body[key] = value
    return call("POST", "/ui/" + op, body)


WATCH_ID_SCHEMA = {
    "type": "object",
    "properties": {"watch_id": {"type": "string", "pattern": "^w[1-9][0-9]*$"}},
    "required": ["watch_id"],
}


TOOLS = [
    {
        "name": "sp_watch_create",
        "description": "Register a temporary condition on a raw game unit field. Returns the watch ID and initial probe. Poll sp_watch_events using the returned event_cursor to catch even immediate hits.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "unit_id": {"type": ["string", "integer"]},
                "field": {"type": "string", "minLength": 1},
                "mode": {"type": "string", "enum": ["state", "edge"], "default": "state"},
                "op": {"type": "string", "enum": ["eq", "ne", "gt", "lt", "exists", "changed"]},
                "value": {"description": "Comparison value for eq, ne, gt or lt. Edge mode observes changed."},
                "once": {"type": "boolean"},
                "ttl_s": {"type": "number", "minimum": 0, "description": "Wall-clock seconds. Zero means no time limit."},
            },
            "required": ["unit_id", "field"],
        },
        "handler": t_watch_create,
    },
    {
        "name": "sp_watch_list",
        "description": "List live watches and their current state.",
        "inputSchema": {"type": "object", "properties": {}},
        "handler": t_watch_list,
    },
    {
        "name": "sp_watch_get",
        "description": "Read the state of a watch by ID.",
        "inputSchema": WATCH_ID_SCHEMA,
        "handler": t_watch_get,
    },
    {
        "name": "sp_watch_delete",
        "description": "Remove a watch by ID.",
        "inputSchema": WATCH_ID_SCHEMA,
        "handler": t_watch_delete,
    },
    {
        "name": "sp_watch_events",
        "description": "Poll the local Bridge outbox for Watch events. Pass event_cursor from sp_watch_create or cursor from the previous poll. Omit cursor to initialize at the current tail. Keep a separate cursor per filtered consumer and deduplicate event_key across polls. Only evidence:true indicates a hit or change.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "cursor": {"type": "string", "maxLength": 256},
                "watch_id": {"type": "string", "pattern": "^w[1-9][0-9]*$"},
                "world_generation": {"type": ["string", "integer"]},
                "limit": {"type": "integer", "minimum": 1, "maximum": 100, "default": 32},
            },
        },
        "handler": watch_events.read_events,
    },
    {
        "name": "sp_ping",
        "description": "Check that the in-game bridge is alive and report mod version / Unity version / current scene.",
        "inputSchema": {"type": "object", "properties": {}},
        "handler": t_ping,
    },
    {
        "name": "sp_session",
        "description": "World-generation boundary report (TR2-S). worldGeneration increments only when the bridge decides the world was rebuilt; boundaryState is one of no_world / watching / rebuild_seen / verified. Call this before reusing any unit id you cached earlier: UniqueID comes from an instance counter, so a reloaded save reuses the same numbers for different objects, and an order issued against a stale id can hit the wrong ship. A savegame load is a *rewind* of missionSessionTime rather than a scene change, which is why this is sampled continuously instead of only when polled. gateEnabled reflects a session_gate.on file in the mod folder and is OFF by default: while it is off, nothing blocks writes. lastSuspectSignal reports a shrinking maxUniqueId, which is NOT treated as proof of a rebuild because sinking the top-numbered hulls looks identical. Read runtimeUnverified before trusting this to catch a reload at all - the classifier is offline-tested but the reload signal has not been confirmed in game.",
        "inputSchema": {"type": "object", "properties": {}},
        "handler": t_session,
    },
    {
        "name": "sp_session_fleet",
        "description": "Own fleet of the scenario that is loaded right now: world generation, session id, counts by domain (air/surface/sub/land) and one row per own unit with its current id, name, class and order text. Call this as the first thing after a scenario load, or whenever a cached unit id starts answering 404: UniqueID comes from an instance counter, so ids from a previous world are not reusable and a stale id is indistinguishable from an id that never existed. Enemy units are deliberately absent - use sp_units or the tracks view for what the player has actually detected.",
        "inputSchema": {"type": "object", "properties": {}},
        "handler": t_session_fleet,
    },
    {
        "name": "sp_state",
        "description": "Battlefield snapshot: paused flag, time compression, mission time, and unit counts (own/enemy/air/surface/sub). Call this first to understand the situation.",
        "inputSchema": {"type": "object", "properties": {}},
        "handler": t_state,
    },
    {
        "name": "sp_units",
        "description": "List live units with id, name, class, nation, side, position (lat/lon), heading, speed and altitude. Filter by side (mine|enemy) and kind (air|surface|sub).",
        "inputSchema": {
            "type": "object",
            "properties": {
                "side": {"type": "string", "enum": ["mine", "enemy"], "description": "Filter by allegiance."},
                "kind": {"type": "string", "enum": ["air", "surface", "sub", "land"], "description": "Filter by domain. land covers shore installations and mobile ground units; an unknown value is refused, not ignored."},
                "include_non_units": {
                    "type": "boolean",
                    "description": "Also return missiles, decoys and other non-unit objects.",
                },
            },
        },
        "handler": t_units,
    },
    {
        "name": "sp_unit",
        "description": "Deep detail for one unit: waypoints, current order text, formation role, max speed, EMCON and altitude. A unit your side does not control answers {redacted:true, withheld, intelPolicy, unlock} instead of its internal state: the game's own plot never shows a foreign magazine, battery, deck or order, so the bridge does not either. Treat this as a candidate implementation, not as proof that foreign state cannot be read - the reveal_foreign.on switch is development diagnostics only, never a product feature or an acceptance criterion. Do not report a redacted answer as an empty or broken one.",
        "inputSchema": {"type": "object", "properties": dict(U), "required": ["unit_id"]},
        "handler": t_unit,
    },
    {
        "name": "sp_ammo",
        "description": "Munition inventory of one unit as a name -> count map. A unit your side does not control answers {redacted:true, withheld, intelPolicy, unlock} instead of its internal state: the game's own plot never shows a foreign magazine, battery, deck or order, so the bridge does not either. Treat this as a candidate implementation, not as proof that foreign state cannot be read - the reveal_foreign.on switch is development diagnostics only, never a product feature or an acceptance criterion. Do not report a redacted answer as an empty or broken one.",
        "inputSchema": {"type": "object", "properties": dict(U), "required": ["unit_id"]},
        "handler": t_ammo,
    },
    {
        "name": "sp_weapons",
        "description": "Each magazine entry of a unit with its minimum and maximum launch range in nautical miles. Check this before sp_attack: the game silently refuses shots inside the minimum range. A unit your side does not control answers {redacted:true, withheld, intelPolicy, unlock} instead of its internal state: the game's own plot never shows a foreign magazine, battery, deck or order, so the bridge does not either. Treat this as a candidate implementation, not as proof that foreign state cannot be read - the reveal_foreign.on switch is development diagnostics only, never a product feature or an acceptance criterion. Do not report a redacted answer as an empty or broken one.",
        "inputSchema": {"type": "object", "properties": dict(U), "required": ["unit_id"]},
        "handler": t_weapons,
    },
    {
        "name": "sp_deck",
        "description": "Read-only flight deck state: how many aircraft are on board / in the air, launch and landing flags, deck park slots, aviation ammunition, and the airgroup with each type's selectable loadouts. Read via ObjectBase._obp._flightDeck. Any platform that owns a deck answers here, not just carriers. A unit with no deck returns hasFlightDeck=false and supported=not_applicable; an unreadable field comes back as null rather than 0. This tool never launches, recovers or re-arms anything. A unit your side does not control answers {redacted:true, withheld, intelPolicy, unlock} instead of its internal state: the game's own plot never shows a foreign magazine, battery, deck or order, so the bridge does not either. Treat this as a candidate implementation, not as proof that foreign state cannot be read - the reveal_foreign.on switch is development diagnostics only, never a product feature or an acceptance criterion. Do not report a redacted answer as an empty or broken one.",
        "inputSchema": {"type": "object", "properties": dict(U), "required": ["unit_id"]},
        "handler": t_deck,
    },
    {
        "name": "sp_sub",
        "description": "Read-only submarine systems report for one unit: submerged / below-periscope / emergency-surface / fully-surfaced flags, whether a dive is in progress, current preset depth, telegraph, the surface and submerged speed envelopes, battery charge and remaining range on batteries, all four mast states plus the raised-mast count, noise and cavitation onset, hull crush depth and periscope depth exactly as stored, and whether a wire torpedo controller is attached. Non-submarines return isSubmarine=false and supported=not_applicable. capabilities explains where the depth/periscope/preset commands live and states plainly that mast writes and wire-torpedo control are NOT exposed. This tool never commands anything. A unit your side does not control answers {redacted:true, withheld, intelPolicy, unlock} instead of its internal state: the game's own plot never shows a foreign magazine, battery, deck or order, so the bridge does not either. Treat this as a candidate implementation, not as proof that foreign state cannot be read - the reveal_foreign.on switch is development diagnostics only, never a product feature or an acceptance criterion. Do not report a redacted answer as an empty or broken one.",
        "inputSchema": {"type": "object", "properties": dict(U), "required": ["unit_id"]},
        "handler": t_sub,
    },
    {
        "name": "sp_vertical_state",
        "description": "Read-only altitude and depth state of one unit, plus which vertical commands that unit actually accepts. For a submarine: submerged / below-periscope / emergency-surface flags, battery charge, mast states, and the hull's own preset depth table in feet. For an aircraft or helicopter: the raw preset altitude table. Also returns the legal height_state names for sp_move / sp_set_course. Modes you can use are listed per unit, so do not guess.",
        "inputSchema": {"type": "object", "properties": dict(U), "required": ["unit_id"]},
        "handler": t_vertical_read,
    },
    {
        "name": "sp_vertical",
        "description": "Immediate vertical command. mode=depth (submarine, meters, positive means deeper), mode=periscope (submarine, dive to the hull's own periscope depth), mode=preset_depth (submarine, preset index 0-6 plus update_waypoints), mode=preset_height (aircraft or helicopter, preset index plus update_waypoints - note update_waypoints does nothing for aircraft because the game type-gates it), mode=push_waypoints (submarine, rewrites existing waypoint altitudes from the current DesiredAltitude). Sending a submarine-only mode to a surface ship is refused with 409 wrong_unit_type before any game call. The reply carries confirmation=submitted: altitude takes time to change, so re-read sp_vertical_state rather than expecting an immediate difference.",
        "inputSchema": {
            "type": "object",
            "properties": dict(U, **{
                "mode": {"type": "string", "enum": ["depth", "periscope", "preset_depth", "preset_height", "push_waypoints"]},
                "meters": {"type": "number", "description": "mode=depth: depth below the surface, positive = deeper. Refused with 400 above 110 m unless allow_beyond_measured is set: the game clamps such orders past the hull's crush depth and the boat is lost."},
                "preset": {"type": "integer", "description": "mode=preset_depth / preset_height: index into the hull's own preset table (submarines 0-6)."},
                "update_waypoints": {"type": "boolean", "description": "Also rewrite existing waypoint altitudes. Only has an effect for submarines."},
                "index": {"type": "integer", "description": "mode=push_waypoints: 0=Number 1=Surface 2=Periscope 3=VeryLow 4=Low 5=Medium 6=High 7=VeryHigh."},
                "allow_beyond_measured": {"type": "boolean", "description": "mode=depth: allow a depth above 110 m, the highest value measured to survive. Live incident: the game replaced every order above ~110 m with one fixed depth past the crush depth and the hull sank out of control. Prefer preset_depth for deep dives."},
            }),
            "required": ["unit_id", "mode"],
        },
        "handler": t_vertical,
    },
    {
        "name": "sp_tracks",
        "description": "Every contact plotted by the player's side: track id, side, class, identification state, freshness, and the object_id it maps to. Use it to resolve a track number seen in the game UI.",
        "inputSchema": {"type": "object", "properties": {}},
        "handler": t_tracks,
    },
    {
        "name": "sp_tracks_brief",
        "description": "Read-only ranked changes since this MCP process last observed player tracks. The first read per world gives a ranked baseline. Object-ID remaps bypass the ordinary 800-byte text budget. This is a situational brief, so use sp_tracks for full track identity and before issuing an attack.",
        "inputSchema": {"type": "object", "properties": {}},
        "handler": t_tracks_brief,
    },
    {
        "name": "sp_waypoints",
        "description": "Current waypoint chain of one unit.",
        "inputSchema": {"type": "object", "properties": dict(U), "required": ["unit_id"]},
        "handler": t_waypoints,
    },
    {
        "name": "sp_taskforces",
        "description": "Task force groups known to the game, with their member units.",
        "inputSchema": {"type": "object", "properties": {}},
        "handler": t_taskforces,
    },
    {
        "name": "sp_time",
        "description": "Pause/unpause the simulation or change time compression (1 = real time, higher = faster). At least one argument is required.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "paused": {"type": "boolean", "description": "true pauses, false resumes."},
                "compression": {"type": "number", "description": "Time compression multiplier."},
            },
        },
        "handler": t_time,
    },
    {
        "name": "sp_set_speed",
        "description": "Order a unit to hold an exact speed in knots.",
        "inputSchema": {
            "type": "object",
            "properties": dict(U, knots={"type": "number", "description": "Desired speed in knots."}),
            "required": ["unit_id", "knots"],
        },
        "handler": t_speed,
    },
    {
        "name": "sp_set_course",
        "description": "Turn a unit to a compass heading in degrees (0 = north, 90 = east). Implemented as a change-course order to a point 20 nm away. Optional height_state applies to the new waypoint (Surface for a submarine on the surface, Low/Cruise for aircraft); omit it to keep the previous behaviour, which sends NoChange.",
        "inputSchema": {
            "type": "object",
            "properties": dict(U, degrees={"type": "number", "description": "Heading in degrees true."},
                              height_state=HEIGHT_STATE_NOTE),
            "required": ["unit_id", "degrees"],
        },
        "handler": t_course,
    },
    {
        "name": "sp_move_to",
        "description": "Send a unit to a geographic position. Replaces the current waypoint chain. Optional height_state applies to the new waypoint; omit it to keep NoChange, which is what every earlier build sent.",
        "inputSchema": {
            "type": "object",
            "properties": dict(
                U,
                lat={"type": "number", "description": "Target latitude in degrees."},
                lon={"type": "number", "description": "Target longitude in degrees."},
                height_state=HEIGHT_STATE_NOTE,
            ),
            "required": ["unit_id", "lat", "lon"],
        },
        "handler": t_move,
    },
    {
        "name": "sp_attack",
        "description": "Order a unit to attack another object by id. The relationship gate uses the game's own Taskforce.RelationshipTo: Hostile passes; Friendly is refused; Neutral and Unknown are refused too, because the game fires on a neutral contact exactly like a hostile one and nothing downstream catches a mis-identified neutral -- pass allow_neutral=true to engage a neutral on purpose, or force=true to engage an unknown relationship. It also refuses targets no sensor has detected, ranges outside the selected weapon's envelope, and targets with no usable ammunition, unless force is true. Pass validate_only=true to run every check and fire nothing (force does NOT mean dry-run). No ammo given => the game's own default lookup is used; set auto_ammo=true to let the bridge pick by range instead (longest reach is not always the right weapon). IMPORTANT: 'executed' only means the order was handed to the game. Read 'confirmation' instead: 'fired' = the magazine dropped and a round left, 'unverified' = the count had not moved yet (this is NORMAL and not a failure - rounds are deducted seconds later, so re-check sp_ammo and orderText after ~5 s), 'unavailable' = no readable count for that weapon. An order is only really dropped when both the magazine AND orderText stay unchanged, which happens when the target is already sunk.",
        "inputSchema": {
            "type": "object",
            "properties": dict(
                U,
                target_id={"type": "integer", "description": "UniqueID of the target."},
                ammo={"type": "string", "description": "Ammunition name to use. Omit to let the game choose."},
                shots={"type": "integer", "description": "Missiles fire exactly this many (default 1). Guns keep firing until the target sinks or the task clears."},
                auto_ammo={"type": "boolean", "description": "Let the bridge pick a weapon by range instead of the game default. Ignored when ammo is given."},
                validate_only={"type": "boolean", "description": "Run every precondition and report wouldFire without firing. force does NOT mean dry-run."},
                allow_neutral={"type": "boolean", "description": "Engage a Neutral contact on purpose. Without it a neutral target is refused."},
                force={"type": "boolean", "description": "Skip every refusal, including the unknown-relationship gate. Not a dry-run flag."},
            ),
            "required": ["unit_id", "target_id"],
        },
        "handler": t_attack,
    },
    {
        "name": "sp_validate_attack",
        "description": "Precondition check only: reports whether an attack order would be accepted (relationship? detected? inside the weapon envelope? ammunition available?) and which round it would use. Never fires, never changes game state. Run it before a real attack to learn exactly why a target would be refused.",
        "inputSchema": {
            "type": "object",
            "properties": dict(
                U,
                target_id={"type": "integer", "description": "UniqueID of the target."},
                ammo={"type": "string", "description": "Check this specific ammunition; omit to check the default pick."},
                auto_ammo={"type": "boolean", "description": "Check what the bridge's range-based picker would choose."},
                allow_neutral={"type": "boolean", "description": "Treat a Neutral contact as engageable for this check."},
                force={"type": "boolean", "description": "Treat every refusal as waived, including unknown relationship."},
            ),
            "required": ["unit_id", "target_id"],
        },
        "handler": t_validate_attack,
    },
    {
        "name": "sp_identify",
        "description": "Order a unit to close in and identify a contact.",
        "inputSchema": {
            "type": "object",
            "properties": dict(U, target_id={"type": "integer", "description": "UniqueID of the contact."}),
            "required": ["unit_id", "target_id"],
        },
        "handler": t_identify,
    },
    {
        "name": "sp_rtb",
        "description": "Order a unit to return to base.",
        "inputSchema": {"type": "object", "properties": dict(U), "required": ["unit_id"]},
        "handler": t_rtb,
    },
    {
        "name": "sp_ceasefire",
        "description": (
            "Make a unit hold fire. Each flag maps 1:1 onto the native "
            "CeaseFire(report, clearOrder, formationCheck, stopJamming, airFormationCheck, "
            "clearAttackTasks) parameter and defaults to true, so an empty call behaves "
            "exactly as it always did; pass a flag only to suppress that part."
        ),
        "inputSchema": {
            "type": "object",
            "properties": dict(
                U,
                report={"type": "boolean", "description": "Ask the unit to report (default true)."},
                clear_order={"type": "boolean", "description": "Clear the current order/waypoints (default true)."},
                formation_check={"type": "boolean", "description": "Also stop surface formation mates (default true)."},
                stop_jamming={"type": "boolean", "description": "Stop jamming (default true)."},
                air_formation_check={"type": "boolean", "description": "Also stop air formation mates (default true)."},
                clear_attack_tasks={"type": "boolean", "description": "Cancel queued attack tasks (default true)."},
            ),
            "required": ["unit_id"],
        },
        "handler": t_ceasefire,
    },
    {
        "name": "sp_emcon",
        "description": "Toggle EMCON (emissions control) on a unit. on=true silences emitters, on=false allows them.",
        "inputSchema": {
            "type": "object",
            "properties": dict(U, on={"type": "boolean", "description": "true = EMCON on (quiet)."}),
            "required": ["unit_id", "on"],
        },
        "handler": t_emcon,
    },
    {
        "name": "sp_radar",
        "description": "Enable or disable every radar on a unit.",
        "inputSchema": {
            "type": "object",
            "properties": dict(U, on={"type": "boolean", "description": "true enables radars."}),
            "required": ["unit_id", "on"],
        },
        "handler": t_radar,
    },
    {
        "name": "sp_sonar",
        "description": "Enable or disable active sonars on a unit.",
        "inputSchema": {
            "type": "object",
            "properties": dict(U, on={"type": "boolean", "description": "true enables active sonar."}),
            "required": ["unit_id", "on"],
        },
        "handler": t_sonar,
    },
    {
        "name": "sp_launch_aircraft",
        "description": (
            "Launch aircraft from a carrier/flight-deck unit. With `aircraft` the bridge asks the "
            "game for THAT type by its file name (exactly as reported in airgroupOnBoard[].fileName by sp_deck); "
            "without it, the game picks any airframe of ObjectType.Aircraft with UnitRoles.Fighter. "
            "Returns selectionPath so you can tell which path ran, plus airborneBefore/airborneAfter. "
            "confirmation is always 'submitted': check GET sp_airborne afterwards to see the real "
            "aircraft objects, because the spawn can happen a moment later. `mission` must be one of "
            "None, CAP, Intercept, BombStrike, MissileAttack, SEAD, Jamming, AEW, Recon, MPA, "
            "SearchAndDestroyASW or SAR - anything else is a 400 that lists them."
        ),
        "inputSchema": {
            "type": "object",
            "properties": dict(
                U,
                aircraft={"type": "string", "description": "Exact airframe file name from sp_deck's airgroupOnBoard[].fileName (case-sensitive). Omit to let the game choose."},
                loadout={"type": "string", "description": "Loadout name; omit for the default loadout."},
                squadron={"type": "string", "description": "Optional squadron name filter (case-insensitive)."},
                require_exact_loadout={"type": "boolean", "description": "If true, a loadout name that does not exist on board fails instead of falling back to Default."},
                launch_count={"type": "integer", "description": "How many airframes to put up; 0 (default) keeps the game's own single-launch behaviour."},
                mission={"type": "string", "description": "Mission type passed to FlightDeckTask.MissionType (e.g. CAP, Intercept, SEAD). Default None."},
            ),
            "required": ["unit_id"],
        },
        "handler": t_launch,
    },
    {
        "name": "sp_airborne",
        "description": (
            "Read-only: which aircraft this deck has actually put in the air. Reads "
            "ObjectBase._childAircraft, the game's own list (FlightDeck.launchVehicle appends to it), "
            "so it is an association rather than a guess. Poll this after sp_launch_aircraft."
            "A unit your side does not control answers {redacted:true, withheld, intelPolicy, unlock} instead of its internal state: the game's own plot never shows a foreign magazine, battery, deck or order, so the bridge does not either. Treat this as a candidate implementation, not as proof that foreign state cannot be read - the reveal_foreign.on switch is development diagnostics only, never a product feature or an acceptance criterion. Do not report a redacted answer as an empty or broken one."
        ),
        "inputSchema": {"type": "object", "properties": dict(U), "required": ["unit_id"]},
        "handler": t_airborne,
    },
    {
        "name": "sp_decktasks",
        "description": (
            "Read-only: the flight-deck task queue with each task's uid, label, aircraft type, "
            "squadron, vehicle id, cancelled/isRTB/isBolter flags, startTime and duration. "
            "The uid is the handle sp_abort_task needs to cancel exactly one task."
            "A unit your side does not control answers {redacted:true, withheld, intelPolicy, unlock} instead of its internal state: the game's own plot never shows a foreign magazine, battery, deck or order, so the bridge does not either. Treat this as a candidate implementation, not as proof that foreign state cannot be read - the reveal_foreign.on switch is development diagnostics only, never a product feature or an acceptance criterion. Do not report a redacted answer as an empty or broken one."
        ),
        "inputSchema": {"type": "object", "properties": dict(U), "required": ["unit_id"]},
        "handler": t_decktasks,
    },
    {
        "name": "sp_recover_aircraft",
        "description": (
            "Vector aircraft back to their carrier. Pass `aircraft` (an objectId from sp_airborne) "
            "to recall one, or `all` true to call FlightDeck.RecoverAll on the airgroup; neither or "
            "both is a 400. A single aircraft is refused unless it is in that deck's _childAircraft "
            "list, so you cannot recall someone else's plane. "
            "The game then chains a ReturnTask and its own RecoveryTask and lands the aircraft by "
            "itself; two recalled F-4Js were back on deck in a live run. It is only meaningful while "
            "the aircraft is airborne - a recall raised for a helicopter still in the launch queue "
            "disappears again. Habit worth keeping: give the aircraft a waypoint over safe water "
            "first when it must stay clear of threats. "
            "WATCH THE CONFIRMATION. A live run called this twice for one SH-3H and got 200 with "
            "nothing queued either time, while `all` raised a ReturnTask for every airborne aircraft "
            "in the same second; so the bridge now reads the deck task list back immediately. "
            "'submitted' means a task naming that aircraft was actually observed, and "
            "'submitted_no_visible_task' means the native call ran but queued nothing - retry with "
            "fallback_all true (or all true) if the aircraft must come back now. follow it with "
            "sp_decktasks until a ReturnTask names the aircraft, then sp_airborne until it is gone."
        ),
        "inputSchema": {
            "type": "object",
            "properties": dict(
                U,
                aircraft={"type": "integer", "description": "objectId of the aircraft to recover (from sp_airborne). Omit when using `all`."},
                all={"type": "boolean", "description": "Recover every aircraft on this deck via FlightDeck.RecoverAll. This is the entry point live runs have seen work."},
                fallback_all={"type": "boolean", "description": "Only with `aircraft`: if the single-aircraft call queued nothing, retry through RecoverAll (the whole airgroup). Never assumed; refused together with `all`."},
            ),
            "required": ["unit_id"],
        },
        "handler": t_recover,
    },
    {
        "name": "sp_abort_task",
        "description": (
            "Cancel a queued deck task by uid (from sp_decktasks), or every queued launch task "
            "with `all` true; pass exactly one of them. Only launch tasks are targeted - a "
            "ReturnTask or RecoveryTask (what a recall creates) is never touched. "
            "`delayed` defaults to TRUE, which asks the game to defer the abort to a safe point. "
            "NEVER send delayed false without meaning it: a live run cancelled a launch task while "
            "its aircraft was already taxiing, and Player.log then held 35,032 NullReferenceExceptions "
            "on one stack once per FixedUpdate (FlightDeck.OnFixedUpdate -> FlightDeckTask."
            "fixedTickStateMachine -> ClearRunway.onFixedUpdate -> Taxi.onFixedUpdate -> "
            "GroundTaxiPhysics.get_PositionError -> Component.get_transform) until the process ended. "
            "The immediate form is refused with 400 unsafe_abort unless `unsafe_immediate` is also true. "
            "The response reports tasksTargeted, tasksBefore/tasksAfter and removed, and a shrinking "
            "list is the only evidence of an effective cancel: one live run removed a task, another "
            "returned removed=0 while the aircraft still took off. Judge it on a game-time budget - "
            "raise time compression with sp_time and poll sp_decktasks rather than reading once."
        ),
        "inputSchema": {
            "type": "object",
            "properties": dict(
                U,
                task={"type": "string", "description": "Task uid (GUID) from sp_decktasks."},
                all={"type": "boolean", "description": "Abort every queued launch task on this deck (ReturnTask and RecoveryTask are not touched). The bridge walks the queue and calls abortLaunchTask per task with the same delayed semantics."},
                delayed={"type": "boolean", "description": "Forwarded to abortLaunchTask's delayed argument; defaults to TRUE. False cancels on the spot and is refused unless unsafe_immediate is also true."},
                unsafe_immediate={"type": "boolean", "description": "Required to send delayed false. Reads as \"I accept that this can leave the deck ticking a cancelled task on a destroyed transform\"."},
            ),
            "required": ["unit_id"],
        },
        "handler": t_abort,
    },
    {
        "name": "sp_rearm",
        "description": "Rearm/refuel a unit from its supply. reload_broken maps onto the native RearmAll(reloadBroken); default false matches the previous call exactly.",
        "inputSchema": {
            "type": "object",
            "properties": dict(U, reload_broken={"type": "boolean", "description": "true = also reload broken mounts (native RearmAll(reloadBroken)); default false."}),
            "required": ["unit_id"],
        },
        "handler": t_rearm,
    },
    {
        "name": "sp_clear_waypoints",
        "description": "Remove every waypoint the unit is holding.",
        "inputSchema": {"type": "object", "properties": dict(U), "required": ["unit_id"]},
        "handler": t_clear_waypoints,
    },
    {
        "name": "sp_land",
        "description": (
            "Read a ground unit (SAM site, coastal battery, radar station) - the land domain, "
            "which the sea-only routes cannot describe. part selects one view: summary (all of "
            "them, folded), movement (_isMobile, speed, destination), damage (hull and system "
            "state), systems (sensors/weapons mounts), fires (what it is engaging), target_points "
            "(the aim points a strike would use), schema (the field list of this route, no unit "
            "data). Works on any land unit you can see; a sea unit answers not_applicable with the "
            "reason instead of pretending to have these fields. part=schema still requires a real "
            "unit id: the path id is an address, not a wildcard."
        ),
        "inputSchema": {
            "type": "object",
            "properties": dict(
                U,
                part={
                    "type": "string",
                    "enum": ["summary", "movement", "damage", "systems", "fires", "target_points", "schema"],
                    "description": "Which view to read. Default summary.",
                },
            ),
            "required": ["unit_id"],
        },
        "handler": t_land,
    },
    {
        "name": "sp_fire",
        "description": (
            "Fire at a POSITION rather than at a track: the round goes where you say, which is what "
            "a player does by clicking the plot when there is no firing solution yet. Unlike "
            "sp_attack there is no target id, so the friendly/undetected/range gates do not apply - "
            "the ammunition name is resolved against the hull's mounts, and the receipt carries "
            "ammoBefore/ammoAfter so a shot that never left the tube is visible as an unchanged "
            "count. ammo is required: guessing which weapon a captain meant is not the bridge's call."
        ),
        "inputSchema": {
            "type": "object",
            "properties": dict(
                U,
                lat={"type": "number", "description": "Latitude of the aim point, degrees."},
                lon={"type": "number", "description": "Longitude of the aim point, degrees."},
                ammo={"type": "string", "description": "Ammo id from the unit's own inventory (sp_ammo lists it)."},
                shots={"type": "integer",
                      "description": "Rounds to fire. -1 is passed through to the game "
                                     "unchanged; what the game does with it is NOT verified "
                                     "by any test here. Default 1."},
                priority={"type": "integer", "description": "Task priority 1..100, default 1."},
                validate_only={"type": "boolean", "description": "Check the request without firing anything."},
            ),
            "required": ["unit_id", "lat", "lon", "ammo"],
        },
        "handler": t_fire,
    },
    {
        "name": "sp_weapon_status",
        "description": (
            "Set the weapons-control doctrine of one hull: tight (fire only on explicit orders), "
            "free (engage on its own judgement), hold (weapons tight, do not fire at all). This is "
            "doctrine, not a target order, so it does not need a target and it does not clear the "
            "route. formation=true applies it to the whole formation."
        ),
        "inputSchema": {
            "type": "object",
            "properties": dict(
                U,
                status={"type": "string", "enum": ["tight", "free", "hold"], "description": "Doctrine to set."},
                formation={"type": "boolean", "description": "Apply to the whole formation."},
                validate_only={"type": "boolean", "description": "Check without changing anything."},
            ),
            "required": ["unit_id", "status"],
        },
        "handler": t_weapon_status,
    },
    {
        "name": "sp_telegraph",
        "description": (
            "Set the engine order (the telegraph notches) on one hull: -1 is the game's all-stop "
            "sentinel, 0..5 are the real notches. This is not sp_set_speed: speed sets a target "
            "velocity, the telegraph sets the ordered engine step, and on an aircraft the value is "
            "stored 0-based while every surface hull stores it as given - the receipt reports both "
            "the number written and the number the game reads back. A hull with no telegraph "
            "override (helicopter, land unit) answers 409 wrong_unit_type instead of silently doing "
            "nothing."
        ),
        "inputSchema": {
            "type": "object",
            "properties": dict(
                U,
                telegraph={"type": "integer", "minimum": -1, "maximum": 5, "description": "Engine notch: -1 all stop, 0..5 the real steps."},
                validate_only={"type": "boolean", "description": "Check without changing anything."},
            ),
            "required": ["unit_id", "telegraph"],
        },
        "handler": t_telegraph,
    },
    {
        "name": "sp_rudder",
        "description": (
            "Put the rudder over to an angle in degrees (-90 hard port .. +90 hard starboard, 0 "
            "amidships). This is a direct control input, not sailing orders: it does not create a "
            "waypoint and it does not set a course, it moves the rudder now. Read back from "
            "getRudderAngle so a refused or ignored write is visible in the receipt. Units whose "
            "class has no rudder override answer 409 wrong_unit_type."
        ),
        "inputSchema": {
            "type": "object",
            "properties": dict(
                U,
                angle={"type": "number", "minimum": -90, "maximum": 90, "description": "Rudder angle in degrees; positive is starboard."},
                validate_only={"type": "boolean", "description": "Check without changing anything."},
            ),
            "required": ["unit_id", "angle"],
        },
        "handler": t_rudder,
    },
    {
        "name": "sp_heading",
        "description": (
            "Turn a ship onto a heading without laying a waypoint. The game has no setHeading at all "
            "(whole-assembly IL check), so this is a small closed-loop order built on the game's own "
            "course law: the heading error is converted to a rudder angle clamped to max_rudder, and "
            "the hull reports back the heading it is actually on. smoothness is a DIVISOR on the "
            "(0, 100]. This is a course change, not a route: the unit keeps its existing waypoints "
            "and its speed."
        ),
        "inputSchema": {
            "type": "object",
            "properties": dict(
                U,
                heading={"type": "number", "description": "Target heading in degrees (wraps; 0 = north)."},
                max_rudder={"type": "number", "description": "Rudder limit for this turn in degrees, (0, 90]. Default 25."},
                smoothness={"type": "number",
                            "description": "Divisor on the course error (rudder = "
                                           "error / smoothness), so a higher value turns "
                                           "more gently. (0, 100]. Default 5."},
                validate_only={"type": "boolean", "description": "Check without changing anything."},
            ),
            "required": ["unit_id", "heading"],
        },
        "handler": t_heading,
    },
    {
        "name": "sp_radars",
        "description": (
            "Switch a hull's air-search or surface-search radars on or off (role=air|surface, "
            "state=on|off). Unlike sp_radar, which drives the whole EMCON state, this addresses one "
            "role and can be applied across the formation. The game does not maintain its own "
            "'air search radars on' flag through these calls, so the receipt reports the per-mount "
            "IsOn read-back instead of a flag that would be stale."
        ),
        "inputSchema": {
            "type": "object",
            "properties": dict(
                U,
                role={"type": "string", "enum": ["air", "surface"], "description": "Which radar set."},
                state={"type": "string", "enum": ["on", "off"], "description": "What to set it to."},
                formation={"type": "boolean", "description": "Apply to the whole formation."},
                sync_flag={"type": "boolean", "description": "Also sync the game's own 'is on' flag. Default true."},
                validate_only={"type": "boolean", "description": "Check without changing anything."},
            ),
            "required": ["unit_id", "role", "state"],
        },
        "handler": t_radars,
    },
    {
        "name": "sp_countermeasures",
        "description": (
            "Launch countermeasures: chaff, noisemaker, or both. ammo is the countermeasure id from "
            "the unit's inventory; omit it to let the bridge resolve the hull's own chaff/noisemaker "
            "mounts - and when there is nothing to launch it answers 409 with the reason instead of "
            "calling the game with an empty ammunition id. message only decides whether the game "
        "plays its own chaff call-out and raises the alert; it never changes what leaves "
        "the hull, and the noisemaker path ignores it."
        ),
        "inputSchema": {
            "type": "object",
            "properties": dict(
                U,
                kind={"type": "string", "enum": ["chaff", "noisemaker", "both"], "description": "What to launch. Default both."},
                ammo={"type": "string", "description": "Optional countermeasure ammo id; omit to resolve the mounts."},
                message={"type": "boolean", "description": "Also message the formation. Default true."},
                validate_only={"type": "boolean", "description": "Check without launching anything."},
            ),
            "required": ["unit_id"],
        },
        "handler": t_countermeasures,
    },
    {
        "name": "sp_attack_waypoint",
        "description": (
            "Attack a WAYPOINT: the hull flies to a place and engages whatever comes inside the "
            "engagement area there, instead of chasing one target id. lat/lon are the point to "
            "engage; waypoint_lat/waypoint_lon default to the same point ('go here and engage "
            "here'). ammo is required. area_radius is in NAUTICAL MILES and matches the game's own "
            "menu (-1 off, 0.5, 1, 2, 5, 10, 15 ...) - the conversion to world units happens in the "
            "bridge because the game stores the value in world units while its menu is in miles. "
            "target_unit optionally says which contact the area is aimed at; salvo_type is "
            "per_launcher (default), per_target (split across targets) or in_total (cap the whole "
        "count)."
        ),
        "inputSchema": {
            "type": "object",
            "properties": dict(
                U,
                ammo={"type": "string", "description": "Ammo id from the unit's own inventory. Required: there is no default."},
                lat={"type": "number", "description": "Latitude of the point to engage."},
                lon={"type": "number", "description": "Longitude of the point to engage."},
                waypoint_lat={"type": "number", "description": "Latitude of the waypoint to hold at. Defaults to lat."},
                waypoint_lon={"type": "number", "description": "Longitude of the waypoint to hold at. Defaults to lon."},
                target_unit={"type": "integer", "description": "Optional contact id the engagement area is aimed at."},
                salvo={"type": "integer", "description": "Rounds per engagement, 1..1000. Default 1."},
                salvo_type={"type": "string", "enum": ["per_launcher", "per_target", "in_total"],
                        "description": "How to count the salvo: per_launcher (default), "
                                       "per_target or in_total."},
                area_radius={"type": "number", "description": "Engagement radius in NAUTICAL MILES; -1 (default) means off."},
                formation_attack={"type": "boolean", "description": "Let the formation join the attack."},
                attack_only_detected={"type": "boolean", "description": "Engage only contacts your side has detected."},
                validate_only={"type": "boolean", "description": "Check without ordering anything."},
            ),
            "required": ["unit_id", "ammo", "lat", "lon"],
        },
        "handler": t_attack_waypoint,
    },
    {
        "name": "sp_group",
        "description": (
            "Issue ONE command to many units at once. You MUST name the scope: ids, or tf, "
            "or side (mine|enemy|all). There is no implicit default, and an empty ids list is "
            "rejected rather than widened. "
            "For real fire distribution use op=attack with `assign`: a list of "
            "{unit, target} pairs so each ship engages its own target instead of the whole "
            "group piling onto one. ops: speed, course, move, attack, emcon, radar, sonar, "
            "ceasefire, rtb, rearm, waypointClear, telegraph. "
            "Returns per-unit ok/error plus a succeeded count; a refused engagement counts as "
            "not succeeded."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "op": {"type": "string", "description": "Command name: speed / course / move / attack / emcon / radar / sonar / ceasefire / rtb / rearm / waypointClear / telegraph."},
                "assign": {
                    "type": "array",
                    "description": "op=attack only. Fire-distribution list, e.g. [{\"unit\":9,\"target\":52},{\"unit\":1,\"target\":53}]. Each entry may carry its own ammo/shots. When present, ids/tf/side are ignored.",
                    "items": {
                        "type": "object",
                        "properties": {
                            "unit": {"type": "integer", "description": "Firing unit id."},
                            "target": {"type": "integer", "description": "Target object id."},
                            "ammo": {"type": "string", "description": "Optional ammo id for this unit."},
                            "shots": {"type": "integer", "description": "Optional shot count for this unit."}
                        },
                        "required": ["unit", "target"]
                    }
                },
                "ids": {"type": "array", "items": {"type": "integer"}, "description": "Explicit unit ids. Overrides tf/side."},
                "tf": {"type": "string", "description": "Taskforce name, e.g. Taskforce1."},
                "side": {"type": "string", "enum": ["mine", "enemy", "all"], "description": "Required unless ids or tf is given. mine = friendly units, enemy = hostile units, all = every live unit."},
                "knots": {"type": "number", "description": "For op=speed."},
                "degrees": {"type": "number", "description": "For op=course."},
                "nm": {"type": "number", "description": "For op=course, distance in nm (default 60)."},
                "lat": {"type": "number", "description": "For op=move."},
                "lon": {"type": "number", "description": "For op=move."},
                "target": {"type": "integer", "description": "Target object id for op=attack (shared by every unit)."},
                "ammo": {"type": "string", "description": "Ammo id for op=attack. Omit to let the bridge pick a weapon that can reach the target."},
                "shots": {"type": "integer", "description": "Shots for op=attack (default 1). Missiles fire exactly this many; guns keep firing."},
                "on": {"type": "boolean", "description": "Toggle for emcon/radar/sonar."},
                "telegraph": {"type": "integer", "description": "0-5 for op=telegraph."},
            },
            "required": ["op"],
        },
        "handler": t_group,
    },
    {
        "name": "sp_screenshot",
        "description": "Take ONE picture of the frame the game is currently drawing, and return it as an image. Use it to check what every other readout only claims: were the glyphs actually rasterised (a font name is not proof of a visible Chinese title), is a panel really inside the window instead of clipped, did a marker land on the right pixel, does the map show what /units reports. The picture is exactly what a player would see, INCLUDING any UI on screen - so it is not an IntelPolicy-filtered view; if a debug overlay was visible it is in the picture, and it must not be reused as intel the other tools would have refused. Captures happen at end of frame, one at a time, with a bounded wait: a failed capture comes back as an error, never as an older picture with a fresh timestamp. By default the image is downscaled to 1024 px wide for reading; full=true gives the untouched texture (slower, larger, needed for 1 px lines and CJK glyph inspection). save=true also writes a PNG under the mod folder's shots\\ directory, which is the only place screenshots can be written - there is no path argument for that reason.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "full": {"type": "boolean", "description": "Return the untouched texture instead of a 1024 px wide copy. Nothing is ever upscaled."},
                "save": {"type": "boolean", "description": "Also write the PNG to <mod>\\shots\\ as debugging evidence."},
            },
        },
        "handler": t_screenshot,
    },
    {
        "name": "sp_raw",
        "description": "Escape hatch: issue a raw HTTP request against the bridge. Use only when no dedicated tool covers the need. path examples: /state, /units?side=enemy.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "method": {"type": "string", "enum": ["GET", "POST"], "description": "HTTP method."},
                "path": {"type": "string", "description": "Path starting with a slash."},
                "body": {"type": "object", "description": "Optional JSON body for POST."},
            },
            "required": ["path"],
        },
        "handler": t_raw,
    },
    # ------------------------------------------------ plotting table (MapView / MapPlot)
    # Nine routes from PLOT_CONTRACT.md appendix A. Reads first, then the writes, then
    # the ruler that touches the game at all. Deliberately NOT wrapped yet: /map/select,
    # /map/deselect, /map/set-center, /map/geo-to-view, /plot/ruler - see
    # research/MCP_MAP_TOOLS.md for why each one waits.
    {
        "name": "sp_map_view",
        "description": (
            "Read-only snapshot of the plotting table: which map is live, the camera's centre / height / yaw / "
            "pitch / zoom, the view model in use, the current interaction state, and how many routes or markers "
            "are drawn. Nothing moves and nothing is written, and that is exactly why it is the before-and-after "
            "picture for every other sp_map_* and sp_plot_* call: a write replies with verified plus the fields it "
            "re-read afterwards, and verified:false means the game accepted the call but the read-back disagreed. "
            "This is the camera and the control, NOT battlefield contacts - for those use sp_tracks."
        ),
        "inputSchema": {"type": "object", "properties": {}},
        "handler": t_map_view,
    },
    {
        "name": "sp_map_focus",
        "description": (
            "WRITE - THIS MOVES THE PLAYER'S CAMERA. Recentres the plotting table on a geographic point; the "
            "optional height also sets the camera's altitude in metres. It orders no unit and writes no route, but "
            "it does move the view the player is looking at, so treat it as a visible action, not as a read. "
            "validate_only=true runs every check (is the plotting table there, are lat/lon in range, is a camera "
            "attached) and reports wouldMove without touching the camera - do that first on a live session. "
            "How you know it worked: not the 200. The reply carries verified together with the camera fields read "
            "back after the call; verified:false means the call was accepted but the camera did not land where you "
            "asked, which is what a clamp at the map edge looks like. sp_map_view gives you that same read on demand."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "lat": LAT,
                "lon": LON,
                "height": CAMERA_HEIGHT,
                "validate_only": VALIDATE_ONLY,
            },
            "required": ["lat", "lon"],
        },
        "handler": t_map_focus,
    },
    {
        "name": "sp_map_rotate",
        "description": (
            "WRITE - THIS TURNS THE PLAYER'S CAMERA. yaw is the map's own rotation in degrees (0 = north up); the "
            "optional pitch tilts the view. It orders nothing and plots nothing, but the camera does turn on "
            "screen. validate_only=true validates and reports wouldRotate without turning anything. "
            "Confirm it by comparing yaw/pitch before and after: the reply's verified flag is that comparison already "
            "done for you, and sp_map_view reports the same numbers if you want to see them yourself."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "yaw": {"type": "number", "description": "Map rotation in degrees; 0 = north up. The sign convention is the map control's own, so read yaw back from sp_map_view the first time instead of assuming which way is positive."},
                "pitch": {"type": "number", "description": "View tilt in degrees. Omit to leave the current pitch alone."},
                "validate_only": VALIDATE_ONLY,
            },
            "required": ["yaw"],
        },
        "handler": t_map_rotate,
    },
    {
        "name": "sp_map_zoom",
        "description": (
            "WRITE - THIS CHANGES WHAT THE PLAYER CAN SEE. Give distance in metres (an absolute camera distance) or "
            "steps (relative zoom steps on the current zoom) - exactly one of them. A call naming neither is "
            "refused; with both present the bridge decides which one wins, so never send both. It orders nothing and "
            "plots nothing. validate_only=true reports the value that would be applied without applying it. "
            "Confirm with sp_map_view by comparing zoom/distance before and after; the reply's verified flag is that "
            "read-back, not an echo of your argument."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "distance": {"type": "number", "description": "Absolute camera distance in metres. Mutually exclusive with steps - give one."},
                "steps": {"type": "number", "description": "Relative zoom steps from the current zoom. Mutually exclusive with distance - give one, and read the resulting zoom back from sp_map_view rather than assuming a sign convention."},
                "validate_only": VALIDATE_ONLY,
            },
        },
        "handler": t_map_zoom,
    },
    {
        "name": "sp_map_units",
        "description": (
            "Read-only. The units the plotting table is currently drawing, each with its geographic position, its "
            "world position, and its SCREEN position in the map control's own pixel space, plus whether it falls "
            "inside the visible rect. That screen pixel is the whole promise of this tool: it is a coordinate "
            "conversion for units the plot already knows about, NOT the list of contacts your side has discovered "
            "in the viewport, and it is NOT intel-filtered - a unit can appear here with no track on the plot, and "
            "the plot can show a track with no unit behind it. Ask sp_tracks what your side has detected; ask this "
            "what the camera is drawing and where. unit=N narrows the answer to one ObjectBase.UniqueID. "
            "If the x/y look wrong, read sp_map_view first: screen coordinates only mean something for the camera "
            "that produced them, so a stale camera makes every pixel a lie. Success looks like an entry per drawn "
            "unit carrying geo/world/screen; an empty list together with hasMap=false means there is no live plot "
            "to read - it does not mean the sea is empty. Writes nothing."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "unit": UNIT_REF,
            },
        },
        "handler": t_map_units,
    },
    {
        "name": "sp_map_to_geo",
        "description": (
            "Read-only inverse of the screen conversion: takes an x/y pixel in the map control's view space and "
            "returns the geographic position (and world position) under it. Use it to turn a pixel you actually saw "
            "into lat/lon before calling sp_map_focus. It expects the same view-space pixels sp_map_units reports, "
            "and whether a pixel outside the visible rect is clamped or extrapolated is the bridge's decision - read "
            "the answer rather than assuming. Moves nothing and writes nothing; it is only a lookup. Success looks "
            "like a reply that carries the geographic position (lat/lon, and the world position) for that pixel; a "
            "reply with no geo means the bridge refused the pixel, which is information, not a failure to retry "
            "blindly. Prove the mapping by feeding the returned lat/lon back through sp_map_units and checking it "
            "lands on the pixel you started from."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "x": {"type": "number", "description": "Pixel x in the map control's view space, as sp_map_units reports it."},
                "y": {"type": "number", "description": "Pixel y in that same view space."},
            },
            "required": ["x", "y"],
        },
        "handler": t_map_to_geo,
    },
    {
        "name": "sp_plot_route",
        "description": (
            "Two tools in one, chosen by whether `points` is present - there is no mode flag. "
            "WITHOUT points it READS: the waypoint chain the unit is actually holding (lat/lon/height and height "
            "state per leg, straight from the game's own waypoint export). Writes nothing. "
            "WITH points it WRITES that chain: 1-40 legs of {lat,lon,height?}, an optional height_state applied to "
            "the new legs, and validate_only=true to check everything and plot nothing. THIS CHANGES THE GAME - the "
            "hull steers the route you send, so a wrong lat/lon moves a real ship. Neither form draws on the map: "
            "this is the unit's order chain, not a map overlay. "
            "How you know a write landed: call this again WITHOUT points and compare. The reply's verified/after "
            "fields come from that same export, and verified:false means the chain on the ship does not match what "
            "you asked for - do not report that as success."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "unit": UNIT_REF,
                "points": {
                    "type": "array",
                    "description": "Presence of this field is what turns the call into a WRITE. 1-40 legs in order. Omit it entirely to read the route instead.",
                    "items": PLOT_POINT,
                    "minItems": 1,
                    "maxItems": 40,
                },
                "replace": {"type": "boolean", "description": "Only meaningful with points: true replaces the whole chain, false appends after the existing waypoints. Send it explicitly on a real order - omit it and the bridge's own default applies."},
                "height_state": HEIGHT_STATE_NOTE,
                "validate_only": VALIDATE_ONLY,
            },
            "required": ["unit"],
        },
        "handler": t_plot_route,
    },
    {
        "name": "sp_plot_clear",
        "description": (
            "WRITE - THIS EMPTIES A UNIT'S PLOT. Removes every waypoint the unit holds, addressed through the "
            "plotting route rather than through the unit list, which makes it the plot-side counterpart of "
            "sp_clear_waypoints: the hull loses its destination and whatever that route was driving stops being "
            "driven. validate_only=true reports how many legs would go and removes nothing. "
            "Confirm by reading sp_plot_route back with no points - an empty chain is the evidence, and the reply's "
            "verified flag is that read-back rather than a claim."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "unit": UNIT_REF,
                "validate_only": VALIDATE_ONLY,
            },
            "required": ["unit"],
        },
        "handler": t_plot_clear,
    },
    {
        "name": "sp_plot_measure",
        "description": (
            "Pure arithmetic: no game state is read and none is written. Distance and bearing between two geographic "
            "points, given as fromLat/fromLon/toLat/toLon. It needs no unit, no selected map and no camera, and it "
            "works with the plot closed or the simulation paused, so it is the one plotting-table tool that is "
            "always safe to call. Nothing is drawn (the map's own ruler overlay is not what this is) and nothing "
            "moves. Success looks like a reply carrying distance and bearing for the two points; a missing or NaN "
            "field means the bridge rejected the coordinates, so read the error instead of re-sending. Sanity-check "
            "it by measuring A->B and then B->A: the distance must match and the bearing must differ by 180 degrees."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "fromLat": LAT,
                "fromLon": LON,
                "toLat": LAT,
                "toLon": LON,
            },
            "required": ["fromLat", "fromLon", "toLat", "toLon"],
        },
        "handler": t_plot_measure,
    },
    {
        "name": "sp_plot_marker",
        "description": (
            "WRITE - THIS DRAWS A POINT ON THE PLAYER'S PLOTTING TABLE, AND THE DRAWING IS PART OF WHAT THE GAME "
            "SAVES. A marker is one labelled point: the bridge adds a single marker graphic at lat/lon, carrying "
            "label as its text when you pass one. It is an annotation, not a waypoint - no unit is given a "
            "destination and nothing starts moving because of it. The new graphic joins the plotting layer's "
            "graphics list, and that list is serialised into the player's save file, automatic save included, so "
            "the drawing is still on the chart after the player reloads; the reply's savedIntoAutoSave flag is the "
            "bridge's answer to 'did this go into the save'. That cuts both ways: a drawing you meant as scratch "
            "work is in the player's file the moment it appears, so either pass persist=false when the bridge "
            "should keep it out, or erase it with the id this call returns. validate_only=true computes and checks "
            "the geometry and draws NOTHING - nothing is added to the layer, nothing reaches the save, and the "
            "reply describes what would have been drawn - which is what to use while coordinates are still being "
            "generated. Success looks like a reply carrying the new drawing's id and the layer's resulting "
            "graphics count; read sp_plot_drawings back to see it listed before telling the player something is on "
            "the chart. Nothing here de-duplicates: calling twice draws two markers."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "lat": LAT,
                "lon": LON,
                "label": DRAW_LABEL,
                "validate_only": VALIDATE_ONLY,
                "persist": DRAW_PERSIST,
            },
            "required": ["lat", "lon"],
        },
        "handler": t_plot_marker,
    },
    {
        "name": "sp_plot_ruler",
        "description": (
            "WRITE - THIS DRAWS A STRAIGHT LINE BETWEEN TWO GEOGRAPHIC POINTS ON THE PLAYER'S PLOTTING TABLE. The "
            "ruler graphic runs from from_lat/from_lon to to_lat/to_lon and is an annotation only: it measures "
            "nothing back to you and it moves nothing. For the distance and bearing use sp_plot_measure, which is "
            "pure arithmetic and draws nothing - drawing first and measuring second is the wrong order, because "
            "the drawing goes into the game's graphics list and that list is serialised into the player's save "
            "file (automatic save included), so the line is on the chart again after a reload and the reply's "
            "savedIntoAutoSave flag says whether it went in. Pass persist=false if the bridge should keep this "
            "line out of the save, as with the other draw tools. thickness is a drawing parameter, not a "
            "geographic one; omit it for the layer's own default. validate_only=true computes and checks both "
            "endpoints and draws NOTHING - no graphic is added, nothing reaches the save - so use it while the "
            "coordinates are still being generated. Success looks like a reply with the new drawing's id and the "
            "layer's graphics count; read sp_plot_drawings back to confirm the line exists before reporting it."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "from_lat": LAT,
                "from_lon": LON,
                "to_lat": LAT,
                "to_lon": LON,
                "thickness": DRAW_THICKNESS,
                "validate_only": VALIDATE_ONLY,
                "persist": DRAW_PERSIST,
            },
            "required": ["from_lat", "from_lon", "to_lat", "to_lon"],
        },
        "handler": t_plot_ruler,
    },
    {
        "name": "sp_plot_ring",
        "description": (
            "WRITE - THIS DRAWS A CIRCLE OF A GIVEN RADIUS, IN NAUTICAL MILES, CENTRED ON A GEOGRAPHIC POINT. It "
            "is the range-ring annotation: the bridge turns center_lat/center_lon plus radius_nm into one ring "
            "graphic, which is what makes it the right tool for 'anything within N nm of here' on a map that has "
            "no scale of its own. The graphic is an annotation - it does not create a zone, does not trigger "
            "detection and moves nothing - but it is a real drawing in the plotting layer, and the plotting "
            "layer's graphics are serialised into the player's save file (automatic save included), so the ring "
            "is still there after a reload; the reply's savedIntoAutoSave flag reports whether this one went in. "
            "persist=false asks the bridge to keep it out of the save when you only want it for the current "
            "session. validate_only=true converts the radius and checks the centre and draws NOTHING - nothing "
            "joins the layer and nothing reaches the save - which is the call to make while radius or centre is "
            "still being computed. Success looks like a reply with the drawing's id and the layer's graphics "
            "count; read sp_plot_drawings back to see the ring listed."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "center_lat": LAT,
                "center_lon": LON,
                "radius_nm": RADIUS_NM,
                "validate_only": VALIDATE_ONLY,
                "persist": DRAW_PERSIST,
            },
            "required": ["center_lat", "center_lon", "radius_nm"],
        },
        "handler": t_plot_ring,
    },
    {
        "name": "sp_plot_polygon",
        "description": (
            "WRITE - THIS DRAWS A CLOSED POLYGON ON THE PLAYER'S PLOTTING TABLE, FROM A STRING OF VERTICES. points "
            "is one string of 'lat,lon' pairs separated by ';' - \"54.6,-26.3;54.7,-26.2;54.6,-26.1\" - because a "
            "nested array of objects is easy to get subtly wrong in one argument and impossible to read back when "
            "it fails. This layer parses the string into the point list the bridge wants; if the string does not "
            "parse, the answer is a bad_points refusal and nothing is sent to the bridge at all. Give the vertices "
            "in outline order, once each: the drawing type closes the outline itself, so do not repeat the first "
            "vertex at the end. The bridge, not this layer, decides whether the result is a usable polygon (it "
            "refuses too few vertices with 400 bad_value). Passing it means a real graphic in the plotting layer, "
            "and that layer's graphics are serialised into the player's save file - automatic save included - so "
            "the outline survives a reload; the reply's savedIntoAutoSave flag reports whether this one did, and "
            "persist=false asks the bridge to leave it out of the save. validate_only=true parses, converts and "
            "checks the vertices and draws NOTHING - no graphic, nothing written to the save - so a half-built "
            "polygon can be checked before it is drawn. Success looks like a reply with the drawing's id and the "
            "layer's graphics count."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "points": POLYGON_POINTS_TEXT,
                "validate_only": VALIDATE_ONLY,
                "persist": DRAW_PERSIST,
            },
            "required": ["points"],
        },
        "handler": t_plot_polygon,
    },
    {
        "name": "sp_plot_text",
        "description": (
            "WRITE - THIS DRAWS A TEXT LABEL AT A GEOGRAPHIC POINT ON THE PLAYER'S PLOTTING TABLE. text is drawn "
            "as its own graphic anchored at lat/lon: use it for a caption that belongs at a place rather than on "
            "the map edge - a name for a strait, a note about what was seen, the time a contact was last heard. "
            "It is not a chat message and it does not abbreviate anything, so keep it short enough to read at "
            "whatever zoom the player is using. The label is a graphic in the plotting layer, and the plotting "
            "layer's graphics are serialised into the player's save file, automatic save included, so the text is "
            "on the chart again after a reload; the reply's savedIntoAutoSave flag is where the bridge says "
            "whether it went in. This one takes no persist switch - the bridge decides, and reads it back in that "
            "flag. validate_only=true checks the anchor and the text and draws NOTHING: nothing joins the layer "
            "and nothing reaches the save. Success looks like a reply carrying the drawing's id and the layer's "
            "graphics count; read sp_plot_drawings back to confirm the label exists before reporting it."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "lat": LAT,
                "lon": LON,
                "text": {"type": "string", "description": "The label to draw. Short - it is rendered at the anchor point, not wrapped around it."},
                "validate_only": VALIDATE_ONLY,
            },
            "required": ["lat", "lon", "text"],
        },
        "handler": t_plot_text,
    },
    {
        "name": "sp_plot_drawings",
        "description": (
            "READ-ONLY - LISTS WHAT IS CURRENTLY DRAWN ON THE PLAYER'S PLOTTING TABLE. This is the read-back for "
            "the five draw tools: call it to confirm a drawing exists rather than trusting the draw reply, and to "
            "get an id to erase. Each entry carries the drawing's id, its kind and whether the agent or the player "
            "put it there - that ownership is the same distinction sp_plot_erase's all_mine uses, and it is why "
            "you read the list instead of guessing. The count is the whole plotting graphics layer, not just your "
            "own work: the player's hand-drawn graphics live in the same collection, so a count larger than the "
            "number of things you drew is normal and is not a sign that something you drew was duplicated. Because "
            "it covers the player's drawings, the list also tells you what an index-based erase would address - "
            "index is a position in this list, and the list shifts whenever the player draws or deletes by hand. "
            "What this list is NOT is the save: a drawing shows up here the moment it exists, while whether it was "
            "written into the player's save is what a draw reply's savedIntoAutoSave flag reports. "
            "Nothing is read from the simulation, so this works with the plot closed or the game paused, and it "
            "changes nothing: no graphic is added, removed or written to the save."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "limit": DRAW_LIMIT,
            },
        },
        "handler": t_plot_drawings,
    },
    {
        "name": "sp_plot_erase",
        "description": (
            "WRITE, DESTRUCTIVE - THIS REMOVES DRAWING GRAPHICS FROM THE PLAYER'S PLOTTING TABLE. Exactly one "
            "selector: id (an integer a draw reply or sp_plot_drawings handed you), index (a position in that "
            "list), or all_mine=true. The three are NOT interchangeable, and the difference is the whole point of "
            "this paragraph. index addresses the plotting graphics layer by position, and that layer holds the "
            "player's own hand-drawn graphics as well as the agent's - so index can delete something the player "
            "drew, and the position means nothing until you have read sp_plot_drawings in the same moment, "
            "because it shifts the instant the player draws. all_mine=true removes only the drawings this bridge "
            "session made and leaves every player drawing alone: it is the safe bulk cleanup, and the one to use "
            "when you do not know which drawing is which. id is the narrowest of the three and the only one to use "
            "when you do know. There is deliberately no 'clear the whole layer' mode - a call that emptied the "
            "plotting graphics would take the player's own work with it. Nothing here can be undone through the "
            "MCP layer, but erase is also how you undo a drawing that should not have stayed: the layer's "
            "graphics are serialised into the player's save file, automatic save included, so anything left in it "
            "is in the player's save the next time the game writes one (savedIntoAutoSave on the draw reply is "
            "how the bridge reported that). Success looks like a reply carrying how many graphics were removed "
            "and the resulting count; removed 0 with ok:true means the selector matched nothing, which is a "
            "result, not a failure."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "id": {"type": "integer", "description": "id of one drawing, from a draw reply or sp_plot_drawings. The narrowest selector and the only one that cannot touch the player's own drawings."},
                "index": {"type": "integer", "minimum": 0, "description": "Position in the plotting layer's graphics list, in the order sp_plot_drawings reports. Addresses the whole layer, which includes graphics the player drew by hand."},
                "all_mine": {"type": "boolean", "description": "true = remove every drawing this bridge session made and leave the player's own drawings alone. The safe bulk cleanup."},
            },
        },
        "handler": t_plot_erase,
    },
    {
        "name": "sp_drop_sonobuoy",
        "description": (
            "WRITE - drop sonobuoys from an aircraft or helicopter onto a chosen point. This is the "
            "ASW detective work: a buoy that goes into the water starts listening, and its contact is "
            "what eventually lets a weapon release at all. The bridge builds the same task the game's "
            "own maritime-patrol AI builds (a position engage task, one buoy per task, priority 1000, "
            "autoAttack off), so it does not depend on the aircraft being in any particular patrol "
            "state.\n\n"
            "strategy=direct (default) expects the aircraft to be near the point already: it queues the "
            "drop against the aircraft's current position, which is right when you flew it there "
            "yourself. strategy=order makes the aircraft fly to the point and drop on arrival - use it "
            "when the aircraft is still far away; it is also the safer way to probe whether the hull "
            "carries the buoy at all, because the range check happens the same way the game does it. "
            "ammo is optional: omit it and the bridge picks the first sonobuoy the hull carries; name it "
            "(for example usn_ssq-53) when you care which one, and the reply lists what it chose.\n\n"
            "count>1 spreads the buoys evenly along the aircraft's heading through the point, 0.2 NM "
            "apart, one task each - deliberately not one task with a salvo, because the game treats some "
            "buoy rounds as single-shot and a salvo would either over-drop or drop nothing depending on "
            "the round's shape. depth sets which water layer the buoy listens in (AboveLayer / "
            "BelowLayer / Intermittent) and is best effort: a refusal is reported, never silently "
            "ignored.\n\n"
            "Read the reply's ammoBefore/ammoAfter: a buoy is only truly away when the magazine count "
            "falls, and that takes a moment, so verified=false with pending=true means 'submitted, not "
            "yet observed', not 'failed'. The reply also states position, dropped, and a verifyNote "
            "explaining exactly what was and was not proven. Nothing here draws on the plot; pair it "
            "with sp_plot_marker if the player should see where the buoys went."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "unit": {"type": "integer", "description": "UniqueID of the aircraft or helicopter that drops the buoys (the hull must carry sonobuoys)."},
                "lat": {"type": "number", "minimum": -90, "maximum": 90, "description": "Latitude of the drop point, degrees. Negative = south."},
                "lon": {"type": "number", "minimum": -180, "maximum": 180, "description": "Longitude of the drop point, degrees. Negative = west."},
                "ammo": {"type": "string", "description": "Sonobuoy ammunition name, for example usn_ssq-53. Omit to let the bridge pick the first sonobuoy the hull carries."},
                "count": {"type": "integer", "minimum": 1, "maximum": 32, "description": "How many buoys to drop, spread along the aircraft's heading through the point. Default 1."},
                "strategy": {"type": "string", "enum": ["direct", "order"], "description": "direct = drop now from where the aircraft is; order = fly to the point and drop on arrival. Default direct."},
                "depth": {"type": "string", "description": "Listening layer: AboveLayer / BelowLayer / Intermittent. Optional and best effort."},
                "validate_only": {"type": "boolean", "description": "true = check the request and the hull's loadout without dropping anything. Use it first when unsure whether the hull carries buoys."},
            },
            "required": ["unit", "lat", "lon"],
        },
        "handler": t_drop_sonobuoy,
    },
    {
        "name": "sp_inspector_window",
        "description": (
            "Open, show, hide or arrange the AGENT INSPECTOR window inside the game's "
            "tactical view, so the human can watch what this agent is doing while it "
            "works. This is the window with the event log, the per-event detail pane and "
            "the column of commands the agent issued. Use it when the player should see "
            "the work: install once, then hide/show as needed.\n\n"
            "op=install mounts it (idempotent; needs a mission view - at the main menu it "
            "answers 409 no_ui_host, which is a fact about the game and not something to "
            "retry in a loop). op=show/op=hide toggle visibility and keep the geometry. "
            "op=state returns sizes, the three readout groups (panes/columns/rows, the "
            "window's rect mapped into the tactical root, the ancestor clip chain) and "
            "which localization strings are loaded. op=geometry moves/resizes by pixels "
            "(left/top/width/height), op=split sets the dividers (x/y in pixels), "
            "op=select/detail_page pick an event and a page of its detail, op=language "
            "switches the window's own chrome between en and zh without rebuilding it. "
            "op=remount/maximize/restore/collapse/expand are the window-management verbs. "
            "A 4K game returns 3840x2160 pixels; the window is placed in that space.\n\n"
            "Nothing here reads or changes game state, so it is not a game command. If the "
            "task is to look at the battle, use sp_screenshot instead."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "op": {
                    "type": "string",
                    "enum": ["state", "install", "show", "hide", "remount", "geometry",
                             "split", "maximize", "restore", "collapse", "expand",
                             "select", "detail_page", "language"],
                    "description": "Default state (read only). install mounts the window; "
                                   "show/hide only toggle visibility.",
                },
                "left": {"type": "number", "description": "geometry: pixels from the left of the tactical root."},
                "top": {"type": "number", "description": "geometry: pixels from the top."},
                "width": {"type": "number", "description": "geometry: pixels wide (the window refuses below 520x320)."},
                "height": {"type": "number", "description": "geometry: pixels tall."},
                "x": {"type": "number", "description": "split: the right column width in pixels."},
                "y": {"type": "number", "description": "split: the timeline height in pixels."},
                "index": {"type": "integer", "description": "select/detail_page: a 0 based row or page. Fractions are refused, not truncated."},
                "page": {"type": "integer", "description": "detail_page: page number, 0 based."},
                "language": {"type": "string", "description": "language: en, zh, cn, auto, or game."},
                "save": {"type": "boolean", "description": "geometry/split: also write the position into setting.ini. Default false so an agent's experiment does not overwrite the player's layout."},
            },
            "additionalProperties": False,
        },
        "handler": t_inspector_window,
    },
    {
        "name": "sp_agent_panel",
        "description": (
            "The older one-line agent panel: a strip of text the bridge draws inside the "
            "game's tactical view, plus a text box the player can type into and submit. "
            "Use sp_inspector_window for narration and this panel only when a single line "
            "of status or a typed command channel is what is wanted.\n\n"
            "op=state reports whether the panel is installed and what it currently shows. "
            "op=install/remove add or take away exactly the instance this bridge owns. "
            "op=type puts text in the box, op=submit submits what is in the box, op=typing "
            "shows or hides the typing indicator, op=hold freezes the panel's own refresh "
            "so the player can read it without it changing under them (omitting 'on' "
            "flips it, exactly like clicking)."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "op": {
                    "type": "string",
                    "enum": ["state", "install", "remove", "hold", "type", "submit", "typing"],
                    "description": "Default state (read only).",
                },
                "text": {"type": "string", "description": "install: the panel's initial text. type: the text to put in the box."},
                "on": {"type": "boolean", "description": "hold/typing: true or false. Omit to flip."},
            },
            "additionalProperties": False,
        },
        "handler": t_agent_panel,
    },
    {
        "name": "sp_comms_state",
        "description": (
            "Read the harness communication link: whether the page is attached to a live "
            "session log, which session, how long since the last event, how many player lines "
            "are still queued, and the generation state (idle/working/executing) with the tool "
            "currently running. This is the honest half of the page: link, submission and "
            "generation are three separate fields because they fail separately."
        ),
        "inputSchema": {"type": "object", "properties": {}, "required": []},
        "handler": t_comms_state,
    },
    {
        "name": "sp_comms_page",
        "description": (
            "Read the harness conversation incrementally. Each item is one of: user (the "
            "player's line), assistant (the visible reply text the harness produced - hidden "
            "reasoning is never included), exec (a tool the harness started), execdone (that "
            "tool finished, with isError), turn (a turn started or ended). Pass the seq of the "
            "last item you already have as since to get only what is new."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "since": {"type": "integer", "description": "Only items with a greater seq. Default 0 (everything held)."},
                "limit": {"type": "integer", "description": "Maximum items to return. Default 200."},
            },
            "required": [],
        },
        "handler": t_comms_page,
    },
    {
        "name": "sp_comms_send",
        "description": (
            "Send a player line to the harness. The game process cannot reach the harness HTTP "
            "API, so this writes the text into the mod's outbox file and returns "
            "state=queued_for_harness - it does NOT claim the harness received it. Something "
            "outside the game has to pick the outbox up."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "text": {"type": "string", "description": "The message. Over 4000 characters is truncated and flagged."},
            },
            "required": ["text"],
        },
        "handler": t_comms_send,
    },
    {
        "name": "sp_comms_window",
        "description": (
            "Drive the in-game harness communication page: state (default) reads geometry and "
            "visibility, install mounts it, remove takes it out, toggle flips it, show sets it "
            "explicitly with on=true/false. The page has its own key (M by default) and never "
            "covers the inspector."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "op": {"type": "string", "enum": ["state", "install", "remove", "toggle", "show"],
                       "description": "What to do with the page. Default state."},
                "on": {"type": "boolean", "description": "For op=show: visible or not."},
            },
            "required": [],
        },
        "handler": t_comms_window,
    },
]

TOOLS_BY_NAME = dict((t["name"], t) for t in TOOLS)


# ------------------------------------------------------------------- jsonrpc


def send(msg):
    sys.stdout.write(json.dumps(msg, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def reply(req_id, result):
    send({"jsonrpc": "2.0", "id": req_id, "result": result})


def reply_error(req_id, code, message):
    send({"jsonrpc": "2.0", "id": req_id, "error": {"code": code, "message": message}})


def handle_tools_call(params):
    name = params.get("name")
    args = params.get("arguments") or {}
    tool = TOOLS_BY_NAME.get(name)
    if tool is None:
        return {
            "content": [{"type": "text", "text": "unknown tool: %s" % name}],
            "isError": True,
        }
    try:
        result = tool["handler"](args)
    except KeyError as e:
        return {"content": [{"type": "text", "text": "missing argument: %s" % e}], "isError": True}
    except Exception as e:  # noqa: BLE001
        return {"content": [{"type": "text", "text": "tool failed: %r" % e}], "isError": True}

    if name in compact_replies.TOOLS and compact_replies.enabled():
        if name == "sp_tracks":
            result = TRACK_MOTION.annotate(result)
        projected = compact_replies.project(name, result)
        if projected is not result:
            text = json.dumps(projected, ensure_ascii=False, separators=(",", ":"))
            return {"content": [{"type": "text", "text": text}], "isError": False}

    text = json.dumps(result, ensure_ascii=False, indent=2)
    is_error = bool(result.get("ok") is False) if isinstance(result, dict) else False

    # An image has to leave this process as image content, not as a JSON field: base64
    # inside a text block reaches the model as a wall of characters it cannot look at.
    if isinstance(result, dict) and "__image_b64__" in result:
        meta = result.get("__image_meta__") or {}
        return {
            "content": [
                {"type": "text", "text": json.dumps(meta, ensure_ascii=False, indent=2)},
                {"type": "image",
                 "data": result["__image_b64__"],
                 "mimeType": result.get("__image_mime__") or "image/png"},
            ],
            "isError": False,
        }

    return {"content": [{"type": "text", "text": text}], "isError": is_error}


def handle(msg):
    method = msg.get("method")
    req_id = msg.get("id")
    params = msg.get("params") or {}

    if method == "initialize":
        return reply(
            req_id,
            {
                "protocolVersion": PROTOCOL_VERSION,
                "capabilities": {"tools": {"listChanged": False}},
                "serverInfo": {"name": SERVER_NAME, "version": SERVER_VERSION},
            },
        )
    if method == "notifications/initialized":
        return None
    if method == "ping":
        return reply(req_id, {})
    if method == "tools/list":
        return reply(
            req_id,
            {
                "tools": [
                    {"name": t["name"], "description": t["description"], "inputSchema": t["inputSchema"]}
                    for t in TOOLS
                ]
            },
        )
    if method == "tools/call":
        return reply(req_id, handle_tools_call(params))
    if method == "resources/list":
        return reply(req_id, {"resources": []})
    if method == "prompts/list":
        return reply(req_id, {"prompts": []})
    if method == "shutdown":
        return reply(req_id, {})
    if req_id is None:
        return None
    return reply_error(req_id, -32601, "method not found: %s" % method)


def main():
    log("starting, bridge at 127.0.0.1:%d" % PORT)
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
        except Exception as e:  # noqa: BLE001
            log("bad json: %r" % e)
            continue
        try:
            handle(msg)
        except Exception as e:  # noqa: BLE001
            log("handler error: %r" % e)
            if msg.get("id") is not None:
                reply_error(msg["id"], -32603, "internal error: %r" % e)


if __name__ == "__main__":
    main()
