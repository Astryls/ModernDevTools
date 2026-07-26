using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// Boots Harmony once, on the main thread, after all assemblies load. Patch classes are applied
    /// one at a time so a single bad target can never take the whole mod down (resilient-boot idiom).
    /// </summary>
    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        static Bootstrap()
        {
            var harmony = new Harmony("astryl.moderndevtools");
            Type[] types;
            try { types = Assembly.GetExecutingAssembly().GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }

            foreach (Type t in types)
            {
                if (!t.IsDefined(typeof(HarmonyPatch), false)) continue;
                try { harmony.CreateClassProcessor(t).Patch(); }
                catch (Exception e) { Log.Warning("[Modern Dev Tools] skipped patch class " + t.Name + ": " + e.Message); }
            }

            TryShowUpdateNotes();
            try { CommunityData.LoadCache(); } catch { }

            // Prewarm the mod/namespace indexes off the main thread. The first error analysis enumerates
            // every mod assembly's types (BuildNamespaceRoots); on a large modlist that is a multi-second
            // freeze, and it otherwise lands right when the launch-time auto-opened log is read/closed.
            try
            {
                var prewarm = new System.Threading.Thread(() => { try { InstalledModIndex.Prewarm(); } catch { } })
                { IsBackground = true, Name = "MDT-PrewarmIndex" };
                prewarm.Start();
            }
            catch { }
        }

        private static void TryShowUpdateNotes()
        {
            try
            {
                var s = ModernDevToolsMod.Settings;
                if (s == null || s.lastSeenVersion == ModernDevToolsMod.Version) return;
                string prev = s.lastSeenVersion;
                s.lastSeenVersion = ModernDevToolsMod.Version;
                ModernDevToolsMod.Instance?.WriteSettings();
                LongEventHandler.ExecuteWhenFinished(delegate
                {
                    try { Find.WindowStack?.Add(new Dialog_UpdateNotes(prev)); } catch { }
                });
            }
            catch (Exception e) { Log.Warning("[Modern Dev Tools] update notes check failed: " + e.Message); }
        }
    }

    /// <summary>Auto-open on error/warning: redirect vanilla's poll to open our window instead.</summary>
    [HarmonyPatch(typeof(UIRoot), "CheckOpenLogWindow")]
    public static class Patch_CheckOpenLogWindow
    {
        static bool Prefix()
        {
            try
            {
                if (EditWindow_Log.wantsToOpen)
                {
                    Window_ModernLog.OpenIfNeeded();
                    EditWindow_Log.wantsToOpen = false;
                }
                return false; // fully replaces vanilla's poll
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[Modern Dev Tools] auto-open redirect failed: " + e, 0x2E19A20);
                return true; // fail open to vanilla auto-open
            }
        }
    }

    /// <summary>Dev toolbar button + the toggle-debug-log hotkey both call this. Redirect to our window.</summary>
    [HarmonyPatch(typeof(DebugWindowsOpener), "ToggleLogWindow")]
    public static class Patch_ToggleLogWindow
    {
        static bool Prefix()
        {
            try { Window_ModernLog.Toggle(); return false; }
            catch (Exception e)
            {
                Log.ErrorOnce("[Modern Dev Tools] toggle redirect failed: " + e, 0x2E19A21);
                return true; // fail open to vanilla toggle
            }
        }
    }

    /// <summary>
    /// Parity for the openOnMessage dev flow: Log.PostMessage calls SelectLastMessage to select the
    /// newest entry and expand its details. Mirror that onto our own selection state.
    /// </summary>
    [HarmonyPatch(typeof(EditWindow_Log), nameof(EditWindow_Log.SelectLastMessage))]
    public static class Patch_SelectLastMessage
    {
        static bool Prefix(bool expandDetailsPane)
        {
            try
            {
                LogState.Selected = Log.Messages.LastOrDefault();
                LogState.InspectorScroll = UnityEngine.Vector2.zero;
                LogState.ListScroll = new UnityEngine.Vector2(0f, float.MaxValue); // window clamps to bottom
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[Modern Dev Tools] select-last redirect failed: " + e, 0x2E19A22);
                return true;
            }
        }
    }

    /// <summary>Dev toolbar buttons + hotkeys for the three debug tabs -> our modern dev window.</summary>
    [HarmonyPatch(typeof(DebugWindowsOpener), "ToggleDebugActionsMenu")]
    public static class Patch_ToggleDebugActionsMenu
    {
        static bool Prefix()
        {
            try { Diag.Mark("toggle-actions-menu pressed"); Window_ModernDevActions.OpenOrSwitch(DebugTabMenuDefOf.Actions); Diag.Mark("toggle-actions-menu returned"); return false; }
            catch (Exception e) { Log.ErrorOnce("[Modern Dev Tools] actions redirect failed: " + e, 0x2E19A23); return true; }
        }
    }

    [HarmonyPatch(typeof(DebugWindowsOpener), "ToggleDebugSettingsMenu")]
    public static class Patch_ToggleDebugSettingsMenu
    {
        static bool Prefix()
        {
            try { Window_ModernDevActions.OpenOrSwitch(DebugTabMenuDefOf.Settings); return false; }
            catch (Exception e) { Log.ErrorOnce("[Modern Dev Tools] settings redirect failed: " + e, 0x2E19A24); return true; }
        }
    }

    [HarmonyPatch(typeof(DebugWindowsOpener), "ToggleDebugLogMenu")]
    public static class Patch_ToggleDebugLogMenu
    {
        static bool Prefix()
        {
            try { Window_ModernDevActions.OpenOrSwitch(DebugTabMenuDefOf.Output); return false; }
            catch (Exception e) { Log.ErrorOnce("[Modern Dev Tools] output redirect failed: " + e, 0x2E19A25); return true; }
        }
    }

    /// <summary>Dev palette toggle (toolbar + hotkey) -> our suite-styled palette.</summary>
    [HarmonyPatch(typeof(DebugWindowsOpener), nameof(DebugWindowsOpener.TryOpenOrClosePalette))]
    public static class Patch_TryOpenOrClosePalette
    {
        static bool Prefix()
        {
            try
            {
                var ws = Find.WindowStack;
                if (ws == null) return false;
                if (DebugSettings.devPalette)
                {
                    if (!ws.IsOpen<Window_ModernDevPalette>()) ws.Add(new Window_ModernDevPalette());
                }
                else ws.TryRemove(typeof(Window_ModernDevPalette), false);
                return false;
            }
            catch (Exception e) { Log.ErrorOnce("[Modern Dev Tools] palette redirect failed: " + e, 0x2E19A26); return true; }
        }
    }

    /// <summary>Bump the change token when a message is enqueued so the window knows to rebuild.</summary>
    [HarmonyPatch]
    public static class Patch_Enqueue_Revision
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(LogMessageQueue), "Enqueue",
                new[] { typeof(LogMessage), typeof(bool).MakeByRefType() });
        }

        static void Postfix()
        {
            unchecked { LogState.Revision++; }
        }
    }

    /// <summary>Bump the change token (and drop selection) when the log is cleared.</summary>
    [HarmonyPatch(typeof(LogMessageQueue), "Clear")]
    public static class Patch_QueueClear_Revision
    {
        static void Postfix()
        {
            unchecked { LogState.Revision++; }
            LogState.ClearSelection();
        }
    }

    // === Experimental window hardening (all gated by the setting; no-op when off) ===

    /// <summary>Isolate a throwing window so it can't abort the whole window loop (freeze/black screen).</summary>
    [HarmonyPatch(typeof(Window), nameof(Window.WindowOnGUI))]
    public static class Patch_Window_WindowOnGUI_Harden
    {
        static Exception Finalizer(Exception __exception, Window __instance)
        {
            if (__exception == null || !WindowWatchdog.Enabled) return __exception; // off: behave like vanilla
            WindowWatchdog.Notify(__instance, __exception, "WindowOnGUI");
            return null; // suppress so the rest of the windows still draw this frame
        }
    }

    /// <summary>Catch the "black box" case: vanilla logs "Exception filling window for X" per frame.</summary>
    [HarmonyPatch(typeof(Log), nameof(Log.Error), new[] { typeof(string) })]
    public static class Patch_Log_Error_FillDetector
    {
        static void Postfix(string text)
        {
            if (WindowWatchdog.Enabled) WindowWatchdog.HandleFillException(text);
        }
    }

    /// <summary>Force-close persistently-broken windows at a safe point (after the window loop).</summary>
    [HarmonyPatch(typeof(WindowStack), nameof(WindowStack.WindowStackOnGUI))]
    public static class Patch_WindowStackOnGUI_Drain
    {
        static void Postfix()
        {
            if (WindowWatchdog.Enabled) WindowWatchdog.DrainCloses();
        }
    }

    // === Experimental world/map UI hardening (second toggle; no-op when off) ===

    /// <summary>Isolate a throw in the world map UI so the world screen isn't left blank/broken.</summary>
    [HarmonyPatch(typeof(WorldInterface), nameof(WorldInterface.WorldInterfaceOnGUI))]
    public static class Patch_WorldInterfaceOnGUI_Harden
    { static Exception Finalizer(Exception __exception) => UiHardening.GuardMapUi("world interface", __exception); }

    [HarmonyPatch(typeof(MapInterface), nameof(MapInterface.MapInterfaceOnGUI_BeforeMainTabs))]
    public static class Patch_MapInterfaceBefore_Harden
    { static Exception Finalizer(Exception __exception) => UiHardening.GuardMapUi("map interface", __exception); }

    [HarmonyPatch(typeof(MapInterface), nameof(MapInterface.MapInterfaceOnGUI_AfterMainTabs))]
    public static class Patch_MapInterfaceAfter_Harden
    { static Exception Finalizer(Exception __exception) => UiHardening.GuardMapUi("map interface", __exception); }

    /// <summary>The bottom-right controls (time/date/weather/letters) commonly break on faction issues.</summary>
    [HarmonyPatch(typeof(GlobalControls), nameof(GlobalControls.GlobalControlsOnGUI))]
    public static class Patch_GlobalControlsOnGUI_Harden
    { static Exception Finalizer(Exception __exception) => UiHardening.GuardMapUi("global controls", __exception); }
}
