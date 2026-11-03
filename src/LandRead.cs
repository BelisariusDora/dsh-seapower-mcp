// LandRead.cs - LAND-2: the read side of a ground unit, member by member.
//
// WHY THIS FILE EXISTS
//   LandUnits.cs answers the gate question: is this a ground unit, what kind, may it be
//   ordered to move. It answers that and stops, on purpose. A player who looks at a SAM
//   site in the game sees far more than a subtype string: the points the site defends and
//   how much each one matters (the debug boxes are coloured red / yellow / green by
//   priority), which of its systems are wrecked, what is burning and since when, and how
//   close the site is to the "everything goes inoperable" line. Every one of those is
//   already a plain field on SeaPower.LandUnit / SeaPower.BaseSystem /
//   SeaPower.LandUnitParameters. This file reads them out and nothing else.
//
// WHAT THIS FILE DOES NOT DO
//   * It writes nothing. Every game member is reached through GameReflect.Member (read
//     only); there is no SetMember and no mutating Call anywhere below. No command is
//     raised, so none of this can move a unit by accident.
//   * It does not re-implement LandUnits. IsLand and SubTypeName are called, not copied.
//   * It does not fill a hole with a plausible number. A member that cannot be read makes
//     its key ABSENT and appends its name to "unreadable[]". Three members that the task
//     asked about are REFUSED rather than dressed up (see research\LAND_READ_IMPL.md):
//       _openMissileContainers  exactly one writer in the whole assembly (init:
//                               new List<int>()) and no reader: an always-empty list, not
//                               "which launchers are open".
//       _currentOpenHatch       same shape: one stfld in .ctor (default 0), no other access.
//       _maskVegetation         one stfld in .ctor (default false); LandUnit never reads it.
//   * It does not pretend _destroyedSystems is the live dead count. IL below shows it is an
//     append-only log. Both numbers are reported, under names that say which is which.
//
// EVIDENCE (ildump il on Sea Power_Data\Managed\Seapower-Scripts.dll)
//   OnUpdateEveryFrame IL_0172   call ObjectBase::get_IsDestroyed / brtrue -> IL_03CA, so a
//                                destroyed unit stops refreshing the damage fields below.
//   OnUpdateEveryFrame IL_0183   stfld _landUnitOverallDamage (zeroed every frame first).
//   OnUpdateEveryFrame IL_018E..IL_01C4  for each BaseSystem in _obp._systems with
//                                IsDestroyed(): add (_maxIntegrity - CurrentIntegrity)
//                                (IL_01AC / IL_01B2).
//   OnUpdateEveryFrame IL_01D2..IL_0201  "if destroyed and !_destroyedSystems.Contains(s)"
//                                -> new LandUnit/Fire(s, _obp) into _fires, then Add(s).
//   OnUpdateEveryFrame IL_0225..IL_0232  100 * _landUnitOverallDamage /
//                                ObjectBase._totalDamagePoints.
//   OnUpdateEveryFrame IL_0267   ldc.r4 99 / bgt.s -> IL_02B4 calls MakeInoperable() on
//                                every system in _obp._systems.
//   OnUpdateEveryFrame IL_027F..IL_02AA  else, subtype == 4 (SAM) and the radar subset
//                                (<>c::b__33_0 = "x is SensorSystemRadar") is non-empty and
//                                all of it satisfies b__33_1 = "x.IsDestroyed()" -> same
//                                MakeInoperable branch.
//   get_TrueKinematics           !_isMobile -> {MaxSpeedMps=0, MaxAccelMps2=0}; else
//                                MaxSpeedMps = _maxForwardVelocityInKnots * 0.514444 and
//                                MaxAccelMps2 = _acceleration > 0 ? _acceleration*0.514444
//                                : -1  (-1 is the game's own "unknown" sentinel).
//   get_isNearShore              transform.position.y <= 2.267855f. A height test, NOT a
//                                distance-to-coast test; the key below says so.
//   get_MaxForwardSpeedInKnots   forwards LandUnitParams._maxForwardVelocityInKnots;
//   get_MaxBackwardSpeedInKnots  same for _maxBackwardVelocityInKnots.
//   loadTargetPointsFromIni IL_0177..IL_0183  TargetPoint._position is pushed through
//                                Transform.set_localPosition on a child of
//                                _targetPointsParentGO (which init parents to the unit root)
//                                => object-local coordinates, not lat/lon, not world space.
//   loadTargetPointsFromIni IL_0125..IL_0135  debug box colour by _priority:
//                                2 High -> red, 1 Medium -> yellow, 0 Low -> green.
//   loadTargetPointsFromIni IL_013A..IL_0166  box name = _name, plus ";" + _description
//                                when the description is non-empty.
//   LandUnit/Fire..ctor IL_0018..IL_001E  FireStartTime = GameTime.missionElapsedTime,
//                                i.e. mission seconds since scenario start.
//   BaseSystem.get_IsDestroyed   CurrentDamageType == 4 (DamageValues/DamageType
//                                .Destroyed). BaseSystem.get_DisplayName reads the
//                                ReactiveProperty<string> backing field.
//   Every enum number used below came out of the constant tables (GetRawConstantValue),
//   never from declaration order: LandUnitSubType, TargetPointPriority (Low=0 Medium=1
//   High=2), TargetPoint/Category (General=0 Radar=1 LowSAM=2 HighSAM=3 AAA=4),
//   FireType (No=0 SelfExtinguishing=1 Moderate=2 Severe=3),
//   DamageType (No=0 Light=1 Medium=2 Severe=3 Destroyed=4), and SystemType
//   (Generic=0 .. CombatSystem=23).
//
// CONTRACT
//   Read-only, main thread only. Every entry point starts with the Core.IsMain guard and
//   throws ApiException(500, "off_main_thread", ...) otherwise. C#5: no interpolation, no
//   null-conditional, no nameof, no expression bodies, no out var.

using System;
using System.Collections;
using Newtonsoft.Json.Linq;
using SeaPower;

namespace SeaPowerAgentBridge
{
    internal static class LandRead
    {
        // Row caps: a port or an airbase can carry a lot of systems, and the bridge's job is
        // to answer quickly with a bounded payload. "truncated" says what was cut.
        private const int MaxRows = 64;
        private const int MaxSystemNames = 16;

        private static int _failures;

        // How many members have failed to read since load. Diagnostic only: a valid read that
        // legitimately reports false/0 never touches this.
        internal static int FailureCount
        {
            get { return _failures; }
        }

        // ---------------------------------------------------------------------------------
        // Entry points
        // ---------------------------------------------------------------------------------

        // Everything a caller needs about one ground unit, in one object. The five sections
        // are the same objects the individual entry points return, so a caller that already
        // has one of them does not need Summary.
        internal static JObject Summary(ObjectBase u)
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread", "LandRead must run on the game's main thread", false);

            JObject o = new JObject();
            if (u == null)
            {
                o["reason"] = "null unit";
                return o;
            }

            if (!(u is SeaPower.LandUnit))
            {
                // Not a refusal to answer - a refusal to invent. LandRead only describes the
                // ground-unit schema; anything else is LandUnits' business.
                o["typeName"] = u.GetType().FullName;
                o["isGroundUnit"] = false;
                o["reason"] = "not a SeaPower.LandUnit; LandRead describes ground units only";
                return o;
            }

            Tag t = new Tag(o);
            t.Given("typeName", new JValue(u.GetType().FullName), "u.GetType().FullName", "il-derived", "verified-schema");
            t.Given("isGroundUnit", new JValue(true), "u is SeaPower.LandUnit (ObjectBase.isUnit IL does isinst LandUnit)", "il-derived", "verified-schema");
            try
            {
                t.Given("id", new JValue(IdOf(u)), "ObjectBase.UniqueID", "field", "verified-schema");
            }
            catch (Exception)
            {
                Note(null, "ObjectBase.UniqueID");
            }
            t.Given("name", new JValue(NameOf(u)), "ObjectBase.DisplayNameFull via Api.NameField", "property", "verified-schema");
            string sub = LandUnits.SubTypeName(u);
            t.Given("subType", new JValue(sub == null ? "Unknown" : sub), "LandUnitParams._landUnitSubType (via LandUnits.SubTypeName)", "field", "verified-schema");

            o["movement"] = Movement(u);
            o["damage"] = Damage(u);
            // Deliberately NOT called "destroyedSystems": LandUnits.Detail already uses that
            // key for _destroyedSystems.Count, which is a different thing (an append-only log).
            o["systems"] = Systems(u);
            o["fires"] = Fires(u);
            o["targetPoints"] = TargetPoints(u);

            t.Seal();
            o["notes"] = NotesOf();
            o["schemaEntryPoint"] = "LandRead.Schema()";
            return o;
        }

        // Can it move, how fast can it go, where does it think it is.
        internal static JObject Movement(ObjectBase u)
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread", "LandRead must run on the game's main thread", false);

            JObject o = new JObject();
            Tag t = new Tag(o);
            if (u == null || !(u is SeaPower.LandUnit))
            {
                o["reason"] = "not a SeaPower.LandUnit";
                t.Seal();
                return o;
            }

            // _isMobile has no writer in the whole assembly: its value is authored in the ini
            // (key IsMobile) or the prefab, so "who set this" has no IL answer, only "this is
            // what the game loaded".
            t.Flag("isMobile", GameReflect.Member(u, "_isMobile"), "LandUnit._isMobile", "field", "from-il");
            // Also writer-less, and read in exactly one place: ApplyMovement IL_057D forces
            // the target velocity to 0 when this is false, then calls ApplyVelocity(0).
            t.Flag("isMobileAndCanMove", GameReflect.Member(u, "_isMobileAndCanMove"), "LandUnit._isMobileAndCanMove", "field", "from-il");
            t.Flag("nearShoreByHeight", GameReflect.Member(u, "isNearShore"), "isNearShore (transform.position.y <= 2.267855f)", "property", "verified-schema");
            t.Num("maxForwardKnots", GameReflect.Member(u, "MaxForwardSpeedInKnots"), "MaxForwardSpeedInKnots -> LandUnitParams._maxForwardVelocityInKnots", "property", "verified-schema");
            t.Num("maxBackwardKnots", GameReflect.Member(u, "MaxBackwardSpeedInKnots"), "MaxBackwardSpeedInKnots -> LandUnitParams._maxBackwardVelocityInKnots", "property", "verified-schema");

            object obp = Params(u);
            object accel = obp == null ? null : GameReflect.Member(obp, "_acceleration");
            t.Num("acceleration", accel, "LandUnitParams._acceleration (get_TrueKinematics multiplies it by 0.514444 to reach m/s^2, so it is in knots/s)", "field", "from-il");

            object kin = GameReflect.Member(u, "TrueKinematics");
            if (kin == null)
            {
                Note(null, "TrueKinematics (property returned no value)");
            }
            else
            {
                t.Num("maxSpeedMps", GameReflect.Member(kin, "MaxSpeedMps"), "TrueKinematics.MaxSpeedMps", "property", "verified-schema");
                object acc = GameReflect.Member(kin, "MaxAccelMps2");
                t.Num("maxAccelMps2", acc, "TrueKinematics.MaxAccelMps2 (-1 = the game's unknown sentinel)", "property", "verified-schema");
                if (o["maxAccelMps2"] != null)
                {
                    // Do not hide the sentinel behind a 0: say whether the number is a number.
                    double v = GameReflect.Num(acc, -2d);
                    t.Given("maxAccelKnown", new JValue(v >= 0d), "TrueKinematics.MaxAccelMps2 >= 0", "il-derived", "verified-schema");
                }
            }

            // Localized current-order text; CheckStatus writes it. Read as a string only -
            // stringifying the ReactiveProperty wrapper would produce a fake "name".
            object order = GameReflect.Member(u, "CurrentOrderText");
            t.Text("orderText", order, "ObjectBase.CurrentOrderText (ReactiveProperty<string>, localized)", "property", "from-il");

            t.Seal();
            return o;
        }

        // Damage numbers exactly as the game derives them, plus a live dead-system recount.
        internal static JObject Damage(ObjectBase u)
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread", "LandRead must run on the game's main thread", false);

            JObject o = new JObject();
            Tag t = new Tag(o);
            if (u == null || !(u is SeaPower.LandUnit))
            {
                o["reason"] = "not a SeaPower.LandUnit";
                t.Seal();
                return o;
            }

            t.Flag("unitDestroyed", GameReflect.Member(u, "IsDestroyed"), "ObjectBase.IsDestroyed", "property", "verified-schema");
            t.Num("overallDamage", GameReflect.Member(u, "_landUnitOverallDamage"), "LandUnit._landUnitOverallDamage", "field", "verified-schema");
            t.Num("totalDamagePoints", GameReflect.Member(u, "_totalDamagePoints"), "ObjectBase._totalDamagePoints", "field", "verified-schema");

            object obp = Params(u);
            object systems = obp == null ? null : GameReflect.Member(obp, "_systems");
            t.Count("systemsTotal", systems, "ObjectBaseParameters._systems", "field", "verified-schema");

            int dead = CountDestroyed(systems);
            if (dead >= 0)
            {
                t.Given("systemsDestroyedNow", new JValue(dead), "recount of _obp._systems where BaseSystem.IsDestroyed", "il-derived", "verified-schema");
            }
            else
            {
                Note(null, "ObjectBaseParameters._systems (not enumerable)");
            }

            // Not the live dead count: an append-only log. Named so nobody reads it as one.
            t.Count("destroyedSystemsLogged", GameReflect.Member(u, "_destroyedSystems"), "LandUnit._destroyedSystems (append-only log, never shrinks)", "field", "from-il");

            // Recompute the percentage here rather than trusting a cached value, and only when
            // both halves are readable: 0/0 must stay silent, not become 0%.
            if (o["overallDamage"] != null && o["totalDamagePoints"] != null)
            {
                double dmg = GameReflect.Num(GameReflect.Member(u, "_landUnitOverallDamage"), -1d);
                double total = GameReflect.Num(GameReflect.Member(u, "_totalDamagePoints"), -1d);
                if (dmg >= 0d && total > 0d)
                {
                    t.Given("damagePercent", new JValue(Math.Round(100d * dmg / total, 3)), "100 * _landUnitOverallDamage / _totalDamagePoints (OnUpdateEveryFrame IL_0225..IL_0232)", "il-derived", "verified-schema");
                }
            }

            t.Seal();
            return o;
        }

        // One row per BaseSystem under _obp._systems, capped at MaxRows.
        internal static JObject Systems(ObjectBase u)
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread", "LandRead must run on the game's main thread", false);

            JObject o = new JObject();
            JArray unreadable = new JArray();
            if (u == null || !(u is SeaPower.LandUnit))
            {
                o["reason"] = "not a SeaPower.LandUnit";
                return o;
            }

            object obp = Params(u);
            object seq = obp == null ? null : GameReflect.Member(obp, "_systems");
            int total = CountOf(seq);
            if (total < 0)
            {
                Note(unreadable, "ObjectBaseParameters._systems");
                o["unreadable"] = unreadable;
                return o;
            }

            JArray rows = new JArray();
            IEnumerable e = seq as IEnumerable;
            int n = 0;
            if (e != null)
            {
                foreach (object s in e)
                {
                    if (s == null) continue;
                    if (n >= MaxRows)
                    {
                        o["truncated"] = true;
                        break;
                    }
                    rows.Add(SystemRow(s));
                    n++;
                }
            }

            o["count"] = rows.Count;
            o["total"] = total;
            o["rows"] = rows;
            o["conf"] = SystemsConf();
            if (unreadable.Count > 0) o["unreadable"] = unreadable;
            return o;
        }

        // Open fires on this unit, with the mission time each started at.
        internal static JObject Fires(ObjectBase u)
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread", "LandRead must run on the game's main thread", false);

            JObject o = new JObject();
            if (u == null || !(u is SeaPower.LandUnit))
            {
                o["reason"] = "not a SeaPower.LandUnit";
                return o;
            }

            object seq = GameReflect.Member(u, "_fires");
            int total = CountOf(seq);
            if (total < 0)
            {
                Note(null, "LandUnit._fires");
                o["unreadable"] = new JArray(new JValue[] { new JValue("LandUnit._fires") });
                return o;
            }

            JArray rows = new JArray();
            IEnumerable e = seq as IEnumerable;
            int n = 0;
            if (e != null)
            {
                foreach (object f in e)
                {
                    if (f == null) continue;
                    if (n >= MaxRows)
                    {
                        o["truncated"] = true;
                        break;
                    }
                    rows.Add(FireRow(f));
                    n++;
                }
            }

            o["count"] = rows.Count;
            o["total"] = total;
            o["rows"] = rows;
            o["conf"] = FiresConf();
            return o;
        }

        // The defensive target points authored in the unit's ini, with their priority and
        // category. Positions are object-local (see the IL note above), not map coordinates.
        internal static JObject TargetPoints(ObjectBase u)
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread", "LandRead must run on the game's main thread", false);

            JObject o = new JObject();
            if (u == null || !(u is SeaPower.LandUnit))
            {
                o["reason"] = "not a SeaPower.LandUnit";
                return o;
            }

            object obp = Params(u);
            object seq = obp == null ? null : GameReflect.Member(obp, "_targetPoints");
            int total = CountOf(seq);
            if (total < 0)
            {
                Note(null, "LandUnitParameters._targetPoints");
                o["unreadable"] = new JArray(new JValue[] { new JValue("LandUnitParameters._targetPoints") });
                return o;
            }

            JArray rows = new JArray();
            IEnumerable e = seq as IEnumerable;
            int n = 0;
            if (e != null)
            {
                foreach (object tp in e)
                {
                    if (tp == null) continue;
                    if (n >= MaxRows)
                    {
                        o["truncated"] = true;
                        break;
                    }
                    rows.Add(TargetPointRow(tp));
                    n++;
                }
            }

            o["count"] = rows.Count;
            o["total"] = total;
            o["rows"] = rows;
            o["conf"] = TargetPointsConf();
            o["coordinateSpace"] = "object-local (TargetPoint._position -> Transform.set_localPosition)";
            return o;
        }

        // The self-describing table: what each key is, where it comes from, and how sure we are.
        // Kept out of Summary so a summary call stays small, but it is the authority for the
        // confidence tags that every other entry point carries inline.
        internal static JObject Schema()
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread", "LandRead must run on the game's main thread", false);

            JObject o = new JObject();
            JArray entryPoints = new JArray();
            entryPoints.Add("Summary(ObjectBase)");
            entryPoints.Add("Movement(ObjectBase)");
            entryPoints.Add("Damage(ObjectBase)");
            entryPoints.Add("Systems(ObjectBase)");
            entryPoints.Add("Fires(ObjectBase)");
            entryPoints.Add("TargetPoints(ObjectBase)");
            o["entryPoints"] = entryPoints;
            o["confidenceLegend"] = "verified-schema = member name+type+meaning confirmed in IL; from-il = IL shows the member exists and is read/written, but not what the value means for the player; unknown = readable, meaning unproven";
            o["sourceLegend"] = "field = private/public instance field read via reflection; property = property getter; il-derived = computed here from members whose IL was traced";

            JObject t = new JObject();
            Add(t, "movement.isMobile", "Boolean", "field", "from-il",
                "LandUnit._isMobile - no stfld anywhere in the assembly (authored in ini key IsMobile / prefab); ldfld sites: get_TrueKinematics IL_0001, init IL_0019, OnUpdateEveryFrame IL_00FA/IL_02ED, OnFixedUpdate IL_0029, CheckStatus IL_00BE");
            Add(t, "movement.isMobileAndCanMove", "Boolean", "field", "from-il",
                "LandUnit._isMobileAndCanMove - no stfld; one ldfld in ApplyMovement IL_057D: when false the target velocity is zeroed before ApplyVelocity");
            Add(t, "movement.nearShoreByHeight", "Boolean", "property", "verified-schema",
                "get_isNearShore: transform.position.y <= 2.267855f - a height test, not a distance-to-coast test");
            Add(t, "movement.maxForwardKnots", "Single", "property", "verified-schema",
                "get_MaxForwardSpeedInKnots returns LandUnitParams._maxForwardVelocityInKnots");
            Add(t, "movement.maxBackwardKnots", "Single", "property", "verified-schema",
                "get_MaxBackwardSpeedInKnots returns LandUnitParams._maxBackwardVelocityInKnots");
            Add(t, "movement.acceleration", "Single", "field", "from-il",
                "LandUnitParams._acceleration; get_TrueKinematics multiplies it by 0.514444 (knots->m/s), so the stored unit reads as knots/s");
            Add(t, "movement.maxSpeedMps", "Single", "property", "verified-schema",
                "TrueKinematics.MaxSpeedMps = _maxForwardVelocityInKnots * 0.514444");
            Add(t, "movement.maxAccelMps2", "Single", "property", "verified-schema",
                "TrueKinematics.MaxAccelMps2 = _acceleration > 0 ? _acceleration * 0.514444 : -1; -1 is the game's own unknown sentinel");
            Add(t, "movement.maxAccelKnown", "Boolean", "il-derived", "verified-schema",
                "recomputed as MaxAccelMps2 >= 0, so -1 is never reported as 0 m/s^2");
            Add(t, "movement.orderText", "String", "property", "from-il",
                "ObjectBase.CurrentOrderText (ReactiveProperty<string>); CheckStatus sets it, so it is the localized order line the player sees");
            Add(t, "damage.unitDestroyed", "Boolean", "property", "verified-schema",
                "ObjectBase.get_IsDestroyed reads ldfld isDestroyed");
            Add(t, "damage.overallDamage", "Single", "field", "verified-schema",
                "_landUnitOverallDamage: sum over _obp._systems with IsDestroyed of (_maxIntegrity - CurrentIntegrity), recomputed every frame (IL_0183..IL_01C4)");
            Add(t, "damage.totalDamagePoints", "Single", "field", "verified-schema",
                "ObjectBase._totalDamagePoints - the divisor at IL_0225/IL_022C");
            Add(t, "damage.damagePercent", "Single", "il-derived", "verified-schema",
                "100 * overallDamage / totalDamagePoints; at >99 the game itself calls MakeInoperable on every system (IL_0267 -> IL_02B4)");
            Add(t, "damage.systemsTotal", "Int32", "field", "verified-schema",
                "ObjectBaseParameters._systems.Count (List<BaseSystem>, element type from the IL_018E iteration)");
            Add(t, "damage.systemsDestroyedNow", "Int32", "il-derived", "verified-schema",
                "live recount of _obp._systems where BaseSystem.IsDestroyed (CurrentDamageType == DamageType.Destroyed == 4)");
            Add(t, "damage.destroyedSystemsLogged", "Int32", "field", "from-il",
                "_destroyedSystems is an append-only log filled at OnUpdateEveryFrame IL_01D2..IL_0201; it never shrinks and stops growing once the unit is destroyed");
            Add(t, "systems.rows[].name", "String", "property", "verified-schema",
                "BaseSystem.DisplayName (ReactiveProperty<string> backing field) with _systemName / _moduleName / _sectionName as fallback - never the wrapper's ToString");
            Add(t, "systems.rows[].systemType", "SystemType", "field", "verified-schema",
                "_systemType; BaseSystem/SystemType Generic=0 ... CombatSystem=23 (constant table)");
            Add(t, "systems.rows[].maxIntegrity", "Single", "field", "verified-schema",
                "_maxIntegrity - the left operand of the damage sum at IL_01AC");
            Add(t, "systems.rows[].currentIntegrity", "Single", "field", "verified-schema",
                "_currentIntegrity (returned by get_CurrentIntegrity)");
            Add(t, "systems.rows[].integrityPercent", "Single", "field", "verified-schema",
                "_currentIntegrityPercent");
            Add(t, "systems.rows[].damageType", "DamageType", "field", "verified-schema",
                "_currentDamageType; DamageType Destroyed=4 is what get_IsDestroyed tests");
            Add(t, "systems.rows[].repairable", "Boolean", "field", "verified-schema",
                "_repairable");
            Add(t, "systems.rows[].fire", "FireType", "field", "verified-schema",
                "_currentFire; FireType No=0 SelfExtinguishing=1 Moderate=2 Severe=3");
            Add(t, "systems.rows[].inoperable", "Boolean", "property", "verified-schema",
                "get_Inoperable (ReactiveProperty<bool>), unwrapped; also set wholesale by the >99% and SAM-radar rules");
            Add(t, "fires.rows[].systemName", "String", "property", "verified-schema",
                "Fire.System (BaseSystem) resolved the same way as systems.rows[].name");
            Add(t, "fires.rows[].fireType", "FireType", "field", "verified-schema",
                "LandUnit/Fire.FireType; the same enum the effect selection switches on (ctor IL_0023..IL_0059)");
            Add(t, "fires.rows[].startMissionSeconds", "Double", "field", "verified-schema",
                "Fire.FireStartTime = GameTime.missionElapsedTime (ctor IL_0018..IL_001E)");
            Add(t, "fires.rows[].effectObjects", "Int32", "field", "from-il",
                "Fire.FireEffectGOs.Count - pooled effect instances, not a damage figure");
            Add(t, "targetPoints.rows[].name", "String", "field", "verified-schema",
                "TargetPoint._name; loadTargetPointsFromIni IL_013A uses it as the debug box name");
            Add(t, "targetPoints.rows[].description", "String", "field", "verified-schema",
                "TargetPoint._description; IL_0144..IL_0166 appends it after ';' in the debug box name");
            Add(t, "targetPoints.rows[].priority", "TargetPointPriority", "field", "verified-schema",
                "TargetPoint._priority Low=0 Medium=1 High=2; drives the debug box colour red/yellow/green (IL_0125..IL_0135)");
            Add(t, "targetPoints.rows[].category", "Category", "field", "verified-schema",
                "TargetPoint._category General=0 Radar=1 LowSAM=2 HighSAM=3 AAA=4");
            Add(t, "targetPoints.rows[].isCenterPoint", "Boolean", "field", "verified-schema",
                "TargetPoint._isCenterPoint");
            Add(t, "targetPoints.rows[].localPosition", "Vector3", "field", "verified-schema",
                "TargetPoint._position -> Transform.set_localPosition under _targetPointsParentGO (IL_017D..IL_0183): object-local, not lat/lon, not world");
            Add(t, "targetPoints.rows[].associatedSystems", "String[]", "field", "from-il",
                "TargetPoint._associatedSystems (List<BaseSystem>, element type proven by the struct ctor signature); names only, capped at 16");
            o["fields"] = t;

            JArray refused = new JArray();
            refused.Add("LandUnit._openMissileContainers - one writer (init: new List<int>()) and no reader in the whole assembly: always empty");
            refused.Add("LandUnit._currentOpenHatch - one stfld in .ctor (default 0), no other access");
            refused.Add("LandUnit._maskVegetation - one stfld in .ctor (default false); LandUnit never reads it");
            refused.Add("LandUnit._lockCourseTime - written and read only inside ApplyMovement (course-lock timer); unit and timebase not traced");
            refused.Add("LandUnit._groundOffset - terrain-snapping offset used by OnUpdateEveryFrame/OnFixedUpdate/ApplyMovement; not player-visible state");
            refused.Add("LandUnit._performAutoFlatten - an authoring switch read once in OnUpdateEveryFrame IL_0057; LandUnits already flags it");
            refused.Add("LandUnit._targetPointsDebug - debug cubes only built by loadTargetPointsFromIni, which in-game code only calls from exportTargetPointsToIniFile");
            refused.Add("LandUnit/Fire.ShouldCease - no writer traced, so the value cannot be explained");
            refused.Add("CampaignLandUnit (+ its LandUnitType / DefenceType) - a SeapowerUI.ViewModels.GroupOfUnits campaign-layer view model, not a live unit, with no verified link from SeaPower.LandUnit");
            refused.Add("Ammunition/LandAttack - a weapon's land-attack class (None/Installation/Mobile/ShoreTargetsOnly), not a property of the target unit");
            o["refused"] = refused;
            return o;
        }

        // ---------------------------------------------------------------------------------
        // Rows
        // ---------------------------------------------------------------------------------

        private static JObject SystemRow(object s)
        {
            JObject row = new JObject();
            JArray missing = new JArray();

            string name = SystemNameOf(s);
            if (name != null) row["name"] = name;
            else Note(missing, "BaseSystem.DisplayName");

            EmitText(row, missing, "systemName", GameReflect.Member(s, "_systemName"), "BaseSystem._systemName");
            EmitText(row, missing, "moduleName", GameReflect.Member(s, "_moduleName"), "BaseSystem._moduleName");
            EmitText(row, missing, "sectionName", GameReflect.Member(s, "_sectionName"), "BaseSystem._sectionName");
            EmitChoice(row, missing, "systemType", GameReflect.Member(s, "_systemType"), "BaseSystem._systemType");
            EmitNum(row, missing, "maxIntegrity", GameReflect.Member(s, "_maxIntegrity"), "BaseSystem._maxIntegrity");
            EmitNum(row, missing, "currentIntegrity", GameReflect.Member(s, "CurrentIntegrity"), "BaseSystem.CurrentIntegrity");
            EmitNum(row, missing, "integrityPercent", GameReflect.Member(s, "_currentIntegrityPercent"), "BaseSystem._currentIntegrityPercent");
            EmitChoice(row, missing, "damageType", GameReflect.Member(s, "CurrentDamageType"), "BaseSystem.CurrentDamageType");
            EmitFlag(row, missing, "repairable", GameReflect.Member(s, "_repairable"), "BaseSystem._repairable");
            EmitChoice(row, missing, "fire", GameReflect.Member(s, "_currentFire"), "BaseSystem._currentFire");
            EmitFlag(row, missing, "inoperable", GameReflect.Member(s, "Inoperable"), "BaseSystem.Inoperable");

            if (missing.Count > 0) row["missing"] = missing;
            return row;
        }

        private static JObject FireRow(object f)
        {
            JObject row = new JObject();
            JArray missing = new JArray();

            object sys = GameReflect.Member(f, "System");
            string sname = SystemNameOf(sys);
            if (sname != null) row["systemName"] = sname;
            else Note(missing, "Fire.System");

            EmitChoice(row, missing, "fireType", GameReflect.Member(f, "FireType"), "Fire.FireType");
            EmitNum(row, missing, "startMissionSeconds", GameReflect.Member(f, "FireStartTime"), "Fire.FireStartTime");
            EmitCount(row, missing, "effectObjects", GameReflect.Member(f, "FireEffectGOs"), "Fire.FireEffectGOs");

            if (missing.Count > 0) row["missing"] = missing;
            return row;
        }

        private static JObject TargetPointRow(object tp)
        {
            JObject row = new JObject();
            JArray missing = new JArray();

            EmitText(row, missing, "name", GameReflect.Member(tp, "_name"), "TargetPoint._name");
            EmitText(row, missing, "description", GameReflect.Member(tp, "_description"), "TargetPoint._description");
            EmitChoice(row, missing, "priority", GameReflect.Member(tp, "_priority"), "TargetPoint._priority");
            EmitChoice(row, missing, "category", GameReflect.Member(tp, "_category"), "TargetPoint._category");
            EmitFlag(row, missing, "isCenterPoint", GameReflect.Member(tp, "_isCenterPoint"), "TargetPoint._isCenterPoint");
            // Object-local. Never reported as a map position; the container says so too.
            EmitVector(row, missing, "localPosition", GameReflect.Member(tp, "_position"), "TargetPoint._position");

            object seq = GameReflect.Member(tp, "_associatedSystems");
            int total = CountOf(seq);
            if (total < 0)
            {
                Note(missing, "TargetPoint._associatedSystems");
            }
            else
            {
                JArray names = new JArray();
                IEnumerable e = seq as IEnumerable;
                int n = 0;
                if (e != null)
                {
                    foreach (object s in e)
                    {
                        if (n >= MaxSystemNames) break;
                        string sn = SystemNameOf(s);
                        if (sn != null) names.Add(new JValue(sn));
                        n++;
                    }
                }
                row["associatedSystemCount"] = total;
                row["associatedSystems"] = names;
            }

            if (missing.Count > 0) row["missing"] = missing;
            return row;
        }

        // ---------------------------------------------------------------------------------
        // Conf maps for the row-style payloads (identical members on every row, so the map is
        // stated once at container level instead of repeating it in each row).
        // ---------------------------------------------------------------------------------

        private static JObject SystemsConf()
        {
            JObject c = new JObject();
            Conf(c, "rows[].name", "property", "verified-schema");
            Conf(c, "rows[].systemName", "field", "verified-schema");
            Conf(c, "rows[].moduleName", "field", "verified-schema");
            Conf(c, "rows[].sectionName", "field", "verified-schema");
            Conf(c, "rows[].systemType", "field", "verified-schema");
            Conf(c, "rows[].maxIntegrity", "field", "verified-schema");
            Conf(c, "rows[].currentIntegrity", "property", "verified-schema");
            Conf(c, "rows[].integrityPercent", "field", "verified-schema");
            Conf(c, "rows[].damageType", "property", "verified-schema");
            Conf(c, "rows[].repairable", "field", "verified-schema");
            Conf(c, "rows[].fire", "field", "verified-schema");
            Conf(c, "rows[].inoperable", "property", "verified-schema");
            return c;
        }

        private static JObject FiresConf()
        {
            JObject c = new JObject();
            Conf(c, "rows[].systemName", "property", "verified-schema");
            Conf(c, "rows[].fireType", "field", "verified-schema");
            Conf(c, "rows[].startMissionSeconds", "field", "verified-schema");
            Conf(c, "rows[].effectObjects", "field", "from-il");
            return c;
        }

        private static JObject TargetPointsConf()
        {
            JObject c = new JObject();
            Conf(c, "rows[].name", "field", "verified-schema");
            Conf(c, "rows[].description", "field", "verified-schema");
            Conf(c, "rows[].priority", "field", "verified-schema");
            Conf(c, "rows[].category", "field", "verified-schema");
            Conf(c, "rows[].isCenterPoint", "field", "verified-schema");
            Conf(c, "rows[].localPosition", "field", "verified-schema");
            Conf(c, "rows[].associatedSystems", "field", "from-il");
            return c;
        }

        // ---------------------------------------------------------------------------------
        // Tagging: one JObject, every value carries its origin and its confidence.
        // ---------------------------------------------------------------------------------

        private sealed class Tag
        {
            private readonly JObject _o;
            private readonly JObject _conf;
            private readonly JArray _unreadable;

            internal Tag(JObject target)
            {
                _o = target;
                _conf = new JObject();
                _unreadable = new JArray();
            }

            private void Mark(string key, string source, string confidence)
            {
                _conf[key] = source + "/" + confidence;
            }

            internal void Seal()
            {
                if (_conf.Count > 0) _o["conf"] = _conf;
                if (_unreadable.Count > 0) _o["unreadable"] = _unreadable;
            }

            internal void Num(string key, object raw, string member, string source, string confidence)
            {
                if (!EmitNum(_o, _unreadable, key, raw, member)) return;
                _o[key + "Field"] = member;
                Mark(key, source, confidence);
            }

            internal void Flag(string key, object raw, string member, string source, string confidence)
            {
                if (!EmitFlag(_o, _unreadable, key, raw, member)) return;
                _o[key + "Field"] = member;
                Mark(key, source, confidence);
            }

            internal void Text(string key, object raw, string member, string source, string confidence)
            {
                if (!EmitText(_o, _unreadable, key, raw, member)) return;
                _o[key + "Field"] = member;
                Mark(key, source, confidence);
            }

            internal void Count(string key, object seq, string member, string source, string confidence)
            {
                if (!EmitCount(_o, _unreadable, key, seq, member)) return;
                _o[key + "Field"] = member;
                Mark(key, source, confidence);
            }

            internal void Choice(string key, object raw, string member, string source, string confidence)
            {
                int before = _o.Count;
                if (EmitChoice(_o, _unreadable, key, raw, member))
                {
                    _o[key + "Field"] = member;
                    Mark(key, source, confidence);
                    return;
                }
                // An ordinal with no declared name is still a fact, but not a meaning: keep it
                // and label it unknown rather than guessing the name.
                if (_o.Count > before && _o[key + "Value"] != null)
                {
                    _o[key + "Field"] = member + " (ordinal; no declared member name)";
                    Mark(key + "Value", source, "unknown");
                }
            }

            // For values this file computes itself, where the provenance is a formula rather
            // than a single member.
            internal void Given(string key, JToken value, string how, string source, string confidence)
            {
                if (value == null)
                {
                    Note(_unreadable, how);
                    return;
                }
                _o[key] = value;
                _o[key + "Field"] = how;
                Mark(key, source, confidence);
            }
        }

        // ---------------------------------------------------------------------------------
        // Emitters: add a value if and only if it really reads; otherwise record the member
        // name as unreadable. A missing member never becomes 0 / false / "".
        // ---------------------------------------------------------------------------------

        private static bool EmitNum(JObject into, JArray missing, string key, object raw, string member)
        {
            if (raw == null)
            {
                Note(missing, member);
                return false;
            }
            JToken v = Api.SafeNum(raw);
            if (v == null || v.Type == JTokenType.Null)
            {
                Note(missing, member);
                return false;
            }
            into[key] = v;
            return true;
        }

        private static bool EmitFlag(JObject into, JArray missing, string key, object raw, string member)
        {
            if (raw == null)
            {
                Note(missing, member);
                return false;
            }
            // Api.Truthy unwraps a UniRx ReactiveProperty<T> one level before converting, which
            // is what BaseSystem.Inoperable needs; a plain bool passes straight through.
            into[key] = Api.Truthy(raw);
            return true;
        }

        private static bool EmitText(JObject into, JArray missing, string key, object raw, string member)
        {
            string s = StrOf(raw);
            if (s == null)
            {
                Note(missing, member);
                return false;
            }
            into[key] = s;
            return true;
        }

        private static bool EmitCount(JObject into, JArray missing, string key, object seq, string member)
        {
            int n = CountOf(seq);
            if (n < 0)
            {
                Note(missing, member);
                return false;
            }
            into[key] = n;
            return true;
        }

        private static bool EmitChoice(JObject into, JArray missing, string key, object raw, string member)
        {
            if (raw == null)
            {
                Note(missing, member);
                return false;
            }
            string name = EnumName(raw);
            int ord = EnumOrdinal(raw);
            if (name != null)
            {
                into[key] = name;
                into[key + "Value"] = ord;
                return true;
            }
            if (ord >= 0)
            {
                into[key + "Value"] = ord;
                Note(missing, member + " (ordinal only)");
            }
            else
            {
                Note(missing, member);
            }
            return false;
        }

        private static bool EmitVector(JObject into, JArray missing, string key, object vec, string member)
        {
            if (vec == null)
            {
                Note(missing, member);
                return false;
            }
            JToken x = Api.SafeNum(GameReflect.Member(vec, "x"));
            JToken y = Api.SafeNum(GameReflect.Member(vec, "y"));
            JToken z = Api.SafeNum(GameReflect.Member(vec, "z"));
            if (IsNullToken(x) || IsNullToken(y) || IsNullToken(z))
            {
                Note(missing, member);
                return false;
            }
            JObject v = new JObject();
            v["x"] = x;
            v["y"] = y;
            v["z"] = z;
            into[key] = v;
            return true;
        }

        private static bool IsNullToken(JToken t)
        {
            return t == null || t.Type == JTokenType.Null;
        }

        private static void Note(JArray missing, string member)
        {
            _failures++;
            if (missing == null) return;
            for (int i = 0; i < missing.Count; i++)
            {
                if (missing[i] != null && missing[i].ToString() == member) return;
            }
            missing.Add(new JValue(member));
        }

        private static void Conf(JObject conf, string path, string source, string confidence)
        {
            conf[path] = source + "/" + confidence;
        }

        private static void Add(JObject table, string path, string type, string source, string confidence, string evidence)
        {
            JObject e = new JObject();
            e["type"] = type;
            e["source"] = source;
            e["confidence"] = confidence;
            e["evidence"] = evidence;
            table[path] = e;
        }

        // ---------------------------------------------------------------------------------
        // Small readers. LandUnits keeps equivalents private and this file may not modify it,
        // so the few lines are restated here on purpose (see research\LAND_READ_IMPL.md).
        // ---------------------------------------------------------------------------------

        private static object Params(ObjectBase u)
        {
            return GameReflect.Member(u, "LandUnitParams");
        }

        private static int CountOf(object seq)
        {
            if (seq == null) return -1;
            ICollection c = seq as ICollection;
            if (c != null) return c.Count;
            object n = GameReflect.Member(seq, "Count");
            if (n == null) return -1;
            double d = GameReflect.Num(n, -1d);
            if (d < 0d) return -1;
            return (int)d;
        }

        private static int CountDestroyed(object seq)
        {
            IEnumerable e = seq as IEnumerable;
            if (e == null) return -1;
            int n = 0;
            foreach (object s in e)
            {
                if (s == null) continue;
                object d = GameReflect.Member(s, "IsDestroyed");
                if (d is bool && (bool)d) n++;
            }
            return n;
        }

        // Reads a string member, unwrapping a UniRx ReactiveProperty<T> one level, and never
        // calls ToString on the wrapper: a wrapper's ToString looks like a plausible name and
        // would be exactly the kind of fake value this file refuses to emit.
        private static string StrOf(object v)
        {
            if (v == null) return null;
            object inner = GameReflect.Member(v, "Value");
            if (inner != null) v = inner;
            return v as string;
        }

        private static string SystemNameOf(object sys)
        {
            if (sys == null) return null;
            string[] order = new string[] { "DisplayName", "_systemName", "_moduleName", "_sectionName" };
            for (int i = 0; i < order.Length; i++)
            {
                string s = StrOf(GameReflect.Member(sys, order[i]));
                if (s != null && s.Length > 0) return s;
            }
            return null;
        }

        private static string EnumName(object v)
        {
            if (v == null || !v.GetType().IsEnum) return null;
            try
            {
                return Enum.GetName(v.GetType(), v);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static int EnumOrdinal(object v)
        {
            if (v == null || !v.GetType().IsEnum) return -1;
            try
            {
                return Convert.ToInt32(v);
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static int IdOf(ObjectBase u)
        {
            object v = GameReflect.Member(u, "UniqueID");
            if (v == null) return -1;
            return (int)GameReflect.Num(v, -1d);
        }

        private static string NameOf(ObjectBase u)
        {
            string s = StrOf(GameReflect.Member(u, "DisplayNameFull"));
            if (s != null && s.Length > 0) return s;
            try
            {
                s = Api.NameField(u);
            }
            catch (Exception)
            {
                s = null;
            }
            if (s != null && s.Length > 0) return s;
            return u.GetType().Name;
        }

        // ---------------------------------------------------------------------------------
        // Notes: the sentences a caller needs in order not to misread the numbers above.
        // ---------------------------------------------------------------------------------

        private static JArray NotesOf()
        {
            JArray a = new JArray();
            a.Add("damage.overallDamage and damage.damagePercent are only refreshed while the unit is alive (OnUpdateEveryFrame IL_0172 jumps over the whole block once IsDestroyed is true), so on a wreck they are the last values written, not 0.");
            a.Add("damage.destroyedSystemsLogged is an append-only log, not the live dead count; damage.systemsDestroyedNow is the live recount. They legitimately disagree.");
            a.Add("At more than 99% damage the game itself makes every system inoperable (IL_0267 -> IL_02B4), so 'inoperable' can be true on a system that was never hit.");
            a.Add("A SAM site (subtype 4) whose SensorSystemRadar systems are all destroyed is also made fully inoperable (IL_027F..IL_02AA).");
            a.Add("movement.nearShoreByHeight is transform.position.y <= 2.267855f: a height test, not a distance to the coast.");
            a.Add("movement.maxAccelMps2 == -1 is the game's unknown sentinel; movement.maxAccelKnown says whether the number is real.");
            a.Add("targetPoints.rows[].localPosition is object-local (set through Transform.set_localPosition), not latitude/longitude and not world space.");
            a.Add("All reads are reflection over private members; nothing here is cached, so two calls a frame apart can differ. Nothing is written to the game object.");
            return a;
        }
    }
}
