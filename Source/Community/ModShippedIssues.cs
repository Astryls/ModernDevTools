using System;
using System.Collections.Generic;
using System.IO;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// Loads known-issue entries that mod AUTHORS ship inside their own mod, so the log can explain their
    /// errors without them writing C# or a KnownIssueDef (and with no LoadFolders trickery). A mod just
    /// drops a file at:
    ///
    ///     &lt;ModRoot&gt;/About/known-issues.json       (canonical)
    ///     &lt;ModRoot&gt;/About/ModernDevTools.json      (also accepted)
    ///
    /// The About folder is ideal: RimWorld ignores files there other than About.xml/Preview.png/
    /// PublishedFileId.txt, so the file needs no LoadFolders setup and ships harmlessly to everyone.
    ///
    /// using the EXACT same schema as the community database (known-issues.json). These are local,
    /// always-on (no internet, no opt-in), and rank ABOVE the community repo - which is the fallback
    /// when a mod ships no file, or its file does not match the error.
    ///
    /// Scanned once at startup; the file being present does not require Modern Dev Tools as a dependency,
    /// so shipping one is safe for players who do not have this mod.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class ModShippedIssues
    {
        private static readonly string[] CandidatePaths =
        {
            Path.Combine("About", "known-issues.json"),
            Path.Combine("About", "ModernDevTools.json"),
        };

        private static List<RemoteIssue> _all;

        static ModShippedIssues() { Load(); }

        public static List<RemoteIssue> All { get { if (_all == null) Load(); return _all; } }

        public static void Load()
        {
            var list = new List<RemoteIssue>();
            try
            {
                foreach (ModContentPack mcp in LoadedModManager.RunningModsListForReading)
                {
                    string root = mcp?.RootDir;
                    if (root.NullOrEmpty() || !Directory.Exists(root)) continue;

                    foreach (string rel in CandidatePaths)
                    {
                        string path = Path.Combine(root, rel);
                        if (!File.Exists(path)) continue;
                        try
                        {
                            List<RemoteIssue> issues = CommunityData.ParseIssues(StripBom(File.ReadAllText(path)));
                            foreach (RemoteIssue ri in issues)
                            {
                                if (ri.ReportedBy.NullOrEmpty()) ri.ReportedBy = mcp.Name;   // credit the shipping mod
                                ri.OwnerPackageId = mcp.PackageId;   // and RECORD it, so the entry cannot
                                                                     // silently claim another mod's error
                                list.Add(ri);
                            }
                            if (issues.Count > 0)
                                Log.Message("[Modern Dev Tools] loaded " + issues.Count + " shipped known issue(s) from " + mcp.Name + ".");
                        }
                        catch (Exception e) { Log.Warning("[Modern Dev Tools] failed reading shipped known-issues for " + mcp.Name + ": " + e.Message); }
                        break;   // one file per mod
                    }
                }
            }
            catch (Exception e) { Log.Warning("[Modern Dev Tools] shipped known-issue scan failed: " + e.Message); }
            _all = list;
        }

        private static string StripBom(string s) => IssueTextUtil.StripBom(s);
    }
}
