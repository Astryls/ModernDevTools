using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// Modern Suite shared visual language. Tokens are kept hex-for-hex in parity with
    /// the sibling mods (Modern Needs/Health/Bio tabs). The accent is resolved from Modern
    /// Notifications' Theme.Accent via cached reflection so one theme choice restyles the suite;
    /// it falls back to a hardcoded blue when that mod is absent.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class Palette
    {
        // Base palette (see GLOBAL_RULES.md parity contract).
        public static readonly Color BG = FromHex(0x15191D);       // window/base
        public static readonly Color BGL = FromHex(0x2F3337);      // 1px borders
        public static readonly Color BGD = FromHex(0x0E1013);      // wells / backdrops
        public static readonly Color Stat = FromHex(0xE3E3E3);     // primary text
        public static readonly Color TextDim = new Color(0.62f, 0.65f, 0.70f);
        public static readonly Color PanelBG = Color.Lerp(BG, BGL, 0.22f); // card/row fill ~#1B1F23
        public static readonly Color RowAlt = FromHex(0x20242A);   // alternating row plate
        public static readonly Color StripGray = FromHex(0x5A6270);// neutral strip (info messages)

        // One status ramp everywhere. Same meaning = same pixel across the suite.
        public static readonly Color Good = new Color(0.40f, 0.85f, 0.40f);
        public static readonly Color Warn = new Color(0.95f, 0.65f, 0.20f);
        public static readonly Color Bad = new Color(0.90f, 0.35f, 0.35f);

        private static readonly Color AccentFallback = new Color(0.45f, 0.75f, 1f);

        // --- Theme.Accent bridge (cached reflection on ModernNotifications) ---
        private static bool _accentResolved;
        private static FieldInfo _accentField;
        private static PropertyInfo _accentProp;
        private static int _accentFrame = -1;
        private static Color _accentCached;

        public static Color Accent
        {
            get
            {
                int frame = Time.frameCount;
                if (frame == _accentFrame) return _accentCached;
                _accentFrame = frame;
                _accentCached = ResolveAccent();
                return _accentCached;
            }
        }

        private static Color ResolveAccent()
        {
            try
            {
                if (!_accentResolved)
                {
                    _accentResolved = true;
                    Type t = AccessTools.TypeByName("ModernNotifications.Theme");
                    if (t != null)
                    {
                        _accentProp = t.GetProperty("Accent", BindingFlags.Public | BindingFlags.Static);
                        if (_accentProp == null || _accentProp.PropertyType != typeof(Color))
                        {
                            _accentProp = null;
                            _accentField = t.GetField("Accent", BindingFlags.Public | BindingFlags.Static);
                            if (_accentField != null && _accentField.FieldType != typeof(Color))
                                _accentField = null;
                        }
                    }
                }

                if (_accentProp != null) return (Color)_accentProp.GetValue(null, null);
                if (_accentField != null) return (Color)_accentField.GetValue(null);
            }
            catch
            {
                _accentField = null;
                _accentProp = null;
            }
            return AccentFallback;
        }

        public static Color FromHex(int hex)
        {
            return new Color(((hex >> 16) & 0xFF) / 255f, ((hex >> 8) & 0xFF) / 255f, (hex & 0xFF) / 255f);
        }

        // --- Draw helpers ---

        /// <summary>Solid opaque card: PanelBG fill + 1px BGL border.</summary>
        public static void DrawCard(Rect r)
        {
            Widgets.DrawBoxSolid(r, PanelBG);
            DrawBox(r, BGL, 1);
        }

        /// <summary>Recessed well: BGD fill + 1px BGL border.</summary>
        public static void DrawWell(Rect r)
        {
            Widgets.DrawBoxSolid(r, BGD);
            DrawBox(r, BGL, 1);
        }

        public static void DrawBox(Rect r, Color color, int thickness)
        {
            Color prev = GUI.color;
            GUI.color = color;
            Widgets.DrawBox(r, thickness);
            GUI.color = prev;
        }

        /// <summary>The suite's single state indicator: a left-edge vertical strip.</summary>
        public static void StateStrip(Rect rowRect, Color color, float width = 3f)
        {
            Widgets.DrawBoxSolid(new Rect(rowRect.x, rowRect.y, width, rowRect.height), color);
        }

        /// <summary>Sentence-case Small header label + a 1px BGL divider along the bottom edge.</summary>
        public static void SectionHeader(Rect r, string label)
        {
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.LowerLeft;
            bool prevWrap = Text.WordWrap;
            Text.WordWrap = false;
            GUI.color = Stat;
            Widgets.Label(new Rect(r.x, r.y, r.width, r.height), label);
            GUI.color = Color.white;
            Text.WordWrap = prevWrap;
            Text.Anchor = TextAnchor.UpperLeft;
            Widgets.DrawBoxSolid(new Rect(r.x, r.yMax - 1f, r.width, 1f), BGL);
        }

        /// <summary>Flat gray suite button (never the tan vanilla ButtonText). Returns true on left-click.</summary>
        public static bool GrayButton(Rect r, string label, string tooltip = null, bool enabled = true)
        {
            bool over = enabled && Mouse.IsOver(r);
            Color fill = !enabled ? PanelBG : (over ? Color.Lerp(BGL, Accent, 0.14f) : BGL);
            Widgets.DrawBoxSolid(r, fill);
            DrawBox(r, new Color(0f, 0f, 0f, 0.28f), 1);

            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleCenter;
            bool prevWrap = Text.WordWrap;
            Text.WordWrap = false;
            GUI.color = enabled ? Stat : TextDim;
            Widgets.Label(r, label);
            GUI.color = Color.white;
            Text.WordWrap = prevWrap;
            Text.Anchor = TextAnchor.UpperLeft;

            if (!tooltip.NullOrEmpty()) TooltipHandler.TipRegion(r, tooltip);
            if (!enabled) return false;
            return Widgets.ButtonInvisible(r);
        }

        /// <summary>A small on/off switch (draw-only; the caller handles the click on its row/rect).</summary>
        public static void DrawToggle(Rect r, bool on)
        {
            Widgets.DrawBoxSolid(r, on ? Accent : BGL);
            DrawBox(r, new Color(0f, 0f, 0f, 0.35f), 1);
            var knob = new Rect(on ? r.xMax - 16f : r.x + 2f, r.y + 2f, 14f, 14f);
            Widgets.DrawBoxSolid(knob, on ? BGD : TextDim);
        }

        /// <summary>
        /// Height a full-width settings toggle row needs: one label line plus an optional wrapped
        /// dim description under it. Measure with the SAME width you will draw into so text never clips
        /// (descriptions are measured with Text.CalcHeight, honoring the current DisableTinyText metrics).
        /// </summary>
        public static float ToggleRowHeight(string description, float width)
        {
            float h = 24f;
            if (!description.NullOrEmpty())
            {
                Text.Font = GameFont.Small;
                bool prevWrap = Text.WordWrap;
                Text.WordWrap = true;
                h += 4f + Text.CalcHeight(description, width);
                Text.WordWrap = prevWrap;
            }
            return h;
        }

        /// <summary>
        /// A full-width settings row in the suite language: sentence-case label on the left, an on/off
        /// switch on the right, and an optional wrapped dim description beneath. The whole row is the
        /// click target. Size the rect with <see cref="ToggleRowHeight"/> so nothing clips. Returns the
        /// new value; the caller is responsible for persisting it.
        /// </summary>
        public static bool ToggleRow(Rect r, string label, bool value, string description = null)
        {
            if (Mouse.IsOver(r)) Widgets.DrawBoxSolid(r, new Color(1f, 1f, 1f, 0.04f));

            LabelFit(new Rect(r.x, r.y, r.width - 48f, 24f), label, Stat);
            DrawToggle(new Rect(r.xMax - 40f, r.y + 3f, 36f, 18f), value);

            if (!description.NullOrEmpty())
            {
                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.UpperLeft;
                Text.WordWrap = true;
                GUI.color = TextDim;
                float dy = r.y + 28f;
                Widgets.Label(new Rect(r.x, dy, r.width, r.yMax - dy), description);
                GUI.color = Color.white;
            }

            return Widgets.ButtonInvisible(r) ? !value : value;
        }

        /// <summary>Themed close button (an X drawn from two lines; no glyph). Returns true on click.</summary>
        public static bool CloseX(Rect r)
        {
            Color c = Mouse.IsOver(r) ? Stat : TextDim;
            if (MdtTex.Close != null)
            {
                GUI.color = c;
                GUI.DrawTexture(r.ContractedBy(2f), MdtTex.Close, ScaleMode.ScaleToFit);
                GUI.color = Color.white;
            }
            else
            {
                float pad = 6f;
                Widgets.DrawLine(new Vector2(r.x + pad, r.y + pad), new Vector2(r.xMax - pad, r.yMax - pad), c, 1.5f);
                Widgets.DrawLine(new Vector2(r.xMax - pad, r.y + pad), new Vector2(r.x + pad, r.yMax - pad), c, 1.5f);
            }
            return Widgets.ButtonInvisible(r);
        }

        /// <summary>A left-aligned single-line label, width-tested with a shorter fallback (never a blind Truncate).</summary>
        // --- icons ---

        /// <summary>Thumbtack pin icon: gray when inactive, near-white when pinned.</summary>
        public static void DrawPin(Rect r, bool active)
        {
            if (MdtTex.Pin != null)
            {
                GUI.color = active ? Color.white : new Color(0.52f, 0.55f, 0.60f);
                GUI.DrawTexture(r, MdtTex.Pin, ScaleMode.ScaleToFit);
                GUI.color = Color.white;
            }
            else
            {
                Widgets.DrawBoxSolid(r.ContractedBy(4f), active ? Accent : TextDim);
            }
        }

        /// <summary>Right-pointing chevron (crisp texture, line fallback).</summary>
        public static void DrawChevron(Rect r, bool over)
        {
            Color c = over ? Stat : TextDim;
            if (MdtTex.Chevron != null)
            {
                GUI.color = c;
                GUI.DrawTexture(r, MdtTex.Chevron, ScaleMode.ScaleToFit);
                GUI.color = Color.white;
            }
            else
            {
                Widgets.DrawLine(new Vector2(r.x, r.y), new Vector2(r.xMax, r.center.y), c, 1.5f);
                Widgets.DrawLine(new Vector2(r.xMax, r.center.y), new Vector2(r.x, r.yMax), c, 1.5f);
            }
        }

        // --- flat gray scrollbars (suite-wide rule) ---
        // Swap GUI.skin's vertical scrollbar for a flat gray thumb on a faint track. Always pair
        // BeginScroll with EndScroll (in a try/finally) so the skin is restored even on exceptions.
        private static bool _scrollInit;
        private static GUIStyle _flatBar, _flatThumb, _flatBtn;
        private static GUIStyle _savedBar, _savedThumb, _savedUp, _savedDown;

        private static void InitScroll()
        {
            if (_scrollInit) return;
            _scrollInit = true;
            var track = SolidColorMaterials.NewSolidColorTexture(new Color(1f, 1f, 1f, 0.04f));
            var thumb = SolidColorMaterials.NewSolidColorTexture(new Color(0.55f, 0.58f, 0.62f, 0.55f));
            var thumbHover = SolidColorMaterials.NewSolidColorTexture(new Color(0.72f, 0.75f, 0.80f, 0.75f));
            _flatBar = new GUIStyle { fixedWidth = 8f };
            _flatBar.normal.background = track;
            _flatThumb = new GUIStyle { fixedWidth = 8f, border = new RectOffset(0, 0, 0, 0) };
            _flatThumb.normal.background = thumb;
            _flatThumb.hover.background = thumbHover;
            _flatThumb.active.background = thumbHover;
            _flatBtn = new GUIStyle();
        }

        public static void BeginScroll(Rect outRect, ref Vector2 scroll, Rect viewRect)
        {
            InitScroll();
            _savedBar = GUI.skin.verticalScrollbar;
            _savedThumb = GUI.skin.verticalScrollbarThumb;
            _savedUp = GUI.skin.verticalScrollbarUpButton;
            _savedDown = GUI.skin.verticalScrollbarDownButton;
            GUI.skin.verticalScrollbar = _flatBar;
            GUI.skin.verticalScrollbarThumb = _flatThumb;
            GUI.skin.verticalScrollbarUpButton = _flatBtn;
            GUI.skin.verticalScrollbarDownButton = _flatBtn;
            Widgets.BeginScrollView(outRect, ref scroll, viewRect);
        }

        public static void EndScroll()
        {
            Widgets.EndScrollView();
            if (!_scrollInit) return;
            GUI.skin.verticalScrollbar = _savedBar;
            GUI.skin.verticalScrollbarThumb = _savedThumb;
            GUI.skin.verticalScrollbarUpButton = _savedUp;
            GUI.skin.verticalScrollbarDownButton = _savedDown;
        }

        // --- themed text / search field (dark well, accent-on-focus border, no vanilla frame) ---
        private static GUIStyle _clearFieldStyle;

        private static GUIStyle ClearFieldStyle()
        {
            if (_clearFieldStyle == null)
            {
                // Built lazily during OnGUI (GUI.skin is not ready at static-ctor time). Strip every
                // state background so the field paints no vanilla box - we draw our own well + border.
                _clearFieldStyle = new GUIStyle(GUI.skin.textField);
                _clearFieldStyle.normal.background = null;
                _clearFieldStyle.focused.background = null;
                _clearFieldStyle.active.background = null;
                _clearFieldStyle.hover.background = null;
                _clearFieldStyle.onNormal.background = null;
                _clearFieldStyle.onFocused.background = null;
                _clearFieldStyle.onActive.background = null;
                _clearFieldStyle.normal.textColor = Stat;
                _clearFieldStyle.focused.textColor = Stat;
                _clearFieldStyle.active.textColor = Stat;
                _clearFieldStyle.hover.textColor = Stat;
                _clearFieldStyle.alignment = TextAnchor.MiddleLeft;
                _clearFieldStyle.padding = new RectOffset(8, 8, 0, 0);
                _clearFieldStyle.margin = new RectOffset(0, 0, 0, 0);
                _clearFieldStyle.wordWrap = false;
                _clearFieldStyle.clipping = TextClipping.Clip;
            }
            return _clearFieldStyle;
        }

        /// <summary>
        /// Suite-styled single-line text/search field: a dark well with a 1px border that turns Accent
        /// while focused, a dim placeholder when empty and unfocused, and NO vanilla box. The control
        /// name is routed through GUI.SetNextControlName so keyboard focus never jumps between fields.
        /// </summary>
        public static string SearchField(Rect r, string controlName, string text, string placeholder = null)
        {
            text = text ?? "";
            bool focused = GUI.GetNameOfFocusedControl() == controlName;

            Widgets.DrawBoxSolid(r, BGD);
            DrawBox(r, focused ? Accent : BGL, 1);

            Text.Font = GameFont.Small;
            GUI.SetNextControlName(controlName);
            string edited = GUI.TextField(r, text, ClearFieldStyle());

            if (edited.Length == 0 && !focused && !placeholder.NullOrEmpty())
            {
                GUI.color = TextDim;
                Text.Anchor = TextAnchor.MiddleLeft;
                Text.WordWrap = false;
                Widgets.Label(new Rect(r.x + 8f, r.y, r.width - 12f, r.height), placeholder);
            }

            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = true;
            return edited;
        }

        public static void LabelFit(Rect r, string text, Color color, string shortFallback = null)
        {
            Text.Font = GameFont.Small;
            bool prevWrap = Text.WordWrap;
            Text.WordWrap = false;
            string draw = text;
            if (Text.CalcSize(text).x > r.width && !shortFallback.NullOrEmpty())
                draw = shortFallback;
            if (Text.CalcSize(draw).x > r.width)
                draw = draw.Truncate(r.width); // last resort
            GUI.color = color;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(r, draw);
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = prevWrap;
        }
    }
}
