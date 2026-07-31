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
    }
}
