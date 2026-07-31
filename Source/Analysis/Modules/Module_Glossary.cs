using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// Built-in inspector section: when the selected error or its explanations contain modding jargon
    /// (Harmony, def, xpath, TPS, load order, ...), draws a short "Terms in this error" card defining
    /// each in one plain sentence. Drawn via the module DrawSection hook, so it appears under the
    /// diagnoses and can be toggled off like any other module.
    /// </summary>
    public class Module_Glossary : ErrorModule
    {
        public override bool HasSection => true;

        public override float DrawSection(float width, float y, ErrorContext ctx)
        {
            // Cached on the analysis: BuildText concatenates the message plus every diagnosis and then
            // 26 compiled regexes sweep the result. All of those inputs are fixed once the pipeline has
            // run, so doing it per OnGUI pass was pure waste.
            LogAnalysis a = ctx?.Message != null ? LogAnalysisCache.Peek(ctx.Message) : null;
            List<KeyValuePair<string, string>> terms = a != null
                ? a.GlossaryTerms(() => Glossary.TermsIn(BuildText(ctx)))
                : Glossary.TermsIn(BuildText(ctx));
            if (terms.Count == 0) return y;

            Text.Font = GameFont.Small;
            Palette.SectionHeader(new Rect(0f, y, width, 22f), "MDT_SectionTerms".Translate());
            y += 22f + 6f;

            float innerW = width - 20f;
            var labelH = new float[terms.Count];
            var defH = new float[terms.Count];
            float contentH = 0f;
            for (int i = 0; i < terms.Count; i++)
            {
                labelH[i] = Text.LineHeight;
                defH[i] = Mathf.Ceil(TextMetrics.Height(terms[i].Value, innerW));
                contentH += labelH[i] + 2f + defH[i] + (i < terms.Count - 1 ? 8f : 0f);
            }

            float cardH = 8f + contentH + 8f;
            Rect card = new Rect(0f, y, width, cardH);
            Palette.DrawCard(card);
            Palette.StateStrip(card, Palette.StripGray, 3f);

            float cx = card.x + 10f;
            float cy = card.y + 8f;
            for (int i = 0; i < terms.Count; i++)
            {
                Text.WordWrap = false;
                GUI.color = Palette.Stat;
                Widgets.Label(new Rect(cx, cy, innerW, labelH[i]), terms[i].Key);
                GUI.color = Color.white;
                Text.WordWrap = true;
                cy += labelH[i] + 2f;

                GUI.color = Palette.TextDim;
                Widgets.Label(new Rect(cx, cy, innerW, defH[i]), terms[i].Value);
                GUI.color = Color.white;
                cy += defH[i] + 8f;
            }

            return y + cardH + 6f;
        }

        private static string BuildText(ErrorContext ctx)
        {
            if (ctx == null) return "";
            var sb = new StringBuilder();
            if (!ctx.Text.NullOrEmpty()) sb.Append(ctx.Text).Append(' ');
            if (!ctx.ExceptionType.NullOrEmpty()) sb.Append(ctx.ExceptionType).Append(' ');
            if (ctx.Diagnoses != null)
                foreach (ErrorDiagnosis d in ctx.Diagnoses)
                {
                    if (!d.Title.NullOrEmpty()) sb.Append(d.Title).Append(' ');
                    if (!d.Explanation.NullOrEmpty()) sb.Append(d.Explanation).Append(' ');
                    if (!d.Fix.NullOrEmpty()) sb.Append(d.Fix).Append(' ');
                }
            return sb.ToString();
        }
    }
}
