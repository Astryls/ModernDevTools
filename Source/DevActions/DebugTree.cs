using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// Hardened bridge to RimWorld's debug-action tree (LudeonTK.Dialog_Debug / DebugActionNode).
    /// We reuse the vanilla node graph so every vanilla AND mod-added debug action/item shows up
    /// automatically. Every call into a mod-supplied getter (children, visibility, label, the action
    /// itself) is wrapped so one broken mod action can never break the menu; failures are logged once
    /// and the offending node is flagged.
    /// </summary>
    public static class DebugTree
    {
        private static readonly AccessTools.FieldRef<Dictionary<DebugTabMenuDef, DebugActionNode>> RootsRef = ResolveRoots();
        private static readonly HashSet<string> Broken = new HashSet<string>();

        private static AccessTools.FieldRef<Dictionary<DebugTabMenuDef, DebugActionNode>> ResolveRoots()
        {
            try
            {
                var fi = AccessTools.Field(typeof(Dialog_Debug), "roots");
                if (fi != null) return AccessTools.StaticFieldRefAccess<Dictionary<DebugTabMenuDef, DebugActionNode>>(fi);
            }
            catch (Exception e) { Log.Warning("[Modern Dev Tools] could not bind Dialog_Debug.roots: " + e.Message); }
            return null;
        }

        public static List<DebugTabMenuDef> Tabs()
        {
            var list = DefDatabase<DebugTabMenuDef>.AllDefs.ToList();
            list.SortBy(x => x.displayOrder, y => y.label);
            return list;
        }

        public static DebugActionNode RootOf(DebugTabMenuDef tab)
        {
            try
            {
                Dialog_Debug.TrySetupNodeGraph();
                var roots = RootsRef != null ? RootsRef() : null;
                if (roots != null && tab != null && roots.TryGetValue(tab, out var n)) return n;
            }
            catch (Exception e) { Log.WarningOnce("[Modern Dev Tools] RootOf failed: " + e.Message, 0x2E19C01); }
            return null;
        }

        /// <summary>Visible, sorted children of a node. Guards the mod-supplied childGetter.</summary>
        public static List<DebugActionNode> Children(DebugActionNode node)
        {
            var result = new List<DebugActionNode>();
            if (node == null) return result;
            try
            {
                node.TrySetupChildren();
                node.TrySort();
                foreach (DebugActionNode c in node.children)
                    if (IsVisible(c)) result.Add(c);
            }
            catch (Exception e)
            {
                MarkBroken(node, e, "building its list");
            }
            return result;
        }

        /// <summary>Visible children that are ALREADY materialized - does NOT call TrySetupChildren, so a
        /// lazy childGetter (the giant spawn/thing grids) is never invoked. This is what lets search index
        /// the whole tab cheaply: every discrete action (vanilla and mod-added) is a flat, pre-built node,
        /// while the grids stay collapsed and are searched in place once you drill in.</summary>
        public static List<DebugActionNode> BuiltChildren(DebugActionNode node)
        {
            var result = new List<DebugActionNode>();
            if (node == null) return result;
            try
            {
                node.TrySort();
                List<DebugActionNode> kids = node.children;
                if (kids != null)
                    foreach (DebugActionNode c in kids)
                        if (IsVisible(c)) result.Add(c);
            }
            catch (Exception e) { MarkBroken(node, e, "reading its list"); }
            return result;
        }

        public static bool HasChildren(DebugActionNode node)
        {
            if (node == null) return false;
            try { node.TrySetupChildren(); return node.children.Count > 0; }
            catch (Exception e) { MarkBroken(node, e, "building its list"); return false; }
        }

        /// <summary>Cheap category test that does NOT build the children (a node with a childGetter is a
        /// submenu). Used in the per-row hot path so spawn menus are only built when actually entered.</summary>
        public static bool IsCategory(DebugActionNode node)
        {
            if (node == null) return false;
            try { return node.childGetter != null || node.children.Count > 0; }
            catch { return false; }
        }

        public static bool IsVisible(DebugActionNode node)
        {
            try { return node.VisibleNow; }
            catch { return true; }
        }

        public static bool IsActive(DebugActionNode node)
        {
            try { return node.ActiveNow; }
            catch { return true; }
        }

        public static string Label(DebugActionNode node)
        {
            try { return node.LabelNow ?? node.label ?? ""; }
            catch { return node.label ?? ""; }
        }

        public static string PathOf(DebugActionNode node)
        {
            try { return node.Path; }
            catch { return node?.label ?? ""; }
        }

        private static readonly Dictionary<string, string> PrettyCache = new Dictionary<string, string>();

        /// <summary>Vanilla-parity palette label (mirrors Dialog_DevPalette.PrettifyNodeName): the leaf
        /// label prefixed by its parent chain MINUS the top-level tab, with "..." stripped. So
        /// "Actions\Spawn pawn\Colonist" renders as "Spawn pawn / Colonist", not "Actions / ...".</summary>
        public static string PrettyName(DebugActionNode node)
        {
            if (node == null) return "";
            string path = PathOf(node);
            if (!string.IsNullOrEmpty(path) && PrettyCache.TryGetValue(path, out string cached)) return cached;
            string value;
            try
            {
                DebugActionNode n = node;
                value = Label(n).Replace("...", "");
                // Stop one level below the tab root: parent must not be the graph root, and the
                // grandparent must not be either (that grandparent is the tab node, e.g. "Actions").
                while (n.parent != null && !n.parent.IsRoot && (n.parent.parent == null || !n.parent.parent.IsRoot))
                {
                    value = Label(n.parent).Replace("...", "") + " / " + value;
                    n = n.parent;
                }
            }
            catch { value = Label(node); }
            if (!string.IsNullOrEmpty(path)) PrettyCache[path] = value;
            return value;
        }

        public static bool IsBroken(DebugActionNode node) => node != null && Broken.Contains(PathOf(node));

        /// <summary>Run a leaf action/tool. Closes the window first (parity), routes output to the log,
        /// and shuts the action down with a report if it throws.</summary>
        public static void RunLeaf(DebugActionNode node, Window window)
        {
            if (node == null) return;
            try
            {
                window?.Close();
                bool prev = Log.openOnMessage;
                Log.openOnMessage = true;               // output actions pop the log + select their line
                try { node.Enter(null); }               // dialog null: runs the action / sets the tool, no vanilla dialog
                finally { Log.openOnMessage = prev; }
            }
            catch (Exception e)
            {
                MarkBroken(node, e, "running");
                Messages.Message("MDT_ActionFailed".Translate(Label(node)), MessageTypeDefOf.RejectInput, false);
            }
        }

        // --- checkboxes (Settings tab / any settingsField node) ---

        public static bool IsCheckbox(DebugActionNode node) => node?.settingsField != null;

        public static bool GetCheck(DebugActionNode node)
        {
            try { return node.On; } catch { return false; }
        }

        public static void SetCheck(DebugActionNode node, bool value)
        {
            try
            {
                FieldInfo f = node.settingsField;
                if (f == null) return;
                f.SetValue(null, value);
                node.DirtyLabelCache();
                MethodInfo m = f.DeclaringType.GetMethod(f.Name + "Toggled", BindingFlags.Static | BindingFlags.Public);
                m?.Invoke(null, null);
            }
            catch (Exception e) { MarkBroken(node, e, "toggling"); }
        }

        // --- palette pinning ---

        public static bool IsPinned(DebugActionNode node)
        {
            try { return Prefs.DebugActionsPalette.Contains(PathOf(node)); } catch { return false; }
        }

        public static void TogglePin(DebugActionNode node)
        {
            try { Dialog_DevPalette.ToggleAction(PathOf(node)); }
            catch (Exception e) { Log.WarningOnce("[Modern Dev Tools] toggle pin failed: " + e.Message, 0x2E19C02); }
        }

        // --- thing icon grid detection (Spawn thing / stack / try place / mod spawn menus) ---

        public static bool LooksLikeThingGrid(List<DebugActionNode> children)
        {
            if (children == null || children.Count < 6) return false;
            int check = Mathf.Min(children.Count, 24);
            int hits = 0;
            for (int i = 0; i < check; i++) if (ThingForNode(children[i]) != null) hits++;
            return hits >= check * 0.6f;
        }

        public static ThingDef ThingForNode(DebugActionNode node)
        {
            string s = node?.label;
            if (s.NullOrEmpty()) return null;
            ThingDef d = DefDatabase<ThingDef>.GetNamedSilentFail(s);
            if (d != null) return d;
            int p = s.IndexOf(" (", StringComparison.Ordinal);
            if (p < 0) p = s.IndexOf(" x", StringComparison.Ordinal);
            if (p > 0) d = DefDatabase<ThingDef>.GetNamedSilentFail(s.Substring(0, p));
            return d;
        }

        private static void MarkBroken(DebugActionNode node, Exception e, string phase)
        {
            string path = PathOf(node);
            Broken.Add(path);
            Log.ErrorOnce("[Modern Dev Tools] debug action '" + path + "' threw while " + phase + ": " + e, path.GetHashCode() ^ 0x2E19C0);
        }
    }
}
