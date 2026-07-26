using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// A global reference browser for everything Modern Dev Tools knows, independent of any selected
    /// error. A left rail of source categories, a searchable content pane on the right, and every entry
    /// cited to where it came from (built-in library, community databases, a shipping mod, your own mod
    /// list, or the live Harmony registry). Opened from the debug log's "Knowledge base" button.
    ///
    /// The community compatibility databases (RimSort rules, Use This Instead) are tens of thousands of
    /// entries, so they are scoped to the player's own installed/active mods - only findings relevant to
    /// this setup are shown.
    /// </summary>
    public class Window_KnowledgeBase : Window
    {
        private enum KbTab { Sources, KnownIssues, Benign, CommunityBugs, Compatibility, Dependencies, Harmony, Glossary }

        private const float Pad = 12f;
        private const float Gap = 8f;

        private static readonly KbTab[] TabsOrder =
        {
            KbTab.Sources, KbTab.KnownIssues, KbTab.Benign, KbTab.CommunityBugs,
            KbTab.Compatibility, KbTab.Dependencies, KbTab.Harmony, KbTab.Glossary
        };

        private KbTab _tab = KbTab.Sources;
        private string _search = "";
        private Vector2 _scroll;
        private float _contentH = 400f;
        private bool _focusSearch;
        private int _openedFrame;

        // Memoized compatibility findings (rebuilt when the community-data stamp changes).
        private CompatData _compat;
        private long _compatStamp = -1;

        // Harmony conflicts, built lazily the first time the Harmony tab is opened (the index build hitches
        // once on large modlists, so it is not done for the rail badge).
        private List<HConflict> _harmony;

        public Window_KnowledgeBase()
        {
            doWindowBackground = false;
            doCloseX = false;
            draggable = true;
            resizeable = true;
            preventCameraMotion = false;
            drawShadow = true;
            closeOnAccept = false;
            closeOnCancel = true;
            onlyOneOfTypeAllowed = true;
            layer = WindowLayer.Dialog;
        }

        protected override float Margin => 0f;

        public override Vector2 InitialSize =>
            new Vector2(Mathf.Min(UI.screenWidth * 0.72f, 1160f), Mathf.Min(UI.screenHeight * 0.76f, 840f));

        public static void OpenIfNeeded()
        {
            var ws = Find.WindowStack;
            if (ws == null) return;
            if (!ws.IsOpen<Window_KnowledgeBase>()) ws.Add(new Window_KnowledgeBase());
        }

        public override void DoWindowContents(Rect inRect)
        {
            try { DrawAll(inRect); }
            catch (Exception e) { Log.ErrorOnce("[Modern Dev Tools] knowledge window draw failed: " + e, 0x2E19F00); }
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

            // Title + close X
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleLeft;
            Text.WordWrap = false;
            GUI.color = Palette.Stat;
            Widgets.Label(new Rect(content.x, content.y, content.width - 34f, 30f), "MDT_KbTitle".Translate());
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = true;
            if (Palette.CloseX(new Rect(content.xMax - 26f, content.y + 2f, 22f, 22f))) Close();

            float top = content.y + 34f;
            const float railW = 236f;
            Rect railR = new Rect(content.x, top, railW, content.yMax - top);
            Rect paneR = new Rect(railR.xMax + Gap, top, content.width - railW - Gap, content.yMax - top);

            DrawRail(railR);
            DrawPane(paneR);
        }

        // --- left rail ---

        private void DrawRail(Rect rect)
        {
            Palette.DrawCard(rect);
            Rect inner = rect.ContractedBy(8f);
            const float rowH = 44f;
            float y = inner.y;
            foreach (KbTab t in TabsOrder)
            {
                Rect row = new Rect(inner.x, y, inner.width, rowH - 6f);
                bool sel = _tab == t;
                Color plate = sel ? Color.Lerp(Palette.PanelBG, Palette.BGL, 0.5f) : Palette.PanelBG;
                if (!sel && Mouse.IsOver(row)) plate = Color.Lerp(plate, Palette.BGL, 0.45f);
                Widgets.DrawBoxSolid(row, plate);
                Palette.DrawBox(row, Palette.BGL, 1);
                if (sel) Palette.StateStrip(row, Palette.Accent, 3f);

                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.MiddleLeft;
                Text.WordWrap = false;
                GUI.color = sel ? Palette.Stat : Palette.TextDim;
                Widgets.Label(new Rect(row.x + 12f, row.y, row.width - 52f, row.height), TabLabel(t));

                int c = RailCount(t);
                if (c >= 0)
                {
                    Text.Anchor = TextAnchor.MiddleRight;
                    GUI.color = Palette.TextDim;
                    Widgets.Label(new Rect(row.x, row.y, row.width - 12f, row.height), c.ToString());
                }
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                Text.WordWrap = true;

                if (Widgets.ButtonInvisible(row)) Select(t);
                y += rowH;
            }
        }

        private void Select(KbTab t)
        {
            if (_tab == t) return;
            _tab = t;
            _search = "";
            _scroll = Vector2.zero;
            _focusSearch = true;
            _openedFrame = Time.frameCount;
        }

        private static string TabLabel(KbTab t)
        {
            switch (t)
            {
                case KbTab.Sources: return "MDT_KbTabSources".Translate();
                case KbTab.KnownIssues: return "MDT_KbTabKnown".Translate();
                case KbTab.Benign: return "MDT_KbTabBenign".Translate();
                case KbTab.CommunityBugs: return "MDT_KbTabCommunity".Translate();
                case KbTab.Compatibility: return "MDT_KbTabCompat".Translate();
                case KbTab.Dependencies: return "MDT_KbTabDeps".Translate();
                case KbTab.Harmony: return "MDT_KbTabHarmony".Translate();
                default: return "MDT_KbTabGlossary".Translate();
            }
        }

        private int RailCount(KbTab t)
        {
            switch (t)
            {
                case KbTab.KnownIssues: return CountDefs(false);
                case KbTab.Benign: return CountDefs(true);
                case KbTab.CommunityBugs: return CommunityData.BugsCount + (ModShippedIssues.All?.Count ?? 0);
                case KbTab.Compatibility: return Compat().Count;
                case KbTab.Dependencies: return ModDependencyIndex.All?.Count ?? 0;
                case KbTab.Harmony: return HarmonyIndex.Built ? HarmonyIndex.BuiltMethodCount : -1;
                case KbTab.Glossary: return Glossary.AllTerms().Count;
                default: return -1;
            }
        }

        // --- right pane ---

        private void DrawPane(Rect rect)
        {
            Palette.DrawCard(rect);
            Rect inner = rect.ContractedBy(10f);
            Rect topBar = new Rect(inner.x, inner.y, inner.width, 28f);

            if (_tab == KbTab.Sources)
            {
                DrawCommunityControl(topBar);
            }
            else
            {
                string edited = Palette.SearchField(topBar, "MDT_KbSearch", _search ?? "", "MDT_KbSearchPlaceholder".Translate());
                if (edited != _search) { _search = edited; _scroll.y = 0f; }
                if (_focusSearch && Time.frameCount > _openedFrame) { GUI.FocusControl("MDT_KbSearch"); _focusSearch = false; }
            }

            Rect body = new Rect(inner.x, topBar.yMax + 8f, inner.width, inner.yMax - topBar.yMax - 8f);
            float vw = body.width - 16f;
            Rect view = new Rect(0f, 0f, vw, Mathf.Max(_contentH, body.height));
            Palette.BeginScroll(body, ref _scroll, view);
            try
            {
                float yEnd;
                try { yEnd = DrawTabContent(vw); }
                catch (Exception e)
                {
                    Log.WarningOnce("[Modern Dev Tools] knowledge section draw failed: " + e.Message, 0x2E19F02);
                    yEnd = DrawNote(vw, 0f, "MDT_KbNoResults".Translate());
                }
                if (Event.current.type == EventType.Layout) _contentH = yEnd;
            }
            finally { Palette.EndScroll(); }
        }

        private float DrawTabContent(float w)
        {
            switch (_tab)
            {
                case KbTab.Sources: return DrawSources(w);
                case KbTab.KnownIssues: return DrawLibrary(w, false);
                case KbTab.Benign: return DrawLibrary(w, true);
                case KbTab.CommunityBugs: return DrawCommunityBugs(w);
                case KbTab.Compatibility: return DrawCompatibility(w);
                case KbTab.Dependencies: return DrawDependencies(w);
                case KbTab.Harmony: return DrawHarmony(w);
                default: return DrawGlossary(w);
            }
        }

        // --- Sources dashboard ---

        private float DrawSources(float w)
        {
            float y = 0f;
            y = DrawNote(w, y, "MDT_KbSourcesIntro".Translate());
            y += 2f;

            int nonBenign = CountDefs(false);
            y = DrawSourceRow(w, y, "MDT_KbSourceKnown".Translate(), "MDT_KbSourceKnownDesc".Translate(),
                "MDT_KbEntries".Translate(nonBenign), nonBenign > 0 ? Palette.Good : Palette.StripGray, KbTab.KnownIssues);

            int benign = CountDefs(true);
            y = DrawSourceRow(w, y, "MDT_KbSourceBenign".Translate(), "MDT_KbSourceBenignDesc".Translate(),
                "MDT_KbEntries".Translate(benign), benign > 0 ? Palette.Good : Palette.StripGray, KbTab.Benign);

            int commBugs = CommunityData.BugsCount + (ModShippedIssues.All?.Count ?? 0);
            bool anyCommShown = CommunityData.Enabled || (ModShippedIssues.All?.Count ?? 0) > 0;
            string commCount = anyCommShown ? "MDT_KbEntries".Translate(commBugs).ToString() : "MDT_KbSourceOff".Translate().ToString();
            Color commDot = !CommunityData.Enabled ? Palette.StripGray
                : (CommunityData.Loading ? Palette.Warn : (CommunityData.HasData ? Palette.Good : Palette.StripGray));
            y = DrawSourceRow(w, y, "MDT_KbSourceCommunity".Translate(), "MDT_KbSourceCommunityDesc".Translate(),
                commCount, commDot, KbTab.CommunityBugs);

            int compat = Compat().Count;
            y = DrawSourceRow(w, y, "MDT_KbSourceCompat".Translate(), "MDT_KbSourceCompatDesc".Translate(),
                "MDT_KbEntries".Translate(compat), compat > 0 ? Palette.Warn : Palette.Good, KbTab.Compatibility);

            int deps = ModDependencyIndex.All?.Count ?? 0;
            y = DrawSourceRow(w, y, "MDT_KbSourceDeps".Translate(), "MDT_KbSourceDepsDesc".Translate(),
                "MDT_KbEntries".Translate(deps), deps > 0 ? Palette.Warn : Palette.Good, KbTab.Dependencies);

            string harmCount = HarmonyIndex.Built ? "MDT_KbEntries".Translate(HarmonyIndex.BuiltMethodCount).ToString()
                                                  : "MDT_KbNotScanned".Translate().ToString();
            y = DrawSourceRow(w, y, "MDT_KbSourceHarmony".Translate(), "MDT_KbSourceHarmonyDesc".Translate(),
                harmCount, Palette.StripGray, KbTab.Harmony);

            int gloss = Glossary.AllTerms().Count;
            y = DrawSourceRow(w, y, "MDT_KbSourceGlossary".Translate(), "MDT_KbSourceGlossaryDesc".Translate(),
                "MDT_KbEntries".Translate(gloss), Palette.StripGray, KbTab.Glossary);

            return y;
        }

        private float DrawSourceRow(float w, float y, string name, string desc, string countText, Color dot, KbTab target)
        {
            Text.Font = GameFont.Small;
            Text.WordWrap = true;
            float lh = Text.LineHeight;
            float innerW = w - 24f;
            float descH = Mathf.Ceil(Text.CalcHeight(desc, innerW));
            float h = 8f + lh + 2f + descH + 8f;
            Rect card = new Rect(0f, y, w, h);
            bool over = Mouse.IsOver(card);
            Widgets.DrawBoxSolid(card, over ? Color.Lerp(Palette.PanelBG, Palette.BGL, 0.45f) : Palette.PanelBG);
            Palette.DrawBox(card, Palette.BGL, 1);
            Palette.StateStrip(card, dot, 3f);

            float cx = card.x + 12f, cy = card.y + 8f;
            Text.Anchor = TextAnchor.MiddleLeft;
            Text.WordWrap = false;
            GUI.color = Palette.Stat;
            Widgets.Label(new Rect(cx, cy, innerW - 92f, lh), name);
            Text.Anchor = TextAnchor.MiddleRight;
            GUI.color = Palette.TextDim;
            Widgets.Label(new Rect(cx, cy, innerW, lh), countText);
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = true;
            cy += lh + 2f;

            GUI.color = Palette.TextDim;
            Widgets.Label(new Rect(cx, cy, innerW, descH), desc);
            GUI.color = Color.white;

            if (Widgets.ButtonInvisible(card)) Select(target);
            return y + h + 6f;
        }

        // --- Known-issue / benign libraries ---

        private float DrawLibrary(float w, bool benign)
        {
            float y = 0f;
            int total = 0;
            var list = new List<KnownIssueDef>();
            foreach (KnownIssueDef d in DefDatabase<KnownIssueDef>.AllDefsListForReading)
            {
                if (d.benign != benign) continue;
                total++;
                if (!Match(_search, d.label, d.description, d.fix)) continue;
                list.Add(d);
            }
            if (total == 0) return DrawNote(w, y, (benign ? "MDT_KbEmptyBenign" : "MDT_KbEmptyKnown").Translate());
            if (list.Count == 0) return DrawNote(w, y, "MDT_KbNoResults".Translate());

            list.Sort((a, b) => string.Compare(TitleOf(a), TitleOf(b), StringComparison.OrdinalIgnoreCase));
            foreach (KnownIssueDef d in list)
            {
                string sig = benign ? null : SignalText(d.exceptionTypes, d.keywords, d.regexes?.Count ?? 0, d.namespaces, d.packageIds);
                Color strip = benign ? Palette.Good : (d.ignorable ? Palette.StripGray : Palette.Accent);
                y = DrawIssueCard(w, y, TitleOf(d), d.description, benign ? null : d.fix, d.url, TagFor(d), sig, strip);
            }
            return y;
        }

        // --- Community bugs (our DB + mod-shipped) ---

        private float DrawCommunityBugs(float w)
        {
            float y = 0f;

            y = DrawSection(w, y, "MDT_KbSecCommunityDb".Translate());
            if (!CommunityData.Enabled)
            {
                y = DrawCommunityHint(w, y);
            }
            else
            {
                var bugs = CommunityData.AllBugs;
                if (bugs == null || bugs.Count == 0) y = DrawNote(w, y, "MDT_KbNoCommunityBugs".Translate());
                else
                {
                    int shown = 0;
                    foreach (RemoteIssue b in bugs)
                    {
                        if (!Match(_search, b.Title, b.Explanation, b.Fix)) continue;
                        y = DrawRemoteCard(w, y, b, false);
                        shown++;
                    }
                    if (shown == 0) y = DrawNote(w, y, "MDT_KbNoResults".Translate());
                }
            }
            y += 6f;

            y = DrawSection(w, y, "MDT_KbSecShipped".Translate());
            var shipped = ModShippedIssues.All;
            if (shipped == null || shipped.Count == 0) y = DrawNote(w, y, "MDT_KbNoShipped".Translate());
            else
            {
                int shown = 0;
                foreach (RemoteIssue b in shipped)
                {
                    if (!Match(_search, b.Title, b.Explanation, b.Fix, b.ReportedBy)) continue;
                    y = DrawRemoteCard(w, y, b, true);
                    shown++;
                }
                if (shown == 0) y = DrawNote(w, y, "MDT_KbNoResults".Translate());
            }
            return y;
        }

        private float DrawRemoteCard(float w, float y, RemoteIssue b, bool shipped)
        {
            string tag = shipped
                ? "MDT_KbTagShipped".Translate(b.ReportedBy.NullOrEmpty() ? "?" : b.ReportedBy).ToString()
                : (b.ReportedBy.NullOrEmpty() ? "MDT_KbTagCommunityBug".Translate().ToString() : "MDT_CommunityReportedBy".Translate(b.ReportedBy).ToString());
            string sig = SignalText(b.ExceptionTypes, b.Keywords, b.Regexes.Length, b.Namespaces, b.PackageIds);
            return DrawIssueCard(w, y, b.Title, b.Explanation, b.Fix, b.Url, tag, sig, Palette.Accent);
        }

        // --- Compatibility (scoped to installed/active mods) ---

        private float DrawCompatibility(float w)
        {
            CompatData d = Compat();
            float y = 0f;
            bool anyShown = false;

            if (d.About.Count > 0)
            {
                var filtered = new List<AboutIncompatIndex.Pair>();
                foreach (var p in d.About) if (Match(_search, p.AName, p.BName)) filtered.Add(p);
                if (filtered.Count > 0)
                {
                    anyShown = true;
                    y = DrawSection(w, y, "MDT_KbSecAbout".Translate());
                    foreach (var p in filtered)
                        y = DrawIssueCard(w, y, "MDT_KbIncompatPair".Translate(p.AName, p.BName), "MDT_KbBothActive".Translate(),
                            null, null, "MDT_KbTagAbout".Translate(), null, Palette.Warn);
                    y += 6f;
                }
            }

            if (!CommunityData.Enabled)
            {
                y = DrawCommunityHint(w, y);
                return y;
            }

            if (d.Rules.Count > 0)
            {
                var filtered = new List<Pair2>();
                foreach (var p in d.Rules) if (Match(_search, p.A, p.B)) filtered.Add(p);
                if (filtered.Count > 0)
                {
                    anyShown = true;
                    y = DrawSection(w, y, "MDT_KbSecRules".Translate());
                    foreach (var p in filtered)
                        y = DrawIssueCard(w, y, "MDT_KbIncompatPair".Translate(p.A, p.B), "MDT_KbBothActive".Translate(),
                            null, null, "MDT_KbTagCommunityRule".Translate(), null, Palette.Warn);
                    y += 6f;
                }
            }

            if (d.Replace.Count > 0)
            {
                var filtered = new List<RepItem>();
                foreach (var r in d.Replace) if (Match(_search, r.Old, r.New)) filtered.Add(r);
                if (filtered.Count > 0)
                {
                    anyShown = true;
                    y = DrawSection(w, y, "MDT_KbSecReplace".Translate());
                    foreach (var r in filtered)
                    {
                        string body = "MDT_KbReplaceBlurb".Translate().ToString();
                        if (!r.Versions.NullOrEmpty()) body += "\n" + "MDT_KbReplaceVersions".Translate(r.Versions);
                        y = DrawIssueCard(w, y, "MDT_KbReplacePair".Translate(r.Old, r.New), body, null, r.Url,
                            "MDT_KbTagReplace".Translate(), null, Palette.Accent);
                    }
                    y += 6f;
                }
            }

            if (!anyShown)
                y = DrawNote(w, y, _search.NullOrEmpty() ? "MDT_KbNoCompat".Translate() : "MDT_KbNoResults".Translate());
            return y;
        }

        // --- Dependencies and load order ---

        private float DrawDependencies(float w)
        {
            var all = ModDependencyIndex.All;
            float y = 0f;
            if (all == null || all.Count == 0) return DrawNote(w, y, "MDT_KbNoDeps".Translate());

            int shown = 0;
            foreach (ModDependencyIndex.ModProblems prob in all)
            {
                if (!MatchesDep(prob)) continue;
                y = DrawDepCard(w, y, prob);
                shown++;
            }
            if (shown == 0) y = DrawNote(w, y, "MDT_KbNoResults".Translate());
            return y;
        }

        private bool MatchesDep(ModDependencyIndex.ModProblems prob)
        {
            if (Match(_search, prob.ModName)) return true;
            foreach (var md in prob.Missing) if (Match(_search, md.Name, md.PackageId)) return true;
            return false;
        }

        private float DrawDepCard(float w, float y, ModDependencyIndex.ModProblems prob)
        {
            var sb = new StringBuilder();
            foreach (var md in prob.Missing)
            {
                string state = md.Installed ? "MDT_KbDepInstalledInactive".Translate() : "MDT_KbDepNotInstalled".Translate();
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("MDT_KbDepMissingLine".Translate(md.Name + " (" + state + ")"));
            }
            foreach (string lo in prob.LoadOrder)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(lo);
            }
            string url = null;
            foreach (var md in prob.Missing) if (!md.Url.NullOrEmpty()) { url = md.Url; break; }
            return DrawIssueCard(w, y, prob.ModName, sb.ToString(), null, url, "MDT_KbTagYourMods".Translate(), null, Palette.Warn);
        }

        // --- Harmony patch registry ---

        private float DrawHarmony(float w)
        {
            if (_harmony == null) _harmony = BuildHarmony();
            float y = 0f;
            y = DrawNote(w, y, "MDT_KbHarmonySummary".Translate(HarmonyIndex.PatchedMethodCount, _harmony.Count));

            if (_harmony.Count == 0) return DrawNote(w, y, "MDT_KbNoConflicts".Translate());

            var list = new List<HConflict>();
            foreach (HConflict h in _harmony)
                if (Match(_search, h.Method, h.Full) || MatchOwners(_search, h.Owners)) list.Add(h);
            if (list.Count == 0) return DrawNote(w, y, "MDT_KbNoResults".Translate());

            const int cap = 300;
            if (list.Count > cap) y = DrawNote(w, y, "MDT_KbHarmonyShowing".Translate(cap, list.Count));

            int n = 0;
            foreach (HConflict h in list)
            {
                if (n >= cap) break;
                string body = "MDT_KbHarmonyPatchers".Translate(string.Join(", ", h.Owners.ToArray()));
                y = DrawIssueCard(w, y, h.Method, body, null, null, "MDT_KbTagHarmony".Translate(), h.Full, Palette.Warn);
                n++;
            }
            return y;
        }

        private List<HConflict> BuildHarmony()
        {
            var res = new List<HConflict>();
            InstalledModIndex idx = InstalledModIndex.Instance;
            foreach (var kv in HarmonyIndex.Snapshot())
            {
                var names = new List<string>();
                foreach (string o in kv.Value)
                {
                    if (o.NullOrEmpty()) continue;
                    if (string.Equals(o, HarmonyIndex.SelfId, StringComparison.OrdinalIgnoreCase)) continue;
                    ModMetaData meta = idx?.MatchOwnerId(o);
                    string nm = meta != null ? meta.Name : o;
                    if (!names.Contains(nm)) names.Add(nm);
                }
                if (names.Count < 2) continue;   // only methods contested by 2+ distinct foreign mods
                res.Add(new HConflict { Method = Pretty(kv.Key), Full = kv.Key, Owners = names });
            }
            res.Sort((a, b) =>
            {
                int c = b.Owners.Count.CompareTo(a.Owners.Count);
                return c != 0 ? c : string.CompareOrdinal(a.Method, b.Method);
            });
            return res;
        }

        // --- Glossary ---

        private float DrawGlossary(float w)
        {
            float y = 0f;
            int shown = 0;
            foreach (var t in Glossary.AllTerms())
            {
                if (!Match(_search, t.Key, t.Value)) continue;
                y = DrawGlossaryCard(w, y, t.Key, t.Value);
                shown++;
            }
            if (shown == 0) return DrawNote(w, y, "MDT_KbNoResults".Translate());
            return y;
        }

        private float DrawGlossaryCard(float w, float y, string label, string def)
        {
            Text.Font = GameFont.Small;
            Text.WordWrap = true;
            float lh = Text.LineHeight;
            float innerW = w - 24f;
            float defH = Mathf.Ceil(Text.CalcHeight(def, innerW));
            float h = 8f + lh + 4f + defH + 8f;
            Rect card = new Rect(0f, y, w, h);
            Palette.DrawCard(card);
            Palette.StateStrip(card, Palette.StripGray, 3f);

            float cx = card.x + 12f, cy = card.y + 8f;
            Text.WordWrap = false;
            GUI.color = Palette.Stat;
            Widgets.Label(new Rect(cx, cy, innerW, lh), label);
            GUI.color = Color.white;
            Text.WordWrap = true;
            cy += lh + 4f;

            GUI.color = Palette.TextDim;
            Widgets.Label(new Rect(cx, cy, innerW, defH), def);
            GUI.color = Color.white;
            return y + h + 6f;
        }

        // --- shared card + notes ---

        private static float DrawIssueCard(float w, float y, string title, string body, string fix, string url, string tag, string signals, Color strip)
        {
            Text.Font = GameFont.Small;
            Text.WordWrap = true;
            float lh = Text.LineHeight;
            float innerW = w - 24f;

            bool hasTag = !tag.NullOrEmpty(), hasBody = !body.NullOrEmpty(), hasFix = !fix.NullOrEmpty(),
                 hasSig = !signals.NullOrEmpty(), hasUrl = !url.NullOrEmpty();
            float titleH = Mathf.Ceil(Text.CalcHeight(title ?? "", innerW));
            float tagH = hasTag ? lh : 0f;
            float bodyH = hasBody ? Mathf.Ceil(Text.CalcHeight(body, innerW)) : 0f;
            string fixLine = hasFix ? ("MDT_FixLabel".Translate() + " " + fix) : "";
            float fixH = hasFix ? Mathf.Ceil(Text.CalcHeight(fixLine, innerW)) : 0f;
            float sigH = hasSig ? Mathf.Ceil(Text.CalcHeight(signals, innerW)) : 0f;
            float urlH = hasUrl ? lh : 0f;

            float cardH = 8f + titleH + (hasTag ? 2f + tagH : 0f) + (hasBody ? 6f + bodyH : 0f)
                        + (hasFix ? 6f + fixH : 0f) + (hasSig ? 6f + sigH : 0f) + (hasUrl ? 4f + urlH : 0f) + 8f;
            Rect card = new Rect(0f, y, w, cardH);
            Palette.DrawCard(card);
            Palette.StateStrip(card, strip, 3f);

            float cx = card.x + 12f, cy = card.y + 8f;
            GUI.color = Palette.Stat;
            Widgets.Label(new Rect(cx, cy, innerW, titleH), title ?? "");
            GUI.color = Color.white;
            cy += titleH;

            if (hasTag) { cy += 2f; Palette.LabelFit(new Rect(cx, cy, innerW, tagH), tag, Palette.TextDim); cy += tagH; }
            if (hasBody) { cy += 6f; GUI.color = Palette.TextDim; Widgets.Label(new Rect(cx, cy, innerW, bodyH), body); GUI.color = Color.white; cy += bodyH; }
            if (hasFix) { cy += 6f; GUI.color = Palette.Stat; Widgets.Label(new Rect(cx, cy, innerW, fixH), fixLine); GUI.color = Color.white; cy += fixH; }
            if (hasSig) { cy += 6f; GUI.color = Palette.TextDim; Widgets.Label(new Rect(cx, cy, innerW, sigH), signals); GUI.color = Color.white; cy += sigH; }
            if (hasUrl)
            {
                cy += 4f;
                Rect ur = new Rect(cx, cy, innerW, urlH);
                Text.WordWrap = false;
                GUI.color = Palette.Accent;
                Widgets.Label(ur, url);
                float ulW = Mathf.Min(Text.CalcSize(url).x, innerW);
                Widgets.DrawBoxSolid(new Rect(ur.x, ur.yMax - 3f, ulW, 1f), Palette.Accent);
                GUI.color = Color.white;
                Text.WordWrap = true;
                if (Mouse.IsOver(ur)) TooltipHandler.TipRegion(ur, url);
                if (Widgets.ButtonInvisible(ur)) Application.OpenURL(url);
            }

            return y + cardH + 6f;
        }

        private static float DrawSection(float w, float y, string label)
        {
            Palette.SectionHeader(new Rect(0f, y, w, 24f), label);
            return y + 24f + 6f;
        }

        private static float DrawNote(float w, float y, string text)
        {
            Text.Font = GameFont.Small;
            Text.WordWrap = true;
            Text.Anchor = TextAnchor.UpperLeft;
            float h = Mathf.Ceil(Text.CalcHeight(text, w));
            GUI.color = Palette.TextDim;
            Widgets.Label(new Rect(0f, y, w, h), text);
            GUI.color = Color.white;
            return y + h + 8f;
        }

        private float DrawCommunityHint(float w, float y)
        {
            Text.Font = GameFont.Small;
            Text.WordWrap = true;
            string txt = "MDT_KbCommOff".Translate();
            float innerW = w - 24f;
            float th = Mathf.Ceil(Text.CalcHeight(txt, innerW));
            float h = 8f + th + 8f + 26f + 8f;
            Rect card = new Rect(0f, y, w, h);
            Palette.DrawCard(card);
            Palette.StateStrip(card, Palette.Accent, 3f);

            float cx = card.x + 12f, cy = card.y + 8f;
            GUI.color = Palette.TextDim;
            Widgets.Label(new Rect(cx, cy, innerW, th), txt);
            GUI.color = Color.white;
            cy += th + 8f;
            if (Palette.GrayButton(new Rect(cx, cy, 180f, 26f), "MDT_KbCommEnable".Translate(), "MDT_CommUpdateTip".Translate(), !CommunityData.Loading))
                EnableAndUpdate();
            return y + h + 6f;
        }

        private void DrawCommunityControl(Rect r)
        {
            var s = ModernDevToolsMod.Settings;
            bool enabled = s != null && s.enableCommunityData;
            const float bw = 170f;
            Rect btn = new Rect(r.xMax - bw, r.y, bw, r.height);
            Palette.LabelFit(new Rect(r.x, r.y, r.width - bw - 8f, r.height), CommStatus(), Palette.TextDim);
            string label = !enabled ? "MDT_KbCommEnable".Translate()
                : (CommunityData.Loading ? "MDT_CommUpdating".Translate() : "MDT_CommUpdate".Translate());
            if (Palette.GrayButton(btn, label, "MDT_CommUpdateTip".Translate(), !CommunityData.Loading))
                EnableAndUpdate();
        }

        private void EnableAndUpdate()
        {
            var s = ModernDevToolsMod.Settings;
            if (s != null && !s.enableCommunityData)
            {
                s.enableCommunityData = true;
                ModernDevToolsMod.Instance?.WriteSettings();
                Messages.Message("MDT_CommEnabledMsg".Translate(), MessageTypeDefOf.TaskCompletion, false);
            }
            CommunityData.Update();
            _compat = null;   // recompute compat with fresh data next draw
        }

        // --- data helpers ---

        private CompatData Compat()
        {
            long stamp = ((CommunityData.LastUpdated?.Ticks) ?? 0L) ^ (CommunityData.Enabled ? 1L : 0L) ^ ((long)CommunityData.BugsCount << 8);
            if (_compat == null || _compatStamp != stamp) { _compat = BuildCompat(); _compatStamp = stamp; }
            return _compat;
        }

        private static CompatData BuildCompat()
        {
            var d = new CompatData();
            try
            {
                d.About.AddRange(AboutIncompatIndex.ActivePairs);
                var seen = new HashSet<string>();
                foreach (var p in d.About) seen.Add(PairKey(p.APid, p.BPid));

                InstalledModIndex idx = InstalledModIndex.Instance;
                if (CommunityData.Enabled)
                {
                    foreach (ModMetaData m in ModsConfig.ActiveModsInLoadOrder)
                    {
                        if (m == null || m.PackageId.NullOrEmpty()) continue;
                        CommRule rule = CommunityData.RuleFor(m.PackageId);
                        if (rule == null) continue;
                        foreach (string other in rule.Incompat)
                        {
                            if (other.NullOrEmpty() || !ModsConfig.IsActive(other)) continue;   // scope: both active
                            if (!seen.Add(PairKey(m.PackageId, other))) continue;
                            string bName = idx?.PackageId(other)?.Name ?? other;
                            d.Rules.Add(new Pair2 { A = m.Name, B = bName });
                        }
                    }

                    foreach (ModMetaData meta in ModLister.AllInstalledMods)
                    {
                        if (meta == null) continue;
                        Replacement rep = CommunityData.ReplacementFor(meta);
                        if (rep == null) continue;
                        string url = !rep.NewWorkshopId.NullOrEmpty()
                            ? "https://steamcommunity.com/sharedfiles/filedetails/?id=" + rep.NewWorkshopId : null;
                        d.Replace.Add(new RepItem
                        {
                            Old = meta.Name,
                            New = rep.NewName.NullOrEmpty() ? rep.OldName : rep.NewName,
                            Url = url,
                            Versions = (rep.NewVersions != null && rep.NewVersions.Count > 0) ? string.Join(", ", rep.NewVersions.ToArray()) : null
                        });
                    }
                }
            }
            catch (Exception e) { Log.WarningOnce("[Modern Dev Tools] knowledge compat build failed: " + e.Message, 0x2E19F01); }
            return d;
        }

        private static int CountDefs(bool benign)
        {
            int n = 0;
            foreach (KnownIssueDef d in DefDatabase<KnownIssueDef>.AllDefsListForReading) if (d.benign == benign) n++;
            return n;
        }

        private static string TitleOf(KnownIssueDef d) => d.label.NullOrEmpty() ? d.defName : d.LabelCap.ToString();

        private static string TagFor(KnownIssueDef d)
        {
            var pack = d.modContentPack;
            if (pack == null || pack.PackageId.NullOrEmpty() || string.Equals(pack.PackageId, "astryl.moderndevtools", StringComparison.OrdinalIgnoreCase))
                return "MDT_TagBuiltIn".Translate();
            return "MDT_KbTagShipped".Translate(pack.Name);
        }

        private static string SignalText(IEnumerable<string> exTypes, IEnumerable<string> keywords, int regexCount, IEnumerable<string> namespaces, IEnumerable<string> pids)
        {
            var parts = new List<string>();
            string ex = Cap(exTypes, 4); if (!ex.NullOrEmpty()) parts.Add(ex);
            string kw = Cap(keywords, 3); if (!kw.NullOrEmpty()) parts.Add("keyword " + kw);
            if (regexCount > 0) parts.Add(regexCount == 1 ? "1 pattern" : regexCount + " patterns");
            string ns = Cap(namespaces, 2); if (!ns.NullOrEmpty()) parts.Add("namespace " + ns);
            string pd = Cap(pids, 2); if (!pd.NullOrEmpty()) parts.Add("mod " + pd);
            if (parts.Count == 0) return null;
            string s = string.Join(" - ", parts.ToArray());
            if (s.Length > 170) s = s.Substring(0, 170) + "...";
            return "MDT_KbSignals".Translate(s);
        }

        private static string Cap(IEnumerable<string> items, int max)
        {
            if (items == null) return null;
            var list = new List<string>();
            foreach (string it in items) { if (it.NullOrEmpty()) continue; list.Add(it); if (list.Count >= max) break; }
            return list.Count == 0 ? null : string.Join(", ", list.ToArray());
        }

        private static string Pretty(string methodKey)
        {
            if (methodKey.NullOrEmpty()) return methodKey;
            int c = methodKey.IndexOf(':');
            string type = c >= 0 ? methodKey.Substring(0, c) : methodKey;
            string method = c >= 0 ? methodKey.Substring(c + 1) : "";
            int dot = type.LastIndexOf('.');
            string shortType = (dot >= 0 && dot < type.Length - 1) ? type.Substring(dot + 1) : type;
            return method.NullOrEmpty() ? shortType : shortType + "." + method;
        }

        private static string PairKey(string a, string b)
        {
            string la = (a ?? "").ToLowerInvariant(), lb = (b ?? "").ToLowerInvariant();
            return string.CompareOrdinal(la, lb) <= 0 ? la + "|" + lb : lb + "|" + la;
        }

        private static bool Match(string q, params string[] fields)
        {
            if (q.NullOrEmpty()) return true;
            foreach (string f in fields)
                if (!f.NullOrEmpty() && f.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static bool MatchOwners(string q, List<string> owners)
        {
            if (q.NullOrEmpty()) return true;
            foreach (string o in owners)
                if (!o.NullOrEmpty() && o.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static string CommStatus()
        {
            var s = ModernDevToolsMod.Settings;
            if (s == null || !s.enableCommunityData) return "MDT_CommDisabled".Translate();
            if (CommunityData.Loading) return "MDT_CommUpdating".Translate();
            if (!CommunityData.LastError.NullOrEmpty()) return "MDT_CommError".Translate(CommunityData.LastError);
            if (CommunityData.LastUpdated.HasValue) return "MDT_CommUpdated".Translate(CommunityData.LastUpdated.Value.ToString("yyyy-MM-dd HH:mm"));
            if (CommunityData.HasData) return "MDT_CommCached".Translate();
            return "MDT_CommNoData".Translate();
        }

        // --- small models ---

        private class Pair2 { public string A; public string B; }
        private class RepItem { public string Old; public string New; public string Url; public string Versions; }
        private class HConflict { public string Method; public string Full; public List<string> Owners; }

        private class CompatData
        {
            public readonly List<AboutIncompatIndex.Pair> About = new List<AboutIncompatIndex.Pair>();
            public readonly List<Pair2> Rules = new List<Pair2>();
            public readonly List<RepItem> Replace = new List<RepItem>();
            public int Count => About.Count + Rules.Count + Replace.Count;
        }
    }
}
