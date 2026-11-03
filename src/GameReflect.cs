// Shared reflection helpers for the map work. Three planned files (MapView, MapUnits,
// MapPlot) each need to touch game internals that are private-ish, and copying the same
// six helpers into three files is how they drift apart: one copy gets the base-class walk
// fixed and the other two keep failing. So they all come through here.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace SeaPowerAgentBridge
{
    internal static class GameReflect
    {
        private const BindingFlags Inst = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance;

        private const BindingFlags Stat = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Static;

        private static readonly Dictionary<string, Type> _typeCache =
            new Dictionary<string, Type>(StringComparer.Ordinal);
        /// <summary>Type by full name, falling back to a short name and to a dotted
        /// suffix.
        ///
        /// The fallback is not a convenience: the first live run of the plot table asked
        /// for "GameMain" when the type is "SeaPower.GameMain", GetType returned null,
        /// and every unit lookup silently produced an empty list - a whole route read as
        /// "no units" while /state happily counted nineteen. A miss here is invisible at
        /// the call site, so resolution is now generous and cached.
        /// </summary>
        // ---------------------------------------------------------------- name index
        //
        // Why this exists: a MISS in FindType walks GetTypes() over every loaded assembly, and
        // Unity assemblies make that expensive. The first /plot/drawings call on a fresh boot
        // took over twenty seconds with the game frozen and the HTTP client giving up at its
        // own timeout - the reply was written to a closed socket. Short names ("SceneMapViewModel"
        // rather than "SeapowerUI.ViewModels.SceneMapViewModel") always take the slow path, so the
        // cost is paid per distinct name, not per call.
        //
        // One pass over the assemblies builds a name -> Type index; after that a lookup is a
        // dictionary hit even for a name that does not exist. GetTypes() reads metadata only, so
        // building it on a background thread is safe and the freeze moves to load time.
        private static volatile bool _indexed;
        private static Dictionary<string, Type> _byName;
        private static Dictionary<string, Type> _byFullName;
        private static readonly object _indexLock = new object();

        internal static void BuildIndex()
        {
            lock (_indexLock)
            {
                if (_indexed) return;
                Dictionary<string, Type> byName = new Dictionary<string, Type>(StringComparer.Ordinal);
                Dictionary<string, Type> byFull = new Dictionary<string, Type>(StringComparer.Ordinal);
                Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
                int loaded = 0;
                for (int i = 0; i < asms.Length; i++)
                {
                    Type[] all;
                    try { all = asms[i].GetTypes(); }
                    catch (Exception) { continue; }
                    loaded++;
                    for (int k = 0; k < all.Length; k++)
                    {
                        Type t = all[k];
                        if (t == null) continue;
                        // First wins, which is the same rule the old suffix scan used (assembly
                        // order). A caller that wants a specific one passes the full name.
                        if (!byName.ContainsKey(t.Name)) byName[t.Name] = t;
                        string fn = t.FullName;
                        if (fn != null && !byFull.ContainsKey(fn)) byFull[fn] = t;
                    }
                }
                _byName = byName;
                _byFullName = byFull;
                _indexed = true;
                Core.Log("type index built: " + byName.Count + " names from " + loaded + " assemblies");
            }
        }

        internal static bool Indexed { get { return _indexed; } }

        internal static int IndexedNameCount { get { return _byName == null ? 0 : _byName.Count; } }

        internal static Type FindType(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            Type cached;
            if (_typeCache.TryGetValue(fullName, out cached)) return cached;

            // Fast path once the index exists: no assembly walk at all, including for a miss.
            if (_indexed)
            {
                Type byFull;
                if (_byFullName.TryGetValue(fullName, out byFull) && byFull != null)
                {
                    _typeCache[fullName] = byFull;
                    return byFull;
                }
                Type byShort;
                if (_byName.TryGetValue(fullName, out byShort) && byShort != null)
                {
                    _typeCache[fullName] = byShort;
                    return byShort;
                }
                _typeCache[fullName] = null;
                return null;
            }

            Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
            Type hit = null;
            for (int i = 0; i < asms.Length && hit == null; i++)
            {
                try { hit = asms[i].GetType(fullName, false); }
                catch (Exception) { }
            }
            if (hit == null)
            {
                string suffix = "." + fullName;
                for (int i = 0; i < asms.Length && hit == null; i++)
                {
                    Type[] all;
                    try { all = asms[i].GetTypes(); }
                    catch (Exception) { continue; }
                    for (int k = 0; k < all.Length; k++)
                    {
                        Type t = all[k];
                        if (t == null) continue;
                        if (t.Name == fullName
                            || (t.FullName != null && t.FullName.EndsWith(suffix, StringComparison.Ordinal)))
                        {
                            hit = t;
                            break;
                        }
                    }
                }
            }
            _typeCache[fullName] = hit;
            return hit;
        }

        /// <summary>Field or property, walking up the base types. Null when absent or throwing.</summary>
        internal static object Member(object holder, string name)
        {
            if (holder == null || name == null) return null;
            try
            {
                Type t = holder.GetType();
                while (t != null)
                {
                    FieldInfo f = t.GetField(name, Inst);
                    if (f != null) return f.GetValue(holder);
                    PropertyInfo p = t.GetProperty(name, Inst);
                    if (p != null && p.CanRead) return p.GetValue(holder, null);
                    t = t.BaseType;
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>
        /// Same as Call, but hands back why the game refused. Call collapses "the game said no"
        /// and "the game threw" into one null; the weapon and sonobuoy paths need them apart, and
        /// they need the same rule, which is why this lives here and not in two callers.
        /// </summary>
        internal static object CallWithError(object holder, string name, object[] args,
            out string error)
        {
            error = null;
            if (holder == null) { error = "the target object is gone"; return null; }
            object[] safe = args == null ? new object[0] : args;
            try
            {
                MethodInfo[] methods = holder.GetType().GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo m = methods[i];
                    if (m.Name != name) continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length != safe.Length) continue;
                    bool ok = true;
                    for (int p = 0; p < ps.Length && ok; p++)
                    {
                        if (safe[p] == null) { ok = !ps[p].ParameterType.IsValueType; continue; }
                        ok = ps[p].ParameterType.IsAssignableFrom(safe[p].GetType());
                    }
                    if (!ok) continue;
                    try { return m.Invoke(holder, safe); }
                    catch (Exception ex)
                    {
                        Exception inner = ex.InnerException != null ? ex.InnerException : ex;
                        error = inner.GetType().Name + ": " + inner.Message;
                        return null;
                    }
                }
                error = "no overload of " + name + " takes " + safe.Length + " argument(s)";
            }
            catch (Exception ex) { error = ex.Message; }
            return null;
        }

        /// <summary>Static field or property on a named type.</summary>
        internal static object StaticMember(string typeName, string name)
        {
            Type t = FindType(typeName);
            if (t == null) return null;
            try
            {
                FieldInfo f = t.GetField(name, Stat);
                if (f != null) return f.GetValue(null);
                PropertyInfo p = t.GetProperty(name, Stat);
                if (p != null && p.CanRead) return p.GetValue(null, null);
            }
            catch (Exception) { }
            return null;
        }

        internal static bool SetMember(object holder, string name, object value)
        {
            if (holder == null || name == null) return false;
            try
            {
                Type t = holder.GetType();
                while (t != null)
                {
                    FieldInfo f = t.GetField(name, Inst);
                    if (f != null) { f.SetValue(holder, value); return true; }
                    PropertyInfo p = t.GetProperty(name, Inst);
                    if (p != null && p.CanWrite) { p.SetValue(holder, value, null); return true; }
                    t = t.BaseType;
                }
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>
        /// Invoke an instance method by name. Overloads are picked by argument count and
        /// then by "can this null/value be accepted", first match wins; the game types we
        /// touch do not have same-arity overloads that would make that ambiguous.
        /// </summary>
        internal static object Call(object holder, string name, params object[] args)
        {
            if (holder == null || name == null) return null;
            object[] safe = args == null ? new object[0] : args;
            MethodInfo[] methods = holder.GetType().GetMethods(Inst);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo m = methods[i];
                if (m.Name != name) continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length != safe.Length) continue;
                bool ok = true;
                for (int p = 0; p < ps.Length && ok; p++)
                {
                    if (safe[p] == null) { ok = !ps[p].ParameterType.IsValueType; continue; }
                    ok = ps[p].ParameterType.IsAssignableFrom(safe[p].GetType());
                }
                if (!ok) continue;
                try { return m.Invoke(holder, safe); }
                catch (Exception) { return null; }
            }
            return null;
        }

        /// <summary>Invoke a static method by type name + method name.</summary>
        internal static object CallStatic(string typeName, string name, params object[] args)
        {
            Type t = FindType(typeName);
            if (t == null) return null;
            object[] safe = args == null ? new object[0] : args;
            MethodInfo[] methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo m = methods[i];
                if (m.Name != name) continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length != safe.Length) continue;
                bool ok = true;
                for (int p = 0; p < ps.Length && ok; p++)
                {
                    if (safe[p] == null) { ok = !ps[p].ParameterType.IsValueType; continue; }
                    ok = ps[p].ParameterType.IsAssignableFrom(safe[p].GetType());
                }
                if (!ok) continue;
                try { return m.Invoke(null, safe); }
                catch (Exception) { return null; }
            }
            return null;
        }

        /// <summary>
        /// JSON wants a number or null. Anything numeric becomes a number, anything else
        /// becomes its string form, so a probe never silently drops a value.
        /// </summary>
        internal static JToken Raw(object v)
        {
            if (v == null) return JValue.CreateNull();
            if (v is double || v is float || v is int || v is long || v is bool || v is string)
                return JToken.FromObject(v);
            return new JValue(v.ToString());
        }

        internal static double Num(object v, double fallback)
        {
            if (v == null) return fallback;
            try
            {
                if (v is float) return (double)(float)v;
                if (v is double) return (double)v;
                if (v is int) return (double)(int)v;
                if (v is long) return (double)(long)v;
                return Convert.ToDouble(v);
            }
            catch (Exception) { return fallback; }
        }

        internal static bool Truth(object v)
        {
            if (v == null) return false;
            if (v is bool) return (bool)v;
            return false;
        }

        /// <summary>
        /// A GeoPosition built through the game's own constructor, so callers never have
        /// to care that its fields are private. Height defaults to sea level.
        /// </summary>
        internal static object Geo(double lat, double lon, double height)
        {
            Type t = FindType("SeaPower.GeoPosition");
            if (t == null) return null;
            try
            {
                ConstructorInfo c = t.GetConstructor(new Type[] { typeof(double), typeof(double), typeof(double) });
                if (c != null) return c.Invoke(new object[] { lat, lon, height });
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>
        /// Every live ObjectBase, straight out of GameMain._objects. Api.cs already reads
        /// that field for /units; this is the same source so a plot command and a /units
        /// read can never disagree about which objects exist.
        /// </summary>
        internal static List<object> Units()
        {
            List<object> result = new List<object>();
            // Same list /units reads. The earlier version went through reflection
            // (StaticMember("GameMain","Instance") -> Member(gm,"_objects")) and quietly returned
            // nothing: /units listed nineteen objects while /map/units reported zero and
            // /plot/course answered 404 unit_not_found for a unit that exists. Delegating means
            // there is one answer to "which objects exist".
            List<SeaPower.ObjectBase> src = Api.AllObjects();
            if (src == null) return result;
            for (int i = 0; i < src.Count; i++)
            {
                if (src[i] != null) result.Add(src[i]);
            }
            return result;
        }

        /// <summary>Null when there is no such unit; callers decide whether that is a 404.</summary>
        internal static object UnitById(int id)
        {
            List<object> all = Units();
            for (int i = 0; i < all.Count; i++)
            {
                if ((int)Num(Member(all[i], "UniqueID"), -1) == id) return all[i];
            }
            return null;
        }

        internal static double Lat(object geo) { return Num(Member(geo, "Latitude"), double.NaN); }

        internal static double Lon(object geo) { return Num(Member(geo, "Longitude"), double.NaN); }
    }
}
