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
        /// <summary>
        /// Every message this mod logs itself is prefixed with its own name. That matters here because
        /// LogAnalysis fills Frames from LogMessage.StackTrace, which for a Log.Error(text) call is the
        /// stack of the CALLER - so when our hardening finalizer catches another mod's UI exception and
        /// reports it, the only frames present are OURS. The real thrower appears in the message TEXT,
        /// not the trace.
        ///
        /// Observed live (test run #232): Vehicle Framework's SmashTools.DetachedMapComponentCache threw
        /// a KeyNotFoundException inside the colonist bar; HardeningPatches.MapBefore_Finalizer caught
        /// and reported it. Stack attribution then named Modern Dev Tools at WeightStackBase (5), and
        /// because Vehicle Framework was only found via a file path (frameIndex -1, FirstIndex
        /// int.MaxValue) the weight tie broke on FirstIndex and WE RANKED FIRST - a diagnostic tool
        /// blaming itself for the bug it just caught for you.
        ///
        /// So: when the message is one of ours, skip self-attribution from the stack entirely. A genuine
        /// unhandled exception inside our code is logged by the ENGINE, without our prefix, and still
        /// attributes to us normally.
        /// </summary>
        private static bool IsSelfReport(ErrorContext ctx) =>
            ctx.Text != null && ctx.Text.StartsWith(ModernDevToolsMod.LogPrefix, StringComparison.Ordinal);

        private static bool IsSelf(ModContentPack mcp) =>
            mcp != null && !mcp.PackageId.NullOrEmpty()
            && mcp.PackageId.Equals(HarmonyIndex.SelfId, StringComparison.OrdinalIgnoreCase);

        public override void ContributeAttribution(ErrorContext ctx)
        {
            if (ctx.Frames == null) return;
            bool selfReport = IsSelfReport(ctx);

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
                    if (selfReport && IsSelf(mod)) { index++; continue; }
                    ctx.Attribute(mod, ErrorContext.WeightStackBase, "MDT_ReasonInStack".Translate(), index);
                }
                else if (ctx.Mods.NamespaceRoot(FrameParser.RootNamespaceOf(qualified)) is ModContentPack nsMod)
                {
                    if (selfReport && IsSelf(nsMod)) { index++; continue; }
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
