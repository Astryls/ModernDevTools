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
            var pool = ModShippedIssues.All;
            if (pool == null || pool.Count == 0) return;

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
                    Score = 9 + (n - i)   // the author's own shipped fix ranks above community (8) and the library
                });
            }
        }
    }
}
