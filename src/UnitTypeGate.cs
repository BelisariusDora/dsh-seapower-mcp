// UnitTypeGate.cs - TR2-M / ISS-002: "can this op mean anything for THIS hull?"
//
// WHY THIS FILE EXISTS
// Every unit op rides the same endpoint (/units/{id}/{op}), so asking a submarine to launch
// aircraft, or a frigate to dive, reaches the game's own code and comes back as whatever the
// game felt like - usually a silent no-op with ok:true. That is the failure mode this project
// keeps tripping over: a plausible response that proves nothing. The gate answers ONE question
// - is this op meaningful for this kind of unit - and answers it before any game call, so a
// wrong pairing costs a 409 wrong_unit_type instead of a mystery.
//
// WHAT IT DELIBERATELY DOES NOT DO
//   * It never validates arguments. Missing lat, bad ammo id, out-of-range depth: those are
//     400s from the op's own validator, which knows the arguments better than a type table.
//   * It never gates reads. A read of "deck" on a submarine returns not_applicable with a
//     reason today, which is more useful than a 409.
//   * It touches no field whitelist. AllowedFieldsFor / ValidateUnitCommandFields stay the
//     only owners of request shape.
//   * It refuses ONLY where the pairing is impossible or provably a no-op. Anything that is
//     merely "unlikely" is left alone on purpose: a wrong 409 breaks working features, which
//     is worse than a silent no-op. Table() marks those cells "uncertain".
//
// JUDGEMENT SOURCES, in this order (never guess a criterion that is not on this list)
//   1. real runtime type: u is SeaPower.Vessel / Submarine / Aircraft / Helicopter /
//      LandUnit / Biologic. Those six plus WeaponBase / AI_Marker / WakeBubble are the ONLY
//      direct subclasses of ObjectBase in Seapower-Scripts.dll, so the type test is
//      exhaustive. Two traps, both IL-verified:
//        * Submarine is NOT a Vessel  -> `u is Vessel` means "surface ship", nothing else.
//        * Helicopter is NOT an Aircraft -> asking one does not cover the other. Any comment
//          that says "Helicopter derives from Aircraft" is wrong for this build.
//   2. capability reached by reflection: ObjectBase._obp (ObjectBaseParameters) ->
//      _flightDeck (SeaPower.FlightDeck). Two hops, exactly like Api.cs DeckObject.
//      LandUnit._isMobile decides whether a ground unit can move at all.
//   3. Api.SafeFlag(u, "air" / "sub" / "surface") - a cross-check only, never the primary
//      test, because its IL widens the meaning:
//        get_IsSubUnit     = `is Submarine || is Biologic`   -> a whale reports sub = true
//        get_IsSurfaceUnit = `is Vessel  || is LandUnit`     -> a truck reports surface = true
//        get_IsAirUnit     = `is Aircraft || is Helicopter`  -> this one is clean
//      get_IsAirUnit / get_IsSubUnit / get_IsSurfaceUnit all return false for WeaponBase,
//      AI_Marker and WakeBubble, which is why they are useless as the "is it a unit" test.
//
// EVIDENCE for the refusals below (decompiled IL, not guesswork)
//   launch / recover / abort   the deck hangs off ObjectBase._obp._flightDeck and both
//                              FlightDeckLaunchUnit overloads are instance methods that need
//                              it; with no deck there is no aircraft to put in the air and
//                              none to recover. The deck is a property of the OBJECT, not of
//                              the class: an airbase LandUnit owns one, a destroyer does not,
//                              so the class table cannot answer this - HasFlightDeck does.
//   vertical                   Submarine.setDepth / setPeriscopeDepth / setPresetDepth are
//                              Submarine methods; Aircraft.setPresetHeight and
//                              Helicopter.setPresetHeight are per-class; ObjectBase's
//                              UpdateWaypointsAltitudes is type-gated to Submarine/Biologic.
//                              A surface hull or a land unit has no altitude to write.
//   sonobuoy                   the buoy is dropped by the aircraft that carries it (the game's
//                              own user is an AircraftStates.LaySonobouys task); src\Sonobuoy.cs
//                              already enforces the same test one layer down.
//   telegraph                  ObjectBase.setTelegraph(int) compiles to a single IL `ret` - a
//                              real stub. Only Vessel, Submarine, Aircraft and Biologic
//                              override it, so a LandUnit or a Helicopter answers 200 and
//                              changes nothing. That is the silent no-op this ticket is about.
//   speed / course / move      a LandUnit with _isMobile == false cannot move; docs\
//                              COVERAGE_MATRIX.md section 3 lists this cell as "should be
//                              409 wrong_unit_type".
//
// C#5 only: the project builds with /langversion:5. No interpolation, no null-propagation,
// no nameof, no expression-bodied members, no out var, no tuples, no pattern matching.

using System;
using Newtonsoft.Json.Linq;
using SeaPower;

namespace SeaPowerAgentBridge
{
    internal static class UnitTypeGate
    {
        // Unit classes as a bit mask. "Air" is two classes because Aircraft and Helicopter are
        // siblings under ObjectBase, not parent and child.
        [Flags]
        private enum Klass
        {
            None = 0,
            Surface = 1,
            Sub = 2,
            FixedWing = 4,
            Rotary = 8,
            Land = 16,
            Bio = 32
        }

        private const Klass Air = Klass.FixedWing | Klass.Rotary;
        private const Klass Platforms = Klass.Surface | Klass.Sub | Air | Klass.Land;

        // The authoritative op list. Mirrors AllowedFieldsFor in Api.cs: an op that is not
        // here is not gated here, and the dispatch switch owns the "unknown unit command"
        // answer for it.
        private static readonly string[] OpNames = new string[]
        {
            "speed", "telegraph", "course", "move", "vertical", "attack", "fire",
            "sonobuoy", "identify", "emcon", "radar", "sonar", "launch", "recover",
            "abort", "rearm", "ceasefire", "rtb", "waypointClear"
        };

        // How many times the gate itself failed. Refuse() fails OPEN (returns null) so a
        // reflection mishap can never break a command that used to work, but the count is
        // readable so "it let something through" stays visible instead of silent.
        private static int _failures;

        internal static int FailureCount
        {
            get { return _failures; }
        }

        // ------------------------------------------------------------------ public API

        /// <summary>
        /// Null = the op is type-applicable to this unit and the caller may proceed.
        /// A sentence = it is not, and the caller should answer 409 wrong_unit_type with it.
        /// This method never throws for a type/capability question; an internal failure is
        /// counted and treated as "allow".
        /// </summary>
        internal static string Refuse(string op, ObjectBase u)
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread", "UnitTypeGate must run on the game's main thread", false);
            if (u == null || op == null || op.Length == 0) return null;

            try
            {
                Klass mask = MaskFor(op);
                // Not a gated op: stay out of the dispatch's way rather than inventing a 409.
                if (mask == Klass.None) return null;

                Klass k = Classify(u);
                int id = IdOf(u);

                if (k == Klass.None)
                {
                    return "op '" + op + "' does not apply to unit " + id + ": " + Describe(u)
                        + " is not a commandable platform. The game's isUnit() accepts only"
                        + " Vessel / Submarine / Aircraft / Helicopter / LandUnit / Biologic,"
                        + " so an object like this has no order path at all.";
                }

                if ((k & mask) == 0)
                {
                    return "op '" + op + "' does not apply to unit " + id + ": " + Describe(u)
                        + " is " + Article(k) + ", and this op is limited to "
                        + AppliesToText(op) + ".";
                }

                // "Has no flight deck" is NOT a type question, it is a capability question that
                // the dispatch already answers with its own code (409 no_flight_deck, raised by
                // the launch/recover/abort handlers). Answering wrong_unit_type here would
                // shadow a more precise refusal and invalidate live assertions that were earned
                // against it (tests\aviation_trace.py, tests\t195_live.py). So: stay silent.
                // Only a hull that is not even a platform at all is refused above.
                if (NeedsDeck(op) && !HasFlightDeck(u))
                    return null;

                if (NeedsMobility(op) && k == Klass.Land && !LandCanMove(u))
                {
                    return "unit " + id + " is a land unit with LandUnit._isMobile == false;"
                        + " it cannot move, so '" + op + "' does not apply.";
                }

                return null;
            }
            catch (Exception e)
            {
                _failures++;
                Core.Log("[UnitTypeGate] Refuse('" + op + "') failed, allowing the command: " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// Diagnostic reading of the whole table, op -> {appliesTo, reason, dynamic?, uncertain?}.
        /// Intended for docs and static checks, not for the request path.
        /// </summary>
        internal static JObject Table()
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread", "UnitTypeGate must run on the game's main thread", false);
            try
            {
                JObject ops = new JObject();
                int uncertainOps = 0;
                for (int i = 0; i < OpNames.Length; i++)
                {
                    string op = OpNames[i];
                    JObject e = new JObject();
                    e["appliesTo"] = Tokens(MaskFor(op));
                    e["reason"] = ReasonOf(op);
                    string dyn = DynamicOf(op);
                    if (dyn != null) e["dynamic"] = dyn;
                    JArray unc = UncertainOf(op);
                    if (unc.Count > 0)
                    {
                        e["uncertain"] = unc;
                        uncertainOps++;
                    }
                    ops[op] = e;
                }

                JObject o = new JObject();
                o["readPath"] = "ObjectBase._obp._flightDeck (two hops); LandUnit._isMobile (one hop)";
                o["typeSource"] = "sealed type test over the six unit subclasses of ObjectBase";
                o["safeFlagCaveat"] = "SafeFlag('sub') is also true for Biologic and SafeFlag('surface') is also true for LandUnit, so the flags are a cross-check only";
                o["opCount"] = OpNames.Length;
                o["uncertainOpCount"] = uncertainOps;
                o["failuresSoFar"] = _failures;
                o["ops"] = ops;
                return o;
            }
            catch (Exception e)
            {
                _failures++;
                Core.Log("[UnitTypeGate] Table() failed: " + e.Message);
                JObject o = new JObject();
                o["error"] = e.Message;
                o["opCount"] = OpNames.Length;
                o["failuresSoFar"] = _failures;
                return o;
            }
        }

        /// <summary>
        /// Human readable one-liner for a unit, e.g.
        /// "071型船坞登陆舰 / surface ship / flight deck: yes".
        /// Every part is read for real; anything unreadable becomes "unknown".
        /// </summary>
        internal static string Why(ObjectBase u)
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread", "UnitTypeGate must run on the game's main thread", false);
            if (u == null) return "unknown";

            try
            {
                Klass k = Classify(u);
                string noun;
                if (k == Klass.None) noun = TypeNameOf(u);
                else noun = Noun(k);

                string deck;
                if (HasFlightDeck(u)) deck = "yes";
                else deck = "no";

                string s = NameOf(u) + " / " + noun + " / flight deck: " + deck;

                if (k == Klass.Land)
                {
                    int mobile = Mobility(u);
                    if (mobile == 1) s = s + " / mobile: yes";
                    else if (mobile == 0) s = s + " / mobile: no";
                    else s = s + " / mobile: unknown";
                }
                return s;
            }
            catch (Exception e)
            {
                _failures++;
                Core.Log("[UnitTypeGate] Why() failed: " + e.Message);
                return "unknown";
            }
        }

        // ------------------------------------------------------- helpers other files may use

        /// <summary>
        /// Machine token for what the object is: "surface", "sub", "air", "helicopter",
        /// "land", "biologic", or the runtime type name for a non-unit.
        /// </summary>
        internal static string KindOf(ObjectBase u)
        {
            Klass k = Classify(u);
            if (k == Klass.None) return TypeNameOf(u);
            return Token(k);
        }

        /// <summary>Two hops, same path and same null handling as Api.cs DeckObject.</summary>
        internal static bool HasFlightDeck(ObjectBase u)
        {
            if (u == null) return false;
            try
            {
                object obp = GameReflect.Member(u, "_obp");
                if (obp == null) return false;
                object deck = GameReflect.Member(obp, "_flightDeck");
                return deck != null;
            }
            catch (Exception) { return false; }
        }

        /// <summary>Short human phrase for a unit, used inside refusal sentences.</summary>
        internal static string Describe(ObjectBase u)
        {
            if (u == null) return "this object";
            return "\"" + NameOf(u) + "\" (" + KindOf(u) + ", id " + IdOf(u) + ")";
        }

        // ------------------------------------------------------------------ class rules
        //
        // MaskFor / ReasonOf / DynamicOf / UncertainOf are the single source of truth for both
        // Refuse() and Table(), so the prose in the report cannot drift from the code.

        private static Klass MaskFor(string op)
        {
            if (op == null) return Klass.None;
            switch (op)
            {
                // Dropped from the air, never from a hull.
                case "sonobuoy":
                    return Air;

                // Only these four override the stub, so only these four do anything.
                case "telegraph":
                    return Klass.Surface | Klass.Sub | Klass.FixedWing | Klass.Bio;

                // Class half only: depth/periscope/preset_depth/push_waypoints need a
                // Submarine, preset_height needs an Aircraft or Helicopter, and Refuse cannot
                // see the requested mode, so the per-mode split stays in Api.cs UnitCommand.
                // Biologic is allowed through here because the class really does own
                // setDepth/setPresetDepth; Api.cs then refuses the modes it will not run.
                case "vertical":
                    return Klass.Sub | Air | Klass.Bio;

                // A deck belongs to the object, not to the class: an airbase LandUnit owns one
                // and a destroyer does not. NeedsDeck() applies the real test.
                case "launch":
                case "recover":
                case "abort":
                    return Platforms;

                // Movement. A stationary LandUnit passes the class test and is then refused by
                // the LandUnit._isMobile check.
                case "speed":
                case "course":
                case "move":
                    return Platforms;

                // Need a weapons fit / magazines. A hull with none keeps the cheaper, more
                // specific answer further down (404 ammo_not_found), so only the class is
                // decided here.
                case "attack":
                case "fire":
                case "rearm":
                    return Platforms;

                // Order and sensor ops: any commandable platform. Whether the platform carries
                // a radar or a sonar is equipment, not type, and is left to the op.
                case "identify":
                case "emcon":
                case "radar":
                case "sonar":
                case "ceasefire":
                case "rtb":
                case "waypointClear":
                    return Platforms;

                default:
                    return Klass.None;
            }
        }

        private static string ReasonOf(string op)
        {
            switch (op)
            {
                case "speed":
                case "course":
                case "move":
                    return "movable platforms; a land unit whose LandUnit._isMobile is false cannot move";
                case "telegraph":
                    return "Vessel / Submarine / Aircraft / Biologic only: ObjectBase.setTelegraph(int) is an empty stub (IL: a single ret), so LandUnit and Helicopter answer 200 and change nothing";
                case "vertical":
                    return "per mode: depth | periscope | preset_depth | push_waypoints are submarine-only, preset_height is aircraft/helicopter-only; the mode is not visible to Refuse, so Api.cs keeps that split";
                case "attack":
                    return "platforms with a weapons fit; the fit itself is not examined here";
                case "fire":
                    return "platforms with a weapons fit; ammunition is checked later (404 ammo_not_found)";
                case "rearm":
                    return "platforms with magazines; a unit with none is a silent no-op and is not caught here";
                case "sonobuoy":
                    return "Aircraft and Helicopter only: sonobuoys are dropped from the air (src\\Sonobuoy.cs keeps its own copy of this test)";
                case "launch":
                case "recover":
                case "abort":
                    return "platforms whose ObjectBase._obp._flightDeck is non-null: an airbase LandUnit has one, a destroyer does not";
                case "identify":
                case "emcon":
                case "radar":
                case "sonar":
                    return "any commandable platform; sensor fit is equipment and is not examined here";
                case "ceasefire":
                case "rtb":
                case "waypointClear":
                    return "any commandable platform";
                default:
                    return "op not covered by this gate";
            }
        }

        private static string DynamicOf(string op)
        {
            if (op == null) return null;
            switch (op)
            {
                case "launch":
                case "recover":
                case "abort":
                    return "flight_deck";
                case "speed":
                case "course":
                case "move":
                    return "land_mobile";
                default:
                    return null;
            }
        }

        private static JArray UncertainOf(string op)
        {
            JArray a = new JArray();
            if (op == null) return a;
            switch (op)
            {
                case "launch":
                case "recover":
                    a.Add("a deck that exists is not a deck that can take this aircraft: SeaPower.FlightDeckAircraft exposes only Name / Loadouts / Number / UnitRoles and carries no fixed-wing-vs-helicopter flag, so a helo-only deck cannot be told apart from a CATOBAR deck");
                    a.Add("runway length, catapult, elevators and the requested loadout are not examined");
                    if (op == "recover")
                        a.Add("whether any aircraft is airborne is runtime state, not unit type");
                    break;
                case "sonar":
                    a.Add("fitted active sonar systems (_obp._activeSonarSystems) are not examined: a hull with none answers 200 and does nothing");
                    break;
                case "radar":
                    a.Add("fitted radar sets are not examined: a platform with none answers 200 and does nothing");
                    break;
                case "emcon":
                    a.Add("a unit with no sensors still accepts emcon and shows no visible change");
                    break;
                case "attack":
                    a.Add("weapons fit and range are not examined here; an empty magazine surfaces later as 404 ammo_not_found");
                    break;
                case "fire":
                    a.Add("ammunition and range are not examined here; both surface later as 404 ammo_not_found or a 200 with refused:true");
                    break;
                case "rearm":
                    a.Add("magazines are not examined: a unit with none is a silent no-op");
                    break;
                case "vertical":
                    a.Add("Refuse cannot see the mode, so the sub-vs-air split must stay in Api.cs; this gate only excludes surface ships and land units at class level");
                    break;
            }
            return a;
        }

        // ------------------------------------------------------------------ predicates

        // ObjectBase has exactly nine direct subclasses and these are six of them; the other
        // three (WeaponBase / AI_Marker / WakeBubble) are the objects ?all=1 exposes and the
        // ones a command can never be issued to.
        private static Klass Classify(ObjectBase u)
        {
            if (u == null) return Klass.None;
            if (u is SeaPower.Vessel) return Klass.Surface;
            if (u is SeaPower.Submarine) return Klass.Sub;
            if (u is SeaPower.Aircraft) return Klass.FixedWing;
            if (u is SeaPower.Helicopter) return Klass.Rotary;
            if (u is SeaPower.LandUnit) return Klass.Land;
            if (u is SeaPower.Biologic) return Klass.Bio;
            return Klass.None;
        }

        private static bool NeedsDeck(string op)
        {
            return op == "launch" || op == "recover" || op == "abort";
        }

        private static bool NeedsMobility(string op)
        {
            return op == "speed" || op == "course" || op == "move";
        }

        /// <summary>1 = mobile, 0 = immobile, -1 = unreadable (treated as "allow").</summary>
        private static int Mobility(ObjectBase u)
        {
            try
            {
                object v = GameReflect.Member(u, "_isMobile");
                if (v == null) return -1;
                if (Api.Truthy(v)) return 1;
                return 0;
            }
            catch (Exception) { return -1; }
        }

        // Only refuse when the flag was readable AND false. An unreadable flag must not become
        // a refusal, or a game-side rename would silently break movement for every land unit.
        private static bool LandCanMove(ObjectBase u)
        {
            return Mobility(u) != 0;
        }

        // ------------------------------------------------------------------ labels

        private static string Token(Klass k)
        {
            if (k == Klass.Surface) return "surface";
            if (k == Klass.Sub) return "sub";
            if (k == Klass.FixedWing) return "air";
            if (k == Klass.Rotary) return "helicopter";
            if (k == Klass.Land) return "land";
            if (k == Klass.Bio) return "biologic";
            return "unknown";
        }

        /// <summary>Article-less noun, for Why(): "071型 / surface ship / flight deck: yes".</summary>
        private static string Noun(Klass k)
        {
            if (k == Klass.Surface) return "surface ship";
            if (k == Klass.Sub) return "submarine";
            if (k == Klass.FixedWing) return "aircraft";
            if (k == Klass.Rotary) return "helicopter";
            if (k == Klass.Land) return "land unit";
            if (k == Klass.Bio) return "biological contact";
            return "unknown object";
        }

        /// <summary>With article, for refusal sentences. Spelled out to dodge a/an bugs.</summary>
        private static string Article(Klass k)
        {
            if (k == Klass.Surface) return "a surface ship";
            if (k == Klass.Sub) return "a submarine";
            if (k == Klass.FixedWing) return "an aircraft";
            if (k == Klass.Rotary) return "a helicopter";
            if (k == Klass.Land) return "a land unit";
            if (k == Klass.Bio) return "a biological contact";
            return "an unknown object";
        }

        private static string AppliesToText(string op)
        {
            if (op == null) return "commandable platforms";
            switch (op)
            {
                case "sonobuoy":
                    return "aircraft and helicopters (sonobuoys are dropped from the air)";
                case "telegraph":
                    return "surface ships, submarines, aircraft and biological contacts (ObjectBase.setTelegraph is an empty stub)";
                case "vertical":
                    return "submarines (depth / periscope / preset_depth / push_waypoints) and aircraft or helicopters (preset_height)";
                case "launch":
                case "recover":
                case "abort":
                    return "platforms that own a flight deck";
                case "speed":
                case "course":
                case "move":
                    return "movable platforms";
                case "attack":
                case "fire":
                    return "platforms with a weapons fit";
                case "rearm":
                    return "platforms with magazines";
                default:
                    return "commandable platforms";
            }
        }

        private static string TypeNameOf(ObjectBase u)
        {
            if (u == null) return "unknown";
            try { return u.GetType().Name; }
            catch (Exception) { return "unknown"; }
        }

        private static int IdOf(ObjectBase u)
        {
            if (u == null) return 0;
            try { return u.UniqueID; }
            catch (Exception) { return 0; }
        }

        private static string NameOf(ObjectBase u)
        {
            if (u == null) return "unknown";
            // DisplayNameFull is the field every other route reports as "name" (Api.cs Deck(),
            // Units()). NameField is the fallback for the SourcedProperty wrappers, then the
            // bare type name so a refusal still identifies something.
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

        private static JArray Tokens(Klass mask)
        {
            JArray a = new JArray();
            if ((mask & Klass.Surface) != 0) a.Add("surface");
            if ((mask & Klass.Sub) != 0) a.Add("sub");
            if ((mask & Klass.FixedWing) != 0) a.Add("air");
            if ((mask & Klass.Rotary) != 0) a.Add("helicopter");
            if ((mask & Klass.Land) != 0) a.Add("land");
            if ((mask & Klass.Bio) != 0) a.Add("biologic");
            return a;
        }
    }
}
