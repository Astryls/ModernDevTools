using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// A proactive, error-independent scan of every ACTIVE mod's declared requirements and load order,
    /// computed from About.xml + ModsConfig exactly the way vanilla computes its mod-list warnings. Unmet
    /// dependencies and load-order violations are the single most common root cause of the "could not
    /// find def/type", "could not load reference" and "cross-reference" cascades - and unlike those
    /// symptom errors, the cause is knowable up front without any error at all. Built once (the modlist
    /// can't change mid-session) and cached.
    /// </summary>
    public static class ModDependencyIndex
    {
        public struct MissingDep
        {
            public string Name;
            public string PackageId;
            public string Url;
            public bool Installed;   // present but inactive (vs not installed at all)
        }

        public class ModProblems
        {
            public string ModName;
            public string PackageId;
            public readonly List<MissingDep> Missing = new List<MissingDep>();
            public readonly List<string> LoadOrder = new List<string>();   // localized, matches the mod-list text
            public bool Any => Missing.Count > 0 || LoadOrder.Count > 0;
        }

        private static Dictionary<string, ModProblems> _byPid;
        private static List<ModProblems> _all;

        public static List<ModProblems> All { get { EnsureBuilt(); return _all; } }
        public static bool AnyProblems { get { EnsureBuilt(); return _all.Count > 0; } }

        public static ModProblems For(string packageId)
        {
            EnsureBuilt();
            return !packageId.NullOrEmpty() && _byPid.TryGetValue(packageId, out var p) ? p : null;
        }

        public static void Invalidate() { _byPid = null; _all = null; }

        private static void EnsureBuilt()
        {
            if (_byPid != null) return;
            Build();
        }

        private static void Build()
        {
            var byPid = new Dictionary<string, ModProblems>(StringComparer.OrdinalIgnoreCase);
            var all = new List<ModProblems>();
            try
            {
                List<ModMetaData> mods = ModsConfig.ActiveModsInLoadOrder.Where(m => m != null).ToList();
                var indexOf = new Dictionary<ModMetaData, int>();
                for (int i = 0; i < mods.Count; i++) indexOf[mods[i]] = i;

                for (int i = 0; i < mods.Count; i++)
                {
                    ModMetaData m = mods[i];
                    var prob = new ModProblems { ModName = m.Name, PackageId = m.PackageId };

                    if (m.Dependencies != null)
                    {
                        foreach (ModDependency dep in m.Dependencies)
                        {
                            if (dep == null || dep.IsSatisfied) continue;
                            ModMetaData installed = !dep.packageId.NullOrEmpty()
                                ? ModLister.GetModWithIdentifier(dep.packageId, ignorePostfix: true)
                                : null;
                            prob.Missing.Add(new MissingDep
                            {
                                Name = dep.displayName.NullOrEmpty() ? dep.packageId : dep.displayName,
                                PackageId = dep.packageId,
                                Url = !dep.steamWorkshopUrl.NullOrEmpty() ? dep.steamWorkshopUrl : dep.downloadUrl,
                                Installed = installed != null
                            });
                        }
                    }

                    // Mirror ModsConfig.GetModWarnings' FindConflicts, per direction.
                    AddOrder(prob, m.LoadBefore, indexOf, i, mustLoadBefore: true);
                    AddOrder(prob, m.ForceLoadBefore, indexOf, i, mustLoadBefore: true);
                    AddOrder(prob, m.LoadAfter, indexOf, i, mustLoadBefore: false);
                    AddOrder(prob, m.ForceLoadAfter, indexOf, i, mustLoadBefore: false);

                    if (prob.Any)
                    {
                        if (!m.PackageId.NullOrEmpty() && !byPid.ContainsKey(m.PackageId)) byPid[m.PackageId] = prob;
                        all.Add(prob);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warning("[Advanced Dev Tools] dependency scan failed: " + e.Message);
            }
            _byPid = byPid;
            _all = all;
        }

        private static void AddOrder(ModProblems prob, List<string> ids, Dictionary<ModMetaData, int> indexOf, int index, bool mustLoadBefore)
        {
            if (ids == null || ids.Count == 0) return;
            var conflicting = new List<string>();
            foreach (string id in ids)
            {
                if (id.NullOrEmpty()) continue;
                ModMetaData other = ModLister.GetActiveModWithIdentifier(id, ignorePostfix: true);
                if (other == null || !indexOf.TryGetValue(other, out int oi)) continue;
                // mustLoadBefore: this mod must load before 'other', so a violation is 'other' earlier (oi < index).
                // else (must load after): violation is 'other' later (oi > index).
                bool violated = mustLoadBefore ? oi < index : oi > index;
                if (violated) conflicting.Add(other.Name);
            }
            if (conflicting.Count > 0)
                prob.LoadOrder.Add((mustLoadBefore ? "ModMustLoadBefore" : "ModMustLoadAfter").Translate(conflicting.ToCommaList(useAnd: true)));
        }
    }
}
