using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace ModernDevTools
{
    public enum ImpactLevel { Critical, High, Moderate, Low, Info }

    public struct ImpactResult
    {
        public ImpactLevel Level;
        public string PerfNote;   // localized one-liner about performance/frequency
        public bool Known;        // matched a CURATED library entry (not merely "some module said something")
        public bool Benign;       // normal, no-fault engine output - shown as "No concern"
        public bool Capped;       // a curated entry lowered the level below what the heuristic computed
    }

    /// <summary>
    /// Heuristic assessment of how much an error matters: severity plus whether it is likely to hurt
    /// simulation speed (TPS) or rendering (FPS), inferred from the stack-trace signals and how often
    /// it repeats. Cheap and pure; computed per selected message.
    ///
    /// LOCATION IS NOT FREQUENCY - the rule this class got wrong once and must not get wrong again.
    /// TickSignals/FrameSignals only prove WHERE an error happened, and nearly everything in RimWorld
    /// runs somewhere under TickManager.DoSingleTick: incidents, quests, one-shot storyteller events.
    /// Being inside the tick loop ONCE costs nothing. A performance claim therefore requires evidence
    /// of RECURRENCE (msg.repeats), never hot-path frames alone. The bug this rule replaces flagged a
    /// single self-healing vanilla raid fallback (IncidentWorker_Raid.cs:57, logged at Error severity
    /// but immediately defaulting to EdgeWalkIn) as "Critical - affects simulation (TPS)", purely
    /// because the storyteller runs inside the tick loop.
    /// </summary>
    public static class ImpactAssessor
    {
        /// <summary>Repeats needed before a hot-path error is treated as an ongoing performance drain.</summary>
        private const int RepeatingThreshold = 3;
        /// <summary>Repeats that are a problem on their own, wherever they came from.</summary>
        private const int SpamThreshold = 10;

        // Frames that mean the error fires inside the simulation loop (per tick / per pawn / AI).
        private static readonly string[] TickSignals =
        {
            "Tick(", ".Tick ", ":Tick", "TickRare", "TickLong", "TickList", "TickManager",
            "MapComponentTick", "GameComponentTick", "CompTick", "JobDriver", "Verse.AI",
            "WorkGiver", "ThinkNode", "Pawn_JobTracker", "Pawn_HealthTracker", "PathFollower",
            "Hediff", "Need", "MentalState"
        };

        // Frames that mean the error fires while drawing (per frame).
        private static readonly string[] FrameSignals =
        {
            "OnGUI", ".Draw(", "DrawAt", "Widgets.", "WindowStack", "GizmoOnGUI",
            "DynamicDrawPhase", "DoWindowContents", "ExtraOnGUI", "MapInterface", "PawnRenderer"
        };

        public static ImpactResult Assess(LogMessage msg, LogAnalysis a)
        {
            var r = new ImpactResult
            {
                // "Known" means a CURATED entry matched - not merely that some module emitted something.
                // Basing this on Diagnoses.Count let a Harmony or dependency heuristic light up the
                // "Known issue" badge for errors the library has never seen.
                Known = a?.Diagnoses != null && a.Diagnoses.Any(d => d.FromLibrary),
                Benign = a != null && a.Benign
            };
            try
            {
                string trace = msg.StackTrace ?? "";
                string text = msg.text ?? "";
                int reps = Mathf.Max(1, msg.repeats);
                bool err = msg.type == LogMessageType.Error;
                bool warn = msg.type == LogMessageType.Warning;

                bool tick = ContainsAny(trace, TickSignals) || ContainsAny(text, TickSignals);
                bool frame = ContainsAny(trace, FrameSignals);
                bool repeating = reps >= RepeatingThreshold;
                bool spam = reps >= SpamThreshold;

                // A hot-path frame only matters once the error actually RECURS. One error inside the
                // tick loop is a one-off, not a TPS problem.
                bool hot = (tick || frame) && repeating;

                if (err && (spam || hot)) r.Level = ImpactLevel.Critical;
                else if (err) r.Level = ImpactLevel.High;
                else if (warn && (spam || hot)) r.Level = ImpactLevel.Moderate;
                else if (warn) r.Level = ImpactLevel.Low;
                else r.Level = ImpactLevel.Info;

                r.PerfNote = BuildPerfNote(reps, tick, frame, repeating);

                // Curated knowledge overrules the heuristic. ImpactLevel runs Critical=0 -> Info=4, so a
                // cap is "never more severe than this"; the default (Critical) is a no-op.
                ImpactLevel cap = ImpactLevel.Critical;
                if (a?.Diagnoses != null)
                    foreach (ErrorDiagnosis d in a.Diagnoses)
                        if ((int)d.MaxImpact > (int)cap) cap = d.MaxImpact;
                if ((int)r.Level < (int)cap)
                {
                    r.Level = cap;
                    r.Capped = true;
                    // An entry asserting "this is at most Low" also contradicts a TPS/FPS claim.
                    if ((int)cap >= (int)ImpactLevel.Low)
                        r.PerfNote = reps > 1 ? "MDT_ImpactRepeats".Translate(reps).ToString() : "MDT_ImpactOneOff".Translate().ToString();
                }

                if (r.Benign) r.Level = ImpactLevel.Info; // a normal line is never "critical", whatever its frames
            }
            catch
            {
                r.Level = msg.type == LogMessageType.Error ? ImpactLevel.High : ImpactLevel.Low;
                r.PerfNote = "";
            }
            return r;
        }

        private static string BuildPerfNote(int reps, bool tick, bool frame, bool repeating)
        {
            string freq = reps > 1 ? "MDT_ImpactRepeats".Translate(reps).ToString() : null;

            // Only a RECURRING error can be claimed to cost TPS or FPS. Without this guard every
            // one-shot incident or quest error inherits a performance warning it does not deserve,
            // because the storyteller happens to run under the tick loop.
            string perf = null;
            if (repeating)
            {
                if (tick && frame) perf = "MDT_ImpactBoth".Translate();
                else if (tick) perf = "MDT_ImpactTps".Translate();
                else if (frame) perf = "MDT_ImpactFps".Translate();
            }

            if (freq == null && perf == null) return "MDT_ImpactOneOff".Translate();
            if (freq != null && perf != null) return freq + " - " + perf;
            return freq ?? perf;
        }

        public static Color ColorFor(ImpactLevel level)
        {
            switch (level)
            {
                case ImpactLevel.Critical:
                case ImpactLevel.High: return Palette.Bad;
                case ImpactLevel.Moderate:
                case ImpactLevel.Low: return Palette.Warn;
                default: return Palette.StripGray;
            }
        }

        public static string LabelFor(ImpactLevel level)
        {
            switch (level)
            {
                case ImpactLevel.Critical: return "MDT_Critical".Translate();
                case ImpactLevel.High: return "MDT_High".Translate();
                case ImpactLevel.Moderate: return "MDT_Moderate".Translate();
                case ImpactLevel.Low: return "MDT_Low".Translate();
                default: return "MDT_Info".Translate();
            }
        }

        private static bool ContainsAny(string s, string[] needles)
        {
            if (s.NullOrEmpty()) return false;
            for (int i = 0; i < needles.Length; i++)
                if (s.IndexOf(needles[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }
    }
}
