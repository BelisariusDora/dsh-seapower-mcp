// Sonobuoy drop: put an SSQ-* buoy in the water at a point the agent chooses.
//
// Why this file exists: /units/{id}/attack with a sonobuoy ammunition answers executed=true
// and the magazine never moves. The reason is in the IL, not in the ammunition table:
// AttackTask builds its EngageTask from a *unit* target, and the drop the game itself uses
// for sonobuoys is a *position* engage task:
//
//   AircraftStates.MaritimePatrol/LaySonobouys.onEnter   (Seapower-Scripts.dll)
//     IL_0198  newobj EngageTask::.ctor(String, Vector3, ObjectBase, Int32, Int32, Boolean, Boolean)
//              (_sonobouyType, worldPos with y forced to 0, aircraft, shots=1, priority=1000, false, false)
//     IL_01B7  callvirt ObjectBase::AddEngageTask(EngageTask)
//
// and the public helper that lands in exactly the same place:
//
//   ObjectBase.InsertEngageTask(ammoId, targetObject=null, worldVec, 1, 1000, false, false, false)
//     IL_0027  newobj EngageTask::.ctor(String, Vector3, ObjectBase, ...)   // the null-target overload
//     IL_002F  callvirt ObjectBase::AddEngageTask(EngageTask)
//
// ObjectBase.AddEngageTask is literally `_currentEngageTasks.Add(task)` - no ammunition, no
// range, no unit-type check - so nothing here can promise a buoy actually left the aircraft.
// What decides it is the downstream loop (ObjectBase.HandleEngageTasks -> WeaponSelector ->
// AlignUnitToTarget -> WeaponSystemHardpoint.OnUpdate -> ExecuteLaunch -> Bomb.Container_Launch).
// That is why the report below is built out of read-backs: the injected task is read back field
// by field, and the magazine is read back before and after. "verified" never means "the game
// did what I asked" on faith.
//
// Route B (let the ASW doctrine spread the buoys by itself) was investigated and rejected:
// TacticalDoctrine's SonobuoyDeployState options only choose the *release depth* (the consumer
// is Bomb.SetupSonobuoyDepth, which reads ObjectBase.SonoDeployState), and the "lay a field of
// buoys" behaviour lives inside the MaritimePatrol state machine, which needs a
// MissionType.MPA / SearchAndDestroyASW deck task and lays along that aircraft's own track - it
// cannot be pointed at a spot. Evidence and the full argument: research\SONOBUOY_IMPL.md.
//
// Two strategies, because "drop now" and "go there and drop" are different orders:
//   direct  - one EngageTask per buoy, no change to the aircraft's current task list (the
//             game's own MPA lay is built this way). Needs the aircraft to already be in a
//             position the weapon system accepts.
//   order   - ObjectBase.SetAttackAtWaypointTask, which flies to the point, clamps the
//             aircraft's DesiredAltitude into the ammunition's launch band and releases inside
//             a trigger radius. It also replaces the aircraft's current task chain, so it is
//             not the default.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using SeaPower;
using UnityEngine;

namespace SeaPowerAgentBridge
{
    internal static class Sonobuoy
    {
        // The game's own numbers, copied so a bridge drop looks like an AI drop.
        // LaySonobouys builds every EngageTask with shots=1 and priority=1000 (IL_0198-01A8);
        // DropSonobuoyTask.OnExecute uses the same 1/1000 pair. The priority is copied onto
        // WeaponSystem._engageTaskPriority and decides which queued task wins a weapon channel,
        // so dropping it would let an older attack task starve the buoy drop.
        private const int EngagePriority = 1000;

        // A barrier rather than a heap: SSQ-53/62/77 are kilometre-class sensors, so 0.2 nmi
        // (~370 m) between buoys makes a continuous line without spending the magazine on one
        // spot. Fixed on purpose - the request surface stays small, and the docs say so.
        private const double SpacingNm = 0.2;

        private const int MaxCount = 32;
        private const double CoordTolerance = 1e-5;   // ~1.1 m; the game copies the point verbatim
        // The waypoint of an AttackAtWaypoint task is NOT copied verbatim: the path code may
        // snap it. 2e-3 deg is ~220 m, far below anything tactically meaningful and well above
        // what a snap produces, so a snapped waypoint is reported with its offset instead of
        // being called a mismatch (2026-11-05: every strategy:"order" drop was refused).
        private const double WaypointTolerance = 2e-3;

        /// <summary>POST /units/{id}/drop_sonobuoy (the router passes {"unit": id, ...}).</summary>
        internal static JObject Drop(JObject req)
        {
            if (!Core.IsMain)
                throw new ApiException(500, "off_main_thread",
                    "drop_sonobuoy reads and writes live game objects, so it has to run on the main thread",
                    false);

            JObject body = req == null ? new JObject() : req;
            RejectUnknownFields(body, Api.ModuleAllowedFieldsFor("sonobuoy"));

            int unitId = ReqInt(body, "unit", -1);
            if (unitId < 0)
                throw new ApiException(400, "missing_field",
                    "drop_sonobuoy requires 'unit': the UniqueID of the aircraft that should drop (GET /units lists them)",
                    false);

            double lat = ReqDouble(body, "lat", double.NaN);
            double lon = ReqDouble(body, "lon", double.NaN);
            if (double.IsNaN(lat) || double.IsNaN(lon))
                throw new ApiException(400, "missing_field",
                    "drop_sonobuoy requires 'lat' and 'lon': the point to drop on", false);
            if (lat < -90.0 || lat > 90.0)
                throw new ApiException(400, "bad_value",
                    "'lat' must be -90..90, got " + lat.ToString("0.######"), false);
            if (lon < -180.0 || lon > 180.0)
                throw new ApiException(400, "bad_value",
                    "'lon' must be -180..180, got " + lon.ToString("0.######"), false);

            int count = ReqInt(body, "count", 1);
            if (count < 1 || count > MaxCount)
                throw new ApiException(400, "bad_value",
                    "'count' must be 1.." + MaxCount + ", got " + count, false);

            string strategy = ReqStr(body, "strategy", "direct");
            if (strategy != "direct" && strategy != "order")
                throw new ApiException(400, "bad_value",
                    "'strategy' must be \"direct\" (drop from where the aircraft is) or \"order\" (fly to the point, then drop), got '"
                    + strategy + "'", false);

            bool validateOnly = ReqBool(body, "validate_only", false);
            string ammoRequested = ReqStr(body, "ammo", null);
            string depthRequested = ReqStr(body, "depth", null);

            // REVIEW (round 3, residual risk 4): this used to be a private lookup with its own
            // 404 text, so the intelligence question was answered only by the router - one caller
            // away from an oracle. The gate is local now: exists AND on the player's picture, with
            // the same answer and the same wording as every other route.
            ObjectBase u = Api.FindKnownUnit(unitId);

            if (SafeFlag(u, "destroyed"))
                throw new ApiException(409, "unit_destroyed",
                    "unit " + unitId + " is destroyed, so it cannot release anything", false);

            // Aircraft and Helicopter are two separate roots in this game (Api.cs already uses
            // `u is SeaPower.Aircraft` / `u is SeaPower.Helicopter` for the same reason), so the
            // gate has to ask both. Everything else stays out: a ship has no buoy rack and the
            // verification story for one would be guesswork.
            bool isAircraft = u is Aircraft;
            bool isHelicopter = u is Helicopter;
            if (!isAircraft && !isHelicopter)
                throw new ApiException(409, "wrong_unit_type",
                    "unit " + unitId + " is a " + ClassOf(u) + ", not an aircraft or helicopter; sonobuoys are dropped from the air",
                    false);

            JObject ammoFacts = null;
            string ammoId = ResolveAmmo(u, ammoRequested, out ammoFacts);
            if (ammoId == null)
            {
                if (ammoRequested != null)
                    throw new ApiException(404, "ammo_not_found",
                        "unit " + unitId + " does not carry '" + ammoRequested + "' (carried: " + AmmoKeyList(u) + ")",
                        false);
                throw new ApiException(409, "no_sonobuoy",
                    "unit " + unitId + " carries no sonobuoy; look for ammo whose type/subtype is Sonobuoy (usn_ssq-* on the ASW loadouts). Carried: "
                    + AmmoKeyList(u), false);
            }

            bool isSonobuoy = Bool(JsonMember(ammoFacts, "isSonobuoy"), false);
            if (!isSonobuoy)
                throw new ApiException(409, "no_sonobuoy",
                    "'" + ammoId + "' is not a sonobuoy (type=" + Str(JsonMember(ammoFacts, "typeName"))
                    + " subtype=" + Str(JsonMember(ammoFacts, "subTypeName"))
                    + "); refusing to build a drop task around it", false);

            // A position engage task only makes sense for ammunition that does not need a unit
            // target. Asking first turns a silent `SetAttackAtWaypointTask -> null` into a
            // sentence the caller can act on.
            bool needsTarget = false;
            string gateError = null;
            string requiresTarget = ResolveRequiresTarget(u, ammoId, out gateError);
            if (requiresTarget == "true") needsTarget = true;
            if (needsTarget)
                throw new ApiException(409, "ammo_needs_target",
                    "'" + ammoId + "' requires a unit target (DoesEngageRequiresTarget returned true), so it cannot be dropped on a bare point; use /units/{id}/attack with a target instead",
                    false);

            GeoPosition baseGeo = new GeoPosition(lat, lon, 0.0);
            double heading = HeadingOf(u);
            List<GeoPosition> points = PlanPoints(baseGeo, count, heading);

            int ammoBefore = AmmoCount(u, ammoId);
            JObject depthReport = null;
            List<string> warnings = new List<string>();
            JArray pointReports = new JArray();
            int injected = 0;
            bool readBackOk = true;
            string firstFailure = null;
            string worldSource = null;

            if (strategy == "direct")
            {
                // The world frame has to exist before anything is submitted; a null world Vector3
                // would put every buoy at the map origin and look like a successful drop.
                Vector3 probe;
                string probeSource;
                if (!MapUnits.GeoToWorld(lat, lon, 0.0, out probe, out probeSource))
                    throw new ApiException(409, "no_world_frame",
                        "cannot convert a geographic point to world space yet: the game has not published a centre tile (wait for the match to load)",
                        false);
                worldSource = probeSource;
            }

            if (!validateOnly)
            {
                if (depthRequested != null)
                {
                    depthReport = ApplyDepth(u, depthRequested);
                    if (!Bool(JsonMember(depthReport, "verified"), false))
                        warnings.Add("depth was requested but the read-back did not confirm it: "
                                     + Str(JsonMember(depthReport, "note")));
                }

                for (int i = 0; i < points.Count; i++)
                {
                    JObject one = new JObject();
                    one["index"] = i;
                    one["lat"] = Lat(points[i]);
                    one["lon"] = Lon(points[i]);

                    JObject rb = null;
                    string why = null;
                    bool ok = false;
                    if (strategy == "direct")
                    {
                        rb = InjectDirect(u, ammoId, points[i], out why);
                    }
                    else
                    {
                        rb = InjectOrder(u, ammoId, points[i], out why);
                    }

                    if (rb == null)
                    {
                        one["injected"] = false;
                        one["reason"] = why == null ? "the game refused the task" : why;
                        if (firstFailure == null) firstFailure = (string)one["reason"];
                        pointReports.Add(one);
                        readBackOk = false;
                        // One refusal is the signal; hammering the queue with the remaining
                        // points produces the same sentence N times.
                        break;
                    }

                    ok = Bool(JsonMember(rb, "matches"), false);
                    if (!ok && firstFailure == null)
                        firstFailure = "the task the game built does not match the request: " + Str(JsonMember(rb, "mismatch"));
                    if (!ok) readBackOk = false;
                    injected++;
                    one["injected"] = true;
                    one["matches"] = ok;
                    one["taskReadback"] = rb;
                    pointReports.Add(one);
                }
            }
            else
            {
                // validate_only has to be zero-side-effect, so the points are reported as a plan
                // and nothing is read back from a task that was never built.
                for (int i = 0; i < points.Count; i++)
                {
                    JObject one = new JObject();
                    one["index"] = i;
                    one["lat"] = Lat(points[i]);
                    one["lon"] = Lon(points[i]);
                    one["planned"] = true;
                    pointReports.Add(one);
                }
            }

            int ammoAfter = AmmoCount(u, ammoId);
            int dropped = 0;
            if (ammoBefore >= 0 && ammoAfter >= 0 && ammoAfter < ammoBefore) dropped = ammoBefore - ammoAfter;

            bool verified = false;
            string note;
            if (validateOnly)
            {
                note = "validate_only: nothing was submitted to the game, so there is nothing to verify";
            }
            else if (injected < count)
            {
                note = "the game accepted " + injected + " of " + count
                       + " drop task(s); first refusal: " + (firstFailure == null ? "unknown" : firstFailure);
            }
            else if (!readBackOk)
            {
                note = "all " + injected + " task(s) were submitted but the read-back disagrees: "
                       + (firstFailure == null ? "unknown" : firstFailure);
            }
            else if (ammoBefore < 0 || ammoAfter < 0)
            {
                note = "the magazine count for '" + ammoId
                       + "' is unavailable, so the release cannot be confirmed from here";
            }
            else
            {
                verified = true;
                if (dropped > 0)
                    note = "magazine " + ammoBefore + " -> " + ammoAfter + " (" + dropped
                           + " released) and " + injected + " task(s) read back as requested";
                else
                    note = injected + " task(s) are queued with the requested ammo and point (priority "
                           + EngagePriority + "), the magazine still read " + ammoBefore
                           + " in the same frame - a release takes a few seconds, so re-read GET /units/"
                           + unitId + "/ammo";
            }

            if (!validateOnly && !readBackOk)
            {
                // A refusal is not a partial success: report it as a conflict, not as ok:true.
                // The detail of what the game did accept is still in the message because a caller
                // debugging a layout needs to know how far it got.
                throw new ApiException(409, strategy == "order" ? "order_refused" : "drop_refused",
                    "the game did not accept the drop: " + (firstFailure == null ? "no reason reported" : firstFailure)
                    + (injected > 0 ? " (" + injected + " of " + count + " tasks were accepted before the refusal)" : ""),
                    false);
            }

            if (strategy == "direct" && dropped == 0 && !validateOnly)
                warnings.Add("no ammunition has left the aircraft in this frame. That is normal for a queued drop, "
                             + "but if the count is still unchanged after ~10 s the weapon system never found a "
                             + "solution: re-read GET /units/" + unitId + "/weapons for this ammo's launch envelope, or "
                             + "retry with strategy=order so the game flies the aircraft into the release altitude band");

            JObject o = new JObject();
            o["ok"] = true;
            o["action"] = "drop_sonobuoy";
            o["strategy"] = strategy;
            o["unit"] = unitId;
            o["unitName"] = RxStr(u, "DisplayNameFull");
            o["unitClass"] = ClassOf(u);
            o["ammo"] = ammoId;
            o["ammoPicked"] = ammoRequested == null;
            o["count"] = count;
            JObject pos = new JObject();
            pos["lat"] = lat;
            pos["lon"] = lon;
            o["position"] = pos;
            o["points"] = pointReports;
            o["injected"] = injected;
            o["executed"] = !validateOnly;
            o["ammoBefore"] = ammoBefore;
            o["ammoAfter"] = ammoAfter;
            o["dropped"] = dropped;
            o["pending"] = !validateOnly && dropped == 0;
            o["verified"] = verified;
            o["verifyNote"] = note;
            if (validateOnly)
            {
                o["validated"] = true;
                o["wouldDrop"] = ammoId;
            }
            if (depthReport != null) o["depth"] = depthReport;
            if (warnings.Count > 0) o["warnings"] = JArray.FromObject(warnings);
            o["drawingOfTruth"] = DrawingOfTruth(u, ammoId, ammoFacts, strategy, points, ammoBefore, ammoAfter, worldSource);
            Core.Log("sonobuoy: unit=" + unitId + " ammo=" + ammoId + " count=" + count + " strategy=" + strategy
                     + " injected=" + injected + " ammo " + ammoBefore + "->" + ammoAfter + " verified=" + verified);
            return o;
        }

        // ------------------------------------------------------------------ strategies

        /// <summary>
        /// One EngageTask per buoy, with no target object, exactly like the AI's own lay.
        /// Returns the read-back, or null with a reason.
        /// </summary>
        private static JObject InjectDirect(ObjectBase u, string ammoId, GeoPosition geo, out string why)
        {
            why = null;
            Vector3 world;
            string source;
            if (!MapUnits.GeoToWorld(Lat(geo), Lon(geo), 0.0, out world, out source))
            {
                why = "the bridge cannot convert this point to world space (no centre tile published)";
                return null;
            }
            // LaySonobouys zeroes y on purpose (IL_0188 ldc.r4 0 / stfld Vector3::y): a buoy is
            // released over the water surface, and a non-zero aim height only makes the aligner
            // chase an altitude it cannot reach.
            world.y = 0f;

            string err;
            object task = GameReflect.CallWithError(u, "InsertEngageTask", new object[]
            {
                ammoId, null, world, 1, EngagePriority, false, false, false
            }, out err);
            if (task == null)
            {
                why = err == null ? "InsertEngageTask returned null" : ("InsertEngageTask failed: " + err);
                return null;
            }

            string gotAmmo = Str(GameReflect.Member(task, "_ammoId"));
            int gotShots = Int(GameReflect.Member(task, "_shotsToFire"), -1);
            int gotPriority = Int(GameReflect.Member(task, "_priority"), -1);
            bool hasTargetObject = GameReflect.Member(task, "_targetObject") != null;
            object targetGeo = GameReflect.Member(task, "_targetGeoPosition");

            List<string> mismatch = new List<string>();
            if (gotAmmo != ammoId) mismatch.Add("_ammoId was '" + Str(gotAmmo) + "'");
            if (gotShots != 1) mismatch.Add("_shotsToFire was " + gotShots + " (a buoy drop is one per task)");
            if (hasTargetObject) mismatch.Add("_targetObject was set, so this is not a position drop");
            if (!GeoNear(targetGeo, geo, CoordTolerance))
                mismatch.Add("_targetGeoPosition does not match the requested point");

            JObject r = new JObject();
            r["kind"] = "EngageTask";
            r["ammoId"] = gotAmmo;
            r["shotsToFire"] = gotShots;
            r["priority"] = gotPriority;
            r["autoAttack"] = Bool(GameReflect.Member(task, "_isAutoAttack"), false);
            r["targetObjectIsNull"] = !hasTargetObject;
            r["targetGeoPosition"] = GeoJson(targetGeo);
            r["world"] = WorldJson(world);
            r["worldSource"] = source;
            r["matches"] = mismatch.Count == 0;
            r["mismatch"] = mismatch.Count == 0 ? null : String.Join("; ", mismatch.ToArray());
            return r;
        }

        /// <summary>
        /// ObjectBase.SetAttackAtWaypointTask(ammo, target=null, targetGeo, waypointGeo, salvo,
        /// after=null, SalvoType.PerTarget, areaRadius=0, formationAttack=false,
        /// attackOnlyDetected=false) - the game's own "fly to the point and release there" task.
        /// Reached by reflection because the bridge has no compile-time record of its visibility;
        /// the IL (ObjectBase::SetAttackAtWaypointTask, IL_0299-02B8) shows it ends in
        /// _userRoot.AddTask(task), i.e. it does change the aircraft's current task chain.
        /// </summary>
        private static JObject InjectOrder(ObjectBase u, string ammoId, GeoPosition geo, out string why)
        {
            why = null;
            object salvoType = SalvoTypeValue(1);   // SeaPower.EngageTask+SalvoType.PerTarget
            if (salvoType == null)
            {
                why = "cannot resolve the SalvoType enum (SeaPower.EngageTask+SalvoType)";
                return null;
            }

            string err;
            object task = GameReflect.CallWithError(u, "SetAttackAtWaypointTask", new object[]
            {
                ammoId,   // String ammunitionName
                null,     // ObjectBase targetObject -> a point drop, not a unit attack
                geo,      // GeoPosition targetGeoPosition
                geo,      // GeoPosition waypointGeoPosition
                1,        // Int32 salvo: one buoy per task, see GetAvailableAmmoForAttack below
                null,     // VisualActionTask after
                salvoType,// SalvoType
                0f,       // Single areaRadius (0 = no area distribution)
                false,    // Boolean formationAttack
                false     // Boolean attackOnlyDetected
            }, out err);
            if (task == null)
            {
                why = err != null
                    ? ("SetAttackAtWaypointTask failed: " + err)
                    : "SetAttackAtWaypointTask returned null (the game refuses position drops for this ammo: "
                      + "no _userRoot, unknown ammo name, or DoesEngageRequiresTarget said true)";
                return null;
            }

            string gotAmmo = Str(GameReflect.Member(task, "_ammunitionName"));
            int gotSalvo = Int(GameReflect.Member(task, "_salvo"), -1);
            bool hasTargetObject = Bool(GameReflect.Member(task, "_hasTargetObject"), true);
            bool isSonobouyDrop = Bool(GameReflect.Member(task, "_isSonobouyDrop"), false);
            object waypoint = UnboxValue(GameReflect.Member(task, "_waypointGeoPos"));

            List<string> mismatch = new List<string>();
            if (gotAmmo != ammoId) mismatch.Add("_ammunitionName was '" + Str(gotAmmo) + "'");
            if (gotSalvo != 1) mismatch.Add("_salvo was " + gotSalvo);
            if (hasTargetObject) mismatch.Add("_hasTargetObject was true, so the task is aimed at a unit");
            double wpOffset = GeoOffsetDeg(waypoint, geo);
            if (!double.IsNaN(wpOffset) && wpOffset > WaypointTolerance)
                mismatch.Add("_waypointGeoPos is " + wpOffset.ToString("0.00000")
                             + " deg away from the requested point");

            JObject r = new JObject();
            r["kind"] = "AttackAtWaypoint";
            r["ammunitionName"] = gotAmmo;
            r["salvo"] = gotSalvo;
            r["hasTargetObject"] = hasTargetObject;
            r["isSonobouyDrop"] = isSonobouyDrop;
            r["waypointGeoPosition"] = GeoJson(waypoint);
            if (double.IsNaN(wpOffset)) r["waypointReadback"] = "unreadable";
            else r["waypointOffsetDeg"] = wpOffset;
            r["minAltitude"] = GameReflect.Raw(UnboxValue(GameReflect.Member(task, "_minAltitude")));
            r["maxAltitude"] = GameReflect.Raw(UnboxValue(GameReflect.Member(task, "_maxAltitude")));
            r["triggerRange"] = GameReflect.Raw(GameReflect.Member(task, "_triggerRange"));
            r["engageAtWaypointPosition"] = Bool(GameReflect.Member(task, "_engageAtWaypointPosition"), false);
            r["matches"] = mismatch.Count == 0;
            r["mismatch"] = mismatch.Count == 0 ? null : String.Join("; ", mismatch.ToArray());
            return r;
        }

        // ------------------------------------------------------------------ depth

        /// <summary>
        /// The doctrine option the game reads when a buoy is released: Bomb.SetupSonobuoyDepth
        /// asks WeaponBase._launchPlatform for ObjectBase.SonoDeployState and then places the
        /// buoy at / above / below the layer height. This is the only piece of "doctrine" that a
        /// single set can drive, which is why it is a field here instead of its own route.
        /// </summary>
        private static JObject ApplyDepth(ObjectBase u, string depthText)
        {
            int want = ParseDepth(depthText);
            object rp = GameReflect.Member(u, "SonoDeployState");
            if (rp == null)
                throw new ApiException(409, "depth_unavailable",
                    "unit " + u.UniqueID + " has no SonoDeployState, so its release depth cannot be set", false);

            Type et = FindNested("SeaPower.Bomb", "SonobuoyDeployState");
            if (et == null)
                throw new ApiException(409, "depth_unavailable",
                    "cannot resolve SeaPower.Bomb+SonobuoyDeployState in the loaded assemblies", false);

            object before = UnboxValue(rp);
            object ev = Enum.ToObject(et, want);
            if (!GameReflect.SetMember(rp, "Value", ev))
            {
                // The backing field and the property are both called Value-ish; falling back to
                // the field is what SetMember already does, so a false here means neither exists.
                throw new ApiException(409, "depth_unavailable",
                    "the SonoDeployState wrapper on unit " + u.UniqueID + " has no writable Value", false);
            }

            object after = UnboxValue(rp);
            int have = Int(after, -1);
            JObject r = new JObject();
            r["requested"] = depthText;
            r["before"] = DepthName(before);
            r["after"] = DepthName(after);
            r["verified"] = have == want;
            r["note"] = have == want
                ? "release depth is now " + DepthName(after)
                : "wrote " + want + " but the read-back says " + have;
            return r;
        }

        private static int ParseDepth(string text)
        {
            if (text == null) return 0;
            string t = text.Trim().ToLowerInvariant();
            if (t == "intermittent" || t == "0") return 0;
            if (t == "below" || t == "below_layer" || t == "belowlayer" || t == "1") return 1;
            if (t == "above" || t == "above_layer" || t == "abovelayer" || t == "2") return 2;
            throw new ApiException(400, "bad_value",
                "'depth' must be intermittent|below_layer|above_layer (0|1|2), got '" + text + "'", false);
        }

        private static string DepthName(object v)
        {
            if (v == null) return null;
            int n = Int(v, -1);
            if (n == 0) return "Intermittent";
            if (n == 1) return "BelowLayer";
            if (n == 2) return "AboveLayer";
            return v.ToString();
        }

        // ------------------------------------------------------------------ ammunition

        private static string ResolveAmmo(ObjectBase u, string requested, out JObject facts)
        {
            facts = null;
            if (requested != null)
            {
                JObject f = AmmoFacts(u, requested);
                if (!Bool(JsonMember(f, "exists"), false)) return null;
                facts = f;
                return requested;
            }

            // Auto-pick: prefer the game's own "is this a sonobuoy" answer over a name pattern,
            // but keep the usn_ssq* prefix as a tie-breaker so a mixed ASW loadout picks the
            // passive buoy the AI would have picked first.
            List<string> keys = AmmoKeys(u);
            string best = null;
            int bestScore = -1;
            for (int i = 0; i < keys.Count; i++)
            {
                JObject f = AmmoFacts(u, keys[i]);
                if (!Bool(JsonMember(f, "isSonobuoy"), false)) continue;
                int amount = AmmoCount(u, keys[i]);
                if (amount == 0 && best != null) continue;
                int score = 0;
                if (keys[i] != null && keys[i].StartsWith("usn_ssq", StringComparison.OrdinalIgnoreCase)) score += 2;
                if (amount > 0) score += 1;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = keys[i];
                    facts = f;
                }
            }
            return best;
        }

        /// <summary>
        /// Everything the game will tell us about one magazine entry. `isSonobuoy` accepts both
        /// shapes the IL shows: Ammunition/Type.Sonobuoy (11) as the top-level type, and a buoy
        /// carried as a Bomb (8) whose subtype is Sonobuoy - SetAttackAtWaypointTask itself
        /// keys off `_subType == 11` (IL_0254-0260) while TreatAmmoAsSingleAttack keys off
        /// `_type == 8 && _subType == 11`, so both fields matter and both are reported.
        /// </summary>
        private static JObject AmmoFacts(ObjectBase u, string ammoId)
        {
            JObject o = new JObject();
            o["ammo"] = ammoId;
            o["exists"] = false;
            o["isSonobuoy"] = false;
            o["type"] = -1;
            o["subType"] = -1;
            if (ammoId == null) return o;

            Ammunition ammo = null;
            try { ammo = u.getAmmunitionByName(ammoId); }
            catch (Exception) { }
            if (ammo == null) return o;
            o["exists"] = true;
            o["amount"] = AmmoCount(u, ammoId);

            object ap = GameReflect.Member(ammo, "_ap");
            if (ap == null)
            {
                o["apError"] = "the AmmunitionParameters object could not be read";
                return o;
            }

            int type = EnumNum(GameReflect.Member(ap, "_type"));
            int subType = EnumNum(GameReflect.Member(ap, "_subType"));
            o["type"] = type;
            o["subType"] = subType;
            o["typeName"] = EnumName(GameReflect.Member(ap, "_type"));
            o["subTypeName"] = EnumName(GameReflect.Member(ap, "_subType"));
            o["isSonobuoy"] = type == 11 || subType == 11;
            o["carriedAsBomb"] = type == 8 && subType == 11;
            o["displayedName"] = Str(GameReflect.Member(ap, "_displayedName"));
            o["targetRequired"] = GameReflect.Raw(GameReflect.Member(ap, "_targetRequired"));
            o["guidanceType"] = EnumNum(GameReflect.Member(ap, "_guidanceType"));
            o["isActiveSonobuoy"] = GameReflect.Raw(GameReflect.Member(ap, "_isActiveSonobuoy"));
            o["sonarDepth"] = GameReflect.Raw(GameReflect.Member(ap, "_sonarDepth"));
            o["lifeTime"] = GameReflect.Raw(GameReflect.Member(ap, "_lifeTime"));
            o["minLaunchRangeMi"] = GameReflect.Raw(GameReflect.Member(ap, "_minLaunchRangeInMiles"));
            o["maxLaunchRangeMi"] = GameReflect.Raw(GameReflect.Member(ap, "_maxLaunchRangeInMiles"));
            object alt = GameReflect.Member(ap, "_launchAltitudesInUnity");
            if (alt is Vector2)
            {
                JObject a = new JObject();
                a["min"] = ((Vector2)alt).x;
                a["max"] = ((Vector2)alt).y;
                o["launchAltitudes"] = a;
            }

            // TreatAmmoAsSingleAttack (AttackAtWaypoint IL) decides whether one order can carry
            // more than one round. For a buoy carried as a bomb it returns true, which means
            // GetAvailableAmmoForAttack is pinned to 1 and an order drops exactly one buoy.
            bool singleAttack = type == 9 || type == 12 || (type == 8 && subType == 11);
            o["treatAmmoAsSingleAttack"] = singleAttack;
            o["shootersPerTask"] = singleAttack ? 1 : Int(JsonMember(o, "amount"), 1);
            return o;
        }

        private static int AmmoCount(ObjectBase u, string ammoId)
        {
            if (u == null || ammoId == null) return -1;
            try
            {
                UniRx.ReactiveDictionary<string, int> dict = u.AmmunitionAmountDictionary;
                if (dict == null) return -1;
                foreach (KeyValuePair<string, int> kv in dict)
                {
                    if (kv.Key == ammoId) return kv.Value;
                }
                return 0;
            }
            catch (Exception) { return -1; }
        }

        private static List<string> AmmoKeys(ObjectBase u)
        {
            List<string> keys = new List<string>();
            if (u == null) return keys;
            try
            {
                UniRx.ReactiveDictionary<string, int> dict = u.AmmunitionAmountDictionary;
                if (dict == null) return keys;
                foreach (KeyValuePair<string, int> kv in dict) keys.Add(kv.Key);
            }
            catch (Exception) { }
            return keys;
        }

        private static string AmmoKeyList(ObjectBase u)
        {
            List<string> keys = AmmoKeys(u);
            if (keys.Count == 0) return "(none readable)";
            return String.Join(", ", keys.ToArray());
        }

        // ------------------------------------------------------------------ geometry

        private static List<GeoPosition> PlanPoints(GeoPosition baseGeo, int count, double heading)
        {
            List<GeoPosition> pts = new List<GeoPosition>();
            if (count <= 1)
            {
                pts.Add(baseGeo);
                return pts;
            }
            // Centred on the requested point and laid along the aircraft's current heading, so a
            // count>1 request reads as "a short barrier through that point" and the aircraft
            // walks the line instead of re-dropping on one spot.
            double now = heading;
            for (int i = 0; i < count; i++)
            {
                double offset = (i - (count - 1) / 2.0) * SpacingNm;
                double bearing = now;
                if (offset < 0.0)
                {
                    offset = -offset;
                    bearing = now + 180.0;
                    while (bearing >= 360.0) bearing -= 360.0;
                }
                if (offset < 1e-9)
                {
                    pts.Add(baseGeo);
                    continue;
                }
                GeoPosition p = baseGeo;
                try { p = baseGeo.GeoOffsetByDistanceOnHeading((float)offset, (float)bearing); }
                catch (Exception) { p = baseGeo; }
                pts.Add(p);
            }
            return pts;
        }

        private static double HeadingOf(ObjectBase u)
        {
            object h = GameReflect.Member(u, "Heading");
            double d = GameReflect.Num(h, 0.0);
            if (double.IsNaN(d) || double.IsInfinity(d)) return 0.0;
            return d;
        }

        private static double Lat(object geo) { return geo == null ? double.NaN : Convert.ToDouble(GameReflect.Member(geo, "Latitude")); }
        private static double Lon(object geo) { return geo == null ? double.NaN : Convert.ToDouble(GameReflect.Member(geo, "Longitude")); }

        /// <summary>Largest absolute lat/lon difference in degrees, or NaN when the value
        /// could not be read at all (a struct whose lat/lon members are not what we expect).</summary>
        private static double GeoOffsetDeg(object geo, GeoPosition want)
        {
            if (geo == null || want == null) return double.NaN;
            double la = Lat(geo);
            double lo = Lon(geo);
            if (double.IsNaN(la) || double.IsNaN(lo)) return double.NaN;
            return Math.Max(Math.Abs(la - Lat(want)), Math.Abs(lo - Lon(want)));
        }

        private static bool GeoNear(object geo, GeoPosition want, double tol)
        {
            if (geo == null || want == null) return false;
            double la = Lat(geo);
            double lo = Lon(geo);
            if (double.IsNaN(la) || double.IsNaN(lo)) return false;
            return Math.Abs(la - Lat(want)) <= tol && Math.Abs(lo - Lon(want)) <= tol;
        }

        private static JObject GeoJson(object geo)
        {
            JObject g = new JObject();
            if (geo == null) return g;
            g["lat"] = Lat(geo);
            g["lon"] = Lon(geo);
            object h = GameReflect.Member(geo, "Height");
            if (h != null) g["height"] = GameReflect.Raw(h);
            return g;
        }

        private static JObject WorldJson(Vector3 v)
        {
            JObject w = new JObject();
            w["x"] = v.x;
            w["y"] = v.y;
            w["z"] = v.z;
            return w;
        }

        private static GeoPosition GeoOf(ObjectBase u)
        {
            object p = RxValue(u, "Position");
            if (p is GeoPosition) return (GeoPosition)p;
            return GeoPosition.zero;
        }

        // ------------------------------------------------------------------ game plumbing

        private static ObjectBase FindUnit(int id)
        {
            List<ObjectBase> all = Api.AllObjects();
            for (int i = 0; i < all.Count; i++)
            {
                ObjectBase u = all[i];
                if (u != null && u.UniqueID == id) return u;
            }
            return null;
        }

        private static bool SafeFlag(ObjectBase u, string which)
        {
            if (u == null) return false;
            try
            {
                if (which == "destroyed") return u.IsDestroyed;
            }
            catch (Exception) { }
            return false;
        }

        private static string ClassOf(ObjectBase u)
        {
            return u == null ? "null" : u.GetType().Name;
        }

        /// <summary>
        /// ObjectBase.DoesEngageRequiresTarget(String) -> bool, as a string so the caller can
        /// tell "the game says no" from "the question could not be asked".
        /// </summary>
        private static string ResolveRequiresTarget(ObjectBase u, string ammoId, out string error)
        {
            error = null;
            object v = GameReflect.CallWithError(u, "DoesEngageRequiresTarget", new object[] { ammoId }, out error);
            if (v == null) return null;
            try { return ((bool)v) ? "true" : "false"; }
            catch (Exception) { return null; }
        }

        private static Type SalvoTypeType()
        {
            Type t = FindNested("SeaPower.EngageTask", "SalvoType");
            if (t != null) return t;
            return GameReflect.FindType("SeaPower.EngageTask+SalvoType");
        }

        private static object SalvoTypeValue(int ordinal)
        {
            Type t = SalvoTypeType();
            if (t == null) return null;
            try { return Enum.ToObject(t, ordinal); }
            catch (Exception) { return null; }
        }

        private static Type FindNested(string outerName, string nestedName)
        {
            try
            {
                Type outer = GameReflect.FindType(outerName);
                if (outer == null) return null;
                return outer.GetNestedType(nestedName, BindingFlags.Public | BindingFlags.NonPublic);
            }
            catch (Exception) { return null; }
        }

        // ------------------------------------------------------------------ reflection odds

        private static object RxValue(object holder, string name)
        {
            return UnboxValue(GameReflect.Member(holder, name));
        }

        private static string RxStr(object holder, string name)
        {
            return Str(RxValue(holder, name));
        }

        /// <summary>Read [[T]].Value out of a BBParameter / ReactiveProperty wrapper.</summary>
        private static object UnboxValue(object boxed)
        {
            if (boxed == null) return null;
            object v = GameReflect.Member(boxed, "Value");
            if (v != null) return v;
            return GameReflect.Member(boxed, "value");
        }

        private static int EnumNum(object v)
        {
            if (v == null) return -1;
            try { return Convert.ToInt32(v); }
            catch (Exception) { return -1; }
        }

        private static string EnumName(object v)
        {
            if (v == null) return null;
            try { return v.ToString(); }
            catch (Exception) { return null; }
        }

        private static int Int(object v, int fallback)
        {
            if (v == null) return fallback;
            try { return Convert.ToInt32(v); }
            catch (Exception) { return fallback; }
        }

        private static bool Bool(object v, bool fallback)
        {
            if (v == null) return fallback;
            if (v is bool) return (bool)v;
            try { return Convert.ToBoolean(v); }
            catch (Exception) { return fallback; }
        }

        private static string Str(object v)
        {
            if (v == null) return null;
            try { return v.ToString(); }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Read one field out of a JObject as a plain CLR value, so Bool()/Int()/Str() below can
        /// stay dumb. Game objects are read with GameReflect.Member; this is only for JSON.
        /// </summary>
        private static object JsonMember(JObject o, string key)
        {
            if (o == null) return null;
            JToken t = o[key];
            if (t == null) return null;
            if (t.Type == JTokenType.Null) return null;
            JValue v = t as JValue;
            if (v != null) return v.Value;
            return t;
        }

        // ------------------------------------------------------------------ request parsing
        //
        // Same rules as Api's helpers (absent -> default, explicit null -> 400 bad_type, wrong
        // type -> 400), duplicated because they are private over there and a second, sloppier
        // copy of "did the caller mean it" is how a typo becomes a command.

        private static JToken Present(JObject req, string key)
        {
            JToken t = req == null ? null : req[key];
            if (t == null) return null;
            if (t.Type == JTokenType.Null)
                throw new ApiException(400, "bad_type",
                    "'" + key + "' was explicitly null; omit it to take the default", false);
            return t;
        }

        private static int ReqInt(JObject req, string key, int dflt)
        {
            JToken t = Present(req, key);
            if (t == null) return dflt;
            if (t.Type != JTokenType.Integer)
                throw new ApiException(400, "bad_type",
                    "'" + key + "' must be an integer, got " + t.Type.ToString(), false);
            try { return (int)t; }
            catch (Exception)
            {
                throw new ApiException(400, "bad_type", "'" + key + "' is not a valid integer", false);
            }
        }

        private static double ReqDouble(JObject req, string key, double dflt)
        {
            JToken t = Present(req, key);
            if (t == null) return dflt;
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
                throw new ApiException(400, "bad_value",
                    "'" + key + "' must be a finite number", false);
            return v;
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

        private static string ReqStr(JObject req, string key, string dflt)
        {
            JToken t = Present(req, key);
            if (t == null) return dflt;
            if (t.Type != JTokenType.String)
                throw new ApiException(400, "bad_type",
                    "'" + key + "' must be a string, got " + t.Type.ToString(), false);
            return (string)t;
        }

        private static void RejectUnknownFields(JObject req, string[] allowed)
        {
            if (req == null) return;
            if (allowed == null)
                throw new ApiException(400, "unknown_field",
                    "this op has no field list", false);
            foreach (JProperty p in req.Properties())
            {
                bool known = false;
                for (int i = 0; i < allowed.Length; i++)
                {
                    if (allowed[i] == p.Name) { known = true; break; }
                }
                if (!known)
                    throw new ApiException(400, "unknown_field",
                        "drop_sonobuoy does not accept field '" + p.Name + "' (accepted: "
                        + String.Join(", ", allowed) + ")", false);
            }
        }

        // ------------------------------------------------------------------ drawing of truth

        /// <summary>
        /// The raw in-game side of the story, so a caller can tell a bridge mistake from a game
        /// refusal without reading the log: what the magazine holds, what the ammunition really
        /// is, and what the aircraft is currently ordered to do.
        /// </summary>
        private static JObject DrawingOfTruth(ObjectBase u, string ammoId, JObject ammoFacts,
            string strategy, List<GeoPosition> points, int ammoBefore, int ammoAfter, string worldSource)
        {
            JObject t = new JObject();
            t["unit"] = u.UniqueID;
            t["unitName"] = RxStr(u, "DisplayNameFull");
            t["unitClass"] = ClassOf(u);
            t["unitGeo"] = GeoJson(GeoOf(u));
            t["heading"] = HeadingOf(u);
            t["ammo"] = ammoFacts;
            t["ammoBefore"] = ammoBefore;
            t["ammoAfter"] = ammoAfter;
            t["strategy"] = strategy;
            t["plannedPoints"] = points.Count;

            JArray carried = new JArray();
            List<string> keys = AmmoKeys(u);
            for (int i = 0; i < keys.Count; i++)
            {
                JObject c = new JObject();
                c["ammo"] = keys[i];
                c["amount"] = AmmoCount(u, keys[i]);
                carried.Add(c);
            }
            t["magazine"] = carried;

            t["engageTaskCount"] = ListCount(GameReflect.Member(u, "_currentEngageTasks"));
            t["selectedAmmunitionForEngage"] = GameReflect.Raw(GameReflect.Member(u, "_selectedAmmunitionForEngage"));
            t["sonoDeployState"] = DepthName(UnboxValue(GameReflect.Member(u, "SonoDeployState")));
            t["currentOrderText"] = RxStr(u, "CurrentOrderText");
            if (worldSource != null) t["worldSource"] = worldSource;
            return t;
        }

        private static int ListCount(object list)
        {
            if (list == null) return -1;
            try
            {
                IList l = list as IList;
                if (l != null) return l.Count;
            }
            catch (Exception) { }
            return -1;
        }
    }
}
