// TR2-I / ISS-004: what one plotting-table contact is allowed to say about itself.
//
// ISS-004 was "GET /tracks merges every side's plotting table", closed by restricting the
// walk to the player's own tables (Api.cs, P-31 / T19.6). That closed the AGGREGATION leak.
// It did not close the per-contact one: AddTrack still hands out objectId
// (Vehicle.Object.UniqueID) for every contact whose Object happens to be non-null, whether
// or not the player has identified it. objectId plus /units lat/lon turns "a contact
// somewhere over there" into "this exact hull, at its true position" - the very join
// research\PLOT_NEAR.md:17 says must never be handed out together, because /units gives the
// real position while the track only ever carries an estimate.
//
// So the decision is per contact, not per route: a field is given when that contact's own
// identification state justifies it. Three states, one label each:
//
//   full          identified && classified   identity and platform class both known
//   partial       identified, not classified identity known, platform class is not
//   contact_only  not identified             the plot shows its generic unknown icon
//
// The four governed fields are objectId, class, type and side. Everything else is passed
// through untouched - and "untouched" includes keys this file has never heard of, because
// the whole object is DeepClone'd and only the governed four are edited or removed. Nothing
// is invented either: a key that arrives absent stays absent (an unidentified contact with
// no side key does not get one grafted on just to look uniform).
//
// WHY A PURE JSON TRANSFORM: the same rules must be provable offline, from fixtures, with
// no game running. This file touches no Unity type, no game assembly type and no IO. The
// Python reference implementation in tests\trackscope_selftest.py is the executable form of
// the table below; it exists because the main-thread guard makes the C# entry points
// unreachable from an offline harness (Core.MainThreadId is -1 there, so IsMain is false).
//
// WHY "side" IS DOWNGRADED AND NOT REMOVED: the field table in reviews\TR2-I\
// FIELD_PROVENANCE.md §2.6 already fixes this exact value - side falls back to "Unknown"
// when the target's own side cannot be read, and deliberately does NOT fall back to
// ReportingSide (that bug showed a hostile submarine as "Player"). The player's plot draws
// "unknown" for an unidentified contact, so "Unknown" is what the plot says, not a stand-in
// fake value; deleting the key instead would change the response shape for no benefit. It
// is still recorded as a downgrade so an auditor cannot mistake it for "the gate let it
// through untouched".
//
// WHAT THIS FILE IS NOT: it is not the whole of the picture by itself. The /tracks handler
// calls Apply for every contact (src\Api.cs, the "GET /tracks" branch), so the per-contact
// ladder does run; what this file cannot do is decide existence, which stays with
// IntelScope.PlayerKnows in the caller.
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SeaPower;

namespace SeaPowerAgentBridge
{
    internal static class TrackScope
    {
        /// <summary>identified && classified: the contact's identity and class are both known.</summary>
        internal const string PolicyFull = "full";

        /// <summary>identified but not classified: we know which hull, not what kind it is.</summary>
        internal const string PolicyPartial = "partial";

        /// <summary>Not identified: the plot draws a generic unknown icon and nothing more.</summary>
        internal const string PolicyContactOnly = "contact_only";

        private const string OffThreadMessage = "TrackScope must run on the game's main thread";

        // ---------------------------------------------------------------- entry points

        /// <summary>
        /// Returns a NEW track object with the governed fields trimmed to what the contact's
        /// identification state allows. The argument is never modified: every edit happens on
        /// a DeepClone, so a caller that still needs the raw row keeps it intact.
        /// </summary>
        internal static JObject Sanitize(JObject track)
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread", OffThreadMessage, false);

            if (track == null)
                throw new ApiException(400, "null_track",
                    "TrackScope.Sanitize needs a track object; a null here is a caller bug, and returning an empty object would hide it", false);

            return Rewrite(track, Classify(track));
        }

        /// <summary>
        /// Sanitizes every element of a /tracks array IN PLACE (the array the caller already
        /// built, so no second array has to be threaded back out) and returns the audit object.
        /// Elements that are not JSON objects are left exactly as they are and are not counted
        /// as contacts: this is a filter, not a place to drop data it does not understand.
        /// </summary>
        internal static JObject Apply(JArray tracks)
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread", OffThreadMessage, false);

            if (tracks == null)
                throw new ApiException(400, "null_tracks",
                    "TrackScope.Apply needs a tracks array; a null here would silently sanitize nothing", false);

            return Sweep(tracks, true);
        }

        /// <summary>
        /// Same judgement as <see cref="Apply"/>, counted but not written: what WOULD be
        /// withheld from this array. Shares Classify and Sweep with Apply on purpose - two
        /// copies of the criteria would drift, and the drift would be invisible.
        /// </summary>
        internal static JObject Audit(JArray tracks)
        {
            if (!Core.IsMain) throw new ApiException(500, "off_main_thread", OffThreadMessage, false);

            if (tracks == null)
                throw new ApiException(400, "null_tracks",
                    "TrackScope.Audit needs a tracks array; a null here would silently audit nothing", false);

            return Sweep(tracks, false);
        }

        // ---------------------------------------------------------------- the one judgement

        /// <summary>
        /// The whole policy, in one place, for one track. Everything below - the rewrite and
        /// both audit counters - reads this and nothing else, so "what Apply does" and "what
        /// Audit counts" cannot disagree.
        /// </summary>
        private static Verdict Classify(JObject track)
        {
            Verdict v = new Verdict();

            // Fail closed on purpose: only a real JSON boolean true counts as identified or
            // classified. A string "true", a 1, a null or a missing key all read as false,
            // because the expensive mistake here is releasing a field for a contact that was
            // never actually identified.
            v.Identified = Flag(track, "identified");
            v.Classified = Flag(track, "classified");

            if (v.Identified && v.Classified) v.Policy = PolicyFull;
            else if (v.Identified) v.Policy = PolicyPartial;
            else v.Policy = PolicyContactOnly;

            // objectId is the join to a live ObjectBase: it is given only in the full state.
            // Expressed as "policy is not full" rather than a second copy of the two booleans
            // so there is exactly one definition of "full" in this file.
            v.ObjectIdPresent = track.Property("objectId") != null;
            v.HideObjectId = v.ObjectIdPresent && v.Policy != PolicyFull;

            // class / type are governed by classified alone: a contact can be classified
            // (platform kind known) while still unidentified (which hull it is, unknown), and
            // in that state the plot does show a class icon. Presence-checked, so a field that
            // was never on the row is not claimed as withheld.
            v.HideClass = track.Property("class") != null && !v.Classified;
            v.HideType = track.Property("type") != null && !v.Classified;

            // side is the downgrade case: key kept, value replaced. Recorded only when the key
            // is actually there, for the same no-false-claims reason.
            v.DowngradeSide = track.Property("side") != null && !v.Identified;

            return v;
        }

        // ---------------------------------------------------------------- rewrite

        /// <summary>
        /// Pure rewrite with no guard: the guard lives on the three documented entry points.
        /// Kept separate so an offline harness can reflect into it and compare this file's
        /// behaviour against tests\trackscope_selftest.py without faking a main thread.
        /// </summary>
        private static JObject Rewrite(JObject track, Verdict v)
        {
            JObject o = (JObject)track.DeepClone();

            // A row that was already through this gate carries its own audit trail. Drop it
            // first: keeping last pass's redactedFields next to this pass's would produce a
            // list that describes neither state.
            o.Remove("intelPolicy");
            o.Remove("redactedFields");
            o.Remove("redactionNotes");

            List<string> fields = new List<string>();
            List<string> notes = new List<string>();

            if (v.HideObjectId)
            {
                o.Remove("objectId");
                fields.Add("objectId");
                notes.Add("objectId withheld: the object behind a track is named only when identified AND classified are both true. The link would upgrade a position estimate into the real hull via GET /units.");
            }

            if (v.HideClass)
            {
                o.Remove("class");
                fields.Add("class");
            }

            if (v.HideType)
            {
                o.Remove("type");
                fields.Add("type");
            }

            if ((v.HideClass || v.HideType) && !v.Classified)
            {
                // One note for the pair: they are withheld for the same reason and splitting
                // it into two near-identical sentences just makes the response noisier.
                notes.Add("class/type withheld: classified is not true, so the game's own plot draws no platform class for this contact either.");
            }

            if (v.DowngradeSide)
            {
                // Kept as a key, replaced in value. See the file header for why this is not a
                // removal: "Unknown" is what the player's plot displays for an unidentified
                // contact, and FIELD_PROVENANCE.md §2.6 already fixes this fallback.
                o["side"] = "Unknown";
                fields.Add("side");
                notes.Add("side downgraded to \"Unknown\", not dropped: an unidentified contact has no side to assert, and the key stays so the response shape matches what the plot shows. The plotting table that reported it is still in reportedBy.");
            }

            o["intelPolicy"] = v.Policy;

            // R4: the three tiers used to be readable only by inferring "which keys survived",
            // which forces a caller to re-derive the policy before it knows the shape it got.
            // State the ladder explicitly. parentId stays null and says why HONESTLY: an earlier
            // version of this comment claimed the plotting table "exposes no parent/child link",
            // which was never measured - research/type_vehicle_full.txt:139-140 shows the
            // simulation really does maintain VehicleParentComponent { Entity parent } plus
            // VehicleChildComponent, so the relation exists; what does not exist is a verified
            // path from a contact on the player's picture to another contact's id. See
            // docs/TRACKSCOPE_PARENT_ID.md for the three unknowns that keep this null.
            JObject ladder = new JObject();
            ladder["level"] = v.Policy == PolicyFull ? 2 : (v.Policy == PolicyPartial ? 1 : 0);
            ladder["policy"] = v.Policy;
            ladder["parentId"] = JValue.CreateNull();
            ladder["parentNote"] = "not established, so it is not guessed: the simulation does carry a "
                + "VehicleParentComponent/VehicleChildComponent link, but no verified path turns a contact "
                + "in the player's picture into another contact's id, and none was measured "
                + "(docs/TRACKSCOPE_PARENT_ID.md); level is the identity ladder only";
            o["hierarchy"] = ladder;

            // Absent rather than empty when nothing was withheld: an empty array would read as
            // "this gate ran and found nothing to say", which is not information worth sending
            // on every own-side contact.
            if (fields.Count > 0)
            {
                JArray arr = new JArray();
                for (int i = 0; i < fields.Count; i++) arr.Add(fields[i]);
                o["redactedFields"] = arr;
            }

            if (notes.Count > 0)
            {
                JArray narr = new JArray();
                for (int i = 0; i < notes.Count; i++) narr.Add(notes[i]);
                o["redactionNotes"] = narr;
            }

            return o;
        }

        // ---------------------------------------------------------------- sweep + audit

        /// <summary>
        /// One walk, two consumers: rewrite=true is Apply, rewrite=false is Audit. Keeping
        /// them as one loop is the whole point of the private Classify - the audit is then a
        /// statement about the same predicate, not a second reading of the same JSON.
        /// </summary>
        private static JObject Sweep(JArray tracks, bool rewrite)
        {
            JObject counts = new JObject();
            int full = 0;
            int partial = 0;
            int contactOnly = 0;
            int objectIdGiven = 0;
            int objectIdWithheld = 0;

            for (int i = 0; i < tracks.Count; i++)
            {
                JObject t = tracks[i] as JObject;
                if (t == null) continue;   // not a contact row: left untouched, not counted

                Verdict v = Classify(t);

                if (v.Policy == PolicyFull) full++;
                else if (v.Policy == PolicyPartial) partial++;
                else contactOnly++;

                // objectIdGiven/Withheld describe what happens to a link that is actually on
                // the row. A contact with no linked object is neither given nor withheld - it
                // never had one - so objectIdGiven + objectIdWithheld can legitimately be less
                // than count, and that gap is exactly the "no link right now" population.
                if (v.ObjectIdPresent)
                {
                    if (v.HideObjectId)
                    {
                        objectIdWithheld++;
                        Bump(counts, "objectId");
                    }
                    else objectIdGiven++;
                }

                if (v.HideClass) Bump(counts, "class");
                if (v.HideType) Bump(counts, "type");
                if (v.DowngradeSide) Bump(counts, "side");

                if (rewrite) tracks[i] = Rewrite(t, v);
            }

            JObject o = new JObject();
            // count is the ARRAY's length, not the number of rows classified. When the two
            // disagree it means a non-object element was in the array; it is still there and
            // still untouched, and forcing count == sum would erase the evidence of it.
            o["count"] = tracks.Count;
            o["full"] = full;
            o["partial"] = partial;
            o["contactOnly"] = contactOnly;
            o["objectIdGiven"] = objectIdGiven;
            o["objectIdWithheld"] = objectIdWithheld;
            // Only fields that were withheld at least once. An empty object here means
            // "nothing was withheld", which is a readable statement; a full table of zeros
            // would suggest the gate inspected eight things it never inspected.
            o["countsByField"] = counts;
            return o;
        }

        // ---------------------------------------------------------------- primitives

        /// <summary>True only for a real JSON boolean true. Missing, null, 0, "true": false.</summary>
        private static bool Flag(JObject o, string name)
        {
            JToken t = o[name];
            if (t == null) return false;
            if (t.Type != JTokenType.Boolean) return false;
            return (bool)t;
        }

        /// <summary>countsByField[name] += 1, creating the key on first real redaction only.</summary>
        private static void Bump(JObject counts, string name)
        {
            JToken cur = counts[name];
            int n = 0;
            if (cur != null && cur.Type == JTokenType.Integer) n = (int)cur;
            counts[name] = n + 1;
        }

        /// <summary>
        /// What Classify concluded, and what the audit therefore has to count. Public fields on
        /// a private nested type: no properties, no IEnumerable, nothing else can depend on it.
        /// </summary>
        private sealed class Verdict
        {
            public bool Identified;
            public bool Classified;
            public string Policy;
            public bool ObjectIdPresent;
            public bool HideObjectId;
            public bool HideClass;
            public bool HideType;
            public bool DowngradeSide;
        }
    }
}
