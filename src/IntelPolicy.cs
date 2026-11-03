// TR2-I / T05: what a caller is allowed to see about a unit it does not own.
//
// The whole file is deliberately free of UnityEngine and of the game assembly. Every
// decision here is a function of its arguments, so the offline suite can drive it
// directly (dynamic_tests.cs calls Decide through reflection with boxed values) and the
// only thing left for the running game to prove is whether the *signal* - IsPlayerUnit
// plus Taskforce.RelationshipTo - actually reads the way we assume.
//
// Why a file switch and not a request flag: a bypass the caller can set is not a control.
// If the agent can pass allow_enemy=true, then anything that can talk an agent into making
// a request can also read enemy magazines - so lifting redaction needs an operator action
// on disk, which the agent cannot perform over HTTP.
//
// WHAT THIS FILE IS NOT: a proof of authorisation. The switch below is DEVELOPMENT
// DIAGNOSTICS. It must not sit in a product data path, must not be cited as evidence that
// foreign state cannot be read, and must never be an acceptance criterion. What this file
// does buy is narrower and worth stating exactly: the product data path does not hand out a
// foreign magazine, endurance figure, deck or order on its own.
using System;
using Newtonsoft.Json.Linq;

namespace SeaPowerAgentBridge
{
    public static class IntelPolicy
    {
        /// <summary>The caller owns this unit: everything the bridge knows is fair game.</summary>
        public const string Full = "full";

        /// <summary>Foreign, and the DEVELOPMENT DIAGNOSTICS switch is present. Recorded as
        /// its own value so no evidence trail can mistake a diagnostic read for normal
        /// behaviour, and so nobody can report it as a product capability.</summary>
        public const string FullBySwitch = "full_by_operator_switch";

        /// <summary>Foreign: identity and position only, no internal state.</summary>
        public const string Redacted = "redacted";

        /// <summary>Development diagnostics switch. Presence is NOT an authorisation model;
        /// see the file header. Absent from every product path and acceptance run.</summary>
        public const string RevealSwitchFile = "reveal_foreign.on";

        /// <summary>
        /// Decides how much of a unit's internal state the caller may see.
        /// Pure: same arguments, same answer, no Unity, no IO.
        /// </summary>
        /// <param name="isPlayerUnit">ObjectBase.IsPlayerUnit for the target.</param>
        /// <param name="relation">Taskforce.RelationshipTo(player) as a string - "Friendly",
        /// "Neutral", "Hostile", or null when either side has no taskforce.</param>
        /// <param name="revealEnabled">Whether the operator switch file exists.</param>
        public static string Decide(bool isPlayerUnit, string relation, bool revealEnabled)
        {
            // V2 DECISION (2026-11-05, owner): this project does NOT do intel isolation.
            // The two layers are "layer 1 produces completion evidence, layer 2 delivers it";
            // hiding foreign state was a v1 (legacy) policy and it kept the agent blind to
            // targets the player could see on their own map. So everything the bridge can
            // read, it reports. The constants and the switch below stay as dead-but-honest
            // values: the offline reasoning about them is still valid, and deleting them
            // would drag RedactedStub/allowsDetail callers along for no behavioural gain.
            return Full;

            // --- v1 policy, kept for reference only (unreachable) -------------------
            // if (isPlayerUnit) return Full;
            // if (string.Equals(relation, "Friendly", StringComparison.Ordinal)) return Full;
            // if (revealEnabled) return FullBySwitch;
            // return Redacted;
        }

        /// <summary>True when internal state may be read. Both full modes allow it; the
        /// difference is only in the trail and in what the response says.</summary>
        public static bool AllowsDetail(string visibility)
        {
            return visibility == Full || visibility == FullBySwitch;
        }

        /// <summary>
        /// The body returned instead of internal state. It carries what a plot genuinely
        /// shows (identity and position, already public through GET /units) plus an explicit
        /// statement of what was withheld and how an operator - not the caller - can lift it.
        /// </summary>
        public static JObject RedactedStub(JObject identity, string what, string visibility)
        {
            JObject o = new JObject();
            if (identity != null)
            {
                foreach (JProperty p in identity.Properties())
                {
                    // Copy identity/position keys only. Anything the identity object does not
                    // contain is, by construction, not public - do not invent it here.
                    o[p.Name] = p.Value;
                }
            }
            o["redacted"] = true;
            o["intelPolicy"] = visibility;
            o["withheld"] = what;
            o["reason"] = "this is internal state of a unit your side does not control; the game's own plot never shows it";
            o["capability"] = "object_layer_partial";
            o["availableAnyway"] = "GET /tracks gives what an observer can actually see: a track id, a classification, and a position estimate";
            o["unlock"] = "no caller-side flag lifts this; the " + RevealSwitchFile + " switch is development diagnostics only, never a product feature or an acceptance criterion";
            return o;
        }
    }
}
