// Sea Power Agent Bridge - the plotting table: routes, rulers and range math.
// Compiled with C# 5 (framework csc.exe). No string interpolation, no ?., no nameof.
//
// Scope, from PLOT_CONTRACT.md appendix A:
//   * POST /plot/course - write a unit's route (1..40 waypoints);
//   * POST /plot/clear  - wipe a unit's route;
//   * GET  /plot/route  - read a unit's route back;
//   * POST /plot/ruler  - draw one range ruler on the live tactical map;
//   * GET  /plot/measure- pure distance/bearing math, no unit and no map needed.
//
// Two game behaviours are part of the answer, not trivia, because a caller that
// does not know them will misread what it just did:
//
//   1. In the game's own input path (SeapowerUI UnitSelectedState) the order
//      "add waypoint" first runs `if (!obj._isCurrentWaypointReachable)
//      obj.RemoveWaypoints();`, so clicking while under way wipes the whole old
//      route. That clearing lives in the UI handler, NOT in SetWaypointTask
//      (IL of SetWaypointTask only copies fields and calls UserRootNode.AddTask),
//      so a bridge call that appends really does append - and we prove it by
//      reading the route back and reporting `appendedTo` instead of asserting it.
//
//   2. A waypoint's height defaults to the unit's current height
//      (`_height = obj.Position.Value._height`). When the caller gives no height
//      we copy the unit's live height and say so with `heightDefaulted: true`.
//
// Everything is written by compiling against the game assemblies rather than by
// ad-hoc reflection: WaypointData's `_geoposition` and `_heightState` turned out
// to be public fields (the research note that said "42 private fields, reflection
// only" was wrong), and a compiler-checked assignment cannot silently miss.
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SeaPower;

namespace SeaPowerAgentBridge
{
    internal static class MapPlot
    {
        // Upper bound on one course order. Kept as a request-shape limit so a bad
        // caller cannot turn one HTTP call into hundreds of task insertions.
        private const int MaxPoints = 40;

        // Dead Sea shore to the top of the operational envelope: wide enough to
        // accept every altitude the game can express, narrow enough that a typo
        // (or a metres/feet mix-up) is refused instead of silently accepted.
        private const double MinHeight = -12000.0;
        private const double MaxHeight = 100000.0;

        // Matches the game's own ruler drawing (PLOT_MAPCONTROL.md: StrokeThickness = 2f).
        private const float DefaultThickness = 2f;

        // Per-op field whitelists. The contract's list (unit, points, replace,
        // height_state, validate_only, fromLat, fromLon, toLat, toLon, stroke,
        // thickness) is the union of these; each op only accepts its own slice so a
        // field aimed at the wrong route is reported instead of silently ignored.
        private static readonly string[] CourseFields = new string[]
        {
            "unit", "points", "replace", "height_state", "validate_only"
        };

        private static readonly string[] ClearFields = new string[]
        {
            "unit", "validate_only"
        };

        private static readonly string[] RouteFields = new string[]
        {
            "unit"
        };

        private static readonly string[] RulerFields = new string[]
        {
            "fromLat", "fromLon", "toLat", "toLon", "stroke", "thickness", "validate_only"
        };

        private static readonly string[] MeasureFields = new string[]
        {
            "fromLat", "fromLon", "toLat", "toLon"
        };

        // Height states in the order the contract lists them. Values come from the
        // enum itself so a reordered enum cannot silently shift a name to a wrong
        // number; the names are what the caller sends.
        private static readonly string[] HeightStateNames = new string[]
        {
            "Number", "Surface", "Periscope", "VeryLow", "Low", "Medium", "High",
            "VeryHigh", "Cruise", "Ceiling", "Current", "Parent", "NoChange"
        };

        // ---------------------------------------------------------------- helpers

        /// <summary>One waypoint from the request, already validated. Kept separate
        /// from WaypointData so validate_only never needs a game object.</summary>
        private sealed class PlotPoint
        {
            public double Lat;
            public double Lon;
            public bool HasHeight;
            public double Height;
        }

        /// <summary>A value that the caller actually sent. A JSON null counts as
        /// absent: it cannot be told apart from "not sent" by intent, and treating
        /// it as absent keeps one rule instead of two.</summary>
        private static JToken Present(JObject req, string name)
        {
            if (req == null) return null;
            JToken t = req[name];
            if (t == null) return null;
            if (t.Type == JTokenType.Null) return null;
            return t;
        }

        private static void RejectUnknown(JObject req, string op, string[] allowed)
        {
            if (req == null) return;
            foreach (JProperty p in req.Properties())
            {
                bool known = false;
                for (int i = 0; i < allowed.Length; i++)
                {
                    if (allowed[i] == p.Name) { known = true; break; }
                }
                if (!known)
                    throw new ApiException(400, "unknown_field",
                        "op '" + op + "' does not accept field '" + p.Name + "'", false);
            }
        }

        /// <summary>Numbers only. GET query parameters are decoded by Api.QueryObject
        /// into JValue doubles, POST bodies keep whatever JSON type the client sent,
        /// so both Integer and Float are accepted here.</summary>
        private static double NumberField(JToken t, string name)
        {
            if (t.Type != JTokenType.Integer && t.Type != JTokenType.Float)
                throw new ApiException(400, "bad_type",
                    "'" + name + "' must be a number, got " + t.Type.ToString(), false);
            double d;
            try { d = t.Value<double>(); }
            catch (Exception)
            {
                throw new ApiException(400, "bad_type", "'" + name + "' is not a valid number", false);
            }
            if (double.IsNaN(d) || double.IsInfinity(d))
                throw new ApiException(400, "bad_value", "'" + name + "' must be finite", false);
            return d;
        }

        private static double RequireNumber(JObject req, string name, string op)
        {
            JToken t = Present(req, name);
            if (t == null)
                throw new ApiException(400, "missing_field",
                    "'" + name + "' is required for " + op, false);
            return NumberField(t, name);
        }

        private static double OptionalNumber(JObject req, string name, double dflt)
        {
            JToken t = Present(req, name);
            if (t == null) return dflt;
            return NumberField(t, name);
        }

        /// <summary>Whole numbers only. A GET parameter such as ?unit=5 arrives as a
        /// JValue double, so an integral double is accepted and a fractional one is
        /// refused rather than truncated.</summary>
        private static int WholeField(JToken t, string name)
        {
            double d = NumberField(t, name);
            if (d != Math.Floor(d))
                throw new ApiException(400, "bad_type",
                    "'" + name + "' must be a whole number, got " + d, false);
            if (d < -2147483648.0 || d > 2147483647.0)
                throw new ApiException(400, "bad_value", "'" + name + "' is out of integer range", false);
            return (int)d;
        }

        private static int RequireInt(JObject req, string name, string op)
        {
            JToken t = Present(req, name);
            if (t == null)
                throw new ApiException(400, "missing_field",
                    "'" + name + "' is required for " + op, false);
            return WholeField(t, name);
        }

        private static bool OptionalBool(JObject req, string name, bool dflt)
        {
            JToken t = Present(req, name);
            if (t == null) return dflt;
            if (t.Type != JTokenType.Boolean)
                throw new ApiException(400, "bad_type",
                    "'" + name + "' must be true or false, got " + t.Type.ToString(), false);
            return (bool)t;
        }

        private static void CheckLatLon(double lat, double lon, string label)
        {
            if (lat < -90.0 || lat > 90.0)
                throw new ApiException(400, "bad_value",
                    label + " latitude must be within -90..90, got " + lat, false);
            if (lon < -180.0 || lon > 180.0)
                throw new ApiException(400, "bad_value",
                    label + " longitude must be within -180..180, got " + lon, false);
        }

        /// <summary>Resolves a unit id the same way /units does, so a plot command
        /// and a /units read can never disagree about what exists.
        ///
        /// REVIEW (round 2, blocker 1): this used to stop at GameReflect.UnitById, so
        /// GET /plot/route returned any hull's true waypoints - a detected foreign hull's route
        /// included - and its miss said "no live object with UniqueID N" while /units said
        /// "unit not found: N". Both halves are the same question, so both routes ask Api.</summary>
        private static ObjectBase RequireUnit(int unitId)
        {
            return Api.FindKnownUnit(unitId);
        }

        /// <summary>The same gate GuardedRead applies, for plot writes and route reads.
        /// A plot command is a write, and the game ignores orders to allies and refuses to
        /// nobody: the bridge has to say no itself, in the wording the rest of the build uses.</summary>
        private static void RequireOwnUnit(ObjectBase unit, int unitId, string what)
        {
            if (Api.SafeFlag(unit, "mine")) return;
            throw new ApiException(409, "not_your_unit",
                what + " writes only to the player's own units; unique id " + unitId
                + " is not one of them (the game would silently ignore it on an ally and treat it"
                + " as hostile on a neutral)", false);
        }

        /// <summary>ExportWaypoints is the game's own serializer for a route, so it is
        /// also the honest read-back.
        ///
        /// REVIEW (round 2, hint 12): this used to swallow the exception and return an empty list,
        /// and the callers used that list as fact - so a throw read back as "route is empty" and
        /// /plot/clear reported verified:true having cleared nothing. A read that failed is now
        /// null, and every caller has to say "unknown" instead of inventing zero.</summary>
        private static List<WaypointData> ExportRoute(ObjectBase unit)
        {
            try
            {
                return unit.ExportWaypoints();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static int CountByGame(ObjectBase unit)
        {
            try { return unit.GetNumberOfWaypoints(); }
            catch (Exception) { return -1; }
        }

        /// <summary>A waypoint count as JSON, where a negative count means "could not be read"
        /// and must never be confused with zero. Every caller already guards with its own
        /// readability flag; this is the second line of defence, so a missed flag cannot turn
        /// "ExportWaypoints threw" into a confident 0.</summary>
        private static JToken Tok(int count)
        {
            if (count < 0) return JValue.CreateNull();
            return new JValue(count);
        }

        /// <summary>The unit's live geo position, which is exactly what the game's own
        /// waypoint creator copies into a new waypoint's height.</summary>
        private static double UnitHeight(ObjectBase unit, out bool readable)
        {
            readable = false;
            try
            {
                GeoPosition geo = unit._geoPosition;
                readable = true;
                return geo._height;
            }
            catch (Exception)
            {
                return 0.0;
            }
        }

        private static string HeightStateName(int value)
        {
            if (value >= 0 && value < HeightStateNames.Length) return HeightStateNames[value];
            return value.ToString();
        }

        private static int HeightStateValue(int index)
        {
            switch (index)
            {
                case 0: return (int)WaypointData.WaypointHeightState.Number;
                case 1: return (int)WaypointData.WaypointHeightState.Surface;
                case 2: return (int)WaypointData.WaypointHeightState.Periscope;
                case 3: return (int)WaypointData.WaypointHeightState.VeryLow;
                case 4: return (int)WaypointData.WaypointHeightState.Low;
                case 5: return (int)WaypointData.WaypointHeightState.Medium;
                case 6: return (int)WaypointData.WaypointHeightState.High;
                case 7: return (int)WaypointData.WaypointHeightState.VeryHigh;
                case 8: return (int)WaypointData.WaypointHeightState.Cruise;
                case 9: return (int)WaypointData.WaypointHeightState.Ceiling;
                case 10: return (int)WaypointData.WaypointHeightState.Current;
                case 11: return (int)WaypointData.WaypointHeightState.Parent;
                case 12: return (int)WaypointData.WaypointHeightState.NoChange;
            }
            return -1;
        }

        /// <summary>height_state accepts the enum's own names (case-insensitive) or its
        /// numeric value. Anything else is refused by name so the caller can see the
        /// accepted set instead of guessing.</summary>
        private static int ParseHeightState(JObject req)
        {
            JToken t = Present(req, "height_state");
            if (t == null) return HeightStateValue(0);
            if (t.Type == JTokenType.String)
            {
                string name = (string)t;
                for (int i = 0; i < HeightStateNames.Length; i++)
                {
                    if (string.Equals(HeightStateNames[i], name, StringComparison.OrdinalIgnoreCase))
                        return HeightStateValue(i);
                }
                throw new ApiException(400, "bad_value",
                    "'height_state' must be one of " + string.Join(", ", HeightStateNames)
                    + " (case-insensitive), got '" + name + "'", false);
            }
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float)
            {
                int v = WholeField(t, "height_state");
                for (int i = 0; i < HeightStateNames.Length; i++)
                {
                    if (HeightStateValue(i) == v) return v;
                }
                throw new ApiException(400, "bad_value",
                    "'height_state' numeric value " + v + " is not a WaypointHeightState (0..12)", false);
            }
            throw new ApiException(400, "bad_type",
                "'height_state' must be a name or an integer, got " + t.Type.ToString(), false);
        }

        private static List<PlotPoint> ParsePoints(JObject req)
        {
            JToken raw = Present(req, "points");
            if (raw == null)
                throw new ApiException(400, "missing_field", "'points' is required for plot/course", false);
            JArray arr = raw as JArray;
            if (arr == null)
                throw new ApiException(400, "bad_type",
                    "'points' must be an array of {lat,lon,height?} objects, got " + raw.Type.ToString(), false);
            if (arr.Count < 1)
                throw new ApiException(400, "bad_value", "'points' must contain at least one waypoint", false);
            if (arr.Count > MaxPoints)
                throw new ApiException(400, "bad_value",
                    "'points' may contain at most " + MaxPoints + " waypoints, got " + arr.Count, false);

            List<PlotPoint> points = new List<PlotPoint>(arr.Count);
            for (int i = 0; i < arr.Count; i++)
            {
                JObject o = arr[i] as JObject;
                if (o == null)
                    throw new ApiException(400, "bad_type",
                        "points[" + i + "] must be an object {lat,lon,height?}", false);
                foreach (JProperty p in o.Properties())
                {
                    if (p.Name != "lat" && p.Name != "lon" && p.Name != "height")
                        throw new ApiException(400, "unknown_field",
                            "points[" + i + "] does not accept field '" + p.Name + "'", false);
                }
                PlotPoint pp = new PlotPoint();
                pp.Lat = RequireNumber(o, "lat", "points[" + i + "]");
                pp.Lon = RequireNumber(o, "lon", "points[" + i + "]");
                CheckLatLon(pp.Lat, pp.Lon, "points[" + i + "]");
                JToken h = Present(o, "height");
                if (h != null)
                {
                    pp.HasHeight = true;
                    pp.Height = NumberField(h, "points[" + i + "].height");
                    if (pp.Height < MinHeight || pp.Height > MaxHeight)
                        throw new ApiException(400, "bad_value",
                            "points[" + i + "].height must be within " + MinHeight + ".." + MaxHeight
                            + " metres, got " + pp.Height, false);
                }
                points.Add(pp);
            }
            return points;
        }

        /// <summary>Parses {lat,lon} style corners shared by ruler and measure.</summary>
        private static void ParseCorner(JObject req, string latKey, string lonKey, out double lat, out double lon)
        {
            lat = RequireNumber(req, latKey, "the request");
            lon = RequireNumber(req, lonKey, "the request");
            CheckLatLon(lat, lon, "'" + latKey + "'/'" + lonKey + "'");
        }

        // ------------------------------------------------------------ /plot/course

        /// <summary>POST /plot/course - write a route for one unit.
        ///
        /// The order of operations matters: RemoveWaypoints() (when replace) and the
        /// per-point SetWaypointTask both happen before the single read-back, so the
        /// answer describes the finished state, not an intermediate one.</summary>
        internal static JObject Course(JObject req)
        {
            if (!Core.IsMain)
                throw new ApiException(500, "off_main_thread",
                    "plot/course must run on the Unity main thread", true);
            RejectUnknown(req, "plot/course", CourseFields);

            int unitId = RequireInt(req, "unit", "plot/course");
            bool replace = OptionalBool(req, "replace", true);
            bool validateOnly = OptionalBool(req, "validate_only", false);
            // HOLD is the operator's stop, and this route moves a hull: RemoveWaypoints plus
            // SetWaypointTask below are a real order, not a drawing. A dry run stays exempt
            // because it returns before touching the unit, which is also why the gate can sit
            // here rather than after the lookup.
            if (CommsBridge.Hold && !validateOnly)
                throw new ApiException(409, "held",
                    "operator HOLD is on: routes are refused until it is released", true);

            List<PlotPoint> points = ParsePoints(req);
            int heightState = ParseHeightState(req);

            // Read-only even in validate mode: catching a bad unit id here is worth
            // more than saving one lookup, and UnitById changes nothing.
            ObjectBase unit = RequireUnit(unitId);

            // REVIEW (round 3): ownership is answered BEFORE the validate_only return. A dry run
            // used to reply validated:true for somebody else's hull while plot/clear replied 409 for
            // the same unit - two answers to one question, and the permissive one teaches a caller
            // (an agent, typically) that the command is allowed.
            RequireOwnUnit(unit, unitId, "plot/course");

            if (validateOnly)
            {
                JObject v = new JObject();
                v["validated"] = true;
                v["unit"] = unitId;
                v["points"] = points.Count;
                v["replace"] = replace;
                v["heightState"] = HeightStateName(heightState);
                return v;
            }

            // REVIEW (round 2, blockers 9/11): ownership is answered before the type question, so
            // "wrong_unit_type" can never be the reply that distinguishes somebody else's hull.
            // (round 3: the call itself moved above the validate_only return.)

            // A unit with no UserRootNode has nowhere to put a task: SetWaypointTask
            // would dereference null. Refuse before changing anything.
            if (unit._userRoot == null)
                throw new ApiException(409, "wrong_unit_type",
                    "unit " + unitId + " has no UserRootNode, so it cannot hold a player route"
                    + " (is it alive and under player control?)", false);

            List<WaypointData> beforeList = ExportRoute(unit);
            bool beforeReadable = beforeList != null;
            int before = beforeReadable ? beforeList.Count : -1;
            bool reachableBefore = true;
            try { reachableBefore = unit._isCurrentWaypointReachable; }
            catch (Exception) { }

            int afterRemove = -1;
            bool afterRemoveReadable = true;
            if (replace)
            {
                unit.RemoveWaypoints();
                List<WaypointData> afterRemoveList = ExportRoute(unit);
                afterRemoveReadable = afterRemoveList != null;
                afterRemove = afterRemoveReadable ? afterRemoveList.Count : -1;
            }

            bool heightReadable;
            double unitHeight = UnitHeight(unit, out heightReadable);
            bool heightDefaulted = false;

            int made = 0;
            string failure = null;
            for (int i = 0; i < points.Count; i++)
            {
                PlotPoint pp = points[i];
                double height = pp.Height;
                if (!pp.HasHeight)
                {
                    height = unitHeight;
                    heightDefaulted = true;
                }
                try
                {
                    // Only two fields are written. WaypointData's own ctor leaves
                    // _setTelegraph at -100, and setWaypointTaskParameters only touches
                    // the speed setting when _setTelegraph > -2 (IL-verified), so the
                    // default already means "leave the telegraph alone" - overriding it
                    // would silently order AllStop (=0).
                    WaypointData wd = new WaypointData();
                    wd._geoposition = new GeoPosition(pp.Lat, pp.Lon, height);
                    wd._heightState = (WaypointData.WaypointHeightState)heightState;
                    unit.SetWaypointTask(wd);
                    made++;
                }
                catch (Exception ex)
                {
                    failure = "SetWaypointTask threw on points[" + i + "]: " + ex.GetType().Name
                        + ": " + ex.Message;
                    break;
                }
            }

            List<WaypointData> afterList = ExportRoute(unit);
            bool afterReadable = afterList != null;
            int after = afterReadable ? afterList.Count : -1;
            int afterByGame = CountByGame(unit);

            // What the route should look like if the engine took every order. When either read
            // failed there is no expectation to compare against, so nothing is claimed.
            bool expectedKnown = beforeReadable;
            int expected = (replace ? 0 : before) + made;
            bool verified = expectedKnown && afterReadable && after >= 1 && after >= expected;

            JObject o = new JObject();
            o["unit"] = unitId;
            o["requested"] = points.Count;
            o["ordered"] = made;
            o["before"] = beforeReadable ? Tok(before) : JValue.CreateNull();
            if (replace) o["afterRemove"] = afterRemoveReadable ? Tok(afterRemove) : JValue.CreateNull();
            o["after"] = afterReadable ? Tok(after) : JValue.CreateNull();
            o["expected"] = expectedKnown ? Tok(expected) : JValue.CreateNull();
            o["verified"] = verified;
            o["replace"] = replace;
            // Only meaningful without replace: did the old route survive and the new
            // waypoints land behind it? Reported, never assumed.
            o["appendedTo"] = (!replace && made > 0 && afterReadable && after == before + made);
            o["heightDefaulted"] = heightDefaulted;
            o["heightState"] = HeightStateName(heightState);
            o["unitHeightMetres"] = heightReadable ? (JToken)unitHeight : JValue.CreateNull();
            if (afterByGame >= 0) o["afterByGameCount"] = afterByGame;

            JArray fields = new JArray();
            fields.Add("_geoposition");
            fields.Add("_heightState");
            o["fieldsSet"] = fields;

            JArray warnings = new JArray();
            if (!heightReadable)
                warnings.Add("unit height unreadable; waypoints without an explicit height used 0 m");
            if (!beforeReadable || !afterReadable || !afterRemoveReadable)
                warnings.Add("ExportWaypoints threw, so the route could not be read back; the nulls"
                    + " above mean unknown, not zero");
            if (!replace && !reachableBefore)
                warnings.Add("unit was still under way (_isCurrentWaypointReachable=false): the game's"
                    + " own next waypoint click would have cleared the old route first, this call"
                    + " appended directly to the task list instead");
            if (failure != null) warnings.Add(failure);
            if (afterByGame >= 0 && afterReadable && afterByGame != after)
                warnings.Add("ExportWaypoints reports " + after + " waypoints while GetNumberOfWaypoints"
                    + " reports " + afterByGame + "; the route contains non-GoTo tasks or was rejected");

            string note;
            if (verified)
            {
                note = "read back from ExportWaypoints after the orders: " + after + " waypoint(s)"
                    + (replace ? " (route was cleared first)" : " (route was not cleared)");
            }
            else if (!expectedKnown || !afterReadable)
            {
                note = "the route could not be read back (ExportWaypoints threw), so this call claims"
                    + " nothing about what the unit now holds; requested " + points.Count
                    + ", accepted " + made;
            }
            else
            {
                note = "read back from ExportWaypoints found " + after + " waypoint(s) but the orders"
                    + " imply at least " + expected + " (requested " + points.Count + ", accepted "
                    + made + ", before " + before + ")";
                if (failure != null) note = note + "; " + failure;
            }
            o["note"] = note;
            if (warnings.Count > 0) o["warnings"] = warnings;
            return o;
        }

        // ------------------------------------------------------------- /plot/clear

        /// <summary>POST /plot/clear - remove every waypoint from a unit, then prove
        /// the route really is empty by reading it back.</summary>
        internal static JObject Clear(JObject req)
        {
            if (!Core.IsMain)
                throw new ApiException(500, "off_main_thread",
                    "plot/clear must run on the Unity main thread", true);
            RejectUnknown(req, "plot/clear", ClearFields);

            int unitId = RequireInt(req, "unit", "plot/clear");
            bool validateOnly = OptionalBool(req, "validate_only", false);

            // HOLD is the operator's stop, and this route moves a hull: RemoveWaypoints plus
            // SetWaypointTask below are a real order, not a drawing. A dry run stays exempt
            // because it returns before touching the unit, which is also why the gate can sit
            // here rather than after the lookup.
            if (CommsBridge.Hold && !validateOnly)
                throw new ApiException(409, "held",
                    "operator HOLD is on: routes are refused until it is released", true);
            ObjectBase unit = RequireUnit(unitId);
            RequireOwnUnit(unit, unitId, "plot/clear");

            List<WaypointData> beforeList = ExportRoute(unit);
            bool beforeReadable = beforeList != null;
            int before = beforeReadable ? beforeList.Count : -1;
            if (validateOnly)
            {
                JObject v = new JObject();
                v["validated"] = true;
                v["unit"] = unitId;
                v["before"] = beforeReadable ? Tok(before) : JValue.CreateNull();
                return v;
            }

            unit.RemoveWaypoints();
            List<WaypointData> afterList = ExportRoute(unit);
            bool afterReadable = afterList != null;
            int after = afterReadable ? afterList.Count : -1;
            int afterByGame = CountByGame(unit);

            JObject o = new JObject();
            o["unit"] = unitId;
            o["before"] = beforeReadable ? Tok(before) : JValue.CreateNull();
            o["after"] = afterReadable ? Tok(after) : JValue.CreateNull();
            // REVIEW (round 2, hint 12): "the exporter threw" used to read back as "zero
            // waypoints", so this said verified:true while the route was untouched.
            o["verified"] = afterReadable && after == 0;
            if (afterByGame >= 0) o["afterByGameCount"] = afterByGame;
            if (!afterReadable)
                o["note"] = "RemoveWaypoints was called but ExportWaypoints threw, so the bridge"
                    + " cannot say what the unit now holds; this is not a verification";
            else
                o["note"] = (after == 0)
                    ? "route is empty after RemoveWaypoints (was " + before + ")"
                    : "RemoveWaypoints left " + after + " waypoint(s) behind (was " + before + ")";
            return o;
        }

        // ------------------------------------------------------------- /plot/route

        /// <summary>GET /plot/route?unit=N - the unit's route, straight out of the
        /// game's own exporter. Each point reports lat/lon/height or null when the
        /// waypoint has no usable geo position (relative waypoints do exist).</summary>
        internal static JObject Route(JObject req)
        {
            if (!Core.IsMain)
                throw new ApiException(500, "off_main_thread",
                    "plot/route must run on the Unity main thread", true);
            RejectUnknown(req, "plot/route", RouteFields);

            int unitId = RequireInt(req, "unit", "plot/route");
            ObjectBase unit = RequireUnit(unitId);

            // REVIEW (round 2, blocker 1): existence and the player picture are not the same
            // permission. A detected foreign hull is on the plot, but its true waypoints are its
            // internal state - /units answers that case with a stub, so this route does too
            // instead of printing the coordinates.
            string routeVis = Api.VisibilityOf(unit);
            if (!IntelPolicy.AllowsDetail(routeVis))
            {
                JObject stub = new JObject();
                stub["unit"] = unitId;
                stub["count"] = JValue.CreateNull();
                stub["points"] = new JArray();
                stub["redacted"] = true;
                stub["intelPolicy"] = routeVis;
                stub["withheld"] = "route waypoints";
                stub["reason"] = "a detected hull's planned route is its internal state, not something"
                    + " the player's plot ever shows";
                stub["availableAnyway"] = "GET /tracks gives the position estimate an observer can see";
                return stub;
            }

            List<WaypointData> route = ExportRoute(unit);
            if (route == null)
            {
                JObject failed = new JObject();
                failed["unit"] = unitId;
                failed["count"] = JValue.CreateNull();
                failed["points"] = new JArray();
                failed["note"] = "ExportWaypoints threw, so the route is unknown - not empty";
                return failed;
            }
            JArray points = new JArray();
            for (int i = 0; i < route.Count; i++)
            {
                JObject p = new JObject();
                WaypointData wd = route[i];
                bool readable = false;
                try
                {
                    GeoPosition geo = wd._geoposition;
                    p["lat"] = geo._latitude;
                    p["lon"] = geo._longitude;
                    p["height"] = geo._height;
                    readable = true;
                }
                catch (Exception)
                {
                }
                if (!readable)
                {
                    p["lat"] = JValue.CreateNull();
                    p["lon"] = JValue.CreateNull();
                    p["height"] = JValue.CreateNull();
                }
                try
                {
                    if (wd.wpName != null && wd.wpName.Length > 0) p["name"] = wd.wpName;
                    if (wd._isRelative) p["relative"] = true;
                }
                catch (Exception)
                {
                }
                points.Add(p);
            }

            JObject o = new JObject();
            o["unit"] = unitId;
            o["count"] = route.Count;
            int byGame = CountByGame(unit);
            if (byGame >= 0) o["countByGame"] = byGame;
            o["points"] = points;
            if (byGame >= 0 && byGame != route.Count)
                o["note"] = "ExportWaypoints lists " + route.Count + " entries while GetNumberOfWaypoints"
                    + " counts " + byGame + " (relative or attack waypoints are exported differently)";
            return o;
        }

        // ------------------------------------------------------------- /plot/ruler

        /// <summary>POST /plot/ruler - add one MapControl.Ruler to the live map.
        ///
        /// The ruler subscribes to ViewportChanged and recomputes its own projection,
        /// so no screen maths happens here: we only give it two geographies and a pen.
        /// This touches the visual tree only - never a save file, never Graphics.</summary>
        internal static JObject Ruler(JObject req)
        {
            if (!Core.IsMain)
                throw new ApiException(500, "off_main_thread",
                    "plot/ruler must run on the Unity main thread", true);
            RejectUnknown(req, "plot/ruler", RulerFields);

            bool validateOnly = OptionalBool(req, "validate_only", false);
            double fromLat, fromLon, toLat, toLon;
            ParseCorner(req, "fromLat", "fromLon", out fromLat, out fromLon);
            ParseCorner(req, "toLat", "toLon", out toLat, out toLon);

            byte red = 255, green = 255, blue = 255;
            JToken strokeToken = Present(req, "stroke");
            if (strokeToken != null)
            {
                JObject stroke = strokeToken as JObject;
                if (stroke == null)
                    throw new ApiException(400, "bad_type",
                        "'stroke' must be an object {r,g,b}, got " + strokeToken.Type.ToString(), false);
                foreach (JProperty p in stroke.Properties())
                {
                    if (p.Name != "r" && p.Name != "g" && p.Name != "b")
                        throw new ApiException(400, "unknown_field",
                            "'stroke' does not accept field '" + p.Name + "'", false);
                }
                red = ColorChannel(stroke, "r");
                green = ColorChannel(stroke, "g");
                blue = ColorChannel(stroke, "b");
            }

            double thicknessValue = OptionalNumber(req, "thickness", DefaultThickness);
            if (thicknessValue < 1.0 || thicknessValue > 20.0)
                throw new ApiException(400, "bad_value",
                    "'thickness' must be within 1..20 pixels, got " + thicknessValue, false);
            float thickness = (float)thicknessValue;

            if (validateOnly)
            {
                JObject v = new JObject();
                v["validated"] = true;
                v["from"] = Corner(fromLat, fromLon);
                v["to"] = Corner(toLat, toLon);
                v["stroke"] = Rgb(red, green, blue);
                v["thickness"] = thicknessValue;
                return v;
            }

            MapControl.MapBase mapBase = null;
            try { mapBase = MapView.MapBase() as MapControl.MapBase; }
            catch (Exception) { mapBase = null; }
            if (mapBase == null)
                throw new ApiException(409, "no_ui_host",
                    "no MapBase in the visual tree (is a mission loaded?)", true);

            int childrenBefore = -1;
            int childrenAfter = -1;
            bool parentMapSet = false;
            string error = null;
            try
            {
                GeoPosition from = new GeoPosition(fromLat, fromLon, 0.0);
                GeoPosition to = new GeoPosition(toLat, toLon, 0.0);

                MapControl.Ruler ruler = new MapControl.Ruler();
                ruler.Start = from;
                ruler.End = to;
                ruler.Stroke = new Noesis.SolidColorBrush(Noesis.Color.FromRgb(red, green, blue));
                ruler.StrokeThickness = thickness;

                childrenBefore = mapBase.Children.Count;
                mapBase.Children.Add(ruler);
                childrenAfter = mapBase.Children.Count;

                // The ruler needs its parent map for the coordinate transform; the
                // instance setter is protected, the static helper is the public door.
                MapControl.MapPanel.SetParentMap(ruler, mapBase);
                parentMapSet = true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
            }

            // A ruler that never reached the tree is a failure, not a route note: the
            // caller asked for something visible and got nothing. Partial state (Add
            // succeeded, SetParentMap threw) is reported inside the message because
            // there is no public way to remove the element again.
            if (error != null)
                throw new ApiException(500, "ruler_failed",
                    "building or attaching the ruler failed" + (childrenAfter >= 0
                        ? " after the element was added to MapBase.Children" : "")
                    + ": " + error, true);

            JObject o = new JObject();
            o["from"] = Corner(fromLat, fromLon);
            o["to"] = Corner(toLat, toLon);
            o["stroke"] = Rgb(red, green, blue);
            o["thickness"] = thicknessValue;
            o["addedTo"] = "MapBase.Children";
            o["parentMapSet"] = parentMapSet;
            if (childrenBefore >= 0) o["childrenBefore"] = childrenBefore;
            if (childrenAfter >= 0) o["childrenAfter"] = childrenAfter;
            // A Noesis element that never entered the tree cannot be shown, so the
            // child count is the verification here, not the fact that we called Add.
            bool verified = childrenBefore >= 0 && childrenAfter == childrenBefore + 1 && parentMapSet;
            o["verified"] = verified;
            if (!verified)
            {
                o["note"] = "ruler was built and attached but MapBase.Children did not grow by"
                    + " exactly one element (before " + childrenBefore + ", after " + childrenAfter + ")";
            }
            return o;
        }

        private static byte ColorChannel(JObject stroke, string name)
        {
            JToken t = Present(stroke, name);
            if (t == null)
                throw new ApiException(400, "missing_field",
                    "'stroke." + name + "' is required when 'stroke' is given", false);
            double d = NumberField(t, "stroke." + name);
            if (d < 0.0 || d > 255.0)
                throw new ApiException(400, "bad_value",
                    "'stroke." + name + "' must be within 0..255, got " + d, false);
            return (byte)Math.Round(d);
        }

        private static JObject Corner(double lat, double lon)
        {
            JObject o = new JObject();
            o["lat"] = lat;
            o["lon"] = lon;
            return o;
        }

        private static JArray Rgb(byte red, byte green, byte blue)
        {
            JArray a = new JArray();
            a.Add((int)red);
            a.Add((int)green);
            a.Add((int)blue);
            return a;
        }

        // ----------------------------------------------------------- /plot/measure

        /// <summary>GET /plot/measure - great-circle distance and forward azimuth
        /// between two geographies. No unit, no map, no visual tree: this is the
        /// game's own spherical maths called with the game's own Earth radius, so a
        /// number returned here matches what the tactical ruler would show.</summary>
        internal static JObject Measure(JObject req)
        {
            if (!Core.IsMain)
                throw new ApiException(500, "off_main_thread",
                    "plot/measure must run on the Unity main thread", true);
            RejectUnknown(req, "plot/measure", MeasureFields);

            double fromLat, fromLon, toLat, toLon;
            ParseCorner(req, "fromLat", "fromLon", out fromLat, out fromLon);
            ParseCorner(req, "toLat", "toLon", out toLat, out toLon);

            // Wgs84EquatorialRadius is a public const, so it is readable without the
            // game running; the guard exists so a zeroed/absent value cannot turn
            // every distance into zero.
            double radius = MapControl.MapProjection.Wgs84EquatorialRadius;
            string radiusUsed = "wgs84";
            if (!(radius > 0.0))
            {
                radius = 6378137.0;
                radiusUsed = "fallback";
            }

            double metres;
            double bearing;
            try
            {
                GeoPosition from = new GeoPosition(fromLat, fromLon, 0.0);
                GeoPosition to = new GeoPosition(toLat, toLon, 0.0);
                metres = MapControl.Location.GetDistance(from, to, radius);
                bearing = MapControl.Location.GetForwardAzimuth(from, to);
            }
            catch (Exception ex)
            {
                throw new ApiException(500, "measure_failed",
                    "MapControl.Location could not measure the pair: " + ex.GetType().Name
                    + ": " + ex.Message, true);
            }

            JObject o = new JObject();
            o["from"] = Corner(fromLat, fromLon);
            o["to"] = Corner(toLat, toLon);
            o["distanceMetres"] = metres;
            o["distanceKm"] = metres / 1000.0;
            o["distanceNm"] = metres / 1852.0;
            o["bearingDeg"] = bearing;
            o["earthRadiusUsed"] = radiusUsed;
            o["source"] = "MapControl.Location";
            return o;
        }
    }
}
