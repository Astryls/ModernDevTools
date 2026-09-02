using UnityEngine;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// Rounded surfaces, neutral elevation and the iOS control shapes the sidebar layout needs and
    /// RimWorld's widget set does not have. Ported from the suite's shared spatial kit (Modern Needs
    /// Tab) so the two mods draw the same corners at the same radii.
    ///
    /// Everything here is ONE procedurally generated texture per shape, drawn through
    /// <see cref="Widgets.DrawAtlas"/> (vanilla's 9-slice: corner size = atlas.width/4, clamped to
    /// half the rect, UI-scaling snapped). That gives a true rounded rectangle at any size from a
    /// single small texture, tinted by GUI.color, with no shipped art - and so no cs-assets manifest
    /// entry that could drift.
    ///
    /// COST DISCIPLINE. A 9-slice is NINE GUI.DrawTexture calls, so it is reserved for SURFACES -
    /// a handful per panel (the sidebar, the group plates, the inspector cards). Anything drawn per
    /// LIST ROW uses the single-quad helpers (<see cref="Dot"/>) or a plain filled rect: the log list
    /// draws ~30 rows per pass and OnGUI runs 2-3 passes a frame, so a rounded plate per row would be
    /// ~800 draw calls a frame for corners nobody can see behind the text. A tint below ~5% alpha has
    /// no visible corners anyway, which is why the alternating row plate stays square.
    ///
    /// [StaticConstructorOnStartup] is mandatory: the class holds static Texture2D fields and the
    /// attribute is what tells RimWorld to build them on the main thread at load.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class Spatial
    {
        // Corner radii, matching the suite's scale (xl 24 / lg 18 / md 14 / sm 10).
        private static Texture2D _capsule, _surface, _row, _shadow, _disc;

        /// <summary>
        /// Textures are rebuilt on demand rather than only in the static constructor.
        ///
        /// A Texture2D whose only surviving reference is a native GUI pointer can be collected by
        /// Resources.UnloadUnusedAssets(), which RimWorld runs through MemoryUtility on every new
        /// game, save load and return to the main menu. These are held in static managed fields AND
        /// flagged HideAndDontSave, which should be belt and braces - but Unity overloads == so a
        /// destroyed texture compares equal to null while the C# reference is still non-null, and
        /// that is the exact state a sweep leaves behind. Re-validating here (rather than latching a
        /// bool) is what stopped the flat scrollbar from vanishing mid-session, so the same rule is
        /// applied to every generated texture in the mod.
        /// </summary>
        private static void Ensure()
        {
            if (_capsule == null) _capsule = MakeRounded(22);
            if (_surface == null) _surface = MakeRounded(18);
            if (_row == null) _row = MakeRounded(10);
            if (_shadow == null) _shadow = MakeShadow(22, 14);
            if (_disc == null) _disc = MakeDisc(32);
        }

        // ── draw ────────────────────────────────────────────────────────────────
        // Every public shape branches on the skin (the dual-skin house rule: branches live in the
        // style layer, never at call sites). Vanilla = square fills; panels additionally wear the
        // engine's menu-section border when the fill is opaque, so nested groups read the way
        // vanilla's own section-in-section screens do. Translucent tints (banners, hover films,
        // chips) stay borderless in both skins.

        private static void Atlas(Rect r, Color c, Texture2D tex)
        {
            if (r.width < 2f || r.height < 2f || tex == null) return;
            Color prev = GUI.color;
            GUI.color = c;
            Widgets.DrawAtlas(r, tex);
            GUI.color = prev;
        }

        private static void SquarePanel(Rect r, Color c)
        {
            if (r.width < 2f || r.height < 2f) return;
            Widgets.DrawBoxSolid(r, c);
            if (c.a >= 0.995f) Palette.DrawBox(r, Palette.BGL, 1);
        }

        /// <summary>Raised island / large card (22px corners; vanilla: bordered square panel).</summary>
        public static void Capsule(Rect r, Color c)
        {
            if (!Palette.ModernSkin) { SquarePanel(r, c); return; }
            Ensure(); Atlas(r, c, _capsule);
        }

        /// <summary>Panel surface, sidebar, grouped-list plate (18px corners; vanilla: bordered
        /// square panel).</summary>
        public static void Surface(Rect r, Color c)
        {
            if (!Palette.ModernSkin) { SquarePanel(r, c); return; }
            Ensure(); Atlas(r, c, _surface);
        }

        /// <summary>Row plate / small control (10px corners; vanilla: plain square fill).</summary>
        public static void RowPlate(Rect r, Color c)
        {
            if (!Palette.ModernSkin) { if (r.width >= 2f && r.height >= 2f) Widgets.DrawBoxSolid(r, c); return; }
            Ensure(); Atlas(r, c, _row);
        }

        /// <summary>Fully rounded capsule (vanilla: plain square fill). DrawAtlas clamps the corner
        /// to half the rect, so the 22px atlas produces a true pill on anything up to 44px tall.</summary>
        public static void Pill(Rect r, Color c)
        {
            if (!Palette.ModernSkin) { if (r.width >= 2f && r.height >= 2f) Widgets.DrawBoxSolid(r, c); return; }
            Ensure(); Atlas(r, c, _capsule);
        }

        /// <summary>Filled circle - ONE draw call, so this is what rows and markers use. Vanilla:
        /// a square chip (vanilla never draws circles; a small square reads native).</summary>
        public static void Dot(Rect r, Color c)
        {
            if (!Palette.ModernSkin) { Widgets.DrawBoxSolid(r, c); return; }
            Ensure();
            if (_disc == null) return;
            Color prev = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _disc);
            GUI.color = prev;
        }

        /// <summary>Neutral drop shadow under a raised surface. The blur is baked into the atlas, so
        /// an elevation is 9 draw calls rather than a stack of offset rectangles. No coloured halos:
        /// the suite's elevation is always plain black alpha.</summary>
        public static void Elevate(Rect r, Rect clamp, float spread = 16f, float alpha = 0.5f)
        {
            if (!Palette.ModernSkin) return;   // vanilla has no soft shadows
            if (Event.current.type != EventType.Repaint) return;
            Ensure();
            Rect s = new Rect(r.x - spread, r.y - spread + spread * 0.42f,
                              r.width + spread * 2f, r.height + spread * 2f);
            // A shadow is the one thing that draws OUTSIDE its own surface, so it is the one thing
            // that can escape the panel it belongs to. IMGUI has no cheap clip that is safe here, so
            // the spill is clamped instead; the 9-slice just scales its edge slices and the falloff
            // still reads correctly.
            float x0 = Mathf.Max(s.x, clamp.x), x1 = Mathf.Min(s.xMax, clamp.xMax);
            float y0 = Mathf.Max(s.y, clamp.y), y1 = Mathf.Min(s.yMax, clamp.yMax);
            if (x1 - x0 < 2f || y1 - y0 < 2f) return;
            Atlas(new Rect(x0, y0, x1 - x0, y1 - y0), new Color(0f, 0f, 0f, alpha), _shadow);
        }

        /// <summary>The 1px top inner highlight that separates a raised surface from an identically
        /// filled recessed one. Inset past the corner arc so it cannot poke out.</summary>
        public static void TopHighlight(Rect r, float radius = 18f, float alpha = 0.055f)
        {
            if (!Palette.ModernSkin) return;   // elevation grammar is Modern-only
            if (r.width <= radius * 2f) return;
            Widgets.DrawBoxSolid(new Rect(r.x + radius, r.y, r.width - radius * 2f, 1f),
                new Color(1f, 1f, 1f, alpha));
        }

        /// <summary>A hairline separator between grouped rows. Inset from the left so it starts under
        /// the row's content rather than cutting the whole plate - the iOS grouped-table rule.</summary>
        public static void Separator(float x, float y, float w)
        {
            if (w <= 0f) return;
            Widgets.DrawBoxSolid(new Rect(x, y, w, 1f), Palette.Sep);
        }

        /// <summary>iOS-shaped switch in suite colours: the ACCENT carries "on" (the suite marks
        /// interactive state with the accent; green is reserved for good/health semantics). This also
        /// matches the settings-page toggles, which were already accent - the green here was the one
        /// switch in the mod that disagreed.</summary>
        public static void Switch(Rect r, bool on)
        {
            if (!Palette.ModernSkin)
            {
                // Vanilla skin: the engine's own checkbox art, right-aligned in the switch's rect
                // (draw-only, like the modern branch - the caller owns the click on its row).
                float s = Mathf.Min(24f, r.height + 2f);
                Widgets.CheckboxDraw(r.xMax - s, r.center.y - s * 0.5f, on, false, s);
                return;
            }
            Pill(r, on ? Palette.Accent : Palette.SwitchOff);
            float d = Mathf.Max(4f, r.height - 4f);
            Dot(new Rect(on ? r.xMax - 2f - d : r.x + 2f, r.y + 2f, d, d), Color.white);
        }

        /// <summary>Capsule badge with centred text - the state tags and count pills.</summary>
        public static void Badge(Rect r, string label, Color fill, Color text)
        {
            Pill(r, fill);
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleCenter;
            bool prevWrap = Text.WordWrap;
            Text.WordWrap = false;
            GUI.color = text;
            Widgets.Label(r, label);
            GUI.color = Color.white;
            Text.WordWrap = prevWrap;
            Text.Anchor = TextAnchor.UpperLeft;
        }

        // ── generation ──────────────────────────────────────────────────────────

        private static Texture2D NewTex(int n)
        {
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false);
            t.filterMode = FilterMode.Bilinear;
            t.wrapMode = TextureWrapMode.Clamp;
            t.hideFlags = HideFlags.HideAndDontSave;
            t.name = "MDT_Spatial_" + n;
            return t;
        }

        private static Color32 White(float a)
            => new Color32(255, 255, 255, (byte)Mathf.RoundToInt(Mathf.Clamp01(a) * 255f));

        /// <summary>White rounded rect, sized so DrawAtlas's quarter-width corner slice is exactly the
        /// requested radius (n = 4r). Coverage is antialiased from the signed distance, so the arc
        /// stays clean at every scale the 9-slice stretches to.</summary>
        private static Texture2D MakeRounded(int r)
        {
            int n = r * 4;
            var tex = NewTex(n);
            var px = new Color32[n * n];
            float half = n * 0.5f;
            float flat = half - r;
            for (int y = 0; y < n; y++)
            {
                float dy = Mathf.Abs(y + 0.5f - half);
                float qy = Mathf.Max(dy - flat, 0f);
                for (int x = 0; x < n; x++)
                {
                    float dx = Mathf.Abs(x + 0.5f - half);
                    float qx = Mathf.Max(dx - flat, 0f);
                    float d = Mathf.Sqrt(qx * qx + qy * qy) - r;
                    px[y * n + x] = White(0.5f - d);
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);   // never read back: drop the CPU copy
            return tex;
        }

        /// <summary>Soft shadow for a rounded rect, blur baked in. Corner slice = r + blur, so the
        /// atlas still 9-slices correctly.</summary>
        private static Texture2D MakeShadow(int r, int blur)
        {
            int c = r + blur;
            int n = c * 4;
            var tex = NewTex(n);
            var px = new Color32[n * n];
            float half = n * 0.5f;
            float flat = half - blur - r;
            for (int y = 0; y < n; y++)
            {
                float dy = Mathf.Abs(y + 0.5f - half);
                float qy = Mathf.Max(dy - flat, 0f);
                for (int x = 0; x < n; x++)
                {
                    float dx = Mathf.Abs(x + 0.5f - half);
                    float qx = Mathf.Max(dx - flat, 0f);
                    float d = Mathf.Sqrt(qx * qx + qy * qy) - r;
                    float a = d <= 0f ? 1f : 1f - Mathf.Clamp01(d / blur);
                    px[y * n + x] = White(a * a);   // squared falloff reads softer
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }

        private static Texture2D MakeDisc(int n)
        {
            var tex = NewTex(n);
            var px = new Color32[n * n];
            float half = n * 0.5f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = x + 0.5f - half, dy = y + 0.5f - half;
                    float d = Mathf.Sqrt(dx * dx + dy * dy) - (half - 0.5f);
                    px[y * n + x] = White(0.5f - d);
                }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }
    }
}
