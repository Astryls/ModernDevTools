using System;
using System.Collections.Generic;
using System.Diagnostics;
using LudeonTK;
using RimWorld;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// Whole-tab search index for the modern dev-actions window, built PROGRESSIVELY across render frames
    /// (a small time budget per frame) rather than in one synchronous walk. This is what lets search find
    /// actions nested inside categories - including mod-added ones like the "Modern Dev Tools" test actions -
    /// without the up-front freeze a full-tree walk caused (building every category's children, including the
    /// giant spawn/thing grids, stalled for tens of seconds).
    ///
    /// Giant grids (>300 children) are recorded as folders but NOT expanded, so we never label the tens of
    /// thousands of spawn-item nodes (those are searched in place once you drill in). The index is cached
    /// per tab for the session: the first pass over a tab completes within a second or two of the window
    /// being open, and every reopen/keystroke after that filters the cached list instantly.
    /// </summary>
    public static class ActionSearchIndex
    {
        private struct Entry { public DebugActionNode Node; public string LabelLower; }

        private class State
        {
            public readonly List<Entry> Entries = new List<Entry>();
            public Queue<KeyValuePair<DebugActionNode, int>> Frontier;
            public bool Done;
            public long BuildMs;
            public long MaxStepMs;
            public bool Logged;
        }

        private static readonly Dictionary<string, State> _byTab = new Dictionary<string, State>();
        // Category paths already found to be huge spawn/thing grids; never re-expanded.
        private static readonly HashSet<string> _giant = new HashSet<string>();

        private static State Ensure(DebugTabMenuDef tab, DebugActionNode tabRoot)
        {
            string key = tab?.defName ?? "";
            if (_byTab.TryGetValue(key, out State st) && st.Frontier != null) return st;
            st = new State { Frontier = new Queue<KeyValuePair<DebugActionNode, int>>() };
            try
            {
                foreach (DebugActionNode c in DebugTree.Children(tabRoot))
                    st.Frontier.Enqueue(new KeyValuePair<DebugActionNode, int>(c, 0));
            }
            catch { }
            _byTab[key] = st;
            return st;
        }

        /// <summary>Advance the current tab's index by up to budgetMs of work. Call once per frame while the
        /// window is open; a no-op once the tab is fully indexed.</summary>
        public static void Step(DebugTabMenuDef tab, DebugActionNode tabRoot, int budgetMs = 6)
        {
            if (tab == null || tabRoot == null) return;
            State st = Ensure(tab, tabRoot);
            if (st.Done) return;

            var sw = Stopwatch.StartNew();
            try
            {
                while (st.Frontier.Count > 0)
                {
                    KeyValuePair<DebugActionNode, int> kv = st.Frontier.Dequeue();
                    DebugActionNode n = kv.Key;
                    st.Entries.Add(new Entry { Node = n, LabelLower = (DebugTree.Label(n) ?? "").ToLowerInvariant() });

                    if (kv.Value < 6 && DebugTree.IsCategory(n))
                    {
                        string path = DebugTree.PathOf(n);
                        if (path == null || !_giant.Contains(path))
                        {
                            List<DebugActionNode> kids = DebugTree.Children(n);
                            if (kids.Count > 300) { if (path != null) _giant.Add(path); }   // giant grid: index as a folder only
                            else foreach (DebugActionNode child in kids)
                                st.Frontier.Enqueue(new KeyValuePair<DebugActionNode, int>(child, kv.Value + 1));
                        }
                    }

                    if (sw.ElapsedMilliseconds >= budgetMs) break;   // resume next frame
                }
            }
            catch (Exception e) { Log.WarningOnce("[Modern Dev Tools] search index step failed: " + e.Message, 0x2E19C35); }

            sw.Stop();
            st.BuildMs += sw.ElapsedMilliseconds;
            if (sw.ElapsedMilliseconds > st.MaxStepMs) st.MaxStepMs = sw.ElapsedMilliseconds;
            if (st.Frontier.Count == 0)
            {
                st.Done = true;
                if (!st.Logged)
                {
                    st.Logged = true;
                    Log.Message("MDT-DIAG index tab=" + tab.defName + " entries=" + st.Entries.Count
                        + " giantSkipped=" + _giant.Count + " buildMs=" + st.BuildMs + " maxStepMs=" + st.MaxStepMs);
                }
            }
        }

        public static List<DebugActionNode> Filter(DebugTabMenuDef tab, string search, int cap = 400)
        {
            var res = new List<DebugActionNode>();
            if (!_byTab.TryGetValue(tab?.defName ?? "", out State st)) return res;
            string needle = (search ?? "").ToLowerInvariant();
            List<Entry> entries = st.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].LabelLower.IndexOf(needle, StringComparison.Ordinal) >= 0)
                {
                    res.Add(entries[i].Node);
                    if (res.Count >= cap) break;
                }
            }
            return res;
        }

        /// <summary>How many nodes are indexed so far (used to re-filter as the index grows).</summary>
        public static int Count(DebugTabMenuDef tab) =>
            _byTab.TryGetValue(tab?.defName ?? "", out State st) ? st.Entries.Count : 0;
    }
}
