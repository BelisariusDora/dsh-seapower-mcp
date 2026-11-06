"""Small, deterministic predicates for the TR1 speed ticket."""
import math


def number(value):
    return type(value) in (int, float) and math.isfinite(value)


def speed_contract(command, predicate):
    if not isinstance(command, dict) or command.get("kind") != "set_speed":
        raise ValueError("TR1 supports command.kind=set_speed only")
    if type(command.get("unit_id")) is not int or command["unit_id"] <= 0:
        raise ValueError("command.unit_id must be a current positive unit ID")
    knots = command.get("knots")
    if not number(knots) or not 0 <= knots <= 80:
        raise ValueError("command.knots must be finite and within 0..80")
    if not isinstance(predicate, dict) or predicate.get("kind") != "speed_reached":
        raise ValueError("An observable speed_reached predicate is required")
    if predicate.get("unit_id") != command["unit_id"] or predicate.get("target_knots") != knots:
        raise ValueError("Predicate unit and target must match the authorized command")
    tolerance = predicate.get("tolerance_knots", 0.5)
    if not number(tolerance) or not 0 < tolerance <= 1:
        raise ValueError("tolerance_knots must be within (0, 1]")
    return dict(command), {**predicate, "tolerance_knots": tolerance}


def evaluate_speed(unit, predicate):
    if unit.get("id") != predicate["unit_id"]:
        return "unavailable"
    if unit.get("destroyed") is True:
        return "impossible"
    speed = unit.get("speedKts")
    if not number(speed):
        return "unavailable"
    return "satisfied" if abs(speed - predicate["target_knots"]) <= predicate["tolerance_knots"] else "pending"


def check_target(unit, command):
    if (unit.get("id") != command["unit_id"] or unit.get("mine") is not True
            or unit.get("surface") is not True or unit.get("sub") is True
            or unit.get("air") is True or unit.get("isLand") is True
            or unit.get("destroyed") is not False):
        raise ValueError("TR1 requires a live, owned surface ship")
    maximum = unit.get("maxForwardKnots")
    if not number(maximum) or command["knots"] > maximum:
        raise ValueError("Requested speed exceeds the verified platform envelope")
    if not number(unit.get("speedKts")):
        raise ValueError("Current speed is unreadable")



def task_contract(command, predicate):
    if not isinstance(command, dict) or command.get('kind') != 'mcp_task':
        return speed_contract(command, predicate)
    import api_catalog
    tools = command.get('tools', ['*'])
    if not isinstance(tools, list) or not 1 <= len(tools) <= 64 or not all(isinstance(t, str) for t in tools):
        raise ValueError('tools must contain 1..64 API names, or ["*"] for business APIs')
    if tools != ['*']:
        registry = api_catalog.registry()
        if any(t not in registry or not api_catalog.business(t) for t in tools):
            raise ValueError('Unknown or worker-control API in tool scope')
    resources = command.get('resources', ['world'])
    if not isinstance(resources, list) or not 1 <= len(resources) <= 32 or not all(isinstance(r,str) and 0 < len(r) <= 80 for r in resources):
        raise ValueError('resources must contain 1..32 short names; default world is exclusive')
    maximum = command.get('max_calls', 32)
    if type(maximum) is not int or not 1 <= maximum <= 64:
        raise ValueError('max_calls must be 1..64')
    if predicate is not None and predicate != {'kind':'execution_recorded'}:
        raise ValueError('Generic tasks use execution_recorded; do not claim game completion')
    return {'kind':'mcp_task','tools':list(dict.fromkeys(tools)),
            'resources':list(dict.fromkeys(resources)),'max_calls':maximum}, {'kind':'execution_recorded'}


def resources(command):
    return set(command.get('resources', ['unit:'+str(command['unit_id'])] if 'unit_id' in command else ['world']))


def conflicts(first, second):
    a, b = resources(first), resources(second)
    return 'world' in a or 'world' in b or bool(a & b)
