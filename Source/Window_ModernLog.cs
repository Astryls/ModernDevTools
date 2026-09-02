using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// The modern replacement for RimWorld's debug log.
    ///
    /// LAYOUT: three columns ("sidebar" study C). LEFT is a permanent navigation column - the type
    /// filters with their counts, the three behaviour switches, and the tool buttons - each in an
    /// iOS grouped-list plate. MIDDLE is search plus the message list, with the add-on tray pinned
    /// under it. RIGHT is the inspector: attribution, curated guidance and the stack trace.
    ///
    /// Why a sidebar: the previous layout put eleven controls across two toolbar rows above the
    /// list, where every one of them had to be short enough to fit a chip and none of them could
    /// afford a label. Giving them a column buys them room to be readable, hands the entire middle
    /// column back to the messages, and means the filter counts are always on screen instead of
    /// competing with the toolbar for width. Full vanilla parity is unchanged: filtering, clear,
    /// copy, auto-open, pause-on-error.
    /// </summary>
    public class Window_ModernLog : Window
    {
        private const float Pad = 12f;
        private const float Gap = 10f;
        private const float RowH = 28f;   // B1: a touch more air per row (suite rhythm)
        private const float BarH = 30f;   // capsule pills need the height to read as capsules

        // Sidebar geometry.
        private const float SideW = 212f;
        private const float SidePad = 12f;
        private const float GroupRowH = 32f;
        private const float HeaderH = 22f;
        private const float SwitchW = 38f;
        private const float SwitchH = 22f;

        // Below this much room for the middle column the inspector stands down and the list takes
        // the space; the sidebar is never dropped, because it is the only route to the filters.
        private const float InspectorNeeds = 600f;

        private readonly List<LogMessage> _filtered = new List<LogMessage>();
        private int _cErr, _cWarn, _cMsg;

        // Filtered-view cache gate (rebuild only on real change).
        private int _lastRev = -1;
        private int _lastIgnoreVer = -1;
        private int _hidden;
        private string _lastSearch;
        private bool _lastSE = true, _lastSW = true, _lastSM = true;

        private float _inspH = 200f; // cached inspector content height for the scroll viewRect

        // Visibility latched once per FRAME. OnGUI runs several passes per frame, and a control that
        // appears or disappears between passes shifts every later IMGUI control id - which kills
        // clicks in the list below it. Latching makes the control set identical across all passes of
        // a frame; the change lands on the next frame instead.
        private int _hintFrame = -1;
        private bool _hintVisible;
        private int _modsFrame = -1;
        private bool _modsVisible;

        public Window_ModernLog()
        {
            doCloseX = false;
            doWindowBackground = false;
            draggable = true;
            resizeable = true;
            preventCameraMotion = false;
            drawShadow = true;
            closeOnAccept = false;
            closeOnCancel = Prefs.CloseLogWindowOnEscape; // vanilla parity
            drawInScreenshotMode = true;
            layer = WindowLayer.Dialog;
        }

        protected override float Margin => 0f;

        public override Vector2 InitialSize =>
            new Vector2(Mathf.Min(UI.screenWidth * 0.72f, 1280f), Mathf.Min(UI.screenHeight * 0.66f, 800f));

        // --- static open/close plumbing (used by the Harmony redirects) ---

        public static bool IsOpenNow => Find.WindowStack != null && Find.WindowStack.IsOpen<Window_ModernLog>();

        public static void OpenIfNeeded()
        {
            var ws = Find.WindowStack;
            if (ws == null) return;
            if (!ws.IsOpen<Window_ModernLog>()) ws.Add(new Window_ModernLog());
        }

        public static void Toggle()
        {
            var ws = Find.WindowStack;
            if (ws == null) return;
            var existing = ws.WindowOfType<Window_ModernLog>();
            if (existing != null) ws.TryRemove(existing);
            else ws.Add(new Window_ModernLog());
        }

        // --- draw ---

        public override void DoWindowContents(Rect inRect)
        {
            try
            {
                DrawAll(inRect);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[Advanced Dev Tools] window draw failed: " + e, 0x2E19A30);
            }
            finally { Palette.ResetGuiState(); }
        }

        private void DrawAll(Rect inRect)
        {
            // Modern: near-black page under raised panel2 surfaces (the suite's contrast
            // hierarchy). Vanilla: the engine's own window fill + border. One call, both skins.
            Palette.WindowBG(inRect);

            Rect content = inRect.ContractedBy(Pad);
            RebuildFiltered();

            // Latch the two conditional surfaces for the whole frame.
            int frame = Time.frameCount;
            if (frame != _hintFrame) { _hintFrame = frame; _hintVisible = LogWindowCompat.ShowHint; }
            if (frame != _modsFrame) { _modsFrame = frame; _modsVisible = ModChange.HasChanges; }

            float midAvail = content.width - SideW - Gap;
            float inspW = midAvail >= InspectorNeeds ? Mathf.Clamp(midAvail * 0.42f, 330f, 430f) : 0f;

            Rect sidebar = new Rect(content.x, content.y, SideW, content.height);
            Rect middle = new Rect(sidebar.xMax + Gap, content.y,
                                   content.width - SideW - Gap - (inspW > 0f ? inspW + Gap : 0f),
                                   content.height);
            Rect inspector = new Rect(middle.xMax + Gap, content.y, inspW, content.height);

            try { DrawSidebar(sidebar); }
            catch (Exception e) { Log.ErrorOnce("[Advanced Dev Tools] sidebar draw failed: " + e, 0x2E19A34); }

            try { DrawMiddle(middle); }
            catch (Exception e) { Log.ErrorOnce("[Advanced Dev Tools] list column draw failed: " + e, 0x2E19A31); }

            if (inspW > 0f)
            {
                try { DrawInspector(inspector); }
                catch (Exception e) { Log.ErrorOnce("[Advanced Dev Tools] inspector draw failed: " + e, 0x2E19A32); }
            }

            if (Palette.CloseX(new Rect(inRect.xMax - 30f, inRect.y + 8f, 22f, 22f))) Close();
        }

        // ── sidebar ─────────────────────────────────────────────────────────────

        private void DrawSidebar(Rect r)
        {
            Spatial.Surface(r, Palette.SidebarBG);
            Spatial.TopHighlight(r);

            float x = r.x + SidePad;
            float w = r.width - SidePad * 2f;
            float y = r.y + 14f;

            // Large title + shown count.
            Text.Font = GameFont.Medium;
            float titleH = Text.LineHeight;
            UILabel(new Rect(x, y, w - 22f, titleH), "MDT_Title".Translate(), Palette.Bright, TextAnchor.MiddleLeft);
            Text.Font = GameFont.Small;
            y += titleH + 1f;

            string shown = "MDT_Shown".Translate(_filtered.Count).ToString();
            if (_hidden > 0) shown += " " + "MDT_HiddenSuffix".Translate(_hidden);
            UILabel(new Rect(x, y, w, 18f), shown, Palette.TextFaint, TextAnchor.MiddleLeft);
            y += 18f + 12f;

            // ── filters: one grouped plate, three rows, hairline separators.
            Rect fg = new Rect(x, y, w, GroupRowH * 3f);
            Spatial.Surface(fg, Palette.GroupBG);
            FilterRow(new Rect(fg.x, fg.y, fg.width, GroupRowH), "MDT_Errors".Translate(),
                      _cErr, Palette.Bad, LogMessageType.Error, false);
            FilterRow(new Rect(fg.x, fg.y + GroupRowH, fg.width, GroupRowH), "MDT_Warnings".Translate(),
                      _cWarn, Palette.Warn, LogMessageType.Warning, true);
            FilterRow(new Rect(fg.x, fg.y + GroupRowH * 2f, fg.width, GroupRowH), "MDT_Messages".Translate(),
                      _cMsg, Palette.StripGray, LogMessageType.Message, true);
            y = fg.yMax + 14f;

            // ── behaviour switches.
            y = SidebarHeader(x, y, w, "MDT_SidebarBehaviour".Translate());
            Rect bg = new Rect(x, y, w, GroupRowH * 3f);
            Spatial.Surface(bg, Palette.GroupBG);
            SwitchRow(new Rect(bg.x, bg.y, bg.width, GroupRowH), "MDT_AutoOpen".Translate(),
                      LogState.AutoOpen, "MDT_AutoOpenTip".Translate(), v => LogState.AutoOpen = v, false);
            SwitchRow(new Rect(bg.x, bg.y + GroupRowH, bg.width, GroupRowH), "MDT_PauseOnError".Translate(),
                      LogState.PauseOnError, "MDT_PauseOnErrorTip".Translate(), v => LogState.PauseOnError = v, true);
            SwitchRow(new Rect(bg.x, bg.y + GroupRowH * 2f, bg.width, GroupRowH), "MDT_OpenOnWarnings".Translate(),
                      LogState.OpenOnWarnings, "MDT_OpenOnWarningsTip".Translate(), v => LogState.OpenOnWarnings = v, true);
            y = bg.yMax + 14f;

            // ── tools. The vanilla-log escape hatch only exists when another mod actually decorates
            //    that window; AnyDecorator is scanned once at startup, so the row count is frame-stable.
            bool vanilla = LogWindowCompat.AnyDecorator;
            int toolRows = vanilla ? 4 : 3;
            y = SidebarHeader(x, y, w, "MDT_SidebarTools".Translate());
            Rect tg = new Rect(x, y, w, GroupRowH * toolRows);
            Spatial.Surface(tg, Palette.GroupBG);
            float ty = tg.y;
            if (ToolRow(new Rect(tg.x, ty, tg.width, GroupRowH), "MDT_KbButton".Translate(),
                        "MDT_KbButtonTip".Translate(), false))
            {
                if (!Find.WindowStack.IsOpen<Window_KnowledgeBase>()) Find.WindowStack.Add(new Window_KnowledgeBase());
            }
            ty += GroupRowH;
            if (ToolRow(new Rect(tg.x, ty, tg.width, GroupRowH), "MDT_Modules".Translate(),
                        "MDT_ModulesTip".Translate(), true))
            {
                if (!Find.WindowStack.IsOpen<Dialog_Modules>()) Find.WindowStack.Add(new Dialog_Modules());
            }
            ty += GroupRowH;
            if (ToolRow(new Rect(tg.x, ty, tg.width, GroupRowH), "MDT_CommUpdate".Translate(),
                        "MDT_CommUpdateTip".Translate(), true))
            {
                if (!ModernDevToolsMod.Settings.enableCommunityData)
                {
                    ModernDevToolsMod.Settings.enableCommunityData = true;
                    ModernDevToolsMod.Instance?.WriteSettings();
                    Messages.Message("MDT_CommEnabledMsg".Translate(), MessageTypeDefOf.TaskCompletion, false);
                }
                CommunityData.Update();
            }
            ty += GroupRowH;
            if (vanilla && ToolRow(new Rect(tg.x, ty, tg.width, GroupRowH), "MDT_VanillaLog".Translate(),
                                   "MDT_VanillaLogTip".Translate(LogWindowCompat.DecoratorNames()), true))
                LogWindowCompat.ToggleVanillaLog();

            // ── mods-changed, pinned to the bottom of the column.
            if (_modsVisible)
            {
                // Amber capsule pill with centred content - the suite's soft-banner shape.
                Rect mc = new Rect(x, r.yMax - 14f - GroupRowH, w, GroupRowH);
                bool over = Mouse.IsOver(mc);
                Spatial.Pill(mc, new Color(Palette.Warn.r, Palette.Warn.g, Palette.Warn.b, over ? 0.22f : 0.14f));
                Text.Font = GameFont.Small;
                string mcLabel = "MDT_ModsChangedBtn".Translate(ModChange.Report.Count);
                float mlw = Mathf.Min(TextMetrics.Size(mcLabel).x, mc.width - 44f);
                float bx = mc.center.x - (mlw + 16f) / 2f;
                Spatial.Dot(new Rect(bx, mc.center.y - 4f, 8f, 8f), Palette.Warn);
                Palette.LabelFit(new Rect(bx + 16f, mc.y, mlw + 6f, mc.height), mcLabel, Palette.Warn);
                TooltipHandler.TipRegion(mc, ModChangeTooltip());
                if (Widgets.ButtonInvisible(mc) && !Find.WindowStack.IsOpen<Dialog_ModChanges>())
                    Find.WindowStack.Add(new Dialog_ModChanges());
            }
        }

        private static float SidebarHeader(float x, float y, float w, string label)
        {
            UILabel(new Rect(x + 4f, y, w - 8f, HeaderH), Micro(label), Palette.TextFaint, TextAnchor.MiddleLeft);
            return y + HeaderH;
        }

        // Micro-headers live in the style layer now: uppercase is the Modern suite's signature and
        // the vanilla skin keeps sentence case, so the skin branch belongs to Palette, not here.
        private static string Micro(string label) => Palette.Micro(label);

        /// <summary>One type filter: colour dot, label, count. The whole row toggles it; an off filter
        /// dims rather than disappearing, so the count stays readable.</summary>
        private void FilterRow(Rect r, string label, int count, Color dot, LogMessageType type, bool sep)
        {
            bool show = LogState.VisibleType(type);
            if (sep) Spatial.Separator(r.x + 12f, r.y, r.width - 12f);
            if (Mouse.IsOver(r)) Spatial.RowPlate(r.ContractedBy(2f), new Color(1f, 1f, 1f, 0.05f));

            Spatial.Dot(new Rect(r.x + 12f, r.center.y - 4.5f, 9f, 9f),
                        show ? dot : Color.Lerp(dot, Palette.GroupBG, 0.62f));

            string cnt = count.ToString();
            float cw = TextMetrics.Size(cnt).x + 10f;
            UILabel(new Rect(r.x + 30f, r.y, r.width - 30f - cw - 10f, r.height), label,
                    show ? Palette.Stat : Palette.TextFaint, TextAnchor.MiddleLeft);
            UILabel(new Rect(r.xMax - cw - 10f, r.y, cw, r.height), cnt,
                    show ? Palette.TextDim : Palette.TextFaint, TextAnchor.MiddleRight);

            if (Widgets.ButtonInvisible(r)) SetVisibleType(type, !show);
        }

        private void SwitchRow(Rect r, string label, bool value, string tip, Action<bool> setter, bool sep)
        {
            if (sep) Spatial.Separator(r.x + 12f, r.y, r.width - 12f);
            if (Mouse.IsOver(r)) Spatial.RowPlate(r.ContractedBy(2f), new Color(1f, 1f, 1f, 0.05f));

            UILabel(new Rect(r.x + 12f, r.y, r.width - SwitchW - 26f, r.height), label,
                    Palette.Stat, TextAnchor.MiddleLeft);
            Spatial.Switch(new Rect(r.xMax - SwitchW - 10f, r.center.y - SwitchH / 2f, SwitchW, SwitchH), value);

            if (!tip.NullOrEmpty()) TooltipHandler.TipRegion(r, tip);
            if (Widgets.ButtonInvisible(r)) setter(!value);
        }

        private bool ToolRow(Rect r, string label, string tip, bool sep)
        {
            if (sep) Spatial.Separator(r.x + 12f, r.y, r.width - 12f);
            bool over = Mouse.IsOver(r);
            if (over) Spatial.RowPlate(r.ContractedBy(2f), new Color(1f, 1f, 1f, 0.05f));
            UILabel(new Rect(r.x + 12f, r.y, r.width - 24f, r.height), label,
                    over ? Palette.Stat : Palette.Accent, TextAnchor.MiddleLeft);
            if (!tip.NullOrEmpty()) TooltipHandler.TipRegion(r, tip);
            return Widgets.ButtonInvisible(r);
        }

        // ── middle column ───────────────────────────────────────────────────────

        private void DrawMiddle(Rect r)
        {
            float y = r.y;

            // Search + the two destructive/bulk actions. These stay above the list (rather than in
            // the sidebar) because they act on what the list is currently showing.
            float clearW = BtnW("MDT_Clear".Translate());
            float copyW = BtnW("MDT_CopyAll".Translate());
            Rect searchR = new Rect(r.x, y, r.width - clearW - copyW - Gap * 2f, BarH);
            DrawSearch(searchR);
            if (PillButton(new Rect(searchR.xMax + Gap, y, copyW, BarH), "MDT_CopyAll".Translate(),
                           "MDT_CopyAllTip".Translate(), Palette.Accent))
                Copy(BuildAllMessages());
            // Softened red on the destructive pill (the suite's danger-text treatment; full Bad is
            // reserved for status - the error dot and the impact banner - never for a control label).
            if (PillButton(new Rect(searchR.xMax + Gap + copyW + Gap, y, clearW, BarH), "MDT_Clear".Translate(),
                           "MDT_ClearTip".Translate(), Color.Lerp(Palette.Bad, Palette.Bright, 0.35f)))
            {
                Log.Clear();
                LogState.ClearSelection();
                LogState.ListScroll = Vector2.zero;
            }
            y += BarH + Gap;

            y += DrawCompatHint(new Rect(r.x, y, r.width, 0f));

            bool tray = LogWidgets.Any;
            float trayH = tray ? LogWidgets.TrayHeight + Gap : 0f;

            Rect listRect = new Rect(r.x, y, r.width, r.yMax - y - trayH);
            DrawList(listRect);

            if (tray)
            {
                try { LogWidgets.Draw(this, new Rect(r.x, listRect.yMax + Gap, r.width, LogWidgets.TrayHeight), LogState.Selected); }
                catch (Exception e) { Log.ErrorOnce("[Advanced Dev Tools] add-on tray draw failed: " + e, 0x2E19A33); }
            }
        }

        /// <summary>
        /// A one-time strip telling the player that other installed mods put their own buttons on the
        /// vanilla log window. Without this the loss is completely silent - HugsLib's "Share logs"
        /// simply stops existing and the player concludes HugsLib broke. Returns the height consumed
        /// (0 when hidden), so the caller just adds it to its layout cursor.
        /// </summary>
        private float DrawCompatHint(Rect r)
        {
            if (!_hintVisible) return 0f;

            Text.Font = GameFont.Small;
            Text.WordWrap = true;
            const float btnW = 84f;
            float textW = r.width - 26f - btnW - 12f - 22f;
            string text = "MDT_CompatHint".Translate(LogWindowCompat.DecoratorNames());
            float textH = Mathf.Ceil(TextMetrics.Height(text, textW));
            float h = Mathf.Max(textH, 24f) + 18f;

            Rect card = new Rect(r.x, r.y, r.width, h);
            Spatial.Surface(card, new Color(Palette.Accent.r, Palette.Accent.g, Palette.Accent.b, 0.10f));

            Rect badge = new Rect(card.x + 12f, card.y + (h - 18f) / 2f, 18f, 18f);
            Spatial.Dot(badge, Palette.Accent);
            UILabel(badge, "i", Palette.Ink, TextAnchor.MiddleCenter);

            GUI.color = Palette.Stat;
            Text.Anchor = TextAnchor.UpperLeft;
            Widgets.Label(new Rect(card.x + 38f, card.y + 9f, textW, textH), text);
            GUI.color = Color.white;

            // Frosted capsule on the tinted strip (white at low alpha, not a grouped fill).
            Rect dr = new Rect(card.xMax - 12f - btnW, card.y + (h - 24f) / 2f, btnW, 24f);
            bool dOver = Mouse.IsOver(dr);
            Spatial.Pill(dr, new Color(1f, 1f, 1f, dOver ? 0.14f : 0.08f));
            UILabel(dr, "MDT_CompatDismiss".Translate(), Palette.Stat, TextAnchor.MiddleCenter);
            if (Widgets.ButtonInvisible(dr)) LogWindowCompat.DismissHint();

            return h + Gap;
        }

        private void DrawSearch(Rect r)
        {
            // Suite search: rounded inset well + the vanilla magnifier, with a 1px accent ring while
            // focused (two nested plates - the ring costs a second 9-slice only while focused).
            bool focused = GUI.GetNameOfFocusedControl() == "MDT_Search";
            if (focused)
            {
                Spatial.RowPlate(r, new Color(Palette.Accent.r, Palette.Accent.g, Palette.Accent.b, 0.55f));
                Spatial.RowPlate(r.ContractedBy(1f), Palette.BGD);
            }
            else Spatial.RowPlate(r, Palette.BGD);

            GUI.color = Palette.TextFaint;
            GUI.DrawTexture(new Rect(r.x + 9f, r.center.y - 7f, 14f, 14f), Verse.TexButton.Search, ScaleMode.ScaleToFit);
            GUI.color = Color.white;

            Rect field = new Rect(r.x + 29f, r.y, r.width - 35f, r.height);
            string cur = LogState.Search ?? "";
            string edited = Palette.SearchFieldFlat(field, "MDT_Search", cur, "MDT_SearchPlaceholder".Translate());
            if (edited != cur) LogState.Search = edited;
        }

        // --- message list ---

        private void DrawList(Rect rect)
        {
            // No backing plate (B1): the rows float on the window base and the list reads as the
            // window's negative space - the same treatment the sibling tabs give their row areas.
            Rect inner = rect.ContractedBy(2f);

            int n = _filtered.Count;
            float viewH = n * RowH;
            float maxScroll = Mathf.Max(0f, viewH - inner.height);
            LogState.ListScroll.y = Mathf.Clamp(LogState.ListScroll.y, 0f, maxScroll);

            Rect view = new Rect(0f, 0f, inner.width - 16f, Mathf.Max(viewH, inner.height));
            Palette.BeginScroll(inner, ref LogState.ListScroll, view);
            try
            {
                if (n == 0)
                {
                    UILabel(new Rect(0f, 0f, view.width, inner.height), "MDT_EmptyList".Translate(),
                            Palette.TextFaint, TextAnchor.MiddleCenter);
                }
                else
                {
                    int first = Mathf.Max(0, Mathf.FloorToInt(LogState.ListScroll.y / RowH) - 1);
                    int last = Mathf.Min(n, Mathf.CeilToInt((LogState.ListScroll.y + inner.height) / RowH) + 1);
                    for (int i = first; i < last; i++)
                        DrawRow(new Rect(0f, i * RowH, view.width, RowH), _filtered[i], i);
                }
            }
            finally
            {
                Palette.EndScroll();
            }
        }

        private void DrawRow(Rect row, LogMessage msg, int index)
        {
            bool selected = msg == LogState.Selected;

            // Only the SELECTED row gets a rounded plate: a 9-slice per row would be ~800 draw calls
            // a frame on a full list, for corners hidden behind text. Everything else is a hairline
            // separator, which is the iOS grouped-table rule anyway.
            if (selected)
            {
                // RowAlt plate inside a 1px accent ring: two nested 9-slices, affordable because at
                // most ONE row in the whole list is ever selected.
                Spatial.RowPlate(row.ContractedBy(1f), new Color(Palette.Accent.r, Palette.Accent.g, Palette.Accent.b, 0.55f));
                Spatial.RowPlate(row.ContractedBy(2f), Palette.RowAlt);
            }
            else
            {
                if (index > 0) Spatial.Separator(row.x + 10f, row.y, row.width - 14f);
                if (Mouse.IsOver(row)) Widgets.DrawBoxSolid(row, new Color(1f, 1f, 1f, 0.035f));
            }

            // Column order (B1): timestamp gutter first, then the type dot, then chip + text - the
            // time reads as row metadata while the dot sits against the message it describes.
            float x = row.x + 10f;

            // Timestamp column. Fixed width (measured once, memoised) so the message text of every row
            // starts on the same pixel - a ragged left edge on a 1000-row list is unreadable.
            if (ShowStamps)
            {
                string ts = LogTimestamps.Of(msg);
                if (!ts.NullOrEmpty())
                {
                    float sw = StampWidth();
                    UILabel(new Rect(x, row.y, sw, row.height), ts, Palette.TextFaint, TextAnchor.MiddleLeft);
                    x += sw + 8f;
                }
            }

            Spatial.Dot(new Rect(x, row.center.y - 4f, 8f, 8f), TypeColor(msg.type));
            x += 16f;

            // Repeat chip. When the throttle held further repeats back we show them as "+N" rather than
            // silently understating the count - a hidden repeat count would defeat the whole point of
            // the log, and the throttle is only defensible if it is honest about what it suppressed.
            int suppressed = LogThrottle.Enabled ? LogThrottle.SuppressedFor(msg.text, msg.type) : 0;
            if (msg.repeats > 1 || suppressed > 0)
            {
                string rep = "x" + msg.repeats + (suppressed > 0 ? "+" + suppressed : "");
                Text.Font = GameFont.Small;
                float rw = TextMetrics.Size(rep).x + 14f;
                Rect chip = new Rect(x, row.y + (RowH - 17f) / 2f, rw, 17f);
                Spatial.Badge(chip, rep,
                    suppressed > 0 ? new Color(Palette.Warn.r, Palette.Warn.g, Palette.Warn.b, 0.20f) : Palette.RowAlt,
                    suppressed > 0 ? Palette.Warn : Palette.TextDim);
                if (suppressed > 0) TooltipHandler.TipRegion(chip, "MDT_SuppressedTip".Translate(suppressed));
                x += rw + 8f;
            }

            Palette.LabelFit(new Rect(x, row.y, row.xMax - x - 8f, row.height), FirstLine(msg),
                             selected ? Palette.Bright : Palette.Stat);

            if (Widgets.ButtonInvisible(row))
            {
                LogState.Selected = selected ? null : msg;
                LogState.InspectorScroll = Vector2.zero;
            }
        }

        // --- inspector ---

        private void DrawInspector(Rect rect)
        {
            Spatial.Surface(rect, Palette.SidebarBG);   // panel2, matching the sidebar (B1)
            Spatial.TopHighlight(rect);
            Rect inner = rect.ContractedBy(12f);

            LogMessage msg = LogState.Selected;
            if (msg == null)
            {
                UILabel(inner, "MDT_SelectHint".Translate(), Palette.TextFaint, TextAnchor.MiddleCenter);
                return;
            }

            LogAnalysis analysis = LogAnalysisCache.For(msg);
            Rect view = new Rect(0f, 0f, inner.width - 16f, Mathf.Max(_inspH, inner.height));
            Palette.BeginScroll(inner, ref LogState.InspectorScroll, view);
            try
            {
                float y = DrawInspectorContent(view.width, msg, analysis);
                if (Event.current.type == EventType.Layout) _inspH = y;
            }
            finally
            {
                Palette.EndScroll();
            }
        }

        private float DrawInspectorContent(float w, LogMessage msg, LogAnalysis a)
        {
            Text.Font = GameFont.Small;
            float lh = Text.LineHeight;
            float y = 0f;

            // Impact banner: severity + performance impact + known/unknown
            y = DrawImpactBanner(w, y, msg, a);

            // Type header
            string head = TypeLabel(msg.type);
            if (msg.repeats > 1) head += "   x" + msg.repeats;
            UILabel(new Rect(0f, y, w * 0.55f, lh), head, TypeColor(msg.type), TextAnchor.MiddleLeft);

            // Timestamp, right-aligned in the same row. A repeated line carries the time it was FIRST
            // seen (LogMessageQueue folds repeats into the retained message), so it is labelled as such
            // rather than reading as the time of the latest occurrence.
            if (ShowStamps)
            {
                string ts = LogTimestamps.Of(msg);
                if (!ts.NullOrEmpty())
                    UILabel(new Rect(w * 0.45f, y, w * 0.55f, lh),
                            msg.repeats > 1 ? "MDT_FirstSeenAt".Translate(ts) : "MDT_LoggedAt".Translate(ts),
                            Palette.TextFaint, TextAnchor.MiddleRight);
            }
            y += lh + 7f;

            // Message text
            Text.Font = GameFont.Small;
            Text.WordWrap = true;
            string mt = msg.text ?? "";
            float mth = Mathf.Ceil(TextMetrics.Height(mt, w - 24f));
            Rect mwell = new Rect(0f, y, w, mth + 18f);
            Spatial.RowPlate(mwell, Palette.BGD);
            GUI.color = Palette.Stat;
            Widgets.Label(new Rect(mwell.x + 12f, mwell.y + 9f, mwell.width - 24f, mth), mt);
            GUI.color = Color.white;
            y += mwell.height + 10f;

            // Copy actions sit under the message they act on (B1 inspector order: impact, type,
            // message, actions - the top-down "what / how bad / what to do" read of the mockup).
            float bw = (w - 12f) / 3f;
            if (PlainButton(new Rect(0f, y, bw, 26f), "MDT_CopyMessage".Translate(), Palette.Accent)) Copy(msg.text);
            if (PlainButton(new Rect(bw + 6f, y, bw, 26f), "MDT_CopyTrace".Translate(), Palette.Accent)) Copy(msg.StackTrace);
            if (PlainButton(new Rect(2f * (bw + 6f), y, bw, 26f), "MDT_CopyReport".Translate(), Palette.Accent))
                Copy(ReportBuilder.FullReport(msg, a));
            y += 26f + 14f;

            // Likely source
            y = Section(w, y, "MDT_SectionSource".Translate());
            if (a != null && a.Benign)
            {
                y = DrawWrapped(w, y, "MDT_BenignNoSource".Translate(), Palette.TextDim);
            }
            else if (a != null && a.AnyCulprit)
            {
                // Measure first so the whole group sits on one rounded plate with hairlines between.
                int rank = 0;
                float gy = y;
                var rows = new List<AttributedMod>();
                foreach (AttributedMod m in a.Culprits)
                {
                    rows.Add(m);
                    if (++rank >= 8) break;
                }
                float groupH = 0f;
                for (int i = 0; i < rows.Count; i++) groupH += SourceRowHeight(rows[i], lh);
                Spatial.Surface(new Rect(0f, gy, w, groupH), Palette.GroupBG);
                for (int i = 0; i < rows.Count; i++)
                    gy = DrawSourceRow(w, gy, rows[i], i == 0, i > 0, lh);
                y = gy + 4f;
            }
            else
            {
                y = DrawWrapped(w, y, "MDT_NoCulprit".Translate(), Palette.TextDim);
            }
            y += 10f;

            // What this means (diagnoses contributed by all modules)
            y = Section(w, y, "MDT_SectionMeaning".Translate());
            if (a?.Diagnoses != null && a.Diagnoses.Count > 0)
            {
                foreach (ErrorDiagnosis d in a.Diagnoses)
                    y = DrawDiagnosis(w, y, d);
            }
            else
            {
                y = DrawWrapped(w, y, "MDT_NoLibraryMatch".Translate(), Palette.TextDim);
            }
            y += 10f;

            // Custom module sections (advanced third-party modules)
            if (a?.Context != null)
            {
                foreach (ErrorModule module in ErrorModuleRegistry.Modules)
                {
                    if (!module.HasSection) continue;
                    try { y = module.DrawSection(w, y, a.Context); }
                    catch (Exception e) { Log.WarningOnce("[Advanced Dev Tools] module section '" + module.Label + "' failed: " + e.Message, module.GetType().GetHashCode() ^ 13); }
                }
            }

            // Report to the community bug/fix database (never for a normal, no-fault engine line)
            if (msg.type != LogMessageType.Message && !(a != null && a.Benign))
            {
                y = Section(w, y, "MDT_SectionReport".Translate());
                y = DrawReportSection(w, y, msg, a);
                y += 10f;
            }

            // Stack trace
            y = Section(w, y, "MDT_SectionTrace".Translate());
            Text.Font = GameFont.Small;
            Text.WordWrap = true;
            string tr = msg.StackTrace ?? "";
            float trh = Mathf.Ceil(TextMetrics.Height(tr, w - 24f));
            Rect twell = new Rect(0f, y, w, trh + 18f);
            Spatial.RowPlate(twell, Palette.BGD);
            GUI.color = Palette.TextDim;
            Widgets.Label(new Rect(twell.x + 12f, twell.y + 9f, twell.width - 24f, trh), tr);
            GUI.color = Color.white;
            y += twell.height + 6f;

            return y;
        }

        private float DrawReportSection(float w, float y, LogMessage msg, LogAnalysis a)
        {
            Text.Font = GameFont.Small;
            bool known = ReportBuilder.AlreadyKnown(a);
            string blurb = known ? "MDT_ReportKnown".Translate() : "MDT_ReportBlurb".Translate();
            float innerW = w - 28f;
            float blurbH = Mathf.Ceil(TextMetrics.Height(blurb, innerW));
            float cardH = 12f + blurbH + 9f + 26f + 12f;
            Rect card = new Rect(0f, y, w, cardH);
            Spatial.Surface(card, Palette.GroupBG);

            float cx = card.x + 14f;
            float cy = card.y + 12f;
            Text.WordWrap = true;
            GUI.color = Palette.TextDim;
            Widgets.Label(new Rect(cx, cy, innerW, blurbH), blurb);
            GUI.color = Color.white;
            cy += blurbH + 9f;

            float bw = (innerW - 8f) / 2f;
            string primary = known ? "MDT_ReportAddDetail".Translate() : "MDT_ReportBtn".Translate();
            if (FilledButton(new Rect(cx, cy, bw, 26f), primary, "MDT_ReportTip".Translate()))
                Find.WindowStack.Add(new Dialog_Report(msg, a, Dialog_Report.ReportMode.Bug));
            if (PlainButton(new Rect(cx + bw + 8f, cy, bw, 26f), "MDT_SubmitFix".Translate(),
                            Palette.Accent, "MDT_SubmitFixTip".Translate()))
                Find.WindowStack.Add(new Dialog_Report(msg, a, Dialog_Report.ReportMode.Fix));

            return y + cardH;
        }

        private float DrawImpactBanner(float w, float y, LogMessage msg, LogAnalysis a)
        {
            // Cached on the analysis and keyed on msg.repeats: this scans the whole stack trace against
            // ~30 signal strings and would otherwise re-run on every OnGUI pass.
            ImpactResult imp = a != null ? a.Impact(msg) : ImpactAssessor.Assess(msg, null);
            Color sev = ImpactAssessor.ColorFor(imp.Level);
            const float h = 36f;
            Rect r = new Rect(0f, y, w, h);
            Spatial.RowPlate(r, new Color(sev.r, sev.g, sev.b, 0.15f));

            Text.Font = GameFont.Small;
            string sevLabel = ImpactAssessor.LabelFor(imp.Level);
            float sevW = TextMetrics.Size(sevLabel).x;
            UILabel(new Rect(r.x + 12f, r.y, sevW + 6f, r.height), sevLabel, sev, TextAnchor.MiddleLeft);

            string badge = imp.Benign ? "MDT_Benign".Translate() : (imp.Known ? "MDT_Known".Translate() : "MDT_Unknown".Translate());
            Color badgeCol = imp.Benign ? Palette.Good : (imp.Known ? Palette.Accent : Palette.TextDim);
            float badgeW = TextMetrics.Size(badge).x + 18f;
            Spatial.Badge(new Rect(r.xMax - badgeW - 10f, r.center.y - 9f, badgeW, 18f), badge,
                          new Color(badgeCol.r, badgeCol.g, badgeCol.b, 0.20f), badgeCol);

            if (!imp.PerfNote.NullOrEmpty())
                Palette.LabelFit(new Rect(r.x + 18f + sevW, r.y, Mathf.Max(4f, r.width - sevW - badgeW - 40f), r.height),
                                 imp.PerfNote, Palette.TextDim);

            return y + h + 10f;
        }

        private static float SourceRowHeight(AttributedMod m, float lh)
        {
            bool hasReason = !(m.TopReason ?? "").NullOrEmpty();
            return 10f + lh + (hasReason ? 2f + lh : 0f) + 10f;
        }

        private float DrawSourceRow(float w, float y, AttributedMod m, bool top, bool sep, float lh)
        {
            Text.Font = GameFont.Small;
            string reason = m.TopReason ?? "";
            bool hasReason = reason.Length > 0;
            float rowH = SourceRowHeight(m, lh);
            Rect row = new Rect(0f, y, w, rowH);
            if (sep) Spatial.Separator(row.x + 14f, row.y, row.width - 14f);

            string tag; Color tagCol;
            if (!m.Installed) { tag = "MDT_StateNotInstalled".Translate(); tagCol = Palette.Warn; }
            else if (m.Active) { tag = "MDT_StateActive".Translate(); tagCol = Palette.Good; }
            else { tag = "MDT_StateInactive".Translate(); tagCol = Palette.TextDim; }
            float tagW = TextMetrics.Size(tag).x + 18f;

            Rect nameR = new Rect(row.x + 14f, row.y + 10f, row.width - tagW - 30f, lh);
            bool hasLink = !m.Url.NullOrEmpty();
            Palette.LabelFit(nameR, m.Name, hasLink ? Palette.Accent : (top ? Palette.Bright : Palette.Stat));
            if (hasLink)
            {
                if (Mouse.IsOver(nameR)) TooltipHandler.TipRegion(nameR, "MDT_OpenModPage".Translate().ToString() + "\n" + m.Url);
                if (Widgets.ButtonInvisible(nameR)) Application.OpenURL(m.Url);
            }

            Spatial.Badge(new Rect(row.xMax - tagW - 12f, row.y + 10f + (lh - 18f) / 2f, tagW, 18f), tag,
                          new Color(tagCol.r, tagCol.g, tagCol.b, 0.18f), tagCol);

            if (hasReason)
                Palette.LabelFit(new Rect(row.x + 14f, row.y + 10f + lh + 2f, row.width - 26f, lh), reason, Palette.TextDim);

            string tip = m.PackageId.NullOrEmpty() ? null : m.PackageId;
            if (m.Reasons.Count > 1) tip = (tip.NullOrEmpty() ? "" : tip + "\n") + string.Join("\n", m.Reasons);
            if (!tip.NullOrEmpty()) TooltipHandler.TipRegion(row, tip);

            return y + rowH;
        }

        private float DrawDiagnosis(float w, float y, ErrorDiagnosis d)
        {
            Text.Font = GameFont.Small;
            float lh = Text.LineHeight;
            float innerW = w - 28f;

            string title = d.Title ?? "";
            string desc = d.Explanation ?? "";
            string fix = d.Fix ?? "";
            bool hasUrl = !d.Url.NullOrEmpty();

            float titleH = lh;
            Text.WordWrap = true;
            float descH = desc.Length > 0 ? Mathf.Ceil(TextMetrics.Height(desc, innerW)) : 0f;
            string fixLine = fix.Length > 0 ? ("MDT_FixLabel".Translate().ToString() + " " + fix) : "";
            float fixH = fixLine.Length > 0 ? Mathf.Ceil(TextMetrics.Height(fixLine, innerW)) : 0f;
            float urlH = hasUrl ? lh : 0f;

            float blockH = 12f + titleH + (descH > 0 ? 5f + descH : 0f) + (fixH > 0 ? 7f + fixH : 0f) + (urlH > 0 ? 5f + urlH : 0f) + 12f;
            Rect card = new Rect(0f, y, w, blockH);
            Spatial.Surface(card, d.Benign ? Color.Lerp(Palette.GroupBG, Palette.Good, 0.10f) : Palette.GroupBG);

            float cy = card.y + 12f;
            float cx = card.x + 14f;
            float titleW = d.Ignorable ? innerW - 74f : innerW;
            UILabel(new Rect(cx, cy, titleW, titleH), title, Palette.Bright, TextAnchor.MiddleLeft);
            if (d.Ignorable)
            {
                // Small capsule chip, dim until hovered - the mockup's quiet Ignore affordance.
                Rect igR = new Rect(card.xMax - 14f - 66f, cy + (titleH - 20f) / 2f, 66f, 20f);
                bool igOver = Mouse.IsOver(igR);
                Spatial.Pill(igR, igOver ? Color.Lerp(Palette.RowAlt, Palette.BGL, 0.5f) : Palette.RowAlt);
                UILabel(igR, "MDT_Ignore".Translate(), igOver ? Palette.Stat : Palette.TextDim, TextAnchor.MiddleCenter);
                TooltipHandler.TipRegion(igR, "MDT_IgnoreTip".Translate());
                if (Widgets.ButtonInvisible(igR))
                    ModernDevToolsMod.IgnoreIssue(d.Source);
            }
            cy += titleH;

            Text.WordWrap = true;
            if (descH > 0)
            {
                cy += 5f;
                GUI.color = Palette.TextDim;
                Widgets.Label(new Rect(cx, cy, innerW, descH), desc);
                GUI.color = Color.white;
                cy += descH;
            }
            if (fixH > 0)
            {
                cy += 7f;
                GUI.color = Palette.Good;
                Widgets.Label(new Rect(cx, cy, innerW, fixH), fixLine);
                GUI.color = Color.white;
                cy += fixH;
            }
            if (urlH > 0)
            {
                cy += 5f;
                UILabel(new Rect(cx, cy, innerW, urlH), d.Url, Palette.Accent, TextAnchor.MiddleLeft);
            }

            return y + blockH + 8f;
        }

        // --- shared bits ---

        /// <summary>Uppercase micro section header - see <see cref="Micro"/> for why headers (and
        /// only headers) are uppercased despite the sentence-case house rule.</summary>
        private float Section(float w, float y, string label)
        {
            UILabel(new Rect(4f, y, w - 8f, HeaderH), Micro(label), Palette.TextFaint, TextAnchor.MiddleLeft);
            return y + HeaderH + 4f;
        }

        private float DrawWrapped(float w, float y, string text, Color color)
        {
            Text.Font = GameFont.Small;
            Text.WordWrap = true;
            float h = Mathf.Ceil(TextMetrics.Height(text, w));
            GUI.color = color;
            Widgets.Label(new Rect(0f, y, w, h), text);
            GUI.color = Color.white;
            return y + h + 2f;
        }

        /// <summary>Single-line label with an explicit anchor. Wraps the four-line save/restore dance
        /// every label in this window otherwise repeats.</summary>
        private static void UILabel(Rect r, string text, Color color, TextAnchor anchor)
        {
            Text.Anchor = anchor;
            bool prevWrap = Text.WordWrap;
            Text.WordWrap = false;
            GUI.color = color;
            Widgets.Label(r, text);
            GUI.color = Color.white;
            Text.WordWrap = prevWrap;
            Text.Anchor = TextAnchor.UpperLeft;
        }

        // The window's three button shapes. Each vanilla branch is the engine's own ButtonText
        // (tint/fill distinctions are Modern grammar; vanilla buttons are deliberately uniform).
        // Control parity: ButtonText emits exactly one invisible button, same as each modern path.

        /// <summary>Neutral grouped-fill button with tinted text (the suite "plain" button).</summary>
        private static bool PlainButton(Rect r, string label, Color? tint = null, string tooltip = null)
        {
            if (!Palette.ModernSkin)
            {
                if (!tooltip.NullOrEmpty()) TooltipHandler.TipRegion(r, tooltip);
                return Widgets.ButtonText(r, label);
            }
            bool over = Mouse.IsOver(r);
            Spatial.RowPlate(r, over ? Palette.RowAlt : Palette.GroupBG);
            UILabel(r, label, tint ?? Palette.Stat, TextAnchor.MiddleCenter);
            if (!tooltip.NullOrEmpty()) TooltipHandler.TipRegion(r, tooltip);
            return Widgets.ButtonInvisible(r);
        }

        /// <summary>Accent-filled primary button. Ink text on the accent fill - the token the sibling
        /// tabs use on every accent surface (Palette.BG was close, but it is a surface colour).</summary>
        private static bool FilledButton(Rect r, string label, string tooltip = null)
        {
            if (!Palette.ModernSkin)
            {
                if (!tooltip.NullOrEmpty()) TooltipHandler.TipRegion(r, tooltip);
                return Widgets.ButtonText(r, label);
            }
            bool over = Mouse.IsOver(r);
            Spatial.RowPlate(r, over ? Color.Lerp(Palette.Accent, Color.white, 0.12f) : Palette.Accent);
            UILabel(r, label, Palette.Ink, TextAnchor.MiddleCenter);
            if (!tooltip.NullOrEmpty()) TooltipHandler.TipRegion(r, tooltip);
            return Widgets.ButtonInvisible(r);
        }

        /// <summary>Capsule action pill (the suite's control shape for bar-level actions) whose LABEL
        /// carries the tint - Copy all / Clear, where colour separates bulk from destructive.</summary>
        private static bool PillButton(Rect r, string label, string tooltip, Color tint)
        {
            if (!Palette.ModernSkin)
            {
                if (!tooltip.NullOrEmpty()) TooltipHandler.TipRegion(r, tooltip);
                return Widgets.ButtonText(r, label);
            }
            bool over = Mouse.IsOver(r);
            Text.Font = GameFont.Small;
            Spatial.Pill(r, over ? Palette.RowAlt : Palette.GroupBG);
            UILabel(r, label, tint, TextAnchor.MiddleCenter);
            if (!tooltip.NullOrEmpty()) TooltipHandler.TipRegion(r, tooltip);
            return Widgets.ButtonInvisible(r);
        }

        private static float BtnW(string label)
        {
            Text.Font = GameFont.Small;
            return Mathf.Max(64f, TextMetrics.Size(label).x + 24f);
        }

        private static string ModChangeTooltip()
        {
            var sb = new StringBuilder("MDT_ModsChangedTip".Translate());
            sb.AppendLine();
            var rep = ModChange.Report;
            for (int i = 0; i < rep.Count && i < 25; i++) sb.AppendLine("- " + rep[i].Name);
            return sb.ToString();
        }

        private static void SetVisibleType(LogMessageType type, bool value)
        {
            switch (type)
            {
                case LogMessageType.Error: LogState.ShowErrors = value; break;
                case LogMessageType.Warning: LogState.ShowWarnings = value; break;
                default: LogState.ShowMessages = value; break;
            }
        }

        /// <summary>Whether the timestamp column/label is shown (player setting, on by default).</summary>
        private static bool ShowStamps
        {
            get
            {
                ModernDevToolsSettings s = ModernDevToolsMod.Settings;
                return (s == null || s.showTimestamps) && LogTimestamps.Available;
            }
        }

        /// <summary>Width of the timestamp column. Measured from the widest possible "HH:mm:ss" so the
        /// column never reflows as the clock ticks; TextMetrics memoises, so this is a dictionary hit.</summary>
        private static float StampWidth()
        {
            Text.Font = GameFont.Small;
            return Mathf.Ceil(TextMetrics.Size("00:00:00").x) + 2f;
        }

        private static Color TypeColor(LogMessageType t) =>
            t == LogMessageType.Error ? Palette.Bad : (t == LogMessageType.Warning ? Palette.Warn : Palette.StripGray);

        private static string TypeLabel(LogMessageType t)
        {
            switch (t)
            {
                case LogMessageType.Error: return "MDT_TypeError".Translate();
                case LogMessageType.Warning: return "MDT_TypeWarning".Translate();
                default: return "MDT_TypeMessage".Translate();
            }
        }

        // First-line cache. The row draw runs per visible row per OnGUI pass, and Substring on a long
        // modded error line is a fresh allocation each time - ~100 per frame of pure garbage on a full
        // window. A message's text never changes (repeats fold into the same instance), so the first
        // line is computed once per message; the weak table evicts it with the message itself.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<LogMessage, string> _firstLines =
            new System.Runtime.CompilerServices.ConditionalWeakTable<LogMessage, string>();

        private static string FirstLine(LogMessage msg)
        {
            if (msg == null) return "";
            if (_firstLines.TryGetValue(msg, out string cached)) return cached;
            string line = ComputeFirstLine(msg.text);
            try { _firstLines.Add(msg, line); } catch (ArgumentException) { /* benign add race */ }
            return line;
        }

        private static string ComputeFirstLine(string text)
        {
            if (text.NullOrEmpty()) return "";
            int nl = text.IndexOf('\n');
            string line = nl >= 0 ? text.Substring(0, nl) : text;
            if (line.Length > 300) line = line.Substring(0, 300);
            return line;
        }

        private static void Copy(string text)
        {
            try
            {
                GUIUtility.systemCopyBuffer = text ?? "";
                Messages.Message("MDT_Copied".Translate(), MessageTypeDefOf.SilentInput, false);
            }
            catch { }
        }

        private void RebuildFiltered()
        {
            if (LogState.Revision == _lastRev && _lastIgnoreVer == ModernDevToolsMod.IgnoreVersion
                && _lastSearch == LogState.Search
                && _lastSE == LogState.ShowErrors && _lastSW == LogState.ShowWarnings && _lastSM == LogState.ShowMessages)
                return;

            try
            {
                _filtered.Clear();
                _cErr = _cWarn = _cMsg = 0;
                _hidden = 0;
                string s = LogState.Search;
                bool hasSearch = !string.IsNullOrEmpty(s);
                // Prepared once for the whole rebuild, not once per message.
                KnownIssueIndex.IgnoreMatcher ignoreMatcher =
                    KnownIssueIndex.IgnoreMatcherFor(ModernDevToolsMod.Settings?.ignoredIssues);
                bool hasIgnores = ignoreMatcher.Any;
                LogMessage sel = LogState.Selected;
                bool selExists = false;

                foreach (LogMessage m in Log.Messages)
                {
                    switch (m.type)
                    {
                        case LogMessageType.Error: _cErr++; break;
                        case LogMessageType.Warning: _cWarn++; break;
                        default: _cMsg++; break;
                    }
                    if (m == sel) selExists = true;
                    if (!LogState.VisibleType(m.type)) continue;
                    if (hasSearch)
                    {
                        string txt = m.text;
                        if (txt == null || txt.IndexOf(s, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    }
                    if (hasIgnores && ignoreMatcher.Matches(m.text)) { _hidden++; continue; }
                    _filtered.Add(m);
                }

                if (sel != null && !selExists) LogState.ClearSelection();
            }
            catch
            {
                // Log.Messages mutated mid-enumeration on another thread; keep the previous snapshot.
                return;
            }

            _lastRev = LogState.Revision;
            _lastIgnoreVer = ModernDevToolsMod.IgnoreVersion;
            _lastSearch = LogState.Search;
            _lastSE = LogState.ShowErrors;
            _lastSW = LogState.ShowWarnings;
            _lastSM = LogState.ShowMessages;
        }

        private static string BuildAllMessages()
        {
            var sb = new StringBuilder();
            try
            {
                foreach (LogMessage m in Log.Messages)
                {
                    if (sb.Length != 0) sb.AppendLine();
                    sb.AppendLine(m.text);
                    sb.Append(m.StackTrace);
                    if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.AppendLine();
                }
            }
            catch { }
            return sb.ToString();
        }
    }
}
