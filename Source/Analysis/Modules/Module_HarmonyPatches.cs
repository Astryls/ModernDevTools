using System;
using System.Collections.Generic;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// Built-in, always-on: reads the live Harmony patch registry and names the mods that PATCH a method
    /// appearing in the error, even when the trace itself is pure vanilla. This is the single biggest
    /// attribution gap the stack trace alone can't fill - a mod's Prefix/Postfix/Transpiler on a vanilla
    /// method runs "inside" that method, so the frame reads as RimWorld while the real cause is the
    /// patcher. When two or more mods patch the same method, it flags a likely conflict by name.
    /// </summary>
    public class Module_HarmonyPatches : ErrorModule
    {
        private struct PatchedFrame
        {
            public string Type;
            public string Method;
            public string[] Owners;
            public int Index;
        }

        private static List<PatchedFrame> Collect(ErrorContext ctx)
        {
            var found = new List<PatchedFrame>();
            if (ctx?.Frames == null) return found;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < ctx.Frames.Length; i++)
            {
                if (!FrameParser.TypeMethodOf(ctx.Frames[i], out string type, out string method)) continue;
                string[] owners = HarmonyIndex.OwnersFor(type, method);
                if (owners == null || owners.Length == 0) continue;
                if (!seen.Add(type + ":" + method)) continue;
                found.Add(new PatchedFrame { Type = type, Method = method, Owners = owners, Index = i });
            }
            return found;
        }

        public override void ContributeAttribution(ErrorContext ctx)
        {
            foreach (PatchedFrame pf in Collect(ctx))
            {
                foreach (string owner in pf.Owners)
                {
                    if (owner.Equals(HarmonyIndex.SelfId, StringComparison.OrdinalIgnoreCase)) continue;
                    ModMetaData meta = ctx.Mods?.MatchOwnerId(owner);
                    if (meta != null)
                        ctx.Attribute(meta, ErrorContext.WeightHarmonyPatch, "MDT_ReasonHarmonyPatch".Translate(), pf.Index);
                }
            }
        }

        public override void Diagnose(ErrorContext ctx)
        {
            List<PatchedFrame> patched = Collect(ctx);
            if (patched.Count == 0) return;

            // Pick the patched frame with the most foreign patchers (highest conflict potential),
            // preferring the one closest to the throw when tied.
            PatchedFrame chosen = default;
            int bestForeign = 0;
            bool any = false;
            foreach (PatchedFrame pf in patched)
            {
                int foreign = CountForeign(pf.Owners);
                if (foreign > bestForeign || (foreign == bestForeign && any && pf.Index < chosen.Index))
                {
                    bestForeign = foreign;
                    chosen = pf;
                    any = true;
                }
            }
            if (!any || bestForeign == 0) return;

            // Only surface the diagnosis when it is high-signal: either two+ mods patch the same method
            // (a genuine conflict candidate), or the error itself is clearly Harmony-routed.
            bool harmonyRouted = HarmonyRouted(ctx);
            if (bestForeign < 2 && !harmonyRouted) return;

            var names = new List<string>();
            foreach (string owner in chosen.Owners)
            {
                if (owner.Equals(HarmonyIndex.SelfId, StringComparison.OrdinalIgnoreCase)) continue;
                ModMetaData meta = ctx.Mods?.MatchOwnerId(owner);
                string label = meta != null ? meta.Name : owner;
                if (!names.Contains(label)) names.Add(label);
            }
            if (names.Count == 0) return;

            bool multi = names.Count > 1;
            string methodLabel = ShortType(chosen.Type) + "." + chosen.Method;
            string list = names.ToCommaList(true);

            ctx.AddDiagnosis(new ErrorDiagnosis
            {
                Title = (multi ? "MDT_HarmonyConflictTitle" : "MDT_HarmonyPatchedTitle").Translate(),
                Explanation = multi
                    ? "MDT_HarmonyConflictExplain".Translate(methodLabel, list, names.Count)
                    : "MDT_HarmonyPatchedExplain".Translate(methodLabel, list),
                Fix = (multi ? "MDT_HarmonyConflictFix" : "MDT_HarmonyPatchedFix").Translate(),
                Source = "harmonypatch:" + chosen.Type + ":" + chosen.Method,
                Score = multi ? 6.5f : 4.5f
            });
        }

        private static bool HarmonyRouted(ErrorContext ctx)
        {
            string tr = ctx.StackTrace;
            if (!tr.NullOrEmpty()
                && (tr.IndexOf("HarmonyLib", StringComparison.Ordinal) >= 0
                    || tr.IndexOf("wrapper dynamic-method", StringComparison.Ordinal) >= 0)) return true;
            string t = ctx.Text;
            return !t.NullOrEmpty() && t.IndexOf("wrapper dynamic-method", StringComparison.Ordinal) >= 0;
        }

        private static int CountForeign(string[] owners)
        {
            int n = 0;
            foreach (string o in owners)
                if (!o.Equals(HarmonyIndex.SelfId, StringComparison.OrdinalIgnoreCase)) n++;
            return n;
        }

        private static string ShortType(string full)
        {
            if (full.NullOrEmpty()) return full;
            int dot = full.LastIndexOf('.');
            return dot >= 0 && dot < full.Length - 1 ? full.Substring(dot + 1) : full;
        }
    }
}
