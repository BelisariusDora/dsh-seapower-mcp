"""Optional, stateless field projection for high-volume MCP read replies."""
import configparser
from pathlib import Path

TOOLS = frozenset(("sp_tracks", "sp_session", "sp_deck", "sp_ammo", "sp_session_fleet"))
from runtime_layout import configuration_root
SETTINGS = configuration_root() / "setting.ini"

TRACK_FIELDS = (
    "trackId", "objectId", "side", "reportedBy", "type", "class", "knownClass",
    "observedClass", "identified", "classified", "fresh", "live", "visual",
    "lat", "lon", "positionSource", "missionSeconds", "lastDetectedAgeSec",
    "updateEpoch", "detectionKind", "candidateCount", "candidatesTruncated",
    "intelPolicy", "suspicious",
)


def enabled(path=SETTINGS):
    config = configparser.ConfigParser(interpolation=None, inline_comment_prefixes=(";", "#"))
    try:
        with path.open(encoding="utf-8-sig") as stream:
            config.read_file(stream)
        return config.get("MCP", "CompactReplies", fallback="false").strip().lower() == "true"
    except (OSError, UnicodeError, configparser.Error):
        return False


def pick(source, fields):
    return {key: source[key] for key in fields if key in source}


def project(tool, reply):
    """Return a projected reply, or the original object for an unsupported shape."""
    if tool not in TOOLS or not isinstance(reply, dict) or reply.get("ok") is not True:
        return reply
    data = reply.get("data")
    if not isinstance(data, dict) or data.get("redacted") is True:
        return reply
    if tool == "sp_tracks":
        tracks = data.get("tracks")
        if not isinstance(tracks, list) or not all(isinstance(track, dict) for track in tracks):
            return reply
        compact = pick(data, ("scope", "count", "intel", "revealSwitch", "positionFace", "limited", "worldGeneration"))
        compact["tracks"] = []
        for track in tracks:
            row = pick(track, TRACK_FIELDS)
            candidates = track.get("candidates")
            if isinstance(candidates, list):
                row["candidates"] = [
                    pick(item, ("classRef", "objectType")) for item in candidates if isinstance(item, dict)
                ]
            compact["tracks"].append(row)
    elif tool == "sp_session":
        if "worldGeneration" not in data:
            return reply
        compact = {key: value for key, value in data.items() if key not in ("history", "marks")}
    elif tool == "sp_session_fleet":
        units = data.get("units")
        if "worldGeneration" not in data or not isinstance(units, list) or not all(isinstance(unit, dict) for unit in units):
            return reply
        compact = {key: value for key, value in data.items() if key != "note"}
        # The endpoint only includes owned units. Other flags distinguish land,
        # surface, sub and air, and optional order/sensor data remains intact.
        compact["units"] = [
            {key: value for key, value in unit.items() if key not in ("mine", "enemy", "isUnit")}
            for unit in units
        ]
    elif tool == "sp_deck":
        deck = data.get("deck")
        if not isinstance(deck, dict):
            if data.get("hasFlightDeck") is not False:
                return reply
            compact = {key: value for key, value in data.items() if key != "readPath"}
        else:
            onboard = deck.get("airgroupOnBoard")
            if not isinstance(onboard, list) or not all(isinstance(aircraft, dict) for aircraft in onboard):
                return reply
            compact = {key: value for key, value in data.items() if key != "readPath"}
            compact["deck"] = {key: value for key, value in deck.items()
                               if key not in ("supportedAircraftTypes", "aircraftCurrentlyOnBoardField")}
            compact["deck"]["airgroupOnBoard"] = [
                {key: value for key, value in aircraft.items() if key != "clrType"} for aircraft in onboard
            ]
    else:  # sp_ammo already has a minimal schema. Keep all IDs and counts.
        if not isinstance(data.get("ammo"), dict):
            return reply
        compact = data
    return {**reply, "data": compact}
