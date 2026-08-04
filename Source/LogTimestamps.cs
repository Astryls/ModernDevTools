using System;
using HarmonyLib;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// Wall-clock arrival time for a log line.
    ///
    /// Verse.LogMessage already records one: BOTH of its constructors set a private string field
    /// `timestamp` to DateTime.Now.ToString("HH:mm:ss"), and LogMessage.ToString() renders it as
    /// "[HH:mm:ss] text". Nothing exposes it, so we read the field directly. That is deliberately
    /// preferred over stamping messages ourselves from a patch on LogMessageQueue.Enqueue: the value
    /// we show is then the one the ENGINE recorded at the moment the message object was created,
    /// including every line logged before this mod's patches were installed. No extra permanent
    /// patch, no parallel bookkeeping, nothing to drift.
    ///
    /// Fails closed. If the field is gone or retyped (a future RimWorld build, or a mod that swaps
    /// LogMessage out) Of() returns null and every caller draws nothing at all - a log tool must not
    /// invent a time and present it as recorded fact.
    ///
    /// REPEATS: LogMessageQueue.Enqueue folds a repeat into the RETAINED message and only bumps
    /// `repeats`, so the stamp on a repeated line is when it was FIRST seen, never the latest
    /// occurrence. The UI says "first seen" in that case instead of implying it is current.
    /// </summary>
    public static class LogTimestamps
    {
        private static bool _probed;
        private static AccessTools.FieldRef<LogMessage, string> _get;

        /// <summary>True when the engine field was bound and timestamps can be shown.</summary>
        public static bool Available
        {
            get { Probe(); return _get != null; }
        }

        /// <summary>The "HH:mm:ss" the engine stamped on this message, or null when unknown.</summary>
        public static string Of(LogMessage msg)
        {
            if (msg == null) return null;
            Probe();
            if (_get == null) return null;
            try { return _get(msg); }
            catch { return null; }
        }

        private static void Probe()
        {
            if (_probed) return;
            _probed = true;
            try
            {
                _get = AccessTools.FieldRefAccess<LogMessage, string>("timestamp");
            }
            catch (Exception e)
            {
                _get = null;
                // Keep the LogPrefix: Module_StackTrace uses it to tell "we reported this" from
                // "we caused this", and this warning is emitted from inside the log UI itself.
                Log.Warning(ModernDevToolsMod.LogPrefix + " could not read Verse.LogMessage.timestamp (" +
                            e.GetType().Name + "); log timestamps are unavailable this session.");
            }
        }
    }
}
