// IntelScope.cs - TR2-I / ISS-003: "does the player's own picture contain this object?"
//
// ISS-003 was filed because GET /units enumerates every faction's every object, including
// enemy hulls the player has never detected. The fix is NOT "hide the enemy" - a contact the
// player really is tracking must still come back - it is "answer from the player's side of
// the table": an object is in scope when the player's own plotting tables carry a live
// contact for it, or it is ours, or it is an ally the player's UI already draws.
//
// WHY THIS FILE EXISTS SEPARATELY: /units needs the answer for every object at once and
// PlayerKnows(u) needs it for one. Recomputing the table walk per object would be a full
// sweep per row on the game's main thread, so KnownIds() builds the whole picture in one
// walk and Explain() reports what that walk actually saw - a wrong answer has to be
// diagnosable, not guessed at.
//
// WHAT THIS FILE PROVES: a row exists, in a plotting table owned by the player's side, whose
// Object field is that very object, and whose track is freshly detected or otherwise live.
// WHAT IT DOES NOT PROVE: that the player's screen shows the contact. It says nothing about
// whether Noesis drew the row, whether the UI filters or merges it, or whether the contact
// was ever presented to a human. See research/INTELSCOPE_IMPL.md for the full boundary list.
//
// PURITY: every entry point is a read. Nothing here writes game state, caches a decision, or
// changes what any existing route returns; /units keeps its own filters until the caller wires
// KnownIds() in.
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SeaPower;

namespace SeaPowerAgentBridge
{
    internal static class IntelScope
    {
        // Which rule admitted an object. The order of these constants is the order of the
        // rules in the task: mine, allied, operator switch, track. Explain() counts by
        // "first rule that matched", so an object admitted by two rules is counted once,
        // under the earlier one.
        private const int SourceNone = 0;
        private const int SourceMine = 1;
        private const int SourceFriendly = 2;
        private const int SourceReveal = 3;
        private const int SourceTrack = 4;

        // ---------------------------------------------------------------- public surface

        /// <summary>
        /// True when the player's own picture contains this object. Pure read.
        ///
        /// The rules run in a fixed order and the order is load-bearing:
        ///   (a) null object          -> false
        ///   (b) mine                 -> true   (our own hull is always on our own plot)
        ///   (c) Friendly             -> true   (allied hulls are drawn on the player's plot)
        ///   (d) reveal switch        -> true   (DEVELOPMENT DIAGNOSTICS, see below)
        ///   (e) fresh/live track row -> true   (the actual intel gate)
        ///   (f) otherwise            -> false
        ///
        /// (d) sits BEFORE (e) on purpose: with reveal_foreign.on an operator is asking for
        /// everything, and a diagnostics switch that still filtered would be useless. The cost
        /// of that order is that, with the switch on, knownFromTrack stops counting foreign
        /// objects - they were already admitted by (d). Explain() reports both counts
        /// separately so a diagnostics run cannot be mistaken for the product path.
        /// </summary>
        internal static bool PlayerKnows(ObjectBase u)
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread", "IntelScope must run on the game's main thread; Noesis and the ECS both refuse cross-thread reads", false);

            // (a) There is no knowledge of nothing. Checked first so the cheaper rules below
            // never have to carry a null special case.
            if (u == null) return false;

            // (b) mine, (c) Friendly, (d) reveal switch. Classify owns the order so this
            // method and KnownIds() can never disagree about which rule won.
            if (Classify(u, null, Api.IntelRevealEnabled()) != SourceNone) return true;

            // (e) The expensive step, reached only by a foreign object with the switch off:
            // one walk of every own-side plotting table. No reflection - PlottingTable and
            // Vehicle are compile-time types here - so the cost is one field load and two
            // property reads per contact row.
            Scan scan = ScanOwnTables(Api.AllObjects());
            return scan.Objects.Contains(u);

            // (f) Nothing matched: unknown.
        }

        /// <summary>
        /// Every ObjectBase.UniqueID the player knows about, in one pass. This is what a
        /// filtered /units list needs: building it per object would pay the table walk once
        /// per row.
        ///
        /// COST: one walk of the object list plus one walk of each own-side plotting table,
        /// i.e. O(objects + ownObjects * contactsPerTable). Each own-side object also costs
        /// one Api.SafeFlag read and, for a foreign object, one Api.RelationToPlayer call -
        /// and that call itself walks the object list to find the first player unit, which
        /// makes the worst case O(objects^2) in the relationship step. At the shipped
        /// nineteen objects that is a few hundred cheap calls, not a frame: the table walk
        /// dominates nothing and the whole call stays in the sub-millisecond range. That is an
        /// estimate, not a field measurement - the game was closed while this was written -
        /// and research/INTELSCOPE_IMPL.md derives the per-row cost and the pessimistic upper
        /// bound (a few hundred reflective getter reads, still around a millisecond).
        ///
        /// DESTROYED objects are deliberately NOT filtered here: a hull the player watched
        /// sink is still knowledge, and /units already has its own ?destroyed=1 filter. A
        /// caller that wants a live-contact list applies that filter on top.
        /// </summary>
        internal static HashSet<int> KnownIds()
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread", "IntelScope must run on the game's main thread; Noesis and the ECS both refuse cross-thread reads", false);

            HashSet<int> ids = new HashSet<int>();
            List<ObjectBase> all = Api.AllObjects();
            if (all == null) return ids;

            bool reveal = Api.IntelRevealEnabled();
            Scan scan = ScanOwnTables(all);
            for (int i = 0; i < all.Count; i++)
            {
                ObjectBase o = all[i];
                if (o == null) continue;
                if (Classify(o, scan, reveal) == SourceNone) continue;
                ids.Add(o.UniqueID);
            }
            return ids;
        }

        /// <summary>
        /// The diagnostic readout for the intel gate: how much of the object table the player
        /// actually knows, split by which rule admitted it, plus what the scan physically
        /// walked. Every number is measured, none is a placeholder. `ms` covers the whole
        /// call including the table walk.
        /// </summary>
        internal static JObject Explain()
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread", "IntelScope must run on the game's main thread; Noesis and the ECS both refuse cross-thread reads", false);

            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

            List<ObjectBase> all = Api.AllObjects();
            bool reveal = Api.IntelRevealEnabled();
            Scan scan = ScanOwnTables(all);

            int objects = 0;
            int known = 0;
            int fromMine = 0;
            int fromFriendly = 0;
            int fromReveal = 0;
            int fromTrack = 0;
            if (all != null)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    ObjectBase o = all[i];
                    // Nulls in GameMain._objects are not objects; counting them would make
                    // the denominator lie about how big the world is.
                    if (o == null) continue;
                    objects++;
                    int source = Classify(o, scan, reveal);
                    if (source == SourceNone) continue;
                    known++;
                    if (source == SourceMine) fromMine++;
                    else if (source == SourceFriendly) fromFriendly++;
                    else if (source == SourceReveal) fromReveal++;
                    else if (source == SourceTrack) fromTrack++;
                }
            }

            clock.Stop();

            JObject o2 = new JObject();
            o2["objects"] = objects;
            o2["known"] = known;
            o2["knownFromMine"] = fromMine;
            o2["knownFromFriendly"] = fromFriendly;
            o2["knownFromRevealSwitch"] = fromReveal;
            o2["knownFromTrack"] = fromTrack;
            o2["unknown"] = objects - known;
            o2["revealSwitch"] = reveal;

            // What the walk physically saw. tablesScanned/tablesNull/tableErrors are mutually
            // exclusive buckets over the own-side objects, so they add up to ownObjects and a
            // reader can tell "no table" from "table threw".
            o2["ownObjects"] = scan.OwnObjects;
            o2["tablesScanned"] = scan.TablesScanned;
            o2["tablesNull"] = scan.TablesNull;
            o2["tableErrors"] = scan.TableErrors;
            o2["vehicleErrors"] = scan.VehicleErrors;
            o2["vehiclesSeen"] = scan.VehiclesSeen;
            o2["vehiclesWithObject"] = scan.VehiclesWithObject;
            o2["vehiclesStale"] = scan.VehiclesStale;
            o2["vehiclesQualified"] = scan.VehiclesQualified;
            o2["duplicateTracks"] = scan.Duplicates;
            o2["ms"] = Math.Round(clock.Elapsed.TotalMilliseconds, 3);

            JArray rules = new JArray();
            rules.Add("a) null object -> unknown: there is no such thing as knowledge of nothing");
            rules.Add("b) mine -> known: the player's own hull is on the player's own plot by definition (ObjectBase.IsPlayerUnit, the same flag every other route reads through Api.SafeFlag)");
            rules.Add("c) Friendly -> known: allied hulls are drawn on the player's plot, so hiding them would be security theatre that breaks legitimate use (Api.RelationToPlayer reads Taskforce.RelationshipTo against the first player unit)");
            rules.Add("d) reveal_foreign.on -> known: this is DEVELOPMENT DIAGNOSTICS, not a product feature and never an acceptance criterion; it is checked before the track rule so a diagnostics run sees everything, and knownFromRevealSwitch counts only the objects this rule - not b or c - admitted");
            rules.Add("e) track -> known: some own-side PlottingTable.Vehicles row has Object == this object and is IsDetectedTrackFresh or HasLiveTrack (fresh = the detection component exists and missionSessionTime - _detTrackEpochLive <= _detTrackMaxAge; live = DetectedPosition.HasValue && (fresh || _isSelfCached))");
            rules.Add("f) anything else -> unknown, including an enemy our radar is illuminating before a contact row with a position exists, and a neutral merchant never observed at all");
            rules.Add("cost: one object-list walk plus one walk of each own-side plotting table; KnownIds() pays it once for the whole list, PlayerKnows() pays it only for a foreign object with the switch off");
            rules.Add("boundary: this proves a fresh or live row exists, NOT that Noesis drew it - it cannot prove the player's screen showed the contact");
            o2["rules"] = rules;
            return o2;
        }

        // ---------------------------------------------------------------- rule engine

        /// <summary>
        /// The single copy of the rule order, shared by all three entry points. `scan` may be
        /// null when the caller only wants the cheap rules (that is how PlayerKnows defers the
        /// table walk until it is actually needed): with a null scan rule (e) simply cannot
        /// match, and every other rule is independent of it.
        /// </summary>
        private static int Classify(ObjectBase u, Scan scan, bool reveal)
        {
            if (u == null) return SourceNone;
            if (Api.SafeFlag(u, "mine")) return SourceMine;
            if (IsFriendly(u)) return SourceFriendly;
            if (reveal) return SourceReveal;
            if (scan != null && scan.Objects.Contains(u)) return SourceTrack;
            return SourceNone;
        }

        // Ordinal on purpose, exactly like IntelPolicy.Decide: the game emits "Friendly", and
        // a case-insensitive match would also accept anything else a caller could type. Any
        // throw (no taskforce on either side) reads as "not friendly", never as an exception
        // escaping the gate.
        private static bool IsFriendly(ObjectBase u)
        {
            if (u == null) return false;
            string relation;
            try { relation = Api.RelationToPlayer(u); }
            catch (Exception) { return false; }
            if (relation == null) return false;
            return string.Equals(relation, "Friendly", StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------- the table walk

        // One traversal of the player's plotting tables. Both public queries need the same
        // answer, and two copies of this loop are how the two answers drift apart - so there is
        // exactly one copy, and it also carries the counters Explain() reports.
        private sealed class Scan
        {
            internal HashSet<ObjectBase> Objects = new HashSet<ObjectBase>();
            internal int OwnObjects;
            internal int TablesScanned;
            internal int TablesNull;
            internal int TableErrors;
            internal int VehiclesSeen;
            internal int VehiclesWithObject;
            internal int VehiclesStale;
            internal int VehiclesQualified;
            internal int VehicleErrors;
            internal int Duplicates;
        }

        private static Scan ScanOwnTables(List<ObjectBase> all)
        {
            Scan scan = new Scan();
            if (all == null) return scan;

            for (int i = 0; i < all.Count; i++)
            {
                ObjectBase o = all[i];
                if (o == null) continue;

                // Only the player's side has a table the player's own plot is built from. An
                // enemy table exists in the world too (that is what /tracks?scope=all reads)
                // and consulting it here would mark every contact an enemy sensor can see as
                // player knowledge - the exact leak ISS-003 is about.
                if (!Api.SafeFlag(o, "mine")) continue;
                scan.OwnObjects++;

                PlottingTable table = null;
                try { table = o.PlottingTable; }
                catch (Exception) { scan.TableErrors++; continue; }
                if (table == null) { scan.TablesNull++; continue; }

                System.Collections.IEnumerable vehicles = null;
                try { vehicles = table.Vehicles; }
                catch (Exception) { scan.TableErrors++; continue; }
                if (vehicles == null) { scan.TablesNull++; continue; }

                scan.TablesScanned++;
                try
                {
                    foreach (object raw in vehicles)
                    {
                        scan.VehiclesSeen++;
                        Vehicle v = raw as Vehicle;
                        if (v == null) continue;

                        // Object is the field holding the real ObjectBase; BaseObject is a
                        // property that wraps it. A row without one is an unidentified
                        // contact: it exists on the plot but names no object, so it can never
                        // prove knowledge of a specific object.
                        ObjectBase target = null;
                        try { target = v.Object; }
                        catch (Exception) { scan.VehicleErrors++; continue; }
                        if (target == null) continue;
                        scan.VehiclesWithObject++;

                        bool fresh = false;
                        bool live = false;
                        try
                        {
                            // IsDetectedTrackFresh is NOT public on the game's Vehicle type
                            // (csc CS1061 reading it directly), so it goes through GameReflect
                            // exactly like /tracks already reads it. HasLiveTrack IS public and
                            // is read directly, which is the cheap path that matters.
                            fresh = GameReflect.Truth(GameReflect.Member(v, "IsDetectedTrackFresh"));
                            live = v.HasLiveTrack;
                        }
                        catch (Exception) { scan.VehicleErrors++; continue; }

                        // "fresh OR live", not "fresh": HasLiveTrack also covers the table's
                        // cached self row (_isSelfCached), and a contact whose detection
                        // component just went away but whose DetectedPosition is still valid
                        // is still being drawn. Either one means the row is not a stale ghost.
                        if (!fresh && !live) { scan.VehiclesStale++; continue; }

                        scan.VehiclesQualified++;
                        // The same hull can be reported by several of our tables (taskforce
                        // hierarchy, relayed contacts). It is still one known object.
                        if (!scan.Objects.Add(target)) scan.Duplicates++;
                    }
                }
                catch (Exception)
                {
                    // The game's own Update mutates this ObservableCollection while we walk
                    // it. Losing the rest of THIS table is acceptable; the caller must still
                    // get an answer for the whole object table.
                    scan.TableErrors++;
                }
            }
            return scan;
        }
    }
}
