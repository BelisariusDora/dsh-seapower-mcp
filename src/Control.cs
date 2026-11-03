// Control.cs - write ops that must dispatch on the INSTANCE's real type, not on ObjectBase.
//
// THE CLASS IS CALLED UnitControl, NOT Control: src\UiBridge.cs:576 already has
// `private static void ControlChecks(JObject r, Control c)` where that Control is
// Noesis.Control (UiBridge.cs:31 `using Noesis;`). A SeaPowerAgentBridge.Control would win
// name resolution inside the namespace and turn that parameter into CS0721 (a static type
// cannot be a parameter). The file name is the one the task asked for; the type name has to
// step aside.
//
// WHY THIS FILE EXISTS (and what it is NOT for)
//   SeaPower.ObjectBase declares a family of members whose body is an empty shell. The
//   shell is not decorative: for the six concrete hull classes it is the *only* body that
//   runs when that class does not override it, so the call returns normally and changes
//   nothing. This file exists to make that difference visible and to refuse the shell
//   instead of reporting success.
//
//   A correction to the obvious reading of the bug, because it decides the design:
//   every one of those shells is declared VIRTUAL, and the working subclasses carry real
//   overrides. Cecil metadata (research\_ctl\virtmeta_out.txt, produced by virtmeta.ps1):
//
//     SeaPower.ObjectBase   virtual-newslot  setRudderAngle(Single)
//     SeaPower.Vessel       OVERRIDE         setRudderAngle(Single)
//     SeaPower.Submarine    OVERRIDE         setRudderAngle(Single)
//     SeaPower.Biologic     OVERRIDE         setRudderAngle(Single)
//     SeaPower.ObjectBase   virtual-newslot  setTelegraph(Int32)
//     SeaPower.Vessel       OVERRIDE         setTelegraph(Int32)
//     SeaPower.Submarine    OVERRIDE         setTelegraph(Int32)
//     SeaPower.Aircraft     OVERRIDE         setTelegraph(Int32)
//     SeaPower.Biologic     OVERRIDE         setTelegraph(Int32)
//     SeaPower.ObjectBase   virtual-newslot  getTelegraph()   -> IL: ldarg.0? no: ldc.i4.0 / ret
//
//   So a C# call `ObjectBase u; u.setRudderAngle(x)` emits `callvirt` and DOES reach the
//   override - the game itself relies on exactly that (OrderSetCourse.update calls
//   `callvirt ObjectBase::setRudderAngle`). The silent failure is narrower than "ObjectBase
//   calls do nothing", and it has three distinct shapes:
//     1. a hull with no override at all (Aircraft / Helicopter / LandUnit for rudder,
//        Helicopter / LandUnit for telegraph): the shell runs, 200, nothing changes;
//     2. a member that is non-virtual AND unimplemented everywhere (SetTypeName, OnAlive):
//        nobody can override it, so it is dead for every type;
//     3. a read that resolves to a shell while the write is real: Biologic.setTelegraph is
//        a genuine override, but Biologic has no getTelegraph, so a read-back lands on
//        ObjectBase.getTelegraph (ldc.i4.0 / ret) and reports 0 forever.
//
//   What a plain call therefore still cannot do, and why the MethodInfo route is worth the
//   trouble: (a) name the class that actually did the work, (b) tell "implemented" from
//   "shell" BEFORE writing, (c) pick the right read-back for a hierarchy where the getters
//   are three separate non-virtual copies (Vessel / Submarine / Biologic each declare their
//   own getSetRudderAngle - there is no shared base declaration to call).
//
// CONTRACT
//   * first statement of every entry point is the main-thread guard;
//   * error codes are limited to off_main_thread / unknown_field / missing_field / bad_type /
//     bad_value / out_of_range / unit_not_found / wrong_unit_type - no bad_number.
//     out_of_range is allowed ONLY for a field whose range the route-level guard also enforces,
//     and it must use the identical code there: one field may not have two identities
//     (see Telegraph, where the backstop exists precisely so a direct call cannot disagree);
//   * no `ok` field is written here: Api.cs owns the envelope (BusinessOk + resp["ok"]);
//   * C#5 only: no interpolation, no null-conditional, no nameof, no expression bodies.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using SeaPower;

namespace SeaPowerAgentBridge
{
    internal static class UnitControl
    {
        // DeclaredOnly is the whole point: GetMethods() on the runtime type would already
        // flatten the hierarchy, and a flattened list cannot answer "did this class really
        // implement it, or did we inherit a shell".
        private const BindingFlags Declared =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        private static readonly Type[] NoArg = new Type[0];
        private static readonly Type[] OneFloat = new Type[] { typeof(float) };
        private static readonly Type[] OneInt = new Type[] { typeof(int) };
        private static readonly Type[] ThreeFloat = new Type[] { typeof(float), typeof(float), typeof(float) };

        // 'id' is deliberately absent: Api.cs injects the path id as "unit" after the whitelist
        // runs, so a body-level "id" would be a second address for the same hull. It used to be
        // accepted as an alias and silently disagreed with the path when a caller sent both -
        // "unknown_field" is the honest answer, and the path stays the only address.
        // The per-op lists live in Api.AllowedFieldsFor; the module-side view of them is
        // Api.ModuleAllowedFieldsFor, which is that list plus the injected "unit".

        // The game's own full-rudder convention. Not invented here: Vessel/Submarine/Biologic
        // turnRudderLeft/turnRudderRight clamp at -25/+25 (IL_0069: ldc.r4 -25, IL_007B:
        // ldc.r4 25) and OrderSetCourse.update uses the same 25 as its bang-bang threshold.
        private const double FullRudder = 25.0;

        // =====================================================================
        // rudder: setRudderAngle
        // =====================================================================
        internal static JObject Rudder(JObject req)
        {
            if (!Core.IsMain)
                throw new ApiException(500, "off_main_thread",
                    "rudder writes the running object's rudder set-point and must run on the main thread", false);

            RejectUnknown(req, "rudder", Api.ModuleAllowedFieldsFor("rudder"));
            int id = UnitId(req);
            double angle = ReqDouble(req, "angle");
            if (double.IsNaN(angle) || double.IsInfinity(angle))
                throw Bad("rudder", "angle", "must be a finite number of degrees");
            if (angle < -90.0 || angle > 90.0)
                throw Bad("rudder", "angle", "must be within -90..90 degrees, got " + angle);
            bool validateOnly = OptBool(req, "validate_only", false);

            ObjectBase u = FindUnitGated(id);
            // Refuses a hull whose chain only offers ObjectBase.setRudderAngle (the shell).
            MethodInfo setter = RequireReal(u, "rudder", "setRudderAngle", OneFloat);

            double before;
            string beforeVia;
            bool haveBefore = TryReadRudder(u, out before, out beforeVia);

            JObject data = new JObject();
            data["id"] = id;
            data["op"] = "rudder";
            data["unitType"] = Tok(u.GetType().FullName);
            data["angle"] = Tok(angle);
            data["appliedOn"] = Tok(setter.DeclaringType.FullName);
            data["declaringIsRuntimeType"] = setter.DeclaringType == u.GetType();
            data["dispatch"] = Tok("runtime_type_methodinfo");
            data["before"] = haveBefore ? (JToken)Tok(before) : NullTok();
            data["readbackVia"] = Tok(beforeVia);

            if (validateOnly)
            {
                data["validated"] = true;
                data["wouldCall"] = Tok(Describe(setter) + "(" + angle + ")");
                data["note"] = Tok("validate_only ran every gate (unit exists, " + setter.DeclaringType.Name
                    + ".setRudderAngle is a real implementation, " + angle
                    + " is a legal angle) and stopped before the write.");
                return data;
            }

            Invoke(setter, u, new object[] { (float)angle }, "rudder");

            double after;
            string afterVia;
            bool haveAfter = TryReadRudder(u, out after, out afterVia);
            data["after"] = haveAfter ? (JToken)Tok(after) : NullTok();
            data["readbackAfterVia"] = Tok(afterVia);
            bool verified = haveAfter && Math.Abs(after - angle) <= 0.01;
            data["verified"] = verified;
            data["verifiedNote"] = Tok(haveAfter
                ? (verified
                    ? "the set-point reads back as written: " + afterVia
                    : "the set-point does not read back as written (asked " + angle + ", read " + after
                      + "); this class stores something else")
                : "this object has no readable rudder set-point (no getSetRudderAngle and no _setRudderAngle), so verified is false even though the write ran");

            // The physical angle is integrated later by the type's own applyRudderThrust in
            // FixedUpdate; it is reported as an observation, never as the proof of the write.
            double current;
            string currentVia;
            bool haveCurrent = TryReadCurrentRudder(u, out current, out currentVia);
            data["currentRudderAngle"] = haveCurrent ? (JToken)Tok(current) : NullTok();
            data["currentRudderAngleVia"] = Tok(currentVia);
            data["turningObserved"] = haveCurrent && haveBefore && Math.Abs(current - before) > 0.0;

            if (Math.Abs(angle) > FullRudder)
                data["note"] = Tok("this hull's own full-rudder convention is +/-" + FullRudder
                    + " degrees (turnRudderLeft/Right clamp there, OrderSetCourse uses it as its threshold); "
                    + angle + " was stored as given, so the extra travel may not turn the hull any faster");
            return data;
        }

        // =====================================================================
        // telegraph: setTelegraph  (the engine order)
        // =====================================================================
        internal static JObject Telegraph(JObject req)
        {
            if (!Core.IsMain)
                throw new ApiException(500, "off_main_thread",
                    "telegraph re-issues the engine order and must run on the main thread", false);

            RejectUnknown(req, "telegraph", Api.ModuleAllowedFieldsFor("telegraph"));
            int id = UnitId(req);
            int telegraph = ReqInt(req, "telegraph");
            // REVIEW (round 2, hint): the route-level guard in Api.cs rejects the same field with
            // out_of_range before FindUnit ever runs, so this branch is the backstop for a direct
            // call - it must answer with the same code, or one field would have two identities.
            if (telegraph < -1)
                throw new ApiException(400, "out_of_range",
                    "telegraph: 'telegraph' must be >= -1 (-1 is the game's all-stop sentinel);"
                    + " a lower step does not exist", false);
            // Upper bound too: the route-level guard (Api.cs ReqIntIn 0..5) already refuses 6+,
            // but leaving the processor one-sided means a direct call would hand the game a
            // speed index it does not have. Defence in depth, same code, same field.
            if (telegraph > 5)
                throw new ApiException(400, "out_of_range",
                    "telegraph: 'telegraph' must be <= 5 (the game has six speed steps);"
                    + " got " + telegraph, false);
            bool validateOnly = OptBool(req, "validate_only", false);

            ObjectBase u = FindUnitGated(id);
            MethodInfo setter = RequireReal(u, "telegraph", "setTelegraph", OneInt);

            // Aircraft.setTelegraph stores a 0-based index: `_telegraph = telegraph > 0 ?
            // telegraph - 1 : 0` and then feeds ConstantMach(_speedValuesInMach[_telegraph]).
            // Every other override stores the number it was given. The read-back encoding has
            // to follow the DECLARING class or the verification is guaranteed to be wrong.
            bool index0 = setter.DeclaringType == typeof(Aircraft);
            int expected = index0 ? (telegraph > 0 ? telegraph - 1 : 0) : telegraph;

            int before;
            string beforeVia;
            bool beforeFromStub;
            bool haveBefore = TryReadTelegraph(u, out before, out beforeVia, out beforeFromStub);

            JObject data = new JObject();
            data["id"] = id;
            data["op"] = "telegraph";
            data["unitType"] = Tok(u.GetType().FullName);
            data["telegraph"] = telegraph;
            data["appliedOn"] = Tok(setter.DeclaringType.FullName);
            data["declaringIsRuntimeType"] = setter.DeclaringType == u.GetType();
            data["dispatch"] = Tok("runtime_type_methodinfo");
            data["readbackEncoding"] = Tok(index0 ? "index0" : "raw");
            data["expectedReadback"] = expected;
            data["before"] = haveBefore ? (JToken)new JValue(before) : NullTok();
            data["readbackVia"] = Tok(beforeVia);

            if (validateOnly)
            {
                data["validated"] = true;
                data["wouldCall"] = Tok(Describe(setter) + "(" + telegraph + ")");
                data["note"] = Tok("validate_only ran every gate (unit exists, " + setter.DeclaringType.Name
                    + ".setTelegraph is a real implementation, " + telegraph
                    + " is a legal step) and stopped before the write.");
                if (beforeFromStub)
                    data["readbackWarning"] = Tok(ReadbackStubWarning(u));
                return data;
            }

            Invoke(setter, u, new object[] { telegraph }, "telegraph");

            int after;
            string afterVia;
            bool afterFromStub;
            bool haveAfter = TryReadTelegraph(u, out after, out afterVia, out afterFromStub);
            data["after"] = haveAfter ? (JToken)new JValue(after) : NullTok();
            data["readbackAfterVia"] = Tok(afterVia);
            bool verified = haveAfter && after == expected;
            data["verified"] = verified;
            data["speedCommand"] = Tok(SpeedCommandType(u));

            if (afterFromStub)
            {
                // The write itself is real (Biologic overrides setTelegraph); the read-back is
                // the shell. Say so instead of letting verified:false look like a failed write.
                data["readbackFromStub"] = true;
                data["verifiedNote"] = Tok(ReadbackStubWarning(u));
            }
            else if (!haveAfter)
            {
                data["verifiedNote"] = Tok("this object exposes no readable engine order "
                    + "(no getTelegraph in the chain and no _telegraph field), so the write ran unverified");
            }
            else if (!verified)
            {
                data["verifiedNote"] = Tok("the engine order did not stick: asked " + telegraph
                    + " and the read-back " + afterVia + " reports " + after
                    + " (expected " + expected + " in the " + (index0 ? "index0" : "raw")
                    + " encoding). " + setter.DeclaringType.Name
                    + ".setTelegraph refuses such a step only in a Log.DebugWarning - a hull with a missing "
                    + "or too short speed table is ordered to all stop instead of being told no.");
            }
            else
            {
                data["verifiedNote"] = Tok("the order reads back as written via " + afterVia);
            }
            return data;
        }

        // =====================================================================
        // heading: steer to a compass heading by setting the rudder
        // =====================================================================
        internal static JObject Heading(JObject req)
        {
            if (!Core.IsMain)
                throw new ApiException(500, "off_main_thread",
                    "heading sets a rudder order from a course error and must run on the main thread", false);

            RejectUnknown(req, "heading", Api.ModuleAllowedFieldsFor("heading"));
            int id = UnitId(req);
            double wanted = ReqDouble(req, "heading");
            if (double.IsNaN(wanted) || double.IsInfinity(wanted))
                throw Bad("heading", "heading", "must be a finite number of degrees");
            double target = Wrap360(wanted);
            // REVIEW (round 2, hint): both range checks are false for NaN, so a NaN slipped into
            // RudderForHeading and into the native call. Finite first, range second.
            double maxRudder = OptDouble(req, "max_rudder", 25.0);
            if (double.IsNaN(maxRudder) || double.IsInfinity(maxRudder))
                throw Bad("heading", "max_rudder", "must be a finite number of degrees");
            if (maxRudder <= 0.0 || maxRudder > 90.0)
                throw Bad("heading", "max_rudder", "must be within (0, 90] degrees, got " + maxRudder);
            // Default 5.0, not 1.0: the value is a DIVISOR (r = delta / smoothness), so 1.0 applies
            // the whole course error as rudder on the first tick and overshoots on a slow hull.
            // The advertised contract in mcp_server.py has said "Default 5" since the tool shipped,
            // so the default was the half that was wrong - the docs and the code now agree, and the
            // gentler of the two is the safe one to pick.
            double smoothness = OptDouble(req, "smoothness", 5.0);
            if (double.IsNaN(smoothness) || double.IsInfinity(smoothness))
                throw Bad("heading", "smoothness", "must be a finite number");
            if (smoothness <= 0.0 || smoothness > 100.0)
                throw Bad("heading", "smoothness", "must be within (0, 100], got " + smoothness);
            bool validateOnly = OptBool(req, "validate_only", false);

            ObjectBase u = FindUnitGated(id);
            double current;
            string currentVia;
            bool haveCurrent = TryReadHeading(u, out current, out currentVia);
            if (!haveCurrent)
                throw new ApiException(409, "wrong_unit_type",
                    "heading: " + u.GetType().Name
                    + " exposes no readable heading (ObjectBase.getHeading() and the Heading mirror both came back empty)", false);
            double delta = Wrap180(target - current);

            MethodInfo call;
            object[] args;
            double expectedRudder;
            string law;

            // Vessel.SetRudderToHeading is a Vessel-ONLY method (non-virtual, no base
            // declaration), so it is only visible on the vessel chain - Submarine is not a
            // Vessel and Aircraft/Helicopter/LandUnit never see it. That asymmetry is exactly
            // why the resolution has to start at the runtime type.
            MethodInfo native = Effective(u, "SetRudderToHeading", ThreeFloat);
            if (native != null && !IsStub(native))
            {
                law = "SeaPower.Vessel.SetRudderToHeading (clamped proportional, snapped to +/-1 inside the dead band)";
                expectedRudder = RudderForHeading(delta, maxRudder, smoothness);
                call = native;
                args = new object[] { (float)delta, (float)maxRudder, (float)smoothness };
            }
            else
            {
                // Anything else that can steer at all: use the game's own course->rudder law
                // from OrderSetCourse.update, which is the code the game runs for a player or
                // AI "set course" order.
                MethodInfo setter = RequireReal(u, "heading", "setRudderAngle", OneFloat);
                law = "SeaPower.OrderSetCourse.update law (bang-bang: |delta| > max_rudder -> +/-max_rudder, else truncate(delta))";
                expectedRudder = RudderForCourse(delta, maxRudder);
                call = setter;
                args = new object[] { (float)expectedRudder };
            }

            double before;
            string beforeVia;
            bool haveBefore = TryReadRudder(u, out before, out beforeVia);

            JObject data = new JObject();
            data["id"] = id;
            data["op"] = "heading";
            data["unitType"] = Tok(u.GetType().FullName);
            data["heading"] = Tok(target);
            if (Math.Abs(target - wanted) > 0.000001)
                data["headingNormalized"] = Tok("wrapped into 0..360; requested " + wanted);
            data["headingBefore"] = Tok(current);
            data["headingBeforeVia"] = Tok(currentVia);
            data["deltaHeading"] = Tok(delta);
            data["law"] = Tok(law);
            data["expectedRudder"] = Tok(expectedRudder);
            data["appliedOn"] = Tok(call.DeclaringType.FullName);
            data["declaringIsRuntimeType"] = call.DeclaringType == u.GetType();
            data["rudderBefore"] = haveBefore ? (JToken)Tok(before) : NullTok();

            if (validateOnly)
            {
                data["validated"] = true;
                data["wouldCall"] = Tok(Describe(call) + "(" + ExpectedArgs(args) + ")");
                data["note"] = Tok("validate_only computed the rudder order for a "
                    + Round(delta) + " degree course error and stopped before the write.");
                return data;
            }

            Invoke(call, u, args, "heading");

            double after;
            string afterVia;
            bool haveAfter = TryReadRudder(u, out after, out afterVia);
            data["rudderAfter"] = haveAfter ? (JToken)Tok(after) : NullTok();
            data["readbackVia"] = Tok(afterVia);
            bool verified = haveAfter && Math.Abs(after - expectedRudder) <= 0.01;
            data["verified"] = verified;

            // The hull does not rotate inside this call: the rudder is a set-point and
            // applyRudderThrust integrates it in FixedUpdate. Re-reading the heading now would
            // look like "nothing happened", so the heading change is reported as unverified.
            double headingNow;
            string headingNowVia;
            bool haveNow = TryReadHeading(u, out headingNow, out headingNowVia);
            data["headingAfter"] = haveNow ? (JToken)Tok(headingNow) : NullTok();
            data["headingChangeVerified"] = false;
            data["verifiedNote"] = Tok(verified
                ? "only the rudder order is proven (it reads back as " + after + " via " + afterVia
                  + "). The hull turns later: applyRudderThrust rotates the transform in FixedUpdate at the "
                  + "hull's own turn rate, so a heading change cannot be confirmed inside one request."
                : "the rudder order does not read back as computed (expected " + expectedRudder
                  + ", read " + (haveAfter ? Round(after).ToString() : "nothing") + ")");
            return data;
        }

        // =====================================================================
        // runtime-type dispatcher
        // =====================================================================

        // The most-derived declaration of 'name' on the instance's real type, walking up by
        // hand. null means no type in the chain declares it at all. The caller decides what a
        // base-class shell means - this function never hides which class it found.
        private static MethodInfo Effective(object inst, string name, Type[] argTypes)
        {
            if (inst == null) return null;
            Type t = inst.GetType();
            while (t != null)
            {
                MethodInfo mi = Pick(t, name, argTypes);
                if (mi != null) return mi;
                // Stop at the game's own root: above ObjectBase there is only Unity plumbing,
                // and reporting a MonoBehaviour member as "the implementation" would be a lie.
                if (t == typeof(ObjectBase)) break;
                t = t.BaseType;
            }
            return null;
        }

        private static MethodInfo Pick(Type t, string name, Type[] argTypes)
        {
            MethodInfo[] methods;
            try { methods = t.GetMethods(Declared); }
            catch (Exception) { return null; }
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo mi = methods[i];
                if (mi.Name != name) continue;
                ParameterInfo[] ps = mi.GetParameters();
                if (ps.Length != argTypes.Length) continue;
                bool fits = true;
                for (int k = 0; k < ps.Length; k++)
                {
                    if (ps[k].ParameterType.IsAssignableFrom(argTypes[k])) continue;
                    fits = false;
                    break;
                }
                if (fits) return mi;
            }
            return null;
        }

        // True when the body does nothing and returns a constant: `ret`, `ldc.i4.0 / ret`,
        // `ldc.i4.m1 / ret`, `ldnull / ret`. This is a read of the real IL, not a name list,
        // so it keeps working when a future build adds or removes an override.
        private static bool IsStub(MethodInfo mi)
        {
            if (mi == null) return false;
            byte[] il;
            try
            {
                MethodBody body = mi.GetMethodBody();
                if (body == null) return false;          // abstract / extern: not this defect
                il = body.GetILAsByteArray();
            }
            catch (Exception) { return false; }
            if (il == null) return false;
            if (il.Length == 1 && il[0] == 0x2A) return true;                       // ret
            if (il.Length == 2 && il[1] == 0x2A)
            {
                byte op = il[0];
                if (op == 0x14) return true;                                        // ldnull
                if (op == 0x15) return true;                                        // ldc.i4.m1
                if (op == 0x16) return true;                                        // ldc.i4.0
                if (op >= 0x17 && op <= 0x1E) return true;                          // ldc.i4.1..8
            }
            return false;
        }

        private static string Il(MethodInfo mi)
        {
            if (mi == null) return "(no declaration)";
            try
            {
                MethodBody body = mi.GetMethodBody();
                if (body == null) return "(no managed body)";
                byte[] il = body.GetILAsByteArray();
                if (il == null || il.Length == 0) return "(empty body)";
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < il.Length; i++)
                {
                    if (i > 0) sb.Append(' ');
                    sb.Append(il[i].ToString("X2"));
                }
                return sb.ToString();
            }
            catch (Exception) { return "(unreadable)"; }
        }

        // The gate this whole file turns on: resolve on the runtime type, then refuse a shell
        // instead of writing into it. Deliberately no fallback to "just call the base".
        private static MethodInfo RequireReal(object inst, string op, string name, Type[] argTypes)
        {
            MethodInfo mi = Effective(inst, name, argTypes);
            string runtime = inst == null ? "(null)" : inst.GetType().FullName;
            if (mi == null)
                throw new ApiException(409, "wrong_unit_type",
                    op + ": " + runtime + " declares no '" + name + "' and neither does any type above it, "
                    + "so this operation has no implementation to run on it", false);
            if (IsStub(mi))
                throw new ApiException(409, "wrong_unit_type",
                    op + ": " + runtime + " resolves '" + name + "' to " + Describe(mi)
                    + ", whose body is an empty shell (IL: " + Il(mi)
                    + "). The declaration exists but writes nothing, and no type in this chain overrides it - "
                    + "calling it would answer 200 and change nothing. Refused before the write.", false);
            return mi;
        }

        private static string Describe(MethodInfo mi)
        {
            if (mi == null) return "(none)";
            return mi.DeclaringType.FullName + "." + mi.Name;
        }

        private static string ExpectedArgs(object[] args)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Convert.ToString(args[i], System.Globalization.CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static object Invoke(MethodInfo mi, object inst, object[] args, string op)
        {
            try
            {
                return mi.Invoke(inst, args);
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException == null ? ex : ex.InnerException;
                throw new ApiException(400, "bad_value",
                    op + ": the game threw " + inner.GetType().Name + ": " + inner.Message
                    + " inside " + Describe(mi) + "; the state after that throw is not guaranteed to be the state "
                    + "before it", false);
            }
            catch (Exception ex2)
            {
                throw new ApiException(400, "bad_value",
                    op + ": could not run " + Describe(mi) + ": " + ex2.Message, false);
            }
        }

        // =====================================================================
        // read-backs - each one follows the runtime type, because there is nothing to inherit
        // =====================================================================

        // getSetRudderAngle is declared three times (Vessel, Submarine, Biologic) and nowhere
        // else, so this read is the reason Effective() exists on the read side too.
        private static bool TryReadRudder(object inst, out double value, out string via)
        {
            value = 0.0;
            via = null;
            MethodInfo getter = Effective(inst, "getSetRudderAngle", NoArg);
            if (getter != null && !IsStub(getter))
            {
                object r = InvokeQuiet(getter, inst);
                if (r != null)
                {
                    value = Convert.ToDouble(r, System.Globalization.CultureInfo.InvariantCulture);
                    via = Describe(getter);
                    return true;
                }
            }
            object f = GameReflect.Member(inst, "_setRudderAngle");
            if (f != null)
            {
                value = GameReflect.Num(f, 0.0);
                via = "field _setRudderAngle";
                return true;
            }
            return false;
        }

        // The physical, lagged angle. Reported for context only; never used to verify a write.
        private static bool TryReadCurrentRudder(object inst, out double value, out string via)
        {
            value = 0.0;
            via = null;
            MethodInfo getter = Effective(inst, "getCurrentRudderAngle", NoArg);
            if (getter != null && !IsStub(getter))
            {
                object r = InvokeQuiet(getter, inst);
                if (r != null)
                {
                    value = Convert.ToDouble(r, System.Globalization.CultureInfo.InvariantCulture);
                    via = Describe(getter);
                    return true;
                }
            }
            return false;
        }

        // Biologic is the trap: it overrides setTelegraph but NOT getTelegraph, so the honest
        // path is to notice the shell and read the backing field the game itself keeps.
        private static bool TryReadTelegraph(object inst, out int value, out string via, out bool fromStub)
        {
            value = 0;
            via = null;
            fromStub = false;
            MethodInfo getter = Effective(inst, "getTelegraph", NoArg);
            if (getter != null && !IsStub(getter))
            {
                object r = InvokeQuiet(getter, inst);
                if (r != null)
                {
                    value = Convert.ToInt32(r, System.Globalization.CultureInfo.InvariantCulture);
                    via = Describe(getter);
                    return true;
                }
            }
            object f = GameReflect.Member(inst, "_telegraph");
            if (f != null)
            {
                value = (int)Math.Round(GameReflect.Num(f, 0.0));
                via = "field _telegraph";
                fromStub = getter != null;
                return true;
            }
            return false;
        }

        private static string ReadbackStubWarning(ObjectBase u)
        {
            MethodInfo getter = Effective(u, "getTelegraph", NoArg);
            string detail = getter == null
                ? "no getTelegraph in this chain"
                : Describe(getter) + " is an empty shell (IL: " + Il(getter) + ")";
            return "read back from the _telegraph field instead of the getter: " + detail
                + ". " + u.GetType().Name
                + ".setTelegraph is a real override, so the order itself was applied; only the getter lies.";
        }

        // OrderSetCourse.update compares _newCourse against ObjectBase.getHeading() (IL_0006
        // callvirt ObjectBase::getHeading), so getHeading() - not the Heading mirror - is the
        // measurement that pairs with a course number. The mirror is reported beside it.
        private static bool TryReadHeading(object inst, out double value, out string via)
        {
            value = 0.0;
            via = null;
            MethodInfo getter = Effective(inst, "getHeading", NoArg);
            if (getter != null && !IsStub(getter))
            {
                object r = InvokeQuiet(getter, inst);
                if (r != null)
                {
                    value = Convert.ToDouble(r, System.Globalization.CultureInfo.InvariantCulture);
                    via = Describe(getter);
                    return true;
                }
            }
            object h = GameReflect.Member(inst, "Heading");
            if (h != null)
            {
                value = GameReflect.Num(h, 0.0);
                via = "property Heading (reactive mirror)";
                return true;
            }
            return false;
        }

        private static object InvokeQuiet(MethodInfo mi, object inst)
        {
            try { return mi.Invoke(inst, new object[0]); }
            catch (Exception) { return null; }
        }

        private static string SpeedCommandType(ObjectBase u)
        {
            try
            {
                object sc = GameReflect.Member(u, "SpeedCommand");
                if (sc == null) return null;
                object val = GameReflect.Member(sc, "Value");
                object real = val == null ? sc : val;
                return real.GetType().Name;
            }
            catch (Exception) { return null; }
        }

        // =====================================================================
        // the game's own steering laws, replicated from IL
        // =====================================================================

        // Vessel.SetRudderToHeading(deltaHeading, maxRudderAngle, smoothness):
        //   r = Mathf.Clamp(deltaHeading / smoothness, -max, +max)
        //   if (0.25 < |r| < 1) r = Mathf.Sign(r)     // IL_0024..IL_0045 dead-band snap
        // Then stfld Vessel::_setRudderAngle.
        private static double RudderForHeading(double deltaHeading, double maxRudder, double smoothness)
        {
            double r = deltaHeading / smoothness;
            if (r < -maxRudder) r = -maxRudder;
            if (r > maxRudder) r = maxRudder;
            double a = Math.Abs(r);
            if (a > 0.25 && a < 1.0) r = r < 0.0 ? -1.0 : 1.0;
            return r;
        }

        // OrderSetCourse.update:
        //   IL_0014: V1 = _newCourse - getHeading()
        //   IL_0024..IL_0045: wrap V1 into (-180, 180]
        //   IL_0054: ldc.r4 25 ; IL_0059: |delta| <= 25 ?
        //   IL_0087..IL_0095: setRudderAngle((float)(int)V1)   <- conv.i4 = truncate
        //   IL_0069 / IL_007B: setRudderAngle(-25) / setRudderAngle(25)
        private static double RudderForCourse(double delta, double maxRudder)
        {
            if (Math.Abs(delta) > maxRudder) return delta < 0.0 ? -maxRudder : maxRudder;
            return Math.Truncate(delta);
        }

        private static double Wrap360(double deg)
        {
            double d = deg % 360.0;
            if (d < 0.0) d += 360.0;
            return d;
        }

        private static double Wrap180(double deg)
        {
            double d = Wrap360(deg);
            if (d > 180.0) d -= 360.0;
            return d;
        }

        // =====================================================================
        // addressing + request shape
        // =====================================================================

        // Same walk as Api.FindUnit, and the same intel gate Api.UnitCommand applies before it
        // dispatches: without it a new write route would answer for units the player cannot see.
        private static ObjectBase FindUnitGated(int id)
        {
            List<ObjectBase> all = Api.AllObjects();
            for (int i = 0; i < all.Count; i++)
            {
                ObjectBase u = all[i];
                if (u == null || u.UniqueID != id) continue;
                if (!IntelScope.PlayerKnows(u))
                    throw new ApiException(404, "unit_not_found", "unit not found: " + id, false);
                return u;
            }
            throw new ApiException(404, "unit_not_found", "unit not found: " + id, false);
        }

        private static void RejectUnknown(JObject req, string op, string[] allowed)
        {
            if (req == null) return;
            // ModuleAllowedFieldsFor answers null for an op it does not know; say 400 instead
            // of letting a drifted op string become an NRE 500.
            if (allowed == null)
                throw new ApiException(400, "unknown_field",
                    "op '" + op + "' has no field list", false);
            foreach (JProperty p in req.Properties())
            {
                bool ok = false;
                for (int i = 0; i < allowed.Length; i++)
                {
                    if (allowed[i] == p.Name) { ok = true; break; }
                }
                if (!ok)
                    throw new ApiException(400, "unknown_field",
                        "op '" + op + "' does not accept field '" + p.Name + "'", false);
            }
        }

        private static JToken Field(JObject req, string key)
        {
            if (req == null) return null;
            return req[key];
        }

        // 'unit' is the field the existing write ops use (Sonobuoy.Drop receives req["unit"]);
        // 'id' is accepted as an alias so a caller that has the route id handy need not rename it.
        private static int UnitId(JObject req)
        {
            // One address only. Api.cs writes the path id into "unit" before dispatch, so this
            // reads a value the route already proved it had - it is not a second way in.
            JToken a = Field(req, "unit");
            if (a == null)
                throw new ApiException(400, "missing_field",
                    "'unit' is required: the unit id must come from the path", false);
            return IntToken("unit", a);
        }

        private static int IntToken(string key, JToken t)
        {
            if (t.Type == JTokenType.Null)
                throw new ApiException(400, "bad_type",
                    "'" + key + "' was explicitly null; a unit id is required here", false);
            if (t.Type != JTokenType.Integer)
                throw new ApiException(400, "bad_type",
                    "'" + key + "' must be an integer unit id, got " + t.Type, false);
            long v = t.Value<long>();
            if (v < int.MinValue || v > int.MaxValue)
                throw new ApiException(400, "bad_value", "'" + key + "' is out of range", false);
            return (int)v;
        }

        private static double ReqDouble(JObject req, string key)
        {
            JToken t = Field(req, key);
            if (t == null)
                throw new ApiException(400, "missing_field", "'" + key + "' is required", false);
            return Number(key, t);
        }

        private static double OptDouble(JObject req, string key, double fallback)
        {
            JToken t = Field(req, key);
            if (t == null) return fallback;
            return Number(key, t);
        }

        private static double Number(string key, JToken t)
        {
            if (t.Type == JTokenType.Null)
                throw new ApiException(400, "bad_type",
                    "'" + key + "' was explicitly null; omit it to take the default", false);
            if (t.Type != JTokenType.Integer && t.Type != JTokenType.Float)
                throw new ApiException(400, "bad_type", "'" + key + "' must be a number, got " + t.Type, false);
            return t.Value<double>();
        }

        private static int ReqInt(JObject req, string key)
        {
            JToken t = Field(req, key);
            if (t == null)
                throw new ApiException(400, "missing_field", "'" + key + "' is required", false);
            if (t.Type == JTokenType.Null)
                throw new ApiException(400, "bad_type",
                    "'" + key + "' was explicitly null; omit it to take the default", false);
            if (t.Type != JTokenType.Integer)
                throw new ApiException(400, "bad_type", "'" + key + "' must be an integer, got " + t.Type, false);
            long v = t.Value<long>();
            if (v < int.MinValue || v > int.MaxValue)
                throw new ApiException(400, "bad_value", "'" + key + "' is out of range", false);
            return (int)v;
        }

        private static bool OptBool(JObject req, string key, bool fallback)
        {
            JToken t = Field(req, key);
            if (t == null) return fallback;
            if (t.Type == JTokenType.Null)
                throw new ApiException(400, "bad_type",
                    "'" + key + "' was explicitly null; omit it to take the default", false);
            if (t.Type != JTokenType.Boolean)
                throw new ApiException(400, "bad_type", "'" + key + "' must be a boolean, got " + t.Type, false);
            return t.Value<bool>();
        }

        private static ApiException Bad(string op, string field, string why)
        {
            return new ApiException(400, "bad_value", op + ": '" + field + "' " + why, false);
        }

        private static JToken NullTok() { return JValue.CreateNull(); }

        private static JToken Tok(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return JValue.CreateNull();
            return new JValue(Round(v));
        }

        private static JToken Tok(string s)
        {
            if (s == null) return JValue.CreateNull();
            return new JValue(s);
        }

        private static double Round(double v)
        {
            return Math.Round(v, 5);
        }
    }
}
