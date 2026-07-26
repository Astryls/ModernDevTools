using System;
using System.Collections.Generic;
using System.Reflection;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// Built-in: attributes stack-trace frames to the mod whose assembly declares them. Recognizes
    /// vanilla / Harmony / Unity frames too, and records the namespaces it sees for the library module.
    /// </summary>
    public class Module_StackTrace : ErrorModule
    {
        public override void ContributeAttribution(ErrorContext ctx)
        {
            if (ctx.Frames == null) return;

            int index = 0;
            foreach (string line in ctx.Frames)
            {
                string qualified = FrameParser.QualifiedTypeOf(line);
                if (qualified == null) continue;

                Type t = FrameParser.ResolveType(qualified);
                string ns = FrameParser.NamespaceOf(t != null ? t.FullName : qualified);
                if (!ns.NullOrEmpty()) ctx.Namespaces.Add(ns);

                ModContentPack mod = t != null ? ctx.Mods.Assembly(t.Assembly) : null;
                if (mod != null)
                {
                    ctx.Attribute(mod, ErrorContext.WeightStackBase, "MDT_ReasonInStack".Translate(), index);
                }
                else if (ctx.Mods.NamespaceRoot(FrameParser.RootNamespaceOf(qualified)) is ModContentPack nsMod)
                {
                    // The type didn't resolve to a loaded assembly, but its root namespace is owned by
                    // exactly one loaded mod (typical of Harmony patch classes: MODNAME.SomePatch.Prefix).
                    ctx.Attribute(nsMod, ErrorContext.WeightStackBase, "MDT_ReasonNamespaceOwned".Translate(), index);
                }
                else if (FrameParser.LooksLikeHarmony(qualified))
                {
                    ctx.AttributeSource(SourceKind.Harmony, "Harmony", 1f, "MDT_ReasonHarmonyFrame".Translate(), index);
                }
                else if (FrameParser.LooksLikeVanilla(qualified))
                {
                    ctx.AttributeSource(SourceKind.Vanilla, "MDT_VanillaName".Translate(), 1f, "MDT_ReasonVanillaFrame".Translate(), index);
                }
                else if (FrameParser.LooksLikeUnity(qualified))
                {
                    ctx.AttributeSource(SourceKind.Unity, "MDT_UnityName".Translate(), 0.5f, "MDT_ReasonUnityFrame".Translate(), index);
                }

                index++;
            }
        }
    }
}
