using System;
using System.Collections.Generic;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// Modern, suite-styled replacement for RimWorld's debug window (Dialog_Debug). Full 3-tab parity
    /// (Actions / Output / Settings), reusing the vanilla node graph so mod-added actions appear too.
    /// Spawn pickers render as a searchable icon grid; iconless actions fall back to the dev-action
    /// texture; every mod-supplied call is hardened so a broken action cannot break the menu.
    /// </summary>
    public class Window_ModernDevActions : Window
    {
        private const float Pad = 10f;
        private const float Gap = 8f;
        private const float RowH = 30f;
        private const float BarH = 30f;

        private DebugTabMenuDef _tab;
        private DebugActionNode _tabRoot;
        private DebugActionNode _node;
        private string _search = "";
        private Vector2 _scroll;
        private string _searchKey;
        private List<DebugActionNode> _searchResults;
        private bool _focusSearch;
        private int _openedFrame;   // frame the window opened / switched tabs; focus is deferred past it
        private bool _restorePalette;   // the dev palette was hidden on open and should reappear on close

        public Window_ModernDevActions(DebugTabMenuDef tab)
        {
            doCloseX = false;
            doWindowBackground = false;
            draggable = true;
            resizeable = true;
            preventCameraMotion = false;
            drawShadow = true;
            closeOnAccept = false;
            closeOnCancel = true;   // route Esc to OnCancelKeyPressed (WindowStack ignores it otherwise)
            closeOnClickedOutside = true;
            onlyOneOfTypeAllowed = true;
            layer = WindowLayer.Dialog;
            GoToTab(tab ?? DebugTree.Tabs().FirstOrDefaultSafe());
        }

        protected override float Margin => 0f;

        // The dev palette lives on WindowLayer.SubSuper, so it draws OVER this Dialog-layer window and
        // would cover its content. Hide the palette while we're open (without flipping the devPalette
        // pref) and restore it on close, so the two dev tools never overlap.
        public override void PreOpen()
        {
            base.PreOpen();
            try
            {
                var ws = Find.WindowStack;
                var palette = ws?.WindowOfType<Window_ModernDevPalette>();
                if (palette != null)
                {
                    palette.suppressClosePref = true;
                    ws.TryRemove(palette, false);
                    _restorePalette = true;
                }
            }
            catch (Exception e) { Log.WarningOnce("[Modern Dev Tools] palette hide failed: " + e.Message, 0x2E19C30); }
        }

        public override void PostClose()
        {
            base.PostClose();
            try
            {
                if (_restorePalette && DebugSettings.devPalette)
                {
                    var ws = Find.WindowStack;
                    if (ws != null && !ws.IsOpen<Window_ModernDevPalette>()) ws.Add(new Window_ModernDevPalette());
                }
            }
            catch (Exception e) { Log.WarningOnce("[Modern Dev Tools] palette restore failed: " + e.Message, 0x2E19C31); }
        }

        public override Vector2 InitialSize =>
            new Vector2(Mathf.Min(UI.screenWidth * 0.6f, 1080f), Mathf.Min(UI.screenHeight * 0.72f, 820f));

        public static void OpenOrSwitch(DebugTabMenuDef tab)
        {
            var ws = Find.WindowStack;
            if (ws == null) return;
            var existing = ws.WindowOfType<Window_ModernDevActions>();
            if (existing != null) existing.GoToTab(tab);
            else ws.Add(new Window_ModernDevActions(tab));
        }

        /// <summary>Open (or reuse) the dev actions window and navigate straight to <paramref name="node"/>.
        /// Used whenever a submenu node is entered outside the window - e.g. a dev palette row that turns
        /// out to be a category, which vanilla would answer by popping its own Dialog_Debug.</summary>
        public static void OpenAt(DebugActionNode node)
        {
            if (node == null) return;
            var ws = Find.WindowStack;
            if (ws == null) return;
            DebugTabMenuDef tab = TabForNode(node);
            var existing = ws.WindowOfType<Window_ModernDevActions>();
            if (existing == null)
            {
                existing = new Window_ModernDevActions(tab);
                ws.Add(existing);
            }
            else if (existing._tab != tab) existing.GoToTab(tab);
            existing.Navigate(node);
        }

        /// <summary>Which tab a node belongs to: walk up to the top-most non-root ancestor (that node is
        /// the tab root registered in Dialog_Debug.roots) and match it against the tab list.</summary>
        private static DebugTabMenuDef TabForNode(DebugActionNode node)
        {
            try
            {
                DebugActionNode top = node;
                while (top.parent != null && !top.parent.IsRoot) top = top.parent;
                List<DebugTabMenuDef> tabs = DebugTree.Tabs();
                for (int i = 0; i < tabs.Count; i++)
                    if (DebugTree.RootOf(tabs[i]) == top) return tabs[i];
                return tabs.FirstOrDefaultSafe();
            }
            catch { return DebugTree.Tabs().FirstOrDefaultSafe(); }
        }

        private void GoToTab(DebugTabMenuDef tab)
        {
            _tab = tab;
            _tabRoot = DebugTree.RootOf(tab);
            _node = _tabRoot;
            _search = "";
            _scroll = Vector2.zero;
            _searchKey = null;
            _searchResults = null;
            _focusSearch = true;   // land the caret in the search box on open / tab switch
            _openedFrame = Time.frameCount;   // ...but NOT this frame: the '/' hotkey that opened us would leak in
        }

        private void Navigate(DebugActionNode node)
        {
            _node = node;
            _search = "";
            _scroll = Vector2.zero;
        }

        private bool AtRoot => _node == _tabRoot;

        public override void OnCancelKeyPressed()
        {
            // Esc goes up one level inside a submenu; at the top level it closes the window.
            if (!AtRoot && _node?.parent != null) { Navigate(_node.parent); Event.current.Use(); }
            else { Close(); Event.current.Use(); }
        }

        public override void DoWindowContents(Rect inRect)
        {
            try { DrawAll(inRect); }
            catch (Exception e) { Log.ErrorOnce("[Modern Dev Tools] dev window draw failed: " + e, 0x2E19C10); }
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

            // Header (title + close X on the right, drawn by the base window)
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleLeft;
            Text.WordWrap = false;
            GUI.color = Palette.Stat;
            Widgets.Label(new Rect(content.x, y, content.width - 34f, BarH), "MDT_DevToolsTitle".Translate());
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = true;
            if (Palette.CloseX(new Rect(content.xMax - 26f, content.y + 2f, 22f, 22f))) Close();
            y += BarH + Gap;

            // Tab strip
            DrawTabs(new Rect(content.x, y, content.width, BarH));
            y += BarH + Gap;

            // Search
            DrawSearch(new Rect(content.x, y, content.width, BarH));
            y += BarH + Gap;

            // Breadcrumb + back
            if (!AtRoot)
            {
                Rect bar = new Rect(content.x, y, content.width, BarH);
                float backW = 76f;
                if (Palette.GrayButton(new Rect(bar.x, bar.y, backW, bar.height), "MDT_Back".Translate())
                    && _node?.parent != null) Navigate(_node.parent);
                Palette.LabelFit(new Rect(bar.x + backW + Gap, bar.y, bar.width - backW - Gap, bar.height),
                    DebugTree.PathOf(_node), Palette.TextDim);
                y += BarH + Gap;
            }

            Rect body = new Rect(content.x, y, content.width, content.yMax - y);
            if (_tabRoot == null)
            {
                DrawCenter(body, "MDT_DevMenuError".Translate(), Palette.Warn);
                return;
            }

            List<DebugActionNode> children = string.IsNullOrEmpty(_search)
                ? DebugTree.Children(_node)
                : SearchResults();
            if (children.Count == 0) { DrawCenter(body, "MDT_EmptyNode".Translate(), Palette.TextDim); return; }

            if (DebugTree.LooksLikeThingGrid(children)) DrawGrid(body, children);
            else DrawList(body, children);
        }

        private void DrawTabs(Rect r)
        {
            var tabs = DebugTree.Tabs();
            if (tabs.Count == 0) return;
            float tw = Mathf.Min(r.width / tabs.Count, 170f);
            for (int i = 0; i < tabs.Count; i++)
            {
                DebugTabMenuDef t = tabs[i];
                Rect tr = new Rect(r.x + i * tw, r.y, tw - 4f, r.height);
                bool active = t == _tab;
                Widgets.DrawBoxSolid(tr, active ? Palette.PanelBG : Palette.BGD);
                Palette.DrawBox(tr, Palette.BGL, 1);
                if (active) Palette.StateStrip(tr, Palette.Accent, 3f);
                else if (Mouse.IsOver(tr)) Widgets.DrawBoxSolid(tr, new Color(1f, 1f, 1f, 0.05f));

                Text.Anchor = TextAnchor.MiddleCenter;
                Text.WordWrap = false;
                GUI.color = active ? Palette.Stat : Palette.TextDim;
                Widgets.Label(tr, t.LabelCap);
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                Text.WordWrap = true;

                if (!active && Widgets.ButtonInvisible(tr)) GoToTab(t);
            }
        }

        private void DrawSearch(Rect r)
        {
            string edited = Palette.SearchField(r, "MDT_DevFilter", _search ?? "", "MDT_DevSearchPlaceholder".Translate());
            if (edited != _search) _search = edited;

            // Focus the field the first frame STRICTLY AFTER opening / switching tabs. Deferring past the
            // open frame is what stops the '/' dev hotkey (which opened this window) from being typed into
            // the freshly focused search box - which otherwise searched "/", forcing a full-tree walk.
            if (_focusSearch && Time.frameCount > _openedFrame)
            {
                GUI.FocusControl("MDT_DevFilter");
                _focusSearch = false;
            }
        }

        // --- list view ---

        private void DrawList(Rect rect, List<DebugActionNode> nodes)
        {
            Palette.DrawWell(rect);
            Rect inner = rect.ContractedBy(1f);

            // Precompute total height including category headers.
            float headerH = 24f;
            string lastCat = "\0";
            float total = 0f;
            for (int i = 0; i < nodes.Count; i++)
            {
                string cat = nodes[i].category ?? "";
                if (cat != lastCat) { total += headerH; lastCat = cat; }
                total += RowH;
            }

            Rect view = new Rect(0f, 0f, inner.width - 16f, Mathf.Max(total, inner.height));
            Palette.BeginScroll(inner, ref _scroll, view);
            try
            {
                float y = 0f;
                lastCat = "\0";
                bool alt = false;
                for (int i = 0; i < nodes.Count; i++)
                {
                    DebugActionNode node = nodes[i];
                    string cat = node.category ?? "";
                    if (cat != lastCat)
                    {
                        if (!cat.NullOrEmpty())
                            Palette.SectionHeader(new Rect(0f, y + 2f, view.width, headerH - 3f), cat);
                        y += headerH;
                        lastCat = cat;
                        alt = false;
                    }
                    DrawRow(new Rect(0f, y, view.width, RowH), node, alt);
                    alt = !alt;
                    y += RowH;
                }
            }
            finally { Palette.EndScroll(); }
        }

        private void DrawRow(Rect row, DebugActionNode node, bool alt)
        {
            bool broken = DebugTree.IsBroken(node);
            bool checkbox = DebugTree.IsCheckbox(node);
            bool category = !checkbox && DebugTree.IsCategory(node);
            bool active = DebugTree.IsActive(node);
            bool pinned = DebugTree.IsPinned(node);

            Color plate = alt ? Palette.RowAlt : Palette.PanelBG;
            bool over = Mouse.IsOver(row);
            if (over) plate = Color.Lerp(plate, Palette.BGL, 0.45f);
            Widgets.DrawBoxSolid(row, plate);
            if (broken) Palette.StateStrip(row, Palette.Bad, 3f);

            float pinW = 24f;
            Rect pinR = new Rect(row.xMax - pinW - 4f, row.y + (RowH - 18f) / 2f, 18f, 18f);
            Rect clickR = new Rect(row.x, row.y, row.width - pinW - 8f, row.height);

            float x = row.x + 8f;
            Rect iconR = new Rect(x, row.y + (RowH - 22f) / 2f, 22f, 22f);
            if (checkbox) { DrawCheck(iconR, DebugTree.GetCheck(node)); x = iconR.xMax + 8f; }
            else if (category) { x = row.x + 10f; }             // folders: chevron only, no fallback icon
            else { DrawIcon(iconR, node); x = iconR.xMax + 8f; }

            Color col = broken ? Palette.Bad : (active ? Palette.Stat : Palette.TextDim);
            float labelRight = category ? 20f : 0f;
            Palette.LabelFit(new Rect(x, row.y, clickR.xMax - x - labelRight - 4f, row.height), DebugTree.Label(node), col);

            if (category) Palette.DrawChevron(new Rect(clickR.xMax - 15f, row.y + (RowH - 14f) / 2f, 9f, 14f), over);

            // thumbtack pin: gray when unpinned, white when pinned
            Palette.DrawPin(pinR.ContractedBy(1f), pinned);
            TooltipHandler.TipRegion(pinR, "MDT_PinTip".Translate());
            if (Widgets.ButtonInvisible(pinR)) DebugTree.TogglePin(node);

            if (broken) TooltipHandler.TipRegion(clickR, "MDT_BrokenTip".Translate());

            if (Widgets.ButtonInvisible(clickR))
            {
                if (checkbox) DebugTree.SetCheck(node, !DebugTree.GetCheck(node));
                else if (category) Navigate(node);
                else DebugTree.RunLeaf(node, this);
            }
        }

        // --- thing spawn grid ---

        private void DrawGrid(Rect rect, List<DebugActionNode> nodes)
        {
            // hint line
            GUI.color = Palette.TextDim;
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleLeft;
            Text.WordWrap = false;
            Widgets.Label(new Rect(rect.x + 2f, rect.y, rect.width, 22f), "MDT_SpawnHint".Translate());
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = true;
            Rect gridRect = new Rect(rect.x, rect.y + 26f, rect.width, rect.height - 26f);

            Palette.DrawWell(gridRect);
            Rect inner = gridRect.ContractedBy(4f);

            const float cellW = 116f, cellH = 92f;
            int cols = Mathf.Max(1, Mathf.FloorToInt((inner.width - 16f) / cellW));
            int rows = Mathf.CeilToInt(nodes.Count / (float)cols);
            Rect view = new Rect(0f, 0f, inner.width - 16f, Mathf.Max(rows * cellH, inner.height));
            Palette.BeginScroll(inner, ref _scroll, view);
            try
            {
                float actualCellW = view.width / cols;
                int firstRow = Mathf.Max(0, Mathf.FloorToInt(_scroll.y / cellH) - 1);
                int lastRow = Mathf.Min(rows, Mathf.CeilToInt((_scroll.y + inner.height) / cellH) + 1);
                for (int rIdx = firstRow; rIdx < lastRow; rIdx++)
                    for (int c = 0; c < cols; c++)
                    {
                        int idx = rIdx * cols + c;
                        if (idx >= nodes.Count) break;
                        DrawCell(new Rect(c * actualCellW, rIdx * cellH, actualCellW, cellH).ContractedBy(3f), nodes[idx]);
                    }
            }
            finally { Palette.EndScroll(); }
        }

        private void DrawCell(Rect cell, DebugActionNode node)
        {
            bool over = Mouse.IsOver(cell);
            bool broken = DebugTree.IsBroken(node);
            Widgets.DrawBoxSolid(cell, over ? Color.Lerp(Palette.PanelBG, Palette.BGL, 0.5f) : Palette.PanelBG);
            Palette.DrawBox(cell, broken ? Palette.Bad : Palette.BGL, 1);

            ThingDef def = DebugTree.ThingForNode(node);
            Rect iconR = new Rect(cell.center.x - 24f, cell.y + 6f, 48f, 48f);
            DrawThingIcon(iconR, def);

            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperCenter;
            Text.WordWrap = false;
            GUI.color = broken ? Palette.Bad : Palette.Stat;
            string label = def != null ? def.LabelCap.ToString() : DebugTree.Label(node);
            Widgets.Label(new Rect(cell.x + 2f, cell.y + 56f, cell.width - 4f, 30f), label.Truncate(cell.width - 8f));
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = true;

            TooltipHandler.TipRegion(cell, DebugTree.Label(node));
            if (Widgets.ButtonInvisible(cell)) DebugTree.RunLeaf(node, this);
        }

        // --- shared drawing ---

        private static void DrawIcon(Rect r, DebugActionNode node)
        {
            ThingDef def = DebugTree.ThingForNode(node);
            if (def != null) { DrawThingIcon(r, def); return; }
            // fallback: the vanilla dev-action texture
            Texture t = TexButton.OpenDebugActionsMenu;
            if (t != null)
            {
                GUI.color = new Color(0.75f, 0.78f, 0.82f);
                GUI.DrawTexture(r, t, ScaleMode.ScaleToFit);
                GUI.color = Color.white;
            }
        }

        private static void DrawThingIcon(Rect r, ThingDef def)
        {
            if (def == null)
            {
                Texture t = TexButton.OpenDebugActionsMenu;
                if (t != null) { GUI.color = new Color(0.75f, 0.78f, 0.82f); GUI.DrawTexture(r, t, ScaleMode.ScaleToFit); GUI.color = Color.white; }
                return;
            }
            try
            {
                Texture2D tex = Widgets.GetIconFor(def);
                if (tex != null)
                {
                    GUI.color = def.uiIconColor;
                    GUI.DrawTexture(r, tex, ScaleMode.ScaleToFit);
                    GUI.color = Color.white;
                }
            }
            catch { }
        }

        private static void DrawCheck(Rect r, bool on)
        {
            Widgets.DrawBoxSolid(r, on ? Palette.Accent : Palette.BGD);
            Palette.DrawBox(r, on ? Palette.Accent : Palette.BGL, 1);
            if (on)
            {
                Vector2 a = new Vector2(r.x + 5f, r.y + r.height * 0.55f);
                Vector2 b = new Vector2(r.x + r.width * 0.42f, r.yMax - 5f);
                Vector2 c = new Vector2(r.xMax - 4f, r.y + 4f);
                Widgets.DrawLine(a, b, Palette.BGD, 2f);
                Widgets.DrawLine(b, c, Palette.BGD, 2f);
            }
        }

        private static void DrawChevron(Rect r, bool over)
        {
            Color c = over ? Palette.Stat : Palette.TextDim;
            Widgets.DrawLine(new Vector2(r.x, r.y), new Vector2(r.xMax, r.center.y), c, 1.5f);
            Widgets.DrawLine(new Vector2(r.xMax, r.center.y), new Vector2(r.x, r.yMax), c, 1.5f);
        }

        private static void DrawCenter(Rect r, string text, Color color)
        {
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleCenter;
            Text.WordWrap = true;
            GUI.color = color;
            Widgets.Label(r, text);
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
        }

        /// <summary>Filter the search index for the current context: the whole tab at the root (a cheap flat
        /// index of every discrete action, mod actions included, with the giant grids left collapsed), or the
        /// current category's items once you drill into one (e.g. a spawn grid). The index is built once per
        /// context and cached, so each keystroke is just a substring filter.</summary>
        private List<DebugActionNode> SearchResults()
        {
            bool atRoot = AtRoot;
            DebugActionNode root = atRoot ? _tabRoot : _node;
            string ctx = (_tab?.defName ?? "") + "\u0001" + (atRoot ? "*root*" : DebugTree.PathOf(_node));
            string key = ctx + "\u0001" + _search;
            if (key == _searchKey && _searchResults != null) return _searchResults;
            _searchKey = key;
            _searchResults = ActionSearchIndex.Filter(ctx, root, _search);
            return _searchResults;
        }
    }

    internal static class DevListExt
    {
        public static DebugTabMenuDef FirstOrDefaultSafe(this List<DebugTabMenuDef> list) =>
            list != null && list.Count > 0 ? list[0] : null;
    }
}
