using System.Text.RegularExpressions;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// Built-in: matches the selected error against the shipped (and third-party) KnownIssueDef
    /// library, turning each match into a plain-language diagnosis. If a matched entry carries an
    /// attributeRegex, the captured mod is added to attribution too.
    /// </summary>
    public class Module_KnownIssues : ErrorModule
    {
        public override void Diagnose(ErrorContext ctx)
        {
            var matches = KnownIssueIndex.Match(ctx);
            int n = matches.Count;
            for (int i = 0; i < n; i++)
            {
                KnownIssueDef def = matches[i].def;
                ctx.AddDiagnosis(new ErrorDiagnosis
                {
                    Title = def.label.NullOrEmpty() ? def.defName : def.LabelCap.ToString(),
                    Explanation = def.description,
                    Fix = def.fix,
                    Url = def.url,
                    Source = def.defName,
                    Score = n - i,
                    Ignorable = def.ignorable
                });

                Regex attr = matches[i].attributeRegex;
                if (attr != null)
                {
                    Match m = attr.Match(ctx.Text ?? "");
                    if (m.Success && m.Groups.Count > 1)
                    {
                        string cap = m.Groups[1].Value.Trim();
                        ModMetaData meta = ctx.Mods.ExactName(cap) ?? ctx.Mods.PackageId(cap);
                        if (meta != null) ctx.Attribute(meta, ErrorContext.WeightKnownAttr, "MDT_ReasonKnownPattern".Translate());
                        else ctx.AttributeNamed(cap, null, ErrorContext.WeightKnownAttr, "MDT_ReasonKnownPattern".Translate());
                    }
                }
            }
        }
    }
}
