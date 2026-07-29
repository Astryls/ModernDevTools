using System;
using System.Collections.Generic;
using LudeonTK;
using UnityEngine;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// Suite-styled replacement for the vanilla Dev palette (Dialog_DevPalette): a small floating list
    /// of the actions pinned from the dev tools window. Click a row to run it (checkbox rows toggle),
    /// click the thumbtack to unpin. Sizes to its content and remembers its position.
    /// </summary>
    public class Window_ModernDevPalette : Window
    {
        // Rows are sized from the ACTUAL line height of each font (Text.LineHeightOf) plus padding -
        // hardcoding a height clips glyph tops, because IMGUI labels clip to their rect.
        private const float VPad = 2f;         // breathing room above/below the text inside a row
        private const float LineOverlap = 2f;  // pull the leaf line up into the category line's leading
        private const float RowGap = 2f;       // gap between row plates
        private const float DragThreshold = 3f;// px of movement before a press becomes a reorder drag
        private const float HeaderH = 26f;
        private const float PinW = 20f;
        private const float PinIcon = 16f;
        private const float PadL = 7f;
        private const float PadR = 6f;     // gap between the longest label and the thumbtack
        private const float MinW = 220f;
        private const float MaxW = 640f;
        private int _lastCount = -1;
        private float _lastWidth = -1f;

        /// <summary>Set before a programmatic remove (e.g. while the dev actions window hides us) so
        /// PostClose doesn't turn the devPalette pref off - we want to restore the palette afterwards.</summary>
        public bool suppressClosePref;

        public Window_ModernDevPalette()
        {
            doWindowBackground = false;
            doCloseX = false;
            draggable = true;
            focusWhenOpened = false;
            drawShadow = true;
            closeOnAccept = false;
            closeOnCancel = false;
            preventCameraMotion = false;
            onlyDrawInDevMode = true;
            onlyOneOfTypeAllowed = true;
            // SubSuper (not Super): tooltips are drawn as ImmediateWindows on WindowLayer.Super
            // (ActiveTip.DrawTooltip), so a Super palette would z-fight and cover its own row
            // tooltips. SubSuper keeps the palette above the other tool windows (log / knowledge
            // base / dev actions are all Dialog) while letting Super tooltips render on top.
            layer = WindowLayer.SubSuper;
        }

        protected override float Margin => 0f;

        /// <summary>Resolve the pinned paths to live nodes. <paramref name="prefIndicesOut"/> (when given)
        /// records which Prefs.DebugActionsPalette entry each row came from - pins that no longer resolve
        /// are skipped, so row index != pref index.</summary>
        private static List<DebugActionNode> ResolveNodes(List<int> prefIndicesOut = null)
        {
            var list = new List<DebugActionNode>();
            prefIndicesOut?.Clear();
            try
            {
                var palette = Prefs.DebugActionsPalette;
                for (int i = 0; i < palette.Count; i++)
                {
                    DebugActionNode n = Dialog_Debug.GetNode(palette[i]);
                    if (n == null) continue;
                    list.Add(n);
                    prefIndicesOut?.Add(i);
                }
            }
            catch (Exception e) { Log.WarningOnce("[Modern Dev Tools] palette resolve failed: " + e.Message, 0x2E19C20); }
            return list;
        }

        // --- browser-tab style drag state (ported from Modern Pawn Tabs' DragState idiom) ---
        private readonly List<Rect> _rowRects = new List<Rect>();
        private readonly List<int> _prefIdx = new List<int>();   // row index -> index in Prefs.DebugActionsPalette
        private int _dragRow = -1;      // row the press started on (-1 = no candidate)
        private bool _dragging;         // press has passed the movement threshold
        private int _dropRow = -1;      // insertion slot under the cursor, 0..rowCount
        private Vector2 _dragStart;
        private float _dragOffsetY;     // grab point inside the row, so the ghost tracks naturally
        private bool _swallowClick;     // a drag just ended: don't let this MouseUp fire the action

        private static float CatH => Text.LineHeightOf(GameFont.Tiny);
        private static float LeafH => Text.LineHeightOf(GameFont.Small);
        private static float FlatRowH => LeafH + VPad * 2f + RowGap;
        private static float NestRowH => CatH + LeafH - LineOverlap + VPad * 2f + RowGap;

        private static float RowHeightFor(DebugActionNode node) =>
            DebugTree.PrettyPrefix(node) != null ? NestRowH : FlatRowH;

        /// <summary>Width needed to show every row in full without truncation (vanilla sizes to its
        /// content too). Measures the leaf line at Small and the category line at Tiny, since the two
        /// lines use different fonts. Clamped so a pathological label can't make a screen-wide window.</summary>
        private static float DesiredWidth(List<DebugActionNode> nodes)
        {
            GameFont prevFont = Text.Font;
            bool prevWrap = Text.WordWrap;
            Text.WordWrap = false;
            float w;
            try
            {
                Text.Font = GameFont.Small;
                float titleW = Text.CalcSize("MDT_DevPaletteTitle".Translate()).x + 30f;
                float labelW = 0f;
                for (int i = 0; i < nodes.Count; i++)
                {
                    Text.Font = GameFont.Small;
                    float lw = Text.CalcSize(DebugTree.PrettyLeaf(nodes[i])).x + 4f;
                    if (DebugTree.IsCheckbox(nodes[i])) lw += 20f;
                    string cat = DebugTree.PrettyPrefix(nodes[i]);
                    if (cat != null)
                    {
                        Text.Font = GameFont.Tiny;
                        lw = Mathf.Max(lw, Text.CalcSize(cat).x + 4f);
                    }
                    if (lw > labelW) labelW = lw;
                }
                w = Mathf.Max(titleW, labelW + PadL + PadR + PinW + 4f) + 16f + 2f;
            }
            catch { w = 300f; }
            finally { Text.Font = prevFont; Text.WordWrap = prevWrap; }
            return Mathf.Clamp(w, MinW, Mathf.Min(MaxW, UI.screenWidth));
        }

        protected override void SetInitialSizeAndPosition()
        {
            List<DebugActionNode> nodes = ResolveNodes();
            _lastCount = nodes.Count;
            float w = nodes.Count == 0 ? 300f : DesiredWidth(nodes);
            _lastWidth = w;
            float rowsH = 0f;
            if (nodes.Count == 0) rowsH = 44f;
            else foreach (DebugActionNode nd in nodes) rowsH += RowHeightFor(nd);
            float h = HeaderH + 8f + rowsH + 8f;
            w = Mathf.Min(w, UI.screenWidth);
            h = Mathf.Min(h, UI.screenHeight);
            Vector2 pos = Prefs.DevPalettePosition;
            float x = Mathf.Clamp(pos.x, 0f, UI.screenWidth - w);
            float y = Mathf.Clamp(pos.y, 0f, UI.screenHeight - h);
            windowRect = new Rect(x, y, w, h).Rounded();
        }

        public override void DoWindowContents(Rect inRect)
        {
            try { Draw(inRect); }
            catch (Exception e) { Log.ErrorOnce("[Modern Dev Tools] dev palette draw failed: " + e, 0x2E19C21); }
            finally
            {
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.UpperLeft;
                Text.WordWrap = true;
            }
        }

        private void Draw(Rect inRect)
        {
            List<DebugActionNode> nodes = ResolveNodes(_prefIdx);
            // Labels come from live getters, so re-fit when the count OR the widest label changes.
            if (nodes.Count != _lastCount ||
                (nodes.Count > 0 && Event.current.type == EventType.Repaint &&
                 Mathf.Abs(DesiredWidth(nodes) - _lastWidth) > 1f))
                SetInitialSizeAndPosition();

            Widgets.DrawBoxSolid(inRect, Palette.BG);
            Palette.DrawBox(inRect, Palette.BGL, 1);
            Rect content = inRect.ContractedBy(8f);

            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleLeft;
            Text.WordWrap = false;
            GUI.color = Palette.Stat;
            Widgets.Label(new Rect(content.x, content.y, content.width - 24f, HeaderH), "MDT_DevPaletteTitle".Translate());
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = true;
            if (Palette.CloseX(new Rect(content.xMax - 22f, content.y + 1f, 20f, 20f))) Close();

            float y = content.y + HeaderH + 4f;
            if (nodes.Count == 0)
            {
                GUI.color = Palette.TextDim;
                Text.WordWrap = true;
                Widgets.Label(new Rect(content.x, y, content.width, content.yMax - y), "MDT_DevPaletteEmpty".Translate());
                GUI.color = Color.white;
            }
            else
            {
                // Lay the rows out first: the drag handler needs every rect before anything draws.
                _rowRects.Clear();
                for (int i = 0; i < nodes.Count; i++)
                {
                    float rh = RowHeightFor(nodes[i]);
                    _rowRects.Add(new Rect(content.x, y, content.width, rh - RowGap));
                    y += rh;
                }

                HandleDragInput();
                for (int i = 0; i < nodes.Count; i++) DrawRow(_rowRects[i], nodes[i], i);
                if (_dragging) DrawDragVisuals(nodes);
            }

            // persist dragged position
            if (!Mathf.Approximately(windowRect.x, Prefs.DevPalettePosition.x) || !Mathf.Approximately(windowRect.y, Prefs.DevPalettePosition.y))
                Prefs.DevPalettePosition = new Vector2(windowRect.x, windowRect.y);
        }

        private void DrawRow(Rect row, DebugActionNode node, int index)
        {
            bool checkbox = DebugTree.IsCheckbox(node);
            bool active = DebugTree.IsActive(node);
            bool broken = DebugTree.IsBroken(node);
            bool over = Mouse.IsOver(row);

            bool isSource = _dragging && index == _dragRow;

            Widgets.DrawBoxSolid(row, (index & 1) == 1 ? Palette.RowAlt : Palette.PanelBG);
            if (isSource)
            {
                // The row being dragged leaves a hollow slot behind (browser-tab feel).
                Widgets.DrawBoxSolid(row, new Color(0f, 0f, 0f, 0.35f));
                Palette.DrawBox(row, Palette.BGL, 1);
                return;
            }
            if (over && !_dragging) Widgets.DrawBoxSolid(row, new Color(1f, 1f, 1f, 0.05f));
            if (broken) Palette.StateStrip(row, Palette.Bad, 3f);

            Rect pinR = new Rect(row.xMax - PinW, row.y + (row.height - PinIcon) / 2f, PinIcon, PinIcon);
            Rect clickR = new Rect(row.x, row.y, row.width - PinW - 4f, row.height);

            DrawRowText(row, node, clickR.xMax, checkbox, active, broken, interactive: true);
            TooltipHandler.TipRegion(clickR, DebugTree.PathOf(node));

            // thumbtack: always pinned here, so white; click to unpin
            Palette.DrawPin(pinR.ContractedBy(1f), true);
            TooltipHandler.TipRegion(pinR, "MDT_Unpin".Translate());
            if (Widgets.ButtonInvisible(pinR)) { DebugTree.TogglePin(node); SetInitialSizeAndPosition(); }

            // Always call the button (stable IMGUI control count), but swallow the result while a drag
            // is live or just ended - otherwise dropping a row would also fire its action.
            bool clicked = Widgets.ButtonInvisible(clickR);
            if (clicked && !_dragging && !_swallowClick)
            {
                if (checkbox) DebugTree.SetCheck(node, !DebugTree.GetCheck(node));
                else DebugTree.RunLeaf(node, null); // keep the palette open (like vanilla)
            }
        }

        /// <summary>The two text lines of a row (shared by the real row and the drag ghost). The category
        /// chain is Tiny + italic + dim; the action label is Small in normal white below it. Each line gets
        /// a rect exactly one line-height tall - IMGUI clips labels to their rect, so undersized rects
        /// slice the glyph tops.</summary>
        private static void DrawRowText(Rect row, DebugActionNode node, float textRight, bool checkbox,
                                        bool active, bool broken, bool interactive)
        {
            string cat = DebugTree.PrettyPrefix(node);
            float x = row.x + PadL;
            float textW = textRight - x - PadR;
            float leafY = row.y + VPad;
            if (cat != null)
            {
                LabelCategory(new Rect(x, leafY, textW, CatH), cat);
                leafY += CatH - LineOverlap;
            }

            if (checkbox)
            {
                Rect ck = new Rect(x, leafY + (LeafH - 14f) / 2f, 14f, 14f);
                DrawCheck(ck, DebugTree.GetCheck(node));
                x = ck.xMax + 5f;
                textW = textRight - x - PadR;
            }

            Color col = broken ? Palette.Bad : (active || !interactive ? Palette.Stat : Palette.TextDim);
            Palette.LabelFit(new Rect(x, leafY, textW, LeafH), DebugTree.PrettyLeaf(node), col);
        }

        // ---- drag to reorder (browser-tab style: ghost follows the cursor, slot opens, insert line) ----

        /// <summary>Press anywhere on a row and move &gt;3px to start a reorder; a plain click still runs
        /// the action. Runs before the rows draw so it sees the raw MouseDown before the row buttons
        /// consume it, and it never calls Event.Use() on MouseUp (that would strand GUI hotControl).</summary>
        private void HandleDragInput()
        {
            Event e = Event.current;
            Vector2 m = e.mousePosition;

            if (e.type == EventType.MouseDown && e.button == 0)
            {
                _swallowClick = false;
                for (int i = 0; i < _rowRects.Count; i++)
                {
                    Rect r = _rowRects[i];
                    if (!r.Contains(m)) continue;
                    // the thumbtack is its own button - never start a drag from it
                    if (new Rect(r.xMax - PinW - 2f, r.y, PinW + 2f, r.height).Contains(m)) break;
                    _dragRow = i;
                    _dropRow = i;
                    _dragStart = m;
                    _dragOffsetY = m.y - r.y;
                    break;
                }
            }
            else if (e.rawType == EventType.MouseUp && _dragRow >= 0)
            {
                if (_dragging) { ApplyReorder(_dragRow, _dropRow); _swallowClick = true; }
                ClearDrag();
            }
            else if (_dragRow >= 0 && e.type == EventType.Repaint)
            {
                if (!_dragging && (m - _dragStart).magnitude > DragThreshold) _dragging = true;
                if (_dragging) _dropRow = InsertRowFor(m.y);
            }
        }

        private void ClearDrag()
        {
            _dragRow = -1;
            _dropRow = -1;
            _dragging = false;
        }

        /// <summary>Which slot (0..rowCount) the cursor is hovering, by row centers.</summary>
        private int InsertRowFor(float mouseY)
        {
            for (int i = 0; i < _rowRects.Count; i++)
                if (mouseY < _rowRects[i].center.y) return i;
            return _rowRects.Count;
        }

        /// <summary>Commit a drop: move the pref entry for <paramref name="fromRow"/> into slot
        /// <paramref name="toRow"/>. Row indices are mapped through _prefIdx because unresolvable pins
        /// are skipped when building the row list.</summary>
        private void ApplyReorder(int fromRow, int toRow)
        {
            try
            {
                List<string> pref = Prefs.DebugActionsPalette;
                if (pref == null || fromRow < 0 || fromRow >= _prefIdx.Count) return;
                int from = _prefIdx[fromRow];
                int to = toRow >= _prefIdx.Count ? pref.Count : _prefIdx[toRow];
                if (from < 0 || from >= pref.Count || to == from || to == from + 1) return;
                string item = pref[from];
                pref.Insert(Mathf.Clamp(to, 0, pref.Count), item);
                pref.RemoveAt(from < to ? from : from + 1);
                Prefs.Save();
                SetInitialSizeAndPosition();
            }
            catch (Exception e) { Log.WarningOnce("[Modern Dev Tools] palette reorder failed: " + e.Message, 0x2E19C22); }
        }

        /// <summary>Drop indicator + the ghost row riding the cursor. Drawn after every row so it floats
        /// above them.</summary>
        private void DrawDragVisuals(List<DebugActionNode> nodes)
        {
            if (_dragRow < 0 || _dragRow >= _rowRects.Count || _rowRects.Count == 0) return;
            Rect src = _rowRects[_dragRow];

            float lineY;
            if (_dropRow <= 0) lineY = _rowRects[0].y - RowGap * 0.5f;
            else if (_dropRow >= _rowRects.Count) lineY = _rowRects[_rowRects.Count - 1].yMax + RowGap * 0.5f;
            else lineY = (_rowRects[_dropRow - 1].yMax + _rowRects[_dropRow].y) * 0.5f;

            Color accent = Palette.Accent;
            Widgets.DrawBoxSolid(new Rect(src.x, lineY - 3f, src.width, 6f), new Color(accent.r, accent.g, accent.b, 0.20f));
            Widgets.DrawBoxSolid(new Rect(src.x, lineY - 1f, src.width, 2f), accent);

            Rect ghost = new Rect(src.x, Event.current.mousePosition.y - _dragOffsetY, src.width, src.height);
            Widgets.DrawBoxSolid(new Rect(ghost.x + 3f, ghost.y + 3f, ghost.width, ghost.height), new Color(0f, 0f, 0f, 0.35f));
            Widgets.DrawBoxSolid(ghost, Palette.RowAlt);
            Palette.DrawBox(ghost, accent, 1);
            DebugActionNode node = nodes[_dragRow];
            DrawRowText(ghost, node, ghost.xMax - PinW, DebugTree.IsCheckbox(node),
                        DebugTree.IsActive(node), DebugTree.IsBroken(node), interactive: false);
        }

        /// <summary>The category line: Tiny + rich-text italics + dim gray - the one place the suite uses
        /// GameFont.Tiny on purpose, so it reads as subordinate to the action label. Never scale text with
        /// GUI.matrix inside a Window: the matrix is applied outside the window's GUI clip, so the label
        /// lands in screen space (it renders out on the map instead of on the row).</summary>
        private static void LabelCategory(Rect r, string text)
        {
            GameFont prevFont = Text.Font;
            bool prevWrap = Text.WordWrap;
            try
            {
                Text.Font = GameFont.Tiny;
                Text.WordWrap = false;
                Text.Anchor = TextAnchor.UpperLeft;
                string draw = text ?? "";
                if (Text.CalcSize(draw).x > r.width) draw = draw.Truncate(r.width);
                GUI.color = Palette.TextDim;
                Widgets.Label(r, "<i>" + draw + "</i>");
            }
            catch { /* never let a label kill the palette */ }
            finally
            {
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                Text.WordWrap = prevWrap;
                Text.Font = prevFont;
            }
        }

        private static void DrawCheck(Rect r, bool on)
        {
            Widgets.DrawBoxSolid(r, on ? Palette.Accent : Palette.BGD);
            Palette.DrawBox(r, on ? Palette.Accent : Palette.BGL, 1);
            if (on)
            {
                Widgets.DrawLine(new Vector2(r.x + 4f, r.y + r.height * 0.55f), new Vector2(r.x + r.width * 0.42f, r.yMax - 4f), Palette.BGD, 2f);
                Widgets.DrawLine(new Vector2(r.x + r.width * 0.42f, r.yMax - 4f), new Vector2(r.xMax - 3f, r.y + 3f), Palette.BGD, 2f);
            }
        }

        public override void PostClose()
        {
            base.PostClose();
            if (!suppressClosePref) DebugSettings.devPalette = false;
        }
    }
}
