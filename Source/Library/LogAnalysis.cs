using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Verse;

namespace ModernDevTools
{
    /// <summary>The finished analysis of one error: ranked implicated mods + plain-language diagnoses.</summary>
    public class LogAnalysis
    {
        public ErrorContext Context;
        public List<AttributedMod> Mods;
        public List<ErrorDiagnosis> Diagnoses;

        public IEnumerable<AttributedMod> Culprits => Mods.Where(m => m.Kind == SourceKind.Mod);
        public bool AnyCulprit => Mods.Any(m => m.Kind == SourceKind.Mod);

        /// <summary>Normal, no-fault engine output (version banner, mod-list dumps, ...). Decided once,
        /// before the pipeline runs; when true the inspector shows "No concern" and hides attribution.</summary>
        public bool Benign => Context != null && Context.Benign;
    }

    /// <summary>
    /// Runs the module pipeline for a message once and caches the result. A ConditionalWeakTable keys
    /// on the message identity and auto-evicts once the message leaves the queue and is GC'd. Clear()
    /// replaces the table so a settings/registration change re-analyses everything.
    /// </summary>
    public static class LogAnalysisCache
    {
        private static ConditionalWeakTable<LogMessage, LogAnalysis> _cache =
            new ConditionalWeakTable<LogMessage, LogAnalysis>();

        // Clear() is called from the community-data BACKGROUND thread when a fetch completes. Rather
        // than swap the table off-thread (racing a main-thread GetValue), we only raise a flag and let
        // the next main-thread read do the swap. Keeps every table mutation on the main thread without
        // needing a marshal queue or an extra per-frame Harmony hook.
        private static volatile bool _dirty;

        public static LogAnalysis For(LogMessage msg)
        {
            if (msg == null) return null;
            if (_dirty) { _dirty = false; _cache = new ConditionalWeakTable<LogMessage, LogAnalysis>(); }
            return _cache.GetValue(msg, Compute);
        }

        /// <summary>Drop every cached analysis. Safe to call from any thread.</summary>
        public static void Clear() => _dirty = true;

        private static LogAnalysis Compute(LogMessage msg)
        {
            var ctx = new ErrorContext
            {
                Message = msg,
                Text = msg.text,
                StackTrace = msg.StackTrace,
                Frames = (msg.StackTrace ?? "").Split('\n'),
                Mods = InstalledModIndex.Instance,
                ExceptionType = FrameParser.ExtractExceptionType(msg.text)
            };

            // Recognize normal, no-fault engine lines up front (never an error-level entry). While the
            // flag is set, ErrorContext.Merge suppresses all attribution, so a benign line that merely
            // lists packageIds cannot implicate those mods, and dependent diagnoses fall away with it.
            try { ctx.Benign = msg.type != LogMessageType.Error && KnownIssueIndex.HasBenignMatch(ctx); }
            catch { }

            foreach (ErrorModule module in ErrorModuleRegistry.Modules)
            {
                try { module.ContributeAttribution(ctx); }
                catch (Exception e) { Log.WarningOnce("[Modern Dev Tools] module '" + module.Label + "' attribution failed: " + e.Message, module.GetType().GetHashCode()); }
                try { module.Diagnose(ctx); }
                catch (Exception e) { Log.WarningOnce("[Modern Dev Tools] module '" + module.Label + "' diagnose failed: " + e.Message, module.GetType().GetHashCode() ^ 7); }
            }

            return new LogAnalysis
            {
                Context = ctx,
                Mods = ctx.RankedMods(),
                Diagnoses = ctx.Diagnoses.OrderByDescending(d => d.Score).ToList()
            };
        }
    }
}
