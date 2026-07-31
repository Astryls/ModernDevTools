using System;
using HarmonyLib;
using LudeonTK;
using UnityEngine;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// Single source of truth for the modern log's UI state. Mirrors vanilla's static
    /// EditWindow_Log state (so selection/filters survive window close), and bridges the
    /// toggles that live on vanilla types (auto-open, pause-on-error, open-on-warnings)
    /// so those behaviors stay identical to the vanilla log.
    /// </summary>
    public static class LogState
    {
        // Selection + view state (persist across open/close, like vanilla statics).
        private static LogMessage _selected;

        /// <summary>The message shown in the inspector.</summary>
        public static LogMessage Selected
        {
            get { return _selected; }
            set
            {
                if (_selected == value) return;
                _selected = value;
                Action<LogMessage> handler = SelectionChanged;
                if (handler == null) return;
                try { handler(value); }
                catch (Exception e) { Log.WarningOnce("[Modern Dev Tools] a selection-changed handler threw: " + e.Message, 0x2E19A60); }
            }
        }

        /// <summary>
        /// Raised when the inspected message changes. Interop layers subscribe to mirror the selection
        /// into surfaces other mods read - notably EditWindow_Log's private static selectedMessage,
        /// which Archotech Logs' diagnostics view reads directly rather than asking its host window.
        /// An event rather than a direct call so this core type stays independent of the interop layer.
        /// </summary>
        public static event Action<LogMessage> SelectionChanged;
        public static bool ShowMessages = true;
        public static bool ShowWarnings = true;
        public static bool ShowErrors = true;
        public static string Search = "";
        public static Vector2 ListScroll = Vector2.zero;
        public static Vector2 InspectorScroll = Vector2.zero;

        // Bumped whenever the log queue changes (message enqueued / cleared) so the window only
        // rebuilds its filtered view on real changes instead of every frame.
        public static int Revision;

        // Cached ref to EditWindow_Log.canAutoOpen (private static) so our "auto-open" toggle
        // drives the exact same gate vanilla's TryAutoOpen() reads.
        private static readonly AccessTools.FieldRef<bool> CanAutoOpenRef = ResolveCanAutoOpen();
        private static bool _autoOpenFallback = true;

        private static AccessTools.FieldRef<bool> ResolveCanAutoOpen()
        {
            try
            {
                var fi = AccessTools.Field(typeof(EditWindow_Log), "canAutoOpen");
                if (fi != null) return AccessTools.StaticFieldRefAccess<bool>(fi);
            }
            catch (Exception e)
            {
                Log.WarningOnce("[Modern Dev Tools] Could not bind EditWindow_Log.canAutoOpen: " + e.Message, 0x2E19A01);
            }
            return null;
        }

        /// <summary>Auto-open on error. Reads/writes vanilla's canAutoOpen gate when available.</summary>
        public static bool AutoOpen
        {
            get { try { return CanAutoOpenRef != null ? CanAutoOpenRef() : _autoOpenFallback; } catch { return _autoOpenFallback; } }
            set { try { if (CanAutoOpenRef != null) CanAutoOpenRef() = value; else _autoOpenFallback = value; } catch { _autoOpenFallback = value; } }
        }

        /// <summary>Pause the game when an error is logged (vanilla DebugSettings.pauseOnError).</summary>
        public static bool PauseOnError
        {
            get { return DebugSettings.pauseOnError; }
            set { DebugSettings.pauseOnError = value; }
        }

        /// <summary>Also auto-open on warnings, not just errors (vanilla Prefs.OpenLogOnWarnings).</summary>
        public static bool OpenOnWarnings
        {
            get { return Prefs.OpenLogOnWarnings; }
            set { if (Prefs.OpenLogOnWarnings != value) { Prefs.OpenLogOnWarnings = value; Prefs.Save(); } }
        }

        public static bool VisibleType(LogMessageType t)
        {
            switch (t)
            {
                case LogMessageType.Message: return ShowMessages;
                case LogMessageType.Warning: return ShowWarnings;
                case LogMessageType.Error: return ShowErrors;
                default: return true;
            }
        }

        public static void ClearSelection()
        {
            Selected = null;
            InspectorScroll = Vector2.zero;
        }
    }
}
