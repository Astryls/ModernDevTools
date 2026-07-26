using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// The modern replacement for RimWorld's debug log. Left: a clean, filterable, searchable
    /// message list. Right: an inspector that attributes the selected error to the likely culprit
    /// mod and surfaces curated guidance from the shipped knowledge library, with the full stack
    /// trace beneath. Full vanilla parity: filtering, clear, copy, auto-open, pause-on-error.
    /// </summary>
    public class Window_ModernLog : Window
    {
        private const float Pad = 10f;
        private const float Gap = 8f;
        private const float RowH = 26f;
        private const float ToolbarH = 28f;

        private readonly List<LogMessage> _filtered = new List<LogMessage>();
        private int _cErr, _cWarn, _cMsg;

        // Filtered-view cache gate (rebuild only on real change).
        private int _lastRev = -1;
        private int _lastIgnoreVer = -1;
        private int _hidden;
        private string _lastSearch;
        private bool _lastSE = true, _lastSW = true, _lastSM = true;

        private float _inspH = 200f; // cached inspector content height for the scroll viewRect

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
            new Vector2(Mathf.Min(UI.screenWidth * 0.66f, 1180f), Mathf.Min(UI.screenHeight * 0.62f, 780f));

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
                Log.ErrorOnce("[Modern Dev Tools] window draw failed: " + e, 0x2E19A30);
            }
            finally
            {
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.UpperLeft;
                Text.WordWrap = true;
            }
        }

        private void DrawAll(Rect inRect)
        {
            Widgets.DrawBoxSolid(inRect, Palette.BG);
            Palette.DrawBox(inRect, Palette.BGL, 1);

            Rect content = inRect.ContractedBy(Pad);
            float y = content.y;

            // Header (title + shown-count). The close X is drawn by the base window at top-right.
            DrawHeader(new Rect(content.x, y, content.width, ToolbarH));
            y += ToolbarH + Gap;

            RebuildFiltered();

            // Toolbar A: filters (left) + state toggles (right)
            DrawToolbarA(new Rect(content.x, y, content.width, ToolbarH));
            y += ToolbarH + Gap;

            // Toolbar B: search (left) + copy/clear (right)
            DrawToolbarB(new Rect(content.x, y, content.width, ToolbarH));
            y += ToolbarH + Gap;

            // Body: message list (left) + inspector (right)
            Rect body = new Rect(content.x, y, content.width, content.yMax - y);
            float inspW = Mathf.Clamp(body.width * 0.38f, 330f, 470f);
            Rect listRect = new Rect(body.x, body.y, body.width - inspW - Gap, body.height);
            Rect inspRect = new Rect(listRect.xMax + Gap, body.y, inspW, body.height);

            try { DrawList(listRect); }
            catch (Exception e) { Log.ErrorOnce("[Modern Dev Tools] list draw failed: " + e, 0x2E19A31); }

            try { DrawInspector(inspRect); }
            catch (Exception e) { Log.ErrorOnce("[Modern Dev Tools] inspector draw failed: " + e, 0x2E19A32); }
        }

        private void DrawHeader(Rect r)
        {
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleLeft;
            Text.WordWrap = false;
            GUI.color = Palette.Stat;
            Widgets.Label(new Rect(r.x, r.y, r.width * 0.6f, r.height), "MDT_Title".Translate());

            GUI.color = Palette.TextDim;
            Text.Anchor = TextAnchor.MiddleRight;
            // leave ~30px clear on the right for the base close X
            string shown = "MDT_Shown".Translate(_filtered.Count).ToString();
            if (_hidden > 0) shown += "  " + "MDT_HiddenSuffix".Translate(_hidden);
            Widgets.Label(new Rect(r.x, r.y, r.width - 30f, r.height), shown);

            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = true;
            if (Palette.CloseX(new Rect(r.xMax - 26f, r.y + 2f, 22f, 22f))) Close();
        }

        private void DrawToolbarA(Rect r)
        {
            float x = r.x;
            x = DrawFilterChip(x, r.y, r.height, "MDT_Errors".Translate(), _cErr, Palette.Bad, LogMessageType.Error);
            x = DrawFilterChip(x + 6f, r.y, r.height, "MDT_Warnings".Translate(), _cWarn, Palette.Warn, LogMessageType.Warning);
            x = DrawFilterChip(x + 6f, r.y, r.height, "MDT_Messages".Translate(), _cMsg, Palette.StripGray, LogMessageType.Message);

            // State toggles, right-aligned.
            float rx = r.xMax;
            DrawModChangeIndicator(ref rx, r);
            rx = DrawStateToggleRightward(ref rx, r.y, r.height, "MDT_OpenOnWarnings".Translate(), LogState.OpenOnWarnings, "MDT_OpenOnWarningsTip".Translate(),
                v => LogState.OpenOnWarnings = v);
            rx = DrawStateToggleRightward(ref rx, r.y, r.height, "MDT_PauseOnError".Translate(), LogState.PauseOnError, "MDT_PauseOnErrorTip".Translate(),
                v => LogState.PauseOnError = v);
            rx = DrawStateToggleRightward(ref rx, r.y, r.height, "MDT_AutoOpen".Translate(), LogState.AutoOpen, "MDT_AutoOpenTip".Translate(),
                v => LogState.AutoOpen = v);
        }

        private void DrawToolbarB(Rect r)
        {
            // Right: Clear then Copy all.
            float clearW = BtnW("MDT_Clear".Translate());
            float copyW = BtnW("MDT_CopyAll".Translate());
            float modW = BtnW("MDT_Modules".Translate());
            Rect clearR = new Rect(r.xMax - clearW, r.y, clearW, r.height);
            Rect copyR = new Rect(clearR.x - Gap - copyW, r.y, copyW, r.height);
            Rect modR = new Rect(copyR.x - Gap - modW, r.y, modW, r.height);
            float updW = BtnW("MDT_CommUpdate".Translate());
            Rect updR = new Rect(modR.x - Gap - updW, r.y, updW, r.height);
            float kbW = BtnW("MDT_KbButton".Translate());
            Rect kbR = new Rect(updR.x - Gap - kbW, r.y, kbW, r.height);

            if (Palette.GrayButton(clearR, "MDT_Clear".Translate(), "MDT_ClearTip".Translate()))
            {
                Log.Clear();
                LogState.ClearSelection();
                LogState.ListScroll = Vector2.zero;
            }
            if (Palette.GrayButton(copyR, "MDT_CopyAll".Translate(), "MDT_CopyAllTip".Translate()))
                Copy(BuildAllMessages());
            if (Palette.GrayButton(modR, "MDT_Modules".Translate(), "MDT_ModulesTip".Translate()))
            {
                if (!Find.WindowStack.IsOpen<Dialog_Modules>()) Find.WindowStack.Add(new Dialog_Modules());
            }
            if (Palette.GrayButton(updR, "MDT_CommUpdate".Translate(), "MDT_CommUpdateTip".Translate()))
            {
                if (!ModernDevToolsMod.Settings.enableCommunityData)
                {
                    ModernDevToolsMod.Settings.enableCommunityData = true;
                    ModernDevToolsMod.Instance?.WriteSettings();
                    Messages.Message("MDT_CommEnabledMsg".Translate(), MessageTypeDefOf.TaskCompletion, false);
                }
                CommunityData.Update();
            }
            if (Palette.GrayButton(kbR, "MDT_KbButton".Translate(), "MDT_KbButtonTip".Translate()))
            {
                if (!Find.WindowStack.IsOpen<Window_KnowledgeBase>()) Find.WindowStack.Add(new Window_KnowledgeBase());
            }

            // Left: search field filling the rest.
            Rect searchR = new Rect(r.x, r.y, kbR.x - Gap - r.x, r.height);
            DrawSearch(searchR);
        }

        private void DrawSearch(Rect r)
        {
            string cur = LogState.Search ?? "";
            string edited = Palette.SearchField(r, "MDT_Search", cur, "MDT_SearchPlaceholder".Translate());
            if (edited != cur) LogState.Search = edited;
        }

        // --- message list ---

        private void DrawList(Rect rect)
        {
            Palette.DrawWell(rect);
            Rect inner = rect.ContractedBy(1f);

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
                    Text.Font = GameFont.Small;
                    Text.Anchor = TextAnchor.MiddleCenter;
                    GUI.color = Palette.TextDim;
                    Widgets.Label(new Rect(0f, 0f, view.width, inner.height), "MDT_EmptyList".Translate());
                    GUI.color = Color.white;
                    Text.Anchor = TextAnchor.UpperLeft;
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
            bool alt = (index & 1) == 1;
            Color plate = selected ? Color.Lerp(Palette.PanelBG, Palette.BGL, 0.55f)
                                   : (alt ? Palette.RowAlt : Palette.PanelBG);
            if (!selected && Mouse.IsOver(row)) plate = Color.Lerp(plate, Palette.BGL, 0.45f);
            Widgets.DrawBoxSolid(row, plate);
            Palette.StateStrip(row, TypeColor(msg.type), 3f);

            float x = row.x + 3f + 6f;
            if (msg.repeats > 1)
            {
                string rep = "x" + msg.repeats;
                Text.Font = GameFont.Small;
                float rw = Text.CalcSize(rep).x + 10f;
                Rect chip = new Rect(x, row.y + (RowH - 18f) / 2f, rw, 18f);
                Widgets.DrawBoxSolid(chip, Palette.BGD);
                Text.Anchor = TextAnchor.MiddleCenter;
                GUI.color = Palette.TextDim;
                Widgets.Label(chip, rep);
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                x += rw + 6f;
            }

            Palette.LabelFit(new Rect(x, row.y, row.xMax - x - 6f, row.height), FirstLine(msg.text), Palette.Stat);

            if (Widgets.ButtonInvisible(row))
            {
                LogState.Selected = selected ? null : msg;
                LogState.InspectorScroll = Vector2.zero;
            }
        }

        // --- inspector ---

        private void DrawInspector(Rect rect)
        {
            Palette.DrawCard(rect);
            Rect inner = rect.ContractedBy(10f);

            LogMessage msg = LogState.Selected;
            if (msg == null)
            {
                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.MiddleCenter;
                GUI.color = Palette.TextDim;
                Widgets.Label(inner, "MDT_SelectHint".Translate());
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
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

            // Copy actions
            float bw = (w - 12f) / 3f;
            if (Palette.GrayButton(new Rect(0f, y, bw, 26f), "MDT_CopyMessage".Translate())) Copy(msg.text);
            if (Palette.GrayButton(new Rect(bw + 6f, y, bw, 26f), "MDT_CopyTrace".Translate())) Copy(msg.StackTrace);
            if (Palette.GrayButton(new Rect(2f * (bw + 6f), y, bw, 26f), "MDT_CopyReport".Translate())) Copy(ReportBuilder.FullReport(msg, a));
            y += 26f + 10f;

            // Type header
            Text.Anchor = TextAnchor.MiddleLeft;
            Text.WordWrap = false;
            string head = TypeLabel(msg.type);
            if (msg.repeats > 1) head += "   (x" + msg.repeats + ")";
            GUI.color = TypeColor(msg.type);
            Widgets.Label(new Rect(0f, y, w, lh), head);
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = true;
            y += lh + 6f;

            // Message text
            string mt = msg.text ?? "";
            float mth = Mathf.Ceil(Text.CalcHeight(mt, w - 12f));
            Rect mwell = new Rect(0f, y, w, mth + 10f);
            Palette.DrawWell(mwell);
            GUI.color = Palette.Stat;
            Widgets.Label(new Rect(mwell.x + 6f, mwell.y + 5f, mwell.width - 12f, mth), mt);
            GUI.color = Color.white;
            y += mwell.height + 12f;

            // Likely source
            y = Section(w, y, "MDT_SectionSource".Translate());
            if (a != null && a.Benign)
            {
                y = DrawWrapped(w, y, "MDT_BenignNoSource".Translate(), Palette.TextDim);
            }
            else if (a != null && a.AnyCulprit)
            {
                int rank = 0;
                foreach (AttributedMod m in a.Culprits)
                {
                    y = DrawSourceRow(w, y, m, rank == 0);
                    if (++rank >= 8) break;
                }
            }
            else
            {
                y = DrawWrapped(w, y, "MDT_NoCulprit".Translate(), Palette.TextDim);
            }
            y += 8f;

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
            y += 8f;

            // Custom module sections (advanced third-party modules)
            if (a?.Context != null)
            {
                foreach (ErrorModule module in ErrorModuleRegistry.Modules)
                {
                    if (!module.HasSection) continue;
                    try { y = module.DrawSection(w, y, a.Context); }
                    catch (Exception e) { Log.WarningOnce("[Modern Dev Tools] module section '" + module.Label + "' failed: " + e.Message, module.GetType().GetHashCode() ^ 13); }
                }
            }

            // Report to the community bug/fix database (never for a normal, no-fault engine line)
            if (msg.type != LogMessageType.Message && !(a != null && a.Benign))
            {
                y = Section(w, y, "MDT_SectionReport".Translate());
                y = DrawReportSection(w, y, msg, a);
                y += 8f;
            }

            // Stack trace
            y = Section(w, y, "MDT_SectionTrace".Translate());
            string tr = msg.StackTrace ?? "";
            float trh = Mathf.Ceil(Text.CalcHeight(tr, w - 12f));
            Rect twell = new Rect(0f, y, w, trh + 10f);
            Palette.DrawWell(twell);
            GUI.color = Palette.TextDim;
            Widgets.Label(new Rect(twell.x + 6f, twell.y + 5f, twell.width - 12f, trh), tr);
            GUI.color = Color.white;
            y += twell.height + 6f;

            return y;
        }

        private float DrawReportSection(float w, float y, LogMessage msg, LogAnalysis a)
        {
            Text.Font = GameFont.Small;
            bool known = ReportBuilder.AlreadyKnown(a);
            string blurb = known ? "MDT_ReportKnown".Translate() : "MDT_ReportBlurb".Translate();
            float innerW = w - 20f;
            float blurbH = Mathf.Ceil(Text.CalcHeight(blurb, innerW));
            float cardH = 8f + blurbH + 6f + 26f + 8f;
            Rect card = new Rect(0f, y, w, cardH);
            Palette.DrawCard(card);
            Palette.StateStrip(card, known ? Palette.Good : Palette.Accent, 3f);

            float cx = card.x + 10f;
            float cy = card.y + 8f;
            GUI.color = Palette.TextDim;
            Widgets.Label(new Rect(cx, cy, innerW, blurbH), blurb);
            GUI.color = Color.white;
            cy += blurbH + 6f;

            float bw = (innerW - 6f) / 2f;
            string primary = known ? "MDT_ReportAddDetail".Translate() : "MDT_ReportBtn".Translate();
            if (Palette.GrayButton(new Rect(cx, cy, bw, 26f), primary, "MDT_ReportTip".Translate()))
                Find.WindowStack.Add(new Dialog_Report(msg, a, Dialog_Report.ReportMode.Bug));
            if (Palette.GrayButton(new Rect(cx + bw + 6f, cy, bw, 26f), "MDT_SubmitFix".Translate(), "MDT_SubmitFixTip".Translate()))
                Find.WindowStack.Add(new Dialog_Report(msg, a, Dialog_Report.ReportMode.Fix));

            return y + cardH;
        }

        private float DrawImpactBanner(float w, float y, LogMessage msg, LogAnalysis a)
        {
            ImpactResult imp = ImpactAssessor.Assess(msg, a);
            Color sev = ImpactAssessor.ColorFor(imp.Level);
            const float h = 30f;
            Rect r = new Rect(0f, y, w, h);
            Widgets.DrawBoxSolid(r, Palette.PanelBG);
            Palette.DrawBox(r, Palette.BGL, 1);
            Palette.StateStrip(r, sev, 3f);

            Text.Font = GameFont.Small;
            Text.WordWrap = false;

            string badge = imp.Benign ? "MDT_Benign".Translate() : (imp.Known ? "MDT_Known".Translate() : "MDT_Unknown".Translate());
            Color badgeCol = imp.Benign ? Palette.Good : (imp.Known ? Palette.Accent : Palette.TextDim);
            float badgeW = Text.CalcSize(badge).x + 8f;
            Text.Anchor = TextAnchor.MiddleRight;
            GUI.color = badgeCol;
            Widgets.Label(new Rect(r.x, r.y, r.width - 10f, r.height), badge);

            string sevLabel = ImpactAssessor.LabelFor(imp.Level);
            float sevW = Text.CalcSize(sevLabel).x;
            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color = sev;
            Widgets.Label(new Rect(r.x + 12f, r.y, sevW + 6f, r.height), sevLabel);
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = true;

            if (!imp.PerfNote.NullOrEmpty())
                Palette.LabelFit(new Rect(r.x + 18f + sevW, r.y, r.width - sevW - badgeW - 34f, r.height), imp.PerfNote, Palette.TextDim);

            return y + h + 8f;
        }

        private float DrawSourceRow(float w, float y, AttributedMod m, bool top)
        {
            Text.Font = GameFont.Small;
            float lh = Text.LineHeight;
            string reason = m.TopReason ?? "";
            bool hasReason = reason.Length > 0;
            float rowH = 8f + lh + (hasReason ? 2f + lh : 0f) + 8f;
            Rect row = new Rect(0f, y, w, rowH);
            Widgets.DrawBoxSolid(row, top ? Color.Lerp(Palette.PanelBG, Palette.BGL, 0.35f) : Palette.PanelBG);
            Palette.DrawBox(row, Palette.BGL, 1);
            Palette.StateStrip(row, top ? Palette.Accent : Palette.StripGray, 3f);

            string tag; Color tagCol;
            if (!m.Installed) { tag = "MDT_StateNotInstalled".Translate(); tagCol = Palette.Warn; }
            else if (m.Active) { tag = "MDT_StateActive".Translate(); tagCol = Palette.Good; }
            else { tag = "MDT_StateInactive".Translate(); tagCol = Palette.TextDim; }
            float tagW = Text.CalcSize(tag).x + 12f;

            Rect nameR = new Rect(row.x + 12f, row.y + 8f, row.width - tagW - 16f, lh);
            bool hasLink = !m.Url.NullOrEmpty();
            Palette.LabelFit(nameR, m.Name, hasLink ? Palette.Accent : Palette.Stat);
            if (hasLink)
            {
                float ulW = Mathf.Min(Text.CalcSize(m.Name).x, nameR.width);
                Widgets.DrawBoxSolid(new Rect(nameR.x, nameR.yMax - 5f, ulW, 1f), Palette.Accent);
                if (Mouse.IsOver(nameR)) TooltipHandler.TipRegion(nameR, "MDT_OpenModPage".Translate().ToString() + "\n" + m.Url);
                if (Widgets.ButtonInvisible(nameR)) Application.OpenURL(m.Url);
            }

            Text.Anchor = TextAnchor.MiddleRight;
            Text.WordWrap = false;
            GUI.color = tagCol;
            Widgets.Label(new Rect(row.x, row.y + 8f, row.width - 10f, lh), tag);
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = true;

            if (hasReason)
                Palette.LabelFit(new Rect(row.x + 12f, row.y + 8f + lh + 2f, row.width - 18f, lh), reason, Palette.TextDim);

            string tip = m.PackageId.NullOrEmpty() ? null : m.PackageId;
            if (m.Reasons.Count > 1) tip = (tip.NullOrEmpty() ? "" : tip + "\n") + string.Join("\n", m.Reasons);
            if (!tip.NullOrEmpty()) TooltipHandler.TipRegion(row, tip);

            return y + rowH + 4f;
        }

        private float DrawDiagnosis(float w, float y, ErrorDiagnosis d)
        {
            Text.Font = GameFont.Small;
            float lh = Text.LineHeight;
            float innerW = w - 16f;

            string title = d.Title ?? "";
            string desc = d.Explanation ?? "";
            string fix = d.Fix ?? "";
            bool hasUrl = !d.Url.NullOrEmpty();

            float titleH = lh;
            float descH = desc.Length > 0 ? Mathf.Ceil(Text.CalcHeight(desc, innerW)) : 0f;
            string fixLine = fix.Length > 0 ? ("MDT_FixLabel".Translate().ToString() + " " + fix) : "";
            float fixH = fixLine.Length > 0 ? Mathf.Ceil(Text.CalcHeight(fixLine, innerW)) : 0f;
            float urlH = hasUrl ? lh : 0f;

            float blockH = 8f + titleH + (descH > 0 ? 4f + descH : 0f) + (fixH > 0 ? 6f + fixH : 0f) + (urlH > 0 ? 4f + urlH : 0f) + 8f;
            Rect card = new Rect(0f, y, w, blockH);
            Palette.DrawCard(card);
            Palette.StateStrip(card, d.Benign ? Palette.Good : Palette.Accent, 3f);

            float cy = card.y + 8f;
            float cx = card.x + 10f;
            float titleW = d.Ignorable ? innerW - 80f : innerW;
            Text.WordWrap = false;
            GUI.color = Palette.Stat;
            Widgets.Label(new Rect(cx, cy, titleW, titleH), title);
            GUI.color = Color.white;
            Text.WordWrap = true;
            if (d.Ignorable)
            {
                Rect igR = new Rect(card.xMax - 10f - 70f, cy - 2f, 70f, titleH + 4f);
                if (Palette.GrayButton(igR, "MDT_Ignore".Translate(), "MDT_IgnoreTip".Translate()))
                    ModernDevToolsMod.IgnoreIssue(d.Source);
            }
            cy += titleH;

            if (descH > 0)
            {
                cy += 4f;
                GUI.color = Palette.TextDim;
                Widgets.Label(new Rect(cx, cy, innerW, descH), desc);
                GUI.color = Color.white;
                cy += descH;
            }
            if (fixH > 0)
            {
                cy += 6f;
                GUI.color = Palette.Stat;
                Widgets.Label(new Rect(cx, cy, innerW, fixH), fixLine);
                GUI.color = Color.white;
                cy += fixH;
            }
            if (urlH > 0)
            {
                cy += 4f;
                GUI.color = Palette.Accent;
                Text.WordWrap = false;
                Widgets.Label(new Rect(cx, cy, innerW, urlH), d.Url);
                GUI.color = Color.white;
                Text.WordWrap = true;
            }

            return y + blockH + 6f;
        }

        // --- shared bits ---

        private float Section(float w, float y, string label)
        {
            Palette.SectionHeader(new Rect(0f, y, w, 22f), label);
            return y + 22f + 6f;
        }

        private float DrawWrapped(float w, float y, string text, Color color)
        {
            Text.Font = GameFont.Small;
            Text.WordWrap = true;
            float h = Mathf.Ceil(Text.CalcHeight(text, w));
            GUI.color = color;
            Widgets.Label(new Rect(0f, y, w, h), text);
            GUI.color = Color.white;
            return y + h + 2f;
        }

        private void DrawModChangeIndicator(ref float rx, Rect r)
        {
            if (!ModChange.HasChanges) return;
            string lbl = "MDT_ModsChangedBtn".Translate(ModChange.Report.Count);
            Text.Font = GameFont.Small;
            float w = Mathf.Max(60f, Text.CalcSize(lbl).x + 20f);
            Rect btn = new Rect(rx - w, r.y, w, r.height);
            rx = btn.x - 8f;
            Widgets.DrawBoxSolid(btn, Color.Lerp(Palette.BGL, Palette.Warn, 0.28f));
            Palette.DrawBox(btn, Palette.Warn, 1);
            Palette.StateStrip(btn, Palette.Warn, 3f);
            Text.Anchor = TextAnchor.MiddleCenter;
            Text.WordWrap = false;
            GUI.color = Palette.Stat;
            Widgets.Label(btn, lbl);
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = true;
            TooltipHandler.TipRegion(btn, ModChangeTooltip());
            if (Widgets.ButtonInvisible(btn) && !Find.WindowStack.IsOpen<Dialog_ModChanges>())
                Find.WindowStack.Add(new Dialog_ModChanges());
        }

        private static string ModChangeTooltip()
        {
            var sb = new StringBuilder("MDT_ModsChangedTip".Translate());
            sb.AppendLine();
            var rep = ModChange.Report;
            for (int i = 0; i < rep.Count && i < 25; i++) sb.AppendLine("- " + rep[i].Name);
            return sb.ToString();
        }

        private static float BtnW(string label)
        {
            Text.Font = GameFont.Small;
            return Mathf.Max(60f, Text.CalcSize(label).x + 20f);
        }

        private float DrawFilterChip(float x, float y, float h, string label, int count, Color strip, LogMessageType type)
        {
            bool show = LogState.VisibleType(type);
            string text = label + " (" + count + ")";
            Text.Font = GameFont.Small;
            float tw = Text.CalcSize(text).x;
            float wChip = 3f + 8f + tw + 10f;
            Rect chip = new Rect(x, y, wChip, h);

            Color plate = show ? Palette.PanelBG : Palette.BGD;
            if (Mouse.IsOver(chip)) plate = Color.Lerp(plate, Palette.BGL, 0.45f);
            Widgets.DrawBoxSolid(chip, plate);
            Palette.DrawBox(chip, Palette.BGL, 1);
            Palette.StateStrip(chip, show ? strip : Color.Lerp(strip, Palette.BGD, 0.6f), 3f);

            Text.Anchor = TextAnchor.MiddleLeft;
            Text.WordWrap = false;
            GUI.color = show ? Palette.Stat : Palette.TextDim;
            Widgets.Label(new Rect(chip.x + 11f, chip.y, tw + 4f, chip.height), text);
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = true;

            if (Widgets.ButtonInvisible(chip)) SetVisibleType(type, !show);
            return chip.xMax;
        }

        private float DrawStateToggleRightward(ref float rx, float y, float h, string label, bool value, string tip, Action<bool> setter)
        {
            Text.Font = GameFont.Small;
            string state = value ? "MDT_On".Translate() : "MDT_Off".Translate();
            float tw = Text.CalcSize(label).x;
            float sw = Text.CalcSize(state).x;
            float wBtn = 3f + 8f + tw + 8f + sw + 10f;
            Rect r = new Rect(rx - wBtn, y, wBtn, h);
            rx = r.x - 6f;

            Color plate = Palette.BGL;
            if (Mouse.IsOver(r)) plate = Color.Lerp(plate, Palette.Accent, 0.14f);
            Widgets.DrawBoxSolid(r, plate);
            Palette.DrawBox(r, new Color(0f, 0f, 0f, 0.28f), 1);
            Palette.StateStrip(r, value ? Palette.Good : Palette.StripGray, 3f);

            Text.Anchor = TextAnchor.MiddleLeft;
            Text.WordWrap = false;
            GUI.color = Palette.Stat;
            Widgets.Label(new Rect(r.x + 11f, r.y, tw + 4f, r.height), label);
            Text.Anchor = TextAnchor.MiddleRight;
            GUI.color = value ? Palette.Good : Palette.TextDim;
            Widgets.Label(new Rect(r.x, r.y, r.width - 8f, r.height), state);
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = true;

            if (!tip.NullOrEmpty()) TooltipHandler.TipRegion(r, tip);
            if (Widgets.ButtonInvisible(r)) setter(!value);
            return rx;
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

        private static string FirstLine(string text)
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
                var ignored = ModernDevToolsMod.Settings?.ignoredIssues;
                bool hasIgnores = ignored != null && ignored.Count > 0;
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
                    if (hasIgnores && KnownIssueIndex.TextMatchesAnyIgnored(m.text, ignored)) { _hidden++; continue; }
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
