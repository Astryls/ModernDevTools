using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LudeonTK;
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
        public static void DrawCard(Rect r) => DrawPlate(r, PanelBG, BGL);

        /// <summary>Recessed well: BGD fill + 1px BGL border.</summary>
        public static void DrawWell(Rect r) => DrawPlate(r, BGD, BGL);

        /// <summary>
        /// A filled rect with a 1px border - the suite's single most-drawn primitive.
        ///
        /// The obvious form (DrawBoxSolid + Widgets.DrawBox) costs FIVE GUI.DrawTexture calls: one fill
        /// plus one per border edge. Drawing the border colour as a single filled rect and then the fill
        /// colour inset by 1px produces the same ring in TWO calls - a 60% cut on the primitive that
        /// dominates every panel in the mod.
        ///
        /// The fast path is deliberately limited to UIScale 1.0. Widgets.DrawBox snaps every edge with
        /// UIScaling.AdjustRectToUIScaling (floor the mins, ceil the maxes) so the ring lands on device
        /// pixels; at scale 1.0 that reduces to plain floor/ceil, so snapping the outer rect once and
        /// insetting by a whole pixel is provably identical. At fractional scales the inset edge would
        /// no longer sit on a device-pixel boundary and the border could render uneven or blurry, so
        /// those keep vanilla's per-edge snapping. Appearance is the constraint; the draw saving is not
        /// worth a single soft pixel.
        /// </summary>
        public static void DrawPlate(Rect r, Color fill, Color border)
        {
            if (Prefs.UIScale == 1f)
            {
                Rect outer = UIScaling.AdjustRectToUIScaling(r);
                if (outer.width > 2f && outer.height > 2f)
                {
                    Widgets.DrawBoxSolid(outer, border);
                    Widgets.DrawBoxSolid(new Rect(outer.x + 1f, outer.y + 1f, outer.width - 2f, outer.height - 2f), fill);
                    return;
                }
            }
            Widgets.DrawBoxSolid(r, fill);
            DrawBox(r, border, 1);
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

        /// <summary>
        /// Suite checkbox: accent fill when on, recessed well when off, with a hand-drawn tick (two
        /// lines rather than a glyph, so it stays crisp at any size). Shared by the dev actions window
        /// and the dev palette, which each had their own near-identical copy.
        /// </summary>
        public static void DrawCheck(Rect r, bool on)
        {
            Widgets.DrawBoxSolid(r, on ? Accent : BGD);
            DrawBox(r, on ? Accent : BGL, 1);
            if (!on) return;
            float inset = Mathf.Max(3f, r.width * 0.22f);
            var a = new Vector2(r.x + inset, r.y + r.height * 0.55f);
            var b = new Vector2(r.x + r.width * 0.42f, r.yMax - inset);
            var c = new Vector2(r.xMax - inset + 1f, r.y + inset - 1f);
            Widgets.DrawLine(a, b, BGD, 2f);
            Widgets.DrawLine(b, c, BGD, 2f);
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
                h += 4f + TextMetrics.Height(description, width);
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

        /// <summary>
        /// One analysis-module row: name, provenance sub-label and an on/off switch. Shared by the mod
        /// settings page and the quick-access Modules window - they had byte-identical copies, which is
        /// exactly the drift the UI parity contract exists to prevent. Returns true when the toggle was
        /// clicked; the caller owns persisting the change.
        /// </summary>
        public static bool ModuleRow(Rect row, string label, string sourceTag, string tooltip, bool available, bool enabled)
        {
            Widgets.DrawBoxSolid(row, PanelBG);
            if (enabled) StateStrip(row, Accent, 3f);
            DrawBox(row, BGL, 1);
            if (available && Mouse.IsOver(row)) Widgets.DrawBoxSolid(row, new Color(1f, 1f, 1f, 0.04f));

            Color nameCol = !available ? new Color(0.37f, 0.40f, 0.45f) : (enabled ? Stat : TextDim);
            LabelFit(new Rect(row.x + 12f, row.y + 4f, row.width - 70f, 20f), label, nameCol);
            LabelFit(new Rect(row.x + 12f, row.y + 24f, row.width - 70f, 18f), sourceTag, TextDim);

            Rect toggleR = new Rect(row.xMax - 48f, row.center.y - 9f, 36f, 18f);
            DrawToggle(toggleR, enabled);
            if (!tooltip.NullOrEmpty()) TooltipHandler.TipRegion(row, tooltip);

            return available && Widgets.ButtonInvisible(toggleR);
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

        // MANAGED references to the three scrollbar textures. These are not a convenience: assigning a
        // Texture2D to GUIStyleState.background parks the only reference to it behind GUIStyle's native
        // pointer, and GUIStyle is NOT a UnityEngine.Object, so Resources.UnloadUnusedAssets() cannot
        // trace it and destroys the texture. RimWorld runs that sweep through
        // MemoryUtility.UnloadUnusedUnityAssets() on every new game, every save load and every return to
        // the main menu. Before this, the textures were locals in InitScroll() and _scrollInit latched
        // true forever, so after the first sweep the styles pointed at destroyed textures and the
        // scrollbar drew NOTHING for the rest of the session - which reads to the player as "the scroll
        // bar disappears once the log gets big", because a long log means a long session.
        private static Texture2D _texTrack, _texThumb, _texThumbHover;

        // The saved skin styles form a STACK, not four single fields. ErrorModule.DrawSection hooks
        // run INSIDE the inspector's already-active scroll view, so the first third-party module that
        // begins its own scroll would clobber the outer save and leave the flat skin applied to every
        // vanilla scrollbar in the game for the rest of the session.
        private static readonly List<GUIStyle[]> _skinStack = new List<GUIStyle[]>();

        private static void InitScroll()
        {
            // Unity overloads == so a DESTROYED texture compares equal to null while the C# reference is
            // still non-null. That is precisely the state an unload sweep leaves us in, so this is the
            // check that has to gate the cache - not a plain bool latch.
            if (_scrollInit && _texTrack != null && _texThumb != null && _texThumbHover != null) return;

            _texTrack = KeepTexture(new Color(1f, 1f, 1f, 0.04f));
            _texThumb = KeepTexture(new Color(0.55f, 0.58f, 0.62f, 0.55f));
            _texThumbHover = KeepTexture(new Color(0.72f, 0.75f, 0.80f, 0.75f));

            if (!_scrollInit)
            {
                _scrollInit = true;
                _flatBar = new GUIStyle { fixedWidth = 8f };
                // Unity sizes a scrollbar thumb as size*ValuesPerPixel() + ThumbSize(), where
                // ThumbSize() = thumb.fixedHeight if set, else thumb.padding.vertical (verified against
                // decompiled UnityEngine.SliderHandler.VerticalThumbRect / ThumbSize). With both zero the
                // thumb is PURELY proportional and shrinks toward 0px on very long content. The 24px of
                // vertical padding is the thumb's floor; it still grows proportionally on short content.
                _flatThumb = new GUIStyle { fixedWidth = 8f, border = new RectOffset(0, 0, 0, 0), padding = new RectOffset(0, 0, 12, 12) };
                _flatBtn = new GUIStyle();
            }

            // Re-point the styles at the live textures (first build, and after every rebuild).
            _flatBar.normal.background = _texTrack;
            _flatThumb.normal.background = _texThumb;
            _flatThumb.hover.background = _texThumbHover;
            _flatThumb.active.background = _texThumbHover;
        }

        /// <summary>
        /// A solid-colour texture that survives RimWorld's asset unload sweeps. HideAndDontSave takes it
        /// out of Resources.UnloadUnusedAssets() entirely; the static field the caller stores it in is
        /// the belt to that braces. Returns null off the main thread (vanilla refuses to build textures
        /// there) - callers then draw an untinted bar rather than throwing.
        /// </summary>
        private static Texture2D KeepTexture(Color c)
        {
            Texture2D t = SolidColorMaterials.NewSolidColorTexture(c);
            if (t != null) t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        public static void BeginScroll(Rect outRect, ref Vector2 scroll, Rect viewRect)
        {
            InitScroll();
            _skinStack.Add(new[]
            {
                GUI.skin.verticalScrollbar, GUI.skin.verticalScrollbarThumb,
                GUI.skin.verticalScrollbarUpButton, GUI.skin.verticalScrollbarDownButton
            });
            GUI.skin.verticalScrollbar = _flatBar;
            GUI.skin.verticalScrollbarThumb = _flatThumb;
            GUI.skin.verticalScrollbarUpButton = _flatBtn;
            GUI.skin.verticalScrollbarDownButton = _flatBtn;
            Widgets.BeginScrollView(outRect, ref scroll, viewRect);
        }

        public static void EndScroll()
        {
            Widgets.EndScrollView();
            PopSkin();
        }

        private static void PopSkin()
        {
            int n = _skinStack.Count;
            if (n == 0) return;
            GUIStyle[] saved = _skinStack[n - 1];
            _skinStack.RemoveAt(n - 1);
            GUI.skin.verticalScrollbar = saved[0];
            GUI.skin.verticalScrollbarThumb = saved[1];
            GUI.skin.verticalScrollbarUpButton = saved[2];
            GUI.skin.verticalScrollbarDownButton = saved[3];
        }

        /// <summary>
        /// Restore the engine's GUI defaults at the end of a draw pass. Call this from EVERY window's
        /// finally block. Besides the usual color/font/anchor reset it unwinds any scroll-skin saves
        /// stranded by an exception thrown between BeginScroll and EndScroll - without it, one throw
        /// leaves the flat scrollbar skin applied game-wide (unwinding to the bottom of the stack
        /// restores the pristine skin, because that entry was pushed by the outermost BeginScroll).
        /// </summary>
        public static void ResetGuiState()
        {
            while (_skinStack.Count > 0) PopSkin();
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = true;
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
            if (TextMetrics.Size(text).x > r.width && !shortFallback.NullOrEmpty())
                draw = shortFallback;
            if (TextMetrics.Size(draw).x > r.width)
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
