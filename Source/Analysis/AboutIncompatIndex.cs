using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// Reads every ACTIVE mod's About.xml &lt;incompatibleWith&gt; and records the pairs where BOTH mods
    /// are active - a self-declared incompatibility straight from the mod authors, independent of any
    /// community database. This catches conflicts the community sources (RimSort rules, Use This Instead)
    /// do not know about. Built once (the modlist can't change mid-session) and cached.
    /// </summary>
    public static class AboutIncompatIndex
    {
        public struct Pair
        {
            public string APid, AName, BPid, BName;
        }

        private static List<Pair> _pairs;

        public static List<Pair> ActivePairs { get { if (_pairs == null) Build(); return _pairs; } }

        private static void Build()
        {
            var pairs = new List<Pair>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var active = ModsConfig.ActiveModsInLoadOrder
                    .Where(m => m != null && !m.PackageId.NullOrEmpty())
                    .ToList();
                var byPid = new Dictionary<string, ModMetaData>(StringComparer.OrdinalIgnoreCase);
                foreach (ModMetaData m in active) byPid[m.PackageId] = m;

                foreach (ModMetaData m in active)
                {
                    List<string> incs = m.IncompatibleWith;
                    if (incs == null) continue;
                    foreach (string bPid in incs)
                    {
                        if (bPid.NullOrEmpty() || !byPid.TryGetValue(bPid, out ModMetaData b)) continue;   // B must be active
                        string key = PairKey(m.PackageId, b.PackageId);
                        if (!seen.Add(key)) continue;   // dedupe unordered pairs (A->B and B->A)
                        pairs.Add(new Pair { APid = m.PackageId, AName = m.Name, BPid = b.PackageId, BName = b.Name });
                    }
                }
            }
            catch (Exception e) { Log.Warning("[Modern Dev Tools] About.xml incompatibility scan failed: " + e.Message); }
            _pairs = pairs;
        }

        public static IEnumerable<Pair> PairsInvolving(string packageId)
        {
            if (packageId.NullOrEmpty()) yield break;
            foreach (Pair p in ActivePairs)
                if (p.APid.Equals(packageId, StringComparison.OrdinalIgnoreCase)
                    || p.BPid.Equals(packageId, StringComparison.OrdinalIgnoreCase))
                    yield return p;
        }

        private static string PairKey(string a, string b) => IssueTextUtil.PairKey(a, b);
    }
}
