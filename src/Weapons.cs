// Weapons.cs - three game-side write primitives that /units/{id}/<command> never had:
// attack_waypoint, countermeasures, radars.
//
// Why one file: all three are "order the hull's own systems to do something", all three need the
// same read-back discipline (call, then immediately read the game's own fields and report what
// they actually say), and all three were written against the same IL evidence. Splitting them
// would duplicate the read-back helpers three times and let them drift.
//
// EVIDENCE DISCIPLINE. Nothing here is written from memory of the API; every game-side call below
// carries the ildump line it came from, and the full dumps are quoted in
// research\WEAPONS_IMPL.md. The interesting ones:
//
//   ObjectBase::SetAttackAtWaypointTask(String,ObjectBase,GeoPosition,GeoPosition,Int32,
//       VisualActionTask,SalvoType,Single,Boolean,Boolean)  -> AttackAtWaypoint
//       10 arguments, in that order; returns null (not an exception) on four different refusals:
//       this == null (IL_0009), the ammunition name does not resolve (IL_0016), no target object
//       while DoesEngageRequiresTarget(ammo) says one is required (IL_0057), or _userRoot == null
//       on the AddTask path. Those four are why the failure branch below enumerates causes
//       instead of saying "the game refused".
//
//   ObjectBase::LaunchChaff(Boolean message)      - loops ObjectBaseParameters._chaffWeapons and
//       calls WeaponSystemChaff::launchChaff() on every system that is ready.
//   ObjectBase::LaunchNoisemaker(String ammoId)   - no ammunition check, no weapon-system check:
//       it just builds EngageTask(ammo, worldPos, this, 1, 1000, false, false) and AddEngageTask.
//       The per-frame launcher decides whether a round ever leaves. So the only synchronous
//       proof is the new row in ObjectBase._currentEngageTasks.
//
//   ObjectBase::EnableAirSearchRadars(Boolean setTimer, Boolean formationCheck)
//   ObjectBase::DisableAirSearchRadars(Boolean formationCheck)
//   ... plus the surface/all variants. These flip SensorSystemRadar.IsOn and do NOT write
//       ObjectBase.IsAirSearchRadarsOn; only the UI does that (ObjectSensorViewModel.Toggle*,
//       IL_00A1-00A7) and setEMCON clears it. That is why `radars` reports per-radar IsOn as the
//       truth and treats the flag as a separate, optionally-synced piece of UI state.
//
// NOT RUN-TIME VERIFIED: the game was closed while this file was written. Everything below is
// compiled and every call is read back, but "verified" only ever means "the game's own field
// agreed with the request in the same main-thread tick".
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Newtonsoft.Json.Linq;
using SeaPower;

namespace SeaPowerAgentBridge
{
    internal static class Weapons
    {
        // The game copies the requested point verbatim into the task, so 1e-5 deg (~1.1 m) is a
        // "same point" test, not a fudge for rounding.
        private const double CoordTolerance = 1e-5;

        // Vessel::launchNoisemaker builds its EngageTask with these two numbers (IL_0018/IL_0019).
        // Copied so a bridge launch competes for a weapon channel exactly like the AI's own.
        private const int NoisemakerShots = 1;
        private const int NoisemakerPriority = 1000;

        // RadarParameters.Role ordinals (SeaPower.RadarParameters+Role: Undefined 0, Surface 1,
        // Air 2, SurfaceAndAir 3). ObjectBase.HasRadar/EnableRadars/DisableRadars all switch on
        // these numbers, so the strings below are only a name for the number.
        private const int RoleSurface = 1;
        private const int RoleAir = 2;
        private const int RoleSurfaceAndAir = 3;

        // ====================================================================== attack_waypoint

        /// <summary>
        /// POST /units/{id}/attack_waypoint - ObjectBase.SetAttackAtWaypointTask as a command.
        ///
        /// This is the game's own "fly to this point, then engage from it" task: the same call the
        /// plot UI makes when a player draws a waypoint with an attack order on it, and the same
        /// one Sonobuoy.InjectOrder uses for the buoy-drop case. The difference is that this route
        /// exposes the whole argument list instead of hard-coding the sonobuoy shape, so it can
        /// also attack a resolved *unit* (target_unit) from a waypoint.
        /// </summary>
        internal static JObject AttackWaypoint(JObject req)
        {
            if (!Core.IsMain)
                throw new ApiException(500, "off_main_thread",
                    "attack_waypoint replaces a unit's task chain, so it has to run on the game's main thread",
                    false);

            JObject body = req == null ? new JObject() : req;
            RejectUnknownFields(body, Api.ModuleAllowedFieldsFor("attack_waypoint"), "attack_waypoint");

            int unitId = ReqInt(body, "unit", -1);
            if (unitId < 0)
                throw new ApiException(400, "missing_field",
                    "attack_waypoint requires 'unit': the UniqueID of the unit that should attack (GET /units lists them)",
                    false);

            double lat = ReqDouble(body, "lat", double.NaN);
            double lon = ReqDouble(body, "lon", double.NaN);
            if (double.IsNaN(lat) || double.IsNaN(lon))
                throw new ApiException(400, "missing_field",
                    "attack_waypoint requires 'lat' and 'lon': the position to engage (degrees; the target of the attack)",
                    false);
            CheckRange("lat", lat, -90.0, 90.0);
            CheckRange("lon", lon, -180.0, 180.0);

            // The waypoint the hull flies to defaults to the point it attacks: "go here and engage
            // here" is the common case, and a separate waypoint is the refinement.
            double wpLat = ReqDouble(body, "waypoint_lat", lat);
            double wpLon = ReqDouble(body, "waypoint_lon", lon);
            CheckRange("waypoint_lat", wpLat, -90.0, 90.0);
            CheckRange("waypoint_lon", wpLon, -180.0, 180.0);

            string ammoId = ReqStr(body, "ammo", null);
            if (ammoId == null || ammoId.Length == 0)
                throw new ApiException(400, "missing_field",
                    "attack_waypoint requires 'ammo': the ammunition id from the unit's own inventory"
                    + " (GET /units/{id} lists it). There is no default on purpose - the game's own"
                    + " GetDefaultAmmunitionForEngage returns null on some hulls, and guessing which"
                    + " weapon a captain meant is not the bridge's call", false);

            int targetUnitId = ReqInt(body, "target_unit", -1);
            if (targetUnitId == unitId)
                throw new ApiException(400, "bad_value",
                    "'target_unit' equals 'unit'; a hull cannot engage itself", false);

            int salvo = ReqInt(body, "salvo", 1);
            if (salvo < 1 || salvo > 1000)
                throw new ApiException(400, "bad_value",
                    "'salvo' must be 1..1000 rounds, got " + salvo, false);

            string salvoText = ReqStr(body, "salvo_type", "per_launcher");
            int salvoOrdinal = SalvoOrdinal(salvoText);

            // Nautical miles, because that is the game's own vocabulary for this value: the waypoint
            // context menu offers -1 (off), 0.5, 1, 2, 5, 10, 15 ... and calls
            // AttackAtWaypoint.UpdateAreaRadius(radiusNmi * 27.5595f) with them
            // (WaypointViewModel.GetAreaRadiusCMItem IL_000E-0015). 27.5595 is nmi->Unity world units
            // (1852 m / 67.20007 m per world unit), and _areaRadius is compared straight against a
            // Vector3 magnitude in AttackAtWaypoint.AttackCalculations IL_0174-017F. So the field is
            // in world units while the menu is in miles, and the conversion belongs here, not in the
            // caller. -1 means "no area distribution" and is passed through untouched - that is
            // exactly what the weapon-entry attack-at-waypoint item sends for a plain waypoint attack
            // (WeaponEntry/<>c__DisplayClass87_1::<.ctor>b__2 IL_003F ldc.r4 -1).
            double areaRadiusNmi = ReqDouble(body, "area_radius", -1.0);
            // REVIEW (round 2, important): `0.0 < 0.0` is false, so a zero radius passed this gate,
            // NmiToUnity(0) returned 0 through its own `unity > 0` guard, and the call queued a task
            // with a zero trigger radius that can never fire while still reporting verified:true.
            if (areaRadiusNmi != -1.0 && areaRadiusNmi <= 0.0)
                throw new ApiException(400, "bad_value",
                    "'area_radius' is in nautical miles and must be -1 (off, the default) or > 0, got "
                    + areaRadiusNmi.ToString("0.###"), false);
            double areaRadiusUnity = areaRadiusNmi == -1.0 ? -1.0 : NmiToUnity(areaRadiusNmi);

            bool formationAttack = ReqBool(body, "formation_attack", false);
            bool attackOnlyDetected = ReqBool(body, "attack_only_detected", false);
            bool validateOnly = ReqBool(body, "validate_only", false);

            ObjectBase u = RequireUnit(unitId);
            if (Api.SafeFlag(u, "destroyed"))
                throw new ApiException(409, "unit_destroyed",
                    "unit " + unitId + " is destroyed, so it cannot take an attack task", false);
            string refusal = UnitTypeGate.Refuse("attack", u);
            if (refusal != null)
                throw new ApiException(409, "wrong_unit_type", refusal, false);

            // Read-only proofs that the ammunition exists and whether it needs a target. Both are
            // cheap, both turn a null return into a sentence the caller can act on.
            Ammunition am = null;
            try { am = u.getAmmunitionByName(ammoId); }
            catch (Exception) { am = null; }
            if (am == null)
                throw new ApiException(404, "ammo_not_found",
                    "unit " + unitId + " does not carry ammunition '" + ammoId + "' (ObjectBase.getAmmunitionByName returned null)",
                    false);
            int ammoAmount = AmmoAmount(u, ammoId);

            string askErr;
            object requiresTargetRaw = GameReflect.CallWithError(u, "DoesEngageRequiresTarget", new object[] { ammoId }, out askErr);
            JToken requiresTarget = requiresTargetRaw == null
                ? JValue.CreateNull()
                : (JToken)new JValue(Convert.ToBoolean(requiresTargetRaw));

            ObjectBase target = null;
            if (targetUnitId >= 0)
            {
                target = RequireUnit(targetUnitId);
                if (Api.SafeFlag(target, "destroyed"))
                    throw new ApiException(409, "unit_destroyed",
                        "target unit " + targetUnitId + " is already destroyed; there is nothing to engage",
                        false);
            }
            else if (requiresTargetRaw != null && Convert.ToBoolean(requiresTargetRaw))
            {
                // IL_004F-0057: the game itself returns null in exactly this case, so refusing here
                // with the reason beats handing the caller a bare "task_refused" later.
                throw new ApiException(400, "ammo_requires_target",
                    "ammunition '" + ammoId + "' is only launched at a resolved target"
                    + " (ObjectBase.DoesEngageRequiresTarget returned true), so 'target_unit' is required;"
                    + " without it the game returns null instead of creating the task. This is a property of the"
                    + " ammunition - guns and unguided artillery need something to aim at, while missiles and bombs"
                    + " that fly to a point do not, and GET /units/{id}/weapons marks those with fireAtPoint true", false);
            }

            object salvoTypeEnum = SalvoTypeValue(salvoOrdinal);
            if (salvoTypeEnum == null)
                throw new ApiException(500, "enum_missing",
                    "SeaPower.EngageTask+SalvoType is not in the loaded assemblies, so the salvo type cannot be passed",
                    false);

            JObject plan = new JObject();
            plan["unit"] = unitId;
            plan["ammo"] = ammoId;
            plan["ammoAvailable"] = ammoAmount;
            plan["ammoRequiresTarget"] = requiresTarget;
            plan["targetUnit"] = targetUnitId < 0 ? JValue.CreateNull() : (JToken)new JValue(targetUnitId);
            plan["target"] = GeoJsonOf(lat, lon);
            plan["waypoint"] = GeoJsonOf(wpLat, wpLon);
            plan["salvo"] = salvo;
            plan["salvoType"] = SalvoName(salvoOrdinal);
            plan["areaRadiusNmi"] = areaRadiusNmi;
            plan["areaRadiusUnity"] = areaRadiusUnity;
            plan["formationAttack"] = formationAttack;
            plan["attackOnlyDetected"] = attackOnlyDetected;

            if (validateOnly)
            {
                JObject v = new JObject();
                v["validated"] = true;
                v["executed"] = false;
                v["plan"] = plan;
                v["before"] = TaskChainJson(u);
                v["note"] = "validate_only: nothing was written; the real call adds an AttackAtWaypoint"
                    + " to this unit's task chain (ObjectBase._userRoot.AddTask) and replaces the current order";
                return v;
            }

            object targetGeo = GameReflect.Geo(lat, lon, 0.0);
            object waypointGeo = GameReflect.Geo(wpLat, wpLon, 0.0);
            if (targetGeo == null || waypointGeo == null)
                throw new ApiException(500, "geo_unavailable",
                    "SeaPower.GeoPosition did not accept (lat, lon, height), so the task cannot be built",
                    false);

            JObject before = TaskChainJson(u);

            // Argument order is the signature, not a guess: ammunition name, target object, target
            // point, waypoint, salvo, after (null = the game's 1-arg AddTask path, i.e. make this the
            // current task), salvo type, area radius, formation attack, attack only detected.
            //
            // The game's own attack-at-waypoint menu item calls the same method with
            // (ap._ammunitionFileName, null, task.WaypointPosition, task.WaypointPosition, 1, task,
            // SalvoType 0, -1f, true, false) - see WeaponEntry/<>c__DisplayClass87_1::<.ctor>b__2
            // IL_0024-0046. The two deliberate differences: `after` is the task the player had selected
            // there (the bridge has no way to name a VisualActionTask, so null keeps this simple and
            // additive), and formationAttack defaults to false here (an agent ordering one hull should
            // not silently drag its formation along).
            string invokeErr;
            object task = GameReflect.CallWithError(u, "SetAttackAtWaypointTask", new object[]
            {
                ammoId,
                target,
                targetGeo,
                waypointGeo,
                salvo,
                null,
                salvoTypeEnum,
                (float)areaRadiusUnity,
                formationAttack,
                attackOnlyDetected
            }, out invokeErr);

            if (task == null)
            {
                if (invokeErr != null)
                    throw new ApiException(500, "game_threw",
                        "ObjectBase.SetAttackAtWaypointTask threw: " + invokeErr, false);
                // Known null causes, all four visible in the IL. Enumerated because the caller can
                // fix the first three and cannot fix the fourth.
                throw new ApiException(409, "task_refused",
                    "ObjectBase.SetAttackAtWaypointTask returned null instead of a task. The game returns"
                    + " null when: the ammunition name does not resolve (IL_0016), the ammunition needs a"
                    + " target but targetObject is null (IL_0057), the object has no _userRoot task graph,"
                    + " or the object itself is gone (IL_0009). Request was ammo='" + ammoId + "', target="
                    + (target == null ? "null" : targetUnitId.ToString()) + ".", false);
            }

            JObject after = TaskChainJson(u);
            JObject taskJson = AttackTaskJson(task);

            List<string> mismatch = new List<string>();
            if (Str(GameReflect.Member(task, "_ammunitionName")) != ammoId)
                mismatch.Add("_ammunitionName was '" + Str(GameReflect.Member(task, "_ammunitionName")) + "'");
            if (Int(GameReflect.Member(task, "_salvo"), -1) != salvo)
                mismatch.Add("_salvo was " + Int(GameReflect.Member(task, "_salvo"), -1));
            string gotSalvoType = EnumName(GameReflect.Member(task, "_salvoType"));
            if (gotSalvoType == null || !gotSalvoType.Equals(SalvoName(salvoOrdinal), StringComparison.OrdinalIgnoreCase))
                mismatch.Add("_salvoType was '" + gotSalvoType + "'");
            bool gotHasTarget = Bool(GameReflect.Member(task, "_hasTargetObject"), false);
            if (gotHasTarget != (target != null))
                mismatch.Add("_hasTargetObject was " + (gotHasTarget ? "true" : "false"));
            if (!GeoNear(GeoField(task, "_targetGeoPosition"), lat, lon))
                mismatch.Add("_targetGeoPosition does not match the requested point");
            if (!GeoNear(GeoField(task, "_waypointGeoPos"), wpLat, wpLon))
                mismatch.Add("_waypointGeoPos does not match the requested waypoint");
            double gotArea = GameReflect.Num(GameReflect.Member(task, "_areaRadius"), double.NaN);
            if (double.IsNaN(gotArea) || Math.Abs(gotArea - areaRadiusUnity) > 1e-3)
                mismatch.Add("_areaRadius was " + NumText(gotArea) + " world units (asked "
                    + NumText(areaRadiusUnity) + ", i.e. " + areaRadiusNmi.ToString("0.###") + " nmi)");
            if (Bool(GameReflect.Member(task, "_formationAttack"), false) != formationAttack)
                mismatch.Add("_formationAttack did not take the requested value");
            if (Bool(GameReflect.Member(task, "_attackOnlyDetected"), false) != attackOnlyDetected)
                mismatch.Add("_attackOnlyDetected did not take the requested value");

            JObject r = new JObject();
            r["unit"] = unitId;
            r["unitName"] = RxStr(u, "DisplayNameFull");
            r["unitClass"] = u.GetType().Name;
            r["plan"] = plan;
            r["executed"] = true;
            r["before"] = before;
            r["after"] = after;
            r["task"] = taskJson;
            // REVIEW (round 2, hint): the key said "grew" while the test was `after >= before`, which
            // is also true when the game pruned a finished task and added this one in the same tick.
            // Two keys, each saying exactly what it tests.
            int chainBeforeNow = Int(before["engageTaskCount"], -1);
            int chainAfterNow = Int(after["engageTaskCount"], -1);
            bool chainReadable = ListCount(GameReflect.Member(u, "_currentEngageTasks")) >= 0
                && chainBeforeNow >= 0 && chainAfterNow >= 0;
            r["taskChainGrew"] = chainReadable ? (JToken)new JValue(chainAfterNow > chainBeforeNow) : JValue.CreateNull();
            r["taskChainNotShrunk"] = chainReadable ? (JToken)new JValue(chainAfterNow >= chainBeforeNow) : JValue.CreateNull();
            // D2: this task carries its own waypoint, so the hull flies there and engages. The
            // receipt is the only place a caller learns that waypointClear does not remove it.
            r["cleanupHint"] = "this attack-at-waypoint carries its own waypoint: the hull will fly to"
                + " 'waypoint' and engage there, and waypointClear does not remove it. Clear queued"
                + " attack tasks with POST /units/" + unitId + "/ceasefire"
                + " {\"clear_attack_tasks\":true} (clear_attack_tasks defaults to true).";
            r["matches"] = mismatch.Count == 0;
            r["mismatch"] = mismatch.Count == 0 ? null : String.Join("; ", mismatch.ToArray());
            // Live measurement on a LandUnit, a 091G and a 1134A: the task object is
            // created but both geometry fields read as null. That is either a real hole
            // in the task or the bridge failing to read the field's type, and the
            // caller cannot tell which from 'null'. Publish the declared type and the
            // runtime type that was seen so the next run can decide.
            if (mismatch.Count != 0)
            {
                JObject probe = new JObject();
                string[] geoFields = new string[] { "_targetGeoPosition", "_waypointGeoPos" };
                for (int gi = 0; gi < geoFields.Length; gi++)
                {
                    JObject one = new JObject();
                    System.Reflection.FieldInfo fi = task.GetType().GetField(geoFields[gi],
                        System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic);
                    one["fieldFound"] = fi != null;
                    one["declaredType"] = fi == null ? null : (JToken)new JValue(fi.FieldType.FullName);
                    object raw = fi == null ? null : fi.GetValue(task);
                    one["rawIsNull"] = raw == null;
                    one["rawRuntimeType"] = raw == null ? null : (JToken)new JValue(raw.GetType().FullName);
                    one["rawText"] = raw == null ? null : (JToken)new JValue(raw.ToString());
                    probe[geoFields[gi]] = one;
                }
                r["taskFieldProbe"] = probe;
            }
            r["verified"] = mismatch.Count == 0;
            if (mismatch.Count != 0)
                r["verifyNote"] = "the task object exists but the game's fields disagree with the request;"
                    + " read the plan and the task block side by side";
            return r;
        }

        // ====================================================================== countermeasures

        /// <summary>
        /// POST /units/{id}/countermeasures - chaff and acoustic decoys.
        ///
        /// Two different game mechanisms hide behind one word, so the request names which one:
        ///   chaff       ObjectBase.LaunchChaff(Boolean message) - one round from every ready
        ///               WeaponSystemChaff. Synchronously provable: launchChaff() decrements
        ///               _loadedAmmunitionCount and stamps _launchTime before it returns.
        ///   noisemaker  ObjectBase.LaunchNoisemaker(String ammoId) - queues an EngageTask and
        ///               returns. Nothing moves synchronously, so the read-back is the new row in
        ///               _currentEngageTasks, not a round count.
        /// </summary>
        internal static JObject Countermeasures(JObject req)
        {
            if (!Core.IsMain)
                throw new ApiException(500, "off_main_thread",
                    "countermeasures launches from live weapon systems, so it has to run on the game's main thread",
                    false);

            JObject body = req == null ? new JObject() : req;
            RejectUnknownFields(body, Api.ModuleAllowedFieldsFor("countermeasures"), "countermeasures");

            int unitId = ReqInt(body, "unit", -1);
            if (unitId < 0)
                throw new ApiException(400, "missing_field",
                    "countermeasures requires 'unit': the UniqueID of the platform (GET /units lists them)",
                    false);

            string kind = ReqStr(body, "kind", "both");
            kind = kind.Trim().ToLowerInvariant();
            if (kind != "chaff" && kind != "noisemaker" && kind != "both")
                throw new ApiException(400, "bad_value",
                    "'kind' must be \"chaff\", \"noisemaker\" or \"both\", got '" + kind + "'", false);

            bool message = ReqBool(body, "message", true);
            bool validateOnly = ReqBool(body, "validate_only", false);
            string ammoRequested = ReqStr(body, "ammo", null);

            ObjectBase u = RequireUnit(unitId);
            if (Api.SafeFlag(u, "destroyed"))
                throw new ApiException(409, "unit_destroyed",
                    "unit " + unitId + " is destroyed, so it cannot launch countermeasures", false);
            // "fire" is the closest existing op in the type gate: any commandable platform. Whether
            // the hull actually carries chaff is equipment, and answered below.
            string refusal = UnitTypeGate.Refuse("fire", u);
            if (refusal != null)
                throw new ApiException(409, "wrong_unit_type", refusal, false);

            object obp = GameReflect.Member(u, "_obp");
            if (obp == null)
                throw new ApiException(409, "wrong_unit_type",
                    "unit " + unitId + " has no ObjectBaseParameters (_obp is null), so it has no weapon or"
                    + " sensor fit at all and cannot launch anything", false);

            JArray chaffBefore = ChaffRows(u);
            bool wantChaff = kind == "chaff" || kind == "both";
            bool wantNoisemaker = kind == "noisemaker" || kind == "both";

            JObject noisemakerPlan = null;
            string noisemakerWhy = null;
            if (wantNoisemaker)
                noisemakerPlan = ResolveNoisemaker(u, ammoRequested, out noisemakerWhy);

            // REVIEW (round 2, blocker): ResolveNoisemaker always returns a plan object, so
            // `plan == null` never fired and these two refusals were unreachable. The hull having
            // nothing to launch is `ammo` being null, and the plan still carries the candidates -
            // which is the whole reason it is an object and not a bare string.
            bool haveNoisemaker = noisemakerPlan != null
                && noisemakerPlan["ammo"] != null
                && noisemakerPlan["ammo"].Type != JTokenType.Null;

            if (wantChaff && chaffBefore.Count == 0 && !wantNoisemaker)
                throw new ApiException(409, "wrong_unit_type",
                    "unit " + unitId + " carries no chaff system (ObjectBaseParameters._chaffWeapons is empty)",
                    false);
            if (kind == "both" && chaffBefore.Count == 0 && !haveNoisemaker)
                throw new ApiException(409, "wrong_unit_type",
                    "unit " + unitId + " carries neither chaff (_chaffWeapons is empty) nor a usable"
                    + " noisemaker: " + (noisemakerWhy == null ? "no decoy ammunition" : noisemakerWhy), false);
            if (kind == "noisemaker" && !haveNoisemaker)
                throw new ApiException(409, "wrong_unit_type",
                    "unit " + unitId + " has no launchable noisemaker: "
                    + (noisemakerWhy == null ? "no decoy ammunition found in its inventory" : noisemakerWhy),
                    false);

            if (validateOnly)
            {
                JObject v = new JObject();
                v["validated"] = true;
                v["executed"] = false;
                v["unit"] = unitId;
                v["kind"] = kind;
                v["chaffSystems"] = chaffBefore;
                v["chaffReady"] = ReadyCount(chaffBefore);
                v["noisemaker"] = noisemakerPlan;
                v["noisemakerWhy"] = noisemakerWhy;
                v["message"] = message;
                v["before"] = CmBeforeJson(u);
                v["note"] = "validate_only: nothing was launched; the chaff block is the current state of every"
                    + " WeaponSystemChaff on this hull";
                return v;
            }

            JObject result = new JObject();
            result["unit"] = unitId;
            result["unitName"] = RxStr(u, "DisplayNameFull");
            result["unitClass"] = u.GetType().Name;
            result["kind"] = kind;
            result["executed"] = true;
            result["before"] = CmBeforeJson(u);

            bool anyFired = false;

            if (wantChaff && chaffBefore.Count > 0)
            {
                JObject chaff = new JObject();
                chaff["systemCount"] = chaffBefore.Count;
                chaff["readyBefore"] = ReadyCount(chaffBefore);

                // LaunchChaff(Boolean message) - message only decides whether the game plays the
                // "ChaffOut" voice line and raises the alert; it never changes what leaves the hull.
                string launchErr;
                object ret = GameReflect.CallWithError(u, "LaunchChaff", new object[] { message }, out launchErr);
                if (launchErr != null)
                    throw new ApiException(500, "game_threw",
                        "ObjectBase.LaunchChaff threw: " + launchErr, false);
                chaff["returned"] = ret == null ? JValue.CreateNull() : (JToken)new JValue(ret.ToString());

                JArray chaffAfter = ChaffRows(u);
                JArray evidence = new JArray();
                int fired = 0;
                for (int i = 0; i < chaffAfter.Count; i++)
                {
                    JObject a = chaffAfter[i] as JObject;
                    JObject b = i < chaffBefore.Count ? chaffBefore[i] as JObject : null;
                    JObject row = new JObject();
                    row["index"] = i;
                    row["ammo"] = a == null ? JValue.CreateNull() : a["ammo"];
                    row["readyBefore"] = b == null ? JValue.CreateNull() : b["ready"];
                    row["readyAfter"] = a == null ? JValue.CreateNull() : a["ready"];
                    row["loadedBefore"] = b == null ? JValue.CreateNull() : b["loaded"];
                    row["loadedAfter"] = a == null ? JValue.CreateNull() : a["loaded"];
                    row["magazineBefore"] = b == null ? JValue.CreateNull() : b["magazine"];
                    row["magazineAfter"] = a == null ? JValue.CreateNull() : a["magazine"];
                    row["reloadInProgressBefore"] = b == null ? JValue.CreateNull() : b["reloadInProgress"];
                    row["reloadInProgressAfter"] = a == null ? JValue.CreateNull() : a["reloadInProgress"];
                    row["launchTimeBefore"] = b == null ? JValue.CreateNull() : b["launchTime"];
                    row["launchTimeAfter"] = a == null ? JValue.CreateNull() : a["launchTime"];

                    bool launched = false;
                    if (a != null && b != null)
                    {
                        int bl = Int(b["loaded"], -1);
                        int al = Int(a["loaded"], -1);
                        double bt = GameReflect.Num(b["launchTime"], double.NaN);
                        double at = GameReflect.Num(a["launchTime"], double.NaN);
                        if (bl >= 0 && al >= 0 && al < bl) launched = true;
                        if (!double.IsNaN(bt) && !double.IsNaN(at) && Math.Abs(at - bt) > 1e-9) launched = true;
                        if (!Bool(b["reloadInProgress"], false) && Bool(a["reloadInProgress"], false)) launched = true;
                    }
                    row["launched"] = launched;
                    if (launched) { fired++; anyFired = true; }
                    evidence.Add(row);
                }

                chaff["readyAfter"] = ReadyCount(chaffAfter);
                chaff["systems"] = evidence;
                chaff["launchedCount"] = fired;
                chaff["verified"] = fired > 0;
                if (fired == 0)
                    chaff["verifyNote"] = chaffBefore.Count == 0
                        ? "this hull has no chaff system"
                        : "LaunchChaff returned without changing any chaff system: every system was"
                          + " empty, reloading, or already mid-reload (WeaponSystemChaff.readyToLaunch requires"
                          + " !_isEmpty && !_isReloading && !_isReloadInProgress)";
                else
                    chaff["verifyNote"] = "launchChaff() decrements the loaded count and stamps _launchTime before"
                        + " returning, so a changed value is synchronous proof that a cloud was created";
                result["chaff"] = chaff;
            }
            else
            {
                JObject chaff = new JObject();
                chaff["skipped"] = true;
                chaff["reason"] = wantChaff
                    ? "no chaff system on this hull (ObjectBaseParameters._chaffWeapons is empty)"
                    : "'kind' did not ask for chaff";
                result["chaff"] = chaff;
            }

            if (wantNoisemaker)
            {
                JObject nm = new JObject();
                // REVIEW (round 2, blocker): the plan is always an object, so this branch used to
                // fall through with an empty ammo name and call LaunchNoisemaker("") - a call the
                // game does not reject (no ammunition check in that method), which is why the
                // caller saw 200 + verified:false instead of the refusal the gates promised.
                if (!haveNoisemaker)
                {
                    nm["skipped"] = true;
                    nm["reason"] = noisemakerWhy == null ? "no launchable noisemaker" : noisemakerWhy;
                    nm["candidates"] = noisemakerPlan == null ? null : noisemakerPlan["candidates"];
                }
                else
                {
                    string nmAmmo = Str(noisemakerPlan["ammo"]);
                    nm["ammo"] = nmAmmo;
                    nm["candidates"] = noisemakerPlan["candidates"];
                    int chainBefore = ListCount(GameReflect.Member(u, "_currentEngageTasks"));

                    // LaunchNoisemaker(String ammoForEngageName): no ammunition check, no weapon
                    // system check, no return value - it appends an EngageTask and returns.
                    string nmErr;
                    GameReflect.CallWithError(u, "LaunchNoisemaker", new object[] { nmAmmo }, out nmErr);
                    if (nmErr != null)
                        throw new ApiException(500, "game_threw",
                            "ObjectBase.LaunchNoisemaker threw: " + nmErr, false);

                    int chainAfter = ListCount(GameReflect.Member(u, "_currentEngageTasks"));
                    nm["engageTaskCountBefore"] = chainBefore;
                    nm["engageTaskCountAfter"] = chainAfter;

                    // AddEngageTask is _currentEngageTasks.Add(task), so the new row is at the end.
                    // Looking for the ammunition id rather than trusting the index: the game is free
                    // to prune finished tasks in the same tick.
                    bool queued = false;
                    JObject queuedRow = null;
                    IList tasks = GameReflect.Member(u, "_currentEngageTasks") as IList;
                    if (tasks != null)
                    {
                        for (int i = tasks.Count - 1; i >= 0; i--)
                        {
                            object t = tasks[i];
                            if (Str(GameReflect.Member(t, "_ammoId")) != nmAmmo) continue;
                            if (chainBefore >= 0 && i < chainBefore) continue;
                            queued = true;
                            queuedRow = EngageTaskJson(t);
                            break;
                        }
                    }
                    nm["queued"] = queued;
                    nm["queuedTask"] = queuedRow;
                    nm["verified"] = queued;
                    if (queued)
                        nm["verifyNote"] = "the EngageTask is queued; whether a round leaves is decided by"
                            + " ObjectBase.HandleEngageTasks -> WeaponSelector in a later tick, which this"
                            + " route cannot observe";
                    else
                        nm["verifyNote"] = "no new EngageTask with this ammunition id is in the task list;"
                            + " the call had no effect on this hull";
                    anyFired = anyFired || queued;
                }
                result["noisemaker"] = nm;
            }
            else
            {
                JObject nm = new JObject();
                nm["skipped"] = true;
                nm["reason"] = "'kind' did not ask for a noisemaker";
                result["noisemaker"] = nm;
            }

            result["verified"] = anyFired;
            result["after"] = CmBeforeJson(u);
            return result;
        }

        // ====================================================================== radars

        /// <summary>
        /// POST /units/{id}/radars - turn the air-search / surface-search radars on or off.
        ///
        /// Same thing the sensor mini-panel does per role (ObjectSensorViewModel.ToggleAirSearchRadars
        /// / ToggleSurfaceSearchRadars), except that this route goes through ObjectBase's own bulk
        /// helpers so the formation branch and the forced-EMCON log come along.
        ///
        /// Two read-backs, and they are not the same thing:
        ///   per-radar SensorSystemRadar.IsOn  - the hardware. This is the truth.
        ///   ObjectBase.IsAirSearchRadarsOn    - UI state. EnableAirSearchRadars never writes it;
        ///                                       only the UI toggle and setEMCON do. sync_flag
        ///                                       keeps it from lying after this command.
        /// </summary>
        internal static JObject Radars(JObject req)
        {
            if (!Core.IsMain)
                throw new ApiException(500, "off_main_thread",
                    "radars reads and writes live sensor state, so it has to run on the game's main thread",
                    false);

            JObject body = req == null ? new JObject() : req;
            RejectUnknownFields(body, Api.ModuleAllowedFieldsFor("radars"), "radars");

            int unitId = ReqInt(body, "unit", -1);
            if (unitId < 0)
                throw new ApiException(400, "missing_field",
                    "radars requires 'unit': the UniqueID of the platform (GET /units lists them)", false);

            string roleText = ReqStr(body, "role", null);
            if (roleText == null)
                throw new ApiException(400, "missing_field",
                    "radars requires 'role': \"air\", \"surface\" or \"all\" - the whole point of the op is"
                    + " that EMCON is per role", false);
            roleText = roleText.Trim().ToLowerInvariant();
            int roleOrdinal;
            if (roleText == "air") roleOrdinal = RoleAir;
            else if (roleText == "surface") roleOrdinal = RoleSurface;
            else if (roleText == "all") roleOrdinal = RoleSurfaceAndAir;
            else
                throw new ApiException(400, "bad_value",
                    "'role' must be \"air\", \"surface\" or \"all\", got '" + roleText + "'", false);

            string stateText = ReqStr(body, "state", null);
            if (stateText == null)
                throw new ApiException(400, "missing_field",
                    "radars requires 'state': \"on\", \"off\" or \"toggle\"", false);
            stateText = stateText.Trim().ToLowerInvariant();
            if (stateText != "on" && stateText != "off" && stateText != "toggle")
                throw new ApiException(400, "bad_value",
                    "'state' must be \"on\", \"off\" or \"toggle\", got '" + stateText + "'", false);

            bool formation = ReqBool(body, "formation", false);
            bool syncFlag = ReqBool(body, "sync_flag", true);
            bool validateOnly = ReqBool(body, "validate_only", false);

            ObjectBase u = RequireUnit(unitId);
            if (Api.SafeFlag(u, "destroyed"))
                throw new ApiException(409, "unit_destroyed",
                    "unit " + unitId + " is destroyed, so its radars cannot be switched", false);
            string refusal = UnitTypeGate.Refuse("radar", u);
            if (refusal != null)
                throw new ApiException(409, "wrong_unit_type", refusal, false);

            object obp = GameReflect.Member(u, "_obp");
            if (obp == null)
                throw new ApiException(409, "wrong_unit_type",
                    "unit " + unitId + " has no ObjectBaseParameters (_obp is null), so it has no sensors"
                    + " and no radar state to switch", false);

            object roleEnum = RoleValue(roleOrdinal);
            if (roleEnum == null)
                throw new ApiException(500, "enum_missing",
                    "SeaPower.RadarParameters+Role is not in the loaded assemblies, so the role cannot be passed",
                    false);

            // HasRadar(Role) is the game's own "is there anything to switch" question; asking it is
            // cheaper and more honest than inferring from the lists (and it already handles the
            // SurfaceAndAir case as air||surface).
            string hasErr;
            object hasRaw = GameReflect.CallWithError(u, "HasRadar", new object[] { roleEnum }, out hasErr);
            if (hasErr != null)
                throw new ApiException(500, "game_threw",
                    "ObjectBase.HasRadar threw: " + hasErr, false);
            bool hasRadar = hasRaw != null && Convert.ToBoolean(hasRaw);
            if (!hasRadar)
                throw new ApiException(409, "wrong_unit_type",
                    "unit " + unitId + " has no '" + roleText + "' search radar (ObjectBase.HasRadar(Role."
                    + RoleName(roleOrdinal) + ") returned false)", false);

            JArray airBefore = RadarRows(u, "_airSearchRadars");
            JArray surfaceBefore = RadarRows(u, "_surfaceSearchRadars");
            bool airFlagBefore = FlagValue(u, "IsAirSearchRadarsOn");
            bool surfaceFlagBefore = FlagValue(u, "IsSurfaceSearchRadarsOn");
            bool anyOnBefore = AnyOn(airBefore, surfaceBefore, roleOrdinal);
            bool want = stateText == "on" ? true : (stateText == "off" ? false : !anyOnBefore);

            JObject before = new JObject();
            before["air"] = airBefore;
            before["surface"] = surfaceBefore;
            before["airSearchRadarsOnFlag"] = airFlagBefore;
            before["surfaceSearchRadarsOnFlag"] = surfaceFlagBefore;
            before["anyOnInRole"] = anyOnBefore;

            if (validateOnly)
            {
                JObject v = new JObject();
                v["validated"] = true;
                v["executed"] = false;
                v["unit"] = unitId;
                v["role"] = roleText;
                v["state"] = stateText;
                v["resolvedWant"] = want;
                v["formation"] = formation;
                v["syncFlag"] = syncFlag;
                v["before"] = before;
                v["note"] = "validate_only: nothing was switched; the radar blocks are the current IsOn state"
                    + " of every SensorSystemRadar on this hull";
                return v;
            }

            string callName;
            object[] callArgs;
            if (roleOrdinal == RoleAir)
            {
                callName = want ? "EnableAirSearchRadars" : "DisableAirSearchRadars";
                callArgs = want ? new object[] { true, formation } : new object[] { formation };
            }
            else if (roleOrdinal == RoleSurface)
            {
                callName = want ? "EnableSurfaceSearchRadars" : "DisableSurfaceSearchRadars";
                callArgs = want ? new object[] { true, formation } : new object[] { formation };
            }
            else
            {
                callName = want ? "EnableAllRadars" : "DisableAllRadars";
                callArgs = want ? new object[] { true } : new object[] { };
            }

            string invokeErr2;
            GameReflect.CallWithError(u, callName, callArgs, out invokeErr2);
            if (invokeErr2 != null)
                throw new ApiException(500, "game_threw",
                    "ObjectBase." + callName + " threw: " + invokeErr2, false);

            JArray airAfter = RadarRows(u, "_airSearchRadars");
            JArray surfaceAfter = RadarRows(u, "_surfaceSearchRadars");

            // Report what the sensors say, then make the UI-facing flag agree with that observed
            // state. The UI toggle writes `want`; observed state is the same thing whenever the
            // radars obeyed, and is the honest value when SensorSystem.Enable bailed out (it returns
            // early on _state == 9, Inoperable, or !_allowTurnOn).
            bool anyOnAfter = AnyOn(airAfter, surfaceAfter, roleOrdinal);
            bool allOnAfter = AllOn(airAfter, surfaceAfter, roleOrdinal);
            bool flagSynced = false;
            if (syncFlag)
            {
                if (roleOrdinal == RoleAir)
                    flagSynced = SetFlag(u, "IsAirSearchRadarsOn", anyOnAfter);
                else if (roleOrdinal == RoleSurface)
                    flagSynced = SetFlag(u, "IsSurfaceSearchRadarsOn", anyOnAfter);
                else
                {
                    bool a = SetFlag(u, "IsAirSearchRadarsOn", AnyOn(airAfter, surfaceAfter, RoleAir));
                    bool s = SetFlag(u, "IsSurfaceSearchRadarsOn", AnyOn(airAfter, surfaceAfter, RoleSurface));
                    flagSynced = a && s;
                }
            }

            JObject after = new JObject();
            after["air"] = airAfter;
            after["surface"] = surfaceAfter;
            after["airSearchRadarsOnFlag"] = FlagValue(u, "IsAirSearchRadarsOn");
            after["surfaceSearchRadarsOnFlag"] = FlagValue(u, "IsSurfaceSearchRadarsOn");
            after["anyOnInRole"] = anyOnAfter;
            after["allOnInRole"] = allOnAfter;

            bool verified = want ? allOnAfter : !anyOnAfter;
            // Live measurement: the sensor state machine does not commit on the
            // frame the command lands in. Ordering the air-search radars off returned
            // verified=false, and a dry read two seconds later showed all four off.
            // Reporting that as a failure trains the caller to ignore the flag, so
            // say "pending" and name the read that settles it.
            JObject r = new JObject();
            r["unit"] = unitId;
            r["unitName"] = RxStr(u, "DisplayNameFull");
            r["unitClass"] = u.GetType().Name;
            r["role"] = roleText;
            r["state"] = stateText;
            r["resolvedWant"] = want;
            r["formation"] = formation;
            r["syncFlag"] = syncFlag;
            r["flagSynced"] = flagSynced;
            r["executed"] = true;
            r["call"] = callName;
            r["before"] = before;
            r["after"] = after;
            r["changedCount"] = ChangedCount(airBefore, airAfter) + ChangedCount(surfaceBefore, surfaceAfter);
            // Live measurement: the sensor state machine does not commit on the frame
            // the command lands in. Ordering the air-search radars off returned
            // verified=false, and a dry read two seconds later showed all four off.
            // Reporting that as a failure trains the caller to ignore the flag.
            // An unverified radar write is never "failed": it is either not committed yet
            // (measured: ordering the air radars off returned verified=false and a dry read
            // two seconds later showed all four off) or the sensor refused it (Inoperable,
            // !_allowTurnOn, state 9). Only a re-read can tell which, so say pending.
            bool pending = !verified;
            r["pending"] = pending;
            if (pending)
            {
                r["settleHint"] = "re-read with {\"role\": \"" + roleText + "\", \"state\": \""
                    + (want ? "on" : "off") + "\", \"validate_only\": true} after about a second and"
                    + " compare 'before'";
                r["pendingNote"] = "the state machine did not agree on this frame: either it commits a"
                    + " frame later, or the sensor refused (Inoperable, _allowTurnOn false, or state 9)";
            }
            r["verified"] = verified;
            if (!verified)
                r["verifyNote"] = want
                    ? "at least one radar in role '" + roleText + "' is still off. SensorSystem.Enable returns"
                      + " early when the sensor is Inoperable, when _allowTurnOn is false, or when its state"
                      + " machine is in state 9 (mid-transition); read the per-radar rows above"
                    : "at least one radar in role '" + roleText + "' is still on - see the per-radar rows";
            return r;
        }

        // ====================================================================== chaff read-back

        /// <summary>
        /// One row per WeaponSystemChaff on the hull. Loaded/magazine counts come from
        /// WeaponSystem.getLoadedAmmoCount(String) / getMagazineAmmoCount(String); the first one is
        /// the counter WeaponSystemChaff.launchChaff decrements (IL_001A), so it is the synchronous
        /// evidence that a cloud was created.
        /// </summary>
        private static JArray ChaffRows(ObjectBase u)
        {
            JArray rows = new JArray();
            object obp = GameReflect.Member(u, "_obp");
            object list = obp == null ? null : GameReflect.Member(obp, "_chaffWeapons");
            IList l = list as IList;
            if (l == null) return rows;

            for (int i = 0; i < l.Count; i++)
            {
                object w = l[i];
                if (w == null) continue;
                JObject row = new JObject();
                row["index"] = i;

                string ammoName = null;
                object ammo = GameReflect.Member(w, "_ammoInUse");
                if (ammo != null)
                {
                    object ap = GameReflect.Member(ammo, "_ap");
                    ammoName = Str(GameReflect.Member(ap, "_ammunitionFileName"));
                }
                row["ammo"] = ammoName == null ? JValue.CreateNull() : (JToken)new JValue(ammoName);

                string ignored;
                object loaded = ammoName == null ? null
                    : GameReflect.CallWithError(w, "getLoadedAmmoCount", new object[] { ammoName }, out ignored);
                object magazine = ammoName == null ? null
                    : GameReflect.CallWithError(w, "getMagazineAmmoCount", new object[] { ammoName }, out ignored);
                row["loaded"] = loaded == null ? (JToken)new JValue(-1) : (JToken)new JValue(Int(loaded, -1));
                row["magazine"] = magazine == null ? (JToken)new JValue(-1) : (JToken)new JValue(Int(magazine, -1));

                object ready = GameReflect.CallWithError(w, "readyToLaunch", new object[] { }, out ignored);
                row["ready"] = ready == null ? (JToken)JValue.CreateNull() : (JToken)new JValue(Convert.ToBoolean(ready));
                row["isEmpty"] = Bool(GameReflect.Member(w, "_isEmpty"), false);
                row["isReloading"] = Bool(GameReflect.Member(w, "_isReloading"), false);
                row["reloadInProgress"] = Bool(GameReflect.Member(w, "_isReloadInProgress"), false);
                row["launchTime"] = NumToken(GameReflect.Member(w, "_launchTime"));
                rows.Add(row);
            }
            return rows;
        }

        private static int ReadyCount(JArray rows)
        {
            int n = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                JObject r = rows[i] as JObject;
                if (r != null && Bool(r["ready"], false)) n++;
            }
            return n;
        }

        /// <summary>
        /// Pick a noisemaker ammunition the way Vessel::launchNoisemaker does it: decoy ammunition
        /// that the hull actually holds, with at least one weapon system that is not inoperable,
        /// reloading, or already busy with another engage task.
        /// </summary>
        private static JObject ResolveNoisemaker(ObjectBase u, string requested, out string why)
        {
            why = null;
            JArray candidates = new JArray();
            string chosen = null;

            List<string> ids = new List<string>();
            try
            {
                UniRx.ReactiveDictionary<string, int> inventory = u.AmmunitionAmountDictionary;
                if (inventory != null)
                {
                    foreach (KeyValuePair<string, int> kv in inventory)
                    {
                        if (kv.Key == null) continue;
                        if (kv.Value <= 0) continue;
                        ids.Add(kv.Key);
                    }
                }
            }
            catch (Exception e) { why = "the ammunition inventory could not be read: " + e.Message; }

            for (int i = 0; i < ids.Count; i++)
            {
                string id = ids[i];
                if (requested != null && requested.Length > 0 && !string.Equals(id, requested, StringComparison.Ordinal)) continue;

                Ammunition am = null;
                try { am = u.getAmmunitionByName(id); }
                catch (Exception) { am = null; }
                if (am == null) continue;
                object ap = GameReflect.Member(am, "_ap");
                if (ap == null) continue;
                if (!IsNoisemakerAmmo(ap)) continue;

                JObject cand = new JObject();
                cand["ammo"] = id;
                cand["amount"] = AmmoAmount(u, id);
                string systemState = UsableWeaponSystemState(u, id);
                cand["weaponSystems"] = systemState;
                bool usable = systemState == "ready";
                cand["usable"] = usable;
                candidates.Add(cand);
                if (usable && chosen == null) chosen = id;
            }

            if (chosen == null)
            {
                if (requested != null && requested.Length > 0)
                    why = "ammunition '" + requested + "' is not a usable noisemaker on this hull (it must be"
                        + " decoy-class ammunition the hull carries, with a weapon system that is not inoperable,"
                        + " reloading or already executing another engage task)";
                else if (candidates.Count == 0)
                    why = "no decoy-class ammunition in the inventory";
                else
                    why = "the hull has decoy ammunition but every weapon system for it is busy, reloading or inoperable";
            }

            JObject plan = new JObject();
            plan["ammo"] = chosen == null ? JValue.CreateNull() : (JToken)new JValue(chosen);
            plan["candidates"] = candidates;
            plan["shotsToFire"] = NoisemakerShots;
            plan["priority"] = NoisemakerPriority;
            return plan;
        }

        /// <summary>
        /// Ammunition._ap._type == Ammunition.Type.Noisemaker. Compared by ordinal because the enum
        /// lives in the game assembly and a name lookup would add a second failure mode; the ordinal
        /// is taken from the enum itself, not from a literal.
        /// </summary>
        private static bool IsNoisemakerAmmo(object ap)
        {
            object typeRaw = GameReflect.Member(ap, "_type");
            if (typeRaw == null) return false;
            int have;
            try { have = Convert.ToInt32(typeRaw); }
            catch (Exception) { return false; }

            Type enumType = NestedType("SeaPower.Ammunition", "Type");
            int want = 6;   // Ammunition.Type.Noisemaker, the value in the shipped enum
            if (enumType != null)
            {
                try { want = Convert.ToInt32(Enum.Parse(enumType, "Noisemaker", true)); }
                catch (Exception) { }
            }
            return have == want;
        }

        /// <summary>
        /// "ready" or a comma-separated list of why not, for every weapon system mapped to this
        /// ammunition. Same three skip rules Vessel::launchNoisemaker applies per frame.
        /// </summary>
        private static string UsableWeaponSystemState(ObjectBase u, string ammoId)
        {
            object dictRaw = GameReflect.Member(u, "_ammunitionToWeaponSystemsDictionary");
            IDictionary dict = dictRaw as IDictionary;
            if (dict == null) return "no weapon-system map on this hull";
            object entry;
            try { entry = dict[ammoId]; }
            catch (Exception) { entry = null; }
            IList systems = entry as IList;
            if (systems == null || systems.Count == 0) return "no weapon system is loaded with this ammunition";

            List<string> reasons = new List<string>();
            int usable = 0;
            for (int i = 0; i < systems.Count; i++)
            {
                object w = systems[i];
                if (w == null) continue;
                if (Bool(UnboxValue(GameReflect.Member(w, "Inoperable")), false)) { reasons.Add("inoperable"); continue; }
                if (Bool(GameReflect.Member(w, "_isReloading"), false)) { reasons.Add("reloading"); continue; }
                if (Bool(GameReflect.Member(w, "_executingEngageTask"), false)) { reasons.Add("busy"); continue; }
                usable++;
            }
            if (usable > 0) return "ready";
            return reasons.Count == 0 ? "no usable weapon system" : String.Join(",", reasons.ToArray());
        }

        // ====================================================================== radar read-back

        /// <summary>
        /// One row per SensorSystemRadar in one of the two role lists. IsOn and Inoperable are
        /// ReactiveProperty&lt;bool&gt; on BaseSystem, so they need the .Value unwrap.
        /// </summary>
        private static JArray RadarRows(ObjectBase u, string listName)
        {
            JArray rows = new JArray();
            object obp = GameReflect.Member(u, "_obp");
            object list = obp == null ? null : GameReflect.Member(obp, listName);
            IList l = list as IList;
            if (l == null) return rows;

            for (int i = 0; i < l.Count; i++)
            {
                object r = l[i];
                if (r == null) continue;
                JObject row = new JObject();
                row["index"] = i;
                row["type"] = Str(GameReflect.Member(r, "_type"));
                // BaseSystem has no "Name": the label and the save id are _systemName / DisplayName.
                row["name"] = Str(GameReflect.Member(r, "_systemName"));
                row["displayName"] = Api.SafeStr(UnboxValue(GameReflect.Member(r, "DisplayName")));
                row["isOn"] = Bool(UnboxValue(GameReflect.Member(r, "IsOn")), false);
                row["inoperable"] = Bool(UnboxValue(GameReflect.Member(r, "Inoperable")), false);
                row["state"] = Int(GameReflect.Member(r, "_state"), -1);
                row["allowTurnOn"] = Bool(GameReflect.Member(r, "_allowTurnOn"), true);
                rows.Add(row);
            }
            return rows;
        }

        private static bool AnyOn(JArray air, JArray surface, int roleOrdinal)
        {
            if (roleOrdinal == RoleAir) return ArrayAnyOn(air);
            if (roleOrdinal == RoleSurface) return ArrayAnyOn(surface);
            return ArrayAnyOn(air) || ArrayAnyOn(surface);
        }

        private static bool AllOn(JArray air, JArray surface, int roleOrdinal)
        {
            if (roleOrdinal == RoleAir) return ArrayAllOn(air);
            if (roleOrdinal == RoleSurface) return ArrayAllOn(surface);
            return ArrayAllOn(air) && ArrayAllOn(surface);
        }

        private static bool ArrayAnyOn(JArray rows)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                JObject r = rows[i] as JObject;
                if (r != null && Bool(r["isOn"], false)) return true;
            }
            return false;
        }

        /// <summary>
        /// "All the radars that could be on are on": an Inoperable sensor cannot be switched, so it
        /// is excluded rather than counted as a failure.
        /// </summary>
        private static bool ArrayAllOn(JArray rows)
        {
            int counted = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                JObject r = rows[i] as JObject;
                if (r == null) continue;
                if (Bool(r["inoperable"], false)) continue;
                counted++;
                if (!Bool(r["isOn"], false)) return false;
            }
            return counted > 0 || rows.Count == 0;
        }

        private static int ChangedCount(JArray before, JArray after)
        {
            int n = 0;
            int max = before.Count < after.Count ? before.Count : after.Count;
            for (int i = 0; i < max; i++)
            {
                JObject a = before[i] as JObject;
                JObject b = after[i] as JObject;
                if (a == null || b == null) continue;
                if (Bool(a["isOn"], false) != Bool(b["isOn"], false)) n++;
            }
            return n;
        }

        private static bool FlagValue(ObjectBase u, string prop)
        {
            return Bool(UnboxValue(GameReflect.Member(u, prop)), false);
        }

        /// <summary>
        /// Write the UI-facing ReactiveProperty. ObjectSensorViewModel.Toggle* does exactly this
        /// (IL_00A1-00A7) because ObjectBase.Enable*/Disable* never touch it.
        /// </summary>
        private static bool SetFlag(ObjectBase u, string prop, bool value)
        {
            object rx = GameReflect.Member(u, prop);
            if (rx == null) return false;
            return GameReflect.SetMember(rx, "Value", value);
        }

        private static object RoleValue(int ordinal)
        {
            Type t = NestedType("SeaPower.RadarParameters", "Role");
            if (t == null) t = GameReflect.FindType("SeaPower.RadarParameters+Role");
            if (t == null) return null;
            try { return Enum.ToObject(t, ordinal); }
            catch (Exception) { return null; }
        }

        private static string RoleName(int ordinal)
        {
            if (ordinal == RoleAir) return "Air";
            if (ordinal == RoleSurface) return "Surface";
            if (ordinal == RoleSurfaceAndAir) return "SurfaceAndAir";
            return "Undefined";
        }

        // ====================================================================== task read-back

        private static JObject EngageTaskJson(object task)
        {
            JObject t = new JObject();
            t["ammoId"] = Str(GameReflect.Member(task, "_ammoId"));
            t["shotsToFire"] = Int(GameReflect.Member(task, "_shotsToFire"), -1);
            t["priority"] = Int(GameReflect.Member(task, "_priority"), -1);
            object target = GameReflect.Member(task, "_targetObject");
            t["targetObjectId"] = target == null ? JValue.CreateNull() : (JToken)new JValue(Int(GameReflect.Member(target, "UniqueID"), -1));
            t["targetGeoPosition"] = GeoJson(GeoField(task, "_targetGeoPosition"));
            return t;
        }

        private static JObject AttackTaskJson(object task)
        {
            JObject t = new JObject();
            t["kind"] = "AttackAtWaypoint";
            t["ammunitionName"] = Str(GameReflect.Member(task, "_ammunitionName"));
            t["salvo"] = Int(GameReflect.Member(task, "_salvo"), -1);
            t["salvoType"] = EnumName(GameReflect.Member(task, "_salvoType"));
            t["areaRadius"] = NumToken(GameReflect.Member(task, "_areaRadius"));
            t["formationAttack"] = Bool(GameReflect.Member(task, "_formationAttack"), false);
            t["attackOnlyDetected"] = Bool(GameReflect.Member(task, "_attackOnlyDetected"), false);
            t["hasTargetObject"] = Bool(GameReflect.Member(task, "_hasTargetObject"), false);
            object target = GameReflect.Member(task, "_targetObject");
            t["targetObjectId"] = target == null ? JValue.CreateNull() : (JToken)new JValue(Int(GameReflect.Member(target, "UniqueID"), -1));
            t["targetGeoPosition"] = GeoJson(GeoField(task, "_targetGeoPosition"));
            t["waypointGeoPosition"] = GeoJson(GeoField(task, "_waypointGeoPos"));
            t["triggerRange"] = NumToken(GameReflect.Member(task, "_triggerRange"));
            t["engageAtWaypointPosition"] = Bool(GameReflect.Member(task, "_engageAtWaypointPosition"), false);
            t["minAltitude"] = NumToken(UnboxValue(GameReflect.Member(task, "_minAltitude")));
            t["maxAltitude"] = NumToken(UnboxValue(GameReflect.Member(task, "_maxAltitude")));
            t["targetAltitude"] = NumToken(UnboxValue(GameReflect.Member(task, "_targetAltitude")));
            t["isSonobouyDrop"] = Bool(GameReflect.Member(task, "_isSonobouyDrop"), false);
            t["isCompleted"] = Bool(GameReflect.Member(task, "_isCompleted"), false);
            return t;
        }

        /// <summary>
        /// The part of the task chain that is readable without walking NodeCanvas internals: the
        /// queued engage tasks, and the game's own one-line order text for the unit. Deliberately not
        /// a "before/after of the whole chain" - AddTask replaces the running task, and the node graph
        /// is not a stable read surface.
        /// </summary>
        private static JObject TaskChainJson(ObjectBase u)
        {
            JObject o = new JObject();
            object list = GameReflect.Member(u, "_currentEngageTasks");
            o["engageTaskCount"] = ListCount(list);
            JArray rows = new JArray();
            IList l = list as IList;
            if (l != null)
            {
                for (int i = 0; i < l.Count; i++)
                {
                    object t = l[i];
                    if (t == null) continue;
                    JObject row = new JObject();
                    row["ammoId"] = Str(GameReflect.Member(t, "_ammoId"));
                    row["shotsToFire"] = Int(GameReflect.Member(t, "_shotsToFire"), -1);
                    row["priority"] = Int(GameReflect.Member(t, "_priority"), -1);
                    object target = GameReflect.Member(t, "_targetObject");
                    row["targetObjectId"] = target == null ? JValue.CreateNull() : (JToken)new JValue(Int(GameReflect.Member(target, "UniqueID"), -1));
                    rows.Add(row);
                }
            }
            o["engageTasks"] = rows;
            o["currentOrderText"] = RxStr(u, "CurrentOrderText");
            return o;
        }

        private static JObject CmBeforeJson(ObjectBase u)
        {
            JObject o = new JObject();
            o["engageTaskCount"] = ListCount(GameReflect.Member(u, "_currentEngageTasks"));
            o["currentOrderText"] = RxStr(u, "CurrentOrderText");
            JArray air = RadarRows(u, "_airSearchRadars");
            JArray surface = RadarRows(u, "_surfaceSearchRadars");
            o["airSearchRadarsOn"] = ArrayAnyOn(air);
            o["surfaceSearchRadarsOn"] = ArrayAnyOn(surface);
            return o;
        }

        // ====================================================================== small helpers
        //
        // Request parsing is a deliberate copy of Sonobuoy.cs's helpers: the originals are private,
        // and a second, sloppier copy of "did the caller mean it" is how a typo becomes a command.
        // The rules are the same - absent takes the default, explicit null is 400 bad_type, a wrong
        // JSON type is 400 bad_type.

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
                throw new ApiException(400, "bad_value", "'" + key + "' must be a finite number", false);
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

        private static void RejectUnknownFields(JObject req, string[] allowed, string op)
        {
            if (req == null) return;
            if (allowed == null)
                throw new ApiException(400, "unknown_field",
                    op + " has no field list", false);
            foreach (JProperty p in req.Properties())
            {
                bool known = false;
                for (int i = 0; i < allowed.Length; i++)
                {
                    if (allowed[i] == p.Name) { known = true; break; }
                }
                if (!known)
                    throw new ApiException(400, "unknown_field",
                        op + " does not accept field '" + p.Name + "' (accepted: "
                        + String.Join(", ", allowed) + ")", false);
            }
        }

        private static void CheckRange(string key, double v, double min, double max)
        {
            if (v < min || v > max)
                throw new ApiException(400, "bad_value",
                    "'" + key + "' must be " + min.ToString("0.######") + ".." + max.ToString("0.######")
                    + ", got " + v.ToString("0.######"), false);
        }

        private static int SalvoOrdinal(string text)
        {
            string t = text == null ? "" : text.Trim().ToLowerInvariant();
            if (t == "per_launcher") return 0;   // SalvoType.PerLauncher
            if (t == "per_target") return 1;     // SalvoType.PerTarget
            if (t == "in_total") return 2;       // SalvoType.InTotal
            throw new ApiException(400, "bad_value",
                "'salvo_type' must be \"per_launcher\" (0, the game enum's default), \"per_target\" (1)"
                + " or \"in_total\" (2), got '" + text + "'", false);
        }

        private static string SalvoName(int ordinal)
        {
            if (ordinal == 0) return "PerLauncher";
            if (ordinal == 1) return "PerTarget";
            if (ordinal == 2) return "InTotal";
            return "Unknown";
        }

        private static object SalvoTypeValue(int ordinal)
        {
            Type t = NestedType("SeaPower.EngageTask", "SalvoType");
            if (t == null) t = GameReflect.FindType("SeaPower.EngageTask+SalvoType");
            if (t == null) return null;
            try { return Enum.ToObject(t, ordinal); }
            catch (Exception) { return null; }
        }

        private static Type NestedType(string outerName, string nestedName)
        {
            try
            {
                Type outer = GameReflect.FindType(outerName);
                if (outer == null) return null;
                return outer.GetNestedType(nestedName, BindingFlags.Public | BindingFlags.NonPublic);
            }
            catch (Exception) { return null; }
        }

        // Nautical miles -> Unity world units, which is what AttackAtWaypoint._areaRadius is in.
        // AttackCalculations compares it straight against a Vector3 magnitude (IL_0174-017F), and the
        // waypoint menu converts nmi with a hardcoded 27.5595f before calling UpdateAreaRadius
        // (WaypointViewModel.GetAreaRadiusCMItem IL_000F). That literal is 1852 m per nmi divided by
        // 67.20007 m per world unit (SeaPower.Length.FromUnity), so ask the game's own converter and
        // keep the arithmetic as the fallback - a patch that rescales the world then cannot silently
        // skew every area radius this bridge sends.
        private static double NmiToUnity(double nmi)
        {
            try
            {
                Type len = GameReflect.FindType("SeaPower.Length");
                if (len != null)
                {
                    MethodInfo from = len.GetMethod("FromNauticalMiles",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null,
                        new Type[] { typeof(float) }, null);
                    PropertyInfo inUnity = len.GetProperty("InUnity",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (from != null && inUnity != null)
                    {
                        object boxed = from.Invoke(null, new object[] { (float)nmi });
                        double unity = GameReflect.Num(inUnity.GetValue(boxed, null), double.NaN);
                        if (!double.IsNaN(unity) && unity > 0.0) return unity;
                    }
                }
            }
            catch (Exception) { }
            return nmi * 27.5595;
        }

        // ------------------------------------------------------------------ units

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

        /// <summary>
        /// 404 for both "no such id" and "not on the player's plot", with the same sentence
        /// Api.GuardedRead uses: a different answer for a hidden object would make the 404 an oracle
        /// that says "this id is real".
        /// </summary>
        private static ObjectBase RequireUnit(int id)
        {
            ObjectBase u = FindUnit(id);
            if (u == null || !IntelScope.PlayerKnows(u))
                throw new ApiException(404, "unit_not_found", "unit not found: " + id, false);
            return u;
        }

        private static int AmmoAmount(ObjectBase u, string ammoId)
        {
            if (ammoId == null) return -1;
            try
            {
                UniRx.ReactiveDictionary<string, int> inventory = u.AmmunitionAmountDictionary;
                if (inventory == null) return -1;
                foreach (KeyValuePair<string, int> kv in inventory)
                {
                    if (kv.Key == ammoId) return kv.Value;
                }
            }
            catch (Exception) { }
            return -1;
        }

        // ------------------------------------------------------------------ reflection plumbing

        private static object RxStrValue(object holder, string name)
        {
            return UnboxValue(GameReflect.Member(holder, name));
        }

        private static string RxStr(ObjectBase u, string name)
        {
            return Str(RxStrValue(u, name));
        }

        /// <summary>Read T.Value out of a BBParameter / ReactiveProperty wrapper.</summary>
        // A GeoPosition field on a task is either the plain struct (SeaPower.GeoPosition)
        // or a NodeCanvas parameter wrapping it (BBParameter<GeoPosition>). Unboxing the
        // plain one returns null - measured live: the task really held the requested point
        // while the check reported a mismatch - so only unwrap a wrapper.
        private static object GeoField(object holder, string name)
        {
            object raw = GameReflect.Member(holder, name);
            if (raw == null) return null;
            string typeName = raw.GetType().Name;
            if (typeName.StartsWith("BBParameter", StringComparison.Ordinal))
            {
                object inner = GameReflect.Member(raw, "value");
                if (inner == null) inner = GameReflect.Member(raw, "Value");
                if (inner != null) return inner;
            }
            return raw;
        }

        private static object UnboxValue(object boxed)
        {
            if (boxed == null) return null;
            object v = GameReflect.Member(boxed, "Value");
            if (v != null) return v;
            return GameReflect.Member(boxed, "value");
        }

        // ------------------------------------------------------------------ value plumbing

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

        private static string EnumName(object v)
        {
            if (v == null) return null;
            try { return v.ToString(); }
            catch (Exception) { return null; }
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

        /// <summary>JSON cannot carry NaN/Infinity, and a missing game field is not a zero. Both
        /// collapse to null so a caller can tell "the field said nothing" from "the field said 0".</summary>
        private static JToken NumToken(object v)
        {
            double d = GameReflect.Num(v, double.NaN);
            if (double.IsNaN(d) || double.IsInfinity(d)) return JValue.CreateNull();
            return new JValue(d);
        }

        private static string NumText(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return "null";
            return d.ToString("0.######");
        }

        // Unity prints a position as "Position{_latitude: 31.4, longitude: 26.5, altitude: 0}".
        // A plain GeoPosition field answers through Latitude/Longitude, but a NodeCanvas
        // BBParameter<GeoPosition> does not expose its value through reflection at all - measured
        // live on build 2615EA7A, where the task held the requested waypoint exactly and GameReflect.Member()
        // still returned null. Reading the object's own text covers both shapes. If the format
        // ever changes this returns false, so the answer degrades to "cannot confirm" and never to
        // a false pass.
        private static bool TryNumberAfter(string text, string label, out double value)
        {
            value = double.NaN;
            if (text == null) return false;
            int at = text.IndexOf(label, StringComparison.Ordinal);
            if (at < 0) return false;
            int i = at + label.Length;
            while (i < text.Length && text[i] == ' ') i++;
            int start = i;
            while (i < text.Length)
            {
                char c = text[i];
                if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') i++;
                else break;
            }
            if (i <= start) return false;
            string slice = text.Substring(start, i - start);
            return double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static bool GeoFromText(object geo, out double lat, out double lon)
        {
            lat = double.NaN;
            lon = double.NaN;
            if (geo == null) return false;
            string text;
            try { text = Convert.ToString(geo, CultureInfo.InvariantCulture); }
            catch (Exception) { return false; }
            if (string.IsNullOrEmpty(text)) return false;
            if (!TryNumberAfter(text, "_latitude:", out lat)) return false;
            return TryNumberAfter(text, "longitude:", out lon);
        }

        private static bool GeoNear(object geo, double lat, double lon)
        {
            if (geo == null) return false;
            double a = GameReflect.Lat(geo);
            double b = GameReflect.Lon(geo);
            if (double.IsNaN(a) || double.IsNaN(b))
            {
                if (!GeoFromText(geo, out a, out b)) return false;
            }
            return Math.Abs(a - lat) <= CoordTolerance && Math.Abs(b - lon) <= CoordTolerance;
        }

        private static JObject GeoJson(object geo)
        {
            if (geo == null) return null;
            JObject o = new JObject();
            object rawLat = GameReflect.Member(geo, "Latitude");
            object rawLon = GameReflect.Member(geo, "Longitude");
            if (rawLat == null && rawLon == null)
            {
                // the same wrapper case as GeoNear: no reflected members, but the object prints
                // the position. Cache the parsed pair so the block shows a real point.
                double parsedLat;
                double parsedLon;
                if (GeoFromText(geo, out parsedLat, out parsedLon))
                {
                    rawLat = parsedLat;
                    rawLon = parsedLon;
                }
            }
            o["lat"] = NumToken(rawLat);
            o["lon"] = NumToken(rawLon);
            object height = GameReflect.Member(geo, "Height");
            if (height == null) height = GameReflect.Member(geo, "Altitude");
            o["height"] = NumToken(height);
            return o;
        }

        private static JObject GeoJsonOf(double lat, double lon)
        {
            JObject o = new JObject();
            o["lat"] = lat;
            o["lon"] = lon;
            return o;
        }
    }
}
