using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// Experimental hardening against windows that break the UI. Two failure modes:
    ///   1. A window's content throws every frame (vanilla catches it in InnerWindowOnGUI, leaving a
    ///      dark "black box" and spamming the log) - detected via the "Exception filling window" message.
    ///   2. A window throws OUTSIDE the content try (background/resizer/drag) - this propagates out of
    ///      the unguarded WindowStackOnGUI loop and aborts EVERY window that frame (freeze/black screen);
    ///      a Harmony finalizer on Window.WindowOnGUI catches it so the rest of the UI keeps drawing.
    /// A window that keeps failing for several frames is force-closed with a notice. Off by default.
    /// </summary>
    public static class WindowWatchdog
    {
        private const int Threshold = 8;   // consecutive failing frames before auto-close

        public static bool Enabled => ModernDevToolsMod.Settings != null && ModernDevToolsMod.Settings.experimentalWindowHardening;

        private class Fail { public int count; public int lastFrame; }
        private static readonly Dictionary<Window, Fail> _fails = new Dictionary<Window, Fail>();
        private static readonly List<Window> _toClose = new List<Window>();
        private static int _lastClean;

        /// <summary>Called by the WindowOnGUI finalizer when a window's OnGUI threw (mode 2).</summary>
        public static void Notify(Window w, Exception ex, string phase)
        {
            if (w == null) return;
            try
            {
                Log.ErrorOnce("[Modern Dev Tools] hardening caught a UI exception in " + w.GetType() + " (" + phase + "): " + ex,
                    w.GetType().GetHashCode() ^ 0x77);
                Count(w);
            }
            catch { }
        }

        /// <summary>Called from a Log.Error postfix to catch the "black box" content failures (mode 1).</summary>
        public static void HandleFillException(string text)
        {
            if (text.NullOrEmpty()) return;
            const string P = "Exception filling window for ";
            if (!text.StartsWith(P, StringComparison.Ordinal)) return;
            try
            {
                int colon = text.IndexOf(':', P.Length);
                string typeName = (colon > 0 ? text.Substring(P.Length, colon - P.Length) : text.Substring(P.Length)).Trim();
                var ws = Find.WindowStack;
                if (ws == null) return;
                foreach (Window w in ws.Windows)
                    if (w.GetType().ToString() == typeName) { Count(w); break; }
            }
            catch { }
        }

        private static void Count(Window w)
        {
            int f = Time.frameCount;
            if (!_fails.TryGetValue(w, out Fail fi)) { fi = new Fail(); _fails[w] = fi; }
            fi.count = (f - fi.lastFrame <= 2) ? fi.count + 1 : 1;
            fi.lastFrame = f;
            if (fi.count == Threshold && !(w is Page) && !_toClose.Contains(w)) _toClose.Add(w);
        }

        /// <summary>Force-close windows that hit the failure threshold. Called after the window loop.</summary>
        public static void DrainCloses()
        {
            if (_toClose.Count > 0)
            {
                var ws = Find.WindowStack;
                for (int i = 0; i < _toClose.Count; i++)
                {
                    Window w = _toClose[i];
                    try
                    {
                        if (ws != null && ws.IsOpen(w))
                        {
                            ws.TryRemove(w, false);
                            Log.Warning("[Modern Dev Tools] closed a window that kept erroring: " + w.GetType());
                            Messages.Message("MDT_WindowClosed".Translate(w.GetType().Name), MessageTypeDefOf.NegativeEvent, false);
                        }
                    }
                    catch { }
                    _fails.Remove(w);
                }
                _toClose.Clear();
            }
            CleanupStale();
        }

        private static void CleanupStale()
        {
            if (Time.frameCount - _lastClean < 300 || _fails.Count == 0) return;
            _lastClean = Time.frameCount;
            List<Window> drop = null;
            foreach (var kv in _fails)
                if (Time.frameCount - kv.Value.lastFrame > 120) (drop ?? (drop = new List<Window>())).Add(kv.Key);
            if (drop != null) foreach (var w in drop) _fails.Remove(w);
        }
    }
}
