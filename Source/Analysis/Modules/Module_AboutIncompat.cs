using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// Built-in, always-on: when the error implicates a mod that has declared (in its own About.xml) an
    /// incompatibility with another ACTIVE mod, explain it. This is the LOCAL complement to the community
    /// incompatibility rules - it reads every mod's About.xml, so it catches conflicts the community
    /// sources miss, and it deliberately skips any pair the community rules already cover.
    /// </summary>
    public class Module_AboutIncompat : ErrorModule
    {
        public override void Diagnose(ErrorContext ctx)
        {
            var pairs = AboutIncompatIndex.ActivePairs;
            if (pairs == null || pairs.Count == 0) return;

            var shown = new HashSet<string>();
            foreach (AttributedMod m in ctx.Culprits)
            {
                if (m.PackageId.NullOrEmpty()) continue;
                foreach (AboutIncompatIndex.Pair p in AboutIncompatIndex.PairsInvolving(m.PackageId))
                {
                    string key = PairKey(p);
                    if (!shown.Add(key)) continue;
                    if (CoveredByCommunity(p)) continue;   // don't duplicate what the community module already says

                    bool mIsA = p.APid.Equals(m.PackageId, StringComparison.OrdinalIgnoreCase);
                    string thisName = mIsA ? p.AName : p.BName;
                    string otherName = mIsA ? p.BName : p.AName;

                    ctx.AddDiagnosis(new ErrorDiagnosis
                    {
                        Title = "MDT_AboutIncompatTitle".Translate(),
                        Explanation = "MDT_AboutIncompatExplain".Translate(thisName, otherName),
                        Fix = "MDT_IncompatFix".Translate(),
                        Source = "aboutincompat:" + key,
                        Score = 7
                    });
                }
            }
        }

        private static bool CoveredByCommunity(AboutIncompatIndex.Pair p)
        {
            if (!CommunityData.Enabled) return false;
            CommRule ra = CommunityData.RuleFor(p.APid);
            if (ra != null && ra.Incompat.Contains(p.BPid)) return true;
            CommRule rb = CommunityData.RuleFor(p.BPid);
            if (rb != null && rb.Incompat.Contains(p.APid)) return true;
            return false;
        }

        private static string PairKey(AboutIncompatIndex.Pair p)
        {
            string la = p.APid.ToLowerInvariant(), lb = p.BPid.ToLowerInvariant();
            return string.CompareOrdinal(la, lb) <= 0 ? la + "|" + lb : lb + "|" + la;
        }
    }
}
