using RimWorld;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// Built-in: consults the opt-in community databases for a culprit mod - a recommended replacement
    /// (Use This Instead) and known incompatibilities (RimSort Community Rules). Silently does nothing
    /// when community data is disabled or not downloaded.
    /// </summary>
    public class Module_Community : ErrorModule
    {
        public override void Diagnose(ErrorContext ctx)
        {
            if (!CommunityData.Enabled || !CommunityData.HasData) return;
            string cur = VersionControl.CurrentVersionStringWithoutBuild;

            foreach (AttributedMod m in ctx.Culprits)
            {
                if (m.PackageId.NullOrEmpty()) continue;
                ModMetaData meta = ctx.Mods.PackageId(m.PackageId);

                // Recommended replacement (only when the newer mod supports the running version).
                Replacement rep = meta != null ? CommunityData.ReplacementFor(meta) : null;
                if (rep != null && !rep.NewName.NullOrEmpty() && rep.NewVersions.Contains(cur))
                {
                    ctx.AddDiagnosis(new ErrorDiagnosis
                    {
                        Title = "MDT_ReplaceTitle".Translate(),
                        Explanation = "MDT_ReplaceExplain".Translate(m.Name, rep.NewName),
                        Fix = "MDT_ReplaceFix".Translate(rep.NewName),
                        Url = rep.NewWorkshopId.NullOrEmpty() ? null : "https://steamcommunity.com/sharedfiles/filedetails/?id=" + rep.NewWorkshopId,
                        Source = "MDT_Module_Community",
                        Score = 6
                    });
                }

                // Known incompatibility with another ACTIVE mod.
                CommRule rule = CommunityData.RuleFor(m.PackageId);
                if (rule != null)
                {
                    foreach (string other in rule.Incompat)
                    {
                        if (!ModsConfig.IsActive(other)) continue;
                        string otherName = ctx.Mods.PackageId(other)?.Name ?? other;
                        ctx.AddDiagnosis(new ErrorDiagnosis
                        {
                            Title = "MDT_IncompatTitle".Translate(),
                            Explanation = "MDT_IncompatExplain".Translate(m.Name, otherName),
                            Fix = "MDT_IncompatFix".Translate(),
                            Source = "MDT_Module_Community",
                            Score = 7
                        });
                        break;
                    }
                }
            }
        }
    }
}
