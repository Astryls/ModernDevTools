using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace ModernDevTools
{
    public class ModernDevToolsSettings : ModSettings
    {
        public Dictionary<string, bool> moduleEnabled = new Dictionary<string, bool>();
        public HashSet<string> ignoredIssues = new HashSet<string>();
        public string lastSeenVersion = "";   // for the update-notes popup
        public bool enableCommunityData = false;   // opt-in: fetch community databases from the internet
        public bool dontAutoOpenAtMainMenu = false;   // opt-in: suppress the log's auto-open at the main menu / loading
        public bool experimentalWindowHardening = false;   // experimental: isolate/close UI-breaking windows
        public bool experimentalMapUiHardening = false;    // experimental: recover from broken world/map UI

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
        public const string Version = "1.1";   // bump on each release to trigger the update-notes popup

        public static ModernDevToolsMod Instance;
        public static ModernDevToolsSettings Settings;
        public static int IgnoreVersion;   // bumped when the ignore set changes (gates the list rebuild)
        private Vector2 _scroll;

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

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var defs = DefDatabase<ErrorModuleDef>.AllDefsListForReading.OrderBy(d => d.order).ToList();

            var top = new Listing_Standard();
            top.Begin(new Rect(inRect.x, inRect.y, inRect.width, 158f));
            Text.Font = GameFont.Small;
            top.Label("MDT_SettingsIntro".Translate());
            bool noMenuLog = Settings.dontAutoOpenAtMainMenu;
            top.CheckboxLabeled("MDT_NoMainMenuLog".Translate(), ref noMenuLog, "MDT_NoMainMenuLogDesc".Translate());
            Settings.dontAutoOpenAtMainMenu = noMenuLog;
            bool harden = Settings.experimentalWindowHardening;
            top.CheckboxLabeled("MDT_Hardening".Translate(), ref harden, "MDT_HardeningDesc".Translate());
            Settings.experimentalWindowHardening = harden;
            bool mapHarden = Settings.experimentalMapUiHardening;
            top.CheckboxLabeled("MDT_MapHardening".Translate(), ref mapHarden, "MDT_MapHardeningDesc".Translate());
            Settings.experimentalMapUiHardening = mapHarden;
            top.End();

            Rect listOut = new Rect(inRect.x, inRect.y + 162f, inRect.width, inRect.height - 162f);
            float rowH = 56f;
            Rect view = new Rect(0f, 0f, listOut.width - 16f, defs.Count * rowH + 4f);
            Palette.BeginScroll(listOut, ref _scroll, view);
            float y = 0f;
            foreach (ErrorModuleDef def in defs)
            {
                Rect row = new Rect(0f, y, view.width, rowH - 4f);
                bool avail = def.Available;
                bool cur = IsModuleEnabled(def);

                Rect checkR = new Rect(row.x, row.y, row.width, 24f);
                Text.Font = GameFont.Small;
                if (avail)
                {
                    bool now = cur;
                    Widgets.CheckboxLabeled(checkR, def.label.CapitalizeFirst(), ref now);
                    if (now != cur)
                    {
                        Settings.moduleEnabled[def.defName] = now;
                        ErrorModuleRegistry.Invalidate();
                        LogAnalysisCache.Clear();
                    }
                }
                else
                {
                    GUI.color = new Color(0.62f, 0.65f, 0.70f);
                    Widgets.Label(checkR, def.label.CapitalizeFirst() + "  (" + "MDT_SettingsUnavailable".Translate() + ")");
                    GUI.color = Color.white;
                }

                if (!def.description.NullOrEmpty())
                {
                    Rect descR = new Rect(row.x + 24f, row.y + 24f, row.width - 24f, rowH - 28f);
                    GUI.color = new Color(0.62f, 0.65f, 0.70f);
                    Widgets.Label(descR, def.description);
                    GUI.color = Color.white;
                }
                y += rowH;
            }
            Palette.EndScroll();
            Text.Anchor = TextAnchor.UpperLeft;
        }
    }
}
