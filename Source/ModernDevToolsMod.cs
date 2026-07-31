using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// Every user-facing setting. SETTINGS PARITY RULE: each toggle here is surfaced by the full
    /// mod-settings page (<see cref="SettingsPage"/>), and anything the quick-access Dialog_Modules
    /// window also exposes must have a matching control on that page. When you add a setting:
    ///   1. add the field + Scribe line below,
    ///   2. add a row to SettingsPage (a card row or a new DrawXCard),
    ///   3. mirror it in Dialog_Modules if it belongs on the quick-access surface,
    ///   4. add the Keyed strings (label + description).
    /// </summary>
    public class ModernDevToolsSettings : ModSettings
    {
        public Dictionary<string, bool> moduleEnabled = new Dictionary<string, bool>();
        public HashSet<string> ignoredIssues = new HashSet<string>();
        public string lastSeenVersion = "";   // for the update-notes popup
        public bool enableCommunityData = false;   // opt-in: fetch community databases from the internet
        public bool dontAutoOpenAtMainMenu = false;   // opt-in: suppress the log's auto-open at the main menu / loading
        public bool experimentalWindowHardening = false;   // experimental: isolate/close UI-breaking windows
        public bool experimentalMapUiHardening = false;    // experimental: recover from broken world/map UI

        // --- log-window ownership (see LogWindowCompat) ---
        // Stand down and let the vanilla log window (and everything decorating it - HugsLib's widget
        // API, Archotech Logs) behave as if this mod were not installed.
        public bool yieldLogWindow = false;
        public bool dismissedLogCompatHint = false;

        // Suppress runaway repeating log lines (see LogThrottle). Default OFF: it sits on the error path
        // of every mod in the game, so it earns its default-on over a release, not on day one.
        public bool throttleRepeatingLogs = false;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref moduleEnabled, "moduleEnabled", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref ignoredIssues, "ignoredIssues", LookMode.Value);
            Scribe_Values.Look(ref lastSeenVersion, "lastSeenVersion", "");
            Scribe_Values.Look(ref enableCommunityData, "enableCommunityData", false);
            Scribe_Values.Look(ref dontAutoOpenAtMainMenu, "dontAutoOpenAtMainMenu", false);
            Scribe_Values.Look(ref experimentalWindowHardening, "experimentalWindowHardening", false);
            Scribe_Values.Look(ref experimentalMapUiHardening, "experimentalMapUiHardening", false);
            Scribe_Values.Look(ref yieldLogWindow, "yieldLogWindow", false);
            Scribe_Values.Look(ref dismissedLogCompatHint, "dismissedLogCompatHint", false);
            Scribe_Values.Look(ref throttleRepeatingLogs, "throttleRepeatingLogs", false);
            if (moduleEnabled == null) moduleEnabled = new Dictionary<string, bool>();
            if (ignoredIssues == null) ignoredIssues = new HashSet<string>();
        }
    }

    /// <summary>
    /// Mod entry point + settings. The settings panel lists every analysis module (built-in and
    /// third-party) so the player can turn any of them on or off; the enable state feeds the registry.
    /// </summary>
    public class ModernDevToolsMod : Mod
    {
        public const string Version = "1.2";   // bump on each release to trigger the update-notes popup

        public static ModernDevToolsMod Instance;
        public static ModernDevToolsSettings Settings;
        public static int IgnoreVersion;   // bumped when the ignore set changes (gates the list rebuild)

        public ModernDevToolsMod(ModContentPack content) : base(content)
        {
            Instance = this;
            Settings = GetSettings<ModernDevToolsSettings>();
        }

        public static bool IsModuleEnabled(ErrorModuleDef def)
        {
            if (Settings != null && Settings.moduleEnabled.TryGetValue(def.defName, out bool v)) return v;
            return def.enabledByDefault;
        }

        public static bool IsIgnored(string issueDefName) =>
            Settings != null && !issueDefName.NullOrEmpty() && Settings.ignoredIssues.Contains(issueDefName);

        public static void IgnoreIssue(string issueDefName)
        {
            if (Settings == null || issueDefName.NullOrEmpty()) return;
            if (Settings.ignoredIssues.Add(issueDefName))
            {
                IgnoreVersion++;
                LogState.ClearSelection();
                Instance?.WriteSettings();
            }
        }

        public static void UnignoreIssue(string issueDefName)
        {
            if (Settings == null || issueDefName.NullOrEmpty()) return;
            if (Settings.ignoredIssues.Remove(issueDefName))
            {
                IgnoreVersion++;
                Instance?.WriteSettings();
            }
        }

        public static IEnumerable<string> IgnoredIssues => Settings?.ignoredIssues ?? Enumerable.Empty<string>();

        public override string SettingsCategory() => "Modern Dev Tools";

        public override void DoSettingsWindowContents(Rect inRect) => SettingsPage.Draw(inRect);
    }
}
