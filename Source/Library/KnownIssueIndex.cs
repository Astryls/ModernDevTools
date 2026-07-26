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
        private class Compiled
        {
            public KnownIssueDef def;
            public Regex[] regexes;
            public Regex attributeRegex;
            public string[] exTypesLower;
            public string[] keywordsLower;
            public string[] namespaces;
            public string[] packageIdsLower;
        }

        private static List<Compiled> _all;

        public static void EnsureBuilt()
        {
            if (_all != null) return;
            _all = new List<Compiled>();
            try
            {
                foreach (KnownIssueDef def in DefDatabase<KnownIssueDef>.AllDefsListForReading)
                {
                    _all.Add(new Compiled
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
        }

        private static Regex CompileOne(KnownIssueDef def, string pattern)
        {
            if (pattern.NullOrEmpty()) return null;
            try { return new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant); }
            catch (Exception e) { Log.WarningOnce($"[Modern Dev Tools] Bad attributeRegex in {def.defName}: {e.Message}", def.defName.GetHashCode() ^ 0x11); return null; }
        }

        private static Regex[] CompileList(KnownIssueDef def, List<string> patterns)
        {
            if (patterns == null || patterns.Count == 0) return Array.Empty<Regex>();
            var list = new List<Regex>();
            foreach (string pat in patterns)
            {
                try { list.Add(new Regex(pat, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)); }
                catch (Exception e) { Log.WarningOnce($"[Modern Dev Tools] Bad regex in {def.defName}: {e.Message}", def.defName.GetHashCode()); }
            }
            return list.ToArray();
        }

        private static string[] Lower(List<string> src)
        {
            if (src == null || src.Count == 0) return Array.Empty<string>();
            var arr = new string[src.Count];
            for (int i = 0; i < src.Count; i++) arr[i] = src[i]?.ToLowerInvariant() ?? "";
            return arr;
        }

        /// <summary>Text-only test (no attribution needed) used by the list ignore-filter: does this
        /// message match any of the ignored issue types?</summary>
        public static bool TextMatchesAnyIgnored(string messageText, ICollection<string> ignoredDefNames)
        {
            if (ignoredDefNames == null || ignoredDefNames.Count == 0) return false;
            EnsureBuilt();
            try
            {
                string text = messageText ?? "";
                string textLower = text.ToLowerInvariant();
                string exType = FrameParser.ExtractExceptionType(text)?.ToLowerInvariant();
                foreach (Compiled c in _all)
                {
                    if (!ignoredDefNames.Contains(c.def.defName)) continue;
                    if (c.exTypesLower.Length > 0 && exType != null && c.exTypesLower.Contains(exType)) return true;
                    if (c.regexes.Length > 0 && c.regexes.Any(rx => rx.IsMatch(text))) return true;
                    if (c.keywordsLower.Length > 0 && c.keywordsLower.Any(k => k.Length > 0 && textLower.Contains(k))) return true;
                }
            }
            catch { }
            return false;
        }

        public static List<KnownIssueMatch> Match(ErrorContext ctx)
        {
            EnsureBuilt();
            var scored = new List<KeyValuePair<Compiled, int>>();
            try
            {
                string text = ctx.Text ?? "";
                string textLower = text.ToLowerInvariant();
                string exType = ctx.ExceptionType?.ToLowerInvariant();
                var pids = new HashSet<string>(ctx.ImplicatedPackageIds.Select(p => p.ToLowerInvariant()));

                foreach (Compiled c in _all)
                {
                    int score = 0;
                    if (c.exTypesLower.Length > 0 && exType != null && c.exTypesLower.Contains(exType)) score += 3;
                    if (c.regexes.Length > 0 && c.regexes.Any(rx => rx.IsMatch(text))) score += 3;
                    if (c.keywordsLower.Length > 0 && c.keywordsLower.Any(k => k.Length > 0 && textLower.Contains(k))) score += 2;
                    if (c.namespaces.Length > 0 && ctx.Namespaces.Count > 0 &&
                        c.namespaces.Any(ns => ctx.Namespaces.Any(x => x.StartsWith(ns, StringComparison.OrdinalIgnoreCase)))) score += 2;
                    if (c.packageIdsLower.Length > 0 && pids.Count > 0 && c.packageIdsLower.Any(pids.Contains)) score += 2;

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
