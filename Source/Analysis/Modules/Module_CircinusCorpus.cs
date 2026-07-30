using System;
using System.Reflection;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// Reports what Circinus's shared corpus knows about the selected error.
    ///
    /// REFLECTION, NOT A REFERENCE. Circinus exposes primitives on a static type for exactly
    /// this; binding to it at compile time would make ModernDevTools.dll and Circinus.dll a
    /// dependency pair, which is the coupling both mods have avoided. If Circinus is absent the
    /// type lookup fails once and this module stays silent forever after.
    ///
    /// The def gates on requiresPackageId, so in practice this only ever runs with Circinus
    /// active. The probe still tolerates absence because a module can also be registered
    /// through ModernDevToolsAPI, where no def gate applies.
    /// </summary>
    public class Module_CircinusCorpus : ErrorModule
    {
        private const string ApiType = "Circinus.KnownIssues";

        private static bool _probed;
        private static MethodInfo _describe, _implicated;

        private static void Probe()
        {
            if (_probed) return;
            _probed = true;
            try
            {
                Type t = GenTypes.GetTypeInAnyAssembly(ApiType);
                if (t == null) return;   // Circinus not installed: the normal quiet path.

                Type[] sig = { typeof(string), typeof(string) };
                _describe   = t.GetMethod("Describe",      BindingFlags.Public | BindingFlags.Static, null, sig, null);
                _implicated = t.GetMethod("ImplicatedMod", BindingFlags.Public | BindingFlags.Static, null, sig, null);

                // Circinus IS here but the contract moved. Say so once: a reflection bridge that
                // goes quietly dormant is the failure mode this whole mod exists to catch, and
                // debugging "why is the corpus card missing" from silence is miserable.
                if (_describe == null || _implicated == null)
                    Log.WarningOnce("[Modern Dev Tools] " + ApiType + " found but its methods did not match the " +
                                    "expected contract - corpus prevalence disabled. Describe=" + (_describe != null) +
                                    " ImplicatedMod=" + (_implicated != null), 0x2E19E01);
            }
            catch (Exception e)
            {
                // Null every binding, not just one: a half-probed bridge is worse than no bridge.
                _describe = null;
                _implicated = null;
                Log.WarningOnce("[Modern Dev Tools] corpus probe failed: " + e.Message, 0x2E19E02);
            }
        }

        public override void Diagnose(ErrorContext ctx)
        {
            Probe();
            if (_describe == null) return;

            string line;
            try { line = (string)_describe.Invoke(null, new object[] { ctx.Text, ctx.StackTrace }); }
            catch { return; }

            if (line.NullOrEmpty()) return;

            ctx.AddDiagnosis(new ErrorDiagnosis
            {
                Title = "MDT_CorpusTitle".Translate(),
                // Circinus builds this sentence itself (install counts, and a correlation clause
                // only when the evidence earns it). Deliberately passed through verbatim rather
                // than reformatted here: the phrasing carries its own evidence caveats.
                Explanation = line,
                Url = "https://circinus.sh/errors",
                Source = "MDT_Module_CircinusCorpus",
                // Below every other module ON PURPOSE, and note the scale: sibling modules score
                // 4.5-9.5, and Module_KnownIssues scores a curated match as low as 1. Anything
                // >= 1 here would outrank a real explanation. A shipped entry explains the fault;
                // this only says how widespread it is. Prevalence is context for a diagnosis, not
                // a diagnosis, so it sorts last.
                Score = 0.5f,
                Ignorable = true
            });
        }

        /// <summary>
        /// Corpus correlation as ATTRIBUTION, only when Circinus was willing to name a mod - it
        /// refuses below 25 affected and 25 unaffected runs, so this never fires on thin
        /// evidence. Weighted low: presence across installs is a weaker signal than a stack
        /// frame pointing at somebody's assembly.
        /// </summary>
        public override void ContributeAttribution(ErrorContext ctx)
        {
            Probe();
            if (_implicated == null) return;

            string pkg;
            try { pkg = (string)_implicated.Invoke(null, new object[] { ctx.Text, ctx.StackTrace }); }
            catch { return; }

            if (pkg.NullOrEmpty()) return;

            // 2.0 - under WeightMessageName (2.5) and far under WeightStackBase (5), so a corpus
            // correlation can add a name to the culprit list but can never reorder it above a
            // mod that actually has a frame in the trace.
            const float weight = ErrorContext.WeightKnownAttr * 0.5f;
            string reason = "MDT_ReasonCorpusCorrelation".Translate();

            ModMetaData meta = ctx.Mods?.PackageId(pkg);
            if (meta != null) ctx.Attribute(meta, weight, reason);
            else ctx.AttributeNamed(pkg, null, weight, reason);
        }
    }
}
