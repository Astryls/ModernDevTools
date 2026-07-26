using System.Text.RegularExpressions;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// Built-in: attributes an error by MENTIONS in its message text - the mod named in a vanilla
    /// "Mod X ..." warning, any installed packageId referenced, and mod folders inside file paths.
    /// This is what catches the huge class of load-time warnings whose stack trace is pure vanilla
    /// (dependency/packageId validation, etc.) but whose text names the mod to fix.
    /// </summary>
    public class Module_MessageMentions : ErrorModule
    {
        // Exact vanilla grammars (Verse.ModMetaData): the owner mod is capture group 1.
        private static readonly Regex OwnerDepUrl =
            new Regex(@"^Mod (.+?) dependency \(([^)]+)\) needs to have", RegexOptions.Compiled);
        private static readonly Regex OwnerBadPackageId =
            new Regex(@"^Mod (.+?) <packageId> \(([^)]+)\) is not in valid format", RegexOptions.Compiled);
        private static readonly Regex OwnerHasDependency =
            new Regex(@"^Mod (.+?) has a dependency", RegexOptions.Compiled);

        private static readonly Regex DottedToken =
            new Regex(@"\b([A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+)\b", RegexOptions.Compiled);
        private static readonly Regex ParenToken =
            new Regex(@"\(([A-Za-z0-9_.]+)\)", RegexOptions.Compiled);

        private static readonly Regex WorkshopPath =
            new Regex(@"294100[\\/](\d+)", RegexOptions.Compiled);
        private static readonly Regex LocalModPath =
            new Regex(@"[\\/]Mods[\\/]([^\\/\r\n]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public override void ContributeAttribution(ErrorContext ctx)
        {
            string text = ctx.Text;
            if (text.NullOrEmpty()) return;

            MatchOwner(ctx, OwnerDepUrl.Match(text), true);
            MatchOwner(ctx, OwnerBadPackageId.Match(text), false); // parenthesized token is the owner's OWN bad id
            MatchOwner(ctx, OwnerHasDependency.Match(text), false);

            foreach (Match m in DottedToken.Matches(text)) TryPackage(ctx, m.Groups[1].Value);
            foreach (Match m in ParenToken.Matches(text)) TryPackage(ctx, m.Groups[1].Value);

            foreach (Match m in WorkshopPath.Matches(text)) TryFolder(ctx, m.Groups[1].Value);
            foreach (Match m in LocalModPath.Matches(text)) TryFolder(ctx, m.Groups[1].Value);
        }

        private static void MatchOwner(ErrorContext ctx, Match m, bool hasDependency)
        {
            if (!m.Success) return;
            string owner = m.Groups[1].Value.Trim();
            ModMetaData meta = ctx.Mods.ExactName(owner);
            if (meta != null) ctx.Attribute(meta, ErrorContext.WeightMessageOwner, "MDT_ReasonNamedProblem".Translate());
            else ctx.AttributeNamed(owner, null, ErrorContext.WeightMessageOwner, "MDT_ReasonNamedProblem".Translate());

            if (hasDependency && m.Groups.Count > 2)
            {
                string dep = m.Groups[2].Value.Trim();
                ModMetaData depMeta = ctx.Mods.PackageId(dep);
                if (depMeta != null) ctx.Attribute(depMeta, ErrorContext.WeightMessagePackage, "MDT_ReasonDependency".Translate());
                else ctx.AttributeNamed(dep, dep, ErrorContext.WeightMessagePackage, "MDT_ReasonDependency".Translate());
            }
        }

        private static void TryPackage(ErrorContext ctx, string token)
        {
            ModMetaData meta = ctx.Mods.PackageId(token);
            if (meta != null) ctx.Attribute(meta, ErrorContext.WeightMessagePackage, "MDT_ReasonPackageRef".Translate());
        }

        private static void TryFolder(ErrorContext ctx, string folder)
        {
            ModMetaData meta = ctx.Mods.Folder(folder);
            if (meta != null) ctx.Attribute(meta, ErrorContext.WeightMessagePath, "MDT_ReasonInPath".Translate());
        }
    }
}
