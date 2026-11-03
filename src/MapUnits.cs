// MapUnits.cs - the plotting table's coordinate answers: where every unit sits (Unity world,
// screen pixels, map-view pixels), where a geographic point lands on the map control, and
// which geographic point a map-view pixel belongs to.
//
// Everything here is READ ONLY. The only game state touched is read: Globals._currentCenterTile
// (the 0.2 degree tile grid the world projection is anchored to), CameraManager's current
// camera (screen projection) and the live MapControl.MapBase (view projection). MapBase comes
// from MapView.cs so that all three map files agree on which control is "the" map instead of
// three copies of the visual-tree walk drifting apart.
//
// Signatures and routes are fixed by PLOT_CONTRACT.md appendix A:
//   GET /map/units        -> List        (?unit=N to narrow to one unit)
//   GET /map/unit-screen  -> List        (same handler, same shape)
//   GET /map/geo-to-view  -> GeoToView   (?lat=&lon=)
//   GET /map/view-to-geo  -> ViewToGeo   (?x=&y=)
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace SeaPowerAgentBridge
{
    internal static class MapUnits
    {
        private const string GlobalsTypeName = "SeaPower.Globals";
        private const string UtilsTypeName = "SeaPower.Utils";
        private const string CameraManagerTypeName = "SeaPower.CameraManager";
        private const string NoesisPointTypeName = "Noesis.Point";

        // [IL] SeaPower.Utils, read out of longLatToLocalV3 / calculateCorrectionFor180EW:
        // one 0.2 degree tile is 330.714 Unity units, the tile grid is offset by 1/120 degree
        // (0.008333334), latitude uses |lat - 90| as the row, and longitude wraps once the
        // column distance from the centre tile passes 1795 of the 1800 tiles in a ring.
        private const float TileDegrees = 0.2f;
        private const float UnitsPerTile = 330.714f;
        private const float TileOffsetDegrees = 0.008333334f;
        private const float LongitudeTileCount = 1800f;
        private const float LongitudeWrapThreshold = 1795f;

        // Only used to put a round-trip error into a unit a human can judge. The failure we
        // care about is "the inverse formula is wrong", not "the earth model is off by 0.3%",
        // so the mean metres-per-degree is honest enough for a 1 metre threshold.
        private const double MetersPerDegree = 111320.0;
        private const double RoundTripOkMeters = 1.0;

        // A round trip through geo -> world -> screen and back must land within this many
        // pixels before the camera inverse is called verified; half a pixel would fail on
        // float rounding alone, a whole one passes for correct.
        private const double RayRoundTripPx = 1.5;

        private static readonly string[] FieldsUnits = new string[] { "unit" };
        private static readonly string[] FieldsGeo = new string[] { "lat", "lon" };
        private static readonly string[] FieldsView = new string[] { "x", "y" };

        // Reflection into Utils.longLatToLocalV3 is a per-unit cost: GameReflect.CallStatic
        // walks every method on the type each time. The MethodInfo is resolved once and reused,
        // because a 300 unit listing would otherwise pay that scan 300 times per request.
        private static MethodInfo worldMethod;
        private static bool worldMethodResolved;

        // ---------------------------------------------------------------- GET /map/units

        internal static JObject List(JObject req)
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread",
                "/map/units must run on the Unity main thread", false);

            JObject body = req == null ? new JObject() : req;
            RejectUnknown(body, "map/units", FieldsUnits);

            int wanted = -1;
            if (Present(body, "unit") != null)
                wanted = ReqIntIn(body, "unit", true, 0, 0, int.MaxValue);

            // TR2-I / ISS-003: this used to hand out every object in the world, positional truth
            // included, detected or not. "Which hulls exist and where they are" is intel, so the
            // list is filtered through the same gate /units uses (IntelScope.PlayerKnows), and a
            // known-but-foreign hull keeps its row while its precise position is withheld -
            // IntelPolicy.AllowsDetail is the policy that decides that now (there is no
            // IntelGate class; the gate is IntelScope + IntelPolicy) - /tracks is where
            // estimates come from.
            List<object> all = GameReflect.Units();
            List<object> units = new List<object>();
            int hidden = 0;
            object chosen = null;
            for (int i = 0; i < all.Count; i++)
            {
                SeaPower.ObjectBase ob = all[i] as SeaPower.ObjectBase;
                if (ob == null) continue;
                bool known = false;
                try { known = IntelScope.PlayerKnows(ob); } catch (Exception) { known = false; }
                if (!known)
                {
                    hidden++;
                    if (wanted >= 0 && (int)GameReflect.Num(GameReflect.Member(all[i], "UniqueID"), -1.0) == wanted)
                        chosen = null;
                    continue;
                }
                if (wanted >= 0)
                {
                    if ((int)GameReflect.Num(GameReflect.Member(all[i], "UniqueID"), -1.0) == wanted) chosen = ob;
                    continue;
                }
                units.Add(ob);
            }
            if (wanted >= 0)
            {
                // I8: identical wording to the FindUnit answer on purpose. "Not detected" and
                // "does not exist" must not be distinguishable, or the ids become an oracle.
                if (chosen == null)
                    throw new ApiException(404, "unit_not_found",
                        "unit not found: " + wanted.ToString(CultureInfo.InvariantCulture), false);
                units = new List<object>();
                units.Add(chosen);
            }

            Vector2 centerTile = Vector2.zero;
            bool haveCenter = TryCenterTile(out centerTile);

            object mapBase = SafeMapBase();
            Camera cam = CurrentCamera();
            int screenWidth = Screen.width;
            int screenHeight = Screen.height;

            double viewWidth = double.NaN;
            double viewHeight = double.NaN;
            bool haveView = TryRenderSize(mapBase, out viewWidth, out viewHeight);

            JArray arr = new JArray();
            for (int i = 0; i < units.Count; i++)
            {
                arr.Add(UnitEntry(units[i], haveCenter, centerTile, cam, mapBase, haveView,
                    viewWidth, viewHeight, screenWidth, screenHeight));
            }

            JObject o = new JObject();
            o["screenWidth"] = new JValue(screenWidth);
            o["screenHeight"] = new JValue(screenHeight);
            if (haveCenter)
            {
                JObject ct = new JObject();
                ct["x"] = Tok(centerTile.x, 3);
                ct["y"] = Tok(centerTile.y, 3);
                o["centerTile"] = ct;
            }
            else
            {
                o["centerTile"] = JValue.CreateNull();
            }
            o["units"] = arr;
            o["unitCount"] = new JValue(arr.Count);
            o["count"] = new JValue(arr.Count);
            o["intelScope"] = new JValue("player");
            o["knownUnits"] = new JValue(units.Count);
            // REVIEW 9.1 / C186: the size of the withheld set is itself intel - "there are fifty
            // hulls you cannot see" is the enemy's order of battle. /units dropped the count for
            // exactly that reason, but this route kept publishing it, so the two answers disagreed
            // about how much the player's picture hides. The number is still useful when debugging
            // the filter, so diagnostics.on (the same switch that gates /probe) publishes it and the
            // default answer states only that it was withheld.
            if (MiniHttp.DiagnosticsEnabled)
            {
                o["hiddenUnits"] = new JValue(hidden);
            }
            else
            {
                o["hiddenUnitsWithheld"] = new JValue(
                    "how many objects the player picture does not show is intel; create diagnostics.on in the mod folder to publish the count");
            }
            o["revealSwitch"] = new JValue(Api.IntelRevealEnabled());
            o["mapBaseFound"] = new JValue(mapBase != null);
            o["cameraFound"] = new JValue(cam != null);
            return o;
        }

        private static JObject UnitEntry(object unit, bool haveCenter, Vector2 centerTile, Camera cam,
            object mapBase, bool haveView, double viewWidth, double viewHeight,
            int screenWidth, int screenHeight)
        {
            JObject o = new JObject();
            o["id"] = TokId(GameReflect.Num(GameReflect.Member(unit, "UniqueID"), double.NaN));
            // Existence already passed IntelScope in List(). Position precision is a second gate:
            // a known-but-foreign hull exists on the player's picture, but its true coordinate is
            // not ours to publish. Estimates live in /tracks.
            SeaPower.ObjectBase unitOb = unit as SeaPower.ObjectBase;
            // REVIEW (round 2, hint 12): this defaulted to true, i.e. an object that could not be
            // typed as ObjectBase got its true lat/lon published while the same response still said
            // intelPolicy:"redacted". Fail closed: no type, no detail.
            bool truthOk = false;
            string vis = IntelPolicy.Redacted;
            if (unitOb != null)
            {
                try
                {
                    vis = Api.VisibilityOf(unitOb);
                    truthOk = IntelPolicy.AllowsDetail(vis);
                }
                catch (Exception) { truthOk = false; }
            }
            o["intelPolicy"] = Text(vis);
            o["name"] = Text(UnitName(unit));
            o["displayName"] = Text(UnitDisplayName(unit));

            object taskforce = TaskforceOf(unit);
            o["taskforce"] = Text(TaskforceName(taskforce));
            o["tfType"] = Text(TfTypeName(taskforce));

            object geo = GeoOf(unit);
            double lat = geo == null ? double.NaN : GameReflect.Lat(geo);
            double lon = geo == null ? double.NaN : GameReflect.Lon(geo);
            double height = geo == null ? double.NaN : GeoHeight(geo);
            if (truthOk)
            {
                o["latitude"] = Tok(lat, 7);
                o["longitude"] = Tok(lon, 7);
                o["height"] = Tok(height, 2);
                o["positionSource"] = Text("truth");
            }
            else
            {
                // Withholding the numbers instead of rounding them: a rounded enemy position is
                // still a position, and this route is often the only place a caller looks.
                o["latitude"] = JValue.CreateNull();
                o["longitude"] = JValue.CreateNull();
                o["height"] = JValue.CreateNull();
                o["positionSource"] = Text("withheld");
                o["positionNote"] = Text("detected, but precise position is not published here; /tracks carries the estimated track");
                lat = double.NaN;
                lon = double.NaN;
            }

            // world: the projection the game itself uses to draw the unit on the 3D map. It is
            // relative to Globals._currentCenterTile, so without that field there is no world
            // position to report - null plus a reason beats a number from a different grid.
            Vector3 world = Vector3.zero;
            string formula = "unavailable";
            bool haveWorld = false;
            if (haveCenter && !double.IsNaN(lat) && !double.IsNaN(lon))
            {
                double worldHeight = double.IsNaN(height) ? 0.0 : height;
                haveWorld = TryWorld(lat, lon, worldHeight, centerTile, out world, out formula);
                if (!haveWorld) formula = "unavailable";
            }
            o["world"] = haveWorld ? (JToken)Vec3(world) : JValue.CreateNull();
            o["worldFormula"] = Text(formula);

            Vector3 screen = Vector3.zero;
            bool haveScreen = haveWorld && TryScreen(cam, world, out screen);
            o["screen"] = haveScreen ? (JToken)Vec3(screen) : JValue.CreateNull();
            o["onScreen"] = new JValue(haveScreen && OnScreen(screen, screenWidth, screenHeight));

            // mapView: the same point in the map control's own pixel space (Noesis points).
            // MapBase handles the +-180 wrap through ConstrainedLongitude, so this is the only
            // projection we are allowed to use for "where on the map picture is it".
            // OI-4 hole 1: this used to run for foreign hulls too. A withheld lat/lon plus a
            // published pixel position is not withheld at all - the pixel converts straight back
            // to a place on the map. So the projection is part of the position gate.
            object viewPoint = (mapBase != null && geo != null && truthOk)
                ? GameReflect.Call(mapBase, "LocationToView", geo) : null;
            o["mapView"] = viewPoint == null ? JValue.CreateNull() : (JToken)Pt2(viewPoint);
            o["mapOnScreen"] = new JValue(viewPoint != null && haveView
                && InsideView(viewPoint, viewWidth, viewHeight));
            if (!truthOk)
                o["mapViewNote"] = Text("the map pixel position is withheld with the position: "
                    + "a pixel on the plot converts straight back to a place");
            return o;
        }

        // ---------------------------------------------------------------- GET /map/geo-to-view

        internal static JObject GeoToView(JObject req)
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread",
                "/map/geo-to-view must run on the Unity main thread", false);

            JObject body = req == null ? new JObject() : req;
            RejectUnknown(body, "map/geo-to-view", FieldsGeo);
            double lat = ReqNum(body, "lat", true, 0.0);
            double lon = ReqNum(body, "lon", true, 0.0);
            RequireLatitude(lat);

            JObject o = new JObject();
            o["lat"] = Tok(lat, 7);
            o["lon"] = Tok(lon, 7);

            object mapBase = SafeMapBase();
            o["mapBaseFound"] = new JValue(mapBase != null);

            double viewWidth = double.NaN;
            double viewHeight = double.NaN;
            bool haveView = TryRenderSize(mapBase, out viewWidth, out viewHeight);

            object geo = mapBase == null ? null : GameReflect.Geo(lat, lon, 0.0);
            object viewPoint = geo == null ? null : GameReflect.Call(mapBase, "LocationToView", geo);

            o["view"] = viewPoint == null ? JValue.CreateNull() : (JToken)Pt2(viewPoint);
            o["insideRenderSize"] = new JValue(viewPoint != null && haveView
                && InsideView(viewPoint, viewWidth, viewHeight));

            if (haveView)
            {
                JObject rs = new JObject();
                rs["w"] = Tok(viewWidth, 3);
                rs["h"] = Tok(viewHeight, 3);
                o["renderSize"] = rs;
            }
            else
            {
                o["renderSize"] = JValue.CreateNull();
            }

            object scale = geo == null ? null : GameReflect.Call(mapBase, "GetScale", geo);
            o["scale"] = scale == null ? JValue.CreateNull() : (JToken)Pt2(scale);

            // No MapBase in the tactical view: the plotting table is the 3D scene seen
            // through the camera, so the honest projection is geo -> world -> screen. This
            // is not a fallback for a missing control, it is the correct path for this UI -
            // the first live run showed no MapControl anywhere in the visual tree while the
            // camera was reporting a perfectly good position.
            if (mapBase == null)
            {
                Vector2 tile = Vector2.zero;
                Vector3 world = Vector3.zero;
                string formula = null;
                Camera cam = CurrentCamera();
                bool haveWorld = false;
                if (TryCenterTile(out tile))
                    haveWorld = TryWorld(lat, lon, 0.0, tile, out world, out formula);
                Vector3 screen = Vector3.zero;
                bool haveScreen = haveWorld && TryScreen(cam, world, out screen);
                int sw = Screen.width;
                int sh = Screen.height;
                bool inside = haveScreen && sw > 0 && sh > 0
                    && screen.x > 0f && screen.x <= (float)sw
                    && screen.y > 0f && screen.y <= (float)sh && screen.z > 0f;
                o["projection"] = Text("camera");
                o["world"] = haveWorld ? (JToken)Vec3(world) : JValue.CreateNull();
                o["worldFormula"] = Text(haveWorld ? formula : "unavailable");
                // A projection is arithmetic, not a picture position. The first live run
                // returned screen.y = -2210 for a point outside the view; handing that out as
                // "screen" invites a caller to draw it somewhere that does not exist, so the
                // raw numbers now live in screenRaw and screen stays null unless the point is
                // really on the canvas.
                o["screen"] = inside ? (JToken)Vec3(screen) : JValue.CreateNull();
                o["insideScreen"] = new JValue(inside);
                if (haveScreen)
                {
                    o["screenRaw"] = Vec3(screen);
                    o["screenRawNote"] = Text(inside
                        ? "camera projection: pixels in the whole window, y counts up from the "
                            + "bottom edge. This is 3D-scene pixels, not map-control view pixels."
                        : "camera projection, but the point is off the canvas (a value outside "
                            + "0..width / 0..height, or z <= 0 for a point behind the camera). "
                            + "screen is null on purpose: use screenRaw only for arithmetic and "
                            + "screenDirectionDeg for an edge marker.");
                    if (!inside)
                        o["screenDirectionDeg"] = new JValue(ScreenDirectionDeg(screen, sw, sh));
                }
                o["note"] = Text(haveScreen
                    ? "no live MapControl.MapBase: this is the camera projection (geo -> Unity "
                        + "world -> screen pixels), not map-control view pixels"
                    : "no live MapControl.MapBase and the camera projection did not finish; "
                        + "worldFormula says which half failed");
                return o;
            }
            if (viewPoint == null)
                o["note"] = Text("MapBase.LocationToView(GeoPosition) returned nothing; the "
                    + "projection or the control's layout is not ready");
            return o;
        }

        // ---------------------------------------------------------------- GET /map/view-to-geo

        internal static JObject ViewToGeo(JObject req)
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread",
                "/map/view-to-geo must run on the Unity main thread", false);

            JObject body = req == null ? new JObject() : req;
            RejectUnknown(body, "map/view-to-geo", FieldsView);
            double x = ReqNum(body, "x", true, 0.0);
            double y = ReqNum(body, "y", true, 0.0);

            JObject o = new JObject();
            o["x"] = Tok(x, 3);
            o["y"] = Tok(y, 3);

            object mapBase = SafeMapBase();
            o["mapBaseFound"] = new JValue(mapBase != null);

            if (mapBase == null)
            {
                // Same reasoning as GeoToView: with no MapControl the tactical view is the 3D
                // scene, so a screen pixel is inverted by casting a ray from the camera onto
                // the sea plane, then running the world -> geo algebra. It is our arithmetic
                // (there is no localV3ToLongLat in the scripts), so the round trip below has
                // to close before anything is called verified.
                o["viaMapBase"] = new JValue(false);
                o["projection"] = Text("camera_ray");
                Vector2 tile = Vector2.zero;
                double inLat = double.NaN;
                double inLon = double.NaN;
                string why = null;
                Camera cam = CurrentCamera();
                if (cam == null) why = "no current camera";
                else if (!TryCenterTile(out tile)) why = "Globals._currentCenterTile is unreadable";
                else if (!RayToGeo(cam, (float)x, (float)y, tile, out inLat, out inLon))
                    why = "the screen ray did not meet the sea plane";
                if (why != null)
                {
                    o["geographic"] = JValue.CreateNull();
                    o["note"] = Text("no live MapControl.MapBase and the camera inverse did not "
                        + "finish: " + why);
                    return o;
                }
                JObject g = new JObject();
                g["lat"] = Tok(inLat, 7);
                g["lon"] = Tok(inLon, 7);
                o["geographic"] = g;
                Vector3 back = Vector3.zero;
                string formula = null;
                Vector3 world = Vector3.zero;
                double errorPx = double.NaN;
                try
                {
                    bool haveBack = TryWorld(inLat, inLon, 0.0, tile, out world, out formula);
                    if (haveBack && TryScreen(cam, world, out back))
                        errorPx = Math.Sqrt((back.x - x) * (back.x - x) + (back.y - y) * (back.y - y));
                }
                catch (Exception) { }
                o["roundTripErrorPx"] = double.IsNaN(errorPx) ? (JToken)JValue.CreateNull() : Tok(errorPx, 3);
                o["verified"] = new JValue(!double.IsNaN(errorPx) && errorPx <= RayRoundTripPx);
                o["note"] = Text("no live MapControl.MapBase: inverted through the camera ray "
                    + "(sea plane at y = 0), round trip " + (double.IsNaN(errorPx)
                        ? "not measurable" : errorPx.ToString("0.00", CultureInfo.InvariantCulture) + " px"));
                return o;
            }

            object point = MakePoint(x, y);
            object geo = point == null ? null : GameReflect.Call(mapBase, "ViewToGeoPosition", point);
            if (geo == null)
            {
                o["geographic"] = JValue.CreateNull();
                o["viaMapBase"] = new JValue(false);
                o["note"] = Text(point == null
                    ? "could not construct a Noesis.Point to hand to MapBase"
                    : "MapBase.ViewToGeoPosition returned nothing; the projection or the "
                        + "control's layout is not ready");
                return o;
            }

            double lat = GameReflect.Lat(geo);
            double lon = GameReflect.Lon(geo);
            JObject geographic = new JObject();
            geographic["lat"] = Tok(lat, 7);
            geographic["lon"] = Tok(lon, 7);
            o["geographic"] = geographic;
            o["viaMapBase"] = new JValue(true);

            // Round trip: the answer goes back through longLatToLocalV3 into Unity world, then
            // through the inverse IL formula, and the two GeoPositions are compared. If that
            // path does not close to within a metre the inverse formula (not MapBase) is lying,
            // and the caller must not treat the latitude as verified.
            Vector2 centerTile = Vector2.zero;
            bool haveCenter = TryCenterTile(out centerTile);
            double error = double.NaN;
            bool verified = false;
            if (haveCenter && !double.IsNaN(lat) && !double.IsNaN(lon))
            {
                double height = GeoHeight(geo);
                if (double.IsNaN(height)) height = 0.0;
                Vector3 world = Vector3.zero;
                string formula = null;
                if (TryWorld(lat, lon, height, centerTile, out world, out formula))
                {
                    double backLat;
                    double backLon;
                    InverseWorld(world, centerTile, out backLat, out backLon);
                    error = DegreesToMetres(lat, lon, backLat, backLon);
                    verified = !double.IsNaN(error) && error <= RoundTripOkMeters;
                }
            }
            o["roundTripErrorMetres"] = Tok(error, 3);
            o["verified"] = new JValue(verified);
            if (!haveCenter)
                o["note"] = Text("Globals._currentCenterTile is not readable, so the world round "
                    + "trip could not run; the map-view answer is still MapBase's own");

            // Independent cross check of the screen-pixel chain (geo -> Unity world -> screen ->
            // ScreenPointToRay/sea plane -> inverse formula). Reported, never used as the answer.
            JObject ray = ScreenRayCheck(CurrentCamera(), mapBase, geo, lat, lon, haveCenter, centerTile);
            o["screenRayCheck"] = ray == null ? JValue.CreateNull() : (JToken)ray;
            return o;
        }

        private static JObject ScreenRayCheck(Camera cam, object mapBase, object geo,
            double lat, double lon, bool haveCenter, Vector2 centerTile)
        {
            if (cam == null || !haveCenter || double.IsNaN(lat) || double.IsNaN(lon)) return null;

            double height = GeoHeight(geo);
            if (double.IsNaN(height)) height = 0.0;

            Vector3 world = Vector3.zero;
            string formula = null;
            if (!TryWorld(lat, lon, height, centerTile, out world, out formula)) return null;

            Vector3 screen = Vector3.zero;
            if (!TryScreen(cam, world, out screen)) return null;

            JObject o = new JObject();
            o["screen"] = Vec3(screen);
            try
            {
                Ray ray = cam.ScreenPointToRay(new Vector3(screen.x, screen.y, 0f));
                // The sea is the y = 0 plane. Physics.Raycast would hit units and terrain
                // colliders instead of the water, which is the wrong surface to invert against.
                Plane sea = new Plane(Vector3.up, Vector3.zero);
                float distance;
                if (!sea.Raycast(ray, out distance))
                {
                    o["ok"] = new JValue(false);
                    o["note"] = Text("the camera ray does not meet the y=0 sea plane");
                    return o;
                }
                Vector3 hit = ray.GetPoint(distance);
                o["world"] = Vec3(hit);
                double rayLat;
                double rayLon;
                InverseWorld(hit, centerTile, out rayLat, out rayLon);
                o["lat"] = Tok(rayLat, 7);
                o["lon"] = Tok(rayLon, 7);
                o["errorMetres"] = Tok(DegreesToMetres(lat, lon, rayLat, rayLon), 3);
                o["ok"] = new JValue(true);
            }
            catch (Exception)
            {
                o["ok"] = new JValue(false);
                o["note"] = Text("ScreenPointToRay / sea plane intersection threw");
            }
            return o;
        }

        // ---------------------------------------------------------------- projection

        // GeoPosition -> Unity world, through the game's own longLatToLocalV3 when reflection
        // reaches it. The arithmetic below is that method's IL, kept as the fallback so a
        // private-signature change turns into worldFormula:"il_formula" instead of no answer.
        private static bool TryWorld(double lat, double lon, double height, Vector2 centerTile,
            out Vector3 world, out string source)
        {
            world = Vector3.zero;
            source = null;

            MethodInfo method = WorldMethod();
            if (method != null)
            {
                object geo = GameReflect.Geo(lat, lon, height);
                if (geo != null)
                {
                    try
                    {
                        object result = method.Invoke(null, new object[] { geo, centerTile });
                        if (result is Vector3)
                        {
                            world = (Vector3)result;
                            source = "SeaPower.Utils.longLatToLocalV3";
                            return true;
                        }
                    }
                    catch (Exception) { }
                }
            }

            float lonAdjusted = (float)lon + 180f - TileOffsetDegrees;
            if (lonAdjusted < 0f) lonAdjusted = 0f;
            float column = lonAdjusted / TileDegrees - Mathf.FloorToInt(centerTile.x);
            if (Mathf.Abs(column) > LongitudeWrapThreshold)
                column += (column > LongitudeWrapThreshold) ? -LongitudeTileCount : LongitudeTileCount;

            float latPos = Mathf.Abs((float)lat - 90f) - TileOffsetDegrees;
            float row = latPos / TileDegrees - Mathf.FloorToInt(centerTile.y) - 0.5f;

            world = new Vector3(UnitsPerTile * (column - 0.5f), (float)height, -UnitsPerTile * row);
            source = "il_formula";
            return true;
        }

        private static MethodInfo WorldMethod()
        {
            if (worldMethodResolved) return worldMethod;
            worldMethodResolved = true;
            Type t = GameReflect.FindType(UtilsTypeName);
            if (t == null) return null;
            try
            {
                MethodInfo[] methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Static);
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo m = methods[i];
                    if (m.Name != "longLatToLocalV3") continue;
                    if (m.GetParameters().Length != 2) continue;
                    worldMethod = m;
                    break;
                }
            }
            catch (Exception) { }
            return worldMethod;
        }

        // Public face of the geo -> world conversion. /units/{id}/fire needs a Vector3 to hand to
        // InsertEngageTask, and a second copy of the algebra would drift away from this one.
        internal static bool GeoToWorld(double lat, double lon, double height,
            out Vector3 world, out string source)
        {
            world = Vector3.zero;
            source = null;
            Vector2 tile;
            if (!TryCenterTile(out tile)) return false;
            if (!TryWorld(lat, lon, height, tile, out world, out source))
            {
                world = Vector3.zero;
                source = null;
                return false;
            }
            return true;
        }

        // Screen pixel -> geographic, for when there is no MapControl to ask. The camera ray
        // is intersected with the sea plane (y = 0), then the world -> geo algebra runs.
        // Deliberately not "return the camera centre": a click at the edge of the screen is
        // several degrees away from the camera position, and pretending otherwise would be
        // wrong in exactly the case that matters.
        private static bool RayToGeo(Camera cam, float x, float y, Vector2 centerTile,
            out double lat, out double lon)
        {
            lat = double.NaN;
            lon = double.NaN;
            if (cam == null) return false;
            try
            {
                Ray ray = cam.ScreenPointToRay(new Vector3(x, y, 0f));
                Plane sea = new Plane(Vector3.up, Vector3.zero);
                float distance;
                if (!sea.Raycast(ray, out distance)) return false;
                if (distance <= 0f || float.IsInfinity(distance) || float.IsNaN(distance)) return false;
                Vector3 hit = ray.GetPoint(distance);
                InverseWorld(hit, centerTile, out lat, out lon);
                return !double.IsNaN(lat) && !double.IsNaN(lon);
            }
            catch (Exception) { return false; }
        }
        // Unity world -> GeoPosition, the algebra of the IL above solved for lat/lon. There is
        // no localV3ToLongLat in Seapower-Scripts (checked with ildump), so this inverse is
        // ours and is exactly why ViewToGeo reports roundTripErrorMetres/verified.
        private static void InverseWorld(Vector3 world, Vector2 centerTile, out double lat, out double lon)
        {
            float correction = world.x / UnitsPerTile + 0.5f;
            float lonAdjusted = (correction + Mathf.FloorToInt(centerTile.x)) * TileDegrees;
            lon = (double)lonAdjusted - 180.0 + TileOffsetDegrees;

            float latPos = -world.z / UnitsPerTile + Mathf.FloorToInt(centerTile.y) + 0.5f;
            lat = 90.0 - ((double)latPos * TileDegrees + TileOffsetDegrees);
        }

        private static bool TryCenterTile(out Vector2 centerTile)
        {
            centerTile = Vector2.zero;
            object raw = GameReflect.StaticMember(GlobalsTypeName, "_currentCenterTile");
            if (raw == null) return false;
            double x = GameReflect.Num(GameReflect.Member(raw, "x"), double.NaN);
            double y = GameReflect.Num(GameReflect.Member(raw, "y"), double.NaN);
            if (double.IsNaN(x) || double.IsNaN(y)) return false;
            centerTile = new Vector2((float)x, (float)y);
            return true;
        }

        // Angle from the middle of the window to a projected point, in degrees, read the way a
        // compass rose drawn on the screen would be read: 0 = right, 90 = up. This exists only
        // for points that fall off the canvas - there a caller cannot draw a dot, but it can
        // still say "it is over that way", which is what a human sees at the edge of a map.
        private static double ScreenDirectionDeg(Vector3 screen, int sw, int sh)
        {
            if (sw <= 0 || sh <= 0) return double.NaN;
            double dx = (double)screen.x - (double)sw / 2.0;
            double dy = (double)screen.y - (double)sh / 2.0;
            if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9) return double.NaN;
            return Math.Atan2(dy, dx) * 180.0 / Math.PI;
        }

        private static bool TryScreen(Camera cam, Vector3 world, out Vector3 screen)
        {
            screen = Vector3.zero;
            if (cam == null) return false;
            try
            {
                screen = cam.WorldToScreenPoint(world);
                return true;
            }
            catch (Exception)
            {
                // A destroyed or half-initialised camera throws MissingReferenceException here.
                return false;
            }
        }

        private static bool OnScreen(Vector3 screen, int screenWidth, int screenHeight)
        {
            return screen.x > 0f && screen.x <= (float)screenWidth
                && screen.y > 0f && screen.y <= (float)screenHeight
                && screen.z > 0f;
        }

        // ---------------------------------------------------------------- map control

        // MapView.MapBase() is the single owner of "which control is the map". It can still
        // throw while the UI is being torn down, and a coordinate query must answer instead of
        // 500ing, so the exception becomes "no map" here.
        private static object SafeMapBase()
        {
            try { return MapView.MapBase(); }
            catch (Exception) { return null; }
        }

        private static bool TryRenderSize(object mapBase, out double width, out double height)
        {
            width = double.NaN;
            height = double.NaN;
            if (mapBase == null) return false;
            object size = GameReflect.Member(mapBase, "RenderSize");
            if (size == null) return false;
            width = GameReflect.Num(GameReflect.Member(size, "Width"), double.NaN);
            height = GameReflect.Num(GameReflect.Member(size, "Height"), double.NaN);
            return !double.IsNaN(width) && !double.IsNaN(height);
        }

        private static bool InsideView(object viewPoint, double width, double height)
        {
            double x = GameReflect.Num(GameReflect.Member(viewPoint, "X"), double.NaN);
            double y = GameReflect.Num(GameReflect.Member(viewPoint, "Y"), double.NaN);
            if (double.IsNaN(x) || double.IsNaN(y)) return false;
            return x >= 0.0 && x <= width && y >= 0.0 && y <= height;
        }

        // Noesis.Point is a (Single, Single) struct; build it through reflection so this file
        // does not take a compile-time dependency on the Noesis namespace.
        private static object MakePoint(double x, double y)
        {
            Type t = GameReflect.FindType(NoesisPointTypeName);
            if (t == null) return null;
            try
            {
                ConstructorInfo c = t.GetConstructor(new Type[] { typeof(float), typeof(float) });
                if (c != null) return c.Invoke(new object[] { (float)x, (float)y });
            }
            catch (Exception) { }
            return null;
        }

        // ---------------------------------------------------------------- camera

        // Singleton<T>.Instance is a public static property on the generic BASE type, which
        // GameReflect.StaticMember cannot see (no FlattenHierarchy). Walk the base chain
        // explicitly, then fall back to a Unity object search.
        private static object Singleton(string typeName)
        {
            Type t = GameReflect.FindType(typeName);
            if (t == null) return null;

            Type walk = t;
            while (walk != null)
            {
                try
                {
                    PropertyInfo p = walk.GetProperty("Instance",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                        | BindingFlags.DeclaredOnly);
                    if (p != null && p.CanRead)
                    {
                        object v = p.GetValue(null, null);
                        if (v != null) return v;
                    }
                }
                catch (Exception) { }
                walk = walk.BaseType;
            }

            try
            {
                UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(t);
                if (all != null)
                {
                    for (int i = 0; i < all.Length; i++)
                    {
                        if (all[i] != null) return all[i];
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        // CameraManager.Instance.getCurrentCamera() -> CameraBase.getCamera() -> Camera, the
        // same chain MapUnitViewModel.ScreenPosX walks to project a unit onto the screen.
        private static Camera CurrentCamera()
        {
            object manager = Singleton(CameraManagerTypeName);
            if (manager == null) return null;

            object cameraBase = GameReflect.Call(manager, "getCurrentCamera");
            if (cameraBase == null) cameraBase = GameReflect.Member(manager, "_currentCamera");
            if (cameraBase == null) return null;

            object camera = GameReflect.Call(cameraBase, "getCamera");
            if (camera == null) camera = GameReflect.Member(cameraBase, "_camera");
            return camera as Camera;
        }

        // ---------------------------------------------------------------- unit fields

        private static string UnitName(object unit)
        {
            object v = Rx(unit, "Name");
            return v == null ? null : v.ToString();
        }

        // DisplayNameFull is a plain string in 0.8.7; going through Rx keeps this working if a
        // patch turns it into a ReactiveProperty.
        private static string UnitDisplayName(object unit)
        {
            object v = Rx(unit, "DisplayNameFull");
            if (v == null) v = Rx(unit, "DisplayNameShort");
            return v == null ? null : v.ToString();
        }

        private static object TaskforceOf(object unit)
        {
            object tf = GameReflect.Member(unit, "_taskforce");
            if (tf == null) tf = Rx(unit, "UnitTaskforce");
            return tf;
        }

        private static string TaskforceName(object taskforce)
        {
            if (taskforce == null) return null;
            object v = Rx(taskforce, "Name");
            if (v != null) return v.ToString();
            // Taskforce exposes no Name property in 0.8.7; the mission file name is the only
            // stable human-readable label it has.
            object raw = GameReflect.Member(taskforce, "_nameInMissionFile");
            return raw == null ? null : raw.ToString();
        }

        // Taskforce.Side is SeaPower.Taskforce/TfType: None, Player, Ally, Enemy, Neutral.
        private static string TfTypeName(object taskforce)
        {
            if (taskforce == null) return null;
            object side = GameReflect.Member(taskforce, "Side");
            return side == null ? null : side.ToString();
        }

        private static object GeoOf(object unit)
        {
            // ObjectBase.Position is a ReactiveProperty<GeoPosition>; the value is on .Value.
            object position = GameReflect.Member(unit, "Position");
            object geo = position == null ? null : GameReflect.Member(position, "Value");
            if (geo != null) return geo;
            geo = GameReflect.Member(unit, "_geoPosition");
            if (geo != null) return geo;
            return GameReflect.Call(unit, "getGeoPosition");
        }

        // GeoPosition keeps the height on a private _height field; there is no Height property
        // in 0.8.7, so an unknown height is null rather than a made-up zero.
        private static double GeoHeight(object geo)
        {
            if (geo == null) return double.NaN;
            return GameReflect.Num(GameReflect.Member(geo, "_height"), double.NaN);
        }

        // Unwrap UniRx ReactiveProperty<T> to its value; anything else is returned as it is.
        private static object Rx(object holder, string name)
        {
            object v = GameReflect.Member(holder, name);
            if (v == null) return null;
            string typeName = v.GetType().Name;
            if (typeName.StartsWith("ReactiveProperty", StringComparison.Ordinal)
                || typeName.StartsWith("ReadOnlyReactiveProperty", StringComparison.Ordinal))
                return GameReflect.Member(v, "Value");
            return v;
        }

        // ---------------------------------------------------------------- distances

        private static double DegreesToMetres(double latA, double lonA, double latB, double lonB)
        {
            if (double.IsNaN(latA) || double.IsNaN(lonA) || double.IsNaN(latB) || double.IsNaN(lonB))
                return double.NaN;
            double midLat = (latA + latB) * 0.5;
            double north = (latB - latA) * MetersPerDegree;
            double east = AngleDelta(lonB, lonA) * MetersPerDegree * Math.Cos(midLat * Math.PI / 180.0);
            return Math.Sqrt(north * north + east * east);
        }

        // Shortest signed difference in degrees, so 359 vs 1 reads as 2 and not as 358: the
        // inverse formula crosses the antimeridian and a naive subtraction reports a whole
        // planet of error there.
        private static double AngleDelta(double a, double b)
        {
            if (double.IsNaN(a) || double.IsNaN(b)) return double.NaN;
            double d = a - b;
            while (d > 180.0) d -= 360.0;
            while (d <= -180.0) d += 360.0;
            return d;
        }

        // ---------------------------------------------------------------- json helpers

        // NaN/Infinity never reach the wire: Json.NET writes them as bare tokens that the
        // client's parser rejects, so an unknown number becomes null instead.
        private static JToken Tok(double v, int digits)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return JValue.CreateNull();
            return new JValue(Math.Round(v, digits));
        }

        private static JToken TokId(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return JValue.CreateNull();
            return new JValue((long)Math.Round(v));
        }

        private static JToken Text(string s)
        {
            return s == null ? (JToken)JValue.CreateNull() : new JValue(s);
        }

        private static JObject Vec3(Vector3 v)
        {
            JObject j = new JObject();
            j["x"] = Tok(v.x, 3);
            j["y"] = Tok(v.y, 3);
            j["z"] = Tok(v.z, 3);
            return j;
        }

        // Noesis.Point and Noesis.Vector both carry their coordinates as X/Y properties.
        private static JObject Pt2(object pair)
        {
            if (pair == null) return null;
            JObject j = new JObject();
            j["x"] = Tok(GameReflect.Num(GameReflect.Member(pair, "X"), double.NaN), 3);
            j["y"] = Tok(GameReflect.Num(GameReflect.Member(pair, "Y"), double.NaN), 3);
            return j;
        }

        private static string Inv(double v)
        {
            return v.ToString(CultureInfo.InvariantCulture);
        }

        // ---------------------------------------------------------------- request validation

        // Local copies of the Api.cs Req* helpers: those are private to Api, and the contract
        // forbids editing that file, so the rules are restated here rather than relaxed.
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
                        "op '" + op + "' does not accept field '" + p.Name + "'; allowed: "
                        + string.Join(", ", allowed), false);
            }
        }

        // Absent means "use the default"; present-but-null is a mistake, not a default.
        private static JToken Present(JObject req, string key)
        {
            JToken t = req == null ? null : req[key];
            if (t == null) return null;
            if (t.Type == JTokenType.Null)
                throw new ApiException(400, "bad_type",
                    "'" + key + "' was explicitly null; omit it to take the default", false);
            return t;
        }

        private static double ReqNum(JObject req, string key, bool required, double dflt)
        {
            JToken t = Present(req, key);
            if (t == null)
            {
                if (required)
                    throw new ApiException(400, "missing_field", "'" + key + "' is required", false);
                return dflt;
            }
            if (t.Type != JTokenType.Float && t.Type != JTokenType.Integer)
                throw new ApiException(400, "bad_type",
                    "'" + key + "' must be a number, got " + t.Type.ToString(), false);
            double v;
            try { v = (double)t; }
            catch (Exception)
            {
                throw new ApiException(400, "bad_type", "'" + key + "' is not a valid number", false);
            }
            if (double.IsNaN(v) || double.IsInfinity(v))
                throw new ApiException(400, "bad_number", "'" + key + "' must be a finite number", false);
            return v;
        }

        // Integers accept 12 and 12.0 but not 12.5: a fractional unit id is a caller bug that
        // would otherwise be rounded into a different unit.
        private static int ReqIntIn(JObject req, string key, bool required, int dflt, int min, int max)
        {
            double raw = ReqNum(req, key, required, (double)dflt);
            if (raw != Math.Floor(raw))
                throw new ApiException(400, "bad_type",
                    "'" + key + "' must be an integer, got " + Inv(raw), false);
            if (raw < (double)min || raw > (double)max)
                throw new ApiException(400, "bad_value",
                    "'" + key + "' must be between " + min + " and " + max + ", got " + Inv(raw), false);
            return (int)raw;
        }

        // Latitude outside +-90 is not a projection problem, it is a coordinate that cannot
        // exist; bad_value says so instead of returning a point in the wrong tile.
        private static void RequireLatitude(double lat)
        {
            if (lat < -90.0 || lat > 90.0)
                throw new ApiException(400, "bad_value",
                    "'lat' must be between -90 and 90 degrees, got " + Inv(lat), false);
        }
    }
}
