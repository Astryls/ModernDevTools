using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// The full mod-settings page (Options -> Mod settings -> Modern Dev Tools). Its own distinct
    /// layout - a single scrolling column of suite-styled cards - but it shares the Palette visual
    /// language (and SourceTag/CommunityStatus) with the quick-access Dialog_Modules window.
    ///
    /// SETTINGS PARITY RULE: this page is the canonical home for every user-facing setting. Anything
    /// the Modules/filters window (Dialog_Modules) exposes must also live here. To add a setting:
    ///   1. add the field + Scribe line to ModernDevToolsSettings,
    ///   2. add a row to the matching card below (General / Community / Experimental hardening) or a
    ///      new DrawXCard method wired into DrawContent,
    ///   3. add a matching control to Dialog_Modules if it belongs on the quick-access surface,
    ///   4. add any new Keyed strings.
    /// Heights are measured (Text.CalcHeight / ToggleRowHeight) so descriptions never clip, and the
    /// scrollbar gutter is reserved unconditionally so content width - and thus measured height - is
    /// stable frame to frame (no reflow flicker).
    /// </summary>
    public static class SettingsPage
    {
        private static Vector2 _scroll;
        private static float _lastHeight;

        private const float CardPad = 12f;
        private const float CardGap = 12f;
        private const float HeaderH = 24f;
        private const float HeaderGap = 6f;      // header block = HeaderH + HeaderGap before content
        private const float ModuleRowH = 46f;
        private const float IgnoredRowH = 32f;

        private static ModernDevToolsSettings S => ModernDevToolsMod.Settings;
        private static void Save() => ModernDevToolsMod.Instance?.WriteSettings();

        public static void Draw(Rect inRect)
        {
            Widgets.DrawBoxSolid(inRect, Palette.BG);
            Palette.DrawBox(inRect, Palette.BGL, 1);

            Rect body = inRect.ContractedBy(14f);
            float contentW = body.width - 16f;   // reserve the scrollbar gutter unconditionally
            Rect view = new Rect(0f, 0f, contentW, Mathf.Max(_lastHeight, body.height));

            Palette.BeginScroll(body, ref _scroll, view);
            float y = 0f;
            DrawContent(contentW, ref y);
            Palette.EndScroll();

            _lastHeight = y;
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = true;
            GUI.color = Color.white;
        }

        private static void DrawContent(float width, ref float y)
        {
            DrawGeneralCard(width, ref y);
            DrawModulesCard(width, ref y);
            DrawIgnoredCard(width, ref y);
            DrawCommunityCard(width, ref y);
            DrawHardeningCard(width, ref y);
        }

        // --- card scaffold ---

        private static Rect BeginCard(float width, float y, float innerContentHeight, out Rect inner)
        {
            Rect card = new Rect(0f, y, width, HeaderH + HeaderGap + innerContentHeight + CardPad * 2f);
            Palette.DrawCard(card);
            inner = card.ContractedBy(CardPad);
            return card;
        }

        private static float DrawHeader(Rect inner, string label)
        {
            Palette.SectionHeader(new Rect(inner.x, inner.y, inner.width, HeaderH), label);
            return inner.y + HeaderH + HeaderGap;
        }

        // --- General (settings-only toggles) ---

        private static void DrawGeneralCard(float width, ref float y)
        {
            float iw = width - CardPad * 2f;
            string desc = "MDT_NoMainMenuLogDesc".Translate();
            float rowH = Palette.ToggleRowHeight(desc, iw);

            Rect card = BeginCard(width, y, rowH, out Rect inner);
            float iy = DrawHeader(inner, "MDT_SettingsGeneral".Translate());

            bool v = S.dontAutoOpenAtMainMenu;
            bool nv = Palette.ToggleRow(new Rect(inner.x, iy, inner.width, rowH), "MDT_NoMainMenuLog".Translate(), v, desc);
            if (nv != v) { S.dontAutoOpenAtMainMenu = nv; Save(); }

            y += card.height + CardGap;
        }

        // --- Analysis modules ---

        private static void DrawModulesCard(float width, ref float y)
        {
            float iw = width - CardPad * 2f;
            string intro = "MDT_SettingsIntro".Translate();
            Text.Font = GameFont.Small;
            float introH = Text.CalcHeight(intro, iw);

            var defs = DefDatabase<ErrorModuleDef>.AllDefsListForReading.OrderBy(d => d.order).ToList();
            float content = introH + 8f + defs.Count * ModuleRowH;

            Rect card = BeginCard(width, y, content, out Rect inner);
            float iy = DrawHeader(inner, "MDT_SectionModules".Translate());

            Text.Font = GameFont.Small;
            Text.WordWrap = true;
            GUI.color = Palette.TextDim;
            Widgets.Label(new Rect(inner.x, iy, inner.width, introH), intro);
            GUI.color = Color.white;
            iy += introH + 8f;

            foreach (ErrorModuleDef def in defs)
            {
                DrawModuleRow(new Rect(inner.x, iy, inner.width, ModuleRowH - 6f), def);
                iy += ModuleRowH;
            }

            y += card.height + CardGap;
        }

        private static void DrawModuleRow(Rect row, ErrorModuleDef def)
        {
            bool avail = def.Available;
            bool enabled = avail && ModernDevToolsMod.IsModuleEnabled(def);

            Widgets.DrawBoxSolid(row, Palette.PanelBG);
            if (enabled) Palette.StateStrip(row, Palette.Accent, 3f);
            Palette.DrawBox(row, Palette.BGL, 1);
            if (avail && Mouse.IsOver(row)) Widgets.DrawBoxSolid(row, new Color(1f, 1f, 1f, 0.04f));

            Color nameCol = !avail ? new Color(0.37f, 0.40f, 0.45f) : (enabled ? Palette.Stat : Palette.TextDim);
            Palette.LabelFit(new Rect(row.x + 12f, row.y + 4f, row.width - 70f, 20f), def.label.CapitalizeFirst(), nameCol);
            Palette.LabelFit(new Rect(row.x + 12f, row.y + 24f, row.width - 70f, 18f), Dialog_Modules.SourceTag(def, avail), Palette.TextDim);

            Rect toggleR = new Rect(row.xMax - 48f, row.center.y - 9f, 36f, 18f);
            Palette.DrawToggle(toggleR, enabled);
            if (!def.description.NullOrEmpty()) TooltipHandler.TipRegion(row, def.description);

            if (avail && Widgets.ButtonInvisible(toggleR))
            {
                S.moduleEnabled[def.defName] = !enabled;
                ErrorModuleRegistry.Invalidate();
                LogAnalysisCache.Clear();
                Save();
            }
        }

        // --- Ignored warnings ---

        private static void DrawIgnoredCard(float width, ref float y)
        {
            float iw = width - CardPad * 2f;
            var ignored = ModernDevToolsMod.IgnoredIssues.ToList();

            float content;
            float emptyH = 0f;
            if (ignored.Count == 0)
            {
                Text.Font = GameFont.Small;
                emptyH = Text.CalcHeight("MDT_NoIgnored".Translate(), iw);
                content = emptyH;
            }
            else content = ignored.Count * IgnoredRowH;

            Rect card = BeginCard(width, y, content, out Rect inner);

            // Header + Clear-all (right)
            Palette.SectionHeader(new Rect(inner.x, inner.y, inner.width, HeaderH), "MDT_SectionIgnored".Translate());
            if (ignored.Count > 0)
            {
                float clrW = 72f;
                if (Palette.GrayButton(new Rect(inner.xMax - clrW, inner.y - 2f, clrW, HeaderH), "MDT_ClearIgnores".Translate()))
                    foreach (string d in ignored) ModernDevToolsMod.UnignoreIssue(d);
            }
            float iy = inner.y + HeaderH + HeaderGap;

            if (ignored.Count == 0)
            {
                Text.Font = GameFont.Small;
                Text.WordWrap = true;
                GUI.color = Palette.TextDim;
                Widgets.Label(new Rect(inner.x, iy, inner.width, emptyH), "MDT_NoIgnored".Translate());
                GUI.color = Color.white;
            }
            else
            {
                foreach (string defName in ignored)
                {
                    Rect row = new Rect(inner.x, iy, inner.width, IgnoredRowH - 6f);
                    Widgets.DrawBoxSolid(row, Palette.PanelBG);
                    Palette.DrawBox(row, Palette.BGL, 1);
                    KnownIssueDef kd = DefDatabase<KnownIssueDef>.GetNamedSilentFail(defName);
                    string label = kd != null && !kd.label.NullOrEmpty() ? kd.LabelCap.ToString() : defName;
                    Palette.LabelFit(new Rect(row.x + 10f, row.y, row.width - 80f, row.height), label, Palette.Stat);
                    if (Palette.GrayButton(new Rect(row.xMax - 68f, row.y + 3f, 62f, row.height - 6f), "MDT_Remove".Translate()))
                        ModernDevToolsMod.UnignoreIssue(defName);
                    iy += IgnoredRowH;
                }
            }

            y += card.height + CardGap;
        }

        // --- Community data ---

        private static void DrawCommunityCard(float width, ref float y)
        {
            float iw = width - CardPad * 2f;
            string status = Dialog_Modules.CommunityStatus();
            Text.Font = GameFont.Small;
            float statusH = Text.CalcHeight(status, iw);

            const float enableRowH = 24f;
            const float btnH = 26f;
            float content = enableRowH + 8f + btnH + 8f + statusH;

            Rect card = BeginCard(width, y, content, out Rect inner);
            float iy = DrawHeader(inner, "MDT_SectionCommunity".Translate());

            bool en = S.enableCommunityData;
            bool nEn = Palette.ToggleRow(new Rect(inner.x, iy, inner.width, enableRowH), "MDT_CommEnable".Translate(), en);
            if (nEn != en)
            {
                S.enableCommunityData = nEn;
                Save();
                if (nEn) CommunityData.LoadCache();
            }
            iy += enableRowH + 8f;

            bool canUpdate = S.enableCommunityData && !CommunityData.Loading;
            if (Palette.GrayButton(new Rect(inner.x, iy, 150f, btnH), "MDT_CommUpdate".Translate(), "MDT_CommUpdateTip".Translate(), canUpdate))
                CommunityData.Update();
            iy += btnH + 8f;

            Text.Font = GameFont.Small;
            Text.WordWrap = true;
            GUI.color = Palette.TextDim;
            Widgets.Label(new Rect(inner.x, iy, inner.width, statusH), status);
            GUI.color = Color.white;

            y += card.height + CardGap;
        }

        // --- Experimental hardening ---

        private static void DrawHardeningCard(float width, ref float y)
        {
            float iw = width - CardPad * 2f;
            string d1 = "MDT_HardeningDesc".Translate();
            string d2 = "MDT_MapHardeningDesc".Translate();
            float r1 = Palette.ToggleRowHeight(d1, iw);
            float r2 = Palette.ToggleRowHeight(d2, iw);
            float content = r1 + 10f + r2;

            Rect card = BeginCard(width, y, content, out Rect inner);
            float iy = DrawHeader(inner, "MDT_SectionHardening".Translate());

            bool w1 = S.experimentalWindowHardening;
            bool n1 = Palette.ToggleRow(new Rect(inner.x, iy, inner.width, r1), "MDT_HardeningShort".Translate(), w1, d1);
            if (n1 != w1) { S.experimentalWindowHardening = n1; Save(); }
            iy += r1 + 10f;

            bool w2 = S.experimentalMapUiHardening;
            bool n2 = Palette.ToggleRow(new Rect(inner.x, iy, inner.width, r2), "MDT_MapHardeningShort".Translate(), w2, d2);
            if (n2 != w2) { S.experimentalMapUiHardening = n2; Save(); }

            y += card.height + CardGap;
        }
    }
}
