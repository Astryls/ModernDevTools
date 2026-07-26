using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// Built-in: matches the selected error against our own community bug/fix database (a shipped,
    /// contributor-editable JSON on GitHub, fetched with the opt-in community data). Community-reported
    /// fixes rank above generic library advice and mark the error as a known issue.
    /// </summary>
    public class Module_CommunityBugs : ErrorModule
    {
        public override void Diagnose(ErrorContext ctx)
        {
            if (!CommunityData.Enabled) return;
            var matches = CommunityData.MatchBugs(ctx);
            int n = matches.Count;
            for (int i = 0; i < n; i++)
            {
                RemoteIssue b = matches[i];
                string explanation = b.Explanation ?? "";
                if (!b.ReportedBy.NullOrEmpty())
                    explanation = (explanation.Length > 0 ? explanation + "\n\n" : "") + "MDT_CommunityReportedBy".Translate(b.ReportedBy);

                ctx.AddDiagnosis(new ErrorDiagnosis
                {
                    Title = b.Title,
                    Explanation = explanation,
                    Fix = b.Fix,
                    Url = b.Url,
                    Source = "community:" + b.Id,
                    Score = 8 + (n - i)   // community-reported fixes rank highest
                });
            }
        }
    }
}
