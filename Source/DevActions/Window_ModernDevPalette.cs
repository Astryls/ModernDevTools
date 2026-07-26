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
        private const float RowH = 26f;
        private const float HeaderH = 26f;
        private int _lastCount = -1;

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
            layer = WindowLayer.Super;
        }

        protected override float Margin => 0f;

        private static List<DebugActionNode> ResolveNodes()
        {
            var list = new List<DebugActionNode>();
            try
            {
                var palette = Prefs.DebugActionsPalette;
                for (int i = 0; i < palette.Count; i++)
                {
                    DebugActionNode n = Dialog_Debug.GetNode(palette[i]);
                    if (n != null) list.Add(n);
                }
            }
            catch (Exception e) { Log.WarningOnce("[Modern Dev Tools] palette resolve failed: " + e.Message, 0x2E19C20); }
            return list;
        }

        protected override void SetInitialSizeAndPosition()
        {
            int n = ResolveNodes().Count;
            _lastCount = n;
            float w = 300f;
            float h = HeaderH + 8f + (n == 0 ? 44f : n * RowH) + 8f;
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
            List<DebugActionNode> nodes = ResolveNodes();
            if (nodes.Count != _lastCount) SetInitialSizeAndPosition();

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
                for (int i = 0; i < nodes.Count; i++)
                {
                    DrawRow(new Rect(content.x, y, content.width, RowH - 2f), nodes[i], i);
                    y += RowH;
                }
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

            Widgets.DrawBoxSolid(row, (index & 1) == 1 ? Palette.RowAlt : Palette.PanelBG);
            if (over) Widgets.DrawBoxSolid(row, new Color(1f, 1f, 1f, 0.05f));
            if (broken) Palette.StateStrip(row, Palette.Bad, 3f);

            float pinW = 22f;
            Rect pinR = new Rect(row.xMax - pinW, row.y + (row.height - 18f) / 2f, 18f, 18f);
            Rect clickR = new Rect(row.x, row.y, row.width - pinW - 4f, row.height);

            float x = row.x + 8f;
            if (checkbox)
            {
                Rect ck = new Rect(x, row.y + (row.height - 16f) / 2f, 16f, 16f);
                DrawCheck(ck, DebugTree.GetCheck(node));
                x = ck.xMax + 6f;
            }

            Color col = broken ? Palette.Bad : (active ? Palette.Stat : Palette.TextDim);
            Palette.LabelFit(new Rect(x, row.y, clickR.xMax - x - 2f, row.height), DebugTree.Label(node), col);
            TooltipHandler.TipRegion(clickR, DebugTree.PathOf(node));

            // thumbtack: always pinned here, so white; click to unpin
            Palette.DrawPin(pinR.ContractedBy(1f), true);
            TooltipHandler.TipRegion(pinR, "MDT_Unpin".Translate());
            if (Widgets.ButtonInvisible(pinR)) { DebugTree.TogglePin(node); SetInitialSizeAndPosition(); }

            if (Widgets.ButtonInvisible(clickR))
            {
                if (checkbox) DebugTree.SetCheck(node, !DebugTree.GetCheck(node));
                else DebugTree.RunLeaf(node, null); // keep the palette open (like vanilla)
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
            DebugSettings.devPalette = false;
        }
    }
}
