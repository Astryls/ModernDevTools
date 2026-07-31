using System;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// Optional suppression of runaway repeating log lines.
    ///
    /// WHAT VANILLA DOES, AND THE GAP. Verse.Log.Error/Warning/Message each build a LogMessage whose
    /// stack trace comes from StackTraceUtility.ExtractStackTrace() - and that call is evaluated as an
    /// ARGUMENT, i.e. BEFORE LogMessageQueue.Enqueue gets a chance to notice the line is a repeat. So
    /// every repeat of a spamming error pays a full managed frame walk plus a large string allocation,
    /// plus a DateTime.Now format, before being discarded. Below 99 repeats it also pays a synchronous
    /// Debug.Log* call, which writes to Player.log on disk. All of it happens inside lock(logLock), so
    /// spam from the tick loop serialises.
    ///
    /// The gap this closes: LogMessageQueue only combines a message with `lastMessage`, so dedupe is
    /// strictly CONSECUTIVE. Two errors interleaving (A, B, A, B, ...) - the common real case, e.g. one
    /// broken pawn raising two errors per tick - never combine at all. They never reach the 99 cap,
    /// never stop writing to disk, and permanently churn the 1000-entry queue.
    ///
    /// WHAT THIS DOES NOT DO. It does not fix the cause, and it must never be described as if it did.
    /// It removes the LOGGING overhead of a symptom that is already firing.
    ///
    /// SAFETY RULES BAKED IN HERE:
    ///   * The first <see cref="Threshold"/> occurrences ALWAYS pass. The diagnostic value is the first
    ///     occurrence plus an honest count, so a suppressed error is still visible in the log.
    ///   * Suppressed counts are surfaced (list row, inspector) and folded into impact scoring, so the
    ///     player never sees an understated repeat count.
    ///   * This prefix sits on every mod's error path, so it is allocation-free (FNV-1a over the chars,
    ///     no ToLower, no substring), takes only its own short lock, and fails OPEN on any exception.
    ///   * Log.Error may be called from a background thread, so no Time.frameCount here (it throws off
    ///     the main thread) - the time window uses Environment.TickCount.
    ///   * Summary lines are NOT emitted from the prefix (that would re-enter Log). They are drained on
    ///     the main thread from the per-frame Root.Update hook.
    /// </summary>
    public static class LogThrottle
    {
        /// <summary>Occurrences that always pass through untouched.</summary>
        public const int Threshold = 5;

        /// <summary>Quiet period after which a line is treated as a fresh occurrence again (ms).</summary>
        private const int WindowMs = 10000;

        /// <summary>Emit a "suppressed N" summary after this many suppressions of one line.</summary>
        private const int SummaryEvery = 200;

        private const int Slots = 64;

        private struct Slot
        {
            public uint Hash;
            public byte Type;
            public int Count;        // occurrences seen in the current window (passed + suppressed)
            public int Suppressed;   // total suppressed in the current window
            public int LastMs;
            public int ReportedAt;   // Suppressed value at the last summary emission
            public bool NeedsSummary;
        }

        private static readonly Slot[] _ring = new Slot[Slots];
        private static readonly object _lock = new object();
        private static int _cursor;

        /// <summary>Latched off if anything in here ever throws - a broken throttle must not eat logs.</summary>
        private static bool _broken;

        public static bool Enabled =>
            !_broken && ModernDevToolsMod.Settings != null && ModernDevToolsMod.Settings.throttleRepeatingLogs;

        private static uint HashOf(string s)
        {
            unchecked
            {
                uint h = 2166136261u;                      // FNV-1a
                for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619u; }
                return h;
            }
        }

        /// <summary>
        /// The gate. Returns TRUE to let vanilla log normally, FALSE to suppress this occurrence.
        /// </summary>
        public static bool ShouldLog(string text, LogMessageType type)
        {
            if (_broken || text == null) return true;
            try
            {
                uint hash = HashOf(text);
                byte t = (byte)type;
                int now = Environment.TickCount;

                lock (_lock)
                {
                    for (int i = 0; i < Slots; i++)
                    {
                        if (_ring[i].Hash != hash || _ring[i].Type != t || _ring[i].Count == 0) continue;

                        // Quiet for long enough: treat it as new so a recovered error logs again.
                        if (unchecked(now - _ring[i].LastMs) > WindowMs)
                        {
                            _ring[i].Count = 1;
                            _ring[i].Suppressed = 0;
                            _ring[i].ReportedAt = 0;
                            _ring[i].LastMs = now;
                            return true;
                        }

                        _ring[i].LastMs = now;
                        _ring[i].Count++;
                        if (_ring[i].Count <= Threshold) return true;

                        _ring[i].Suppressed++;
                        if (_ring[i].Suppressed - _ring[i].ReportedAt >= SummaryEvery)
                        {
                            _ring[i].ReportedAt = _ring[i].Suppressed;
                            _ring[i].NeedsSummary = true;
                        }
                        return false;
                    }

                    // Not tracked yet: claim a slot (round-robin; oldest tracked line is evicted).
                    int slot = _cursor;
                    _cursor = (_cursor + 1) % Slots;
                    _ring[slot] = new Slot { Hash = hash, Type = t, Count = 1, LastMs = now };
                    return true;
                }
            }
            catch (Exception e)
            {
                _broken = true;
                try { Log.Error("[Modern Dev Tools] log throttle failed and has been disabled: " + e); } catch { }
                return true;   // fail open: never lose a log line because of us
            }
        }

        /// <summary>
        /// How many occurrences of this exact line we have suppressed in the current window. Used to
        /// show an honest count in the UI and to keep impact scoring truthful. Cheap, but only called
        /// while the feature is on.
        /// </summary>
        public static int SuppressedFor(string text, LogMessageType type)
        {
            if (_broken || text == null) return 0;
            try
            {
                uint hash = HashOf(text);
                byte t = (byte)type;
                lock (_lock)
                {
                    for (int i = 0; i < Slots; i++)
                        if (_ring[i].Hash == hash && _ring[i].Type == t && _ring[i].Count > 0)
                            return _ring[i].Suppressed;
                }
            }
            catch { }
            return 0;
        }

        /// <summary>
        /// Emit any pending "suppressed N" summaries. Called once per frame from the Root.Update hook -
        /// the MAIN thread, and outside Log's own lock - because logging from inside the prefix would
        /// re-enter Log.Warning and recurse. At most one line per frame keeps the summary itself from
        /// becoming spam.
        /// </summary>
        public static void DrainSummaries()
        {
            if (_broken) return;
            int suppressed = 0;
            try
            {
                lock (_lock)
                {
                    for (int i = 0; i < Slots; i++)
                    {
                        if (!_ring[i].NeedsSummary) continue;
                        _ring[i].NeedsSummary = false;
                        suppressed = _ring[i].Suppressed;
                        break;   // one per frame
                    }
                }
            }
            catch { return; }

            if (suppressed <= 0) return;
            try { Log.Warning("[Modern Dev Tools] " + "MDT_ThrottleSummary".Translate(suppressed, Threshold)); }
            catch { }
        }
    }
}
