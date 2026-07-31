using System;
using System.Collections.Generic;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// Built-in, always-on: matches the selected error against known-issue files that other mods ship
    /// inside their own folder (see ModShippedIssues). An author's own explanation for their own error
    /// ranks ABOVE the community database (the fallback). Local; no internet or opt-in required.
    /// </summary>
    public class Module_ModIssues : ErrorModule
    {
        public override void Diagnose(ErrorContext ctx)
        {
            // Two curated-by-the-author sources share this module: entries shipped as a file inside a
            // mod's About folder, and entries registered at runtime through
            // ModernDevToolsAPI.RegisterKnowledgeSource. Both are local, always-on and rank above the
            // community database, so they are scored as one pool.
            var shipped = ModShippedIssues.All;
            var api = KnowledgeSources.Pool;
            int shippedCount = shipped?.Count ?? 0;
            int apiCount = api?.Count ?? 0;
            if (shippedCount + apiCount == 0) return;

            List<RemoteIssue> pool;
            if (apiCount == 0) pool = shipped;
            else if (shippedCount == 0) pool = api;
            else
            {
                pool = new List<RemoteIssue>(shippedCount + apiCount);
                pool.AddRange(shipped);
                pool.AddRange(api);
            }

            var matches = CommunityData.Match(ctx, pool);
            matches = Scope(ctx, matches);
            int n = matches.Count;
            for (int i = 0; i < n; i++)
            {
                RemoteIssue b = matches[i];
                string explanation = b.Explanation ?? "";
                if (!b.ReportedBy.NullOrEmpty())
                    explanation = (explanation.Length > 0 ? explanation + "\n\n" : "") + "MDT_ModShippedBy".Translate(b.ReportedBy);

                ctx.AddDiagnosis(new ErrorDiagnosis
                {
                    Title = b.Title,
                    Explanation = explanation,
                    Fix = b.Fix,
                    Url = b.Url,
                    Source = "modissue:" + b.Id,
                    FromLibrary = true,   // a curated entry, just curated by the mod's own author
                    Score = 9 + (n - i)   // the author's own shipped fix ranks above community (8) and the library
                });
            }
        }

        /// <summary>
        /// A mod-shipped entry may only speak about ITS OWN mod's errors.
        ///
        /// Entries in this pool get the highest score in the whole scale AND FromLibrary=true (the
        /// "Known issue" badge), so they are presented as authoritative. But the file lives in a third
        /// party's About folder and is matched on message text alone - so a single broad keyword like
        /// "NullReferenceException" in one mod's file would take the top diagnosis slot for every
        /// unrelated error in the game and blame the wrong author. That is not a hypothetical: it is
        /// the natural mistake for someone writing their first entry.
        ///
        /// An entry therefore applies only when at least one of these holds:
        ///   * the error already implicates the mod that shipped the file, or
        ///   * the entry declares its own packageIds/namespaces (so the author has stated the scope
        ///     explicitly - which is how you legitimately describe an error thrown by a dependency), or
        ///   * it came from the runtime API, where registration is a deliberate code-level act by an
        ///     author who has our documentation in front of them.
        /// </summary>
        private static List<RemoteIssue> Scope(ErrorContext ctx, List<RemoteIssue> matches)
        {
            if (matches.Count == 0) return matches;

            HashSet<string> implicated = null;
            var kept = new List<RemoteIssue>(matches.Count);
            foreach (RemoteIssue b in matches)
            {
                if (b.OwnerPackageId.NullOrEmpty() || b.HasExplicitScope) { kept.Add(b); continue; }

                if (implicated == null)
                {
                    implicated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string p in ctx.ImplicatedPackageIds) implicated.Add(p);
                }
                if (implicated.Contains(b.OwnerPackageId)) kept.Add(b);
            }
            return kept;
        }
    }
}
