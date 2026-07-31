using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace ModernDevTools
{
    /// <summary>
    /// Installs and removes the experimental hardening patches ON DEMAND, driven by their settings.
    ///
    /// WHY NOT JUST GUARD THE BODIES. Both hardening features default to OFF, but the patches used to
    /// be installed unconditionally with an `if (!Enabled) return;` guard. A guard makes the body free;
    /// it cannot make Harmony's DISPATCH free, and these sit on the hottest UI methods in the game:
    /// Window.WindowOnGUI runs for every open window on every OnGUI pass, WindowStack.WindowStackOnGUI
    /// every frame, and Log.Error for every error raised by any mod. A finalizer additionally forces its
    /// target into a try/catch wrapper. Paying all of that for a feature the player has turned off is
    /// pure waste, so the patches are now applied only while their feature is on.
    ///
    /// WHY THE APPLY IS DEFERRED. Patching or unpatching a method that is CURRENTLY ON THE STACK is the
    /// one genuinely dangerous use of Harmony's runtime API - and the settings UI that toggles these
    /// runs inside Window.WindowOnGUI and WindowStack.WindowStackOnGUI, two of our own targets. So the
    /// toggle only records intent; the actual patching happens from a postfix on Verse.Root.Update.
    /// Root_Entry.Update and Root_Play.Update both call base.Update(), so that one hook fires once per
    /// frame in BOTH the main menu and in game, during Unity's Update phase where no OnGUI method is on
    /// the stack.
    /// </summary>
    public static class HardeningPatches
    {
        private static Harmony _harmony;
        private static bool _windowOn;
        private static bool _mapUiOn;
        private static bool _throttleOn;
        private static bool _trayStyleOn;

        /// <summary>Latched after any patch/unpatch failure so a broken state cannot retry every frame.</summary>
        private static bool _broken;

        internal static void Init(Harmony harmony)
        {
            _harmony = harmony;
            SyncIfNeeded();
        }

        private static bool WantWindow =>
            ModernDevToolsMod.Settings != null && ModernDevToolsMod.Settings.experimentalWindowHardening;

        private static bool WantMapUi =>
            ModernDevToolsMod.Settings != null && ModernDevToolsMod.Settings.experimentalMapUiHardening;

        // The log throttle is on-demand for exactly the same reason as the hardening patches: it sits on
        // Log.Error/Warning/Message - the error path of every mod in the game - and defaults to off.
        private static bool WantThrottle => LogThrottle.Enabled;

        // The tray re-skin patches Widgets.ButtonText, which the whole game uses, so it is only
        // installed once the log window actually has add-on widgets to host. Once true this stays true
        // (registrations happen at startup), so it installs once and never churns.
        private static bool WantTrayStyle => LogWidgets.Any;

        /// <summary>
        /// Reconcile installed patches with the settings. Cheap no-op in the common case (two bool
        /// compares), so it is safe to call every frame from the Root.Update postfix.
        /// </summary>
        public static void SyncIfNeeded()
        {
            if (_broken || _harmony == null) return;
            bool wantWindow = WantWindow, wantMap = WantMapUi, wantThrottle = WantThrottle, wantTray = WantTrayStyle;
            if (wantWindow == _windowOn && wantMap == _mapUiOn && wantThrottle == _throttleOn
                && wantTray == _trayStyleOn) return;

            try
            {
                if (wantWindow != _windowOn)
                {
                    ApplyGroup(WindowTargets(), wantWindow);
                    _windowOn = wantWindow;
                }
                if (wantMap != _mapUiOn)
                {
                    ApplyGroup(MapUiTargets(), wantMap);
                    _mapUiOn = wantMap;
                }
                if (wantThrottle != _throttleOn)
                {
                    ApplyGroup(ThrottleTargets(), wantThrottle);
                    _throttleOn = wantThrottle;
                }
                if (wantTray != _trayStyleOn)
                {
                    ApplyGroup(TrayStyleTargets(), wantTray);
                    _trayStyleOn = wantTray;
                }
            }
            catch (Exception e)
            {
                _broken = true;
                Log.Error("[Modern Dev Tools] hardening patch sync failed; hardening disabled for this session: " + e);
            }
        }

        // --- target table ---------------------------------------------------------------------------

        private struct Target
        {
            public MethodBase Original;
            public MethodInfo Patch;
            public bool IsFinalizer;
            public bool IsPrefix;
        }

        private static Target Fin(MethodBase original, string patchName) =>
            new Target { Original = original, Patch = Self(patchName), IsFinalizer = true };

        private static Target Post(MethodBase original, string patchName) =>
            new Target { Original = original, Patch = Self(patchName), IsFinalizer = false, IsPrefix = false };

        private static Target Pre(MethodBase original, string patchName) =>
            new Target { Original = original, Patch = Self(patchName), IsPrefix = true };

        private static MethodInfo Self(string name) => AccessTools.Method(typeof(HardeningPatches), name);

        private static List<Target> WindowTargets() => new List<Target>
        {
            Fin(AccessTools.Method(typeof(Window), nameof(Window.WindowOnGUI)), nameof(Window_Finalizer)),
            Post(AccessTools.Method(typeof(Log), nameof(Log.Error), new[] { typeof(string) }), nameof(LogError_Postfix)),
            Post(AccessTools.Method(typeof(WindowStack), nameof(WindowStack.WindowStackOnGUI)), nameof(WindowStack_Postfix)),
        };

        private static List<Target> MapUiTargets() => new List<Target>
        {
            Fin(AccessTools.Method(typeof(WorldInterface), nameof(WorldInterface.WorldInterfaceOnGUI)), nameof(World_Finalizer)),
            Fin(AccessTools.Method(typeof(MapInterface), nameof(MapInterface.MapInterfaceOnGUI_BeforeMainTabs)), nameof(MapBefore_Finalizer)),
            Fin(AccessTools.Method(typeof(MapInterface), nameof(MapInterface.MapInterfaceOnGUI_AfterMainTabs)), nameof(MapAfter_Finalizer)),
            Fin(AccessTools.Method(typeof(GlobalControls), nameof(GlobalControls.GlobalControlsOnGUI)), nameof(GlobalControls_Finalizer)),
        };

        private static List<Target> ThrottleTargets() => new List<Target>
        {
            Pre(AccessTools.Method(typeof(Log), nameof(Log.Error), new[] { typeof(string) }), nameof(LogError_ThrottlePrefix)),
            Pre(AccessTools.Method(typeof(Log), nameof(Log.Warning), new[] { typeof(string) }), nameof(LogWarning_ThrottlePrefix)),
            Pre(AccessTools.Method(typeof(Log), nameof(Log.Message), new[] { typeof(string) }), nameof(LogMessage_ThrottlePrefix)),
        };

        // The 7-argument overload is the funnel: the shorter public ButtonText overload calls straight
        // into it, so patching this one covers every call path.
        private static List<Target> TrayStyleTargets() => new List<Target>
        {
            Pre(AccessTools.Method(typeof(Widgets), nameof(Widgets.ButtonText), new[]
                {
                    typeof(Rect), typeof(string), typeof(bool), typeof(bool),
                    typeof(Color), typeof(bool), typeof(TextAnchor?)
                }), nameof(ButtonText_TrayStylePrefix)),
        };

        private static void ApplyGroup(List<Target> targets, bool install)
        {
            foreach (Target t in targets)
            {
                if (t.Original == null || t.Patch == null)
                {
                    // A missing target is survivable - skip it rather than disabling the whole feature.
                    Log.WarningOnce("[Modern Dev Tools] hardening target missing; skipping one patch.", 0x2E19D40);
                    continue;
                }
                if (install)
                {
                    var hm = new HarmonyMethod(t.Patch);
                    if (t.IsFinalizer) _harmony.Patch(t.Original, finalizer: hm);
                    else if (t.IsPrefix) _harmony.Patch(t.Original, prefix: hm);
                    else _harmony.Patch(t.Original, postfix: hm);
                }
                else
                {
                    _harmony.Unpatch(t.Original, t.Patch);
                }
            }
        }

        // --- patch bodies ---------------------------------------------------------------------------
        // These carry NO [HarmonyPatch] attribute on purpose: Bootstrap's class scan must not install
        // them. They keep their own Enabled checks as belt-and-braces, because the setting can change
        // in the window between a toggle and the next Root.Update sync.

        public static Exception Window_Finalizer(Exception __exception, Window __instance)
        {
            if (__exception == null || !WindowWatchdog.Enabled) return __exception;
            WindowWatchdog.Notify(__instance, __exception, "WindowOnGUI");
            return null;   // suppress so the rest of the windows still draw this frame
        }

        public static void LogError_Postfix(string text)
        {
            if (WindowWatchdog.Enabled) WindowWatchdog.HandleFillException(text);
        }

        public static void WindowStack_Postfix()
        {
            if (WindowWatchdog.Enabled) WindowWatchdog.DrainCloses();
        }

        // Throttle prefixes. Returning false skips the whole vanilla body - the stack-trace extraction,
        // the LogMessage allocation, the Debug.Log* disk write and the auto-open/pause-on-error side
        // effects. They re-check Enabled so a toggle takes effect immediately rather than at the next
        // Root.Update sync.
        public static bool LogError_ThrottlePrefix(string text) =>
            !LogThrottle.Enabled || LogThrottle.ShouldLog(text, LogMessageType.Error);

        public static bool LogWarning_ThrottlePrefix(string text) =>
            !LogThrottle.Enabled || LogThrottle.ShouldLog(text, LogMessageType.Warning);

        public static bool LogMessage_ThrottlePrefix(string text) =>
            !LogThrottle.Enabled || LogThrottle.ShouldLog(text, LogMessageType.Message);

        /// <summary>
        /// Re-skins buttons drawn by hosted log add-ons into the suite's flat gray, so HugsLib's
        /// "Share logs" / "Files" / "Copy" match the rest of the window instead of arriving in vanilla
        /// tan and green.
        ///
        /// Only fires while LogWidgets.Drawing is set - one tray, for a handful of frames - so every
        /// other button in the game is untouched.
        ///
        /// Control-count safe: vanilla's ButtonTextWorker emits exactly one ButtonInvisible when
        /// active (and none when inactive), and Palette.GrayButton does exactly the same, so swapping
        /// the draw cannot shift any later IMGUI control id. WidgetRow computes the rect and advances
        /// its own cursor before calling us, and registers its tooltip afterwards, so layout and
        /// tooltips are unaffected. drawBackground:false means the caller wanted a bare text link, not
        /// a button - that intent is preserved by falling through to vanilla.
        /// </summary>
        public static bool ButtonText_TrayStylePrefix(Rect rect, string label, bool drawBackground,
                                                     bool doMouseoverSound, bool active, ref bool __result)
        {
            if (!LogWidgets.Drawing || !drawBackground) return true;
            try
            {
                if (doMouseoverSound) MouseoverSounds.DoRegion(rect);
                __result = Palette.GrayButton(rect, label, null, active);
                return false;
            }
            catch
            {
                return true;   // anything unexpected: let vanilla draw it rather than lose the button
            }
        }

        public static Exception World_Finalizer(Exception __exception) => UiHardening.GuardMapUi("world interface", __exception);
        public static Exception MapBefore_Finalizer(Exception __exception) => UiHardening.GuardMapUi("map interface", __exception);
        public static Exception MapAfter_Finalizer(Exception __exception) => UiHardening.GuardMapUi("map interface", __exception);
        public static Exception GlobalControls_Finalizer(Exception __exception) => UiHardening.GuardMapUi("global controls", __exception);
    }

    /// <summary>
    /// The one permanently-installed hook: a per-frame Update-phase tick used to apply pending
    /// hardening patch changes at a point where none of the target methods are on the stack.
    /// Root_Entry.Update and Root_Play.Update both chain to Root.Update, so this covers the main menu
    /// and in-game alike. The body is two bool compares in the common case.
    /// </summary>
    [HarmonyPatch(typeof(Root), nameof(Root.Update))]
    public static class Patch_Root_Update_HardeningSync
    {
        static void Postfix()
        {
            HardeningPatches.SyncIfNeeded();
            LogThrottle.DrainSummaries();
        }
    }
}
