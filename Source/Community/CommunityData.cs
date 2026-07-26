using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Verse;

namespace ModernDevTools
{
    /// <summary>One entry from our own community bug/fix database (see _dev/ModernDevTools/community-db).</summary>
    public class RemoteIssue
    {
        public string Id, Title, Explanation, Fix, Url, Severity, ReportedBy;
        public string[] ExceptionTypes = System.Array.Empty<string>();
        public string[] Keywords = System.Array.Empty<string>();
        public string[] Namespaces = System.Array.Empty<string>();
        public string[] PackageIds = System.Array.Empty<string>();
        public Regex[] Regexes = System.Array.Empty<Regex>();
    }

    public class CommRule
    {
        public readonly HashSet<string> Incompat = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> LoadAfter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> LoadBefore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool Any => Incompat.Count > 0 || LoadAfter.Count > 0 || LoadBefore.Count > 0;
    }

    public class Replacement
    {
        public string OldName;
        public string NewName;
        public string NewWorkshopId;
        public readonly List<string> NewVersions = new List<string>();
    }

    /// <summary>
    /// Opt-in community databases (RimSort Community Rules + Use This Instead), fetched on a background
    /// thread and cached to disk. All lookups are per-mod and read the cached, parsed dictionaries; the
    /// game thread never blocks on the network and everything degrades gracefully offline.
    /// </summary>
    public static class CommunityData
    {
        private const string RulesUrl = "https://raw.githubusercontent.com/RimSort/Community-Rules-Database/main/communityRules.json";
        private const string ReplacementsUrl = "https://raw.githubusercontent.com/emipa606/UseThisInstead/master/replacements.json.gz";
        // Our own community bug/fix database (see _dev/ModernDevTools/community-db). Change these two if
        // the repo is forked under a different name - the reporting Issue Form URLs (ReportBuilder) and
        // the known-issues fetch below both derive from them.
        public const string RepoOwner = "Astryls";
        public const string RepoName = "ModernDevTools-KnownIssues";
        private const string BugsUrl = "https://raw.githubusercontent.com/" + RepoOwner + "/" + RepoName + "/main/known-issues.json";

        // parsed lookups (assigned atomically from the bg thread; read from the main thread)
        private static volatile Dictionary<string, CommRule> _rules;
        private static volatile Dictionary<string, Replacement> _replacements; // key: pidLower and "wid:"+id
        private static volatile List<RemoteIssue> _bugs;

        public static volatile bool Loading;
        public static DateTime? LastUpdated;
        public static string LastError;

        public static bool Enabled => ModernDevToolsMod.Settings != null && ModernDevToolsMod.Settings.enableCommunityData;
        public static bool HasData => _rules != null || _replacements != null || _bugs != null;

        private static string CacheDir => Path.Combine(GenFilePaths.ConfigFolderPath, "ModernDevTools");
        private static string CachePath(string f) => Path.Combine(CacheDir, f);

        /// <summary>Load cached data at startup (no network).</summary>
        public static void LoadCache()
        {
            if (!Enabled) return;
            RunBg(() =>
            {
                TryLoadFile(CachePath("communityRules.json"), ParseRules);
                TryLoadFile(CachePath("replacements.json"), ParseReplacements);
                TryLoadFile(CachePath("known-issues.json"), ParseBugs);
                try { LastUpdated = File.Exists(CachePath("replacements.json")) ? File.GetLastWriteTime(CachePath("replacements.json")) : (DateTime?)null; } catch { }
            });
        }

        /// <summary>Fetch the latest databases from the internet (opt-in; background).</summary>
        public static void Update()
        {
            if (Loading) return;
            Loading = true;
            LastError = null;
            RunBg(() =>
            {
                try
                {
                    try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }

                    byte[] rb = Download(RulesUrl, false);
                    if (rb != null)
                    {
                        string txt = StripBom(Encoding.UTF8.GetString(rb));
                        Directory.CreateDirectory(CacheDir);
                        File.WriteAllText(CachePath("communityRules.json"), txt);
                        ParseRules(txt);
                    }

                    byte[] gb = Download(ReplacementsUrl, true);
                    if (gb != null)
                    {
                        string txt = StripBom(Gunzip(gb));
                        Directory.CreateDirectory(CacheDir);
                        File.WriteAllText(CachePath("replacements.json"), txt);
                        ParseReplacements(txt);
                    }

                    byte[] bb = Download(BugsUrl, false);
                    if (bb != null)
                    {
                        string txt = StripBom(Encoding.UTF8.GetString(bb));
                        Directory.CreateDirectory(CacheDir);
                        File.WriteAllText(CachePath("known-issues.json"), txt);
                        ParseBugs(txt);
                    }

                    LastUpdated = DateTime.Now;
                }
                catch (Exception e)
                {
                    LastError = e.Message;
                    Log.Warning("[Modern Dev Tools] community data update failed: " + e.Message);
                }
                finally
                {
                    Loading = false;
                    try { LogAnalysisCache.Clear(); } catch { } // re-analyse with the new data
                }
            });
        }

        // --- per-mod lookups (main thread) ---

        public static CommRule RuleFor(string packageId)
        {
            var r = _rules;
            return r != null && !packageId.NullOrEmpty() && r.TryGetValue(packageId, out var rule) ? rule : null;
        }

        public static Replacement ReplacementFor(ModMetaData meta)
        {
            var r = _replacements;
            if (r == null || meta == null) return null;
            if (!meta.PackageId.NullOrEmpty() && r.TryGetValue(meta.PackageId, out var byPid)) return byPid;
            try { string id = meta.RootDir?.Name; if (!id.NullOrEmpty() && r.TryGetValue("wid:" + id, out var byWid)) return byWid; } catch { }
            return null;
        }

        // --- parsing ---

        private static void ParseRules(string txt)
        {
            var root = Json.AsObj(Json.Parse(txt));
            var rulesObj = root != null && root.TryGetValue("rules", out var rv) ? Json.AsObj(rv) : null;
            if (rulesObj == null) return;
            var dict = new Dictionary<string, CommRule>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in rulesObj)
            {
                var body = Json.AsObj(kv.Value);
                if (body == null) continue;
                var rule = new CommRule();
                Collect(body, "incompatibleWith", rule.Incompat);
                Collect(body, "loadAfter", rule.LoadAfter);
                Collect(body, "loadBefore", rule.LoadBefore);
                if (rule.Any) dict[kv.Key] = rule;
            }
            _rules = dict;
        }

        private static void Collect(Dictionary<string, object> body, string key, HashSet<string> into)
        {
            if (!body.TryGetValue(key, out var v)) return;
            var obj = Json.AsObj(v);
            if (obj == null) return;
            foreach (var k in obj.Keys) into.Add(k);
        }

        private static void ParseReplacements(string txt)
        {
            var root = Json.AsObj(Json.Parse(txt));
            var arr = root != null && root.TryGetValue("rules", out var rv) ? Json.AsArr(rv) : null;
            if (arr == null) return;
            var dict = new Dictionary<string, Replacement>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in arr)
            {
                var o = Json.AsObj(item);
                if (o == null) continue;
                var rep = new Replacement
                {
                    OldName = Json.Str(o.TryGetValue("oldName", out var on) ? on : null),
                    NewName = Json.Str(o.TryGetValue("newName", out var nn) ? nn : null),
                    NewWorkshopId = Json.Str(o.TryGetValue("newWorkshopId", out var nw) ? nw : null)
                };
                if (o.TryGetValue("newVersions", out var nvs) && Json.AsArr(nvs) is List<object> vl)
                    foreach (var ver in vl) { string sv = Json.Str(ver); if (!sv.NullOrEmpty()) rep.NewVersions.Add(sv); }

                string oldPid = Json.Str(o.TryGetValue("oldPackageId", out var op) ? op : null);
                string oldWid = Json.Str(o.TryGetValue("oldWorkshopId", out var ow) ? ow : null);
                if (!oldPid.NullOrEmpty()) dict[oldPid] = rep;
                if (!oldWid.NullOrEmpty()) dict["wid:" + oldWid] = rep;
            }
            _replacements = dict;
        }

        private static void ParseBugs(string txt) => _bugs = ParseIssues(txt);

        /// <summary>Parse a known-issues.json document (the community repo OR a mod-shipped file - same
        /// schema) into issues. Shared by the community fetch and by ModShippedIssues.</summary>
        public static List<RemoteIssue> ParseIssues(string txt)
        {
            var list = new List<RemoteIssue>();
            var root = Json.AsObj(Json.Parse(txt));
            var arr = root != null && root.TryGetValue("issues", out var iv) ? Json.AsArr(iv) : null;
            if (arr == null) return list;
            foreach (var item in arr)
            {
                var o = Json.AsObj(item);
                if (o == null) continue;
                var ri = new RemoteIssue
                {
                    Id = S(o, "id"), Title = S(o, "title"), Explanation = S(o, "explanation"),
                    Fix = S(o, "fix"), Url = S(o, "url"), Severity = S(o, "severity"), ReportedBy = S(o, "reportedBy")
                };
                if (ri.Title.NullOrEmpty()) continue;
                var match = o.TryGetValue("match", out var mv) ? Json.AsObj(mv) : null;
                if (match != null)
                {
                    ri.ExceptionTypes = Lower(StrArr(match, "exceptionTypes"));
                    ri.Keywords = Lower(StrArr(match, "keywords"));
                    ri.Namespaces = StrArr(match, "namespaces");
                    ri.PackageIds = Lower(StrArr(match, "packageIds"));
                    ri.Regexes = CompileRegexes(StrArr(match, "regexes"), ri.Id);
                }
                list.Add(ri);
            }
            return list;
        }

        public static List<RemoteIssue> MatchBugs(ErrorContext ctx) => Match(ctx, _bugs);

        /// <summary>Score an error against a pool of issues (the community DB or a mod-shipped list).</summary>
        public static List<RemoteIssue> Match(ErrorContext ctx, List<RemoteIssue> bugs)
        {
            if (bugs == null || ctx == null) return new List<RemoteIssue>();
            var scored = new List<KeyValuePair<RemoteIssue, int>>();
            try
            {
                string text = ctx.Text ?? "";
                string textLower = text.ToLowerInvariant();
                string exType = ctx.ExceptionType?.ToLowerInvariant();
                var pids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in ctx.ImplicatedPackageIds) pids.Add(p);

                foreach (RemoteIssue b in bugs)
                {
                    int score = 0;
                    if (b.ExceptionTypes.Length > 0 && exType != null && System.Array.IndexOf(b.ExceptionTypes, exType) >= 0) score += 3;
                    if (b.Regexes.Length > 0) foreach (var rx in b.Regexes) if (rx.IsMatch(text)) { score += 3; break; }
                    if (b.Keywords.Length > 0) foreach (var k in b.Keywords) if (k.Length > 0 && textLower.Contains(k)) { score += 2; break; }
                    if (b.Namespaces.Length > 0 && ctx.Namespaces.Count > 0)
                        foreach (var ns in b.Namespaces) { bool hit = false; foreach (var x in ctx.Namespaces) if (x.StartsWith(ns, StringComparison.OrdinalIgnoreCase)) { hit = true; break; } if (hit) { score += 2; break; } }
                    if (b.PackageIds.Length > 0 && pids.Count > 0) foreach (var p in b.PackageIds) if (pids.Contains(p)) { score += 2; break; }
                    if (score > 0) scored.Add(new KeyValuePair<RemoteIssue, int>(b, score));
                }
            }
            catch (Exception e) { Log.WarningOnce("[Modern Dev Tools] community bug match failed: " + e.Message, 0x2E19C50); }

            scored.Sort((a, b) => b.Value.CompareTo(a.Value));
            var final = new List<RemoteIssue>(scored.Count);
            foreach (var kv in scored) final.Add(kv.Key);
            return final;
        }

        private static string S(Dictionary<string, object> o, string key) => Json.Str(o.TryGetValue(key, out var v) ? v : null);

        private static string[] StrArr(Dictionary<string, object> o, string key)
        {
            var arr = o.TryGetValue(key, out var v) ? Json.AsArr(v) : null;
            if (arr == null) return System.Array.Empty<string>();
            var list = new List<string>();
            foreach (var it in arr) { string s = Json.Str(it); if (!s.NullOrEmpty()) list.Add(s); }
            return list.ToArray();
        }

        private static string[] Lower(string[] arr)
        {
            for (int i = 0; i < arr.Length; i++) arr[i] = arr[i].ToLowerInvariant();
            return arr;
        }

        private static Regex[] CompileRegexes(string[] patterns, string id)
        {
            if (patterns.Length == 0) return System.Array.Empty<Regex>();
            var list = new List<Regex>();
            foreach (var p in patterns)
            {
                try { list.Add(new Regex(p, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)); }
                catch (Exception e) { Log.WarningOnce("[Modern Dev Tools] bad community regex in " + id + ": " + e.Message, (id ?? "").GetHashCode()); }
            }
            return list.ToArray();
        }

        // --- io helpers ---

        private static void TryLoadFile(string path, Action<string> parse)
        {
            try { if (File.Exists(path)) parse(StripBom(File.ReadAllText(path))); }
            catch (Exception e) { Log.Warning("[Modern Dev Tools] failed reading " + Path.GetFileName(path) + ": " + e.Message); }
        }

        private static byte[] Download(string url, bool binary)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = 25000;
                req.ReadWriteTimeout = 25000;
                req.UserAgent = "ModernDevTools/1.0";
                if (!binary) req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var st = resp.GetResponseStream())
                using (var ms = new MemoryStream())
                {
                    st.CopyTo(ms);
                    return ms.ToArray();
                }
            }
            catch (Exception e) { LastError = e.Message; return null; } // quiet: surfaced on the status line, not the user's log
        }

        private static string Gunzip(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var gz = new GZipStream(ms, CompressionMode.Decompress))
            using (var r = new StreamReader(gz, Encoding.UTF8))
                return r.ReadToEnd();
        }

        private static string StripBom(string s) => (!s.NullOrEmpty() && s[0] == '\uFEFF') ? s.Substring(1) : s;

        private static void RunBg(Action a)
        {
            var t = new Thread(() => { try { a(); } catch (Exception e) { Log.Warning("[Modern Dev Tools] community bg task failed: " + e.Message); } });
            t.IsBackground = true;
            t.Start();
        }
    }
}
