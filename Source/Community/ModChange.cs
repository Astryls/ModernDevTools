using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Verse;

namespace ModernDevTools
{
    public enum ChangeKind { Updated, Removed, Added }

    public struct ModChangeEntry
    {
        public string Name;
        public ChangeKind Kind;
    }

    /// <summary>Result of comparing the mods a save was made with against the mods loaded now.</summary>
    public static class ModChange
    {
        public static List<ModChangeEntry> Report = new List<ModChangeEntry>();
        public static bool HasChanges => Report.Count > 0;

        public static void Set(List<ModChangeEntry> report) => Report = report ?? new List<ModChangeEntry>();
        public static void Clear() => Report = new List<ModChangeEntry>();
    }

    /// <summary>
    /// Snapshots the active mods (and each mod folder's write time) into the save, then on load diffs
    /// that snapshot against the current mods so the log window can warn about mods that were updated,
    /// removed, or added since the save was made - a top cause of "my save is throwing errors".
    /// Auto-registered by the engine (every GameComponent with a (Game) ctor is instantiated).
    /// </summary>
    public class GameComponent_ModSnapshot : GameComponent
    {
        private Dictionary<string, string> saved = new Dictionary<string, string>();

        public GameComponent_ModSnapshot(Game game) { }

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.Saving) saved = CurrentTokens();
            Scribe_Collections.Look(ref saved, "mdtModSnapshot", LookMode.Value, LookMode.Value);
            if (saved == null) saved = new Dictionary<string, string>();
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            try
            {
                var report = new List<ModChangeEntry>();
                if (saved != null && saved.Count > 0)
                {
                    var cur = CurrentTokens();
                    foreach (var kv in saved)
                    {
                        if (!cur.TryGetValue(kv.Key, out string t)) report.Add(new ModChangeEntry { Name = NameOf(kv.Key), Kind = ChangeKind.Removed });
                        else if (t != kv.Value) report.Add(new ModChangeEntry { Name = NameOf(kv.Key), Kind = ChangeKind.Updated });
                    }
                    foreach (var kv in cur)
                        if (!saved.ContainsKey(kv.Key)) report.Add(new ModChangeEntry { Name = NameOf(kv.Key), Kind = ChangeKind.Added });
                }
                ModChange.Set(report);
            }
            catch (Exception e) { Log.Warning("[Modern Dev Tools] mod-change diff failed: " + e.Message); }
        }

        private static Dictionary<string, string> CurrentTokens()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (ModContentPack mcp in LoadedModManager.RunningModsListForReading)
                {
                    string pid = mcp.PackageId;
                    if (pid.NullOrEmpty() || pid.StartsWith("ludeon.", StringComparison.OrdinalIgnoreCase)) continue;
                    string token = "";
                    try { if (!mcp.RootDir.NullOrEmpty() && Directory.Exists(mcp.RootDir)) token = Directory.GetLastWriteTimeUtc(mcp.RootDir).Ticks.ToString(); }
                    catch { }
                    d[pid] = token;
                }
            }
            catch (Exception e) { Log.Warning("[Modern Dev Tools] mod token scan failed: " + e.Message); }
            return d;
        }

        private static string NameOf(string pid)
        {
            try { return ModLister.GetModWithIdentifier(pid, true)?.Name ?? pid; }
            catch { return pid; }
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

                var report = ModChange.Report;
                float rowH = 26f;
                Rect outR = new Rect(content.x, y, content.width, content.yMax - y);
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
                    }
                }
                finally { Palette.EndScroll(); }
            }
            catch (Exception ex) { Log.ErrorOnce("[Modern Dev Tools] mod-changes draw failed: " + ex, 0x2E19C40); }
            finally { Palette.ResetGuiState(); }
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
