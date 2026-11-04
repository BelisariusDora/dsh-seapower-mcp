"""Conservative motion flags derived from player-side track fixes."""
import configparser
import math
from collections import Counter
from itertools import combinations

from compact_replies import SETTINGS

DEFAULTS = {
    "SurfaceFormationNm": 2.0,
    "AirFormationNm": 4.0,
    "SurfaceHighSpeedKts": 30.0,
}
MAX_GAP = 40.0
DOMAINS = {"surface_ship", "aircraft"}


def settings(path=SETTINGS):
    config = configparser.ConfigParser(interpolation=None, inline_comment_prefixes=(";", "#"))
    try:
        with path.open(encoding="utf-8-sig") as stream:
            config.read_file(stream)
    except (OSError, UnicodeError, configparser.Error):
        return DEFAULTS
    values = {}
    for key, default in DEFAULTS.items():
        try:
            value = config.getfloat("TrackMotion", key, fallback=default)
            values[key] = value if math.isfinite(value) and value > 0 else default
        except ValueError:
            values[key] = default
    return values


def numeric(value):
    return isinstance(value, (float, int)) and not isinstance(value, bool) and math.isfinite(value)


def offset(a, b):
    """Local east/north displacement in nautical miles, for short track legs."""
    lat, lon = a
    next_lat, next_lon = b
    longitude = (next_lon - lon + 180) % 360 - 180
    return longitude * 60 * math.cos(math.radians((lat + next_lat) / 2)), (next_lat - lat) * 60


def segment(a, b):
    dt = b[0] - a[0]
    if not 1 <= dt <= MAX_GAP:
        return None
    east, north = offset(a[1], b[1])
    return math.hypot(east, north) * 3600 / dt, math.degrees(math.atan2(east, north))


def heading_difference(a, b):
    return abs((a - b + 180) % 360 - 180)


def formation(a, b, tracks, legs, threshold, heading_limit, min_speed):
    if not all(speed >= min_speed for tid in (a, b) for speed, _ in legs[tid]):
        return False
    if any(heading_difference(x[1], y[1]) > heading_limit for x, y in zip(legs[a], legs[b])):
        return False
    pairs = list(zip(tracks[a], tracks[b]))
    if any(abs(x[0] - y[0]) > 3 for x, y in pairs):
        return False
    shifts = [offset(x[1], y[1]) for x, y in pairs]
    if any(math.hypot(*shift) > threshold for shift in shifts):
        return False
    x, y = shifts[0]
    return all(math.hypot(dx - x, dy - y) <= threshold * 0.35 for dx, dy in shifts[1:])


class TrackMotion:
    def __init__(self, config=None):
        self.config = config
        self.generation = None
        self.history = {}
        self.clock = None

    def annotate(self, reply, generation=None):
        """Add a boolean to unknown contacts. False also covers insufficient evidence."""
        data = reply.get("data") if isinstance(reply, dict) and reply.get("ok") is True else None
        tracks = data.get("tracks") if isinstance(data, dict) else None
        if not isinstance(tracks, list) or not all(isinstance(t, dict) for t in tracks):
            return reply
        if generation is None:
            generation = data.get("worldGeneration")
        now = max((t["missionSeconds"] for t in tracks if numeric(t.get("missionSeconds"))), default=None)
        if generation is None or generation != self.generation or (
                now is not None and self.clock is not None and now < self.clock):
            self.history.clear()
        self.generation = generation
        self.clock = now
        cfg = self.config if self.config is not None else settings()
        ids = Counter(t.get("trackId") for t in tracks)
        objects = Counter(t.get("objectId") for t in tracks if t.get("objectId") is not None)
        following = {}
        fixes = {}
        legs = {}
        domains = {}
        for t in tracks:
            tid, obj, domain = (t.get(k) for k in ("trackId", "objectId", "knownClass"))
            if (generation is None or t.get("side") != "Unknown" or domain not in DOMAINS or
                    tid is None or ids[tid] != 1 or (obj is not None and objects[obj] != 1) or
                    t.get("positionSource") != "detected" or t.get("fresh") is not True):
                continue
            lat, lon, mission, age = (t.get(k) for k in ("lat", "lon", "missionSeconds", "lastDetectedAgeSec"))
            if not all(numeric(v) for v in (lat, lon, mission, age)) or not (
                    abs(lat) <= 90 and abs(lon) <= 180 and age >= 0):
                continue
            stamp = mission - age
            key = (obj, domain)
            previous_key, prior = self.history.get(tid, (None, []))
            prior = prior if previous_key == key else []
            sample = (stamp, (lat, lon))
            if not prior or stamp < prior[-1][0] - 0.05:
                prior = [sample]
            elif stamp > prior[-1][0] + 0.05:
                leg = segment(prior[-1], sample)
                speed_limit = 1400 if domain == "aircraft" else 90
                if leg is not None and leg[0] <= speed_limit:
                    prior = (prior + [sample])[-3:]
                elif stamp - prior[-1][0] >= 1:
                    prior = [sample]
            following[tid] = (key, prior)
            if len(prior) == 3:
                fixes[tid] = prior
                legs[tid] = [segment(a, b) for a, b in zip(prior, prior[1:])]
                domains[tid] = domain
        self.history = following

        suspects = set()
        for domain, threshold, heading_limit, min_speed in (
                ("surface_ship", cfg["SurfaceFormationNm"], 15, 5),
                ("aircraft", cfg["AirFormationNm"], 20, 80)):
            candidates = [tid for tid in fixes if domains[tid] == domain]
            for a, b in combinations(candidates, 2):
                if formation(a, b, fixes, legs, threshold, heading_limit, min_speed):
                    suspects.update((a, b))
        for tid, history in fixes.items():
            if domains[tid] != "surface_ship" or not all(
                    speed >= cfg["SurfaceHighSpeedKts"] for speed, _ in legs[tid]):
                continue
            nearby = any(
                other != tid and domains[other] == "surface_ship" and
                math.hypot(*offset(history[-1][1], fixes[other][-1][1])) <= cfg["SurfaceFormationNm"] and
                heading_difference(legs[tid][-1][1], legs[other][-1][1]) <= 15
                for other in fixes
            )
            if not nearby:
                suspects.add(tid)
        return {**reply, "data": {**data, "tracks": [
            {**t, "suspicious": t.get("trackId") in suspects} if t.get("side") == "Unknown" else t
            for t in tracks
        ]}}
