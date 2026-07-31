using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// The ONE implementation of "how well does this error match a curated issue entry".
    ///
    /// This scoring drives four surfaces that must agree: the shipped KnownIssueDef library, entries a
    /// mod ships in its own About folder, entries registered through the runtime API, and the community
    /// database. It used to exist twice - once over KnownIssueDef's compiled arrays and once over
    /// RemoteIssue's - with the same weights written out by hand in both. Any change to a weight had to
    /// be made in both places or the surfaces would silently disagree about what counts as a match.
    ///
    /// The signals are deliberately additive rather than exclusive: an entry that matches on both an
    /// exception type and a keyword is a better match than one that matches on either alone.
    /// </summary>
    public static class IssueScoring
    {
        /// <summary>Exception type matches exactly. Strong: the type is structural, not prose.</summary>
        public const int ExceptionTypeWeight = 3;

        /// <summary>A regex matches. Strong: patterns are authored to be specific.</summary>
        public const int RegexWeight = 3;

        /// <summary>A keyword appears in the text. Weaker: substrings collide across unrelated errors.</summary>
        public const int KeywordWeight = 2;

        /// <summary>A namespace from the stack trace is covered by the entry.</summary>
        public const int NamespaceWeight = 2;

        /// <summary>An implicated mod's packageId is named by the entry.</summary>
        public const int PackageIdWeight = 2;

        /// <summary>
        /// Score one entry against one error. Returns 0 when nothing matched.
        /// </summary>
        /// <param name="text">Raw message text (regexes run against this).</param>
        /// <param name="exTypeLower">Lowercased exception type from the message, or null.</param>
        /// <param name="namespaces">Namespaces seen in the stack trace; may be null/empty.</param>
        /// <param name="implicatedPidsLower">Lowercased packageIds implicated so far; may be null/empty.</param>
        /// <param name="entryExTypesLower">Entry's exception types, pre-lowercased.</param>
        /// <param name="entryRegexes">Entry's compiled regexes.</param>
        /// <param name="entryKeywordsLower">Entry's keywords, pre-lowercased.</param>
        /// <param name="entryNamespaces">Entry's namespace prefixes.</param>
        /// <param name="entryPidsLower">Entry's packageIds, pre-lowercased.</param>
        public static int Score(
            string text,
            string exTypeLower,
            ICollection<string> namespaces,
            ICollection<string> implicatedPidsLower,
            string[] entryExTypesLower,
            Regex[] entryRegexes,
            string[] entryKeywordsLower,
            string[] entryNamespaces,
            string[] entryPidsLower)
        {
            int score = 0;
            text = text ?? "";

            if (entryExTypesLower != null && entryExTypesLower.Length > 0 && exTypeLower != null
                && Array.IndexOf(entryExTypesLower, exTypeLower) >= 0)
                score += ExceptionTypeWeight;

            if (entryRegexes != null)
                for (int i = 0; i < entryRegexes.Length; i++)
                    if (entryRegexes[i].IsMatch(text)) { score += RegexWeight; break; }

            // OrdinalIgnoreCase IndexOf rather than a lowercased copy of the text: this runs for every
            // entry in the pool, and allocating a full copy of a long message per call added up.
            if (entryKeywordsLower != null)
                for (int i = 0; i < entryKeywordsLower.Length; i++)
                {
                    string k = entryKeywordsLower[i];
                    if (k != null && k.Length > 0 && text.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
                    { score += KeywordWeight; break; }
                }

            if (entryNamespaces != null && entryNamespaces.Length > 0 && namespaces != null && namespaces.Count > 0)
            {
                bool hit = false;
                foreach (string ns in entryNamespaces)
                {
                    if (ns.NullOrEmpty()) continue;
                    foreach (string seen in namespaces)
                        if (seen != null && seen.StartsWith(ns, StringComparison.OrdinalIgnoreCase)) { hit = true; break; }
                    if (hit) break;
                }
                if (hit) score += NamespaceWeight;
            }

            if (entryPidsLower != null && entryPidsLower.Length > 0 && implicatedPidsLower != null && implicatedPidsLower.Count > 0)
                foreach (string p in entryPidsLower)
                    if (p != null && implicatedPidsLower.Contains(p)) { score += PackageIdWeight; break; }

            return score;
        }
    }

    /// <summary>Small helpers that were copy-pasted across the library, community and shipped-issue code.</summary>
    public static class IssueTextUtil
    {
        /// <summary>Strip a UTF-8 BOM that survived into a decoded string (breaks JSON parsing).</summary>
        public static string StripBom(string s) => (!s.NullOrEmpty() && s[0] == '\uFEFF') ? s.Substring(1) : s;

        /// <summary>Lowercase every element into a new array (null/empty input yields an empty array).</summary>
        public static string[] Lower(IList<string> src)
        {
            if (src == null || src.Count == 0) return Array.Empty<string>();
            var arr = new string[src.Count];
            for (int i = 0; i < src.Count; i++) arr[i] = src[i]?.ToLowerInvariant() ?? "";
            return arr;
        }

        /// <summary>Lowercase in place and return the same array.</summary>
        public static string[] LowerInPlace(string[] arr)
        {
            if (arr == null) return Array.Empty<string>();
            for (int i = 0; i < arr.Length; i++) arr[i] = arr[i]?.ToLowerInvariant() ?? "";
            return arr;
        }

        /// <summary>
        /// Compile a set of patterns, skipping (and reporting once) any that are malformed. A bad
        /// pattern in one entry must never take down the whole pool.
        ///
        /// NOTE on RegexOptions.Compiled - deliberately NOT used here. On Unity's Mono runtime Compiled
        /// emits IL through Reflection.Emit at construction time, and the generated dynamic methods are
        /// never reclaimed. Across ~100 shipped, community and mod-supplied patterns that is a real
        /// startup cost for patterns that now run only once per ANALYSED MESSAGE (the analysis is cached
        /// per message), not per frame. This is reasoned rather than measured - if a profile ever shows
        /// matching to be hot, put Compiled back on this one call. The genuinely per-line patterns in
        /// FrameParser deliberately keep it.
        /// </summary>
        public static Regex[] CompileRegexes(IList<string> patterns, string ownerId)
        {
            if (patterns == null || patterns.Count == 0) return Array.Empty<Regex>();
            var list = new List<Regex>(patterns.Count);
            foreach (string pat in patterns)
            {
                if (pat.NullOrEmpty()) continue;
                try { list.Add(new Regex(pat, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)); }
                catch (Exception e)
                {
                    Log.WarningOnce("[Modern Dev Tools] bad regex in " + (ownerId ?? "?") + ": " + e.Message,
                                          (ownerId ?? "").GetHashCode() ^ 0x2E19E30);
                }
            }
            return list.ToArray();
        }

        /// <summary>Stable key for an unordered pair of packageIds (A|B == B|A).</summary>
        public static string PairKey(string a, string b)
        {
            string la = (a ?? "").ToLowerInvariant(), lb = (b ?? "").ToLowerInvariant();
            return string.CompareOrdinal(la, lb) <= 0 ? la + "|" + lb : lb + "|" + la;
        }
    }
}
