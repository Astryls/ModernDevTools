using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace ModernDevTools
{
    public enum ChangeKind { Updated, Removed, Added }

    public struct ModChangeEntry
    {
        public string Name;
        public string PackageId;
        public ChangeKind Kind;
        public string Evidence;   // why we say so, shown on hover - no claim without a stated reason
    }

    /// <summary>
    /// Result of comparing the mods a save was made with against the mods loaded now.
    ///
    /// Everything here is user-facing fact that people act on ("updated mods are the usual cause of
    /// errors on an existing save"), so the rule is FAIL CLOSED: when the evidence for a mod is
    /// incomplete it is counted as unverified and never reported as changed. Silence is correct;
    /// a false accusation is not.
    /// </summary>
    public static class ModChange
    {
        public static List<ModChangeEntry> Report = new List<ModChangeEntry>();

        /// <summary>Mods present on both sides whose content could not be compared (not yet scanned,
        /// unreadable folder, or official content). Surfaced in the UI - never silently dropped.</summary>
        public static int Unverified;

        /// <summary>True when the save carried a snapshot this build can actually compare against.</summary>
        public static bool Usable;

        /// <summary>True when we are working from a PRE-FINGERPRINT snapshot: only added/removed mods
        /// can be determined, never "updated". Surfaced in the UI so the list is not read as complete.</summary>
        public static bool PresenceOnly;

        /// <summary>True while the content scan is still running, so "updated" results are incomplete.</summary>
        public static bool Pending;

        public static bool HasChanges => Report.Count > 0;

        private static Dictionary<string, string> _saved;   // normalized packageId -> "fingerprint|name"
        private static bool _armed;

        /// <summary>Take the snapshot loaded from a save and produce the diff. Called once per game load.</summary>
        public static void Arm(Dictionary<string, string> saved, int savedVersion, Dictionary<string, string> legacy = null)
        {
            Clear();
            try
            {
                if (saved != null && saved.Count > 0 && savedVersion == ModFingerprint.AlgorithmVersion)
                {
                    _saved = saved;
                }
                else
                {
                    // The pre-1.3 snapshot stored folder write-time TICKS, which are not comparable with
                    // fingerprints - comparing them would flag every mod in the list. But its KEYS are
                    // still perfectly good packageIds, and presence needs no fingerprint at all.
                    //
                    // Discarding the whole node threw away usable evidence with the untrustworthy part:
                    // in test run #232 a save whose mods had genuinely been removed (RimVali FFA,
                    // WorkShift, Faction Colonies) produced a wall of cross-reference errors while this
                    // panel stayed silent - the one case it exists for. So fall back to presence-only.
                    _saved = FromLegacy(legacy);
                    if (_saved == null) return;      // nothing usable on either side: say nothing
                    PresenceOnly = true;
                }
                _armed = true;
                Usable = true;
                Recompute();
            }
            catch (Exception e)
            {
                Clear();
                Log.Warning("[Modern Dev Tools] mod-change diff failed: " + e.Message);
            }
        }

        /// <summary>Driven from the permanent Root.Update postfix: the fingerprint scan runs in the
        /// background, so the "updated" half of the diff is finished as soon as it lands.</summary>
        public static void TickPending()
        {
            if (!_armed || !Pending || !ModFingerprint.Ready) return;
            try { Recompute(); }
            catch (Exception e)
            {
                Pending = false;
                Log.WarningOnce("[Modern Dev Tools] mod-change recompute failed: " + e.Message, 0x2E19E03);
            }
        }

        public static void Clear()
        {
            Report = new List<ModChangeEntry>();
            Unverified = 0;
            Usable = false;
            Pending = false;
            PresenceOnly = false;
            _saved = null;
            _armed = false;
        }

        /// <summary>Convert a pre-1.3 snapshot (packageId -> folder write-time ticks) into the current
        /// shape, keeping ONLY the identity. Every fingerprint is left empty, so Recompute can report
        /// added/removed but is structurally incapable of claiming "updated" from this data.</summary>
        private static Dictionary<string, string> FromLegacy(Dictionary<string, string> legacy)
        {
            if (legacy == null || legacy.Count == 0) return null;
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in legacy)
            {
                string norm = ModFingerprint.NormalizeId(kv.Key);
                if (norm == null || d.ContainsKey(norm)) continue;
                d[norm] = "|" + (ResolveName(kv.Key) ?? ResolveName(norm) ?? norm);
            }
            return d.Count > 0 ? d : null;
        }

        /// <summary>Best-effort display name for a packageId that is NOT currently loaded (it may still
        /// be installed but inactive). Deliberately the EXACT, postfix-sensitive lookup: the
        /// ignorePostfix variant returns ElementAtOrDefault(0) across every copy sharing an id, which is
        /// the local copy whenever a mod exists both locally and on Steam - i.e. potentially a different
        /// mod than the one meant. Falling back to the raw packageId is honest; guessing is not.</summary>
        private static string ResolveName(string pid)
        {
            if (pid.NullOrEmpty()) return null;
            try
            {
                string n = ModLister.GetModWithIdentifier(pid)?.Name;
                return n.NullOrEmpty() ? null : n;
            }
            catch { return null; }
        }

        /// <summary>Current running mods keyed the same way the snapshot is: normalized packageId ->
        /// "fingerprint|name". Fingerprint is empty when unknown.</summary>
        public static Dictionary<string, string> BuildSnapshot()
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                foreach (ModContentPack mcp in LoadedModManager.RunningModsListForReading)
                {
                    if (mcp == null) continue;
                    string id = ModFingerprint.NormalizeId(mcp.PackageId);
                    if (id == null || d.ContainsKey(id)) continue;
                    // The name is recorded HERE, from the pack that is actually loaded. It is never
                    // re-derived later from the packageId: ModLister.GetModWithIdentifier(id,
                    // ignorePostfix: true) returns ElementAtOrDefault(0) of every copy sharing that id
                    // - which is the LOCAL copy whenever a mod exists both locally and on Steam, and
                    // null outright for any id carrying the "_steam" postfix (that dictionary is keyed
                    // on the stripped id). Either way it can name a different mod than the one we mean.
                    string name = mcp.Name.NullOrEmpty() ? id : mcp.Name;
                    string fp = ModFingerprint.IsOfficial(id) ? "" : (ModFingerprint.Get(id) ?? "");
                    d[id] = fp + "|" + name;
                }
            }
            catch (Exception e)
            {
                Log.Warning("[Modern Dev Tools] mod snapshot failed: " + e.Message);
            }
            return d;
        }

        private static void Split(string packed, out string fingerprint, out string name)
        {
            fingerprint = ""; name = "";
            if (packed == null) return;
            int bar = packed.IndexOf('|');          // first separator only: names may contain '|'
            if (bar < 0) { name = packed; return; }
            fingerprint = packed.Substring(0, bar);
            name = packed.Substring(bar + 1);
        }

        private static void Recompute()
        {
            var report = new List<ModChangeEntry>();
            int unverified = 0;
            Dictionary<string, string> cur = BuildSnapshot();

            foreach (var kv in _saved)
            {
                Split(kv.Value, out string savedFp, out string savedName);
                if (!cur.TryGetValue(kv.Key, out string curPacked))
                {
                    // Presence is directly observable and needs no fingerprint.
                    report.Add(new ModChangeEntry
                    {
                        Name = savedName.NullOrEmpty() ? kv.Key : savedName,
                        PackageId = kv.Key,
                        Kind = ChangeKind.Removed,
                        Evidence = "MDT_EvidenceRemoved".Translate(kv.Key)
                    });
                    continue;
                }

                Split(curPacked, out string curFp, out string curName);
                if (savedFp.NullOrEmpty() || curFp.NullOrEmpty()) { unverified++; continue; }
                if (string.Equals(savedFp, curFp, StringComparison.Ordinal)) continue;

                report.Add(new ModChangeEntry
                {
                    Name = curName.NullOrEmpty() ? kv.Key : curName,
                    PackageId = kv.Key,
                    Kind = ChangeKind.Updated,
                    Evidence = "MDT_EvidenceUpdated".Translate(kv.Key)
                });
            }

            foreach (var kv in cur)
            {
                if (_saved.ContainsKey(kv.Key)) continue;
                Split(kv.Value, out _, out string curName);
                report.Add(new ModChangeEntry
                {
                    Name = curName.NullOrEmpty() ? kv.Key : curName,
                    PackageId = kv.Key,
                    Kind = ChangeKind.Added,
                    Evidence = "MDT_EvidenceAdded".Translate(kv.Key)
                });
            }

            report.Sort((a, b) => a.Kind != b.Kind
                ? a.Kind.CompareTo(b.Kind)
                : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            Report = report;
            Unverified = unverified;
            // In presence-only mode no fingerprint can ever arrive for the saved side, so waiting on the
            // scan would just re-run this diff forever for a result that cannot change.
            Pending = !PresenceOnly && !ModFingerprint.Ready;
        }
    }

    /// <summary>
    /// Snapshots the active mods (and a content fingerprint of each) into the save, then on load diffs
    /// that snapshot against the mods loaded now, so the log window can warn about mods that were
    /// updated, removed, or added since the save was made - a top cause of "my save is throwing errors".
    /// Auto-registered by the engine (every GameComponent with a (Game) ctor is instantiated).
    /// </summary>
    public class GameComponent_ModSnapshot : GameComponent
    {
        private Dictionary<string, string> saved = new Dictionary<string, string>();
        private Dictionary<string, string> savedLegacy = new Dictionary<string, string>();
        private int savedVersion;

        public GameComponent_ModSnapshot(Game game) { }

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                saved = ModChange.BuildSnapshot();
                savedVersion = ModFingerprint.AlgorithmVersion;
            }
            // Deliberately a NEW node name. The pre-1.3 snapshot stored folder write-time ticks under
            // "mdtModSnapshot"; comparing those against fingerprints would flag every mod in the list.
            Scribe_Values.Look(ref savedVersion, "mdtSnapshotVersion", 0);
            Scribe_Collections.Look(ref saved, "mdtModSnapshotV2", LookMode.Value, LookMode.Value);
            if (saved == null) saved = new Dictionary<string, string>();

            // Read the legacy node on LOAD only - its keys still identify which mods the save was made
            // with, which is enough for added/removed. Guarded so we never WRITE it back: saving it
            // would resurrect the timestamp format we just retired.
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                Scribe_Collections.Look(ref savedLegacy, "mdtModSnapshot", LookMode.Value, LookMode.Value);
                if (savedLegacy == null) savedLegacy = new Dictionary<string, string>();
            }
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            try
            {
                if (ModernDevToolsMod.Settings != null && !ModernDevToolsMod.Settings.detectModChanges)
                {
                    ModChange.Clear();
                    return;
                }
                ModFingerprint.Begin();               // no-op if it already ran at startup
                ModChange.Arm(saved, savedVersion, savedLegacy);
            }
            catch (Exception e) { Log.Warning("[Modern Dev Tools] mod-change init failed: " + e.Message); }
        }
    }

    /// <summary>Small suite-styled list of the mods that changed since the save was made.</summary>
    public class Dialog_ModChanges : Window
    {
        private Vector2 _scroll;

        public Dialog_ModChanges()
        {
            doWindowBackground = false;
            doCloseX = false;
            draggable = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = true;
            closeOnCancel = true;
        }

        protected override float Margin => 0f;
        public override Vector2 InitialSize => new Vector2(460f, 420f);

        public override void DoWindowContents(Rect inRect)
        {
            try
            {
                Widgets.DrawBoxSolid(inRect, Palette.BG);
                Palette.DrawBox(inRect, Palette.BGL, 1);
                Rect content = inRect.ContractedBy(14f);

                Text.Font = GameFont.Medium;
                Text.Anchor = TextAnchor.MiddleLeft;
                GUI.color = Palette.Stat;
                Widgets.Label(new Rect(content.x, content.y, content.width - 28f, 32f), "MDT_ModsChangedTitle".Translate());
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.UpperLeft;
                if (Palette.CloseX(new Rect(content.xMax - 24f, content.y + 4f, 22f, 22f))) Close();

                float y = content.y + 40f;
                GUI.color = Palette.TextDim;
                Text.WordWrap = true;
                float introH = Mathf.Ceil(TextMetrics.Height("MDT_ModsChangedIntro".Translate(), content.width));
                Widgets.Label(new Rect(content.x, y, content.width, introH), "MDT_ModsChangedIntro".Translate());
                GUI.color = Color.white;
                y += introH + 8f;

                // Anything that qualifies the result is stated up front rather than left implied.
                string note = FooterNote();
                float noteH = 0f;
                if (note != null) noteH = Mathf.Ceil(TextMetrics.Height(note, content.width)) + 6f;

                var report = ModChange.Report;
                float rowH = 26f;
                Rect outR = new Rect(content.x, y, content.width, content.yMax - y - noteH);
                Palette.DrawWell(outR);
                Rect inner = outR.ContractedBy(6f);
                Rect view = new Rect(0f, 0f, inner.width - 16f, Mathf.Max(report.Count * rowH, inner.height));
                Palette.BeginScroll(inner, ref _scroll, view);
                try
                {
                    for (int i = 0; i < report.Count; i++)
                    {
                        var e = report[i];
                        Rect row = new Rect(0f, i * rowH, view.width, rowH - 2f);
                        if ((i & 1) == 1) Widgets.DrawBoxSolid(row, Palette.RowAlt);
                        Color c = e.Kind == ChangeKind.Removed ? Palette.Bad : (e.Kind == ChangeKind.Updated ? Palette.Warn : Palette.TextDim);
                        Palette.StateStrip(row, c, 3f);
                        Palette.LabelFit(new Rect(row.x + 10f, row.y, row.width - 100f, row.height), e.Name, Palette.Stat);
                        Text.Anchor = TextAnchor.MiddleRight;
                        Text.WordWrap = false;
                        GUI.color = c;
                        Widgets.Label(new Rect(row.x, row.y, row.width - 8f, row.height), KindLabel(e.Kind));
                        GUI.color = Color.white;
                        Text.Anchor = TextAnchor.UpperLeft;
                        Text.WordWrap = true;
                        if (!e.Evidence.NullOrEmpty()) TooltipHandler.TipRegion(row, e.Evidence);
                    }
                }
                finally { Palette.EndScroll(); }

                if (note != null)
                {
                    GUI.color = Palette.TextDim;
                    Widgets.Label(new Rect(content.x, outR.yMax + 4f, content.width, noteH), note);
                    GUI.color = Color.white;
                }
            }
            catch (Exception ex) { Log.ErrorOnce("[Modern Dev Tools] mod-changes draw failed: " + ex, 0x2E19C40); }
            finally { Palette.ResetGuiState(); }
        }

        private static string FooterNote()
        {
            if (ModChange.PresenceOnly) return "MDT_ModsChangedPresenceOnly".Translate();
            if (ModChange.Pending) return "MDT_ModsChangedPending".Translate();
            if (ModChange.Unverified > 0) return "MDT_ModsChangedUnverified".Translate(ModChange.Unverified);
            return null;
        }

        private static string KindLabel(ChangeKind k)
        {
            switch (k)
            {
                case ChangeKind.Removed: return "MDT_ChangeRemoved".Translate();
                case ChangeKind.Updated: return "MDT_ChangeUpdated".Translate();
                default: return "MDT_ChangeAdded".Translate();
            }
        }
    }
}
