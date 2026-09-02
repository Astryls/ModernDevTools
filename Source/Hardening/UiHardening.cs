using System;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// Experimental hardening for the non-window UI surfaces (world interface, map interface, global
    /// controls). These are called directly from the UI root with NO try/catch, so a throw there -
    /// classically a mod hitting a missing faction or null world data after a modlist change - aborts
    /// the rest of the frame and leaves the world/map with no UI or a broken one. A Harmony finalizer
    /// catches the throw so the rest of the interface still draws. Off by default.
    /// </summary>
    public static class UiHardening
    {
        public static bool MapUiEnabled => ModernDevToolsMod.Settings != null && ModernDevToolsMod.Settings.experimentalMapUiHardening;

        public static Exception GuardMapUi(string surface, Exception ex)
        {
            if (ex == null || !MapUiEnabled) return ex; // off: behave like vanilla (rethrow)
            try { Log.ErrorOnce("[Advanced Dev Tools] hardening caught a UI exception in " + surface + ": " + ex, surface.GetHashCode() ^ 0x2E19D0); }
            catch { }
            return null; // suppress so the rest of the UI keeps drawing
        }
    }
}
