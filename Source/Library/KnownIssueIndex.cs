using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Verse;

namespace ModernDevTools
{
    /// <summary>One matched library entry plus its (optional) precompiled attribution capture.</summary>
    public struct KnownIssueMatch
    {
        public KnownIssueDef def;
        public Regex attributeRegex;
    }

    /// <summary>
    /// Compiles every KnownIssueDef once (regexes + lowercased signal arrays) into an index, then
    /// scores a selected error against it. A match pass is a linear scan over a few dozen entries and
    /// only runs per selected message (cached upstream).
    /// </summary>
    public static class KnownIssueIndex
    {
        internal class Compiled
        {
            public KnownIssueDef def;
            public Regex[] regexes;
            public Regex attributeRegex;
            public string[] exTypesLower;
            public string[] keywordsLower;
            public string[] namespaces;
            public string[] packageIdsLower;
        }

        private static volatile List<Compiled> _all;
        private static readonly object _buildLock = new object();

        /// <summary>
        /// The compiled library. ALWAYS read through this (or a local snapshot of it) - never touch
        /// _all directly, because the list is published only once it is fully populated.
        /// </summary>
        private static List<Compiled> All { get { EnsureBuilt(); return _all; } }

        /// <summary>
        /// Build once, thread-safely. The list is assembled into a LOCAL and published to the volatile
        /// field only when complete: the startup prewarm runs this on a background thread, so publishing
        /// an empty list first (the previous shape) let a main-thread caller see a non-null but partial
        /// library - silently producing no diagnoses, or throwing mid-enumeration into a catch block that
        /// hid the cause. Mirrors HarmonyIndex.Build's correct pattern.
        /// </summary>
        public static void EnsureBuilt()
        {
            if (_all != null) return;
            lock (_buildLock)
            {
                if (_all != null) return;
                var built = new List<Compiled>();
                try
                {
                    foreach (KnownIssueDef def in DefDatabase<KnownIssueDef>.AllDefsListForReading)
                    {
                        built.Add(new Compiled
                        {
                            def = def,
                            exTypesLower = Lower(def.exceptionTypes),
                            keywordsLower = Lower(def.keywords),
                            namespaces = def.namespaces?.ToArray() ?? Array.Empty<string>(),
                            packageIdsLower = Lower(def.packageIds),
                            regexes = CompileList(def, def.regexes),
                            attributeRegex = CompileOne(def, def.attributeRegex)
                        });
                    }
                }
                catch (Exception e)
                {
                    Log.WarningOnce("[Modern Dev Tools] Failed to build known-issue index: " + e.Message, 0x2E19A04);
                }
                _all = built;   // publish only when complete
            }
        }

        private static Regex CompileOne(KnownIssueDef def, string pattern)
        {
            if (pattern.NullOrEmpty()) return null;
            try { return new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant); }
            catch (Exception e) { Log.WarningOnce($"[Modern Dev Tools] Bad attributeRegex in {def.defName}: {e.Message}", def.defName.GetHashCode() ^ 0x11); return null; }
        }

        private static Regex[] CompileList(KnownIssueDef def, List<string> patterns) =>
            IssueTextUtil.CompileRegexes(patterns, def.defName);

        private static string[] Lower(List<string> src) => IssueTextUtil.Lower(src);

        /// <summary>
        /// A prepared filter for "is this message one of the classes the player muted?".
        ///
        /// Built ONCE per list rebuild rather than per message. The previous per-message form allocated
        /// a full lowercase copy of every message's text and ran the exception-type regex over it, for
        /// every one of up to 1000 queued messages, on every rebuild - and a rebuild is triggered by
        /// every single log line that arrives. Here the ignored subset is resolved up front, keyword
        /// tests use allocation-free OrdinalIgnoreCase IndexOf, and the exception type is extracted
        /// only if some ignored entry actually keys on one.
        /// </summary>
        public sealed class IgnoreMatcher
        {
            private readonly Compiled[] _entries;
            private readonly bool _needsExType;

            internal IgnoreMatcher(Compiled[] entries)
            {
                _entries = entries;
                foreach (Compiled c in entries)
                    if (c.exTypesLower.Length > 0) { _needsExType = true; break; }
            }

            public bool Any => _entries.Length > 0;

            public bool Matches(string messageText)
            {
                if (_entries.Length == 0) return false;
                try
                {
                    string text = messageText ?? "";
                    string exType = null;
                    if (_needsExType) exType = FrameParser.ExtractExceptionType(text)?.ToLowerInvariant();

                    for (int i = 0; i < _entries.Length; i++)
                    {
                        Compiled c = _entries[i];
                        if (exType != null && c.exTypesLower.Length > 0
                            && Array.IndexOf(c.exTypesLower, exType) >= 0) return true;
                        for (int r = 0; r < c.regexes.Length; r++)
                            if (c.regexes[r].IsMatch(text)) return true;
                        for (int k = 0; k < c.keywordsLower.Length; k++)
                        {
                            string kw = c.keywordsLower[k];
                            if (kw.Length > 0 && text.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                        }
                    }
                }
                catch { }
                return false;
            }
        }

        private static readonly IgnoreMatcher EmptyMatcher = new IgnoreMatcher(Array.Empty<Compiled>());

        /// <summary>Prepare an <see cref="IgnoreMatcher"/> for the given muted issue defNames.</summary>
        public static IgnoreMatcher IgnoreMatcherFor(ICollection<string> ignoredDefNames)
        {
            if (ignoredDefNames == null || ignoredDefNames.Count == 0) return EmptyMatcher;
            try
            {
                List<Compiled> all = All;
                var subset = new List<Compiled>();
                foreach (Compiled c in all)
                    if (ignoredDefNames.Contains(c.def.defName)) subset.Add(c);
                return subset.Count == 0 ? EmptyMatcher : new IgnoreMatcher(subset.ToArray());
            }
            catch { return EmptyMatcher; }
        }

        /// <summary>Fast pre-pass: is this the text of a known benign, no-fault engine line? Text-only
        /// (regex / keyword / exception-type), so it can run before the module pipeline to set the
        /// suppression flag. Benign entries are a tiny slice of the library.</summary>
        public static bool HasBenignMatch(ErrorContext ctx)
        {
            List<Compiled> all = All;
            try
            {
                string text = ctx.Text ?? "";
                string textLower = text.ToLowerInvariant();
                string exType = ctx.ExceptionType?.ToLowerInvariant();
                foreach (Compiled c in all)
                {
                    if (!c.def.benign) continue;
                    if (c.regexes.Length > 0 && c.regexes.Any(rx => rx.IsMatch(text))) return true;
                    if (c.keywordsLower.Length > 0 && c.keywordsLower.Any(k => k.Length > 0 && textLower.Contains(k))) return true;
                    if (c.exTypesLower.Length > 0 && exType != null && c.exTypesLower.Contains(exType)) return true;
                }
            }
            catch { }
            return false;
        }

        public static List<KnownIssueMatch> Match(ErrorContext ctx)
        {
            List<Compiled> all = All;
            var scored = new List<KeyValuePair<Compiled, int>>();
            try
            {
                string text = ctx.Text ?? "";
                string exType = ctx.ExceptionType?.ToLowerInvariant();
                var pids = new HashSet<string>(ctx.ImplicatedPackageIds.Select(p => p.ToLowerInvariant()));

                foreach (Compiled c in all)
                {
                    // Shared scorer: the same weights serve the shipped library, mod-shipped files,
                    // API-registered sources and the community database.
                    int score = IssueScoring.Score(text, exType, ctx.Namespaces, pids,
                        c.exTypesLower, c.regexes, c.keywordsLower, c.namespaces, c.packageIdsLower);

                    if (score > 0) scored.Add(new KeyValuePair<Compiled, int>(c, score + c.def.priority));
                }
            }
            catch (Exception e)
            {
                Log.WarningOnce("[Modern Dev Tools] Known-issue match failed: " + e.Message, 0x2E19A05);
            }

            return scored
                .OrderByDescending(kv => kv.Value)
                .Select(kv => new KnownIssueMatch { def = kv.Key.def, attributeRegex = kv.Key.attributeRegex })
                .ToList();
        }
    }
}
