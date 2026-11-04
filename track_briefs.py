"""Stateful, read-only player track brief with a standard-library Tiny MLP."""
import json
import math
from pathlib import Path

import compact_replies

MODEL_PATH = Path(__file__).with_name("track_mlp_weights.json")
BRIEF_BYTES = 800
VOLATILE = {"missionSeconds", "lastDetectedAgeSec", "updateEpoch", "lat", "lon"}
IDENTITY = {"side", "class", "knownClass", "identified", "observedClass"}
FEATURES = (
    "enemy", "unknown_side", "added", "removed", "identity_changed", "live_changed",
    "fresh_changed", "moved", "aircraft", "surface_ship", "land", "submarine",
    "identified", "unresolved", "own_sensor", "detected", "suspicious",
)


def feature_vector(track, kind, fields):
    domain = track.get("knownClass")
    return (
        track.get("side") == "Enemy", track.get("side") == "Unknown",
        kind in ("added", "reappeared"), kind == "removed_from_snapshot",
        bool(fields & IDENTITY), "live" in fields, "fresh" in fields, "lat" in fields,
        domain == "aircraft", domain == "surface_ship", domain == "land_unit",
        domain in ("submarine", "sub"), track.get("identified") is True,
        track.get("identification_unresolved") is True, False,
        track.get("positionSource") == "detected", track.get("suspicious") is True,
    )


def moved(previous, current):
    points = [row.get(key) for row in (previous, current) for key in ("lat", "lon")]
    if any(point is None for point in points):
        return points[:2] != points[2:]
    a, b, c, d = map(math.radians, points)
    hav = math.sin((c-a)/2)**2 + math.cos(a)*math.cos(c)*math.sin((d-b)/2)**2
    return 6880.13 * math.asin(math.sqrt(min(1, max(0, hav)))) >= 0.1


def describe(track, kind, fields, previous):
    identity = track.get("objectId")
    if previous is not None and "objectId" in fields:
        identity = f"{previous.get('objectId')}->{identity}"
    classification = track.get("knownClass") or "unknown"
    if not track.get("identification_unresolved"):
        classification = track.get("observedClass") or track.get("class") or classification
    position = ""
    if track.get("lat") is not None and track.get("lon") is not None:
        position = f" {track['lat']:.4f},{track['lon']:.4f}"
    if track.get("suspicious"):
        classification += " [suspicious]"
    detail = " " + ",".join(sorted(fields)) if fields else ""
    return f"{track['trackId']}/{identity} {kind} {track.get('side') or '?'} {classification}{position}{detail}"


class TrackBriefs:
    def __init__(self):
        self.model = None
        self.generation = None
        self.previous = {}
        self.seen = set()

    def score(self, inputs):
        if self.model is None:
            model = json.loads(MODEL_PATH.read_text(encoding="utf-8"))
            if (tuple(model["features"]) != FEATURES or len(model["w"]) != len(FEATURES)
                    or len(model["b"]) != 8 or len(model["head"]) != 8
                    or any(len(row) != 8 for row in model["w"])):
                raise ValueError("Track model feature or layer shape mismatch")
            self.model = model
        m = self.model
        return sum(weight * math.tanh(bias + sum(row[j] for value, row in zip(inputs, m["w"]) if value))
                   for j, (weight, bias) in enumerate(zip(m["head"], m["b"])))

    def build(self, reply, generation):
        projected = compact_replies.project("sp_tracks", reply)
        if projected is reply:
            return {"ok": False, "error": "Unrecognized tracks response; use sp_tracks."}
        data = projected["data"]
        current = {}
        for track in data["tracks"]:
            if track.get("trackId") is None or track["trackId"] in current:
                return {"ok": False, "error": "Missing or duplicate track ID; use sp_tracks."}
            if track.get("candidatesTruncated") or (track.get("candidateCount") or 0) > 1:
                track.pop("candidates", None)
                track["identification_unresolved"] = True
            current[track["trackId"]] = track

        baseline = generation != self.generation
        previous = {} if baseline else self.previous
        seen = set() if baseline else self.seen
        optional, mandatory = [], []
        anchors = {}
        for tid, old in previous.items():
            if tid not in current:
                kind = "removed_from_snapshot"
                optional.append((self.score(feature_vector(old, kind, set())), describe(old, kind, set(), None)))
        for tid, track in current.items():
            old = previous.get(tid)
            fields = set()
            if old is None:
                kind = "baseline" if baseline else "reappeared" if tid in seen else "added"
            else:
                kind = "updated"
                fields = {key for key in (old.keys() | track.keys()) - VOLATILE if old.get(key) != track.get(key)}
                if moved(old, track):
                    fields.update(("lat", "lon"))
                if not fields:
                    anchors[tid] = old
                    continue
            anchors[tid] = track
            line = describe(track, kind, fields, old)
            if old is not None and old.get("objectId") is not None and "objectId" in fields:
                mandatory.append(line)
            else:
                optional.append((self.score(feature_vector(track, kind, fields)), line))

        brief = []
        size = 0
        for _, line in sorted(optional, key=lambda item: item[0], reverse=True):
            length = len(line.encode("utf-8")) + bool(brief)
            if size + length <= BRIEF_BYTES:
                brief.append(line)
                size += length
        self.generation = generation
        self.previous = anchors
        self.seen = seen | current.keys()
        return {"ok": True, "data": {
            "worldGeneration": generation, "trackCount": len(current), "scope": data.get("scope"),
            "mode": "baseline" if baseline else "delta", "model": "bdnetwork_11_4_mlp_run_01_motion",
            "brief": "\n".join(brief), "mustReport": mandatory,
            "omitted": len(optional) - len(brief), "ordinaryBudgetBytes": BRIEF_BYTES,
        }}
