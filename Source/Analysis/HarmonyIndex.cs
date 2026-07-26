using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// A reverse index of every Harmony-patched method to the mods (Harmony owner ids) that patch it.
    /// Built once from HarmonyLib's live patch registry, so it reveals the mods HIDING behind a vanilla
    /// frame: when an error passes through a vanilla method that other mods have patched, the trace
    /// attributes only to RimWorld, but the real culprit is often one of the patchers. Keyed by
    /// "DeclaringType.FullName:MethodName" (overloads folded together) to match how a stack frame reads.
    ///
    /// Built lazily on the first error analysis - NOT in the startup prewarm - because mods apply their
    /// patches from their own [StaticConstructorOnStartup] which may run after ours; by the time any
    /// error is inspected in-game every startup patch is in place.
    /// </summary>
    public static class HarmonyIndex
    {
        public const string SelfId = "astryl.moderndevtools";

        private static Dictionary<string, string[]> _byMethod;
        private static readonly object _lock = new object();

        public static bool Built => _byMethod != null;

        public static void EnsureBuilt()
        {
            if (_byMethod != null) return;
            lock (_lock) { if (_byMethod == null) _byMethod = Build(); }
        }

        /// <summary>Drop the cache (e.g. if patches were added at runtime). Modlists don't normally
        /// change mid-session, so this is rarely needed.</summary>
        public static void Invalidate() { lock (_lock) _byMethod = null; }

        private static Dictionary<string, string[]> Build()
        {
            var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            try
            {
                foreach (MethodBase method in Harmony.GetAllPatchedMethods())
                {
                    if (method?.DeclaringType == null) continue;
                    Patches info;
                    try { info = Harmony.GetPatchInfo(method); }
                    catch { continue; }
                    var owners = info?.Owners;
                    if (owners == null || owners.Count == 0) continue;

                    string type = method.DeclaringType.FullName;
                    if (type.NullOrEmpty()) continue;
                    string key = type + ":" + method.Name;
                    if (!map.TryGetValue(key, out var set))
                        map[key] = set = new HashSet<string>(StringComparer.Ordinal);
                    foreach (string o in owners)
                        if (!o.NullOrEmpty()) set.Add(o);
                }
            }
            catch (Exception e)
            {
                Log.WarningOnce("[Modern Dev Tools] Harmony index build failed: " + e.Message, 0x2E19D01);
            }

            var result = new Dictionary<string, string[]>(map.Count, StringComparer.Ordinal);
            foreach (var kv in map)
            {
                var arr = new string[kv.Value.Count];
                kv.Value.CopyTo(arr);
                result[kv.Key] = arr;
            }
            return result;
        }

        /// <summary>The Harmony owner ids that patch the given type.method, or null when it is unpatched.</summary>
        public static string[] OwnersFor(string typeFullName, string methodName)
        {
            if (typeFullName.NullOrEmpty() || methodName.NullOrEmpty()) return null;
            EnsureBuilt();
            return _byMethod != null && _byMethod.TryGetValue(typeFullName + ":" + methodName, out var owners)
                ? owners
                : null;
        }
    }
}
