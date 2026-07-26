using System.Linq;
using RimWorld;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// Built-in, LOCAL (no network): flags a culprit mod whose About.xml does not list support for the
    /// running RimWorld version. A version mismatch is one of the most common real causes of errors.
    /// </summary>
    public class Module_VersionMismatch : ErrorModule
    {
        public override void Diagnose(ErrorContext ctx)
        {
            foreach (AttributedMod m in ctx.Culprits)
            {
                if (m.PackageId.NullOrEmpty()) continue;
                ModMetaData meta = ctx.Mods.PackageId(m.PackageId);
                if (meta == null) continue;

                bool compat;
                try { compat = meta.VersionCompatible; } catch { continue; }
                if (compat) continue;

                string supported = "";
                try
                {
                    var vers = meta.SupportedVersionsReadOnly;
                    if (vers != null) supported = string.Join(", ", vers.Select(v => v.Major + "." + v.Minor));
                }
                catch { }
                if (supported.NullOrEmpty()) supported = "MDT_VerUnspecified".Translate();

                ctx.AddDiagnosis(new ErrorDiagnosis
                {
                    Title = "MDT_VerTitle".Translate(),
                    Explanation = "MDT_VerExplain".Translate(m.Name, supported, VersionControl.CurrentVersionStringWithoutBuild),
                    Fix = "MDT_VerFix".Translate(),
                    Source = "MDT_Module_VersionMismatch",
                    Score = 5
                });
                return; // one is enough
            }
        }
    }
}
