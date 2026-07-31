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
        /// <summary>
        /// Verbatim fragments of engine messages that mean "something referenced a def/type that was
        /// not there" - the classic symptom of an unmet dependency or a bad load order.
        ///
        /// AUDIT 2026-11: this list had never been checked against the engine source the way
        /// Defs/KnownIssueDefs.xml had, and three of its eight entries were phantoms - strings no
        /// vanilla site emits:
        ///   "could not find def named"                  -> the engine says "Failed to find &lt;Type&gt; named X.
        ///                                                  There are N defs of this type loaded."
        ///                                                  (Verse/DefDatabase.cs:217), or, for filters,
        ///                                                  "could not find thing def named"
        ///                                                  (Verse/ThingFilter.cs:321/:544).
        ///   "found no type named"                       -> the engine only says "Could not find type named".
        ///   "could not instantiate or initialize a def" -> the engine names the component
        ///                                                  ("Could not instantiate a ThingComp...").
        /// Because this list is an OR-gate, a phantom is a silent FALSE NEGATIVE rather than a false
        /// claim - but "could not find def named" was standing in for the single most common cascade
        /// error in RimWorld, so the summary that names the mods with unmet dependencies was simply not
        /// appearing for it. Each entry below is now anchored to a verified Log call site.
        /// </summary>
        private static readonly string[] MissingRefSignals =
        {
            "defs of this type loaded",             // Verse/DefDatabase.cs:217 (DefDatabase<T>.GetNamed)
            "could not find thing def named",       // Verse/ThingFilter.cs:321, :544
            "could not resolve cross-reference",    // Verse/DirectXmlCrossRefLoader.cs:90/:96/:438
            "could not load reference to",          // Verse/ScribeExtractor.cs:56
            "could not find type named",            // Verse/DirectXmlToObject.cs:251
            "is not a def type",                    // Verse/DirectXmlLoader.cs:184 ("is not a Def type or...")
            "could not instantiate",                // ThingWithComps.cs:212, HediffWithComps.cs:413, et al.
            "cross-reference"                       // broad, but only ever appears in the real messages
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
