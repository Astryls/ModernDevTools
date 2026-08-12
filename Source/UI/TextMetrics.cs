using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// Memoised wrappers for Unity text measurement.
    ///
    /// WHY THIS EXISTS. Every card, row and label in the suite sizes itself with Text.CalcHeight /
    /// Text.CalcSize at draw time - which is correct (hardcoded pixel heights clip glyphs, and the
    /// "disable tiny text" accessibility pref silently changes metrics). But OnGUI runs 2-3 passes per
    /// frame, so a panel drawing 300 cards was performing well over a thousand measurements per frame
    /// on strings that had not changed. Text measurement is the dominant IMGUI cost, and vanilla's
    /// Text.CalcHeight additionally calls text.StripTags() on every invocation, so each call also
    /// allocates.
    ///
    /// KEYING. The result depends on the full text-render state, not just the string: the resolved
    /// GameFont, that font style's fontSize (mods like FontControl mutate Text.fontStyles at runtime),
    /// and WordWrap. Text.Font's GETTER already returns the coerced font - it maps Tiny to Small when
    /// TinyFontSupported is false - so an accessibility-pref change automatically produces a different
    /// key and can never serve a stale measurement. Width is rounded to whole pixels, which is what
    /// the layout uses anyway.
    ///
    /// Set Text.Font (and WordWrap) BEFORE calling, exactly as you would for the vanilla call.
    ///
    /// MAIN THREAD ONLY. The dictionaries are not synchronised, and the underlying Unity measurement is
    /// main-thread-only anyway - every caller is draw code, so this is a documented constraint rather
    /// than a lock we pay for on every lookup.
    /// </summary>
    public static class TextMetrics
    {
        private struct Key : IEquatable<Key>
        {
            public readonly string Text;
            public readonly int Width;      // rounded px, or int.MinValue for unbounded (CalcSize)
            public readonly int Font;
            public readonly int FontSize;
            public readonly bool Wrap;

            public Key(string text, int width, int font, int fontSize, bool wrap)
            {
                Text = text; Width = width; Font = font; FontSize = fontSize; Wrap = wrap;
            }

            public bool Equals(Key o) =>
                Width == o.Width && Font == o.Font && FontSize == o.FontSize && Wrap == o.Wrap
                && string.Equals(Text, o.Text, StringComparison.Ordinal);

            public override bool Equals(object o) => o is Key k && Equals(k);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = Text != null ? Text.GetHashCode() : 0;
                    h = (h * 397) ^ Width;
                    h = (h * 397) ^ Font;
                    h = (h * 397) ^ FontSize;
                    return (h * 397) ^ (Wrap ? 1 : 0);
                }
            }
        }

        // Cap-then-clear rather than LRU: the working set is small and stable (the strings a panel
        // draws), so a clear is rare and costs one rebuild. An unbounded cache would hold every
        // distinct stack trace for the session.
        private const int Cap = 8192;

        private static readonly Dictionary<Key, float> _heights = new Dictionary<Key, float>();
        private static readonly Dictionary<Key, Vector2> _sizes = new Dictionary<Key, Vector2>();
        private static readonly Dictionary<Key, string> _fits = new Dictionary<Key, string>();

        private static Key MakeKey(string text, int width)
        {
            GameFont font = Text.Font;                 // already coerced by the setter
            int size = 0;
            try
            {
                GUIStyle st = Text.fontStyles[(int)font];
                if (st != null) size = st.fontSize;
            }
            catch { }
            return new Key(text ?? "", width, (int)font, size, Text.WordWrap);
        }

        /// <summary>Memoised <see cref="Text.CalcHeight"/>. Same contract: set Text.Font first.</summary>
        public static float Height(string text, float width)
        {
            if (string.IsNullOrEmpty(text)) return 0f;
            Key k = MakeKey(text, Mathf.RoundToInt(width));
            if (_heights.TryGetValue(k, out float h)) return h;
            h = Text.CalcHeight(text, width);
            if (_heights.Count >= Cap) _heights.Clear();
            _heights[k] = h;
            return h;
        }

        /// <summary>Memoised <see cref="Text.CalcHeight"/>, ceiling'd to a whole pixel - the form
        /// almost every layout here actually wants.</summary>
        public static float HeightCeil(string text, float width) => Mathf.Ceil(Height(text, width));

        /// <summary>Memoised <see cref="Text.CalcSize"/>. Same contract: set Text.Font first.</summary>
        public static Vector2 Size(string text)
        {
            if (string.IsNullOrEmpty(text)) return Vector2.zero;
            Key k = MakeKey(text, int.MinValue);
            if (_sizes.TryGetValue(k, out Vector2 v)) return v;
            v = Text.CalcSize(text);
            if (_sizes.Count >= Cap) _sizes.Clear();
            _sizes[k] = v;
            return v;
        }

        /// <summary>
        /// Memoised single-line ellipsis fit - the replacement for vanilla GenText.Truncate(width) in
        /// every per-frame path. The no-cache vanilla Truncate shrinks ONE CHARACTER AT A TIME, paying
        /// one Text.CalcSize plus two string allocations per character removed: a 300-char log line
        /// against a ~550px column is ~200 measurements and ~100KB of garbage per call, and it ran per
        /// visible row per OnGUI pass (2-3 passes/frame). That - not message count - was what made the
        /// modern log feel heavy on giant modlists, whose error lines are almost all wider than the
        /// column. This computes the fit once per (text, width, font state) with a binary search
        /// (~8 measurements), then serves it as a dictionary hit forever after.
        /// Same contract as Size/Height: set Text.Font (and WordWrap) BEFORE calling.
        /// </summary>
        public static string Fit(string text, float width)
        {
            if (string.IsNullOrEmpty(text)) return "";
            Key k = MakeKey(text, Mathf.RoundToInt(width));
            if (_fits.TryGetValue(k, out string s)) return s;
            s = ComputeFit(text, width);
            if (_fits.Count >= Cap) _fits.Clear();
            _fits[k] = s;
            return s;
        }

        private static string ComputeFit(string text, float width)
        {
            if (Size(text).x <= width) return text;   // shares the memoised Size entry
            // Longest prefix whose "prefix..." form fits. Rendered width is monotonically
            // non-decreasing in prefix length, so binary search is valid. Measurements here are raw
            // Text.CalcSize on purpose: the ~8 transient prefixes per fit are never asked for again,
            // and caching them would only churn the size cache. Matches vanilla Truncate's output
            // shape ("..." suffix; bare "..." when nothing fits).
            int lo = 0, hi = text.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (Text.CalcSize(text.Substring(0, mid) + "...").x <= width) lo = mid;
                else hi = mid - 1;
            }
            return text.Substring(0, lo) + "...";
        }

        /// <summary>Drop everything. Only needed if a mod swaps the font ASSETS at runtime (fontSize
        /// changes are already part of the key); exposed so such a mod can call it.</summary>
        public static void Invalidate()
        {
            _heights.Clear();
            _sizes.Clear();
            _fits.Clear();
        }
    }
}
