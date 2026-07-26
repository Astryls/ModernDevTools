using System.Diagnostics;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// TEMPORARY diagnostic markers for tracing a main-thread hang. Emits Log.Message lines (kept out of
    /// the bridge's error stream, but written to Player.log) tagged "MDT-DIAG" so the last marker before a
    /// freeze pinpoints the stalling operation. Remove once the hang is located.
    /// </summary>
    public static class Diag
    {
        public static void Mark(string what)
        {
            Log.Message("MDT-DIAG " + what);
        }

        /// <summary>Log begin/end around an operation with elapsed ms; the "begin" line survives a hang.</summary>
        public static Stopwatch Begin(string what)
        {
            Log.Message("MDT-DIAG begin " + what);
            return Stopwatch.StartNew();
        }

        public static void End(string what, Stopwatch sw)
        {
            sw?.Stop();
            Log.Message("MDT-DIAG end   " + what + " (" + (sw?.ElapsedMilliseconds ?? -1) + " ms)");
        }
    }
}
