using System;
using System.Collections.Generic;
using LudeonTK;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// Flat search index for the modern dev-actions window. It walks the tab with DebugTree.BuiltChildren,
    /// which reads only ALREADY-materialized nodes and never fires a lazy childGetter - so the giant spawn/
    /// thing grids are left collapsed (indexed as single drillable folders) while every discrete action,
    /// including mod-added ones like the "Modern Dev Tools" test actions, is captured. Because it touches
    /// no childGetter, the whole build is cheap and runs synchronously on first search: no freeze, and no
    /// per-keystroke work beyond a substring filter over cached, pre-lowercased labels.
    ///
    /// Cached per context (a tab root, or a specific category you drilled into) for the session: when you
    /// enter a spawn grid its children are built by the draw pass, so searching there indexes those items.
    /// </summary>
    public static class ActionSearchIndex
    {
        private struct Entry { public DebugActionNode Node; public string LabelLower; }

        private static readonly Dictionary<string, List<Entry>> _cache = new Dictionary<string, List<Entry>>();

        /// <summary>Matches in the given context, filtered by substring over cached lowercased labels.</summary>
        public static List<DebugActionNode> Filter(string contextKey, DebugActionNode searchRoot, string search, int cap = 400)
        {
            var res = new List<DebugActionNode>();
            List<Entry> entries = Ensure(contextKey, searchRoot);
            string needle = (search ?? "").ToLowerInvariant();
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
                    list.Add(new Entry { Node = n, LabelLower = (DebugTree.Label(n) ?? "").ToLowerInvariant() });
                    // Recurse only into already-built subtrees (BuiltChildren never invokes a childGetter),
                    // so collapsed grids contribute their folder node but not their thousands of items.
                    foreach (DebugActionNode child in DebugTree.BuiltChildren(n)) queue.Enqueue(child);
                }
            }
            catch (Exception e) { Log.WarningOnce("[Modern Dev Tools] search index build failed: " + e.Message, 0x2E19C34); }
            return list;
        }
    }
}
