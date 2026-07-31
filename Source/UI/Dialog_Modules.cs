using System;
using System.Linq;
using UnityEngine;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// Opened from the log window's Modules button. Left: every analysis module with an on/off toggle
    /// (built-in and third-party, unavailable ones dimmed). Right: the warnings the player has muted,
    /// with a Remove per entry, plus a short "extend this mod" surface for authors.
    ///
    /// SETTINGS PARITY RULE: this quick-access window and the full mod-settings page (SettingsPage)
    /// are two distinct layouts that share the Palette styling. Every control exposed here must also
    /// be reachable from the mod-settings page. When you add a control here, add a matching one to
    /// SettingsPage (see the checklist on ModernDevToolsSettings). SourceTag/CommunityStatus are shared
    /// with SettingsPage so the two surfaces can never drift on those.
    /// </summary>
    public class Dialog_Modules : Window
    {
        private Vector2 _moduleScroll;
        private Vector2 _ignoreScroll;

        public override Vector2 InitialSize => new Vector2(860f, 648f);
        protected override float Margin => 0f;

        public Dialog_Modules()
        {
            doWindowBackground = false;
            doCloseX = false;
            draggable = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = true;
            closeOnAccept = false;
        }

        public override void DoWindowContents(Rect inRect)
        {
            try { DrawAll(inRect); }
            catch (Exception e) { Log.ErrorOnce("[Modern Dev Tools] modules window draw failed: " + e, 0x2E19A40); }
            finally { Palette.ResetGuiState(); }
        }

        private void DrawAll(Rect inRect)
        {
            Widgets.DrawBoxSolid(inRect, Palette.BG);
            Palette.DrawBox(inRect, Palette.BGL, 1);

            // Title bar
            Text.Font = GameFont.Medium;
            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color = Palette.Stat;
            Widgets.Label(new Rect(20f, 12f, inRect.width - 80f, 32f), "MDT_ModulesTitle".Translate());
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
            if (Palette.CloseX(new Rect(inRect.xMax - 40f, 14f, 26f, 26f))) Close();

            float top = 54f;
            const float hardH = 72f;
            float bottom = inRect.yMax - 14f - hardH - 8f;
            float half = (inRect.width - 16f - 16f - 12f) / 2f;

            DrawModules(new Rect(16f, top, half, bottom - top));
            DrawRight(new Rect(16f + half + 12f, top, half, bottom - top));
            DrawHardening(new Rect(16f, bottom + 8f, inRect.width - 32f, hardH));
        }

        private void DrawHardening(Rect area)
        {
            Palette.DrawCard(area);
            Rect ci = area.ContractedBy(10f);
            float y = ci.y;
            Palette.SectionHeader(new Rect(ci.x, y, ci.width, 22f), "MDT_SectionHardening".Translate());
            y += 28f;
            var s = ModernDevToolsMod.Settings;
            float halfW = (ci.width - 16f) / 2f;
            DrawToggleRow(new Rect(ci.x, y, halfW, 22f), "MDT_HardeningShort".Translate(), s.experimentalWindowHardening, "MDT_HardeningDesc".Translate(), v => s.experimentalWindowHardening = v);
            DrawToggleRow(new Rect(ci.x + halfW + 16f, y, halfW, 22f), "MDT_MapHardeningShort".Translate(), s.experimentalMapUiHardening, "MDT_MapHardeningDesc".Translate(), v => s.experimentalMapUiHardening = v);
        }

        private static void DrawToggleRow(Rect r, string label, bool val, string tip, Action<bool> setter)
        {
            Palette.LabelFit(new Rect(r.x, r.y, r.width - 44f, r.height), label, Palette.Stat);
            Palette.DrawToggle(new Rect(r.xMax - 40f, r.y + 2f, 36f, 18f), val);
            if (!tip.NullOrEmpty()) TooltipHandler.TipRegion(r, tip);
            if (Widgets.ButtonInvisible(r)) { setter(!val); ModernDevToolsMod.Instance?.WriteSettings(); }
        }

        private void DrawModules(Rect area)
        {
            Palette.DrawCard(area);
            Rect inner = area.ContractedBy(12f);
            float y = inner.y;
            Palette.SectionHeader(new Rect(inner.x, y, inner.width, 24f), "MDT_SectionModules".Translate());
            y += 30f;

            var defs = DefDatabase<ErrorModuleDef>.AllDefsListForReading.OrderBy(d => d.order).ToList();
            const float rowH = 46f;
            Rect listOut = new Rect(inner.x, y, inner.width, inner.yMax - y);
            Rect view = new Rect(0f, 0f, listOut.width - 16f, Mathf.Max(defs.Count * rowH, listOut.height));
            Palette.BeginScroll(listOut, ref _moduleScroll, view);
            try
            {
            float ry = 0f;
            foreach (ErrorModuleDef def in defs)
            {
                Rect row = new Rect(0f, ry, view.width, rowH - 4f);
                bool avail = def.Available;
                bool enabled = avail && ModernDevToolsMod.IsModuleEnabled(def);
                // Shared row renderer - this and the settings page had identical copies.
                if (Palette.ModuleRow(row, def.label.CapitalizeFirst(), SourceTag(def, avail),
                                      def.description, avail, enabled))
                    SettingsPage.ToggleModule(def, enabled);
                ry += rowH;
            }
            }
            finally { Palette.EndScroll(); }
        }

        private void DrawRight(Rect area)
        {
            // Ignored warnings (top ~60%)
            float splitY = area.y + (area.height - 12f) * 0.62f;
            Rect ignoreR = new Rect(area.x, area.y, area.width, splitY - area.y);
            Palette.DrawCard(ignoreR);
            Rect inner = ignoreR.ContractedBy(12f);
            float y = inner.y;

            var ignored = ModernDevToolsMod.IgnoredIssues.ToList();
            Palette.SectionHeader(new Rect(inner.x, y, inner.width, 24f), "MDT_SectionIgnored".Translate());
            if (ignored.Count > 0)
            {
                float clrW = 72f;
                if (Palette.GrayButton(new Rect(inner.xMax - clrW, y - 2f, clrW, 24f), "MDT_ClearIgnores".Translate()))
                {
                    foreach (string d in ignored) ModernDevToolsMod.UnignoreIssue(d);
                }
            }
            y += 30f;

            if (ignored.Count == 0)
            {
                GUI.color = Palette.TextDim;
                Text.WordWrap = true;
                Widgets.Label(new Rect(inner.x, y, inner.width, inner.yMax - y), "MDT_NoIgnored".Translate());
                GUI.color = Color.white;
            }
            else
            {
                const float rowH = 34f;
                Rect listOut = new Rect(inner.x, y, inner.width, inner.yMax - y);
                Rect view = new Rect(0f, 0f, listOut.width - 16f, Mathf.Max(ignored.Count * rowH, listOut.height));
                Palette.BeginScroll(listOut, ref _ignoreScroll, view);
                try
                {
                float ry = 0f;
                foreach (string defName in ignored)
                {
                    Rect row = new Rect(0f, ry, view.width, rowH - 4f);
                    Widgets.DrawBoxSolid(row, Palette.PanelBG);
                    Palette.DrawBox(row, Palette.BGL, 1);
                    KnownIssueDef kd = DefDatabase<KnownIssueDef>.GetNamedSilentFail(defName);
                    string label = kd != null && !kd.label.NullOrEmpty() ? kd.LabelCap.ToString() : defName;
                    Palette.LabelFit(new Rect(row.x + 10f, row.y, row.width - 80f, row.height), label, Palette.Stat);
                    if (Palette.GrayButton(new Rect(row.xMax - 68f, row.y + 3f, 62f, row.height - 6f), "MDT_Remove".Translate()))
                        ModernDevToolsMod.UnignoreIssue(defName);
                    ry += rowH;
                }
                }
                finally { Palette.EndScroll(); }
            }

            // Community data (bottom)
            Rect commR = new Rect(area.x, splitY + 12f, area.width, area.yMax - splitY - 12f);
            Palette.DrawCard(commR);
            Rect ci = commR.ContractedBy(12f);
            float cy = ci.y;
            Palette.SectionHeader(new Rect(ci.x, cy, ci.width, 24f), "MDT_SectionCommunity".Translate());
            cy += 30f;

            var s = ModernDevToolsMod.Settings;
            bool en = s.enableCommunityData;
            Rect enRow = new Rect(ci.x, cy, ci.width, 22f);
            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color = Palette.Stat;
            Widgets.Label(new Rect(enRow.x, enRow.y, enRow.width - 44f, enRow.height), "MDT_CommEnable".Translate());
            GUI.color = Color.white; Text.Anchor = TextAnchor.UpperLeft;
            Palette.DrawToggle(new Rect(enRow.xMax - 40f, enRow.y + 2f, 36f, 18f), en);
            if (Widgets.ButtonInvisible(new Rect(enRow.xMax - 44f, enRow.y, 44f, enRow.height)))
            {
                s.enableCommunityData = !en;
                ModernDevToolsMod.Instance?.WriteSettings();
                if (!en) CommunityData.LoadCache();
            }
            cy += 28f;

            bool canUpdate = s.enableCommunityData && !CommunityData.Loading;
            if (Palette.GrayButton(new Rect(ci.x, cy, 150f, 26f), "MDT_CommUpdate".Translate(), "MDT_CommUpdateTip".Translate(), canUpdate))
                CommunityData.Update();
            cy += 32f;

            GUI.color = Palette.TextDim;
            Text.WordWrap = true;
            Widgets.Label(new Rect(ci.x, cy, ci.width, ci.yMax - cy), CommunityStatus());
            GUI.color = Color.white;
        }

        internal static string CommunityStatus()
        {
            var s = ModernDevToolsMod.Settings;
            if (s == null || !s.enableCommunityData) return "MDT_CommDisabled".Translate();
            if (CommunityData.Loading) return "MDT_CommUpdating".Translate();
            if (!CommunityData.LastError.NullOrEmpty()) return "MDT_CommError".Translate(CommunityData.LastError);
            if (CommunityData.LastUpdated.HasValue) return "MDT_CommUpdated".Translate(CommunityData.LastUpdated.Value.ToString("yyyy-MM-dd HH:mm"));
            if (CommunityData.HasData) return "MDT_CommCached".Translate();
            return "MDT_CommNoData".Translate();
        }

        internal static string SourceTag(ErrorModuleDef def, bool available)
        {
            if (!available)
            {
                if (!def.requiresDlc.NullOrEmpty()) return "MDT_TagRequires".Translate(def.requiresDlc.CapitalizeFirst());
                if (!def.requiresPackageId.NullOrEmpty()) return "MDT_TagRequires".Translate(def.requiresPackageId);
                return "MDT_TagUnavailable".Translate();
            }
            var pack = def.modContentPack;
            if (pack == null || pack.PackageId == "astryl.moderndevtools") return "MDT_TagBuiltIn".Translate();
            return pack.Name;
        }
    }
}
