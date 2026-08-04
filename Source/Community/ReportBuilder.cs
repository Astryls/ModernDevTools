using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// Client half of the community reporting API (Option A: GitHub Issue Forms).
    ///
    /// The mod never holds a write token - it opens a *pre-filled* GitHub Issue Form in the browser and
    /// the user submits it under their own GitHub account. A GitHub Action on the repo folds accepted
    /// "fix" submissions into known-issues.json (which the mod already fetches with the community data)
    /// and dedupes "bug" reports by their signature. This keeps the whole loop server-less and secret-free.
    ///
    /// The query-param names below MUST match the field ids declared in the repo's Issue Forms
    /// (_dev/ModernDevTools/community-db/.github/ISSUE_TEMPLATE/*.yml) or the pre-fill silently no-ops.
    /// </summary>
    public static class ReportBuilder
    {
        private const int UrlBudget = 6000;      // keep the whole URL well under GitHub's ~8 KB cap
        private const int SignatureFrames = 6;   // stack depth folded into the cross-machine signature
        private const int CompactFrames = 8;     // frames shown in the auto-collected "details" block
        private const int DetailsCap = 1600;     // char cap for the pre-filled details field

        private static string NewIssueUrl =>
            "https://github.com/" + CommunityData.RepoOwner + "/" + CommunityData.RepoName + "/issues/new";

        // ===== Report destination: the culprit mod's own channel, community as the fallback =====

        public enum ReportChannel { None, Github, Workshop, Site }

        public struct ReportTarget
        {
            public ReportChannel Channel;
            public string ModName;
            public string PageUrl;
        }

        /// <summary>Where a report should go by default: the top implicated mod's own GitHub, else its
        /// Steam Workshop page, else its site. None when no installed culprit is identified.</summary>
        public static ReportTarget CulpritTarget(LogAnalysis a)
        {
            var t = new ReportTarget { Channel = ReportChannel.None };
            try
            {
                AttributedMod top = null;
                if (a != null)
                    foreach (AttributedMod m in a.Culprits)
                        if (m.Installed && !m.PackageId.NullOrEmpty()) { top = m; break; }
                if (top == null) return t;
                t.ModName = top.Name;

                ModMetaData meta = InstalledModIndex.Instance.PackageId(top.PackageId);
                string url = meta?.Url;
                if (!url.NullOrEmpty() && url.IndexOf("github.com", StringComparison.OrdinalIgnoreCase) >= 0)
                { t.Channel = ReportChannel.Github; t.PageUrl = url; return t; }

                if (meta != null && meta.OnSteamWorkshop)
                {
                    string id = meta.RootDir?.Name;   // workshop mods live under .../294100/<publishedfileid>
                    if (!id.NullOrEmpty() && ulong.TryParse(id, out _))
                    { t.Channel = ReportChannel.Workshop; t.PageUrl = "https://steamcommunity.com/sharedfiles/filedetails/?id=" + id; return t; }
                }
                if (!url.NullOrEmpty()) { t.Channel = ReportChannel.Site; t.PageUrl = url; }
            }
            catch (Exception e) { Log.Warning("[Modern Dev Tools] culprit target resolve failed: " + e.Message); }
            return t;
        }

        /// <summary>Send the report to the culprit mod: a pre-filled issue on its GitHub, or its Workshop/
        /// site page with the report on the clipboard to paste into a comment.</summary>
        public static void ReportToCulprit(ReportTarget target, string title, string body)
        {
            try
            {
                if (target.Channel == ReportChannel.Github && TryParseGithubRepo(target.PageUrl, out string owner, out string repo))
                {
                    var q = new List<KeyValuePair<string, string>> { Kv("title", title), Kv("body", body) };
                    GUIUtility.systemCopyBuffer = body;
                    OpenAt("https://github.com/" + owner + "/" + repo + "/issues/new", q, "body");
                    Messages.Message("MDT_ReportSentToMod".Translate(target.ModName ?? repo), MessageTypeDefOf.TaskCompletion, false);
                }
                else if (!target.PageUrl.NullOrEmpty())
                {
                    GUIUtility.systemCopyBuffer = body;   // can't pre-fill a Workshop/site page; paste from clipboard
                    Application.OpenURL(target.PageUrl);
                    Messages.Message("MDT_ReportPasteToPage".Translate(target.ModName ?? ""), MessageTypeDefOf.TaskCompletion, false);
                }
            }
            catch (Exception e) { Log.Warning("[Modern Dev Tools] report-to-culprit failed: " + e.Message); }
        }

        public static string IssueTitle(LogMessage msg, LogAnalysis a, bool isFix) =>
            (isFix ? "[fix] " : "[bug] ") + TitleSummary(msg) + " [#" + Signature(msg, a) + "]";

        private static bool TryParseGithubRepo(string url, out string owner, out string repo)
        {
            owner = repo = null;
            if (url.NullOrEmpty()) return false;
            int idx = url.IndexOf("github.com/", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;
            string rest = url.Substring(idx + "github.com/".Length);
            string[] parts = rest.Split(new[] { '/', '?', '#' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return false;
            owner = parts[0];
            repo = parts[1];
            if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repo = repo.Substring(0, repo.Length - 4);
            return !owner.NullOrEmpty() && !repo.NullOrEmpty();
        }

        /// <summary>True when the community DB already carries a fix for this error (a community: diagnosis
        /// is present), so the UI can nudge toward "already reported" instead of inviting a duplicate.</summary>
        public static bool AlreadyKnown(LogAnalysis a)
        {
            if (a?.Diagnoses == null) return false;
            foreach (ErrorDiagnosis d in a.Diagnoses)
                if (!d.Source.NullOrEmpty() && d.Source.StartsWith("community:", StringComparison.Ordinal)) return true;
            return false;
        }

        // --- report a bug (any player, low friction) ---

        public static void ReportBug(LogMessage msg, LogAnalysis a, string summary)
        {
            if (msg == null) return;
            try
            {
                string sig = Signature(msg, a);
                GUIUtility.systemCopyBuffer = FullReportWithNote(msg, a, sig, summary);   // full report -> clipboard for the "log" field

                var q = new List<KeyValuePair<string, string>>
                {
                    Kv("template", "bug-report.yml"),
                    Kv("labels", "report"),
                    Kv("title", "[report] " + TitleSummary(msg) + " [#" + sig + "]"),
                    Kv("exception", ExceptionType(msg, a)),
                    Kv("implicated", Implicated(a)),
                    Kv("rwversion", SafeVersion()),
                    Kv("signature", sig),
                    Kv("details", Compact(msg, a, sig)),
                };
                Add(q, "summary", summary);
                // Also send a plain body: GitHub Issue Forms pre-fill by field id ONLY when the template
                // exists on the repo; if it doesn't, GitHub shows a plain issue and honours `body` instead.
                // When the form does exist, `body` is ignored. Sending both makes the description populate
                // either way.
                Add(q, "body", BugBody(msg, a, sig, summary));
                Open(q);
                Messages.Message("MDT_ReportOpened".Translate(), MessageTypeDefOf.TaskCompletion, false);
            }
            catch (Exception e) { Log.Warning("[Modern Dev Tools] report build failed: " + e.Message); }
        }

        // --- submit a fix (mod author / helper, structured to the schema) ---

        public static void SubmitFix(LogMessage msg, LogAnalysis a, string explanation, string fix, string credit)
        {
            try
            {
                // Seed the fix form's match block from the selected error so the author edits real signal.
                var q = new List<KeyValuePair<string, string>>
                {
                    Kv("template", "fix-submission.yml"),
                    Kv("labels", "fix-submission"),
                    Kv("title", "[fix] " + (msg != null ? TitleSummary(msg) : "")),
                };
                Add(q, "explanation", explanation);
                Add(q, "fix", fix);
                Add(q, "reportedBy", credit);
                if (msg != null)
                {
                    Add(q, "match_exceptionTypes", ExceptionType(msg, a));
                    Add(q, "match_packageIds", Implicated(a));
                    Add(q, "match_namespaces", TopNamespaces(a));
                }
                Add(q, "body", FixBody(explanation, fix, credit, msg, a));   // plain-issue fallback (see ReportBug)
                Open(q);
                Messages.Message("MDT_FixFormOpened".Translate(), MessageTypeDefOf.TaskCompletion, false);
            }
            catch (Exception e) { Log.Warning("[Modern Dev Tools] fix form build failed: " + e.Message); }
        }

        // --- signature: stable across machines, folds exception type + normalized top frame TYPES ---
        // (method names, IL offsets, and file paths are dropped because they differ per build/platform).

        public static string Signature(LogMessage msg, LogAnalysis a)
        {
            var sb = new StringBuilder();
            sb.Append(ExceptionType(msg, a) ?? "");
            string[] frames = a?.Context?.Frames ?? (msg?.StackTrace ?? "").Split('\n');
            int n = 0;
            foreach (string line in frames)
            {
                string q = FrameParser.QualifiedTypeOf(line);
                if (q.NullOrEmpty()) continue;
                sb.Append('|').Append(q);
                if (++n >= SignatureFrames) break;
            }
            return Fnv1a(sb.ToString());
        }

        private static string Fnv1a(string s)
        {
            uint h = 2166136261u;
            for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619u; }
            return h.ToString("x8");
        }

        // --- the full, human-readable report (also what the "Copy report" button copies) ---

        public static string FullReport(LogMessage msg, LogAnalysis a) => FullReport(msg, a, Signature(msg, a));

        /// <summary>Full report with the user's typed note prepended (what the report dialog copies/submits).</summary>
        public static string FullReportWithNote(LogMessage msg, LogAnalysis a, string note) =>
            FullReportWithNote(msg, a, Signature(msg, a), note);

        public static string FullReportWithNote(LogMessage msg, LogAnalysis a, string sig, string note)
        {
            string body = FullReport(msg, a, sig);
            return note.NullOrEmpty() ? body : ("User note: " + note.Trim() + "\n\n" + body);
        }

        public static string FullReport(LogMessage msg, LogAnalysis a, string sig)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Modern Dev Tools report");
            sb.AppendLine("Signature: #" + sig);
            sb.AppendLine("RimWorld: " + SafeVersion());
            // Always included when the engine has it, regardless of the display setting - "when did this
            // start" is diagnostic data, and a report is read by someone who was not there.
            string ts = LogTimestamps.Of(msg);
            if (!ts.NullOrEmpty()) sb.AppendLine((msg.repeats > 1 ? "First logged: " : "Logged: ") + ts);
            sb.AppendLine(TypeWord(msg.type) + (msg.repeats > 1 ? " (x" + msg.repeats + ")" : ""));
            sb.AppendLine(msg.text);
            sb.AppendLine();
            if (a != null && a.AnyCulprit)
            {
                sb.AppendLine("Likely source:");
                foreach (AttributedMod m in a.Culprits)
                {
                    string state = !m.Installed ? "not installed" : (m.Active ? "active" : "inactive");
                    string idPart = m.PackageId.NullOrEmpty() ? "" : " [" + m.PackageId + "]";
                    string reasonPart = m.TopReason.NullOrEmpty() ? "" : " - " + m.TopReason;
                    sb.AppendLine("  - " + m.Name + idPart + " (" + state + ")" + reasonPart);
                }
                sb.AppendLine();
            }
            if (a?.Diagnoses != null && a.Diagnoses.Count > 0)
            {
                sb.AppendLine("What this means:");
                foreach (ErrorDiagnosis d in a.Diagnoses)
                    sb.AppendLine("  - " + d.Title);
                sb.AppendLine();
            }
            sb.AppendLine("Stack trace:");
            sb.Append(msg.StackTrace);
            return sb.ToString();
        }

        // --- the compact auto-collected block pre-filled into the issue form ---

        /// <summary>Markdown body used for the plain-issue fallback (repo has no Issue Form template yet).</summary>
        public static string BugBody(LogMessage msg, LogAnalysis a, string sig, string summary)
        {
            var sb = new StringBuilder();
            if (!summary.NullOrEmpty())
            {
                sb.AppendLine("### What happened");
                sb.AppendLine(summary.Trim());
                sb.AppendLine();
            }
            sb.AppendLine("### Auto-collected");
            sb.AppendLine("```");
            sb.Append(Compact(msg, a, sig));
            sb.AppendLine();
            sb.AppendLine("```");
            string outp = sb.ToString();
            return outp.Length > 3000 ? outp.Substring(0, 3000) : outp;
        }

        public static string FixBody(string explanation, string fix, string credit, LogMessage msg, LogAnalysis a)
        {
            var sb = new StringBuilder();
            sb.AppendLine("### Explanation");
            sb.AppendLine(explanation.NullOrEmpty() ? "_(none)_" : explanation.Trim());
            sb.AppendLine();
            sb.AppendLine("### Fix");
            sb.AppendLine(fix.NullOrEmpty() ? "_(none)_" : fix.Trim());
            sb.AppendLine();
            sb.AppendLine("### Match");
            sb.AppendLine("- exceptionTypes: " + (msg != null ? ExceptionType(msg, a) : ""));
            sb.AppendLine("- packageIds: " + Implicated(a));
            sb.AppendLine("- namespaces: " + TopNamespaces(a));
            if (!credit.NullOrEmpty()) { sb.AppendLine(); sb.AppendLine("Credit: " + credit.Trim()); }
            string outp = sb.ToString();
            return outp.Length > 3000 ? outp.Substring(0, 3000) : outp;
        }

        private static string Compact(LogMessage msg, LogAnalysis a, string sig)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Exception: " + (ExceptionType(msg, a).NullOrEmpty() ? "(none)" : ExceptionType(msg, a)));
            sb.AppendLine("RimWorld: " + SafeVersion());
            string imp = Implicated(a);
            sb.AppendLine("Implicated: " + (imp.NullOrEmpty() ? "(none identified)" : imp));
            sb.AppendLine("Signature: #" + sig);
            sb.AppendLine();
            sb.AppendLine("Top of stack:");
            string[] frames = a?.Context?.Frames ?? (msg?.StackTrace ?? "").Split('\n');
            int n = 0;
            foreach (string line in frames)
            {
                string t = TrimFrame(line);
                if (t.NullOrEmpty()) continue;
                sb.AppendLine("  " + t);
                if (++n >= CompactFrames) break;
            }
            sb.AppendLine();
            sb.Append("(The full report was copied to your clipboard - paste it into the \"Full report\" field below.)");
            string outp = sb.ToString();
            if (outp.Length > DetailsCap) outp = outp.Substring(0, DetailsCap) + "\n  ...";
            return outp;
        }

        private static string TrimFrame(string line)
        {
            if (line.NullOrEmpty()) return "";
            string s = line.Trim();
            int off = s.IndexOf("[0x", StringComparison.Ordinal);
            if (off > 0) s = s.Substring(0, off).TrimEnd();
            int at = s.IndexOf(" in ", StringComparison.Ordinal);
            if (at > 0) s = s.Substring(0, at).TrimEnd();
            return s.Length > 160 ? s.Substring(0, 160) : s;
        }

        // --- field helpers ---

        public static string ExceptionType(LogMessage msg, LogAnalysis a) =>
            a?.Context?.ExceptionType ?? FrameParser.ExtractExceptionType(msg?.text) ?? "";

        public static string Implicated(LogAnalysis a)
        {
            if (a?.Context == null) return "";
            var seen = new List<string>();
            foreach (string pid in a.Context.ImplicatedPackageIds)
            {
                if (pid.NullOrEmpty() || seen.Contains(pid)) continue;
                seen.Add(pid);
                if (seen.Count >= 6) break;
            }
            return string.Join(", ", seen.ToArray());
        }

        private static string TopNamespaces(LogAnalysis a)
        {
            if (a?.Context == null) return "";
            var seen = new List<string>();
            foreach (string ns in a.Context.Namespaces)
            {
                string root = FrameParser.RootNamespaceOf(ns);
                if (root.NullOrEmpty() || FrameParser.IsFrameworkRoot(root) || seen.Contains(root)) continue;
                seen.Add(root);
                if (seen.Count >= 4) break;
            }
            return string.Join(", ", seen.ToArray());
        }

        private static string TitleSummary(LogMessage msg)
        {
            string s = msg?.text ?? "";
            int nl = s.IndexOf('\n');
            if (nl >= 0) s = s.Substring(0, nl);
            s = s.Replace('\r', ' ').Trim();
            return s.Length > 90 ? s.Substring(0, 90) + "..." : s;
        }

        public static string SafeVersion()
        {
            try { return VersionControl.CurrentVersionString; } catch { return "?"; }
        }

        private static string TypeWord(LogMessageType t)
        {
            switch (t)
            {
                case LogMessageType.Error: return "MDT_TypeError".Translate();
                case LogMessageType.Warning: return "MDT_TypeWarning".Translate();
                default: return "MDT_TypeMessage".Translate();
            }
        }

        // --- URL assembly (keeps the whole thing under the GitHub length cap) ---

        private static void Open(List<KeyValuePair<string, string>> q) => OpenAt(NewIssueUrl, q, "details", "body");

        private static void OpenAt(string baseUrl, List<KeyValuePair<string, string>> q, params string[] trimKeys)
        {
            string url = Build(baseUrl, q);
            if (url.Length > UrlBudget && trimKeys != null)
            {
                foreach (string key in trimKeys)
                {
                    if (url.Length <= UrlBudget) break;
                    for (int i = 0; i < q.Count; i++)
                    {
                        if (q[i].Key != key) continue;
                        string v = q[i].Value;
                        q[i] = Kv(key, v.Length > 400 ? v.Substring(0, 400) + "\n...(trimmed - use Copy report for the full text)" : v);
                        url = Build(baseUrl, q);
                        if (url.Length > UrlBudget) { q.RemoveAt(i); url = Build(baseUrl, q); }
                        break;
                    }
                }
            }
            Application.OpenURL(url);
        }

        private static string Build(string baseUrl, List<KeyValuePair<string, string>> q)
        {
            var sb = new StringBuilder(baseUrl);
            char sep = baseUrl.IndexOf('?') >= 0 ? '&' : '?';
            foreach (var kv in q)
            {
                if (kv.Value == null) continue;
                sb.Append(sep).Append(kv.Key).Append('=').Append(Uri.EscapeDataString(kv.Value));
                sep = '&';
            }
            return sb.ToString();
        }

        private static KeyValuePair<string, string> Kv(string k, string v) => new KeyValuePair<string, string>(k, v ?? "");
        private static void Add(List<KeyValuePair<string, string>> q, string k, string v) { if (!v.NullOrEmpty()) q.Add(Kv(k, v)); }
    }
}
