// MapView.cs - the plot table's "eyes" (what is on screen and where the camera is) plus
// the camera moves behind /map/focus, /map/rotate, /map/zoom, /map/select, /map/deselect
// and /map/set-center.
//
// MapUnits.cs and MapPlot.cs deliberately call MainGame() / MapBase() from here instead of
// walking the visual tree themselves: three copies of "find the live map control" is how
// two of them end up attached to a PiP map or a stale scene while the third talks to the
// real one.
//
// Everything below runs on the Unity main thread (Api.Dispatch marshals the request), and
// nothing here creates threads or touches UnityEngine APIs from a background thread.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace SeaPowerAgentBridge
{
    internal static class MapView
    {
        // Full type names, verified against Seapower-Scripts.dll with ildump. Keeping them in
        // one place means a rename in a future patch is a one-line fix, not a hunt.
        private const string MainGameTypeName = "SeapowerUI.ViewModels.MainGameViewModel";
        private const string MapTypeName = "SeapowerUI.ViewModels.SceneMapViewModel";
        private const string MapBaseTypeName = "MapControl.MapBase";
        private const string LocationTypeName = "MapControl.Location";
        private const string RenderPositionTypeName = "SeaPower.RenderPosition";
        private const string CameraManagerTypeName = "SeaPower.CameraManager";
        private const string CoreServiceTypeName = "SeaPower.CoreService";
        private const string GeoPositionTypeName = "SeaPower.GeoPosition";
        private const string NoesisViewTypeName = "Noesis.NoesisView";

        // CameraManager.Mode, from the enum's field order.
        private const int ModeInit = 0;
        private const int ModeFollow = 1;
        private const int ModeFlyby = 2;
        private const int ModeFree = 3;
        private const int ModeFollowBehind = 4;

        // The contract pins this radius so a distance computed here can be compared with one
        // the caller computed the same way. 6378137.0 is the WGS84 equatorial radius.
        private const double EarthRadiusM = 6378137.0;
        private const double MetersPerDegree = Math.PI * EarthRadiusM / 180.0;

        // A setGlobalPosition that read back further than this did not really move the
        // camera: the usual cause is the camera still being attached to a target.
        private const double FocusOkMeters = 100.0;

        // ---------------------------------------------------------------- shared lookups

        /// <summary>
        /// The live MainGameViewModel, or null when there is no session UI yet.
        /// MapUnits/MapPlot call this, so it must never throw.
        /// </summary>
        internal static object MainGame()
        {
            Type t = GameReflect.FindType(MainGameTypeName);
            if (t == null) return null;

            // FindObjectsOfType only sees active objects, which is exactly the one that owns
            // the live map. Resources is the fallback for the frame or two before the view is
            // enabled; it is deliberately second because it also returns prefab assets.
            try
            {
                UnityEngine.Object[] live = UnityEngine.Object.FindObjectsOfType(t);
                if (live != null)
                {
                    for (int i = 0; i < live.Length; i++)
                    {
                        if (live[i] != null) return live[i];
                    }
                }
            }
            catch (Exception) { }

            try
            {
                UnityEngine.Object[] any = UnityEngine.Resources.FindObjectsOfTypeAll(t);
                if (any != null)
                {
                    for (int i = 0; i < any.Length; i++)
                    {
                        if (any[i] != null) return any[i];
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>
        /// The live MapControl.MapBase, or null. Walked from the MainGameViewModel's own
        /// RootControl so we get the map the player is looking at, then from the tactical
        /// root and finally from every NoesisView as fallbacks for the frames where the view
        /// model is not bound yet. Never throws: the other two files call it per request.
        /// </summary>
        internal static object MapBase()
        {
            object mg = MainGame();
            if (mg != null)
            {
                object hit = FindMapBase(GameReflect.Member(mg, "RootControl"));
                if (hit != null) return hit;
            }

            object tactical = null;
            try { tactical = UiBridge.TacticalRootForInspector(); }
            catch (Exception) { }
            object hit2 = FindMapBase(tactical);
            if (hit2 != null) return hit2;

            return MapBaseFromNoesisViews();
        }

        private static object FindMapBase(object root)
        {
            if (root == null) return null;
            try { return UiBridge.FindTypeForInspector(root, MapBaseTypeName); }
            catch (Exception) { return null; }
        }

        // A second NoesisView hosts the fullscreen map and the second-display map; when the
        // MainGameViewModel walk fails they are the only places left to look.
        private static object MapBaseFromNoesisViews()
        {
            Type nv = GameReflect.FindType(NoesisViewTypeName);
            if (nv == null) return null;
            UnityEngine.Object[] all;
            try { all = UnityEngine.Object.FindObjectsOfType(nv); }
            catch (Exception) { return null; }
            if (all == null) return null;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null) continue;
                // NoesisView keeps the root on the private _uiView field; Content on the view
                // itself is only a pass-through on some builds, so try both.
                object uiView = GameReflect.Member(all[i], "_uiView");
                object content = uiView == null ? null : GameReflect.Member(uiView, "Content");
                object hit = FindMapBase(content);
                if (hit != null) return hit;
                object direct = FindMapBase(all[i]);
                if (direct != null) return direct;
            }
            return null;
        }

        // ---------------------------------------------------------------- GET /map

        internal static JObject Read()
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread",
                "/map must run on the Unity main thread", false);

            JObject o = new JObject();

            Type mgType = GameReflect.FindType(MainGameTypeName);
            o["mainGameType"] = Text(mgType == null ? null : mgType.FullName);
            object mg = MainGame();
            o["mainGameFound"] = Tok(mg != null);

            object map = mg == null ? null : GameReflect.Member(mg, "Map");
            o["mapFound"] = Tok(map != null);
            o["mapType"] = Text(map == null ? null : map.GetType().FullName);
            Type mapType = GameReflect.FindType(MapTypeName);
            o["mapTypeExpected"] = Text(mapType == null ? null : mapType.FullName);

            JObject display = DisplayMapBlock(map);
            o["displayMap"] = display == null ? (JToken)JValue.CreateNull() : display;

            object mapBase = MapBase();
            o["mapBaseFound"] = Tok(mapBase != null);
            o["mapBaseType"] = Text(mapBase == null ? null : mapBase.GetType().FullName);
            JObject mb = MapBaseBlock(mapBase);
            o["mapBase"] = mb == null ? (JToken)JValue.CreateNull() : mb;

            o["camera"] = CameraBlock();

            o["interactionState"] = Text(InteractionStateName(map));
            object drawingLayer = map == null ? null : GameReflect.Member(map, "DrawingLayer");
            object graphics = drawingLayer == null ? null : GameReflect.Member(drawingLayer, "Graphics");
            o["drawingLayerFound"] = Tok(graphics != null);
            o["drawingCount"] = TokId(DrawingCount(graphics));

            object sel = SelectedObject();
            o["selectedObject"] = sel == null ? (JToken)JValue.CreateNull() : UnitBox(sel);
            o["cameraAttachedToTarget"] = Tok(CameraAttached());

            // PiP / fullscreen are read only to explain a mapBase that is not the main one.
            o["secondMapFound"] = Tok(GameReflect.Member(mg, "SecondMap") != null);
            o["isFullscreen"] = Tok(GameReflect.Truth(GameReflect.Member(map, "IsFullscreen")));

            JArray notes = new JArray();
            if (mg == null)
                notes.Add(new JValue("no MainGameViewModel: the game is not in a running session (main menu or still loading)"));
            if (mapBase == null)
                notes.Add(new JValue("no MapControl.MapBase in the visual tree: the plot table is not visible, so /map/set-center cannot run"));
            if (CameraManager() == null)
                notes.Add(new JValue("no CameraManager singleton: camera moves in this file will refuse with 409 no_ui_host"));
            if (notes.Count > 0) o["notes"] = notes;
            return o;
        }

        private static JObject DisplayMapBlock(object map)
        {
            object dm = map == null ? null : GameReflect.Member(map, "DisplayMap");
            if (dm == null) return null;
            JObject j = new JObject();
            object center = GameReflect.Member(dm, "Center");
            j["centerLat"] = Tok(GameReflect.Lat(center), 7);
            j["centerLon"] = Tok(GameReflect.Lon(center), 7);
            j["centerHeight"] = Tok(GeoHeight(center), 2);
            j["zoom"] = Tok(GameReflect.Num(GameReflect.Member(dm, "Zoom"), double.NaN), 3);
            return j;
        }

        private static JObject MapBaseBlock(object mapBase)
        {
            if (mapBase == null) return null;
            JObject j = new JObject();
            object center = GameReflect.Member(mapBase, "Center");
            j["centerLat"] = Tok(GameReflect.Num(GameReflect.Member(center, "Latitude"), double.NaN), 7);
            j["centerLon"] = Tok(GameReflect.Num(GameReflect.Member(center, "Longitude"), double.NaN), 7);
            j["zoomLevel"] = Tok(GameReflect.Num(GameReflect.Member(mapBase, "ZoomLevel"), double.NaN), 3);
            j["minZoomLevel"] = Tok(GameReflect.Num(GameReflect.Member(mapBase, "MinZoomLevel"), double.NaN), 3);
            j["maxZoomLevel"] = Tok(GameReflect.Num(GameReflect.Member(mapBase, "MaxZoomLevel"), double.NaN), 3);
            j["heading"] = Tok(GameReflect.Num(GameReflect.Member(mapBase, "Heading"), double.NaN), 3);
            object size = GameReflect.Member(mapBase, "RenderSize");
            j["widthPx"] = Tok(GameReflect.Num(GameReflect.Member(size, "Width"), double.NaN), 1);
            j["heightPx"] = Tok(GameReflect.Num(GameReflect.Member(size, "Height"), double.NaN), 1);
            return j;
        }

        // Graphics is an ObservableCollection<IDrawing>, so it has Count, but read through
        // IList first: that is the shape the rest of the game uses and it survives the
        // collection being swapped for a plain List in a future patch.
        private static double DrawingCount(object graphics)
        {
            if (graphics == null) return double.NaN;
            IList list = graphics as IList;
            if (list != null) return (double)list.Count;
            return GameReflect.Num(GameReflect.Member(graphics, "Count"), double.NaN);
        }

        // ReadOnlyReactiveProperty<IInteractionState> is only interesting for the state
        // object it currently holds, so report that type; fall back to the property's own
        // type when the stream has no value yet.
        private static string InteractionStateName(object map)
        {
            object prop = map == null ? null : GameReflect.Member(map, "InteractionState");
            if (prop == null) return null;
            object value = GameReflect.Member(prop, "Value");
            if (value != null) return value.GetType().FullName;
            return prop.GetType().FullName;
        }

        private static JObject CameraBlock()
        {
            JObject c = new JObject();
            object rp = RenderPosition();
            object cm = CameraManager();
            object cb = CameraBaseOf(cm);

            object geo = rp == null ? null : GameReflect.Member(rp, "CurrentGeoPosition");
            c["geoLat"] = Tok(GameReflect.Lat(geo), 7);
            c["geoLon"] = Tok(GameReflect.Lon(geo), 7);
            c["geoHeight"] = Tok(GeoHeight(geo), 2);
            c["renderPositionFound"] = Tok(rp != null);
            c["cameraManagerFound"] = Tok(cm != null);

            int modeId = ModeId(cm);
            c["mode"] = Text(ModeName(modeId));
            c["modeId"] = TokId(modeId);
            c["height"] = Tok(NumOf(cb, cm, "getCameraHeight"), 2);
            c["heading"] = Tok(NumOf(cb, cm, "getCameraHeading"), 3);
            c["pitch"] = Tok(NumOf(cb, cm, "getCameraPitch"), 3);
            c["distanceToPivot"] = Tok(DistanceToPivot(cm), 2);
            // getDistanceToPivot returns 0f outside Follow mode rather than null, so a bare
            // "0" would be read as "the camera is on the pivot". This flag tells them apart.
            c["distanceToPivotReadable"] = Tok(modeId == ModeFollow);
            c["fov"] = Tok(GameReflect.Num(GameReflect.Member(cm, "CameraFOV"), double.NaN), 3);
            c["overrideFov"] = Tok(GameReflect.Truth(GameReflect.Member(cm, "OverrideFOV")));
            c["unityPos"] = VecBox(CameraUnityPos(cm, cb, rp));
            return c;
        }

        // ---------------------------------------------------------------- POST /map/focus

        internal static JObject Focus(JObject req)
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread",
                "/map/focus must run on the Unity main thread", false);

            RejectUnknown(req, "map/focus",
                new string[] { "lat", "lon", "height", "detach", "updateLocal", "validate_only" });

            double lat = ReqNum(req, "lat", true, double.NaN);
            double lon = ReqNum(req, "lon", true, double.NaN);
            RequireRange(lat, "lat", -90.0, 90.0);
            RequireRange(lon, "lon", -180.0, 180.0);

            bool hasHeight = Has(req, "height");
            double height = ReqNum(req, "height", false, 0.0);
            if (hasHeight) RequireRange(height, "height", -11000.0, 90000.0);

            bool detach = ReqBool(req, "detach", true);
            bool updateLocal = ReqBool(req, "updateLocal", true);
            bool validateOnly = ReqBool(req, "validate_only", false);

            object rp = RenderPosition();
            if (rp == null)
                throw new ApiException(409, "no_ui_host",
                    "SeaPower.RenderPosition is not alive, so there is no camera to move - the game is probably not in a mission",
                    true);

            object beforeGeo = GameReflect.Member(rp, "CurrentGeoPosition");
            if (!hasHeight)
            {
                // Dropping an unasked-for camera to sea level looks like a bug from the
                // outside, so an omitted height keeps the altitude the camera already has.
                height = GeoHeight(beforeGeo);
                if (double.IsNaN(height) || double.IsInfinity(height)) height = 0.0;
            }

            object target = GameReflect.Geo(lat, lon, height);
            if (target == null)
                throw new ApiException(500, "route_error",
                    "GeoPosition(double,double,double) rejected (" + Inv(lat) + ", " + Inv(lon) + ", " + Inv(height) + ")",
                    false);

            JObject o = new JObject();
            o["requested"] = GeoBoxAt(lat, lon, height);
            o["updateLocal"] = Tok(updateLocal);
            o["detach"] = Tok(detach);
            o["before"] = GeoBoxOf(beforeGeo);
            o["unityPosBefore"] = VecBox(PivotUnityPos(rp));

            if (validateOnly)
            {
                o["validated"] = Tok(true);
                o["detached"] = Tok(false);
                o["after"] = JValue.CreateNull();
                o["moveErrorM"] = JValue.CreateNull();
                o["moveErrorDeg"] = JValue.CreateNull();
                o["unityPosAfter"] = JValue.CreateNull();
                o["verified"] = Tok(false);
                o["note"] = Text("validate_only: parameters were accepted and the camera was not touched");
                return o;
            }

            bool detached = false;
            if (detach)
            {
                GameReflect.Call(rp, "detachCameraFromTarget");
                // Read the flag back: "we called the method" is not the same as "the camera
                // let go", and the caller needs to know which one happened.
                detached = !CameraAttached();
            }
            o["detached"] = Tok(detached);

            // updateLocal=false is honoured on purpose: the game keeps the geo bookkeeping in
            // sync and leaves the transform alone, which is what a caller comparing the two
            // wants. Everything else goes through the real move path.
            GameReflect.Call(rp, "setGlobalPosition", target, updateLocal);

            object afterGeo = GameReflect.Member(rp, "CurrentGeoPosition");
            double errM = DistanceM(target, afterGeo);
            o["after"] = GeoBoxOf(afterGeo);
            o["moveErrorM"] = Tok(errM, 3);
            o["moveErrorDeg"] = Tok(errM / MetersPerDegree, 9);
            o["unityPosAfter"] = VecBox(PivotUnityPos(rp));
            o["cameraUnityPosAfter"] = VecBox(CameraUnityPos(CameraManager(), CameraBaseOf(CameraManager()), rp));

            bool geoOk = !double.IsNaN(errM) && errM <= FocusOkMeters;
            o["verified"] = Tok(geoOk);
            if (!geoOk)
                o["note"] = Text("the camera read back " + Fmt(errM) + " m from the request (limit " + Fmt(FocusOkMeters)
                    + " m); setGlobalPosition recomputes from the centre tile, so this usually means the camera is still attached to a target - retry with detach:true");
            else if (!updateLocal)
                o["note"] = Text("updateLocal=false: the geo position moved but the transform was left alone by design; the render camera will not show the new spot until something updates it");
            return o;
        }

        // ---------------------------------------------------------------- POST /map/rotate

        internal static JObject Rotate(JObject req)
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread",
                "/map/rotate must run on the Unity main thread", false);

            RejectUnknown(req, "map/rotate", new string[] { "yaw", "pitch", "validate_only" });

            double yaw = ReqNum(req, "yaw", true, double.NaN);
            RequireRange(yaw, "yaw", -36000.0, 36000.0);
            bool hasPitch = Has(req, "pitch");
            double pitch = ReqNum(req, "pitch", false, 0.0);
            if (hasPitch) RequireRange(pitch, "pitch", -360.0, 360.0);
            bool validateOnly = ReqBool(req, "validate_only", false);

            object rp = RenderPosition();
            if (rp == null)
                throw new ApiException(409, "no_ui_host",
                    "SeaPower.RenderPosition is not alive, so there is no camera to rotate - the game is probably not in a mission",
                    true);

            object cm = CameraManager();
            object cb = CameraBaseOf(cm);
            double beforeYaw = NumOf(null, rp, "getRotationY");
            double beforePitch = NumOf(cb, cm, "getCameraPitch");

            JObject o = new JObject();
            o["requested"] = AngleBox(yaw, hasPitch ? pitch : double.NaN);
            o["before"] = AngleBox(beforeYaw, beforePitch);

            if (validateOnly)
            {
                o["validated"] = Tok(true);
                o["after"] = JValue.CreateNull();
                o["verified"] = Tok(false);
                o["note"] = Text("validate_only: parameters were accepted and the camera rotation was not touched");
                return o;
            }

            if (hasPitch)
            {
                // setRotationXY(xAngle, yAngle) writes localEulerAngles = (x, y, currentZ),
                // so x is the PITCH and y is the YAW. Swapping the two would pitch the camera
                // by the requested heading, which still "succeeds" and looks like a mystery.
                GameReflect.Call(rp, "setRotationXY", F(pitch), F(yaw));
            }
            else
            {
                GameReflect.Call(rp, "setRotationY", F(yaw));
            }

            // getRotationY reads transform.eulerAngles.y (0..360) while the setter writes the
            // LOCAL euler angle, so compare by shortest arc instead of by raw value.
            double afterYaw = NumOf(null, rp, "getRotationY");
            double afterPitch = NumOf(cb, cm, "getCameraPitch");
            o["after"] = AngleBox(afterYaw, afterPitch);
            double yawError = AngleDelta(afterYaw, yaw);
            o["yawErrorDeg"] = Tok(yawError, 4);

            bool ok = !double.IsNaN(yawError) && Math.Abs(yawError) <= 0.5;
            o["verified"] = Tok(ok);
            if (!ok)
                o["note"] = Text("the camera read back heading " + Fmt(afterYaw) + " instead of " + Fmt(yaw)
                    + "; setRotationXY silently drops the whole call when any angle is NaN, and a rotation-locked follow camera may overwrite it in LateUpdate");
            return o;
        }

        // ---------------------------------------------------------------- POST /map/zoom

        internal static JObject Zoom(JObject req)
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread",
                "/map/zoom must run on the Unity main thread", false);

            RejectUnknown(req, "map/zoom", new string[] { "distance", "steps", "validate_only" });

            bool hasDistance = Has(req, "distance");
            bool hasSteps = Has(req, "steps");
            if (!hasDistance && !hasSteps)
                throw new ApiException(400, "missing_field",
                    "give 'distance' (meters from the pivot) or 'steps' (positive zooms in, negative zooms out)", false);

            double distance = hasDistance ? ReqNum(req, "distance", false, 0.0) : 0.0;
            if (hasDistance) RequireRange(distance, "distance", 1.0, 1000000.0);
            int steps = hasSteps ? ReqIntIn(req, "steps", false, 0, -50, 50) : 0;
            bool validateOnly = ReqBool(req, "validate_only", false);

            object cm = CameraManager();
            if (cm == null)
                throw new ApiException(409, "no_ui_host",
                    "SeaPower.CameraManager is not alive, so there is nothing to zoom - the game is probably not in a mission",
                    true);

            int modeId = ModeId(cm);
            double beforeDistance = DistanceToPivot(cm);
            double beforeFov = GameReflect.Num(GameReflect.Member(cm, "CameraFOV"), double.NaN);

            JObject o = new JObject();
            // distance wins when both are sent; the steps value is rejected? No - it is
            // ignored, and saying so is friendlier than silently dropping it.
            o["route"] = Text(hasDistance ? "distance" : "steps");
            if (hasDistance && hasSteps) o["ignored"] = Text("steps");
            o["mode"] = Text(ModeName(modeId));
            o["modeId"] = TokId(modeId);
            // setDistanceToPivot only acts in Follow(1) and zoomIn/zoomOut only act in Free(3):
            // the game gates both, so the caller can be told up front which route can work.
            o["modeSuitable"] = Tok(hasDistance ? modeId == ModeFollow : modeId == ModeFree);
            o["before"] = ZoomBox(beforeDistance, beforeFov);

            if (validateOnly)
            {
                o["validated"] = Tok(true);
                o["after"] = JValue.CreateNull();
                o["verified"] = Tok(false);
                o["note"] = Text("validate_only: parameters were accepted and the camera zoom was not touched");
                return o;
            }

            if (hasDistance)
            {
                GameReflect.Call(cm, "setDistanceToPivot", F(distance));
                double afterDistance = DistanceToPivot(cm);
                double afterFov = GameReflect.Num(GameReflect.Member(cm, "CameraFOV"), double.NaN);
                o["after"] = ZoomBox(afterDistance, afterFov);
                o["distanceErrorM"] = Tok(double.IsNaN(afterDistance) ? double.NaN : afterDistance - distance, 3);

                // FollowCamera.getDistanceToPivot is -localPosition.z, and the setter writes
                // that transform immediately, so this readback is synchronous and trustworthy
                // when - and only when - the camera really is in Follow mode.
                bool ok = modeId == ModeFollow && !double.IsNaN(afterDistance);
                o["verified"] = Tok(ok);
                double tol = Math.Max(0.5, 0.01 * Math.Abs(distance));
                if (ok && Math.Abs(afterDistance - distance) > tol)
                    o["note"] = Text("the game clamped the distance to its own min/max (" + Fmt(afterDistance) + " m); the field is 0 unless the camera is following a target");
                else if (!ok)
                    o["note"] = Text("CameraManager.setDistanceToPivot only acts in Follow mode (currently " + ModeName(modeId)
                        + "); call /map/select first, or use steps in Free mode");
                return o;
            }

            int count = Math.Abs(steps);
            for (int i = 0; i < count; i++)
            {
                if (steps > 0) GameReflect.Call(cm, "zoomIn");
                else GameReflect.Call(cm, "zoomOut");
            }
            double afterFovSteps = GameReflect.Num(GameReflect.Member(cm, "CameraFOV"), double.NaN);
            o["after"] = ZoomBox(DistanceToPivot(cm), afterFovSteps);
            o["appliedSteps"] = TokId(steps);

            // FreeCamera.zoomIn only decays a _zoomSpeed accumulator; the FOV itself is
            // applied in FreeCamera.Update on later frames. So a same-frame readback cannot
            // prove the zoom, and claiming verified here would be a lie.
            bool stepsOk = modeId == ModeFree;
            o["accepted"] = Tok(stepsOk);
            o["verified"] = Tok(false);
            o["note"] = Text(stepsOk
                ? "zoomIn/zoomOut were delivered in Free mode, but the FOV is applied by FreeCamera.Update over the following frames; read /map again after a frame to see it"
                : "CameraManager.zoomIn/zoomOut only act in Free mode (currently " + ModeName(modeId) + "); nothing was applied");
            return o;
        }

        // ---------------------------------------------------------------- POST /map/select

        internal static JObject Select(JObject req)
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread",
                "/map/select must run on the Unity main thread", false);

            RejectUnknown(req, "map/select", new string[] { "unit", "validate_only" });

            int unitId = ReqIntIn(req, "unit", true, 0, 0, int.MaxValue);
            bool validateOnly = ReqBool(req, "validate_only", false);

            object unit = FindUnitById(unitId);
            // REVIEW (round 2, blocker 2): the miss used to say "no live ObjectBase has UniqueID N",
            // a third wording for the same question /units answers with "unit not found: N" - and a
            // hull the player never detected was selectable, which answered where it is. I8: absent
            // and undetected reply identically.
            if (unit == null)
                throw new ApiException(404, "unit_not_found", "unit not found: " + unitId, false);
            // REVIEW (round 3): the gate used to read `selectOb != null && !PlayerKnows(...)`, so any
            // id that resolved to something that is NOT an ObjectBase skipped the intel question
            // entirely - and then validate_only answered validated:true for it. Selecting is a read;
            // a thing the intel policy cannot speak about is not selectable either, and it must say
            // so with the same wording as everything else.
            SeaPower.ObjectBase selectOb = unit as SeaPower.ObjectBase;
            if (selectOb == null || !IntelScope.PlayerKnows(selectOb))
                throw new ApiException(404, "unit_not_found", "unit not found: " + unitId, false);

            JObject o = new JObject();
            o["unit"] = UnitBox(unit);
            o["previous"] = SelectedObjectBox();

            if (validateOnly)
            {
                o["validated"] = Tok(true);
                o["after"] = JValue.CreateNull();
                o["verified"] = Tok(false);
                o["sideEffects"] = SideEffects("Select is a camera command, not just a highlight",
                    "the camera attaches to the unit and follows it from now on",
                    "CameraManager switches to Follow mode (1) and RenderPosition.IsCameraAttachedToTarget becomes true",
                    "the camera roll is zeroed; sending other camera moves without detach:true will fight the follow camera");
                o["note"] = Text("validate_only: the unit exists and would be selected; nothing was called");
                return o;
            }

            object rp = RenderPosition();
            if (rp == null)
                throw new ApiException(409, "no_ui_host",
                    "SeaPower.RenderPosition is not alive, so nothing can attach the camera - the game is probably not in a mission",
                    true);

            // performSmoothMove=false, keepCamRotation=true, checkAirLeader=false: the agent
            // wants the camera there now, with the heading it already had, and the air-leader
            // special case only applies to formations the player is flying.
            GameReflect.Call(rp, "switchToObject", unit, false, true, false);

            object after = SelectedObject();
            o["after"] = after == null ? (JToken)JValue.CreateNull() : UnitBox(after);
            o["cameraAttachedToTarget"] = Tok(CameraAttached());
            o["mode"] = Text(ModeName(ModeId(CameraManager())));
            o["sideEffects"] = SideEffects("Select is a camera command, not just a highlight",
                "the camera attaches to the unit and follows it from now on",
                "CameraManager switches to Follow mode (1) and RenderPosition.IsCameraAttachedToTarget becomes true",
                "the camera roll is zeroed; sending other camera moves without detach:true will fight the follow camera");

            double afterId = after == null ? double.NaN : GameReflect.Num(GameReflect.Member(after, "UniqueID"), double.NaN);
            bool ok = !double.IsNaN(afterId) && (int)Math.Round(afterId) == unitId;
            o["verified"] = Tok(ok);
            if (!ok)
                o["note"] = Text("RenderPosition.SelectedObject did not become UniqueID " + unitId
                    + "; switchToObject refuses weapons whose submunition is already live and destroyed objects, so check /units for the current id");
            return o;
        }

        // ---------------------------------------------------------------- POST /map/deselect

        internal static JObject Deselect(JObject req)
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread",
                "/map/deselect must run on the Unity main thread", false);

            RejectUnknown(req, "map/deselect", new string[] { "validate_only" });
            bool validateOnly = ReqBool(req, "validate_only", false);

            object rp = RenderPosition();
            if (rp == null)
                throw new ApiException(409, "no_ui_host",
                    "SeaPower.RenderPosition is not alive, so there is no camera attachment to clear - the game is probably not in a mission",
                    true);

            JObject o = new JObject();
            JObject before = new JObject();
            before["selectedObject"] = SelectedObjectBox();
            before["attached"] = Tok(CameraAttached());
            before["mode"] = Text(ModeName(ModeId(CameraManager())));
            o["before"] = before;

            if (validateOnly)
            {
                o["validated"] = Tok(true);
                o["after"] = JValue.CreateNull();
                o["verified"] = Tok(false);
                o["sideEffects"] = DeselectSideEffects();
                o["note"] = Text("validate_only: nothing was called");
                return o;
            }

            // Per IL this clears SelectedObject, forces CameraManager to Free mode (3),
            // clears IsCameraAttachedToTarget and zeroes the euler roll - all side effects
            // the caller has to be told about, because /map/zoom {distance} stops working
            // afterwards until the next /map/select.
            GameReflect.Call(rp, "deselectObjectAndDetachCamera");

            JObject after = new JObject();
            after["selectedObject"] = SelectedObjectBox();
            after["attached"] = Tok(CameraAttached());
            after["mode"] = Text(ModeName(ModeId(CameraManager())));
            o["after"] = after;
            o["sideEffects"] = DeselectSideEffects();

            bool ok = !CameraAttached() && SelectedObject() == null;
            o["verified"] = Tok(ok);
            if (!ok)
                o["note"] = Text("the camera still reports a selection or an attachment; another system may be re-attaching it every frame");
            return o;
        }

        private static JArray DeselectSideEffects()
        {
            return SideEffects("Deselect also moves the camera, it does not just clear a highlight",
                "RenderPosition.SelectedObject becomes null",
                "CameraManager switches to Free mode (3), so a later /map/zoom {distance} is a no-op until something selects again",
                "the camera roll is zeroed and the camera stops following whatever it was following");
        }

        // ---------------------------------------------------------------- POST /map/set-center

        // The control-level route from the contract table: it writes MapBase's Center /
        // ZoomLevel dependency properties, which is what the map itself does when the player
        // drags, so it needs no camera and no unit.
        internal static JObject SetCenter(JObject req)
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread",
                "/map/set-center must run on the Unity main thread", false);

            RejectUnknown(req, "map/set-center", new string[] { "lat", "lon", "zoom", "validate_only" });

            double lat = ReqNum(req, "lat", true, double.NaN);
            double lon = ReqNum(req, "lon", true, double.NaN);
            RequireRange(lat, "lat", -90.0, 90.0);
            RequireRange(lon, "lon", -180.0, 180.0);
            bool hasZoom = Has(req, "zoom");
            double zoom = ReqNum(req, "zoom", false, 0.0);
            bool validateOnly = ReqBool(req, "validate_only", false);

            object mapBase = MapBase();
            if (mapBase == null)
                throw new ApiException(409, "no_ui_host",
                    "MapControl.MapBase is not in the visual tree, so there is no map to centre - open the plot table first (GET /map reports mapBaseFound)",
                    true);

            double minZoom = GameReflect.Num(GameReflect.Member(mapBase, "MinZoomLevel"), double.NaN);
            double maxZoom = GameReflect.Num(GameReflect.Member(mapBase, "MaxZoomLevel"), double.NaN);
            if (hasZoom)
            {
                // The control clamps to its own bounds; refusing here keeps /map/set-center
                // from looking like it accepted a zoom the map will silently ignore.
                if (!double.IsNaN(minZoom) && zoom < minZoom)
                    throw new ApiException(400, "bad_value",
                        "'zoom' must be >= the control's MinZoomLevel (" + Inv(minZoom) + "), got " + Inv(zoom), false);
                if (!double.IsNaN(maxZoom) && zoom > maxZoom)
                    throw new ApiException(400, "bad_value",
                        "'zoom' must be <= the control's MaxZoomLevel (" + Inv(maxZoom) + "), got " + Inv(zoom), false);
            }

            JObject o = new JObject();
            o["requested"] = GeoBoxAt(lat, lon, 0.0);
            if (hasZoom) o["requestedZoom"] = Tok(zoom, 3);
            o["before"] = MapCenterBox(mapBase);

            object loc = MakeLocation(lat, lon);
            if (loc == null)
                throw new ApiException(500, "route_error",
                    "MapControl.Location could not be constructed for (" + Inv(lat) + ", " + Inv(lon) + ")", false);

            if (validateOnly)
            {
                o["validated"] = Tok(true);
                o["after"] = JValue.CreateNull();
                o["verified"] = Tok(false);
                o["note"] = Text("validate_only: the map centre and zoom were not written");
                return o;
            }

            bool centerWritten = GameReflect.SetMember(mapBase, "Center", loc);
            bool zoomWritten = true;
            if (hasZoom) zoomWritten = GameReflect.SetMember(mapBase, "ZoomLevel", F(zoom));

            o["after"] = MapCenterBox(mapBase);
            o["centerWritten"] = Tok(centerWritten);
            o["zoomWritten"] = Tok(zoomWritten);

            double afterLat = GameReflect.Num(GameReflect.Member(GameReflect.Member(mapBase, "Center"), "Latitude"), double.NaN);
            double afterLon = GameReflect.Num(GameReflect.Member(GameReflect.Member(mapBase, "Center"), "Longitude"), double.NaN);
            bool ok = centerWritten && !double.IsNaN(afterLat)
                && Math.Abs(afterLat - lat) <= 1e-6 && Math.Abs(afterLon - lon) <= 1e-6;
            if (ok && hasZoom)
            {
                double afterZoom = GameReflect.Num(GameReflect.Member(mapBase, "ZoomLevel"), double.NaN);
                ok = !double.IsNaN(afterZoom) && Math.Abs(afterZoom - zoom) <= 1e-3;
            }
            o["verified"] = Tok(ok);
            if (!ok)
                o["note"] = Text("the control read back a different centre/zoom; MapBase is a dependency-object property, so a binding on it can push the value back");
            return o;
        }

        // ---------------------------------------------------------------- game entry points

        // Singleton<T>.Instance is a public static property on the generic BASE type, so
        // GameReflect.StaticMember (which does not use FlattenHierarchy) cannot see it. Walk
        // the base chain explicitly, then fall back to a Unity object search.
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
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
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

        private static object RenderPosition()
        {
            return Singleton(RenderPositionTypeName);
        }

        private static object CameraManager()
        {
            return Singleton(CameraManagerTypeName);
        }

        private static object CameraBaseOf(object cameraManager)
        {
            return cameraManager == null ? null : GameReflect.Call(cameraManager, "getCurrentCamera");
        }

        private static bool CameraAttached()
        {
            object rp = RenderPosition();
            if (rp == null) return false;
            return GameReflect.Truth(GameReflect.Member(rp, "IsCameraAttachedToTarget"));
        }

        private static object SelectedObject()
        {
            object rp = RenderPosition();
            if (rp == null) return null;
            object sel = GameReflect.Member(rp, "SelectedObject");
            if (sel == null) sel = GameReflect.Call(rp, "getSelectedObject");
            return sel;
        }

        private static JToken SelectedObjectBox()
        {
            object sel = SelectedObject();
            return sel == null ? (JToken)JValue.CreateNull() : UnitBox(sel);
        }

        // GameMain._objects is the same source /units reads, so a plot command and a /units
        // read can never disagree about which objects exist. CoreService.FindByUID is the
        // game's own public scripting lookup and covers the case where GameMain is not the
        // owner (campaign / scripted objects).
        private static object FindUnitById(int unitId)
        {
            object unit = GameReflect.UnitById(unitId);
            if (unit != null) return unit;
            return GameReflect.CallStatic(CoreServiceTypeName, "FindByUID", unitId);
        }

        private static int ModeId(object cameraManager)
        {
            if (cameraManager == null) return -1;
            double d = GameReflect.Num(GameReflect.Member(cameraManager, "CurrentMode"), double.NaN);
            if (double.IsNaN(d)) return -1;
            return (int)Math.Round(d);
        }

        private static string ModeName(int modeId)
        {
            if (modeId == ModeInit) return "Init";
            if (modeId == ModeFollow) return "Follow";
            if (modeId == ModeFlyby) return "Flyby";
            if (modeId == ModeFree) return "Free";
            if (modeId == ModeFollowBehind) return "FollowBehind";
            return null;
        }

        private static double DistanceToPivot(object cameraManager)
        {
            if (cameraManager == null) return double.NaN;
            return GameReflect.Num(GameReflect.Call(cameraManager, "getDistanceToPivot"), double.NaN);
        }

        private static double NumOf(object primary, object secondary, string method)
        {
            object v = primary == null ? null : GameReflect.Call(primary, method);
            if (v == null && secondary != null) v = GameReflect.Call(secondary, method);
            return GameReflect.Num(v, double.NaN);
        }

        private static object CameraUnityPos(object cameraManager, object cameraBase, object renderPosition)
        {
            object v = cameraManager == null ? null : GameReflect.Call(cameraManager, "getCameraUnityPosition");
            if (v == null && cameraBase != null) v = GameReflect.Call(cameraBase, "getCameraUnityPosition");
            if (v == null && renderPosition != null) v = GameReflect.Call(renderPosition, "getUnityPosition");
            return v;
        }

        private static object PivotUnityPos(object renderPosition)
        {
            return renderPosition == null ? null : GameReflect.Call(renderPosition, "getUnityPosition");
        }

        // ---------------------------------------------------------------- json shapes

        private static JObject GeoBoxAt(double lat, double lon, double height)
        {
            JObject j = new JObject();
            j["lat"] = Tok(lat, 7);
            j["lon"] = Tok(lon, 7);
            j["height"] = Tok(height, 2);
            return j;
        }

        private static JObject GeoBoxOf(object geo)
        {
            if (geo == null) return null;
            JObject j = new JObject();
            j["lat"] = Tok(GameReflect.Lat(geo), 7);
            j["lon"] = Tok(GameReflect.Lon(geo), 7);
            j["height"] = Tok(GeoHeight(geo), 2);
            return j;
        }

        private static JObject AngleBox(double yaw, double pitch)
        {
            JObject j = new JObject();
            j["yaw"] = Tok(yaw, 4);
            j["pitch"] = Tok(pitch, 4);
            return j;
        }

        private static JObject ZoomBox(double distanceToPivot, double fov)
        {
            JObject j = new JObject();
            j["distanceToPivot"] = Tok(distanceToPivot, 2);
            j["fov"] = Tok(fov, 3);
            return j;
        }

        private static JObject MapCenterBox(object mapBase)
        {
            if (mapBase == null) return null;
            JObject j = new JObject();
            object center = GameReflect.Member(mapBase, "Center");
            j["centerLat"] = Tok(GameReflect.Num(GameReflect.Member(center, "Latitude"), double.NaN), 7);
            j["centerLon"] = Tok(GameReflect.Num(GameReflect.Member(center, "Longitude"), double.NaN), 7);
            j["zoomLevel"] = Tok(GameReflect.Num(GameReflect.Member(mapBase, "ZoomLevel"), double.NaN), 3);
            return j;
        }

        private static JObject UnitBox(object unit)
        {
            JObject j = new JObject();
            j["id"] = TokId(GameReflect.Num(GameReflect.Member(unit, "UniqueID"), double.NaN));
            // REVIEW (round 2, blocker 2/important 8): this box used to hand out the hull's real
            // name, class and coordinate for whatever id the caller named. A camera command is not a
            // permission to identify a contact - /tracks deliberately reports Unknown for the same
            // hull, so the two routes contradicted each other. Identity and position follow the same
            // policy the rest of the bridge uses.
            SeaPower.ObjectBase ob = unit as SeaPower.ObjectBase;
            bool detailed = true;
            string vis = IntelPolicy.Full;
            if (ob != null)
            {
                try
                {
                    vis = Api.VisibilityOf(ob);
                    detailed = IntelPolicy.AllowsDetail(vis);
                }
                catch (Exception) { detailed = false; vis = IntelPolicy.Redacted; }
            }
            j["intelPolicy"] = vis;
            j["type"] = Text(unit.GetType().FullName);
            j["destroyed"] = Tok(GameReflect.Truth(GameReflect.Member(unit, "IsDestroyed")));
            if (detailed)
            {
                j["name"] = Text(UnitName(unit));
                object geo = GameReflect.Member(unit, "_geoPosition");
                if (geo != null) j["geo"] = GeoBoxOf(geo);
            }
            else
            {
                j["name"] = JValue.CreateNull();
                j["nameWithheld"] = Text("identity and position of a hull your side does not control"
                    + " are withheld; GET /tracks reports what an observer can see");
            }
            return j;
        }

        private static string UnitName(object unit)
        {
            object n = GameReflect.Member(unit, "Name");
            if (n == null) return null;
            string s = n as string;
            if (s != null) return s;
            // ObjectBase.Name is a UniRx ReactiveProperty<string>; the text is on Value.
            object v = GameReflect.Member(n, "Value");
            return v == null ? null : v.ToString();
        }

        private static JObject VecBox(object vector)
        {
            if (vector == null) return null;
            JObject j = new JObject();
            j["x"] = Tok(GameReflect.Num(GameReflect.Member(vector, "x"), double.NaN), 3);
            j["y"] = Tok(GameReflect.Num(GameReflect.Member(vector, "y"), double.NaN), 3);
            j["z"] = Tok(GameReflect.Num(GameReflect.Member(vector, "z"), double.NaN), 3);
            return j;
        }

        private static JArray SideEffects(params string[] lines)
        {
            JArray a = new JArray();
            for (int i = 0; i < lines.Length; i++) a.Add(new JValue(lines[i]));
            return a;
        }

        // GeoPosition stores the height on a private _height field; there is no Height
        // property in 0.8.7, so a missing value comes back as null rather than as 0.
        private static double GeoHeight(object geo)
        {
            if (geo == null) return double.NaN;
            return GameReflect.Num(GameReflect.Member(geo, "_height"), double.NaN);
        }

        // Metres between two GeoPositions, through the map's own spherical distance so the
        // number matches what MapBase/MapUnits compute for the same two points.
        private static double DistanceM(object geoA, object geoB)
        {
            if (geoA == null || geoB == null) return double.NaN;
            object v = GameReflect.CallStatic(LocationTypeName, "GetDistance", geoA, geoB, EarthRadiusM);
            double d = GameReflect.Num(v, double.NaN);
            if (!double.IsNaN(d)) return d;
            object deg = GameReflect.CallStatic(GeoPositionTypeName, "GetDistanceInDeg", geoA, geoB, true);
            double dd = GameReflect.Num(deg, double.NaN);
            if (!double.IsNaN(dd)) return dd * MetersPerDegree;
            return double.NaN;
        }

        // MapBase.Center is typed MapControl.Location, so a GeoPosition cannot be assigned to
        // it. Build the Location through the game's own constructor, preferring the
        // GeoPosition overload because that is the path the control itself uses.
        private static object MakeLocation(double lat, double lon)
        {
            Type t = GameReflect.FindType(LocationTypeName);
            if (t == null) return null;
            object geo = GameReflect.Geo(lat, lon, 0.0);
            if (geo != null)
            {
                try
                {
                    ConstructorInfo c = t.GetConstructor(new Type[] { geo.GetType() });
                    if (c != null) return c.Invoke(new object[] { geo });
                }
                catch (Exception) { }
            }
            try
            {
                ConstructorInfo c2 = t.GetConstructor(new Type[] { typeof(double), typeof(double) });
                if (c2 != null) return c2.Invoke(new object[] { lat, lon });
            }
            catch (Exception) { }
            return null;
        }

        // ---------------------------------------------------------------- json value helpers

        // NaN/Infinity never reach the wire: Json.NET would write them as bare tokens that
        // the client's parser rejects, so an unknown number becomes null instead.
        private static JToken Tok(double v, int digits)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return JValue.CreateNull();
            return new JValue(Math.Round(v, digits));
        }

        private static JToken Tok(bool v)
        {
            return new JValue(v);
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

        private static float F(double v)
        {
            return (float)v;
        }

        private static string Fmt(double v)
        {
            if (double.IsNaN(v)) return "unknown";
            return Math.Round(v, 3).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string Inv(double v)
        {
            return v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        // Shortest signed difference in degrees, so 359 vs 1 reads as 2 and not as 358.
        private static double AngleDelta(double a, double b)
        {
            if (double.IsNaN(a) || double.IsNaN(b)) return double.NaN;
            double d = a - b;
            while (d > 180.0) d -= 360.0;
            while (d <= -180.0) d += 360.0;
            return d;
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
                        "op '" + op + "' does not accept field '" + p.Name + "'; allowed: " + string.Join(", ", allowed),
                        false);
            }
        }

        private static bool Has(JObject req, string key)
        {
            return req != null && req[key] != null;
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

        // Integers accept 12 and 12.0 but not 12.5: a fractional id or step count is a caller
        // bug that would otherwise be rounded into a different command.
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

        private static bool ReqBool(JObject req, string key, bool dflt)
        {
            JToken t = Present(req, key);
            if (t == null) return dflt;
            if (t.Type != JTokenType.Boolean)
                throw new ApiException(400, "bad_type",
                    "'" + key + "' must be true or false, got " + t.Type.ToString(), false);
            return (bool)t;
        }

        private static void RequireRange(double v, string key, double min, double max)
        {
            if (double.IsNaN(v) || double.IsInfinity(v))
                throw new ApiException(400, "bad_number", "'" + key + "' must be a finite number", false);
            if (v < min || v > max)
                throw new ApiException(400, "bad_value",
                    "'" + key + "' must be between " + Inv(min) + " and " + Inv(max) + ", got " + Inv(v), false);
        }
    }
}
