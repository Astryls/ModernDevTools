using System;
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

        // "Mods changed since this save" detection. Costs one background pass over the mod folders
        // per session (metadata only, no file contents are read), so it gets an off switch.
        public bool detectModChanges = true;

        // Wall-clock time per log line, read from the engine's own LogMessage.timestamp. On by default:
        // knowing WHEN a line arrived is what separates "this happened when I clicked that" from
        // "this has been spamming since startup". Off for anyone who wants the narrow list back.
        public bool showTimestamps = true;

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
            Scribe_Values.Look(ref detectModChanges, "detectModChanges", true);
            Scribe_Values.Look(ref showTimestamps, "showTimestamps", true);
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
        public const string Version = "1.3";   // bump on each release to trigger the update-notes popup

        /// <summary>Prefix on every log line this mod emits itself. Module_StackTrace uses it to tell
        /// "we REPORTED this error" from "we CAUSED it" - see IsSelfReport there. Keep every internal
        /// Log.* call starting with this exact string.</summary>
        public const string LogPrefix = "[Modern Dev Tools]";

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

        /// <summary>
        /// Human-readable label for an ignore key, for the Ignored list in both settings surfaces.
        ///
        /// The key is whatever produced the diagnosis, and that is NOT always a KnownIssueDef: library
        /// entries use a KnownIssueDef defName, but a diagnosis raised by a module carries that module's
        /// identifier (MDT_Module_*), and a community entry carries "community:&lt;id&gt;". Both call sites
        /// used to fall straight back to the raw string, so ignoring a version-mismatch or community
        /// diagnosis put a bare "MDT_Module_VersionMismatch" in the player's settings - which reads as a
        /// broken translation. Shared by SettingsPage and Dialog_Modules to hold the parity contract.
        /// </summary>
        public static string IgnoredLabel(string key)
        {
            if (key.NullOrEmpty()) return "";
            KnownIssueDef kd = DefDatabase<KnownIssueDef>.GetNamedSilentFail(key);
            if (kd != null && !kd.label.NullOrEmpty()) return kd.LabelCap.ToString();

            ErrorModuleDef md = DefDatabase<ErrorModuleDef>.GetNamedSilentFail(key);
            // Module_CircinusCorpus reports "MDT_Module_CircinusCorpus" while its def is "MDT_CircinusCorpus".
            if (md == null && key.StartsWith("MDT_Module_", StringComparison.Ordinal))
                md = DefDatabase<ErrorModuleDef>.GetNamedSilentFail("MDT_" + key.Substring("MDT_Module_".Length));
            if (md != null && !md.label.NullOrEmpty()) return md.LabelCap.ToString();

            if (key.StartsWith("community:", StringComparison.Ordinal))
                return "MDT_IgnoredCommunityEntry".Translate(key.Substring("community:".Length)).ToString();

            return key;
        }

        public override string SettingsCategory() => "Modern Dev Tools";

        public override void DoSettingsWindowContents(Rect inRect) => SettingsPage.Draw(inRect);
    }
}
