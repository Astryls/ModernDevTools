using System.Text.RegularExpressions;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// Built-in: attributes an error by the "[ModName]" tag that many mods stamp on their own log
    /// output - e.g. "[Giddy-Up] An error occured...", "[Map Preview v1.12.25] Failed to...",
    /// "[MeleeAnim] Error generating texture report", "[Map Mode Framework] ...". The LEADING tag is
    /// the mod that PRINTED the message, a strong and actionable lead even when the stack trace is
    /// pure vanilla; other bracket tags on the same line are treated as weaker mentions.
    ///
    /// Every tag is resolved through InstalledModIndex.MatchTag, which only accepts an unambiguous
    /// match to a single ACTIVE installed mod - so non-mod brackets ("[Ref ..]", "[0x..]") and
    /// ambiguous abbreviations attribute to nothing. Only the first line is scanned, keeping the
    /// exception's own stack-trace brackets out of the picture.
    /// </summary>
    public class Module_LogPrefix : ErrorModule
    {
        private static readonly Regex BracketTag =
            new Regex(@"\[([^\[\]\r\n]{2,60})\]", RegexOptions.Compiled);

        // Unity rich-text markup, which mods routinely wrap their log prefix in - e.g.
        //   <color=#FFA500FF>[Map Mode Framework]</color> BiomesKitWorldLayer ... not found.
        // Without stripping this the tag is not at index 0, so the strongest signal we have ("this mod
        // PRINTED the line", weight 5) silently degrades to a passing mention (weight 2.5) and the real
        // author of the message can lose the top culprit slot to something named later in the text.
        // Found live in test run #232.
        private static readonly Regex RichText =
            new Regex(@"</?(?:color|b|i|size|material|quad)(?:=[^>]*)?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public override void ContributeAttribution(ErrorContext ctx)
        {
            string text = ctx.Text;
            if (text.NullOrEmpty()) return;

            int nl = text.IndexOf('\n');
            string firstLine = nl >= 0 ? text.Substring(0, nl) : text;
            firstLine = RichText.Replace(firstLine, "").TrimStart();

            foreach (Match m in BracketTag.Matches(firstLine))
            {
                ModMetaData meta = ctx.Mods.MatchTag(m.Groups[1].Value);
                if (meta == null) continue;

                bool leading = m.Index == 0;
                ctx.Attribute(meta,
                    leading ? ErrorContext.WeightMessagePrefix : ErrorContext.WeightMessageName,
                    (leading ? "MDT_ReasonLogPrefix" : "MDT_ReasonLogMention").Translate());
            }
        }
    }
}
