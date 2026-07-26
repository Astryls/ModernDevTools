using System.Collections.Generic;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// Built-in, always-on: turns the proactive dependency/load-order scan (ModDependencyIndex) into
    /// plain-language diagnoses. Two paths: (1) an implicated culprit that itself has unmet requirements
    /// or a load-order violation gets a specific, actionable explanation; (2) any "missing def / missing
    /// type / unresolved reference" error - the classic cascade symptom - gets a summary of every active
    /// mod that is missing a required mod, since that is the usual root cause even when the trace names
    /// no one.
    /// </summary>
    public class Module_DependencyCheck : ErrorModule
    {
        private static readonly string[] MissingRefSignals =
        {
            "could not find def named", "could not load reference to", "could not find type named",
            "found no type named", "cross-reference", "could not resolve cross-reference",
            "is not a def type", "could not instantiate or initialize a def"
        };

        public override void Diagnose(ErrorContext ctx)
        {
            if (!ModDependencyIndex.AnyProblems) return;

            var shown = new HashSet<string>();

            // 1) Specific: a culprit named in this error has unmet requirements / wrong load order.
            foreach (AttributedMod m in ctx.Culprits)
            {
                if (m.PackageId.NullOrEmpty()) continue;
                ModDependencyIndex.ModProblems prob = ModDependencyIndex.For(m.PackageId);
                if (prob == null || !prob.Any) continue;
                if (!shown.Add(m.PackageId)) continue;
                EmitSpecific(ctx, prob);
            }

            // 2) Global: a missing-def/type/reference error + active mods with unmet deps elsewhere.
            string tl = (ctx.Text ?? "").ToLowerInvariant();
            bool missingRef = false;
            foreach (string s in MissingRefSignals) if (tl.Contains(s)) { missingRef = true; break; }
            if (!missingRef) return;

            var lines = new List<string>();
            foreach (ModDependencyIndex.ModProblems prob in ModDependencyIndex.All)
            {
                if (prob.Missing.Count == 0) continue;
                if (shown.Contains(prob.PackageId)) continue;   // already called out specifically above
                var deps = new List<string>();
                foreach (ModDependencyIndex.MissingDep d in prob.Missing) deps.Add(d.Name);
                lines.Add("MDT_DepLine".Translate(prob.ModName, deps.ToCommaList(useAnd: true)));
                if (lines.Count >= 8) break;
            }
            if (lines.Count == 0) return;

            ctx.AddDiagnosis(new ErrorDiagnosis
            {
                Title = "MDT_DepSummaryTitle".Translate(),
                Explanation = "MDT_DepSummaryExplain".Translate(lines.Count) + "\n" + string.Join("\n", lines),
                Fix = "MDT_DepSummaryFix".Translate(),
                Source = "depsummary",
                Score = 8f
            });
        }

        private static void EmitSpecific(ErrorContext ctx, ModDependencyIndex.ModProblems prob)
        {
            if (prob.Missing.Count > 0)
            {
                var deps = new List<string>();
                string url = null;
                foreach (ModDependencyIndex.MissingDep d in prob.Missing)
                {
                    deps.Add(d.Installed ? "MDT_DepInactive".Translate(d.Name).ToString() : d.Name);
                    if (url.NullOrEmpty()) url = d.Url;
                }
                ctx.AddDiagnosis(new ErrorDiagnosis
                {
                    Title = "MDT_DepMissingTitle".Translate(),
                    Explanation = "MDT_DepMissingExplain".Translate(prob.ModName, deps.ToCommaList(useAnd: true)),
                    Fix = "MDT_DepMissingFix".Translate(),
                    Url = url,
                    Source = "depmissing:" + prob.PackageId,
                    Score = 8.5f
                });
            }
            if (prob.LoadOrder.Count > 0)
            {
                ctx.AddDiagnosis(new ErrorDiagnosis
                {
                    Title = "MDT_DepOrderTitle".Translate(),
                    Explanation = "MDT_DepOrderExplain".Translate(prob.ModName) + "\n" + string.Join("\n", prob.LoadOrder),
                    Fix = "MDT_DepOrderFix".Translate(),
                    Source = "deporder:" + prob.PackageId,
                    Score = 8f
                });
            }
        }
    }
}
