using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace ModernDevTools
{
    /// <summary>Which side of the add-on tray a widget draws into.</summary>
    public enum LogWidgetAlign { Left, Right }

    /// <summary>
    /// Draws one add-on control into the log window's tray.
    ///
    /// The signature is DELIBERATELY identical to HugsLib's LogWindowExtensions.WidgetDrawer, so a mod
    /// that already supports HugsLib needs no new code to appear here, and a mod written against this
    /// API can be registered with HugsLib unchanged.
    /// </summary>
    /// <param name="logWindow">The window being drawn (ours, or vanilla's under HugsLib).</param>
    /// <param name="widgetArea">The tray rect.</param>
    /// <param name="selectedMessage">The currently selected log message, or null.</param>
    /// <param name="row">Draw through this to stay aligned with the other add-ons.</param>
    public delegate void LogWidgetDrawer(Window logWindow, Rect widgetArea, LogMessage selectedMessage, WidgetRow row);

    /// <summary>
    /// The log window's add-on tray: a strip of third-party controls along the bottom of the modern log.
    ///
    /// THE PROBLEM THIS SOLVES. HugsLib ships the community's standard extension point for the debug
    /// log - LogWindowExtensions.AddLogWindowWidget - and renders it from a prefix on
    /// EditWindow_Log.DoMessagesListing. HugsLib registers "Share logs", "Files" and "Copy" through it,
    /// and any other mod may register more. Because Modern Dev Tools stops the vanilla window from ever
    /// opening, every one of those buttons silently disappeared: no error, no warning, and the player
    /// concludes HugsLib is broken. Losing "Share logs" in particular breaks the single most common
    /// RimWorld support workflow.
    ///
    /// So we host them. Widgets registered with HugsLib are discovered by reflection and drawn in our
    /// own tray, alongside anything registered natively through ModernDevToolsAPI.
    ///
    /// ON APPEARANCE. Hosted widgets draw themselves with vanilla WidgetRow controls, so they do not
    /// wear the suite's flat chrome. Rather than fight that, the tray is a recessed well: the foreign
    /// styling reads as a deliberate, contained boundary instead of as inconsistency leaking into the
    /// suite. Nothing that existed before changes appearance.
    /// </summary>
    public static class LogWidgets
    {
        private struct Entry
        {
            public string Id;
            public LogWidgetDrawer Drawer;
            public LogWidgetAlign Align;
        }

        private static readonly List<Entry> _native = new List<Entry>();

        // --- HugsLib bridge (reflection; never a compile-time reference) ---------------------------
        private static bool _hugsProbed;
        private static bool _hugsBroken;
        private static FieldInfo _hugsWidgetsField;
        private static FieldInfo _hugsDrawerField;
        private static FieldInfo _hugsAlignField;
        private static readonly List<Entry> _hugsCache = new List<Entry>();
        private static int _hugsSeenCount = -1;

        public const float TrayHeight = 30f;

        /// <summary>Register a control to appear in the modern log's add-on tray.</summary>
        public static void Register(string id, LogWidgetDrawer drawer, LogWidgetAlign align = LogWidgetAlign.Left)
        {
            if (drawer == null) return;
            _native.Add(new Entry { Id = id ?? drawer.Method?.Name ?? "widget", Drawer = drawer, Align = align });
        }

        // Latched per frame. The log window sizes its BODY around whether a tray exists, so if this
        // flipped between OnGUI passes the message list would resize mid-frame, change which rows it
        // culls, and shift every IMGUI control id after it - silently dropping clicks. It can genuinely
        // flip: a hosted widget that throws is removed on the spot (see DrawList).
        private static int _anyFrame = -1;
        private static bool _anyCached;

        /// <summary>True when anything would draw in the tray (so the window can reserve space).</summary>
        public static bool Any
        {
            get
            {
                int f = Time.frameCount;
                if (f == _anyFrame) return _anyCached;
                _anyFrame = f;
                RefreshHugsLib();
                _anyCached = _native.Count > 0 || _hugsCache.Count > 0;
                return _anyCached;
            }
        }

        private static void ProbeHugsLib()
        {
            if (_hugsProbed) return;
            _hugsProbed = true;
            try
            {
                Type t = GenTypes.GetTypeInAnyAssembly("HugsLib.Logs.LogWindowExtensions");
                if (t == null) return;   // HugsLib absent: the normal quiet path.

                _hugsWidgetsField = AccessTools.Field(t, "widgets");
                Type widgetType = t.GetNestedType("LogWindowWidget", BindingFlags.NonPublic | BindingFlags.Public);
                if (widgetType != null)
                {
                    _hugsDrawerField = AccessTools.Field(widgetType, "Drawer");
                    _hugsAlignField = AccessTools.Field(widgetType, "Alignment");
                }

                // HugsLib IS here but its internals moved. Say so once and loudly: a bridge that goes
                // quietly dormant is exactly the failure mode this whole mod exists to catch.
                if (_hugsWidgetsField == null || _hugsDrawerField == null || _hugsAlignField == null)
                {
                    _hugsBroken = true;
                    Log.WarningOnce(
                        "[Modern Dev Tools] HugsLib is present but LogWindowExtensions did not match the expected " +
                        "shape, so its log-window buttons cannot be hosted. Use the \"Vanilla log\" button to reach " +
                        "them. widgets=" + (_hugsWidgetsField != null) + " Drawer=" + (_hugsDrawerField != null) +
                        " Alignment=" + (_hugsAlignField != null), 0x2E19E10);
                }
            }
            catch (Exception e)
            {
                _hugsBroken = true;
                Log.WarningOnce("[Modern Dev Tools] HugsLib log-widget probe failed: " + e.Message, 0x2E19E11);
            }
        }

        /// <summary>
        /// Re-read HugsLib's widget list when its length changes. Mods register during startup, so the
        /// count is stable almost immediately; the check is one int compare per call.
        /// </summary>
        private static void RefreshHugsLib()
        {
            ProbeHugsLib();
            if (_hugsBroken || _hugsWidgetsField == null) return;
            try
            {
                var list = _hugsWidgetsField.GetValue(null) as IList;
                if (list == null) return;
                if (list.Count == _hugsSeenCount) return;
                _hugsSeenCount = list.Count;

                _hugsCache.Clear();
                for (int i = 0; i < list.Count; i++)
                {
                    object item = list[i];
                    if (item == null) continue;
                    var raw = _hugsDrawerField.GetValue(item) as Delegate;
                    if (raw == null) continue;

                    // Rebind HugsLib's delegate to OUR identically-shaped delegate type. This gives a
                    // direct call at draw time - DynamicInvoke would pay full reflection cost on every
                    // widget on every OnGUI pass.
                    LogWidgetDrawer bound;
                    try { bound = (LogWidgetDrawer)Delegate.CreateDelegate(typeof(LogWidgetDrawer), raw.Target, raw.Method); }
                    catch { continue; }

                    object alignObj = _hugsAlignField.GetValue(item);
                    var align = Convert.ToInt32(alignObj) == 1 ? LogWidgetAlign.Right : LogWidgetAlign.Left;
                    _hugsCache.Add(new Entry { Id = "hugslib:" + i, Drawer = bound, Align = align });
                }
            }
            catch (Exception e)
            {
                _hugsBroken = true;
                _hugsCache.Clear();
                Log.WarningOnce("[Modern Dev Tools] could not read HugsLib's log widgets: " + e.Message, 0x2E19E12);
            }
        }

        /// <summary>
        /// Draw the tray. Mirrors HugsLib's own layout: left-aligned widgets grow rightward from the
        /// left edge, right-aligned ones grow leftward from the right edge.
        /// </summary>
        public static void Draw(Window window, Rect area, LogMessage selected)
        {
            RefreshHugsLib();
            if (_native.Count == 0 && _hugsCache.Count == 0) return;

            Palette.DrawWell(area);
            Rect inner = area.ContractedBy(4f);

            GameFont prevFont = Text.Font;
            Text.Font = GameFont.Small;
            var left = new WidgetRow(inner.x, inner.y, UIDirection.RightThenUp, inner.width, 4f);
            var right = new WidgetRow(inner.xMax, inner.y, UIDirection.LeftThenUp, inner.width, 4f);

            DrawList(_native, window, inner, selected, left, right, null);
            DrawList(_hugsCache, window, inner, selected, left, right, _hugsCache);

            Text.Font = prevFont;
        }

        private static void DrawList(List<Entry> entries, Window window, Rect inner, LogMessage selected,
                                     WidgetRow left, WidgetRow right, List<Entry> removeFrom)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                Entry e = entries[i];
                try
                {
                    e.Drawer(window, inner, selected, e.Align == LogWidgetAlign.Right ? right : left);
                }
                catch (Exception ex)
                {
                    // Same policy as HugsLib: a widget that throws is dropped rather than allowed to
                    // break the window every frame. Ours is one of the few places a third-party draw
                    // call runs, so it must never escape.
                    Log.ErrorOnce("[Modern Dev Tools] log add-on '" + e.Id + "' threw and was removed: " + ex,
                                  (e.Id ?? "").GetHashCode() ^ 0x2E19E13);
                    if (removeFrom != null) { removeFrom.RemoveAt(i); i--; }
                    else { entries.RemoveAt(i); i--; }
                }
            }
        }
    }
}
