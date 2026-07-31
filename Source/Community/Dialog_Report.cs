using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// In-game composer for a community bug report or fix submission. Shows the auto-collected diagnostics
    /// (exception, implicated mods, RimWorld version, signature) read-only, lets the user add their own
    /// context ("what were you doing", or the explanation + "how I fixed it" for a fix), previews the exact
    /// report live, and only then copies it to the clipboard and/or opens the pre-filled GitHub issue.
    /// Nothing is sent until the user chooses to - this is the "add information before pushing" step.
    /// </summary>
    public class Dialog_Report : Window
    {
        public enum ReportMode { Bug, Fix }

        private readonly LogMessage _msg;
        private readonly LogAnalysis _a;
        private readonly ReportMode _mode;
        private readonly string _sig, _exc, _impl, _ver;
        private readonly ReportBuilder.ReportTarget _target;

        private string _summary = "";
        private string _explanation = "";
        private string _fix = "";
        private string _credit = "";
        private Vector2 _previewScroll;

        public Dialog_Report(LogMessage msg, LogAnalysis a, ReportMode mode)
        {
            _msg = msg; _a = a; _mode = mode;
            _sig = ReportBuilder.Signature(msg, a);
            _exc = ReportBuilder.ExceptionType(msg, a);
            _impl = ReportBuilder.Implicated(a);
            _ver = ReportBuilder.SafeVersion();
            _target = ReportBuilder.CulpritTarget(a);

            doWindowBackground = false;
            doCloseX = false;
            draggable = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = false;
            closeOnCancel = true;
            onlyOneOfTypeAllowed = false;
        }

        protected override float Margin => 0f;
        public override Vector2 InitialSize => new Vector2(680f, _mode == ReportMode.Fix ? 740f : 600f);

        public override void DoWindowContents(Rect inRect)
        {
            try { Draw(inRect); }
            catch (Exception e) { Log.ErrorOnce("[Modern Dev Tools] report dialog draw failed: " + e, 0x2E19C60); }
            finally { Palette.ResetGuiState(); }
        }

        private void Draw(Rect inRect)
        {
            Widgets.DrawBoxSolid(inRect, Palette.BG);
            Palette.DrawBox(inRect, Palette.BGL, 1);
            Rect c = inRect.ContractedBy(14f);
            float y = c.y;

            // Title + close
            Text.Font = GameFont.Medium;
            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color = Palette.Stat;
            Widgets.Label(new Rect(c.x, y, c.width - 28f, 32f),
                (_mode == ReportMode.Fix ? "MDT_FixDialogTitle" : "MDT_ReportDialogTitle").Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small; Text.Anchor = TextAnchor.UpperLeft;
            if (Palette.CloseX(new Rect(c.xMax - 24f, y + 4f, 22f, 22f))) Close();
            y += 40f;

            // Auto-collected info
            y = InfoRow(c, y, "MDT_ReportInfo_Exception".Translate(), _exc.NullOrEmpty() ? "(none)" : _exc);
            y = InfoRow(c, y, "MDT_ReportInfo_Version".Translate(), _ver);
            y = InfoRow(c, y, "MDT_ReportInfo_Implicated".Translate(), _impl.NullOrEmpty() ? "(none identified)" : _impl);
            y = InfoRow(c, y, "MDT_ReportInfo_Signature".Translate(), "#" + _sig);
            y += 8f;

            // Editable fields
            if (_mode == ReportMode.Bug)
            {
                y = Field(c, y, "MDT_ReportField_Summary".Translate(), ref _summary, 74f);
            }
            else
            {
                y = Field(c, y, "MDT_FixField_Explanation".Translate(), ref _explanation, 62f);
                y = Field(c, y, "MDT_FixField_Fix".Translate(), ref _fix, 62f);
                y = FieldLine(c, y, "MDT_FixField_Credit".Translate(), ref _credit);
            }
            y += 4f;

            // Live preview of the exact report
            GUI.color = Palette.TextDim;
            Widgets.Label(new Rect(c.x, y, c.width, 20f), "MDT_ReportTracePreview".Translate());
            GUI.color = Color.white;
            y += 22f;

            const float btnRowH = 30f;
            float previewH = Mathf.Max(70f, c.yMax - (2f * btnRowH + 6f) - 10f - y);
            Rect previewOut = new Rect(c.x, y, c.width, previewH);
            Palette.DrawWell(previewOut);
            string preview = _mode == ReportMode.Bug
                ? ReportBuilder.FullReportWithNote(_msg, _a, _sig, _summary)
                : FixPreview();
            Rect inner = previewOut.ContractedBy(6f);
            float th = Mathf.Max(TextMetrics.Height(preview, inner.width - 16f), inner.height);
            Rect view = new Rect(0f, 0f, inner.width - 16f, th);
            Palette.BeginScroll(inner, ref _previewScroll, view);
            try
            {
                GUI.color = Palette.TextDim;
                Widgets.Label(new Rect(0f, 0f, view.width, th), preview);
                GUI.color = Color.white;
            }
            finally { Palette.EndScroll(); }

            // Buttons. Row 1 = report destinations: the culprit mod's own channel first (GitHub, else
            // Workshop/site), with the community database as an explicit "also". Row 2 = copy / close.
            float row2y = c.yMax - btnRowH;
            float row1y = row2y - btnRowH - 6f;

            string primaryLabel = null, primaryTip = null;
            switch (_target.Channel)
            {
                case ReportBuilder.ReportChannel.Github:
                    primaryLabel = "MDT_ReportToModGithub".Translate(_target.ModName);
                    primaryTip = "MDT_ReportToModGithubTip".Translate(_target.ModName);
                    break;
                case ReportBuilder.ReportChannel.Workshop:
                    primaryLabel = "MDT_ReportToModWorkshop".Translate(_target.ModName);
                    primaryTip = "MDT_ReportToModWorkshopTip".Translate(_target.ModName);
                    break;
                case ReportBuilder.ReportChannel.Site:
                    primaryLabel = "MDT_ReportToModSite".Translate(_target.ModName);
                    primaryTip = "MDT_ReportToModWorkshopTip".Translate(_target.ModName);
                    break;
            }

            if (primaryLabel != null)
            {
                float half = (c.width - 6f) / 2f;
                if (Palette.GrayButton(new Rect(c.x, row1y, half, btnRowH), primaryLabel, primaryTip))
                {
                    string title = ReportBuilder.IssueTitle(_msg, _a, _mode == ReportMode.Fix);
                    string body = _mode == ReportMode.Bug
                        ? ReportBuilder.BugBody(_msg, _a, _sig, _summary)
                        : ReportBuilder.FixBody(_explanation, _fix, _credit, _msg, _a);
                    ReportBuilder.ReportToCulprit(_target, title, body);
                    Close();
                }
                if (Palette.GrayButton(new Rect(c.x + half + 6f, row1y, half, btnRowH), "MDT_ReportToCommunity".Translate(), "MDT_ReportToCommunityTip".Translate()))
                { CommunitySubmit(); Close(); }
            }
            else
            {
                if (Palette.GrayButton(new Rect(c.x, row1y, c.width, btnRowH), "MDT_ReportToCommunityOnly".Translate(), "MDT_ReportToCommunityTip".Translate()))
                { CommunitySubmit(); Close(); }
            }

            float half2 = (c.width - 6f) / 2f;
            if (Palette.GrayButton(new Rect(c.x, row2y, half2, btnRowH), "MDT_ReportCopy".Translate(), "MDT_ReportCopyTip".Translate()))
            {
                GUIUtility.systemCopyBuffer = preview;
                Messages.Message("MDT_ReportCopied".Translate(), MessageTypeDefOf.SilentInput, false);
            }
            if (Palette.GrayButton(new Rect(c.x + half2 + 6f, row2y, half2, btnRowH), "MDT_ReportClose".Translate())) Close();
        }

        private void CommunitySubmit()
        {
            if (_mode == ReportMode.Bug) ReportBuilder.ReportBug(_msg, _a, _summary);
            else ReportBuilder.SubmitFix(_msg, _a, _explanation, _fix, _credit);
        }

        private string FixPreview()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Fix submission");
            sb.AppendLine("Signature: #" + _sig);
            sb.AppendLine();
            sb.AppendLine("Explanation:");
            sb.AppendLine(_explanation.NullOrEmpty() ? "(none yet)" : _explanation.Trim());
            sb.AppendLine();
            sb.AppendLine("Fix:");
            sb.AppendLine(_fix.NullOrEmpty() ? "(none yet)" : _fix.Trim());
            sb.AppendLine();
            sb.AppendLine("Match:");
            sb.AppendLine("  exception: " + (_exc.NullOrEmpty() ? "(none)" : _exc));
            sb.AppendLine("  packageIds: " + (_impl.NullOrEmpty() ? "(none)" : _impl));
            if (!_credit.NullOrEmpty()) { sb.AppendLine(); sb.AppendLine("Credit: " + _credit.Trim()); }
            return sb.ToString();
        }

        private float InfoRow(Rect c, float y, string label, string val)
        {
            Text.Font = GameFont.Small;
            Text.WordWrap = false;
            GUI.color = Palette.TextDim;
            Widgets.Label(new Rect(c.x, y, 90f, 22f), label);
            Palette.LabelFit(new Rect(c.x + 94f, y, c.width - 94f, 22f), val, Palette.Stat);
            GUI.color = Color.white;
            Text.WordWrap = true;
            return y + 22f;
        }

        private float Field(Rect c, float y, string label, ref string val, float h)
        {
            GUI.color = Palette.Stat;
            Widgets.Label(new Rect(c.x, y, c.width, 20f), label);
            GUI.color = Color.white;
            y += 22f;
            Rect box = new Rect(c.x, y, c.width, h);
            Palette.DrawWell(box);
            string edited = Widgets.TextArea(box.ContractedBy(3f), val ?? "");
            if (edited != val) val = edited;
            return y + h + 8f;
        }

        private float FieldLine(Rect c, float y, string label, ref string val)
        {
            GUI.color = Palette.Stat;
            Widgets.Label(new Rect(c.x, y, c.width, 20f), label);
            GUI.color = Color.white;
            y += 22f;
            Rect box = new Rect(c.x, y, c.width, 28f);
            Palette.DrawWell(box);
            string edited = Widgets.TextField(box.ContractedBy(3f), val ?? "");
            if (edited != val) val = edited;
            return y + 28f + 8f;
        }
    }
}
