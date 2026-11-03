// LandUnits.cs - LAND-1: read-only support for SeaPower.LandUnit
//
// WHY THIS FILE EXISTS
// GET /units already lists ground units - ObjectBase.isUnit() does isinst Vessel /
// Submarine / Aircraft / Helicopter / LandUnit / Biologic in that order, so a truck or a SAM
// site passes the same filter as a frigate. What the JSON then says about it is wrong in a
// way that is easy to miss: get_IsSurfaceUnit() is `is Vessel || is LandUnit`, so every land
// unit reports surface:true and nothing else, and an agent reading /units has no way to tell
// a coastal battery from a destroyer. This file answers the questions the list cannot:
// is this thing a land unit, what KIND of land unit, and what does the game actually know
// about it that we can read without guessing.
//
// WHAT THIS FILE DELIBERATELY DOES NOT DO
//   * It does not touch a route. Every HTTP path lives in Api.cs, and this batch must not
//     modify that file, so nothing here is reachable over /units/{id}/... yet. That is the
//     honest state and research\LAND_UNITS_IMPL.md says so in the first paragraph.
//   * It writes nothing. _isMobile, _landUnitOverallDamage, _targetPoints and the rest are
//     read through GameReflect only. TargetPoint and Ammunition/LandAttack are write-side
//     surfaces that need a live observation before a command may exist (see the report).
//   * It invents no predicate. The game has NO IsGroundUnit / IsLandUnit / IsGround member -
//     `ildump find 'IsGround'` returns zero hits - so the only available test is the CLR type
//     test, which is exactly what this file uses.
//   * It never fills in a value it did not read. A flag whose backing field is missing or
//     unreadable is omitted from Flags(), not reported as false: "unknown" and "no" are
//     different answers and collapsing them is how a feature starts lying.
//
// EVIDENCE (decompiled IL, Seapower-Scripts.dll; nothing below is guesswork)
//   SeaPower.LandUnit [base=SeaPower.ObjectBase]
//     Single   _landUnitOverallDamage      total damage of the land unit
//     Boolean  _isMobile                   can it move at all
//     Boolean  _isMobileAndCanMove         mobile AND currently allowed to move
//     List     _fires                      fires burning on the unit
//     List     _destroyedSystems           systems already destroyed
//     List     _openMissileContainers      launchers with an open hatch
//     Int32    _currentOpenHatch           which hatch index is open
//     List     _targetPointsDebug          per-instance target point debug list
//     Double   _lockCourseTime             course lock timestamp
//     Boolean  _performAutoFlatten / _maskVegetation / _launchDestructionPhysics /
//              _turretCookoffTriggered     model/config switches
//     prop     LandUnitParameters LandUnitParams
//     prop     Boolean isNearShore         (the only near-shore predicate that exists)
//   SeaPower.LandUnitParameters [base=SeaPower.ObjectBaseParameters]
//     LandUnitSubType _landUnitSubType     AirBase..OilRig, 10 members, numbers 0..9
//     List            _targetPoints        the authoritative target point list
//     Single          _maxForwardVelocityInKnots / _maxBackwardVelocityInKnots
//   SeaPower.LandUnitParameters/TargetPoint (struct): _name, _description, _position,
//     _associatedSystems, _priority (Low/Medium/High), _isCenterPoint, _category
//     (General/Radar/LowSAM/HighSAM/AAA)
//   The game's own reader is SeapowerUI.MapUnitViewModel::GetMapType(ObjectBase): it takes
//     isinst LandUnit -> get_LandUnitParams() -> ldfld _landUnitSubType and switches on it,
//     and for the MobileUnit case it additionally reads LandUnit._isMobile. This file reads
//     the same two hops, so it cannot disagree with the map.
//
// C#5 only: the project builds with /langversion:5. No interpolation, no null-propagation,
// no nameof, no expression-bodied members, no out var, no tuples, no pattern matching.

using System;
using System.Collections;
using Newtonsoft.Json.Linq;
using SeaPower;

namespace SeaPowerAgentBridge
{
    internal static class LandUnits
    {
        // The ten LandUnitSubType members, in metadata declaration order. The numbers were
        // read out of the enum's constant table (Cecil HasConstant), not inferred from the
        // order: AirBase=0 .. OilRig=9.
        private static readonly string[] SubTypeNames = new string[]
        {
            "AirBase", "Installation", "MobileUnit", "AAA", "SAM",
            "MissileSite", "Radar", "Port", "Bridge", "OilRig"
        };

        // Members this file reads, with the type they are declared as and what the value is
        // used for. Table() publishes this as the "verified" half of the ledger.
        private static readonly string[] VerifiedFields = new string[]
        {
            "LandUnit._isMobile (Boolean) -> Flags.mobile, Detail.mobile",
            "LandUnit._isMobileAndCanMove (Boolean) -> Flags.mobileAndCanMove, Detail.mobileAndCanMove",
            "LandUnit.isNearShore (Boolean property) -> Flags.nearShore, Detail.nearShore",
            "LandUnit._performAutoFlatten (Boolean) -> Flags.autoFlatten",
            "LandUnit._maskVegetation (Boolean) -> Flags.maskVegetation",
            "LandUnit._launchDestructionPhysics (Boolean) -> Flags.launchDestructionPhysics",
            "LandUnit._turretCookoffTriggered (Boolean) -> Flags.turretCookoffTriggered",
            "LandUnit._landUnitOverallDamage (Single) -> Detail.damage",
            "ObjectBase._overallDamage (Single) -> Detail.baseDamage (cross-reference)",
            "LandUnit._fires (List) -> Detail.fires (count only)",
            "LandUnit._destroyedSystems (List) -> Detail.destroyedSystems (count only)",
            "LandUnit._openMissileContainers (List) -> Detail.openMissileContainers (count only)",
            "LandUnit._currentOpenHatch (Int32) -> Detail.currentOpenHatch",
            "LandUnit._targetPointsDebug (List) -> Detail.targetPointsDebug (count only)",
            "LandUnitParameters._landUnitSubType (enum) -> SubTypeName, Detail.subType",
            "LandUnitParameters._targetPoints (List<TargetPoint>) -> Detail.targetPointCount / Categories / Priorities / CenterPoints",
            "LandUnitParameters._maxForwardVelocityInKnots (Single) -> Detail.maxForwardKnots",
            "LandUnitParameters._maxBackwardVelocityInKnots (Single) -> Detail.maxBackwardKnots",
            "ObjectBase._obp._weaponSystems / _gunWeapons / _launcherWeapons / _hardpointWeapons / _magazineSystems (List) -> Detail.*Weapons, Detail.magazineSystems (counts only)"
        };

        // Members this file does NOT read, and why. This half is the point of Table(): a field
        // list that only names what worked is indistinguishable from one that guessed.
        private static readonly string[] UnverifiedFields = new string[]
        {
            "_fires: elements are a List<T> whose element type cannot be read from ildump `type` output (it prints List`1 with no arguments), so only the COUNT is exposed. What one entry represents (a fire zone? a system on fire?) is unverified.",
            "_destroyedSystems: same - count only, element type unverified.",
            "_openMissileContainers: same - count only. This is the field an agent would want for 'which launcher is open', and it is exactly the one we cannot name yet.",
            "_landUnitOverallDamage: the field is read, its SCALE is not verified (0..1 fraction vs 0..100 vs hit points). The number is passed through unchanged; do not compare it against ObjectBase._totalDamagePoints without a live measurement.",
            "_isMobileAndCanMove: the member exists and reads as a bool, but which code path toggles it (and how it differs from _isMobile on a stationary-but-mobile unit) is unverified - both flags are reported separately for that reason.",
            "_targetPointsDebug: read for a count only. Whether it is the live list or a debug copy of LandUnitParameters._targetPoints is unverified, so the report treats LandUnitParameters._targetPoints as authoritative and this one as a cross-check.",
            "LandUnitParameters._targetPoints elements: _name / _description / _position / _associatedSystems are present in the struct but NOT exposed. Only _category, _priority and _isCenterPoint are read, because those are the three the task needs and each is a plain value field; copying strings or vectors out of a boxed struct has no consumer yet.",
            "ObjectBase._obp._weaponSystems on a LandUnit: the path is shared with vessels and is read the same way, but whether a land unit actually populates it (an airbase with no mounts may hold an empty or null list) is unverified until a live unit is observed. A null list omits the key instead of reporting 0.",
            "LandUnit._groundOffset / _accelerationValue / _lockCourseTime / _turretBlownOffPosition / _turretBlownOffRotation: present in the class, not exposed. The first two are tuning values with no reader, the rest are dead-state after a cookoff.",
            "LandUnitParameters._horsePower / _mass / _hpToTons / _acceleration / _braking / _turnRate / _turretBlowOffChance / ClassAbbrev: present in the parameters, not exposed; they describe the template, not the unit's state.",
            "CampaignLandUnit/LandUnitType (MechanisedUnits/ArmouredUnits/InfantryUnits) and /DefenceType (Prepared/Hasty/Fortified): the enums exist and their numbers are known (0..2), but no link from a live LandUnit instance to a CampaignLandUnit was verified, so nothing here reads them.",
            "Ammunition/LandAttack (None=0, Installation=1, Mobile=2, ShoreTargetsOnly=3): the enum is known but the ammunition->target-class matching code was not traced. It is a WRITE-side concern and is not exposed."
        };

        // How many times a read helper hit an exception instead of a value. Flags()/Detail()
        // degrade to "key absent", which is right, but a silent degrade is indistinguishable
        // from "the field is genuinely null" - so the count is published in Table().
        private static int _failures;

        internal static int FailureCount
        {
            get { return _failures; }
        }

        // ------------------------------------------------------------------ public API

        /// <summary>
        /// True for SeaPower.LandUnit. This is a compile-time type test, not reflection:
        /// Seapower-Scripts.dll is a build reference (build.ps1 adds it), which is the same
        /// test UnitTypeGate.Classify uses. The game exposes no is-ground predicate of its
        /// own, so there is nothing to call instead.
        /// </summary>
        internal static bool IsLand(ObjectBase u)
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread", "LandUnits must run on the game's main thread", false);
            if (u == null) return false;
            return u is SeaPower.LandUnit;
        }

        /// <summary>
        /// One of the ten LandUnitSubType names, or null when it cannot be read. The value is
        /// name-resolved through Enum.GetName, so a number that is NOT a declared member comes
        /// back as null - this method never invents a name for an out-of-range value.
        /// Read path: LandUnit.LandUnitParams (property) -> LandUnitParameters._landUnitSubType.
        /// Note the parameters object is the class template shared by every unit of that class,
        /// so the subtype is a class property, not per-instance state.
        /// </summary>
        internal static string SubTypeName(ObjectBase u)
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread", "LandUnits must run on the game's main thread", false);
            try
            {
                object pars = Params(u);
                if (pars == null) return null;
                return EnumName(GameReflect.Member(pars, "_landUnitSubType"));
            }
            catch (Exception e)
            {
                _failures++;
                Core.Log("[LandUnits] SubTypeName() failed: " + e.Message);
                return null;
            }
        }

        // ------------------------------------------------------------------------------
        // Minimal land surface. Slice of OPERATION_COVERAGE_REVIEW_CODEX.md §6 and
        // LAND_UNITS_COVERAGE_ISSUE.md §7.5: four read-only entries, no target-point
        // detail, no terrain, no patrol areas. Read() is the payload the bridge puts on
        // the wire; RefuseMoveCommand() is the "do not fake success" gate for the
        // speed / course / move / telegraph commands.
        // ------------------------------------------------------------------------------

        /// <summary>
        /// Subtypes that are fixed installations BY CONSTRUCTION - their prefabs carry no
        /// movement code path at all, whatever _isMobile happens to say. Measured against
        /// the shipped ini census (research/LAND_MIN_IMPL.md): none of these five ever
        /// writes IsMobile=True. Radar / SAM / AAA / MissileSite are deliberately NOT in
        /// this list: the census has IsMobile=True variants of all three of the first
        /// three, so their mobility must come from the field, never from the subtype.
        /// </summary>
        private static readonly string[] FixedInstallationSubTypes =
        {
            "AirBase", "Installation", "Port", "Bridge", "OilRig"
        };

        /// <summary>
        /// Read-only projection of one land unit. Emitted keys (each numeric / boolean key
        /// is followed by a "&lt;key&gt;Field" key naming the game member it came from):
        ///   landUnitType          LandUnitParameters._landUnitSubType, resolved to a name
        ///   isMobile              LandUnit._isMobile
        ///   isMobileAndCanMove    LandUnit._isMobileAndCanMove
        ///   overallDamage         LandUnit._landUnitOverallDamage (integrity loss, not 0..1)
        ///   overallDamagePct      100 * that / ObjectBase._totalDamagePoints
        ///   baseOverallDamage     ObjectBase._overallDamage (the base-class field)
        ///   selectedTargetPoints  COUNT of LandUnitParameters._targetPoints - never detail
        ///   targetPointDebugMarkers  COUNT of LandUnit._targetPointsDebug (List&lt;GameObject&gt;)
        ///   maskVegetation        LandUnit._maskVegetation, only when readable
        ///   isFixedInstallation   refusal criterion 1, on its own
        ///   moveCommandRefused / moveCommandRefuseReason  refusal criterion 2 + verdict
        ///   unreadable[]          members that were missing instead of silently false/0
        ///   notes[]               provenance and the two limits spelled out in words
        /// canMoveNow is NOT emitted: the shipped build has no such member (see notes).
        /// </summary>
        internal static JObject Read(ObjectBase u)
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread", "LandUnits.Read must run on the game's main thread", false);
            JObject o = new JObject();
            JArray notes = new JArray();
            JArray unreadable = new JArray();
            if (u == null)
            {
                o["reason"] = "null_unit";
                o["notes"] = notes;
                return o;
            }
            try
            {
                o["id"] = IdOf(u);
                string nm = NameOf(u);
                if (nm != null) o["name"] = nm;
                string tn = TypeNameOf(u);
                if (tn != null) o["objectType"] = tn;

                if (!IsLand(u))
                {
                    o["reason"] = "not_land_unit";
                    notes.Add("object is not SeaPower.LandUnit; the land projection does not apply");
                    o["notes"] = notes;
                    return o;
                }

                string sub = SubTypeName(u);
                if (sub != null)
                {
                    o["landUnitType"] = sub;
                    o["landUnitTypeField"] = "LandUnit.LandUnitParams._landUnitSubType";
                }
                else
                {
                    unreadable.Add("LandUnitParameters._landUnitSubType");
                }

                object isMobile = GameReflect.Member(u, "_isMobile");
                PutBool(o, "isMobile", isMobile, "LandUnit._isMobile", unreadable);

                object isMobileAndCanMove = GameReflect.Member(u, "_isMobileAndCanMove");
                PutBool(o, "isMobileAndCanMove", isMobileAndCanMove, "LandUnit._isMobileAndCanMove", unreadable);

                object landDamage = GameReflect.Member(u, "_landUnitOverallDamage");
                PutNum(o, "overallDamage", landDamage, "LandUnit._landUnitOverallDamage", unreadable);
                object totalDamagePoints = GameReflect.Member(u, "_totalDamagePoints");
                double totalDp = GameReflect.Num(totalDamagePoints, -1);
                double rawDmg = GameReflect.Num(landDamage, -1);
                if (totalDp > 0 && rawDmg >= 0)
                {
                    o["overallDamagePct"] = Math.Round(100.0 * rawDmg / totalDp, 2);
                    o["overallDamagePctField"] = "100 * LandUnit._landUnitOverallDamage / ObjectBase._totalDamagePoints";
                }
                else
                {
                    unreadable.Add("ObjectBase._totalDamagePoints");
                }
                object baseDamage = GameReflect.Member(u, "_overallDamage");
                PutNum(o, "baseOverallDamage", baseDamage, "ObjectBase._overallDamage", unreadable);

                object pars = Params(u);
                if (pars == null)
                {
                    unreadable.Add("LandUnit.LandUnitParams");
                }
                else
                {
                    object points = GameReflect.Member(pars, "_targetPoints");
                    PutCount(o, "selectedTargetPoints", points, "LandUnitParameters._targetPoints", unreadable);
                }
                object debugMarkers = GameReflect.Member(u, "_targetPointsDebug");
                PutCount(o, "targetPointDebugMarkers", debugMarkers, "LandUnit._targetPointsDebug", unreadable);

                object maskVeg = GameReflect.Member(u, "_maskVegetation");
                PutBool(o, "maskVegetation", maskVeg, "LandUnit._maskVegetation", unreadable);

                // Refusal criterion 1: fixed installation, decided from the subtype alone.
                bool fixedInstallation = IsFixedSubType(sub);
                o["isFixedInstallation"] = fixedInstallation;
                o["isFixedInstallationField"] = "LandUnitParameters._landUnitSubType in {AirBase, Installation, Port, Bridge, OilRig}";

                // Refusal criterion 2 + verdict, read from the same two fields again so the
                // payload and RefuseMoveCommand() can never disagree about the reason.
                string refuse = RefuseMoveReason(u);
                o["moveCommandRefused"] = refuse != null;
                if (refuse != null) o["moveCommandRefuseReason"] = refuse;

                if (unreadable.Count > 0) o["unreadable"] = unreadable;
                AppendReadNotes(notes, sub, fixedInstallation, refuse);
                o["notes"] = notes;
                return o;
            }
            catch (Exception e)
            {
                _failures++;
                Core.Log("[LandUnits] Read() failed: " + e.Message);
                o["error"] = e.Message;
                o["failuresSoFar"] = _failures;
                if (unreadable.Count > 0) o["unreadable"] = unreadable;
                o["notes"] = notes;
                return o;
            }
        }

        /// <summary>
        /// True when a movement-family command (speed / course / move / telegraph) must be
        /// refused for this unit instead of faking success. Two independent criteria, each
        /// also visible separately in Read():
        ///   1. fixed installation - the subtype is AirBase / Installation / Port / Bridge /
        ///      OilRig, i.e. the prefab has no movement path at all;
        ///   2. not movable right now - _isMobile is false (TrueKinematics returns a zero
        ///      envelope) or _isMobileAndCanMove is false (ApplyMovement force-zeroes the
        ///      target velocity). Unreadable is treated as refuse - this gate fails closed.
        /// A non-land unit returns false: that is "this gate asserts nothing", and the
        /// caller's own unit-type gate still applies. Inheriting a movement command or
        /// owning a Rigidbody is NOT evidence either way (OilRig gets a Rigidbody at
        /// LandUnit.init and still cannot move).
        /// </summary>
        internal static bool RefuseMoveCommand(ObjectBase u)
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread", "LandUnits.RefuseMoveCommand must run on the game's main thread", false);
            return RefuseMoveReason(u) != null;
        }

        /// <summary>
        /// The reason slug behind RefuseMoveCommand(), or null when movement is not refused.
        /// Slugs: null_unit, fixed_installation, not_mobile, not_mobile_and_can_not_move,
        /// movement_state_unreadable. The 409 body should carry this plus landUnitType.
        /// </summary>
        internal static string RefuseMoveReason(ObjectBase u)
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread", "LandUnits.RefuseMoveReason must run on the game's main thread", false);
            if (u == null) return "null_unit";
            try
            {
                if (!(u is SeaPower.LandUnit)) return null; // gate not applicable, not a verdict

                string sub = SubTypeName(u);
                if (IsFixedSubType(sub)) return "fixed_installation";

                object isMobile = GameReflect.Member(u, "_isMobile");
                if (isMobile == null) return "movement_state_unreadable";
                if (!Api.Truthy(isMobile)) return "not_mobile";

                object isMobileAndCanMove = GameReflect.Member(u, "_isMobileAndCanMove");
                if (isMobileAndCanMove == null) return "movement_state_unreadable";
                if (!Api.Truthy(isMobileAndCanMove)) return "not_mobile_and_can_not_move";

                return null;
            }
            catch (Exception e)
            {
                _failures++;
                Core.Log("[LandUnits] RefuseMoveReason() failed: " + e.Message);
                return "movement_state_unreadable";
            }
        }

        private static bool IsFixedSubType(string subTypeName)
        {
            if (subTypeName == null) return false;
            for (int i = 0; i < FixedInstallationSubTypes.Length; i++)
            {
                if (FixedInstallationSubTypes[i] == subTypeName) return true;
            }
            return false;
        }

        /// <summary>
        /// The provenance / limit notes Read() carries. Written as sentences on purpose:
        /// LAND_MIN_IMPL.md repeats each of them next to its IL evidence.
        /// </summary>
        private static void AppendReadNotes(JArray notes, string sub, bool fixedInstallation, string refuse)
        {
            if (notes == null) return;
            notes.Add("landUnitType read path: LandUnitParams (public property) -> LandUnitParameters._landUnitSubType (public field), name via Enum.GetName; an out-of-range value yields no name at all.");
            notes.Add("canMoveNow is absent on purpose: the shipped build has no per-instance canMoveNow / CanMove / IsLandUnit / IsGround member on LandUnit or ObjectBase. Native movement is gated by _isMobile (TrueKinematics returns a zero speed envelope when false) and _isMobileAndCanMove (ApplyMovement force-zeroes the target velocity when false).");
            notes.Add("overallDamage is LandUnit._landUnitOverallDamage: the sum of (maxIntegrity - currentIntegrity) over destroyed systems, NOT a 0..1 ratio; overallDamagePct divides it by ObjectBase._totalDamagePoints. ObjectBase._overallDamage is the base-class field and is reported separately as baseOverallDamage.");
            notes.Add("selectedTargetPoints is a COUNT only (LandUnitParameters._targetPoints.Count). Target point names, descriptions, positions, categories, priorities, terrain and patrol areas are never emitted.");
            notes.Add("targetPointDebugMarkers counts LandUnit._targetPointsDebug, which is List<GameObject> debug markers rather than the TargetPoint data list, so it stays 0 unless the unit created its debug display.");
            notes.Add("_isMobileAndCanMove has no writer in Seapower-Scripts.dll (no stfld anywhere in the assembly) and appears in none of the 218 shipped land unit ini files, so its runtime value is read, not inferred - see the unverified section of research/LAND_MIN_IMPL.md.");
            notes.Add("_isMobile is a serialized public field driven by the ini key IsMobile (18 of the 218 units write IsMobile=True, and 12 MobileUnit entries write nothing at all), so the subtype is not a substitute for the field.");
            notes.Add("a missing or unparsable LandUnitSubType ini key leaves the enum at its default AirBase(0) (loadLandUnitSubType uses Enum.TryParse and keeps the default on failure), so an unstamped Port/Bridge can masquerade as a fixed installation subtype.");
            if (sub == null && !fixedInstallation)
            {
                notes.Add("landUnitType was unreadable, so refusal criterion 1 could not be decided from the subtype; criterion 2 still gate movement.");
            }
            if (fixedInstallation)
            {
                notes.Add("refusal criterion 1 (fixed-installation, from the subtype alone) fires for this unit; criterion 2 was not needed to refuse it.");
            }
            else if (refuse == "not_mobile" || refuse == "not_mobile_and_can_not_move")
            {
                notes.Add("refusal criterion 2 (not movable, from the _isMobile / _isMobileAndCanMove fields) fires for this unit; the subtype alone would have allowed movement.");
            }
            else if (refuse == null && "MobileUnit" == sub)
            {
                notes.Add("subtype MobileUnit with both movement flags true: the bridge must still confirm movement along the real native path before it claims the command took effect.");
            }
            notes.Add("_performAutoFlatten and _maskVegetation default to true in LandUnit..ctor and may be overridden by the ini.");
        }

        /// <summary>
        /// The land-unit flags that could actually be read, as [{flag, value, field, kind}].
        /// A flag whose backing member is missing or unreadable is ABSENT - not false. A flag
        /// that reads as false IS present with value:false, because that is a proof, not a gap.
        /// kind is "field", "property" or "type_test" so a consumer can tell how it was read.
        /// </summary>
        internal static JArray Flags(ObjectBase u)
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread", "LandUnits must run on the game's main thread", false);
            JArray a = new JArray();
            if (u == null) return a;

            try
            {
                if (!(u is SeaPower.LandUnit)) return a;

                // Proven by the type test itself, which is stronger than any field read.
                JObject land = new JObject();
                land["flag"] = "land";
                land["value"] = true;
                land["field"] = "u is SeaPower.LandUnit";
                land["kind"] = "type_test";
                a.Add(land);

                AddBoolFlag(a, u, "mobile", "_isMobile", "field");
                AddBoolFlag(a, u, "mobileAndCanMove", "_isMobileAndCanMove", "field");
                AddBoolFlag(a, u, "nearShore", "isNearShore", "property");
                AddBoolFlag(a, u, "autoFlatten", "_performAutoFlatten", "field");
                AddBoolFlag(a, u, "maskVegetation", "_maskVegetation", "field");
                AddBoolFlag(a, u, "launchDestructionPhysics", "_launchDestructionPhysics", "field");
                AddBoolFlag(a, u, "turretCookoffTriggered", "_turretCookoffTriggered", "field");
                return a;
            }
            catch (Exception e)
            {
                _failures++;
                Core.Log("[LandUnits] Flags() failed: " + e.Message);
                return a;
            }
        }

        /// <summary>
        /// Read-only detail for one land unit. Every value key is paired with a "&lt;key&gt;Field"
        /// holding the exact game member it came from, so a reader can go and check the number
        /// instead of trusting this file. Members that were attempted but could not be read are
        /// listed under "unreadable"; they are never replaced by 0 or false.
        /// No "ok" key: the caller wraps this in the standard envelope.
        /// </summary>
        internal static JObject Detail(ObjectBase u)
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread", "LandUnits must run on the game's main thread", false);
            JObject o = new JObject();
            if (u == null) return o;

            JArray unreadable = new JArray();
            try
            {
                if (!(u is SeaPower.LandUnit))
                {
                    // Answering a detail sheet for a non-land object would be the silent-wrong-answer
                    // failure this project keeps hitting. Say what it is and stop.
                    o["id"] = IdOf(u);
                    o["name"] = NameOf(u);
                    o["objectType"] = TypeNameOf(u);
                    o["objectTypeField"] = "GetType().FullName";
                    o["reason"] = "not a SeaPower.LandUnit; LandUnits.Detail only describes ground units";
                    return o;
                }

                o["id"] = IdOf(u);
                o["name"] = NameOf(u);
                o["objectType"] = TypeNameOf(u);
                o["objectTypeField"] = "GetType().FullName";

                // ---- type / subtype
                object pars = Params(u);
                if (pars == null)
                {
                    unreadable.Add("LandUnitParams (property)");
                }
                else
                {
                    object sub = GameReflect.Member(pars, "_landUnitSubType");
                    string subName = EnumName(sub);
                    int subOrd = EnumOrdinal(sub);
                    if (subName != null)
                    {
                        o["subType"] = subName;
                        o["subTypeField"] = "_landUnitSubType";
                        o["subTypePath"] = "LandUnit.LandUnitParams -> LandUnitParameters._landUnitSubType";
                        o["subTypeValue"] = subOrd;
                        o["subTypeValueField"] = "_landUnitSubType (enum ordinal)";
                    }
                    else
                    {
                        // Either the member is null or the number is not a declared member.
                        // Persisting the ordinal is useful; inventing a name is not.
                        if (subOrd >= 0)
                        {
                            o["subTypeValue"] = subOrd;
                            o["subTypeValueField"] = "_landUnitSubType (enum ordinal, no declared name)";
                        }
                        unreadable.Add("LandUnitParameters._landUnitSubType");
                    }
                }

                // ---- mobility and state flags
                PutBool(o, "mobile", GameReflect.Member(u, "_isMobile"), "_isMobile", unreadable);
                PutBool(o, "mobileAndCanMove", GameReflect.Member(u, "_isMobileAndCanMove"), "_isMobileAndCanMove", unreadable);
                PutBool(o, "nearShore", GameReflect.Member(u, "isNearShore"), "isNearShore", unreadable);

                // ---- damage
                PutNum(o, "damage", GameReflect.Member(u, "_landUnitOverallDamage"), "_landUnitOverallDamage", unreadable);
                PutNum(o, "baseDamage", GameReflect.Member(u, "_overallDamage"), "ObjectBase._overallDamage", unreadable);

                // ---- fires / destroyed systems / launchers / hatch
                PutCount(o, "fires", GameReflect.Member(u, "_fires"), "_fires", unreadable);
                PutCount(o, "destroyedSystems", GameReflect.Member(u, "_destroyedSystems"), "_destroyedSystems", unreadable);
                PutCount(o, "openMissileContainers", GameReflect.Member(u, "_openMissileContainers"), "_openMissileContainers", unreadable);
                PutNum(o, "currentOpenHatch", GameReflect.Member(u, "_currentOpenHatch"), "_currentOpenHatch", unreadable);

                // ---- weapons fit (counts only: the element types are unverified)
                object obp = GameReflect.Member(u, "_obp");
                if (obp == null)
                {
                    unreadable.Add("ObjectBase._obp");
                }
                else
                {
                    PutCount(o, "weaponSystems", GameReflect.Member(obp, "_weaponSystems"), "_obp._weaponSystems", unreadable);
                    PutCount(o, "gunWeapons", GameReflect.Member(obp, "_gunWeapons"), "_obp._gunWeapons", unreadable);
                    PutCount(o, "launcherWeapons", GameReflect.Member(obp, "_launcherWeapons"), "_obp._launcherWeapons", unreadable);
                    PutCount(o, "hardpointWeapons", GameReflect.Member(obp, "_hardpointWeapons"), "_obp._hardpointWeapons", unreadable);
                    PutCount(o, "magazineSystems", GameReflect.Member(obp, "_magazineSystems"), "_obp._magazineSystems", unreadable);
                }

                // ---- target points (the authoritative list lives on the parameters object)
                PutCount(o, "targetPointsDebug", GameReflect.Member(u, "_targetPointsDebug"), "_targetPointsDebug", unreadable);
                if (pars != null) TargetPointStats(pars, o, unreadable);

                // ---- template speeds, only because a land unit is where "max speed" is least obvious
                if (pars != null)
                {
                    PutNum(o, "maxForwardKnots", GameReflect.Member(pars, "_maxForwardVelocityInKnots"), "LandUnitParameters._maxForwardVelocityInKnots", unreadable);
                    PutNum(o, "maxBackwardKnots", GameReflect.Member(pars, "_maxBackwardVelocityInKnots"), "LandUnitParameters._maxBackwardVelocityInKnots", unreadable);
                }

                if (unreadable.Count > 0) o["unreadable"] = unreadable;
                return o;
            }
            catch (Exception e)
            {
                _failures++;
                Core.Log("[LandUnits] Detail() failed: " + e.Message);
                o["error"] = e.Message;
                o["failuresSoFar"] = _failures;
                return o;
            }
        }

        /// <summary>
        /// The honest ledger: what this file reads, what it refuses to read, and where the
        /// knowledge came from. Intended for docs and for the runtime probe's precondition
        /// checks, not for the request path.
        /// </summary>
        internal static JObject Table()
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread", "LandUnits must run on the game's main thread", false);
            try
            {
                JObject o = new JObject();
                o["type"] = "SeaPower.LandUnit";
                o["baseType"] = "SeaPower.ObjectBase";
                o["isLandTest"] = "u is SeaPower.LandUnit (compile-time type test; Seapower-Scripts.dll is a build reference). The game has no IsGroundUnit/IsLandUnit/IsGround member - ildump find 'IsGround' returns zero hits - so a type test is the only predicate that exists.";
                o["reachableFromUnits"] = "ObjectBase.isUnit() does isinst Vessel/Submarine/Aircraft/Helicopter/LandUnit/Biologic, so GET /units already returns ground units. What it does NOT give is any land marker: get_IsSurfaceUnit() is `is Vessel || is LandUnit`, so a land unit answers surface:true and air:false and sub:false, exactly like a small boat.";
                o["subTypeReadPath"] = "LandUnit.LandUnitParams (property) -> LandUnitParameters._landUnitSubType (enum)";
                o["subTypeEnum"] = "SeaPower.LandUnitParameters/LandUnitSubType";
                o["subTypeEnumSource"] = "enum constant table read out of the assembly (Cecil HasConstant), not inferred from declaration order";
                JArray names = new JArray();
                for (int i = 0; i < SubTypeNames.Length; i++) names.Add(SubTypeNames[i]);
                o["subTypeNames"] = names;
                o["subTypeNameCount"] = SubTypeNames.Length;

                JArray ver = new JArray();
                for (int i = 0; i < VerifiedFields.Length; i++) ver.Add(VerifiedFields[i]);
                o["verified"] = ver;

                JArray unv = new JArray();
                for (int i = 0; i < UnverifiedFields.Length; i++) unv.Add(UnverifiedFields[i]);
                o["unverified"] = unv;

                o["flagPolicy"] = "a flag is emitted only when its backing member was read; a missing member makes the key absent, never false";
                o["httpSurface"] = "none yet: no route in Api.cs calls this file (LAND-1 may not modify Api.cs), so LandUnits.Flags/Detail are reachable from a future route and from an in-process probe only";
                o["writeSide"] = "deliberately absent: TargetPoint and Ammunition/LandAttack are write-side surfaces that need a live observation first (research\\LAND_UNITS_IMPL.md)";
                o["failuresSoFar"] = _failures;
                return o;
            }
            catch (Exception e)
            {
                _failures++;
                Core.Log("[LandUnits] Table() failed: " + e.Message);
                JObject o = new JObject();
                o["error"] = e.Message;
                o["failuresSoFar"] = _failures;
                return o;
            }
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>LandUnit.LandUnitParams, the class template object holding the subtype.</summary>
        private static object Params(ObjectBase u)
        {
            if (u == null) return null;
            try
            {
                if (!(u is SeaPower.LandUnit)) return null;
                return GameReflect.Member(u, "LandUnitParams");
            }
            catch (Exception e)
            {
                _failures++;
                Core.Log("[LandUnits] Params() failed: " + e.Message);
                return null;
            }
        }

        private static void AddBoolFlag(JArray a, ObjectBase u, string flag, string member, string kind)
        {
            if (a == null || u == null) return;
            object v;
            try { v = GameReflect.Member(u, member); }
            catch (Exception ex)
            {
                _failures++;
                Core.Log("[LandUnits] flag '" + flag + "' (" + member + ") failed: " + ex.Message);
                return;
            }
            // Absent member = absent key. This is the whole policy in one line.
            if (v == null) return;
            JObject entry = new JObject();
            entry["flag"] = flag;
            entry["value"] = Api.Truthy(v);
            entry["field"] = member;
            entry["kind"] = kind;
            a.Add(entry);
        }

        /// <summary>Count of an ICollection-shaped member; -1 when it cannot be read.</summary>
        private static int CountOf(object seq)
        {
            if (seq == null) return -1;
            try
            {
                ICollection c = seq as ICollection;
                if (c != null) return c.Count;
                // Reactive collections and a few UniRx wrappers do not implement ICollection.
                object n = GameReflect.Member(seq, "Count");
                if (n != null) return (int)GameReflect.Num(n, -1);
            }
            catch (Exception e)
            {
                _failures++;
                Core.Log("[LandUnits] CountOf() failed: " + e.Message);
            }
            return -1;
        }

        private static void PutBool(JObject o, string key, object raw, string field, JArray unreadable)
        {
            if (raw == null)
            {
                if (unreadable != null) unreadable.Add(field);
                return;
            }
            o[key] = Api.Truthy(raw);
            o[key + "Field"] = field;
        }

        private static void PutNum(JObject o, string key, object raw, string field, JArray unreadable)
        {
            if (raw == null)
            {
                if (unreadable != null) unreadable.Add(field);
                return;
            }
            JToken t = Api.SafeNum(raw);
            if (t == null || t.Type == JTokenType.Null)
            {
                // Non-numeric or NaN: do not turn it into 0.
                if (unreadable != null) unreadable.Add(field);
                return;
            }
            o[key] = t;
            o[key + "Field"] = field;
        }

        private static void PutCount(JObject o, string key, object seq, string field, JArray unreadable)
        {
            int n = CountOf(seq);
            if (n < 0)
            {
                if (unreadable != null) unreadable.Add(field);
                return;
            }
            o[key] = n;
            o[key + "Field"] = field;
        }

        /// <summary>
        /// Target point count plus the two distributions the task asks for (category, priority)
        /// and a centre-point count. Elements are TargetPoint structs, iterated through
        /// IEnumerable: each one is boxed, which is why the field reads go through GameReflect.
        /// </summary>
        private static void TargetPointStats(object pars, JObject o, JArray unreadable)
        {
            object list = null;
            try { list = GameReflect.Member(pars, "_targetPoints"); }
            catch (Exception e)
            {
                _failures++;
                Core.Log("[LandUnits] _targetPoints failed: " + e.Message);
            }
            int n = CountOf(list);
            if (n < 0)
            {
                if (unreadable != null) unreadable.Add("LandUnitParameters._targetPoints");
                return;
            }
            o["targetPointCount"] = n;
            o["targetPointCountField"] = "LandUnitParameters._targetPoints";

            JObject byCat = new JObject();
            JObject byPrio = new JObject();
            int centers = 0;
            int centerReadable = 0;
            int scanned = 0;

            IEnumerable seq = list as IEnumerable;
            if (seq != null)
            {
                foreach (object tp in seq)
                {
                    scanned++;
                    string cat = EnumName(GameReflect.Member(tp, "_category"));
                    if (cat != null) Bump(byCat, cat);
                    string prio = EnumName(GameReflect.Member(tp, "_priority"));
                    if (prio != null) Bump(byPrio, prio);
                    object c = GameReflect.Member(tp, "_isCenterPoint");
                    if (c != null)
                    {
                        centerReadable++;
                        if (Api.Truthy(c)) centers++;
                    }
                }
            }

            o["targetPointsScanned"] = scanned;
            o["targetPointCategories"] = byCat;
            o["targetPointCategoryField"] = "LandUnitParameters/TargetPoint._category";
            o["targetPointPriorities"] = byPrio;
            o["targetPointPriorityField"] = "LandUnitParameters/TargetPoint._priority";
            if (centerReadable > 0)
            {
                o["targetPointCenterPoints"] = centers;
                o["targetPointCenterPointField"] = "LandUnitParameters/TargetPoint._isCenterPoint";
            }
            else
            {
                if (unreadable != null) unreadable.Add("LandUnitParameters/TargetPoint._isCenterPoint");
            }
            if (byCat.Count == 0 && n > 0 && unreadable != null)
                unreadable.Add("LandUnitParameters/TargetPoint._category");
        }

        private static void Bump(JObject o, string key)
        {
            if (o == null || key == null) return;
            JToken cur = o[key];
            int v = 0;
            if (cur != null) v = (int)cur;
            o[key] = v + 1;
        }

        /// <summary>
        /// Name of a boxed enum value, or null. Enum.GetName is used instead of ToString
        /// because ToString on an out-of-range value returns the NUMBER as a string, which would
        /// look like a name; null keeps "unknown" distinguishable from "a subtype I never saw".
        /// </summary>
        private static string EnumName(object v)
        {
            if (v == null) return null;
            try
            {
                Type t = v.GetType();
                if (!t.IsEnum) return null;
                return Enum.GetName(t, v);
            }
            catch (Exception e)
            {
                _failures++;
                Core.Log("[LandUnits] EnumName() failed: " + e.Message);
                return null;
            }
        }

        /// <summary>Numeric value of a boxed enum, or -1.</summary>
        private static int EnumOrdinal(object v)
        {
            if (v == null) return -1;
            try
            {
                Type t = v.GetType();
                if (!t.IsEnum) return -1;
                return (int)GameReflect.Num(v, -1);
            }
            catch (Exception) { return -1; }
        }

        private static string TypeNameOf(ObjectBase u)
        {
            if (u == null) return "unknown";
            try { return u.GetType().FullName; }
            catch (Exception) { return "unknown"; }
        }

        private static int IdOf(ObjectBase u)
        {
            if (u == null) return 0;
            try { return u.UniqueID; }
            catch (Exception) { return 0; }
        }

        // UnitTypeGate keeps its own private NameOf; this file may not modify that one, so the
        // same two-step read (DisplayNameFull, then the SourcedProperty fallback) is repeated
        // here rather than reaching into another file's private helper.
        private static string NameOf(ObjectBase u)
        {
            if (u == null) return "unknown";
            try
            {
                string s = Api.SafeStr(Api.Rx(u, "DisplayNameFull"));
                if (s != null && s.Length > 0) return s;
            }
            catch (Exception) { }
            try
            {
                string s2 = Api.SafeStr(Api.NameField(u));
                if (s2 != null && s2.Length > 0) return s2;
            }
            catch (Exception) { }
            return TypeNameOf(u);
        }
    }
}
