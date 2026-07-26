using System;
using UnityEngine;
using Verse;

namespace ModernDevTools
{
    public enum ImpactLevel { Critical, High, Moderate, Low, Info }

    public struct ImpactResult
    {
        public ImpactLevel Level;
        public string PerfNote;   // localized one-liner about performance/frequency
        public bool Known;        // recognized by the library (and later, community databases)
    }

    /// <summary>
    /// Heuristic assessment of how much an error matters: severity plus whether it is likely to hurt
    /// simulation speed (TPS) or rendering (FPS), inferred from the stack-trace signals and how often
    /// it repeats. Cheap and pure; computed per selected message.
    /// </summary>
    public static class ImpactAssessor
    {
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
            var r = new ImpactResult { Known = a?.Diagnoses != null && a.Diagnoses.Count > 0 };
            try
            {
                string trace = msg.StackTrace ?? "";
                string text = msg.text ?? "";
                int reps = Mathf.Max(1, msg.repeats);
                bool err = msg.type == LogMessageType.Error;
                bool warn = msg.type == LogMessageType.Warning;

                bool tick = ContainsAny(trace, TickSignals) || ContainsAny(text, TickSignals);
                bool frame = ContainsAny(trace, FrameSignals);
                bool spam = reps >= 10;

                if (err && (tick || frame || spam)) r.Level = ImpactLevel.Critical;
                else if (err) r.Level = ImpactLevel.High;
                else if (warn && (tick || frame || spam)) r.Level = ImpactLevel.Moderate;
                else if (warn) r.Level = ImpactLevel.Low;
                else r.Level = ImpactLevel.Info;

                r.PerfNote = BuildPerfNote(reps, tick, frame, spam, err);
            }
            catch
            {
                r.Level = msg.type == LogMessageType.Error ? ImpactLevel.High : ImpactLevel.Low;
                r.PerfNote = "";
            }
            return r;
        }

        private static string BuildPerfNote(int reps, bool tick, bool frame, bool spam, bool err)
        {
            string freq = reps > 1 ? "MDT_ImpactRepeats".Translate(reps).ToString() : null;

            string perf = null;
            if (tick && frame) perf = "MDT_ImpactBoth".Translate();
            else if (tick) perf = "MDT_ImpactTps".Translate();
            else if (frame) perf = "MDT_ImpactFps".Translate();
            else if (spam && err) perf = "MDT_ImpactTps".Translate();

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
