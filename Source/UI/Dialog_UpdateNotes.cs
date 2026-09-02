using System;
using UnityEngine;
using Verse;

namespace ModernDevTools
{
    /// <summary>Shown once when the player updates Advanced Dev Tools to a new version (or on first install).</summary>
    public class Dialog_UpdateNotes : Window
    {
        private readonly string _prev;
        private Vector2 _scroll;

        public Dialog_UpdateNotes(string prev)
        {
            _prev = prev;
            doWindowBackground = false;
            doCloseX = false;
            draggable = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = true;
            closeOnAccept = false;
            closeOnCancel = true;
        }

        protected override float Margin => 0f;
        public override Vector2 InitialSize => new Vector2(560f, 480f);

        public override void DoWindowContents(Rect inRect)
        {
            try { Draw(inRect); }
            catch (Exception e) { Log.ErrorOnce("[Advanced Dev Tools] update notes draw failed: " + e, 0x2E19C30); }
            finally { Palette.ResetGuiState(); }
        }

        private void Draw(Rect inRect)
        {
            Palette.DialogBG(inRect);
            Rect content = inRect.ContractedBy(16f);

            Text.Font = GameFont.Medium;
            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color = Palette.Stat;
            Widgets.Label(new Rect(content.x, content.y, content.width - 30f, 34f), "MDT_UpdateNotesTitle".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
            if (Palette.CloseX(new Rect(content.xMax - 26f, content.y + 4f, 22f, 22f))) Close();

            float y = content.y + 40f;
            GUI.color = Palette.Accent;
            Widgets.Label(new Rect(content.x, y, content.width, 22f), "MDT_UpdateNotesVersion".Translate(ModernDevToolsMod.Version));
            GUI.color = Color.white;
            y += 28f;

            float footerH = 40f;
            Rect bodyOut = new Rect(content.x, y, content.width, content.yMax - y - footerH);
            Palette.DrawWell(bodyOut);
            Rect bodyInner = bodyOut.ContractedBy(10f);
            string body = "MDT_UpdateNotesBody".Translate();
            float bodyH = Mathf.Max(bodyInner.height, TextMetrics.Height(body, bodyInner.width - 16f));
            Rect view = new Rect(0f, 0f, bodyInner.width - 16f, bodyH);
            Palette.BeginScroll(bodyInner, ref _scroll, view);
            try
            {
                GUI.color = Palette.Stat;
                Text.WordWrap = true;
                Widgets.Label(new Rect(0f, 0f, view.width, bodyH), body);
                GUI.color = Color.white;
            }
            finally { Palette.EndScroll(); }

            float btnW = 120f;
            if (Palette.GrayButton(new Rect(content.xMax - btnW, content.yMax - 30f, btnW, 30f), "MDT_UpdateNotesClose".Translate()))
                Close();
        }
    }
}
