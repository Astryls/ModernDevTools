using System;
using System.Collections.Generic;
using System.Text;
using LudeonTK;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// Flat search index for the modern dev-actions window. It walks the tab with DebugTree.BuiltChildren,
    /// which reads only ALREADY-materialized nodes and never fires a lazy childGetter - so the giant spawn/
    /// thing grids are left collapsed (indexed as single drillable folders) while every discrete action,
    /// including mod-added ones like the "Advanced Dev Tools" test actions, is captured. Because it touches
    /// no childGetter, the whole build is cheap and runs synchronously on first search: no freeze, and no
    /// per-keystroke work beyond a substring filter over cached, pre-lowercased labels.
    ///
    /// Cached per context (a tab root, or a specific category you drilled into) for the session: when you
    /// enter a spawn grid its children are built by the draw pass, so searching there indexes those items.
    /// </summary>
    public static class ActionSearchIndex
    {
        private struct Entry
        {
            public DebugActionNode Node;
            /// <summary>Lowercased node label PLUS the def's display label when the node spawns a def.
            /// Both must be searchable: the node label is what vanilla stored (a defName for spawn
            /// menus), the display label is what our grid actually draws on screen.</summary>
            public string HayLower;
            /// <summary>Same haystack reduced to letters and digits. This is what lets a player type
            /// what they READ - "simple research bench" - and still hit SimpleResearchBench, whose
            /// defName has no spaces. Without it the spaces alone are enough to lose the item.</summary>
            public string HaySquashed;
        }

        private static readonly Dictionary<string, List<Entry>> _cache = new Dictionary<string, List<Entry>>();

        /// <summary>Matches in the given context, over BOTH the node label and the def's display label,
        /// raw and whitespace/punctuation-insensitive.</summary>
        public static List<DebugActionNode> Filter(string contextKey, DebugActionNode searchRoot, string search, int cap = 400)
        {
            var res = new List<DebugActionNode>();
            List<Entry> entries = Ensure(contextKey, searchRoot);
            string needle = (search ?? "").ToLowerInvariant();
            string squashed = Squash(needle);
            for (int i = 0; i < entries.Count; i++)
            {
                Entry e = entries[i];
                bool hit = e.HayLower.IndexOf(needle, StringComparison.Ordinal) >= 0
                           || (squashed.Length > 0 && e.HaySquashed.IndexOf(squashed, StringComparison.Ordinal) >= 0);
                if (!hit) continue;
                res.Add(e.Node);
                if (res.Count >= cap) break;
            }
            return res;
        }

        /// <summary>Lowercase, keeping only letters and digits. Collapses "Simple research bench",
        /// "SimpleResearchBench" and "simple-research-bench" onto one comparable form.</summary>
        private static string Squash(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        private static List<Entry> Ensure(string contextKey, DebugActionNode searchRoot)
        {
            if (_cache.TryGetValue(contextKey, out List<Entry> cached)) return cached;
            List<Entry> built = Build(searchRoot);
            _cache[contextKey] = built;
            return built;
        }

        private static List<Entry> Build(DebugActionNode root)
        {
            var list = new List<Entry>();
            if (root == null) return list;
            try
            {
                var queue = new Queue<DebugActionNode>();
                foreach (DebugActionNode c in DebugTree.BuiltChildren(root)) queue.Enqueue(c);

                int guard = 0;
                while (queue.Count > 0 && list.Count < 8000 && guard++ < 40000)
                {
                    DebugActionNode n = queue.Dequeue();
                    string nodeLabel = DebugTree.Label(n) ?? "";
                    string shown = DebugTree.DisplayLabelFor(n);
                    string hay = shown.NullOrEmpty() ? nodeLabel : nodeLabel + "\u0001" + shown;
                    list.Add(new Entry
                    {
                        Node = n,
                        HayLower = hay.ToLowerInvariant(),
                        HaySquashed = Squash(hay),
                    });
                    // Recurse only into already-built subtrees (BuiltChildren never invokes a childGetter),
                    // so collapsed grids contribute their folder node but not their thousands of items.
                    foreach (DebugActionNode child in DebugTree.BuiltChildren(n)) queue.Enqueue(child);
                }
            }
            catch (Exception e) { Log.WarningOnce("[Advanced Dev Tools] search index build failed: " + e.Message, 0x2E19C34); }
            return list;
        }
    }
}
